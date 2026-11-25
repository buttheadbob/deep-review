using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace DeepRules
{
    /// <summary>DR026-DR032: comparers, loops, replacement order, build-only code, nullable, reflection, threads.</summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class CorrectnessAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
            Rules.DR026, Rules.DR027, Rules.DR028, Rules.DR029, Rules.DR030, Rules.DR031, Rules.DR032);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(OnComparerMethod, SyntaxKind.MethodDeclaration);
            context.RegisterSyntaxNodeAction(OnComparerLambda, SyntaxKind.SimpleLambdaExpression, SyntaxKind.ParenthesizedLambdaExpression, SyntaxKind.AnonymousMethodExpression);
            context.RegisterSyntaxNodeAction(OnBreak, SyntaxKind.BreakStatement);
            context.RegisterSyntaxNodeAction(OnFunction, SyntaxKind.MethodDeclaration, SyntaxKind.ConstructorDeclaration, SyntaxKind.LocalFunctionStatement,
                SyntaxKind.GetAccessorDeclaration, SyntaxKind.SetAccessorDeclaration, SyntaxKind.SimpleLambdaExpression, SyntaxKind.ParenthesizedLambdaExpression,
                SyntaxKind.AnonymousMethodExpression);
            context.RegisterCompilationStartAction(start =>
            {
                var host = HostContext.Of(start.Compilation);
                start.RegisterSyntaxTreeAction(c => OnTreeDirectives(c, host));
                // DR030 is decided by the compilation's own nullable setting: with annotations off, every '?' on a
                // reference type is CS8632 and every '!' does nothing, unless the file enables them itself.
                var nullable = start.Compilation.Options.NullableContextOptions;
                if (nullable is NullableContextOptions.Disable or NullableContextOptions.Warnings)
                    start.RegisterSemanticModelAction(c => OnNullableAnnotations(c, nullable, host));
                start.RegisterSyntaxNodeAction(c => OnInvocation(c, host), SyntaxKind.InvocationExpression);
            });
        }

        // ---------------- DR026 ----------------

        private static void OnComparerMethod(SyntaxNodeAnalysisContext ctx)
        {
            var md = (MethodDeclarationSyntax)ctx.Node;
            var name = md.Identifier.Text;
            if (name != "Compare" && name != "CompareTo") return;
            if (md.Body == null && md.ExpressionBody == null) return;
            if (ctx.SemanticModel.GetDeclaredSymbol(md, ctx.CancellationToken) is not IMethodSymbol m || m.ReturnType.SpecialType != SpecialType.System_Int32) return;
            if (m.Parameters.Length != (name == "Compare" ? 2 : 1)) return;
            CheckComparer(ctx, md, md.ExpressionBody?.Expression, name);
        }

        private static void OnComparerLambda(SyntaxNodeAnalysisContext ctx)
        {
            var af = (AnonymousFunctionExpressionSyntax)ctx.Node;
            if (ctx.SemanticModel.GetTypeInfo(af, ctx.CancellationToken).ConvertedType is not INamedTypeSymbol d || !d.Is("System.Comparison`1")) return;
            CheckComparer(ctx, af, af.ExpressionBody, "Comparison lambda");
        }

        private static void CheckComparer(SyntaxNodeAnalysisContext ctx, SyntaxNode function, ExpressionSyntax? expressionBody, string what)
        {
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            foreach (var n in Util.FunctionNodes(function))
            {
                if (n is BinaryExpressionSyntax b && b.Kind() is SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression or
                        SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression &&
                    ((IsAbsDifference(b.Left, model, ct) && IsTolerance(b.Right, model, ct)) || (IsAbsDifference(b.Right, model, ct) && IsTolerance(b.Left, model, ct))))
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR026, b.GetLocation(),
                        what + " treats values within a tolerance as equal ('" + Util.Short(b, 40) + "'); that is not transitive (a≈b, b≈c but a<c), so List.Sort can throw 'IComparer.Compare() method returns inconsistent results' or order items differently each time (PluginHub #190). Compare exactly, or round to buckets first"));
                    return;
                }
            }
            var returns = Util.FunctionNodes(function).OfType<ReturnStatementSyntax>().Select(r => r.Expression).ToList();
            if (expressionBody != null) returns.Add(expressionBody);
            foreach (var r in returns)
            {
                if (r == null) continue;
                var e = Util.Strip(r);
                var cast = false;
                if (e is CastExpressionSyntax c)
                {
                    cast = true;
                    e = Util.Strip(c.Expression);
                }
                if (e is not BinaryExpressionSyntax sub || !sub.IsKind(SyntaxKind.SubtractExpression)) continue;
                var t = model.GetTypeInfo(sub, ct).Type?.SpecialType;
                var intLike = t is SpecialType.System_Int32 or SpecialType.System_Int64;
                var truncated = cast && t is SpecialType.System_Double or SpecialType.System_Single or SpecialType.System_Int64 or SpecialType.System_Decimal;
                if (!intLike && !truncated) continue;
                ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR026, r.GetLocation(),
                    what + " returns '" + Util.Short(r, 40) + "': " + (truncated
                        ? "the cast truncates small differences to 0 and wraps large ones, so the order is inconsistent"
                        : "the subtraction overflows for large or opposite-sign values and flips the sign, so the order is inconsistent and List.Sort can throw") +
                    ". Use a.CompareTo(b)"));
                return;
            }
        }

        private static bool IsAbsDifference(ExpressionSyntax e, SemanticModel model, CancellationToken ct)
        {
            e = Util.Strip(e);
            if (e is not InvocationExpressionSyntax inv || Util.InvokedName(inv) != "Abs" || inv.ArgumentList.Arguments.Count != 1) return false;
            var tn = Util.MethodOf(inv, model, ct)?.ContainingType.FullName();
            if (tn != "System.Math" && tn != "System.MathF") return false;
            return Util.Strip(inv.ArgumentList.Arguments[0].Expression) is BinaryExpressionSyntax b && b.IsKind(SyntaxKind.SubtractExpression);
        }

        private static bool IsTolerance(ExpressionSyntax e, SemanticModel model, CancellationToken ct)
        {
            if (model.GetConstantValue(e, ct).HasValue) return true;
            var s = Util.SymbolOf(e, model, ct);
            return s is IFieldSymbol f && (f.IsConst || f.IsReadOnly) || (s != null && Regex.IsMatch(s.Name, "eps|tol", RegexOptions.IgnoreCase));
        }

        // ---------------- DR027 ----------------

        private static void OnBreak(SyntaxNodeAnalysisContext ctx)
        {
            var brk = (BreakStatementSyntax)ctx.Node;
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            SyntaxNode holder = brk;
            if (brk.Parent is BlockSyntax blk && blk.Statements.Count == 1) holder = blk;
            if (holder.Parent is not IfStatementSyntax ifs || ifs.Statement != holder || ifs.Else != null) return;

            SyntaxNode? loop = null;
            for (var p = ifs.Parent; p != null; p = p.Parent)
            {
                if (p is SwitchSectionSyntax || Util.IsNestedFunction(p) || p is MemberDeclarationSyntax) return;
                if (p is ForStatementSyntax || p is CommonForEachStatementSyntax) { loop = p; break; }
                if (p is WhileStatementSyntax || p is DoStatementSyntax) return;
            }
            if (loop == null) return;

            var element = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var index = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            if (loop is ForEachStatementSyntax fe && model.GetDeclaredSymbol(fe, ct) is ILocalSymbol iv) element.Add(iv);
            else if (loop is ForEachVariableStatementSyntax fev)
            {
                foreach (var d in fev.Variable.DescendantNodes().OfType<SingleVariableDesignationSyntax>())
                    if (model.GetDeclaredSymbol(d, ct) is ISymbol ds) element.Add(ds);
            }
            else if (loop is ForStatementSyntax fs && fs.Declaration != null)
            {
                foreach (var v in fs.Declaration.Variables)
                    if (model.GetDeclaredSymbol(v, ct) is ISymbol vs) index.Add(vs);
            }

            var mentionsElement = false;
            foreach (var id in ifs.Condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                if (id.Parent is MemberAccessExpressionSyntax pm && pm.Name == id) continue;
                if (id.Parent is MemberBindingExpressionSyntax) continue;
                var s = model.GetSymbolInfo(id, ct).Symbol;
                if (s == null || s is IMethodSymbol || s is ITypeSymbol || s is INamespaceSymbol || (s is IFieldSymbol cf && (cf.IsConst || cf.ContainingType.TypeKind == TypeKind.Enum))) continue;
                if (element.Contains(s)) { mentionsElement = true; continue; }
                if (index.Contains(s) && InElementAccess(id)) { mentionsElement = true; continue; }
                if (id.Parent is ElementAccessExpressionSyntax ea && ea.Expression == id &&
                    ea.ArgumentList.Arguments.All(a => a.Expression is IdentifierNameSyntax ai && index.Contains(model.GetSymbolInfo(ai, ct).Symbol ?? s)))
                    continue;
                return; // tests something other than the current element
            }
            if (!mentionsElement) return;
            ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR027, brk.GetLocation(),
                "break ends the whole loop when only the current element fails the test '" + Util.Short(ifs.Condition, 50) +
                "'; every later element is skipped. If the remaining elements should still be counted or processed, this should be continue (PluginHub #190 under-counted free space after a null slot)"));

            static bool InElementAccess(IdentifierNameSyntax id) =>
                id.Parent is ArgumentSyntax { Parent: BracketedArgumentListSyntax { Parent: ElementAccessExpressionSyntax } };
        }

        // ---------------- DR028 ----------------

        private static void OnFunction(SyntaxNodeAnalysisContext ctx)
        {
            var function = ctx.Node;
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            var calls = Util.FunctionNodes(function).OfType<InvocationExpressionSyntax>().ToList();
            if (calls.Count < 2) return;
            var reported = new HashSet<InvocationExpressionSyntax>();
            for (var i = 0; i < calls.Count; i++)
            {
                var rem = calls[i];
                var rname = Util.InvokedName(rem);
                if (!(rname.StartsWith("Remove", StringComparison.Ordinal) || rname.StartsWith("Delete", StringComparison.Ordinal)) || rname == "RemoveAll") continue;
                var rm = Util.MethodOf(rem, model, ct);
                if (rm?.ContainingType == null) continue;
                var fileDelete = rm.ContainingType.Is("System.IO.File") && rname == "Delete";
                if (!fileDelete && (rm.ContainingType.NsStarts("System.Collections") || IsBclCollection(Util.Receiver(rem), model, ct))) continue;
                var key = ReceiverKey(rem, rm, model, ct);
                if (key == null) continue;
                for (var j = i + 1; j < calls.Count; j++)
                {
                    var add = calls[j];
                    var aname = Util.InvokedName(add);
                    var isAdd = fileDelete
                        ? aname is "Move" or "Copy" or "WriteAllText" or "WriteAllBytes" or "WriteAllLines" or "Create"
                        : aname.StartsWith("Add", StringComparison.Ordinal) || aname.StartsWith("Insert", StringComparison.Ordinal);
                    if (!isAdd) continue;
                    var am = Util.MethodOf(add, model, ct);
                    if (am == null || ReceiverKey(add, am, model, ct) != key) continue;
                    if (Util.InAnyTry(rem, function) || Util.InAnyTry(add, function)) break;
                    if (!reported.Add(rem)) break;
                    ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR028, rem.GetLocation(),
                        "'" + Util.Short(rem, 45) + "' removes the old item before '" + Util.Short(add, 45) + "' (line " + Util.Line(add) +
                        ") adds its replacement, with no try/finally; if the add throws, the old item is gone and nothing restores it (PluginHub #187 lost GPS markers when AddGps threw). Add the new item first, or restore the old one in a catch"));
                    break;
                }
            }
        }

        private static bool IsBclCollection(ExpressionSyntax? recv, SemanticModel model, CancellationToken ct)
        {
            if (recv == null) return false;
            var t = model.GetTypeInfo(recv, ct).Type;
            return t != null && t.NsStarts("System.Collections");
        }

        private static string? ReceiverKey(InvocationExpressionSyntax inv, IMethodSymbol m, SemanticModel model, CancellationToken ct)
        {
            var recv = Util.Receiver(inv);
            if (recv == null) return m.IsStatic ? "type:" + m.ContainingType.FullName() : "this";
            var s = Util.SymbolOf(recv, model, ct);
            if (s is INamedTypeSymbol nt) return "type:" + nt.FullName();
            if (s != null && s is not IMethodSymbol) return s.Kind + ":" + s.ToDisplayString();
            return "text:" + Regex.Replace(recv.ToString(), @"\s+", "");
        }

        // ---------------- DR029 ----------------

        private static readonly string[] BuildSymbols = { "DEBUG", "LOCAL_BUILD", "TRACE" };
        private static readonly Regex NeutralLine = new Regex(@"^\s*($|//|/\*|\*|[{}]+;?\s*$|#|using\s+[\w.]+\s*;|\[)", RegexOptions.CultureInvariant);
        private static readonly Regex LoggingLine = new Regex(@"\b(Log\w*|Logger|_log\w*|Debug|Trace|Console|Debugger)\b\s*[.(]|\.(Info|Warn\w*|Error|Debug|Trace\w*|Fatal|WriteLine\w*)\s*\(", RegexOptions.CultureInvariant);

        private static void OnTreeDirectives(SyntaxTreeAnalysisContext ctx, HostContext host)
        {
            var root = ctx.Tree.GetRoot(ctx.CancellationToken);
            var defined = new HashSet<string>(ctx.Tree.Options.PreprocessorSymbolNames, StringComparer.Ordinal);
            var directives = new List<DirectiveTriviaSyntax>();
            for (var d = root.GetFirstDirective(); d != null; d = d.GetNextDirective())
                if (d is IfDirectiveTriviaSyntax || d is ElifDirectiveTriviaSyntax || d is ElseDirectiveTriviaSyntax || d is EndIfDirectiveTriviaSyntax)
                    directives.Add(d);
            if (directives.Count == 0) return;
            var text = ctx.Tree.GetText(ctx.CancellationToken);

            // pair each branch directive with the next directive at the same nesting level
            var stack = new Stack<(DirectiveTriviaSyntax start, string? symbol, bool negated)>();
            foreach (var d in directives)
            {
                switch (d)
                {
                    case IfDirectiveTriviaSyntax ifd:
                        stack.Push((ifd, BuildSymbol(ifd.Condition, out var neg), neg));
                        break;
                    case ElifDirectiveTriviaSyntax elif:
                        if (stack.Count == 0) break;
                        var prev = stack.Pop();
                        Branch(prev.start, d, prev.symbol, prev.negated, false);
                        var own = BuildSymbol(elif.Condition, out var neg2);
                        stack.Push(own != null ? (elif, own, neg2) : (elif, prev.symbol, !prev.negated));
                        break;
                    case ElseDirectiveTriviaSyntax:
                        if (stack.Count == 0) break;
                        var p2 = stack.Pop();
                        Branch(p2.start, d, p2.symbol, p2.negated, false);
                        stack.Push((d, p2.symbol, !p2.negated));
                        break;
                    case EndIfDirectiveTriviaSyntax:
                        if (stack.Count == 0) break;
                        var p3 = stack.Pop();
                        Branch(p3.start, d, p3.symbol, p3.negated, p3.start is ElseDirectiveTriviaSyntax);
                        break;
                }
            }

            void Branch(DirectiveTriviaSyntax start, DirectiveTriviaSyntax end, string? symbol, bool releaseOnly, bool isElse)
            {
                if (symbol == null) return;
                var startLine = text.Lines.GetLineFromPosition(start.Span.End).LineNumber + 1;
                var endLine = text.Lines.GetLineFromPosition(end.SpanStart).LineNumber - 1;
                if (endLine < startLine) return;
                var behaviour = false;
                for (var l = startLine; l <= endLine && !behaviour; l++)
                {
                    var line = text.Lines[l].ToString();
                    if (NeutralLine.IsMatch(line) || LoggingLine.IsMatch(line)) continue;
                    behaviour = true;
                }
                if (!behaviour) return;
                var span = TextSpan.FromBounds(text.Lines[startLine].Start, text.Lines[endLine].End);
                var where = "lines " + (startLine + 1) + "-" + (endLine + 1);
                var inThis = defined.Contains(symbol) != releaseOnly;
                var loaders = host.GameOrPlugin ? " The hub loaders compile without DEBUG, LOCAL_BUILD and TRACE." : "";
                var message = releaseOnly
                    ? where + " compile only without " + symbol + " (" + (isElse ? "the #else branch" : "#if !" + symbol) + "), so a build that defines " + symbol + " never compiles or tests this code" +
                      (inThis ? "" : ", and this compilation defines it") + "." + loaders
                    : where + " compile only with " + symbol + " defined" + (inThis ? "" : ", and this compilation does not define it, so this behaviour is not in this build") +
                      "; whatever a build without " + symbol + " relies on instead is untested." + loaders;
                ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR029, Location.Create(ctx.Tree, span),
                    message + " Check the two builds behave the same apart from diagnostics"));
            }
        }

        private static string? BuildSymbol(ExpressionSyntax? cond, out bool negated)
        {
            negated = false;
            if (cond == null) return null;
            foreach (var id in cond.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                var name = id.Identifier.Text;
                if (!BuildSymbols.Contains(name)) continue;
                negated = id.Parent is PrefixUnaryExpressionSyntax pu && pu.IsKind(SyntaxKind.LogicalNotExpression);
                return name;
            }
            return null;
        }

        // ---------------- DR030 ----------------

        private static void OnNullableAnnotations(SemanticModelAnalysisContext ctx, NullableContextOptions nullable, HostContext host)
        {
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            var root = model.SyntaxTree.GetRoot(ct);
            var directives = new List<(int pos, bool? annotations)>();
            for (var d = root.GetFirstDirective(); d != null; d = d.GetNextDirective())
            {
                if (d is not NullableDirectiveTriviaSyntax nd || !nd.IsActive) continue;
                var target = nd.TargetToken;
                if (target.IsKind(SyntaxKind.WarningsKeyword)) continue;
                bool? state = nd.SettingToken.Kind() switch
                {
                    SyntaxKind.EnableKeyword => true,
                    SyntaxKind.DisableKeyword => false,
                    _ => false // restore: back to this compilation's setting, which has annotations off
                };
                directives.Add((nd.SpanStart, state));
            }

            bool Annotated(int pos)
            {
                var on = false;
                foreach (var (p, s) in directives)
                {
                    if (p > pos) break;
                    on = s == true;
                }
                return on;
            }

            var questions = new List<SyntaxNode>();
            var bangs = new List<SyntaxNode>();
            foreach (var n in root.DescendantNodes())
            {
                if (n is NullableTypeSyntax nt && !Annotated(nt.SpanStart))
                {
                    var t = model.GetTypeInfo(nt.ElementType, ct).Type;
                    if (t != null && (t.IsReferenceType || (t is ITypeParameterSymbol tp && !tp.HasValueTypeConstraint && !tp.HasUnmanagedTypeConstraint)))
                        questions.Add(nt);
                }
                else if (n.IsKind(SyntaxKind.SuppressNullableWarningExpression) && !Annotated(n.SpanStart))
                    bangs.Add(n);
            }
            if (questions.Count == 0 && bangs.Count == 0) return;
            var all = questions.Concat(bangs).OrderBy(x => x.SpanStart).ToList();
            var lines = string.Join(", ", all.Select(Util.Line).Distinct().Take(12)) + (all.Count > 12 ? ", ..." : "");
            var parts = new List<string>();
            if (questions.Count > 0) parts.Add(questions.Count + " nullable reference annotation" + (questions.Count == 1 ? "" : "s") + " ('?')");
            if (bangs.Count > 0) parts.Add(bangs.Count + " null-forgiving operator" + (bangs.Count == 1 ? "" : "s") + " ('!')");
            var setting = "NullableContextOptions." + nullable;
            if (ctx.Options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.Nullable", out var msbuild) && !string.IsNullOrEmpty(msbuild))
                setting += ", <Nullable>" + msbuild + "</Nullable>";
            ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR030, all[0].GetLocation(), all.Skip(1).Select(x => x.GetLocation()),
                "this file has " + string.Join(" and ", parts) + " (lines " + lines + ") outside any #nullable enable region, and this compilation has nullable annotations off (" + setting +
                "): each '?' on a reference type is warning CS8632 and each '!' does nothing, so the file relies on a nullable setting it does not set" +
                (host.GameOrPlugin ? " (Pulsar and Magnetar compile plugins without the project's <Nullable>; PluginHub #190 logged 42 CS8632)" : "") +
                ". Add #nullable enable at the top of the file"));
        }

        // ---------------- DR031, DR032 ----------------

        private static void OnInvocation(SyntaxNodeAnalysisContext ctx, HostContext host)
        {
            var inv = (InvocationExpressionSyntax)ctx.Node;
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            var name = Util.InvokedName(inv);
            if (name is "GetField" or "GetProperty" or "GetMethod" or "GetNestedType" or "GetEvent" or "Field" or "Method" or "Property" or
                "DeclaredField" or "DeclaredMethod" or "DeclaredProperty")
                ReflectionByName(ctx, inv, name, model, ct, host);
            // MyEntities and IMyEntities exist only in the game's assemblies.
            if (host.Game && name is "GetEntityById" or "TryGetEntityById" or "GetEntityByName" or "TryGetEntityByName" or "EntityExists")
                EntityLookup(ctx, inv, model, ct);
        }

        private const int BfInstance = 4, BfStatic = 8, BfPublic = 16, BfNonPublic = 32;

        private static void ReflectionByName(SyntaxNodeAnalysisContext ctx, InvocationExpressionSyntax inv, string name, SemanticModel model, CancellationToken ct, HostContext host)
        {
            var m = Util.MethodOf(inv, model, ct);
            if (m?.ContainingType == null) return;
            var tn = m.ContainingType.FullName();
            var isType = tn == "System.Type" || m.ContainingType.InheritsFrom("System.Type");
            var isAccessTools = host.Harmony && tn == "HarmonyLib.AccessTools";
            if (!isType && !isAccessTools) return;
            if (isType && !(name is "GetField" or "GetProperty" or "GetMethod" or "GetNestedType" or "GetEvent")) return;

            string? literal = null;
            foreach (var a in inv.ArgumentList.Arguments)
            {
                var e = Util.Strip(a.Expression);
                if (e is InvocationExpressionSyntax nameOf && Util.InvokedName(nameOf) == "nameof") continue;
                if (model.GetTypeInfo(e, ct).Type?.SpecialType != SpecialType.System_String) continue;
                if (e is LiteralExpressionSyntax || (Util.SymbolOf(e, model, ct) is IFieldSymbol cf && cf.IsConst))
                {
                    literal = model.GetConstantValue(e, ct).Value as string;
                    break;
                }
            }
            if (literal == null) return;

            var problems = new List<string>();
            var use = UnguardedUse(inv, model, ct);
            if (use != null) problems.Add("the result is " + use + " without a null check; after " + (host.Game ? "a game update" : "a change to the reflected type") + " renames or removes '" + literal + "' this throws a NullReferenceException far from the cause. Check for null and log which member is missing");

            if (isType)
            {
                int? flags = null;
                for (var i = 0; i < m.Parameters.Length; i++)
                {
                    if (!m.Parameters[i].Type.Is("System.Reflection.BindingFlags")) continue;
                    var arg = inv.ArgumentList.Arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.Text == m.Parameters[i].Name)?.Expression
                              ?? (i < inv.ArgumentList.Arguments.Count ? inv.ArgumentList.Arguments[i].Expression : null);
                    var c = arg == null ? default : model.GetConstantValue(arg, ct);
                    if (c.HasValue && c.Value != null) flags = Convert.ToInt32(c.Value, System.Globalization.CultureInfo.InvariantCulture);
                }
                var explicitFlags = flags.HasValue;
                var f = flags ?? (BfPublic | BfInstance | BfStatic);
                if (explicitFlags && name != "GetNestedType" && (f & (BfInstance | BfStatic)) == 0)
                    problems.Add("BindingFlags has neither Instance nor Static, so the lookup never finds anything");
                if (explicitFlags && (f & (BfPublic | BfNonPublic)) == 0)
                    problems.Add("BindingFlags has neither Public nor NonPublic, so the lookup never finds anything");
                if ((literal.StartsWith("m_", StringComparison.Ordinal) || literal.StartsWith("_", StringComparison.Ordinal)) && (f & BfNonPublic) == 0)
                    problems.Add("'" + literal + "' looks private but the lookup " + (explicitFlags ? "uses BindingFlags without NonPublic" : "uses the default Public binding flags") + ", so it returns null");
            }
            if (problems.Count == 0) return;
            ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR031, inv.GetLocation(),
                Util.Short(inv, 60) + " looks up a member by name: " + string.Join("; ", problems)));
        }

        /// <summary>How the reflection result is used without a null check, or null when it is guarded or not used.</summary>
        private static string? UnguardedUse(InvocationExpressionSyntax inv, SemanticModel model, CancellationToken ct)
        {
            SyntaxNode cur = inv;
            while (cur.Parent is ParenthesizedExpressionSyntax || cur.Parent is CastExpressionSyntax) cur = cur.Parent;
            if (cur.Parent is PostfixUnaryExpressionSyntax bang && bang.IsKind(SyntaxKind.SuppressNullableWarningExpression)) return "followed by '!'";
            switch (cur.Parent)
            {
                case MemberAccessExpressionSyntax ma when ma.Expression == cur:
                    return "dereferenced directly (." + ma.Name.Identifier.Text + ")";
                case ConditionalAccessExpressionSyntax:
                    return null;
                case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.CoalesceExpression):
                    return null;
                case ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax call }:
                    {
                        var cn = Util.InvokedName(call);
                        return cn.Contains("Null") || cn.Contains("Assert") ? null : "passed straight to " + cn + "()";
                    }
                case ArgumentSyntax { Parent.Parent: BaseObjectCreationExpressionSyntax }:
                    return "passed straight to a constructor";
                case EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax v }:
                    {
                        var declared = model.GetDeclaredSymbol(v, ct);
                        if (declared == null) return null;
                        var scope = declared is ILocalSymbol ? Util.EnclosingFunction(v) : v.FirstAncestorOrSelf<TypeDeclarationSyntax>();
                        return scope == null ? null : VariableUse(declared, scope, v, model, ct);
                    }
                case AssignmentExpressionSyntax a when a.Right == cur:
                    {
                        var target = Util.SymbolOf(a.Left, model, ct);
                        if (target == null) return null;
                        var scope = target is ILocalSymbol ? Util.EnclosingFunction(a) : a.FirstAncestorOrSelf<TypeDeclarationSyntax>();
                        return scope == null ? null : VariableUse(target, scope, a, model, ct);
                    }
            }
            return null;
        }

        private static string? VariableUse(ISymbol variable, SyntaxNode scope, SyntaxNode decl, SemanticModel model, CancellationToken ct)
        {
            string? use = null;
            foreach (var id in scope.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (id.Identifier.Text != variable.Name || decl.Span.Contains(id.Span)) continue;
                if (!Util.Same(model.GetSymbolInfo(id, ct).Symbol, variable)) continue;
                SyntaxNode cur = id;
                if (cur.Parent is MemberAccessExpressionSyntax self && self.Name == cur) cur = self; // this.field
                switch (cur.Parent)
                {
                    case BinaryExpressionSyntax b when b.Kind() is SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression or SyntaxKind.CoalesceExpression:
                    case IsPatternExpressionSyntax:
                    case ConditionalAccessExpressionSyntax:
                        return null; // guarded somewhere
                    case ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax call } when Util.InvokedName(call).Contains("Null") || Util.InvokedName(call).Contains("Assert"):
                        return null;
                    case PostfixUnaryExpressionSyntax bang when bang.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                        use ??= "used with '!'";
                        break;
                    case MemberAccessExpressionSyntax ma when ma.Expression == cur:
                        use ??= "stored in '" + variable.Name + "' and dereferenced (line " + Util.Line(ma) + ")";
                        break;
                    case ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax call2 }:
                        use ??= "stored in '" + variable.Name + "' and passed to " + Util.InvokedName(call2) + "() (line " + Util.Line(call2) + ")";
                        break;
                }
            }
            return use;
        }

        private static void EntityLookup(SyntaxNodeAnalysisContext ctx, InvocationExpressionSyntax inv, SemanticModel model, CancellationToken ct)
        {
            var m = Util.MethodOf(inv, model, ct);
            if (m?.ContainingType == null) return;
            var tname = m.ContainingType.Name;
            if (!(tname == "MyEntities" || tname == "IMyEntities") || !(m.ContainingType.NsStarts("Sandbox") || m.ContainingType.NsStarts("VRage"))) return;

            string? where = null;
            for (SyntaxNode? p = inv.Parent; p != null; p = p.Parent)
            {
                if (p is AnonymousFunctionExpressionSyntax lambda)
                {
                    if (lambda.Parent is ArgumentSyntax { Parent.Parent: SyntaxNode call })
                    {
                        var cm = Util.MethodOf(call, model, ct);
                        var ctn = cm?.ContainingType.FullName() ?? "";
                        var cname = cm?.Name ?? "";
                        if (IsGameThreadMarshal(cname, ctn)) return;
                        if (ctn is "System.Threading.Tasks.Task" or "System.Threading.Tasks.TaskFactory" or "System.Threading.ThreadPool" or
                                "System.Threading.Thread" or "System.Threading.Timer" or "System.Threading.Tasks.Parallel" or "System.Timers.Timer")
                        {
                            where = "inside a " + (cm?.MethodKind == MethodKind.Constructor ? "new " + cm.ContainingType.Name : cm?.ContainingType.Name + "." + cname) + " callback";
                            break;
                        }
                    }
                    if (lambda.Parent is AssignmentExpressionSyntax ea && ea.IsKind(SyntaxKind.AddAssignmentExpression) &&
                        Util.SymbolOf(ea.Left, model, ct) is IEventSymbol ev && ev.ContainingType.Is("System.Timers.Timer"))
                    {
                        where = "inside a System.Timers.Timer.Elapsed handler";
                        break;
                    }
                    if (Util.IsAsync(lambda) && AwaitBefore(lambda, inv))
                    {
                        where = "after an await with ConfigureAwait(false) in an async lambda";
                        break;
                    }
                    continue;
                }
                if (p is LocalFunctionStatementSyntax || p is BaseMethodDeclarationSyntax)
                {
                    if (Util.IsAsync(p) && AwaitBefore(p, inv)) where = "after an await with ConfigureAwait(false)";
                    break;
                }
            }
            if (where == null) return;
            ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR032, inv.GetLocation(),
                m.ContainingType.Name + "." + m.Name + " runs " + where + ", off the game thread; MyEntities.Load can leave entities in a [ThreadStatic] list, so lookups from other threads return null or race the game's own changes. Marshal it to the game thread (MyAPIGateway.Utilities.InvokeOnGameThread / the host's game-thread invoke)"));
        }

        private static bool IsGameThreadMarshal(string name, string type) =>
            name.Contains("GameThread") || name == "Invoke" && (type.Contains("MySandboxGame") || type.Contains("Torch")) || name.StartsWith("InvokeOn", StringComparison.Ordinal) ||
            type.Contains("GameThread");

        /// <summary>An earlier await in the function whose operand is ConfigureAwait(false): the code after it runs on a thread-pool thread.</summary>
        private static bool AwaitBefore(SyntaxNode function, SyntaxNode at) =>
            Util.FunctionNodes(function).OfType<AwaitExpressionSyntax>().Any(a => a.SpanStart < at.SpanStart && !a.Span.Contains(at.Span) &&
                Util.Strip(a.Expression) is InvocationExpressionSyntax cfg && Util.InvokedName(cfg) == "ConfigureAwait" &&
                cfg.ArgumentList.Arguments.Count == 1 && cfg.ArgumentList.Arguments[0].Expression.IsKind(SyntaxKind.FalseLiteralExpression));
    }
}
