using System;
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
    /// <summary>DR021-DR022: paths and amounts that come from callers.</summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class InputsAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Rules.DR021, Rules.DR022);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(OnInvocation, SyntaxKind.InvocationExpression);
            context.RegisterSyntaxNodeAction(OnCreation, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
            context.RegisterSyntaxNodeAction(OnArithmetic, SyntaxKind.AddAssignmentExpression, SyntaxKind.SubtractAssignmentExpression,
                SyntaxKind.MultiplyAssignmentExpression, SyntaxKind.SimpleAssignmentExpression);
        }

        // ---------------- DR021 ----------------

        private static bool IsStringish(ITypeSymbol? t)
        {
            if (t == null) return false;
            if (t.SpecialType == SpecialType.System_String) return true;
            if (t is IArrayTypeSymbol at) return at.ElementType.SpecialType == SpecialType.System_String;
            return t is INamedTypeSymbol nt && nt.TypeArguments.Length == 1 && nt.TypeArguments[0].SpecialType == SpecialType.System_String &&
                   (nt.Name.Contains("List") || nt.Name.Contains("Collection") || nt.Name.Contains("Enumerable"));
        }

        /// <summary>A string (or string[]/list) parameter of an enclosing function that the expression is built from.</summary>
        private static IParameterSymbol? DerivedParam(ExpressionSyntax e, SemanticModel model, CancellationToken ct, int depth)
        {
            foreach (var id in e.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                var s = model.GetSymbolInfo(id, ct).Symbol;
                if (s is IParameterSymbol p && IsStringish(p.Type) && !IsLinqLambdaParameter(p, model, ct)) return p;
                if (s is ILocalSymbol local && depth > 0)
                    foreach (var r in local.DeclaringSyntaxReferences)
                        if (r.GetSyntax(ct) is VariableDeclaratorSyntax v && v.Initializer != null && DerivedParam(v.Initializer.Value, model, ct, depth - 1) is IParameterSymbol hit)
                            return hit;
            }
            return null;
        }

        /// <summary>x in files.Select(x => ...) or list.ForEach(x => ...): an element of local data, not caller input.</summary>
        private static bool IsLinqLambdaParameter(IParameterSymbol p, SemanticModel model, CancellationToken ct)
        {
            if (p.ContainingSymbol is not IMethodSymbol { MethodKind: MethodKind.AnonymousFunction }) return false;
            var lambda = p.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct)?.FirstAncestorOrSelf<AnonymousFunctionExpressionSyntax>();
            if (lambda?.Parent is not ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax call }) return false;
            var m = Util.MethodOf(call, model, ct);
            return m != null && (m.ContainingType.NsStarts("System.Linq") || m.Name == "ForEach");
        }

        /// <summary>The parameter reaches the path through concatenation, interpolation or string.Format/Concat (not as the whole path).</summary>
        private static IParameterSymbol? ComposedFromParam(ExpressionSyntax e, SemanticModel model, CancellationToken ct, int depth)
        {
            e = Util.Strip(e);
            switch (e)
            {
                case InterpolatedStringExpressionSyntax:
                case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AddExpression):
                    return DerivedParam(e, model, ct, 2);
                case InvocationExpressionSyntax inv when Util.InvokedName(inv) is "Concat" or "Format" &&
                                                         Util.MethodOf(inv, model, ct)?.ContainingType.SpecialType == SpecialType.System_String:
                    return DerivedParam(e, model, ct, 2);
            }
            if (depth > 0 && Util.SymbolOf(e, model, ct) is ILocalSymbol local)
                foreach (var r in local.DeclaringSyntaxReferences)
                    if (r.GetSyntax(ct) is VariableDeclaratorSyntax v && v.Initializer != null && ComposedFromParam(v.Initializer.Value, model, ct, depth - 1) is IParameterSymbol hit)
                        return hit;
            return null;
        }

        private static bool Sanitised(SyntaxNode at, SemanticModel model, CancellationToken ct)
        {
            var member = Util.EnclosingMember(at);
            if (member == null) return false;
            var fullPath = false;
            var startsWith = false;
            foreach (var inv in member.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = Util.InvokedName(inv);
                if (name == "GetFileName" || name == "GetFileNameWithoutExtension") return true;
                if (name == "GetFullPath") fullPath = true;
                if (name == "StartsWith" || name == "Equals" || name == "IsBaseOf") startsWith = true;
            }
            return fullPath && startsWith;
        }

        private static void OnInvocation(SyntaxNodeAnalysisContext ctx)
        {
            var inv = (InvocationExpressionSyntax)ctx.Node;
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            var m = Util.MethodOf(inv, model, ct);
            if (m?.ContainingType == null) return;
            var tn = m.ContainingType.FullName();
            if (tn == "System.IO.Path" && (m.Name == "Combine" || m.Name == "Join"))
            {
                foreach (var a in inv.ArgumentList.Arguments)
                {
                    var p = DerivedParam(a.Expression, model, ct, 2);
                    if (p == null) continue;
                    if (Sanitised(inv, model, ct)) return;
                    Report(ctx, inv, "Path." + m.Name, p);
                    return;
                }
                return;
            }
            if ((tn == "System.IO.File" || tn == "System.IO.Directory") && inv.ArgumentList.Arguments.Count > 0 &&
                m.Parameters.Length > 0 && m.Parameters[0].Type.SpecialType == SpecialType.System_String)
            {
                var p = ComposedFromParam(inv.ArgumentList.Arguments[0].Expression, model, ct, 2);
                if (p != null && !Sanitised(inv, model, ct)) Report(ctx, inv, m.ContainingType.Name + "." + m.Name, p);
            }
        }

        private static void OnCreation(SyntaxNodeAnalysisContext ctx)
        {
            var oc = (BaseObjectCreationExpressionSyntax)ctx.Node;
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            var t = model.GetTypeInfo(oc, ct).Type;
            if (!t.IsAny("System.IO.FileInfo", "System.IO.DirectoryInfo", "System.IO.FileStream", "System.IO.StreamReader", "System.IO.StreamWriter")) return;
            var first = oc.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
            if (first == null || model.GetTypeInfo(first, ct).Type?.SpecialType != SpecialType.System_String) return;
            var p = ComposedFromParam(first, model, ct, 2);
            if (p != null && !Sanitised(oc, model, ct)) Report(ctx, oc, "new " + t?.Name, p);
        }

        private static void Report(SyntaxNodeAnalysisContext ctx, SyntaxNode node, string api, IParameterSymbol p) =>
            ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR021, node.GetLocation(),
                api + " builds a path from parameter '" + p.Name + "' with no Path.GetFullPath + StartsWith(root) check and no Path.GetFileName; a value such as ..\\..\\x or an absolute path reaches outside the intended folder (SensEssentials export/import once accepted names pointing outside ExportedGrids). Check where '" + p.Name + "' comes from"));

        // ---------------- DR022 ----------------

        private static readonly Regex MoneyName = new Regex("balance|credit|money|amount|count|total|score", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex AmountParam = new Regex("amount|count|value", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static bool IsCountType(ITypeSymbol? t) => t != null && t.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64 or
            SpecialType.System_UInt32 or SpecialType.System_UInt64 or SpecialType.System_Decimal;

        private static void OnArithmetic(SyntaxNodeAnalysisContext ctx)
        {
            var a = (AssignmentExpressionSyntax)ctx.Node;
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            if (model.Compilation.Options.CheckOverflow) return;
            var target = Util.SymbolOf(a.Left, model, ct);
            if (target is not (IFieldSymbol or IPropertySymbol) || !MoneyName.IsMatch(target.Name) || !IsCountType(Util.TypeOfMember(target))) return;

            ExpressionSyntax? operand = null;
            var op = "";
            if (a.IsKind(SyntaxKind.SimpleAssignmentExpression))
            {
                if (Util.Strip(a.Right) is not BinaryExpressionSyntax b || !(b.IsKind(SyntaxKind.AddExpression) || b.IsKind(SyntaxKind.SubtractExpression) || b.IsKind(SyntaxKind.MultiplyExpression))) return;
                if (Util.Same(Util.SymbolOf(b.Left, model, ct), target)) operand = b.Right;
                else if (!b.IsKind(SyntaxKind.SubtractExpression) && Util.Same(Util.SymbolOf(b.Right, model, ct), target)) operand = b.Left;
                op = b.OperatorToken.Text;
            }
            else
            {
                operand = a.Right;
                op = a.OperatorToken.Text;
            }
            if (operand == null) return;
            if (a.Ancestors().Any(x => x.IsKind(SyntaxKind.CheckedExpression) || x.IsKind(SyntaxKind.CheckedStatement))) return;
            var p = NumericParam(operand, model, ct, 2, out var via);
            if (p == null) return;
            if (operand.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(i => Util.InvokedName(i) is "Min" or "Max" or "Clamp")) return;

            var member = Util.EnclosingMember(a);
            if (member == null) return;
            bool compared = false, zeroChecked = false;
            foreach (var n in member.DescendantNodes())
            {
                switch (n)
                {
                    case BinaryExpressionSyntax cmp when cmp.Kind() is SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression or
                        SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression or SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression:
                        if (Mentions(cmp, p, via, model, ct))
                        {
                            compared = true;
                            if (Util.IsZeroConstant(cmp.Left, model, ct) || Util.IsZeroConstant(cmp.Right, model, ct)) zeroChecked = true;
                        }
                        break;
                    case IsPatternExpressionSyntax ip when Mentions(ip.Expression, p, via, model, ct):
                        compared = true;
                        if (ip.Pattern.ToString().Contains("0")) zeroChecked = true;
                        break;
                    case InvocationExpressionSyntax inv when Util.InvokedName(inv) is "Min" or "Max" or "Clamp" && Mentions(inv, p, via, model, ct):
                        return;
                }
            }
            var signed = p.Type.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Decimal or SpecialType.System_Double or SpecialType.System_Single;
            var negative = signed && AmountParam.IsMatch(p.Name) && !zeroChecked;
            if (compared && !negative) return;

            var text = compared
                ? "'" + target.Name + " " + op + "' takes parameter '" + p.Name + "', which this method never checks against zero; a negative " + p.Name + " turns a payment into a credit (or a removal into an add)"
                : "'" + target.Name + " " + op + "' takes parameter '" + p.Name + "' with no checked context, no Math.Min/Max clamp and no comparison of '" + p.Name + "' in this method; a huge or negative value overflows or flips the balance" +
                  (negative ? ", and a negative " + p.Name + " is accepted" : "");
            ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR022, a.GetLocation(), text + ". Validate the range before applying it"));
        }

        private static IParameterSymbol? NumericParam(ExpressionSyntax e, SemanticModel model, CancellationToken ct, int depth, out ISymbol? via)
        {
            via = null;
            foreach (var id in e.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                var s = model.GetSymbolInfo(id, ct).Symbol;
                if (s is IParameterSymbol p && p.Type.IsValueType && p.Type.SpecialType != SpecialType.System_Boolean) return p;
                if (s is ILocalSymbol local && depth > 0)
                    foreach (var r in local.DeclaringSyntaxReferences)
                        if (r.GetSyntax(ct) is VariableDeclaratorSyntax v && v.Initializer != null && NumericParam(v.Initializer.Value, model, ct, depth - 1, out _) is IParameterSymbol hit)
                        {
                            via = local;
                            return hit;
                        }
            }
            return null;
        }

        private static bool Mentions(SyntaxNode n, IParameterSymbol p, ISymbol? via, SemanticModel model, CancellationToken ct)
        {
            foreach (var id in n.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                if (id.Identifier.Text != p.Name && (via == null || id.Identifier.Text != via.Name)) continue;
                var s = model.GetSymbolInfo(id, ct).Symbol;
                if (Util.Same(s, p) || (via != null && Util.Same(s, via))) return true;
            }
            return false;
        }
    }
}
