using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DeepRules
{
    /// <summary>
    /// DR001's proof, collected over one compilation: does the token of a CancellationTokenSource held in a field reach code
    /// that Cancel() runs synchronously on the cancelling thread? The sinks are contracts of the BCL, not guesses:
    /// CancellationToken.Register/UnsafeRegister (Cancel runs the callback inline), CreateLinkedTokenSource (Cancel cancels
    /// the linked source inline, so its token is followed), and Task.Delay(..., token) awaited with ConfigureAwait(false)
    /// (Cancel completes the delay inside its registration, and the await's continuation runs inline). The token is
    /// followed through locals, through the field's own .Token reads, through a local stored into the field, and into
    /// parameters of methods in this compilation (any depth, resolved at compilation end).
    /// </summary>
    internal sealed class TokenFlow
    {
        /// <summary>A method parameter of type CancellationToken, by method and position.</summary>
        private readonly struct Param
        {
            public Param(IMethodSymbol method, int index)
            {
                Method = method;
                Index = index;
            }

            public IMethodSymbol Method { get; }
            public int Index { get; }
        }

        private sealed class Flow
        {
            public string? Sink;
            public readonly List<(Param target, string call)> Calls = new List<(Param, string)>();
        }

        private sealed class ParamComparer : IEqualityComparer<Param>
        {
            public static readonly ParamComparer Instance = new ParamComparer();
            public bool Equals(Param a, Param b) => a.Index == b.Index && SymbolEqualityComparer.Default.Equals(a.Method, b.Method);
            public int GetHashCode(Param p) => SymbolEqualityComparer.Default.GetHashCode(p.Method) * 31 + p.Index;
        }

        private readonly ConcurrentDictionary<Param, Flow> _params = new ConcurrentDictionary<Param, Flow>(ParamComparer.Instance);
        private readonly ConcurrentBag<(IFieldSymbol field, string where, Flow flow)> _tokenReads = new ConcurrentBag<(IFieldSymbol, string, Flow)>();

        /// <summary>Records where each CancellationToken parameter of a method in this compilation goes.</summary>
        public void OnFunction(SyntaxNode decl, IMethodSymbol method, SemanticModel model, CancellationToken ct)
        {
            for (var i = 0; i < method.Parameters.Length; i++)
            {
                var p = method.Parameters[i];
                if (!IsToken(p.Type)) continue;
                var flow = new Flow();
                foreach (var id in decl.DescendantNodes().OfType<IdentifierNameSyntax>())
                    if (id.Identifier.Text == p.Name && Util.Same(model.GetSymbolInfo(id, ct).Symbol, p))
                        Follow(id, decl, model, ct, flow, 4);
                _params[new Param(method.OriginalDefinition, i)] = flow;
            }
        }

        /// <summary>Records where a .Token read of a CancellationTokenSource held in <paramref name="field"/> goes.</summary>
        public void OnTokenRead(IFieldSymbol field, MemberAccessExpressionSyntax tokenRead, SyntaxNode member, SemanticModel model, CancellationToken ct)
        {
            var flow = new Flow();
            Follow(tokenRead, member, model, ct, flow, 4);
            if (flow.Sink != null || flow.Calls.Count > 0)
                _tokenReads.Add((field, Util.MemberName(member) + " (line " + Util.Line(tokenRead) + ")", flow));
        }

        /// <summary>At compilation end: how a token of this field reaches inline code, or null when no path is proven.</summary>
        public string? ProofFor(IFieldSymbol field)
        {
            var reaching = new Dictionary<Param, string>(ParamComparer.Instance);
            foreach (var kv in _params)
                if (kv.Value.Sink != null) reaching[kv.Key] = kv.Value.Sink;
            for (var changed = true; changed;)
            {
                changed = false;
                foreach (var kv in _params)
                {
                    if (reaching.ContainsKey(kv.Key)) continue;
                    foreach (var (target, call) in kv.Value.Calls)
                        if (reaching.TryGetValue(target, out var rest))
                        {
                            reaching[kv.Key] = call + " -> " + rest;
                            changed = true;
                            break;
                        }
                }
            }
            foreach (var (f, where, flow) in _tokenReads.OrderBy(x => x.where, System.StringComparer.Ordinal))
            {
                if (!Util.Same(f, field)) continue;
                if (flow.Sink != null) return "its token, read in " + where + ", goes to " + flow.Sink;
                foreach (var (target, call) in flow.Calls)
                    if (reaching.TryGetValue(target, out var rest))
                        return "its token, read in " + where + ", goes to " + call + " -> " + rest;
            }
            return null;
        }

        public static bool IsToken(ITypeSymbol? t) => t.Is("System.Threading.CancellationToken");

        /// <summary>Follows one occurrence of a token value inside <paramref name="scope"/>.</summary>
        private static void Follow(ExpressionSyntax e, SyntaxNode scope, SemanticModel model, CancellationToken ct, Flow flow, int depth)
        {
            if (depth <= 0 || flow.Sink != null) return;
            SyntaxNode cur = e;
            while (cur.Parent is ParenthesizedExpressionSyntax) cur = cur.Parent;

            // token.Register(...)
            if (cur.Parent is MemberAccessExpressionSyntax ma && ma.Expression == cur && ma.Parent is InvocationExpressionSyntax reg &&
                ma.Name.Identifier.Text is "Register" or "UnsafeRegister" && Util.MethodOf(reg, model, ct)?.ContainingType.Is("System.Threading.CancellationToken") == true)
            {
                flow.Sink = "CancellationToken." + ma.Name.Identifier.Text + " at line " + Util.Line(reg) + ", whose callback Cancel() runs inline";
                return;
            }

            // an argument of a call
            if (cur.Parent is ArgumentSyntax arg && arg.Parent?.Parent is InvocationExpressionSyntax call && Util.MethodOf(call, model, ct) is IMethodSymbol target)
            {
                var tn = target.ContainingType?.FullName() ?? "";
                if (tn == "System.Threading.CancellationTokenSource" && target.Name == "CreateLinkedTokenSource")
                {
                    var linked = call.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax lv } ? model.GetDeclaredSymbol(lv, ct) as ILocalSymbol
                        : call.Parent is AssignmentExpressionSyntax la && la.Right == call ? Util.SymbolOf(la.Left, model, ct) as ILocalSymbol : null;
                    if (linked != null)
                        foreach (var read in scope.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
                            if (read.Name.Identifier.Text == "Token" && Util.Same(Util.SymbolOf(read.Expression, model, ct), linked))
                                Follow(read, scope, model, ct, flow, depth - 1);
                    return;
                }
                if (tn == "System.Threading.Tasks.Task" && target.Name == "Delay")
                {
                    if (AwaitedWithoutContext(call))
                        flow.Sink = "Task.Delay at line " + Util.Line(call) + ", awaited with ConfigureAwait(false), whose continuation Cancel() runs inline";
                    return;
                }
                if (target.Locations.Any(l => l.IsInSource) && ParameterIndex(arg, call, target) is int index && IsToken(target.Parameters[index].Type))
                {
                    // a reduced extension method call: the declaration has the receiver as parameter 0
                    var key = target.ReducedFrom != null ? new Param(target.ReducedFrom.OriginalDefinition, index + 1) : new Param(target.OriginalDefinition, index);
                    flow.Calls.Add((key, target.ContainingType?.Name + "." + target.Name + " (line " + Util.Line(call) + ")"));
                }
                return;
            }

            // copied into a local: follow the local
            ILocalSymbol? local = cur.Parent switch
            {
                EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax v } => model.GetDeclaredSymbol(v, ct) as ILocalSymbol,
                AssignmentExpressionSyntax a when a.Right == cur && a.IsKind(SyntaxKind.SimpleAssignmentExpression) => Util.SymbolOf(a.Left, model, ct) as ILocalSymbol,
                _ => null
            };
            if (local == null) return;
            foreach (var id in scope.DescendantNodes().OfType<IdentifierNameSyntax>())
                if (id.Identifier.Text == local.Name && id.SpanStart > cur.SpanStart && Util.Same(model.GetSymbolInfo(id, ct).Symbol, local))
                    Follow(id, scope, model, ct, flow, depth - 1);
        }

        /// <summary>await call.ConfigureAwait(false): the continuation runs on whichever thread completes the task.</summary>
        private static bool AwaitedWithoutContext(InvocationExpressionSyntax call) =>
            call.Parent is MemberAccessExpressionSyntax cfg && cfg.Name.Identifier.Text == "ConfigureAwait" &&
            cfg.Parent is InvocationExpressionSyntax cfgCall && cfgCall.ArgumentList.Arguments.Count == 1 &&
            cfgCall.ArgumentList.Arguments[0].Expression.IsKind(SyntaxKind.FalseLiteralExpression) && cfgCall.Parent is AwaitExpressionSyntax;

        private static int? ParameterIndex(ArgumentSyntax arg, InvocationExpressionSyntax call, IMethodSymbol target)
        {
            if (arg.NameColon != null)
            {
                var name = arg.NameColon.Name.Identifier.Text;
                for (var i = 0; i < target.Parameters.Length; i++)
                    if (target.Parameters[i].Name == name) return i;
                return null;
            }
            var pos = call.ArgumentList.Arguments.IndexOf(arg);
            return pos >= 0 && pos < target.Parameters.Length && !target.Parameters[pos].IsParams ? pos : null;
        }
    }
}
