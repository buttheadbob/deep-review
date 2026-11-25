using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DeepRules
{
    /// <summary>DR001-DR009: locks, re-entrancy, async, cancellation, timers.</summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ConcurrencyAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
            Rules.DR001, Rules.DR002, Rules.DR003, Rules.DR004, Rules.DR005, Rules.DR006, Rules.DR007, Rules.DR008, Rules.DR009, Rules.DR035);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(start =>
            {
                // DR001 needs the whole compilation: a CancellationTokenSource.Cancel() under a lock is reported at the end,
                // as DR001 when its token provably reaches code Cancel() runs inline, as DR035 (review-only) otherwise.
                var flow = new TokenFlow();
                var pending = new ConcurrentBag<(IFieldSymbol field, Location location, string trigger, string lockText)>();
                start.RegisterSyntaxNodeAction(c => OnInvocation(c, pending), SyntaxKind.InvocationExpression);
                start.RegisterSyntaxNodeAction(c => OnTokenRead(c, flow), SyntaxKind.SimpleMemberAccessExpression);
                start.RegisterSyntaxNodeAction(c => OnTokenParameters(c, flow), SyntaxKind.MethodDeclaration, SyntaxKind.ConstructorDeclaration, SyntaxKind.LocalFunctionStatement);
                start.RegisterCompilationEndAction(end =>
                {
                    foreach (var (field, location, trigger, lockText) in pending.OrderBy(p => p.location.SourceTree?.FilePath, StringComparer.Ordinal).ThenBy(p => p.location.SourceSpan.Start))
                    {
                        if (flow.ProofFor(field) is string proof)
                            end.ReportDiagnostic(Diagnostic.Create(Rules.DR001, location,
                                trigger + " is called inside " + lockText + ", and the CancellationTokenSource comes from '" + field.Name + "', where " + proof +
                                ": Cancel() runs that code synchronously on this thread while the lock is still held, so it can re-enter this lock and change the state it guards (SensEssentials' cancelled run removed itself from _runs inside Cancel's foreach: 'Collection was modified'). Take what to cancel under the lock and call Cancel() after releasing it"));
                        else
                            end.ReportDiagnostic(Diagnostic.Create(Rules.DR035, location, Unproven(trigger, lockText)));
                    }
                });
            });
            context.RegisterSyntaxNodeAction(OnForEach, SyntaxKind.ForEachStatement, SyntaxKind.ForEachVariableStatement);
            context.RegisterSyntaxNodeAction(OnAsyncVoid, SyntaxKind.MethodDeclaration, SyntaxKind.LocalFunctionStatement,
                SyntaxKind.SimpleLambdaExpression, SyntaxKind.ParenthesizedLambdaExpression, SyntaxKind.AnonymousMethodExpression);
            context.RegisterSyntaxNodeAction(OnMemberAccess, SyntaxKind.SimpleMemberAccessExpression);
            context.RegisterSyntaxNodeAction(OnLock, SyntaxKind.LockStatement);
            context.RegisterSyntaxNodeAction(OnType, SyntaxKind.ClassDeclaration, SyntaxKind.StructDeclaration,
                SyntaxKind.RecordDeclaration, SyntaxKind.RecordStructDeclaration);
            context.RegisterSyntaxNodeAction(OnLazyInit, SyntaxKind.IfStatement, SyntaxKind.CoalesceAssignmentExpression, SyntaxKind.CoalesceExpression);
            context.RegisterSyntaxNodeAction(OnTimerCreation, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
        }

        // ---------------- DR001, DR003, DR005 ----------------

        private static void OnInvocation(SyntaxNodeAnalysisContext ctx, ConcurrentBag<(IFieldSymbol field, Location location, string trigger, string lockText)> pending)
        {
            var inv = (InvocationExpressionSyntax)ctx.Node;
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            var m = Util.MethodOf(inv, model, ct);
            if (m == null) return;

            SyncOverAsync(ctx, inv, m);

            var trigger = Signals.Trigger(inv, m, model, ct);
            if (trigger != null)
            {
                var lockText = Locks.Context(inv, model, ct);
                if (lockText == null || Signals.IsHarmless(inv, m, model, ct)) return;
                // Only Cancel() can be proven: it runs the token's callbacks on this thread by contract.
                if (m.Name == "Cancel" && IsCts(m.ContainingType) && Util.Receiver(inv) is ExpressionSyntax recv &&
                    model.GetEnclosingSymbol(inv.SpanStart, ct)?.ContainingType is INamedTypeSymbol owner &&
                    ResolveField(recv, Util.EnclosingMember(inv), CtsFields(owner), model, ct, 3) is IFieldSymbol field)
                {
                    pending.Add((field, inv.GetLocation(), trigger, lockText));
                    return;
                }
                ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR035, inv.GetLocation(), Unproven(trigger, lockText)));
                return;
            }

            var current = model.GetEnclosingSymbol(inv.SpanStart, ct)?.ContainingType;
            var reason = UnknownCode.Reason(inv, m, model, current, true, ct);
            if (reason == null) return;
            var held = Locks.Context(inv, model, ct);
            if (held == null) return;
            ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR003, inv.GetLocation(),
                "'" + Util.Short(inv, 50) + "' " + reason + ", and it runs while " + held + " is held; if that code calls back into this type (a command that cancels or resets it, a handler that takes another lock) it re-enters the lock or deadlocks, and state written after it can overwrite what it did. Check whether it can run outside the lock"));
        }

        private static string Unproven(string trigger, string lockText) =>
            trigger + " is called inside " + lockText + "; this can run waiting continuations and callbacks inline on this thread while the lock is still held, so they can re-enter this lock and change the state it guards (re-entrant updates, 'Collection was modified'). Check what awaits it, and move the call after the lock is released";

        private static List<IFieldSymbol> CtsFields(INamedTypeSymbol type) =>
            type.GetMembers().OfType<IFieldSymbol>().Where(f => IsCts(f.Type) || HoldsCts(f.Type)).ToList();

        /// <summary>A .Token read of a CancellationTokenSource held in a field of this type (directly, by element, or through a local stored into it).</summary>
        private static void OnTokenRead(SyntaxNodeAnalysisContext ctx, TokenFlow flow)
        {
            var ma = (MemberAccessExpressionSyntax)ctx.Node;
            if (ma.Name.Identifier.Text != "Token") return;
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            if (model.GetSymbolInfo(ma, ct).Symbol is not IPropertySymbol p || !IsCts(p.ContainingType)) return;
            var member = Util.EnclosingMember(ma);
            if (member == null || model.GetEnclosingSymbol(ma.SpanStart, ct)?.ContainingType is not INamedTypeSymbol owner) return;
            var fields = CtsFields(owner);
            if (fields.Count == 0) return;
            if (DisposedField(ma.Expression, ma, member, fields, model, ct) is IFieldSymbol field)
                flow.OnTokenRead(field, ma, member, model, ct);
        }

        private static void OnTokenParameters(SyntaxNodeAnalysisContext ctx, TokenFlow flow)
        {
            if (ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ctx.CancellationToken) is IMethodSymbol m && m.Parameters.Any(x => TokenFlow.IsToken(x.Type)))
                flow.OnFunction(ctx.Node, m, ctx.SemanticModel, ctx.CancellationToken);
        }

        private static void SyncOverAsync(SyntaxNodeAnalysisContext ctx, InvocationExpressionSyntax inv, IMethodSymbol m)
        {
            string? what = null;
            var tn = m.ContainingType?.FullName() ?? "";
            if ((tn == "System.Threading.Tasks.Task" || tn == "System.Threading.Tasks.Task`1") && m.Name == "Wait") what = ".Wait()";
            else if (tn == "System.Threading.Tasks.Task" && (m.Name == "WaitAll" || m.Name == "WaitAny")) what = "Task." + m.Name + "()";
            else if (m.Name == "GetResult" && m.ContainingType != null && m.ContainingType.Ns() == "System.Runtime.CompilerServices" && m.ContainingType.Name.Contains("Awaiter"))
                what = ".GetAwaiter().GetResult()";
            if (what == null) return;

            var taskExpr = Util.Receiver(inv);
            if (what == ".GetAwaiter().GetResult()" && taskExpr is InvocationExpressionSyntax getAwaiter)
                taskExpr = Util.Receiver(getAwaiter);
            if (taskExpr is InvocationExpressionSyntax cfg && Util.InvokedName(cfg) == "ConfigureAwait")
                taskExpr = Util.Receiver(cfg);
            if (taskExpr != null && KnownComplete(taskExpr, inv, ctx.SemanticModel, ctx.CancellationToken)) return;

            ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR005, inv.GetLocation(),
                what + " blocks this thread until the task finishes; on the game thread that stalls the simulation, and with a captured SynchronizationContext it can deadlock. Await it, or show the task is already complete here"));
        }

        private static void OnMemberAccess(SyntaxNodeAnalysisContext ctx)
        {
            var ma = (MemberAccessExpressionSyntax)ctx.Node;
            if (ma.Name.Identifier.Text != "Result") return;
            if (ctx.SemanticModel.GetSymbolInfo(ma, ctx.CancellationToken).Symbol is not IPropertySymbol p) return;
            var tn = p.ContainingType.FullName();
            if (tn != "System.Threading.Tasks.Task`1" && tn != "System.Threading.Tasks.ValueTask`1") return;
            if (KnownComplete(ma.Expression, ma, ctx.SemanticModel, ctx.CancellationToken)) return;
            ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR005, ma.GetLocation(),
                "'" + Util.Short(ma, 50) + "' (.Result) blocks this thread until the task finishes; on the game thread that stalls the simulation, and with a captured SynchronizationContext it can deadlock. Await it, or show the task is already complete here"));
        }

        /// <summary>The task was awaited or its IsCompleted/Status checked earlier in the function, or it is a ContinueWith parameter.</summary>
        private static bool KnownComplete(ExpressionSyntax taskExpr, SyntaxNode at, SemanticModel model, CancellationToken ct)
        {
            var sym = Util.SymbolOf(taskExpr, model, ct);
            if (sym == null) return false;
            if (sym is IParameterSymbol ps && ps.ContainingSymbol is IMethodSymbol lm && lm.MethodKind == MethodKind.AnonymousFunction)
            {
                var lambda = ps.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct)?.FirstAncestorOrSelf<AnonymousFunctionExpressionSyntax>();
                if (lambda?.Parent is ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax call } && Util.InvokedName(call) == "ContinueWith")
                    return true;
            }
            var function = Util.EnclosingFunction(at);
            if (function == null) return false;
            foreach (var id in Util.FunctionNodes(function).OfType<IdentifierNameSyntax>())
            {
                if (id.SpanStart >= at.SpanStart || id.Identifier.Text != sym.Name) continue;
                if (!Util.Same(model.GetSymbolInfo(id, ct).Symbol, sym)) continue;
                if (id.Parent is MemberAccessExpressionSyntax pm && pm.Expression == id &&
                    pm.Name.Identifier.Text is "IsCompleted" or "IsCompletedSuccessfully" or "IsFaulted" or "IsCanceled" or "Status")
                    return true;
                for (var p = id.Parent; p != null && p != function; p = p.Parent)
                    if (p is AwaitExpressionSyntax) return true;
            }
            return false;
        }

        // ---------------- DR002 ----------------

        private static readonly HashSet<string> Mutators = new HashSet<string>(StringComparer.Ordinal)
        {
            "Add", "AddRange", "Remove", "RemoveAt", "RemoveAll", "RemoveRange", "RemoveWhere", "Insert", "InsertRange", "Clear",
            "Push", "Pop", "Enqueue", "Dequeue", "TryAdd", "TryRemove", "TryDequeue", "TryPop", "Sort", "Reverse", "AddFirst",
            "AddLast", "RemoveFirst", "RemoveLast", "UnionWith", "ExceptWith", "IntersectWith", "SymmetricExceptWith", "Move",
            "AddOrUpdate", "GetOrAdd", "TrimExcess"
        };

        internal static string? MutationOf(SyntaxNode n, ISymbol coll, SemanticModel model, CancellationToken ct)
        {
            switch (n)
            {
                case InvocationExpressionSyntax inv:
                    {
                        var name = Util.InvokedName(inv);
                        if (!Mutators.Contains(name)) return null;
                        var recv = Util.Receiver(inv);
                        if (recv == null || Util.LastName(recv) != coll.Name) return null;
                        return Util.Same(Util.SymbolOf(recv, model, ct), coll) ? name : null;
                    }
                case AssignmentExpressionSyntax asg when asg.Left is ElementAccessExpressionSyntax ea:
                    return Util.LastName(ea.Expression) == coll.Name && Util.Same(Util.SymbolOf(ea.Expression, model, ct), coll) ? "indexer set" : null;
            }
            return null;
        }

        private static bool FollowedByExit(SyntaxNode mutation)
        {
            var stmt = mutation.FirstAncestorOrSelf<StatementSyntax>();
            if (stmt is ReturnStatementSyntax) return true;
            if (stmt?.Parent is BlockSyntax block)
            {
                var i = block.Statements.IndexOf(stmt);
                if (i >= 0 && i + 1 < block.Statements.Count)
                    return block.Statements[i + 1] is BreakStatementSyntax or ReturnStatementSyntax or ThrowStatementSyntax;
            }
            return false;
        }

        private static void OnForEach(SyntaxNodeAnalysisContext ctx)
        {
            var fe = (CommonForEachStatementSyntax)ctx.Node;
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            var collExpr = Util.Strip(fe.Expression);
            var coll = Util.SymbolOf(collExpr, model, ct);
            if (coll is not (IFieldSymbol or IPropertySymbol)) return;
            if (collExpr is MemberAccessExpressionSyntax ma &&
                !(ma.Expression is ThisExpressionSyntax || ma.Expression is BaseExpressionSyntax || Util.SymbolOf(ma.Expression, model, ct) is INamedTypeSymbol))
                return;
            if (!Util.IsMutableCollection(Util.TypeOfMember(coll))) return;
            var typeDecl = fe.FirstAncestorOrSelf<TypeDeclarationSyntax>();
            if (typeDecl == null || model.GetDeclaredSymbol(typeDecl, ct) is not INamedTypeSymbol typeSym) return;
            if (!Util.IsSameOrBaseOf(coll.ContainingType, typeSym)) return;

            // (a) the loop body changes the collection itself, directly or through one of this type's own methods
            var direct = new List<string>();
            foreach (var n in Util.FunctionNodes(fe.Statement))
            {
                var mut = MutationOf(n, coll, model, ct);
                if (mut != null && !FollowedByExit(n))
                {
                    direct.Add(mut + " (line " + Util.Line(n) + ")");
                    continue;
                }
                if (n is InvocationExpressionSyntax call && Util.MethodOf(call, model, ct) is IMethodSymbol cm && Util.Same(cm.ContainingType, typeSym))
                {
                    foreach (var r in cm.DeclaringSyntaxReferences)
                    {
                        if (r.SyntaxTree != model.SyntaxTree) continue;
                        var body = r.GetSyntax(ct);
                        var inner = Util.FunctionNodes(body).Select(x => MutationOf(x, coll, model, ct)).FirstOrDefault(x => x != null);
                        if (inner != null) direct.Add(cm.Name + "() which calls " + inner + " (line " + Util.Line(call) + ")");
                    }
                }
            }
            if (direct.Count > 0)
            {
                ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR002, fe.ForEachKeyword.GetLocation(),
                    "foreach over '" + coll.Name + "' changes it inside the loop: " + string.Join(", ", direct.Distinct()) +
                    "; the next MoveNext throws 'Collection was modified'. Iterate a snapshot (ToArray/ToList) or apply the changes after the loop"));
                return;
            }

            // (b) the body runs code that can re-enter, and the collection is changed elsewhere in the type
            string? runs = null;
            foreach (var call in Util.FunctionNodes(fe.Statement).OfType<InvocationExpressionSyntax>())
            {
                var cm = Util.MethodOf(call, model, ct);
                if (cm == null) continue;
                runs = Signals.Trigger(call, cm, model, ct) ?? UnknownCode.Reason(call, cm, model, typeSym, true, ct);
                if (runs != null) break;
            }
            if (runs == null) return;

            var elsewhere = new List<string>();
            foreach (var n in Util.TypeNodes(typeDecl))
            {
                if (fe.Span.Contains(n.Span)) continue;
                var mut = MutationOf(n, coll, model, ct);
                if (mut == null) continue;
                var member = Util.EnclosingMember(n);
                if (member is ConstructorDeclarationSyntax || member is BaseFieldDeclarationSyntax) continue;
                var tags = new List<string>();
                if (Util.IsAsync(member)) tags.Add("async");
                if (n.Ancestors().TakeWhile(a => a != member).Any(a => a is FinallyClauseSyntax)) tags.Add("finally");
                if (n.Ancestors().TakeWhile(a => a != member).Any(a => a is AnonymousFunctionExpressionSyntax)) tags.Add("lambda");
                if (member is MethodDeclarationSyntax md && model.GetDeclaredSymbol(md, ct) is IMethodSymbol ms && Util.IsEventHandlerSignature(ms)) tags.Add("event handler");
                elsewhere.Add(Util.MemberName(member) + (tags.Count > 0 ? " (" + string.Join(", ", tags) + ")" : "") + " via " + mut);
            }
            if (elsewhere.Count == 0) return;
            ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR002, fe.ForEachKeyword.GetLocation(),
                "foreach over '" + coll.Name + "' runs code inside the loop that can re-enter this type (" + runs + "), and '" + coll.Name +
                "' is changed elsewhere: " + string.Join("; ", elsewhere.Distinct()) +
                ". If that change runs during the loop (an inline continuation, a callback, another thread) the enumeration throws 'Collection was modified'. Iterate a snapshot taken under the lock"));
        }

        // ---------------- DR004 ----------------

        private static void OnAsyncVoid(SyntaxNodeAnalysisContext ctx)
        {
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            switch (ctx.Node)
            {
                case MethodDeclarationSyntax md when md.Modifiers.Any(SyntaxKind.AsyncKeyword) && IsVoid(md.ReturnType) && !CatchesEverything(md.Body):
                    if (model.GetDeclaredSymbol(md, ct) is IMethodSymbol ms && !Util.IsEventHandlerSignature(ms))
                        Report(md.Identifier.GetLocation(), "method " + md.Identifier.Text);
                    break;
                case LocalFunctionStatementSyntax lf when lf.Modifiers.Any(SyntaxKind.AsyncKeyword) && IsVoid(lf.ReturnType) && !CatchesEverything(lf.Body):
                    if (model.GetDeclaredSymbol(lf, ct) is IMethodSymbol ls && !Util.IsEventHandlerSignature(ls))
                        Report(lf.Identifier.GetLocation(), "local function " + lf.Identifier.Text);
                    break;
                case AnonymousFunctionExpressionSyntax af when af.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword) && !CatchesEverything(af.Block):
                    if (model.GetTypeInfo(af, ct).ConvertedType is INamedTypeSymbol { TypeKind: TypeKind.Delegate } d &&
                        d.DelegateInvokeMethod is IMethodSymbol invoke && invoke.ReturnsVoid && !Util.IsEventHandlerSignature(invoke))
                        Report(af.AsyncKeyword.GetLocation(), "lambda converted to " + d.Name);
                    break;
            }

            void Report(Location location, string what) =>
                ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR004, location,
                    "async void " + what + ": an exception after its first await escapes to the thread pool or game thread and ends the process, and no caller can await it or see it finish. Return Task, or wrap the whole body in try/catch (Exception) and log"));
        }

        /// <summary>The body is a single try statement with a catch-all (catch or catch (Exception)) and no filter.</summary>
        private static bool CatchesEverything(BlockSyntax? body) =>
            body != null && body.Statements.Count == 1 && body.Statements[0] is TryStatementSyntax t &&
            t.Catches.Any(c => c.Filter == null && (c.Declaration == null || Util.LastName(c.Declaration.Type) is "Exception"));

        private static bool IsVoid(TypeSyntax t) => t is PredefinedTypeSyntax p && p.Keyword.IsKind(SyntaxKind.VoidKeyword);

        // ---------------- DR007 ----------------

        private static void OnLock(SyntaxNodeAnalysisContext ctx)
        {
            var ls = (LockStatementSyntax)ctx.Node;
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            var e = Util.Strip(ls.Expression);
            string? why = null;
            if (e is ThisExpressionSyntax)
                why = "lock (this): any code holding a reference to this object can take or hold the same lock";
            else if (e is TypeOfExpressionSyntax || (e is InvocationExpressionSyntax gi && Util.InvokedName(gi) == "GetType"))
                why = "lock on a Type object: it is shared by every assembly in the process (other plugins, the game)";
            else
            {
                var t = model.GetTypeInfo(e, ct).Type;
                if (t?.SpecialType == SpecialType.System_String)
                    why = "lock on a string: equal strings may be the same interned object across the whole process";
                else if (e is CastExpressionSyntax cast && model.GetTypeInfo(cast.Expression, ct).Type?.IsValueType == true)
                    why = "lock on a boxed value type: every box is a new object, so nothing is actually locked";
                else if (t.Is("System.Type"))
                    why = "lock on a Type object: it is shared by every assembly in the process";
                else if (Util.SymbolOf(e, model, ct) is ISymbol s && (s is IFieldSymbol || s is IPropertySymbol) && s.DeclaredAccessibility == Accessibility.Public)
                    why = "lock on public " + (s is IFieldSymbol ? "field" : "property") + " '" + s.Name + "': code outside this type can take or hold the same lock";
            }
            if (why != null)
                ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR007, ls.Expression.GetLocation(), why + ". Lock on a private readonly object instead"));
        }

        // ---------------- DR008 ----------------

        private static void OnLazyInit(SyntaxNodeAnalysisContext ctx)
        {
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            IFieldSymbol? field = null;
            SyntaxNode? assignment = null;
            switch (ctx.Node)
            {
                case IfStatementSyntax ifs:
                    {
                        var checkedExpr = NullCheckedExpression(ifs.Condition);
                        if (checkedExpr == null || Util.SymbolOf(checkedExpr, model, ct) is not IFieldSymbol f) return;
                        assignment = ifs.Statement.DescendantNodesAndSelf().OfType<AssignmentExpressionSyntax>()
                            .FirstOrDefault(a => a.IsKind(SyntaxKind.SimpleAssignmentExpression) && Util.Same(Util.SymbolOf(a.Left, model, ct), f));
                        field = f;
                        break;
                    }
                case AssignmentExpressionSyntax ca when ca.IsKind(SyntaxKind.CoalesceAssignmentExpression):
                    field = Util.SymbolOf(ca.Left, model, ct) as IFieldSymbol;
                    assignment = ca;
                    break;
                case BinaryExpressionSyntax co when co.IsKind(SyntaxKind.CoalesceExpression):
                    if (Util.Strip(co.Right) is AssignmentExpressionSyntax inner && inner.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
                        Util.SymbolOf(co.Left, model, ct) is IFieldSymbol cf && Util.Same(Util.SymbolOf(inner.Left, model, ct), cf))
                    {
                        field = cf;
                        assignment = inner;
                    }
                    break;
            }
            if (field == null || assignment == null) return;
            if (!field.IsStatic || field.IsReadOnly || field.IsConst || field.HasAttribute("ThreadStatic")) return;
            if (Locks.Context(assignment, model, ct) != null || Locks.Context(ctx.Node, model, ct) != null) return;
            var typeDecl = ctx.Node.FirstAncestorOrSelf<TypeDeclarationSyntax>();
            if (typeDecl == null) return;
            var concurrency = ConcurrencyUse(typeDecl, model, ct);
            if (concurrency == null) return;
            ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR008, ctx.Node.GetLocation(),
                "static '" + field.Name + "' is created on first use without a lock, Lazy<T> or Interlocked.CompareExchange, and this type also " + concurrency +
                "; two threads can both see null and build two instances (the loser's state, handlers or registrations are lost). Use Lazy<T> or a lock"));
        }

        private static ExpressionSyntax? NullCheckedExpression(ExpressionSyntax cond)
        {
            cond = Util.Strip(cond);
            if (cond is BinaryExpressionSyntax b && b.IsKind(SyntaxKind.EqualsExpression))
            {
                if (b.Right.IsKind(SyntaxKind.NullLiteralExpression)) return b.Left;
                if (b.Left.IsKind(SyntaxKind.NullLiteralExpression)) return b.Right;
            }
            if (cond is IsPatternExpressionSyntax ip && ip.Pattern is ConstantPatternSyntax cp && cp.Expression.IsKind(SyntaxKind.NullLiteralExpression))
                return ip.Expression;
            return null;
        }

        internal static string? ConcurrencyUse(TypeDeclarationSyntax typeDecl, SemanticModel model, CancellationToken ct)
        {
            foreach (var n in Util.TypeNodes(typeDecl))
            {
                switch (n)
                {
                    case AwaitExpressionSyntax:
                        return "uses async/await";
                    case MethodDeclarationSyntax md when md.Modifiers.Any(SyntaxKind.AsyncKeyword):
                        return "has async methods";
                    case AnonymousFunctionExpressionSyntax af when af.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword):
                        return "has async lambdas";
                    case InvocationExpressionSyntax inv:
                        {
                            var name = Util.InvokedName(inv);
                            if (name != "Run" && name != "StartNew" && name != "QueueUserWorkItem" && name != "For" && name != "ForEach" && name != "Invoke") break;
                            var m = Util.MethodOf(inv, model, ct);
                            var tn = m?.ContainingType.FullName();
                            if (tn == "System.Threading.Tasks.Task" || tn == "System.Threading.Tasks.TaskFactory" || tn == "System.Threading.ThreadPool" || tn == "System.Threading.Tasks.Parallel")
                                return "calls " + m?.ContainingType.Name + "." + name;
                            break;
                        }
                    case BaseObjectCreationExpressionSyntax oc:
                        {
                            var t = model.GetTypeInfo(oc, ct).Type;
                            if (t.IsAny("System.Threading.Thread", "System.Threading.Timer", "System.Timers.Timer"))
                                return "creates a " + t?.Name;
                            break;
                        }
                }
            }
            return null;
        }

        // ---------------- DR009 ----------------

        private static void OnTimerCreation(SyntaxNodeAnalysisContext ctx)
        {
            var oc = (BaseObjectCreationExpressionSyntax)ctx.Node;
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            var t = model.GetTypeInfo(oc, ct).Type;
            if (!t.IsAny("System.Threading.Timer", "System.Timers.Timer")) return;
            var timerName = t.FullName();
            SyntaxNode cur = oc;
            while (cur.Parent is ParenthesizedExpressionSyntax || cur.Parent is CastExpressionSyntax) cur = cur.Parent;
            switch (cur.Parent)
            {
                case ExpressionStatementSyntax:
                    Report("is created and not stored anywhere");
                    break;
                case EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax v }:
                    {
                        var declared = model.GetDeclaredSymbol(v, ct);
                        if (declared is IFieldSymbol f) CheckStored(f);
                        else if (declared is ILocalSymbol l)
                        {
                            if (v.Parent?.Parent is LocalDeclarationStatementSyntax lds && lds.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)) return;
                            if (!LocalEscapes(l, v, model, ct)) Report("is only kept in the local '" + l.Name + "'");
                        }
                        break;
                    }
                case EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax pd }:
                    if (model.GetDeclaredSymbol(pd, ct) is IPropertySymbol ps) CheckStored(ps);
                    break;
                case AssignmentExpressionSyntax a when a.Right == cur:
                    {
                        var s = Util.SymbolOf(a.Left, model, ct);
                        if (s is IFieldSymbol || s is IPropertySymbol) CheckStored(s);
                        else if (s is ILocalSymbol l && !LocalEscapes(l, a, model, ct)) Report("is only kept in the local '" + l.Name + "'");
                        break;
                    }
            }

            void CheckStored(ISymbol member)
            {
                // Code outside this type can reach a non-private member and dispose it there.
                if (member.DeclaredAccessibility != Accessibility.Private) return;
                var typeDecl = oc.FirstAncestorOrSelf<TypeDeclarationSyntax>();
                if (typeDecl == null) return;
                foreach (var n in Util.TypeNodes(typeDecl))
                {
                    // handed to other code (a disposer, a list of disposables, another owner)
                    if (n is ArgumentSyntax arg && Util.Same(Util.SymbolOf(arg.Expression, model, ct), member)) return;
                    if (n is InvocationExpressionSyntax inv)
                    {
                        var name = Util.InvokedName(inv);
                        var stops = name == "Dispose" || name == "DisposeAsync" || name == "Stop" || name == "Close" ||
                                    (name == "Change" && inv.ArgumentList.ToString().Contains("Infinite"));
                        if (stops && Util.Receiver(inv) is ExpressionSyntax r && Util.Same(Util.SymbolOf(r, model, ct), member)) return;
                    }
                    else if (n is AssignmentExpressionSyntax asg && asg.Left is MemberAccessExpressionSyntax ma && ma.Name.Identifier.Text == "Enabled" &&
                             asg.Right.IsKind(SyntaxKind.FalseLiteralExpression) && Util.Same(Util.SymbolOf(ma.Expression, model, ct), member))
                        return;
                    else if (n is UsingStatementSyntax us && us.Expression != null && Util.Same(Util.SymbolOf(us.Expression, model, ct), member))
                        return;
                }
                Report("is stored in the private member '" + member.Name + "', but this type never disposes or stops it and never hands it to other code, so it keeps firing, and keeps its callback's objects alive, after this object is finished with (in a plugin: after the plugin unloads or the world reloads)");
            }

            void Report(string what) =>
                ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR009, oc.GetLocation(),
                    timerName + " " + what + (what.StartsWith("is stored", StringComparison.Ordinal)
                        ? ". Dispose it in Dispose/Unload"
                        : "; nothing roots it, so the GC can collect it and it silently stops firing (or, if it is rooted elsewhere, nothing can stop it). Keep it in a field and dispose it on unload")));
        }

        private static bool LocalEscapes(ILocalSymbol local, SyntaxNode near, SemanticModel model, CancellationToken ct)
        {
            var function = Util.EnclosingFunction(near);
            if (function == null) return true;
            foreach (var id in function.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (id.Identifier.Text != local.Name || !Util.Same(model.GetSymbolInfo(id, ct).Symbol, local)) continue;
                SyntaxNode cur = id;
                while (cur.Parent is ParenthesizedExpressionSyntax || cur.Parent is CastExpressionSyntax) cur = cur.Parent;
                switch (cur.Parent)
                {
                    case ArgumentSyntax:
                    case ReturnStatementSyntax:
                    case ArrowExpressionClauseSyntax:
                    case InitializerExpressionSyntax:
                    case CollectionExpressionSyntax:
                    case ExpressionElementSyntax:
                        return true;
                    case AssignmentExpressionSyntax a when a.Right == cur && Util.SymbolOf(a.Left, model, ct) is not ILocalSymbol:
                        return true;
                    case EqualsValueClauseSyntax eq when eq.Parent is VariableDeclaratorSyntax v2 && model.GetDeclaredSymbol(v2, ct) is not ILocalSymbol:
                        return true;
                }
            }
            return false;
        }

        // ---------------- DR006 ----------------

        private static void OnType(SyntaxNodeAnalysisContext ctx)
        {
            var typeDecl = (TypeDeclarationSyntax)ctx.Node;
            if (ctx.SemanticModel.GetDeclaredSymbol(typeDecl, ctx.CancellationToken) is not INamedTypeSymbol typeSym) return;
            CtsDisposeRace(ctx, typeDecl, typeSym);
        }

        private static bool IsCts(ITypeSymbol? t) => t.Is("System.Threading.CancellationTokenSource");

        private static bool HoldsCts(ITypeSymbol? t)
        {
            if (t is IArrayTypeSymbol at) return IsCts(at.ElementType);
            return t is INamedTypeSymbol nt && nt.TypeArguments.Any(a => IsCts(a) || HoldsCts(a));
        }

        private static readonly HashSet<string> StoreNames = new HashSet<string>(StringComparer.Ordinal)
            { "Add", "TryAdd", "Push", "Enqueue", "Insert", "AddOrUpdate", "GetOrAdd", "AddFirst", "AddLast" };

        private static void CtsDisposeRace(SyntaxNodeAnalysisContext ctx, TypeDeclarationSyntax typeDecl, INamedTypeSymbol typeSym)
        {
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            var fields = typeSym.GetMembers().OfType<IFieldSymbol>().Where(f => IsCts(f.Type) || HoldsCts(f.Type)).ToList();
            if (fields.Count == 0) return;

            var disposes = new List<(InvocationExpressionSyntax inv, IFieldSymbol field, SyntaxNode? member)>();
            var uses = new List<(SyntaxNode node, IFieldSymbol field, SyntaxNode? member, string what)>();
            foreach (var n in Util.TypeNodes(typeDecl))
            {
                if (n is InvocationExpressionSyntax inv)
                {
                    var name = Util.InvokedName(inv);
                    if (name != "Dispose" && name != "Cancel" && name != "CancelAfter") continue;
                    var m = Util.MethodOf(inv, model, ct);
                    if (m == null || !IsCts(m.ContainingType)) continue;
                    var recv = Util.Receiver(inv);
                    if (recv == null) continue;
                    var member = Util.EnclosingMember(inv);
                    if (name == "Dispose")
                    {
                        var f = DisposedField(recv, inv, member, fields, model, ct);
                        if (f != null) disposes.Add((inv, f, member));
                    }
                    else
                    {
                        var f = ResolveField(recv, member, fields, model, ct, 3);
                        if (f != null && !CatchesDisposed(inv)) uses.Add((inv, f, member, "CancellationTokenSource." + name + "()"));
                    }
                }
                else if (n is MemberAccessExpressionSyntax ma && ma.Name.Identifier.Text == "Token")
                {
                    if (model.GetSymbolInfo(ma, ct).Symbol is not IPropertySymbol p || !IsCts(p.ContainingType)) continue;
                    var member = Util.EnclosingMember(ma);
                    var f = ResolveField(ma.Expression, member, fields, model, ct, 3);
                    if (f != null && !CatchesDisposed(ma)) uses.Add((ma, f, member, ".Token"));
                }
            }

            foreach (var d in disposes)
            {
                var others = uses.Where(u => Util.Same(u.field, d.field) && u.member != d.member).ToList();
                if (others.Count == 0) continue;
                var where = string.Join(", ", others.Select(u => Util.MemberName(u.member) + " (" + u.what + " at line " + Util.Line(u.node) + ")").Distinct().Take(4));
                ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR006, d.inv.GetLocation(),
                    "a CancellationTokenSource held in '" + d.field.Name + "' is disposed here while " + where +
                    " can still reach it (directly or from a snapshot) without catching ObjectDisposedException; if that runs after or during this Dispose it throws. Do not dispose it (a CTS without CancelAfter holds nothing to free), or catch ObjectDisposedException on the cancel side"));
            }
        }

        private static bool CatchesDisposed(SyntaxNode node) =>
            Util.InTryWithCatch(node, c => c.Declaration == null || Util.CatchTypeName(c) is "ObjectDisposedException" or "Exception");

        private static IFieldSymbol? DisposedField(ExpressionSyntax recv, SyntaxNode inv, SyntaxNode? member, List<IFieldSymbol> fields, SemanticModel model, CancellationToken ct)
        {
            recv = Util.Strip(recv);
            if (recv is InvocationExpressionSyntax exchange)
            {
                foreach (var arg in exchange.ArgumentList.Arguments)
                    if (arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) && Util.SymbolOf(arg.Expression, model, ct) is IFieldSymbol rf)
                        return fields.FirstOrDefault(x => Util.Same(x, rf));
                return null;
            }
            var s = Util.SymbolOf(recv, model, ct);
            if (s is IFieldSymbol f) return fields.FirstOrDefault(x => Util.Same(x, f));
            if (s is not (ILocalSymbol or IParameterSymbol) || member == null) return null;

            // stored into a field or collection field earlier in the same member
            foreach (var n in member.DescendantNodes())
            {
                if (n.SpanStart >= inv.SpanStart) break;
                if (n is InvocationExpressionSyntax store && StoreNames.Contains(Util.InvokedName(store)) &&
                    Util.Receiver(store) is ExpressionSyntax sr && Util.SymbolOf(sr, model, ct) is IFieldSymbol sf &&
                    fields.FirstOrDefault(x => Util.Same(x, sf)) is IFieldSymbol hit &&
                    store.ArgumentList.Arguments.Any(a => Util.Same(Util.SymbolOf(a.Expression, model, ct), s)))
                    return hit;
                if (n is AssignmentExpressionSyntax asg && asg.IsKind(SyntaxKind.SimpleAssignmentExpression) && Util.Same(Util.SymbolOf(asg.Right, model, ct), s))
                {
                    var left = asg.Left is ElementAccessExpressionSyntax ea ? ea.Expression : asg.Left;
                    if (Util.SymbolOf(left, model, ct) is IFieldSymbol af && fields.FirstOrDefault(x => Util.Same(x, af)) is IFieldSymbol hit2)
                        return hit2;
                }
            }
            return ResolveField(recv, member, fields, model, ct, 3);
        }

        /// <summary>Does the expression reach one of the CTS fields: directly, by element access, or through locals taken from it?</summary>
        private static IFieldSymbol? ResolveField(ExpressionSyntax expr, SyntaxNode? member, List<IFieldSymbol> fields, SemanticModel model, CancellationToken ct, int depth)
        {
            expr = Util.Strip(expr);
            if (expr is ElementAccessExpressionSyntax ea) return ResolveField(ea.Expression, member, fields, model, ct, depth);
            var s = Util.SymbolOf(expr, model, ct);
            if (s is IFieldSymbol f) return fields.FirstOrDefault(x => Util.Same(x, f));
            if (s is IPropertySymbol && expr is MemberAccessExpressionSyntax pma) return ResolveField(pma.Expression, member, fields, model, ct, depth);
            if (depth <= 0 || s is not ILocalSymbol local) return null;
            foreach (var r in local.DeclaringSyntaxReferences)
            {
                IFieldSymbol? hit = null;
                switch (r.GetSyntax(ct))
                {
                    case VariableDeclaratorSyntax v when v.Initializer != null:
                        hit = FieldIn(v.Initializer.Value, member, fields, model, ct, depth - 1);
                        break;
                    case CommonForEachStatementSyntax fe:
                        hit = FieldIn(fe.Expression, member, fields, model, ct, depth - 1);
                        break;
                    case SingleVariableDesignationSyntax { Parent: DeclarationExpressionSyntax { Parent: ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax call } } }:
                        if (Util.Receiver(call) is ExpressionSyntax cr) hit = ResolveField(cr, member, fields, model, ct, depth - 1);
                        break;
                    case SingleVariableDesignationSyntax sv when sv.FirstAncestorOrSelf<CommonForEachStatementSyntax>() is CommonForEachStatementSyntax dfe:
                        hit = FieldIn(dfe.Expression, member, fields, model, ct, depth - 1);
                        break;
                }
                if (hit != null) return hit;
            }
            if (member != null)
            {
                foreach (var a in member.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                    if (a.IsKind(SyntaxKind.SimpleAssignmentExpression) && Util.Same(Util.SymbolOf(a.Left, model, ct), local) &&
                        FieldIn(a.Right, member, fields, model, ct, depth - 1) is IFieldSymbol hit)
                        return hit;
            }
            return null;
        }

        private static IFieldSymbol? FieldIn(ExpressionSyntax e, SyntaxNode? member, List<IFieldSymbol> fields, SemanticModel model, CancellationToken ct, int depth)
        {
            foreach (var id in e.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                var hit = ResolveField(id, member, fields, model, ct, depth);
                if (hit != null) return hit;
            }
            return null;
        }
    }
}
