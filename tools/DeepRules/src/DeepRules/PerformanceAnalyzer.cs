using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DeepRules
{
    /// <summary>DR023-DR025 and DR036: per-tick work, log floods, collections that only grow, guarded or indirect periodic work.</summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class PerformanceAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Rules.DR023, Rules.DR024, Rules.DR025, Rules.DR036);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(OnType, SyntaxKind.ClassDeclaration, SyntaxKind.StructDeclaration,
                SyntaxKind.RecordDeclaration, SyntaxKind.RecordStructDeclaration);
            context.RegisterCompilationStartAction(start =>
            {
                // Per frame means an override of a member the game calls every frame, a Harmony patch on one, or a method
                // those reach through direct calls on their unconditional path. The roots need the game's (or Torch's)
                // types, so without them there is nothing per frame. The graph is complete only at compilation end.
                // DR036 also starts from timer callbacks, which need no game types, so the graph is always built; the
                // per-frame roots and DR024 still need them.
                var api = HostApi.Of(start.Compilation);
                var ticks = new TickGraph(api);
                start.RegisterSyntaxNodeAction(ticks.OnFunction, SyntaxKind.MethodDeclaration, SyntaxKind.ConstructorDeclaration);
                start.RegisterCompilationEndAction(ticks.Report);
                start.RegisterSyntaxNodeAction(c => OnInvocation(c, api.Any ? ticks : null), SyntaxKind.InvocationExpression);
            });
        }

        // ---------------- DR023 ----------------

        /// <summary>Per-frame methods and the work on their unconditional path, collected per method and reported at compilation end.</summary>
        private sealed class TickGraph
        {
            private sealed class Node
            {
                public string? Root;
                public Location? Location;
                public readonly List<IMethodSymbol> Calls = new List<IMethodSymbol>();
                public readonly Dictionary<string, List<(Location location, int line, string detail)>> Work = new Dictionary<string, List<(Location, int, string)>>();

                // DR036: every call (any path, lambdas, method groups) and every work item of the DR036 kinds, wherever it is.
                public readonly List<IMethodSymbol> AllCalls = new List<IMethodSymbol>();
                public readonly Dictionary<string, List<(Location location, int line, string detail)>> AllWork = new Dictionary<string, List<(Location, int, string)>>();
            }

            /// <summary>A timer callback written as a lambda: it has no method of its own, so it is its own root.</summary>
            private sealed class LambdaRoot
            {
                public string Label = "";
                public Location? Location;
                public readonly Node Node = new Node();
            }

            private readonly HostApi _api;
            private readonly ConcurrentDictionary<IMethodSymbol, Node> _nodes = new ConcurrentDictionary<IMethodSymbol, Node>(SymbolEqualityComparer.Default);
            private readonly ConcurrentBag<(IMethodSymbol method, Location location, string text)> _logs = new ConcurrentBag<(IMethodSymbol, Location, string)>();
            private readonly ConcurrentDictionary<IMethodSymbol, string> _timerRoots = new ConcurrentDictionary<IMethodSymbol, string>(SymbolEqualityComparer.Default);
            private readonly ConcurrentBag<LambdaRoot> _lambdaRoots = new ConcurrentBag<LambdaRoot>();
            private readonly ConcurrentDictionary<IMethodSymbol, ConcurrentBag<IMethodSymbol>> _overrides =
                new ConcurrentDictionary<IMethodSymbol, ConcurrentBag<IMethodSymbol>>(SymbolEqualityComparer.Default);

            public TickGraph(HostApi api) => _api = api;

            public void OnFunction(SyntaxNodeAnalysisContext ctx)
            {
                var decl = (BaseMethodDeclarationSyntax)ctx.Node;
                if (decl.Body == null && decl.ExpressionBody == null) return;
                var model = ctx.SemanticModel;
                var ct = ctx.CancellationToken;
                if (model.GetDeclaredSymbol(decl, ct) is not IMethodSymbol m) return;
                var node = new Node { Root = Util.IsPerTick(m, _api, out var why) ? why : null, Location = decl.GetLocation() };
                foreach (var n in Util.FunctionNodes(decl))
                {
                    if (n is not (InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax) || !Unconditional(n, decl)) continue;
                    if (Util.MethodOf(n, model, ct) is IMethodSymbol target && target.Locations.Any(l => l.IsInSource))
                        node.Calls.Add(target.ReducedFrom?.OriginalDefinition ?? target.OriginalDefinition);
                }
                CollectWork(decl, model, ct, node.Work, node.AllWork);
                CollectAll(decl, model, ct, node);
                foreach (var af in decl.DescendantNodes().OfType<AnonymousFunctionExpressionSyntax>())
                    CollectWork(af, model, ct, null, node.AllWork);

                // A virtual or abstract call can land in any override in this compilation.
                for (var o = m.OverriddenMethod; o != null; o = o.OverriddenMethod)
                    _overrides.GetOrAdd(o.OriginalDefinition, _ => new ConcurrentBag<IMethodSymbol>()).Add(m.OriginalDefinition);

                FindTimerRoots(decl, model, ct, m);
                _nodes[m.OriginalDefinition] = node;
            }

            /// <summary>Every call to a method with source, wherever it is, and every method group passed as an argument.</summary>
            private static void CollectAll(SyntaxNode root, SemanticModel model, CancellationToken ct, Node node)
            {
                foreach (var n in root.DescendantNodes())
                {
                    IMethodSymbol? target = n switch
                    {
                        InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax => Util.MethodOf(n, model, ct),
                        ArgumentSyntax arg when arg.Expression is IdentifierNameSyntax or MemberAccessExpressionSyntax =>
                            model.GetSymbolInfo(arg.Expression, ct).Symbol as IMethodSymbol,
                        _ => null
                    };
                    if (target != null && target.Locations.Any(l => l.IsInSource))
                        node.AllCalls.Add(target.ReducedFrom?.OriginalDefinition ?? target.OriginalDefinition);
                }
            }

            /// <summary>
            /// Handlers added to System.Timers.Timer.Elapsed and callbacks passed to a System.Threading.Timer: they run again
            /// and again for as long as the timer runs. A method group becomes a root; a lambda is its own root.
            /// </summary>
            private void FindTimerRoots(SyntaxNode decl, SemanticModel model, CancellationToken ct, IMethodSymbol owner)
            {
                foreach (var n in decl.DescendantNodes())
                {
                    ExpressionSyntax? callback = null;
                    string? kind = null;
                    if (n is AssignmentExpressionSyntax asg && asg.IsKind(SyntaxKind.AddAssignmentExpression)
                        && model.GetSymbolInfo(asg.Left, ct).Symbol is IEventSymbol ev && ev.Name == "Elapsed"
                        && ev.ContainingType.FullName() == "System.Timers.Timer")
                    {
                        callback = asg.Right;
                        kind = "System.Timers.Timer.Elapsed handler";
                    }
                    else if (n is BaseObjectCreationExpressionSyntax oc && oc.ArgumentList?.Arguments.Count > 0
                             && model.GetTypeInfo(oc, ct).Type?.FullName() == "System.Threading.Timer")
                    {
                        callback = oc.ArgumentList.Arguments[0].Expression;
                        kind = "System.Threading.Timer callback";
                    }
                    if (callback == null || kind == null) continue;

                    if (callback is AnonymousFunctionExpressionSyntax af)
                    {
                        var root = new LambdaRoot { Label = "a lambda in " + Name(owner) + " (" + kind + ")", Location = af.GetLocation() };
                        CollectAll(af, model, ct, root.Node);
                        foreach (var inner in af.DescendantNodesAndSelf().OfType<AnonymousFunctionExpressionSyntax>())
                            CollectWork(inner, model, ct, null, root.Node.AllWork);
                        _lambdaRoots.Add(root);
                    }
                    else if (model.GetSymbolInfo(callback, ct).Symbol is IMethodSymbol handler && handler.Locations.Any(l => l.IsInSource))
                    {
                        var def = handler.ReducedFrom?.OriginalDefinition ?? handler.OriginalDefinition;
                        _timerRoots.TryAdd(def, Name(def) + " (" + kind + ")");
                    }
                }
            }

            public void AddLog(IMethodSymbol method, Location location, string text) => _logs.Add((method.OriginalDefinition, location, text));

            public void Report(CompilationAnalysisContext ctx)
            {
                // breadth first from the roots, in source order, so each method gets its shortest chain
                var chain = new Dictionary<IMethodSymbol, string>(SymbolEqualityComparer.Default);
                var queue = new Queue<IMethodSymbol>();
                foreach (var kv in _nodes.Where(x => x.Value.Root != null)
                             .OrderBy(x => x.Value.Location?.SourceTree?.FilePath, StringComparer.Ordinal).ThenBy(x => x.Value.Location?.SourceSpan.Start))
                {
                    chain[kv.Key] = kv.Value.Root!;
                    queue.Enqueue(kv.Key);
                }
                while (queue.Count > 0)
                {
                    var m = queue.Dequeue();
                    foreach (var callee in _nodes[m].Calls)
                    {
                        if (chain.ContainsKey(callee) || !_nodes.ContainsKey(callee)) continue;
                        chain[callee] = chain[m] + " \u2192 " + Name(callee);
                        queue.Enqueue(callee);
                    }
                }

                foreach (var kv in chain.OrderBy(x => _nodes[x.Key].Location?.SourceTree?.FilePath, StringComparer.Ordinal).ThenBy(x => _nodes[x.Key].Location?.SourceSpan.Start))
                {
                    var node = _nodes[kv.Key];
                    var why = node.Root ?? Name(kv.Key) + " (called every frame: " + kv.Value + ")";
                    foreach (var work in node.Work.OrderBy(w => w.Value[0].location.SourceSpan.Start))
                    {
                        var items = work.Value.OrderBy(x => x.location.SourceSpan.Start).ToList();
                        var lines = string.Join(", ", items.Select(x => x.line).Distinct());
                        var details = string.Join(", ", items.Select(x => x.detail).Distinct().Take(5));
                        ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR023, items[0].location, items.Skip(1).Select(x => x.location),
                            why + " runs every frame, and its unconditional path does " + work.Key + " (" + details + ") at line" + (items.Count > 1 ? "s " : " ") + lines +
                            ": that work is repeated 60 times a second per instance (DoomForSe ran File.Exists 7 times per frame; Atomic.FM parsed CustomData every frame). Cache it, move it out of the per-frame path, or throttle it"));
                    }
                }

                foreach (var (method, location, text) in _logs.OrderBy(x => x.location.SourceTree?.FilePath, StringComparer.Ordinal).ThenBy(x => x.location.SourceSpan.Start))
                {
                    if (!chain.TryGetValue(method, out var path)) continue;
                    ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR024, location,
                        "'" + text + "' logs in " + (_nodes[method].Root ?? Name(method) + " (reached through " + path + ")") +
                        ", which runs every frame, with no time, counter or last-value guard around it; if its condition stays true it writes this line on every pass (APBL logged an error and messaged the owner on every PB run, 60 a second). Check whether it can repeat; if so, log on change or rate-limit it"));
                }

                ReportIndirect(ctx, chain);
            }

            /// <summary>
            /// DR036: from every per-frame root and every timer callback, follow every call (any path, lambdas, method groups,
            /// overrides) and report the work DR023 has not already reported.
            /// </summary>
            private void ReportIndirect(CompilationAnalysisContext ctx, Dictionary<IMethodSymbol, string> counted)
            {
                var reached = new Dictionary<IMethodSymbol, (string cadence, string path)>(SymbolEqualityComparer.Default);
                var queue = new Queue<IMethodSymbol>();

                void Reach(IMethodSymbol callee, string cadence, string path)
                {
                    var targets = new List<IMethodSymbol> { callee };
                    if (_overrides.TryGetValue(callee, out var overrides))
                        targets.AddRange(overrides.OrderBy(o => o.ToDisplayString(), StringComparer.Ordinal));
                    foreach (var target in targets)
                    {
                        if (reached.ContainsKey(target) || !_nodes.ContainsKey(target)) continue;
                        reached[target] = (cadence, path + " \u2192 " + Name(target));
                        queue.Enqueue(target);
                    }
                }

                foreach (var kv in _nodes.Where(x => x.Value.Root != null)
                             .OrderBy(x => x.Value.Location?.SourceTree?.FilePath, StringComparer.Ordinal).ThenBy(x => x.Value.Location?.SourceSpan.Start))
                {
                    reached[kv.Key] = ("runs every frame", kv.Value.Root!);
                    queue.Enqueue(kv.Key);
                }
                foreach (var kv in _timerRoots.OrderBy(x => x.Value, StringComparer.Ordinal))
                {
                    if (reached.ContainsKey(kv.Key) || !_nodes.ContainsKey(kv.Key)) continue;
                    reached[kv.Key] = ("runs on every timer tick", kv.Value);
                    queue.Enqueue(kv.Key);
                }

                var lambdas = _lambdaRoots.OrderBy(r => r.Location?.SourceTree?.FilePath, StringComparer.Ordinal).ThenBy(r => r.Location?.SourceSpan.Start).ToList();
                foreach (var root in lambdas)
                {
                    foreach (var callee in root.Node.AllCalls)
                        Reach(callee, "runs on every timer tick", root.Label);
                }

                while (queue.Count > 0)
                {
                    var m = queue.Dequeue();
                    var (cadence, path) = reached[m];
                    foreach (var callee in _nodes[m].AllCalls)
                        Reach(callee, cadence, path);
                }

                foreach (var root in lambdas)
                    ReportWork(ctx, root.Node, null, root.Label, "runs on every timer tick");

                foreach (var kv in reached.OrderBy(x => _nodes[x.Key].Location?.SourceTree?.FilePath, StringComparer.Ordinal).ThenBy(x => _nodes[x.Key].Location?.SourceSpan.Start))
                {
                    var node = _nodes[kv.Key];
                    var reportedByDr023 = counted.ContainsKey(kv.Key) ? node.Work : null;
                    var isRoot = node.Root != null || _timerRoots.TryGetValue(kv.Key, out var label) && kv.Value.path == label;
                    var who = isRoot ? kv.Value.path : Name(kv.Key) + " (reached through " + kv.Value.path + ")";
                    ReportWork(ctx, node, reportedByDr023, who, kv.Value.cadence);
                }
            }

            private static void ReportWork(CompilationAnalysisContext ctx, Node node,
                Dictionary<string, List<(Location location, int line, string detail)>>? already, string who, string cadence)
            {
                foreach (var work in node.AllWork.OrderBy(w => w.Value[0].location.SourceSpan.Start))
                {
                    var done = already != null && already.TryGetValue(work.Key, out var counted)
                        ? new HashSet<int>(counted.Select(c => c.location.SourceSpan.Start))
                        : new HashSet<int>();
                    var items = work.Value.Where(x => !done.Contains(x.location.SourceSpan.Start))
                        .GroupBy(x => x.location.SourceSpan.Start).Select(g => g.First())
                        .OrderBy(x => x.location.SourceSpan.Start).ToList();
                    if (items.Count == 0) continue;
                    var lines = string.Join(", ", items.Select(x => x.line).Distinct());
                    var details = string.Join(", ", items.Select(x => x.detail).Distinct().Take(5));
                    ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR036, items[0].location, items.Skip(1).Select(x => x.location),
                        who + " " + cadence + ", and does " + work.Key + " (" + details + ") at line" + (items.Count > 1 ? "s " : " ") + lines +
                        " on a path DR023 cannot prove runs every time: behind a guard, branch or loop, in a lambda, or through a method group or override. Check how often that path really runs; if it runs in steady state, reuse a buffer, cache the result or move the work out of the periodic path"));
                }
            }

            private static string Name(IMethodSymbol m) => m.ContainingType.Name + "." + (m.MethodKind == MethodKind.Constructor ? m.ContainingType.Name + " constructor" : m.Name);
        }

        /// <summary>The kinds DR036 reports. String building and logging are left to DR023 and DR024.</summary>
        private static readonly HashSet<string> IndirectKinds = new HashSet<string>(StringComparer.Ordinal)
            { "LINQ", "allocations", "closures capturing locals", "reflection", "file system calls" };

        /// <summary>
        /// Work in one function: on its unconditional path into <paramref name="found"/> (DR023, when given), and of the
        /// DR036 kinds anywhere in it into <paramref name="all"/>.
        /// </summary>
        private static void CollectWork(SyntaxNode md, SemanticModel model, CancellationToken ct,
            Dictionary<string, List<(Location location, int line, string detail)>>? found,
            Dictionary<string, List<(Location location, int line, string detail)>> all)
        {
            void Into(Dictionary<string, List<(Location location, int line, string detail)>> target, string kind, SyntaxNode n, string detail)
            {
                if (!target.TryGetValue(kind, out var list)) target[kind] = list = new List<(Location, int, string)>();
                list.Add((n.GetLocation(), Util.Line(n), detail));
            }

            void Add(string kind, SyntaxNode n, string detail)
            {
                if (found != null && Unconditional(n, md)) Into(found, kind, n, detail);
                if (IndirectKinds.Contains(kind)) Into(all, kind, n, detail);
            }

            foreach (var n in Util.FunctionNodes(md))
            {
                switch (n)
                {
                    case InvocationExpressionSyntax inv:
                        {
                            var cm = Util.MethodOf(inv, model, ct);
                            if (cm?.ContainingType == null) break;
                            var tn = cm.ContainingType.FullName();
                            if (cm.ContainingType.NsStarts("System.Linq")) Add("LINQ", inv, cm.Name);
                            else if (IsReflection(cm, tn)) Add("reflection", inv, cm.ContainingType.Name + "." + cm.Name);
                            else if (tn == "System.IO.File" || tn == "System.IO.Directory" || (tn == "System.IO.Path" && cm.Name == "GetFullPath")) Add("file system calls", inv, cm.ContainingType.Name + "." + cm.Name);
                            else if (cm.ContainingType.SpecialType == SpecialType.System_String && cm.Name is "Concat" or "Format" or "Join") Add("string building", inv, "string." + cm.Name);
                            else if (Util.IsKnownLogging(cm)) Add("logging", inv, cm.ContainingType.Name + "." + cm.Name);
                            break;
                        }
                    case BaseObjectCreationExpressionSyntax oc:
                        {
                            var t = model.GetTypeInfo(oc, ct).Type;
                            if (t == null) break;
                            var tn = t.FullName();
                            if (tn is "System.Collections.Generic.List`1" or "System.Collections.Generic.Dictionary`2" or "System.Collections.Generic.HashSet`1" or
                                "System.Text.StringBuilder" or "System.Collections.Generic.Queue`1" or "System.Collections.Generic.Stack`1" or "System.Collections.Generic.SortedDictionary`2")
                                Add("allocations", oc, "new " + t.Name);
                            else if (tn is "System.IO.FileInfo" or "System.IO.DirectoryInfo" or "System.IO.FileStream" or "System.IO.StreamReader" or "System.IO.StreamWriter")
                                Add("file system calls", oc, "new " + t.Name);
                            break;
                        }
                    case ArrayCreationExpressionSyntax:
                    case ImplicitArrayCreationExpressionSyntax:
                        Add("allocations", n, "new array");
                        break;
                    case CollectionExpressionSyntax ce when ce.Elements.Count > 0:
                        Add("allocations", n, "collection expression");
                        break;
                    case InterpolatedStringExpressionSyntax interp when !model.GetConstantValue(interp, ct).HasValue:
                        Add("string building", n, "interpolation");
                        break;
                    case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AddExpression) && !(b.Parent is BinaryExpressionSyntax pb && pb.IsKind(SyntaxKind.AddExpression)) &&
                                                       model.GetTypeInfo(b, ct).Type?.SpecialType == SpecialType.System_String && !model.GetConstantValue(b, ct).HasValue:
                        Add("string building", n, "concatenation");
                        break;
                    case AssignmentExpressionSyntax asg when asg.IsKind(SyntaxKind.AddAssignmentExpression) && model.GetTypeInfo(asg.Left, ct).Type?.SpecialType == SpecialType.System_String:
                        Add("string building", n, "+=");
                        break;
                    case AnonymousFunctionExpressionSyntax af when Captures(af, model, ct) is string captured:
                        Add("closures capturing locals", n, captured);
                        break;
                }
            }

        }

        /// <summary>
        /// The node runs on every call of the method: it is not inside a branch, a loop body, a catch, the right side of
        /// &amp;&amp;, ||, ?? or ?., or a nested function, and no earlier statement can leave the method (return, throw, goto).
        /// </summary>
        private static bool Unconditional(SyntaxNode node, SyntaxNode method)
        {
            SyntaxNode child = node;
            for (var p = node.Parent; p != null && p != method; child = p, p = p.Parent)
            {
                switch (p)
                {
                    case IfStatementSyntax ifs when child != ifs.Condition:
                    case SwitchSectionSyntax:
                    case SwitchExpressionArmSyntax:
                    case CatchClauseSyntax:
                    case ConditionalExpressionSyntax ce when child != ce.Condition:
                    case BinaryExpressionSyntax b when (b.IsKind(SyntaxKind.LogicalAndExpression) || b.IsKind(SyntaxKind.LogicalOrExpression) || b.IsKind(SyntaxKind.CoalesceExpression)) && child == b.Right:
                    case AssignmentExpressionSyntax ca when ca.IsKind(SyntaxKind.CoalesceAssignmentExpression) && child == ca.Right:
                    case ConditionalAccessExpressionSyntax cae when child == cae.WhenNotNull:
                    case CommonForEachStatementSyntax fe when child == fe.Statement:
                    case WhileStatementSyntax ws when child == ws.Statement:
                    case ForStatementSyntax fs when child == fs.Statement || fs.Incrementors.Contains(child):
                        return false;
                    case BlockSyntax block:
                        foreach (var st in block.Statements)
                        {
                            if (st == child) break;
                            if (st is LocalFunctionStatementSyntax) continue;
                            if (Util.FunctionNodes(st).Any(x => x is ReturnStatementSyntax or ThrowStatementSyntax or ThrowExpressionSyntax or GotoStatementSyntax ||
                                                               x.IsKind(SyntaxKind.YieldBreakStatement)))
                                return false;
                        }
                        break;
                }
                if (Util.IsNestedFunction(p)) return false;
            }
            return true;
        }

        private static bool IsReflection(IMethodSymbol m, string tn)
        {
            if (tn == "System.Activator" || tn.StartsWith("HarmonyLib.AccessTools", StringComparison.Ordinal) || tn.StartsWith("HarmonyLib.Traverse", StringComparison.Ordinal)) return true;
            if (tn == "System.Type" && (m.Name.StartsWith("Get", StringComparison.Ordinal) && m.Name != "GetType" && m.Name != "GetHashCode" || m.Name == "InvokeMember")) return true;
            if (tn == "System.Type" && m.Name == "GetType" && m.IsStatic) return true;
            return m.ContainingType.NsStarts("System.Reflection") && m.Name is "Invoke" or "GetValue" or "SetValue" or "GetCustomAttributes" or "GetCustomAttribute" or "CreateDelegate";
        }

        private static string? Captures(AnonymousFunctionExpressionSyntax af, SemanticModel model, CancellationToken ct)
        {
            if (af.Modifiers.Any(SyntaxKind.StaticKeyword)) return null;
            foreach (var id in af.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                var s = model.GetSymbolInfo(id, ct).Symbol;
                if (s is ILocalSymbol || (s is IParameterSymbol p && !p.IsThis))
                {
                    var decl = s.DeclaringSyntaxReferences.FirstOrDefault();
                    if (decl != null && decl.SyntaxTree == af.SyntaxTree && !af.Span.Contains(decl.Span)) return s.Name;
                }
            }
            return null;
        }

        // ---------------- DR024 ----------------

        private static void OnInvocation(SyntaxNodeAnalysisContext ctx, TickGraph? ticks)
        {
            var inv = (InvocationExpressionSyntax)ctx.Node;
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            var m = Util.MethodOf(inv, model, ct);
            if (m == null || !Util.IsLogging(m)) return;

            // Only loops with no fixed bound (while, do, for with no condition) and every-frame game overrides: a foreach
            // or a counted for over a finite collection logs a bounded number of lines.
            var function = Util.EnclosingFunction(inv);
            string? where = null;
            for (var p = inv.Parent; p != null && p != function; p = p.Parent)
                if (p is WhileStatementSyntax || p is DoStatementSyntax || p is ForStatementSyntax { Condition: null })
                {
                    where = "inside a loop with no fixed bound";
                    break;
                }
            if (Guarded(inv, model, ct)) return;
            if (where == null)
            {
                // whether this method runs every frame is known at compilation end
                if (ticks != null && Util.EnclosingMember(inv) is BaseMethodDeclarationSyntax md && model.GetDeclaredSymbol(md, ct) is IMethodSymbol ms)
                    ticks.AddLog(ms, inv.GetLocation(), Util.Short(inv, 50));
                return;
            }

            ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR024, inv.GetLocation(),
                "'" + Util.Short(inv, 50) + "' logs " + where + " with no time, counter or last-value guard around it; if its condition stays true it writes this line on every pass (APBL logged an error and messaged the owner on every PB run, 60 a second). Check whether it can repeat; if so, log on change or rate-limit it"));
        }

        private static readonly Regex GuardMethod = new Regex("Throttle|RateLimit|ShouldLog|CanLog|Allow|Once|Every|Due|Elapsed", RegexOptions.CultureInvariant);
        private static readonly Regex GuardFlag = new Regex("logged|warned|reported|once|shown|notified", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static bool Guarded(SyntaxNode node, SemanticModel model, CancellationToken ct)
        {
            var member = Util.EnclosingMember(node);
            SyntaxNode child = node;
            for (var p = node.Parent; p != null && p != member; child = p, p = p.Parent)
            {
                switch (p)
                {
                    case IfStatementSyntax ifs when ifs.Condition != child && IsRateGuard(ifs.Condition, model, ct):
                        return true;
                    case ConditionalExpressionSyntax ce when ce.Condition != child && IsRateGuard(ce.Condition, model, ct):
                        return true;
                    case BlockSyntax block:
                        foreach (var st in block.Statements)
                        {
                            if (st.SpanStart >= child.SpanStart) break;
                            if (st is IfStatementSyntax early && IsExit(early.Statement) && IsRateGuard(early.Condition, model, ct)) return true;
                        }
                        break;
                }
            }
            return false;
        }

        private static bool IsExit(StatementSyntax s) =>
            s is ReturnStatementSyntax || s is ContinueStatementSyntax || s is BreakStatementSyntax || s is ThrowStatementSyntax ||
            (s is BlockSyntax b && b.Statements.Count == 1 && IsExit(b.Statements[0]));

        private static bool IsStateMember(ExpressionSyntax e, SemanticModel model, CancellationToken ct)
        {
            var s = Util.SymbolOf(e, model, ct);
            if (s is not (IFieldSymbol or IPropertySymbol) || (s is IFieldSymbol f && f.IsConst)) return false;
            if (e is IdentifierNameSyntax) return true;
            return e is MemberAccessExpressionSyntax ma && (ma.Expression is ThisExpressionSyntax || Util.SymbolOf(ma.Expression, model, ct) is INamedTypeSymbol);
        }

        private static bool IsRateGuard(ExpressionSyntax cond, SemanticModel model, CancellationToken ct)
        {
            foreach (var n in cond.DescendantNodesAndSelf())
            {
                switch (n)
                {
                    case BinaryExpressionSyntax b when b.Kind() is SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression or SyntaxKind.GreaterThanExpression or
                        SyntaxKind.GreaterThanOrEqualExpression or SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression:
                        if (b.Left.DescendantNodesAndSelf().Concat(b.Right.DescendantNodesAndSelf()).OfType<ExpressionSyntax>().Any(x => (x is IdentifierNameSyntax || x is MemberAccessExpressionSyntax) && IsStateMember(x, model, ct)))
                            return true;
                        break;
                    case InvocationExpressionSyntax inv:
                        if (GuardMethod.IsMatch(Util.InvokedName(inv))) return true;
                        if (Util.Receiver(inv) is ExpressionSyntax r && IsStateMember(r, model, ct)) return true;
                        break;
                    case IdentifierNameSyntax id when GuardFlag.IsMatch(id.Identifier.Text) && model.GetSymbolInfo(id, ct).Symbol is IFieldSymbol or IPropertySymbol:
                        return true;
                }
            }
            return false;
        }

        // ---------------- DR025 ----------------

        private static readonly HashSet<string> AddNames = new HashSet<string>(StringComparer.Ordinal)
            { "Add", "AddRange", "Enqueue", "Push", "TryAdd", "AddOrUpdate", "GetOrAdd", "Insert", "InsertRange", "AddFirst", "AddLast", "UnionWith" };

        private static readonly HashSet<string> RemoveNames = new HashSet<string>(StringComparer.Ordinal)
            { "Clear", "Dequeue", "TryDequeue", "Pop", "TryPop", "TryRemove", "TryTake", "Take", "TrimExcess", "ExceptWith", "IntersectWith", "SymmetricExceptWith", "Dispose" };

        private static void OnType(SyntaxNodeAnalysisContext ctx)
        {
            var typeDecl = (TypeDeclarationSyntax)ctx.Node;
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            var candidates = new List<(IFieldSymbol field, SyntaxToken id)>();
            foreach (var fd in typeDecl.Members.OfType<FieldDeclarationSyntax>())
                foreach (var v in fd.Declaration.Variables)
                    if (model.GetDeclaredSymbol(v, ct) is IFieldSymbol f && f.DeclaredAccessibility == Accessibility.Private && Util.IsGrowableCollection(f.Type))
                        candidates.Add((f, v.Identifier));
            if (candidates.Count == 0) return;

            var nodes = Util.TypeNodes(typeDecl).ToList();
            foreach (var (field, id) in candidates)
            {
                var adds = new List<string>();
                var bounded = false;
                foreach (var n in nodes)
                {
                    if (n is not ExpressionSyntax e || (e is not IdentifierNameSyntax && e is not MemberAccessExpressionSyntax)) continue;
                    if (Util.LastName(e) != field.Name) continue;
                    if (e is IdentifierNameSyntax && e.Parent is MemberAccessExpressionSyntax pma && pma.Name == e) continue; // handled at the member access
                    if (!Util.Same(Util.SymbolOf(e, model, ct), field)) continue;
                    var use = Classify(e, model, ct);
                    if (use == null) continue;
                    if (use == "bounded") { bounded = true; break; }
                    adds.Add(use);
                }
                if (bounded || adds.Count == 0) continue;
                ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR025, id.GetLocation(),
                    "'" + field.Name + "' only grows: this type adds to it (" + string.Join(", ", adds.Distinct().Take(4)) +
                    ") but never removes from, clears, replaces or caps it (no Count check); on a server that runs for weeks it holds every entry, and its entities, until the process exits. Remove entries when they end, or cap it"));
            }
        }

        /// <summary>"bounded" for removal, replacement, Count checks or escapes; an add description; or null when neutral.</summary>
        private static string? Classify(ExpressionSyntax e, SemanticModel model, CancellationToken ct)
        {
            SyntaxNode cur = e;
            while (cur.Parent is ParenthesizedExpressionSyntax || (cur.Parent is PostfixUnaryExpressionSyntax pu && pu.IsKind(SyntaxKind.SuppressNullableWarningExpression)))
                cur = cur.Parent;
            switch (cur.Parent)
            {
                case MemberAccessExpressionSyntax ma when ma.Expression == cur:
                    {
                        var name = ma.Name.Identifier.Text;
                        if (ma.Parent is InvocationExpressionSyntax)
                        {
                            if (AddNames.Contains(name)) return name + " at line " + Util.Line(ma);
                            if (name.StartsWith("Remove", StringComparison.Ordinal) || RemoveNames.Contains(name)) return "bounded";
                            return null;
                        }
                        if (name is "Count" or "Length" or "LongCount" && ma.Parent is BinaryExpressionSyntax) return "bounded";
                        return null;
                    }
                case ConditionalAccessExpressionSyntax ca when ca.Expression == cur:
                    {
                        if (ca.WhenNotNull is InvocationExpressionSyntax { Expression: MemberBindingExpressionSyntax mb })
                        {
                            var name = mb.Name.Identifier.Text;
                            if (AddNames.Contains(name)) return name + " at line " + Util.Line(ca);
                            if (name.StartsWith("Remove", StringComparison.Ordinal) || RemoveNames.Contains(name)) return "bounded";
                        }
                        return null;
                    }
                case ElementAccessExpressionSyntax ea when ea.Expression == cur:
                    return ea.Parent is AssignmentExpressionSyntax set && set.Left == ea ? "indexer set at line " + Util.Line(ea) : null;
                case AssignmentExpressionSyntax reassign when reassign.Left == cur:
                    return Util.EnclosingMember(reassign) is ConstructorDeclarationSyntax ? null : "bounded";
                case AssignmentExpressionSyntax copy when copy.Right == cur:
                case ArgumentSyntax:
                case ReturnStatementSyntax:
                case ArrowExpressionClauseSyntax:
                case EqualsValueClauseSyntax:
                    return "bounded"; // escapes: someone else may trim it
            }
            return null;
        }
    }
}
