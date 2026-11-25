using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DeepRules
{
    /// <summary>DR014, DR015, DR033: swallowed exceptions and exceptions escaping callbacks.</summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ErrorsAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Rules.DR014, Rules.DR015, Rules.DR033);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(OnCatch, SyntaxKind.CatchClause);
            context.RegisterSyntaxNodeAction(OnFinalizer, SyntaxKind.DestructorDeclaration);
            context.RegisterSyntaxNodeAction(OnLambda, SyntaxKind.SimpleLambdaExpression, SyntaxKind.ParenthesizedLambdaExpression, SyntaxKind.AnonymousMethodExpression);
            context.RegisterSyntaxNodeAction(OnCallbackArgument, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression,
                SyntaxKind.InvocationExpression);
            context.RegisterCompilationStartAction(start =>
            {
                var api = HostApi.Of(start.Compilation);
                var harmony = HostContext.Of(start.Compilation).Harmony;
                var disposable = start.Compilation.GetTypeByMetadataName("System.IDisposable");
                var asyncDisposable = start.Compilation.GetTypeByMetadataName("System.IAsyncDisposable");
                start.RegisterSyntaxNodeAction(c => OnMethod(c, api, harmony, disposable, asyncDisposable), SyntaxKind.MethodDeclaration);
            });
        }

        // ---------------- DR014 ----------------

        private static void OnCatch(SyntaxNodeAnalysisContext ctx)
        {
            var c = (CatchClauseSyntax)ctx.Node;
            if (c.Filter != null) return; // a filtered catch handles a chosen case on purpose
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            if (c.Declaration != null && !model.GetTypeInfo(c.Declaration.Type, ct).Type.Is("System.Exception")) return;

            foreach (var n in Util.FunctionNodes(c.Block))
                if (n is ThrowStatementSyntax || n is ThrowExpressionSyntax) return;

            if (c.Declaration != null && !c.Declaration.Identifier.IsKind(SyntaxKind.None) &&
                model.GetDeclaredSymbol(c.Declaration, ct) is ILocalSymbol ex &&
                c.DescendantNodes().OfType<IdentifierNameSyntax>().Any(id => id.Identifier.Text == ex.Name && Util.Same(model.GetSymbolInfo(id, ct).Symbol, ex)))
                return;

            foreach (var inv in c.Block.DescendantNodes().OfType<InvocationExpressionSyntax>())
                if (Util.MethodOf(inv, model, ct) is IMethodSymbol m && Util.IsLogging(m)) return;

            var shape = c.Declaration == null ? "catch" : "catch (" + c.Declaration.Type + (c.Declaration.Identifier.IsKind(SyntaxKind.None) ? "" : " " + c.Declaration.Identifier.Text) + ")";
            var body = c.Block.Statements.Count == 0 ? " with an empty block" : "";
            ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR014, c.CatchKeyword.GetLocation(),
                shape + body + " swallows every exception: it does not log, rethrow or use the exception, so a failure here disappears and the code carries on with partial state. Log the exception, or catch only the exception types expected here"));
        }

        // ---------------- DR015, DR033 ----------------

        private const string WhereGame = "the game's or the host's loop that called it";
        private const string WherePatch = "the patched game method and everything that called it";
        private const string WhereDispose = "the code disposing this object, and the rest of its cleanup is skipped";
        private const string WhereFinalizer = "the finalizer thread, which ends the process";
        private const string WhereThread = "the thread or thread pool, which ends the process";
        private const string WhereEvent = "the code that raised the event, and the handlers after this one do not run";
        private const string WhereAsyncVoid = "the synchronization context or thread pool, which ends the process";

        private static void OnMethod(SyntaxNodeAnalysisContext ctx, HostApi api, bool harmony, INamedTypeSymbol? disposable, INamedTypeSymbol? asyncDisposable)
        {
            var md = (MethodDeclarationSyntax)ctx.Node;
            if (md.Body == null && md.ExpressionBody == null) return;
            if (ctx.SemanticModel.GetDeclaredSymbol(md, ctx.CancellationToken) is not IMethodSymbol m) return;

            string what, where;
            var guardMatters = false;
            if (api.CallbackOf(m) is string member)
            {
                what = "game callback " + m.ContainingType.Name + "." + m.Name + " (overrides " + member + ")";
                where = WhereGame;
                guardMatters = true;
            }
            else if (harmony && Util.IsHarmonyPatchMethod(m))
            {
                what = "Harmony patch " + m.ContainingType.Name + "." + m.Name;
                where = WherePatch;
                guardMatters = true;
            }
            else if (IsDispose(m, disposable, asyncDisposable))
            {
                what = m.ContainingType.Name + "." + m.Name;
                where = WhereDispose;
            }
            else if (Util.IsEventHandlerSignature(m))
            {
                what = "event handler " + m.ContainingType.Name + "." + m.Name;
                where = WhereEvent;
            }
            else if (m.IsAsync && m.ReturnsVoid)
            {
                what = "async void method " + m.ContainingType.Name + "." + m.Name;
                where = WhereAsyncVoid;
            }
            else return;

            if (ReportThrow(ctx, md, what, where)) return;
            if (guardMatters) ReportNoGuard(ctx, md, md.Identifier.GetLocation(), what);
        }

        private static bool IsDispose(IMethodSymbol m, INamedTypeSymbol? disposable, INamedTypeSymbol? asyncDisposable)
        {
            var type = m.ContainingType;
            if (type == null) return false;
            foreach (var iface in new[] { disposable, asyncDisposable })
            {
                if (iface == null || !type.AllInterfaces.Contains(iface, SymbolEqualityComparer.Default)) continue;
                foreach (var member in iface.GetMembers().OfType<IMethodSymbol>())
                    if (Util.Same(type.FindImplementationForInterfaceMember(member), m)) return true;
                // the Dispose(bool disposing) pattern of a disposable type
                if (Util.Same(iface, disposable) && m.Name == "Dispose" && m.Parameters.Length == 1 && m.Parameters[0].Type.SpecialType == SpecialType.System_Boolean)
                    return true;
            }
            return false;
        }

        private static void OnFinalizer(SyntaxNodeAnalysisContext ctx)
        {
            var d = (DestructorDeclarationSyntax)ctx.Node;
            if (d.Body == null && d.ExpressionBody == null) return;
            ReportThrow(ctx, d, "finalizer ~" + d.Identifier.Text, WhereFinalizer);
        }

        /// <summary>Lambdas that become thread, thread-pool or timer callbacks, event handlers or async void delegates.</summary>
        private static void OnLambda(SyntaxNodeAnalysisContext ctx)
        {
            var af = (AnonymousFunctionExpressionSyntax)ctx.Node;
            if (ctx.SemanticModel.GetTypeInfo(af, ctx.CancellationToken).ConvertedType is not INamedTypeSymbol { TypeKind: TypeKind.Delegate } d) return;
            var kind = CallbackKind(d, af.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword));
            if (kind == null) return;
            ReportThrow(ctx, af, kind.Value.what, kind.Value.where);
        }

        /// <summary>Method groups handed to Thread, ThreadPool, timers or Timer.Elapsed: the method's own throws escape there.</summary>
        private static void OnCallbackArgument(SyntaxNodeAnalysisContext ctx)
        {
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            ArgumentListSyntax? list;
            switch (ctx.Node)
            {
                case BaseObjectCreationExpressionSyntax oc when oc.ArgumentList != null &&
                                                                model.GetTypeInfo(oc, ct).Type.IsAny("System.Threading.Thread", "System.Threading.Timer"):
                    list = oc.ArgumentList;
                    break;
                case InvocationExpressionSyntax inv when Util.InvokedName(inv) is "QueueUserWorkItem" or "UnsafeQueueUserWorkItem" or "RegisterWaitForSingleObject" or "UnsafeRegisterWaitForSingleObject" &&
                                                        Util.MethodOf(inv, model, ct)?.ContainingType.Is("System.Threading.ThreadPool") == true:
                    list = inv.ArgumentList;
                    break;
                default:
                    return;
            }
            foreach (var arg in list.Arguments.Select(a => a.Expression))
            {
                var e = Util.Strip(arg);
                if (e is not (IdentifierNameSyntax or MemberAccessExpressionSyntax)) continue;
                if (model.GetTypeInfo(e, ct).ConvertedType is not INamedTypeSymbol { TypeKind: TypeKind.Delegate } d) continue;
                if (Util.SymbolOf(e, model, ct) is not IMethodSymbol target || Util.IsEventHandlerSignature(target)) continue; // event handlers are checked at their declaration
                var kind = CallbackKind(d, false);
                if (kind == null) continue;
                foreach (var r in target.DeclaringSyntaxReferences)
                {
                    var decl = r.GetSyntax(ct);
                    if (EscapingThrow(decl) is (SyntaxNode node, string verb))
                    {
                        ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR015, e.GetLocation(),
                            target.ContainingType.Name + "." + target.Name + ", used here as a " + kind.Value.what + ", " + verb + " at line " + Util.Line(node) +
                            " and nothing in it catches the exception, so it escapes into " + kind.Value.where + ". Catch and log it inside the callback"));
                        break;
                    }
                }
            }
        }

        private static (string what, string where)? CallbackKind(INamedTypeSymbol d, bool isAsync)
        {
            switch (d.FullName())
            {
                case "System.Threading.ThreadStart":
                case "System.Threading.ParameterizedThreadStart":
                    return ("thread callback", WhereThread);
                case "System.Threading.WaitCallback":
                case "System.Threading.WaitOrTimerCallback":
                case "System.Threading.TimerCallback":
                    return ("thread-pool or timer callback", WhereThread);
            }
            var invoke = d.DelegateInvokeMethod;
            if (invoke == null) return null;
            if (Util.IsEventHandlerSignature(invoke)) return ("event handler (" + d.Name + ")", WhereEvent);
            if (isAsync && invoke.ReturnsVoid) return ("async lambda converted to " + d.Name + " (async void)", WhereAsyncVoid);
            return null;
        }

        private static bool ReportThrow(SyntaxNodeAnalysisContext ctx, SyntaxNode function, string what, string where)
        {
            if (EscapingThrow(function) is not (SyntaxNode node, string verb)) return false;
            ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR015, node.GetLocation(),
                what + " " + verb + " at line " + Util.Line(node) + " and nothing in it catches the exception, so it escapes into " + where +
                " (Atomic.FM StartPlayback ended its catch with throw; and reached Pulsar's PluginInstance.Update uncaught). Catch and log it here"));
            return true;
        }

        /// <summary>The first throw or rethrow in the function (not in nested functions) that no enclosing try/catch inside it handles.</summary>
        private static (SyntaxNode node, string verb)? EscapingThrow(SyntaxNode function)
        {
            foreach (var n in Util.FunctionNodes(function))
            {
                if (n is not (ThrowStatementSyntax or ThrowExpressionSyntax)) continue;
                if (CaughtInside(n, function)) continue;
                var catchClause = n.Ancestors().TakeWhile(a => a != function).OfType<CatchClauseSyntax>().FirstOrDefault();
                var rethrow = catchClause != null && n is ThrowStatementSyntax ts &&
                              (ts.Expression == null || ts.Expression is IdentifierNameSyntax id && catchClause.Declaration?.Identifier.Text == id.Identifier.Text);
                return (n, rethrow ? "rethrows" : catchClause != null ? "throws from a catch" : "throws");
            }
            return null;
        }

        private static bool CaughtInside(SyntaxNode throwNode, SyntaxNode function)
        {
            for (var p = throwNode.Parent; p != null && p != function; p = p.Parent)
                if (p is TryStatementSyntax t && t.Catches.Count > 0 && t.Block.Span.Contains(throwNode.Span)) return true;
            return false;
        }

        private static void ReportNoGuard(SyntaxNodeAnalysisContext ctx, SyntaxNode function, Location location, string what)
        {
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            if (Util.FunctionNodes(function).OfType<TryStatementSyntax>().Any(t => t.Catches.Count > 0)) return;
            var calls = new List<string>();
            foreach (var inv in Util.FunctionNodes(function).OfType<InvocationExpressionSyntax>())
            {
                if (Util.MethodOf(inv, model, ct) is not IMethodSymbol cm || cm.ContainingType == null || cm.ContainingType.IsBcl() || Util.IsLogging(cm)) continue;
                var s = cm.ContainingType.Name + "." + cm.Name;
                if (!calls.Contains(s)) calls.Add(s);
                if (calls.Count == 3) break;
            }
            if (calls.Count == 0) return;
            ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR033, location,
                what + " has no try/catch and calls " + string.Join(", ", calls) +
                "; an exception from that code escapes into the host's update or event loop. Check whether those calls can throw and whether the host guards this callback (most do not log which plugin failed); if they can and it does not, catch and log here"));
        }
    }
}
