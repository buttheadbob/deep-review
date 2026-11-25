using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace DeepRules
{
    /// <summary>
    /// DR038: Process.Start may only open a fixed local web page: a URL whose scheme and loopback host the code shows as
    /// constant text, with no arguments and the default verb. Replaces the RS0030 bans on Process.Start(string) and
    /// Process.Start(ProcessStartInfo); the other overloads keep their bans in BannedSymbols.txt.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ProcessAnalyzer : DiagnosticAnalyzer
    {
        /// <summary>The only hosts Process.Start may open: this machine.</summary>
        public static readonly ImmutableArray<string> LocalHosts = ImmutableArray.Create("127.0.0.1", "localhost", "[::1]");

        private const string Fix = "Only a fixed local page may be opened: a URL whose scheme and loopback host are constant text, for example " +
                                   "Process.Start(new ProcessStartInfo($\"http://127.0.0.1:{port}/\") { UseShellExecute = true }) (http or https; host 127.0.0.1, localhost or [::1]; " +
                                   "UseShellExecute = true, which .NET 10 does not default to), with no Arguments and the default verb";

        private enum Overload
        {
            Banned,
            Text,
            StartInfo,
            Other
        }

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Rules.DR038);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(start =>
            {
                if (start.Compilation.GetTypeByMetadataName("System.Diagnostics.Process") == null) return;
                start.RegisterSyntaxNodeAction(OnInvocation, SyntaxKind.InvocationExpression);
                start.RegisterOperationAction(OnMethodReference, OperationKind.MethodReference);
            });
        }

        private static Overload? KindOf(IMethodSymbol? m)
        {
            if (m == null || m.Name != "Start" || m.ContainingType.FullName() != "System.Diagnostics.Process") return null;
            // Process.Start() on an instance, Start(string, string) and Start(string, IEnumerable<string>) stay banned (RS0030).
            if (!m.IsStatic) return Overload.Banned;
            var ps = m.Parameters;
            if (ps.Length == 1 && ps[0].Type.SpecialType == SpecialType.System_String) return Overload.Text;
            if (ps.Length == 1 && ps[0].Type.FullName() == "System.Diagnostics.ProcessStartInfo") return Overload.StartInfo;
            if (ps.Length == 2 && ps[0].Type.SpecialType == SpecialType.System_String &&
                (ps[1].Type.SpecialType == SpecialType.System_String ||
                 ps[1].Type.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T))
                return Overload.Banned;
            return Overload.Other;
        }

        private static void OnMethodReference(OperationAnalysisContext ctx)
        {
            var reference = (IMethodReferenceOperation)ctx.Operation;
            var kind = KindOf(reference.Method);
            if (kind == null || kind == Overload.Banned) return;
            ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR038, reference.Syntax.GetLocation(),
                "Process.Start used as a method group: whatever is later passed to it is started, and the code here does not show what. " + Fix + ", passed to Process.Start directly"));
        }

        private static void OnInvocation(SyntaxNodeAnalysisContext ctx)
        {
            var call = (InvocationExpressionSyntax)ctx.Node;
            var m = Util.MethodOf(call, ctx.SemanticModel, ctx.CancellationToken);
            var kind = KindOf(m);
            if (kind == null || kind == Overload.Banned) return;
            if (kind == Overload.Other || call.ArgumentList.Arguments.Count != 1)
            {
                ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR038, call.GetLocation(),
                    "Process.Start(" + string.Join(", ", m!.Parameters.Select(p => p.Type.Name)) + ") starts a program, possibly as another user: reviewed code may not start programs. " + Fix));
                return;
            }

            var argument = call.ArgumentList.Arguments[0].Expression;
            if (kind == Overload.Text)
                CheckTarget(ctx, argument, "Process.Start");
            else
                CheckStartInfo(ctx, call, argument);
        }

        /// <summary>The target must be a URL with a loopback host shown in the code (the forms DR037 accepts for request URIs).</summary>
        private static void CheckTarget(SyntaxNodeAnalysisContext ctx, ExpressionSyntax target, string where)
        {
            var host = NetworkAnalyzer.HostOf(target, ctx.SemanticModel, ctx.CancellationToken);
            if (host == null)
            {
                ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR038, target.GetLocation(),
                    "the file name given to " + where + " is not a URL whose scheme and host are constant text in the code (it is a program or path, or comes from a parameter, field, config value or method), so it can start anything. " + Fix));
            }
            else if (!LocalHosts.Contains(host))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR038, target.GetLocation(),
                    where + " opens '" + host + "', which is not this machine. " + Fix + "; a link to another site is shown to the user as text, not opened"));
            }
        }

        private static void CheckStartInfo(SyntaxNodeAnalysisContext ctx, InvocationExpressionSyntax call, ExpressionSyntax argument)
        {
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            var e = NetworkAnalyzer.Strip(argument);
            var later = new List<(string Name, AssignmentExpressionSyntax Assignment)>();
            BaseObjectCreationExpressionSyntax? creation = null;

            if (e is BaseObjectCreationExpressionSyntax created)
            {
                creation = created;
            }
            else if (e is IdentifierNameSyntax id && model.GetSymbolInfo(id, ct).Symbol is ILocalSymbol local)
            {
                // A local set once, where it is declared, to a new ProcessStartInfo; afterwards only its properties are set
                // (checked like the initializer) or read, and it is only passed to Process.Start.
                var declarator = local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct) as VariableDeclaratorSyntax;
                var body = declarator?.FirstAncestorOrSelf<BlockSyntax>();
                if (declarator?.Initializer != null && body != null && NetworkAnalyzer.Strip(declarator.Initializer.Value) is BaseObjectCreationExpressionSyntax init)
                {
                    creation = init;
                    foreach (var use in body.DescendantNodes().OfType<IdentifierNameSyntax>())
                    {
                        if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(use, ct).Symbol, local)) continue;
                        if (IsStartArgument(use, model, ct)) continue;
                        if (use.Parent is MemberAccessExpressionSyntax ma && ma.Expression == use && ma.IsKind(SyntaxKind.SimpleMemberAccessExpression))
                        {
                            var name = ma.Name.Identifier.ValueText;
                            if (ma.Parent is AssignmentExpressionSyntax set && set.Left == ma)
                            {
                                later.Add((name, set));
                                continue;
                            }
                            if (name != "ArgumentList" && ma.Parent is not ArgumentSyntax { Parent: TupleExpressionSyntax })
                                continue; // a plain read
                        }
                        ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR038, use.GetLocation(),
                            "the ProcessStartInfo '" + local.Name + "' given to Process.Start is changed or handed on here, where the check cannot follow it (its ArgumentList, a deconstruction, a reassignment, a ref or another method). " + Fix + ", built in one new ProcessStartInfo whose properties are set directly"));
                    }
                }
            }

            if (creation == null || model.GetTypeInfo(creation, ct).Type.FullName() != "System.Diagnostics.ProcessStartInfo")
            {
                ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR038, argument.GetLocation(),
                    "the ProcessStartInfo given to Process.Start does not come from a new ProcessStartInfo in this method (a parameter, field, method result or a local set more than once), so the code does not show what it starts. " + Fix));
                return;
            }

            var fileNames = new List<ExpressionSyntax>();
            var args = creation.ArgumentList?.Arguments ?? default;
            if (args.Count >= 1)
                fileNames.Add(args[0].Expression);
            if (args.Count >= 2)
            {
                ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR038, args[1].GetLocation(),
                    "the ProcessStartInfo given to Process.Start has arguments: a page needs none, and arguments turn the start into a program run. " + Fix));
            }

            var sets = new List<(string Name, AssignmentExpressionSyntax Assignment)>();
            if (creation.Initializer != null)
            {
                foreach (var entry in creation.Initializer.Expressions.OfType<AssignmentExpressionSyntax>())
                    if (entry.Left is IdentifierNameSyntax member)
                        sets.Add((member.Identifier.ValueText, entry));
            }
            sets.AddRange(later);

            foreach (var (name, set) in sets)
            {
                var simple = set.IsKind(SyntaxKind.SimpleAssignmentExpression);
                switch (name)
                {
                    case "FileName" when simple:
                        fileNames.Add(set.Right);
                        break;
                    case "FileName":
                        ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR038, set.GetLocation(),
                            "ProcessStartInfo.FileName is changed by a compound assignment, so the code does not show what is started. " + Fix));
                        break;
                    case "Arguments":
                    case "ArgumentList":
                        ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR038, set.GetLocation(),
                            "ProcessStartInfo." + name + " is set: a page needs no arguments, and arguments turn the start into a program run. " + Fix));
                        break;
                    case "Verb":
                        var verb = model.GetConstantValue(set.Right, ct);
                        if (!simple || !verb.HasValue || verb.Value is not string v || !string.Equals(v, "open", StringComparison.OrdinalIgnoreCase))
                        {
                            ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR038, set.GetLocation(),
                                "ProcessStartInfo.Verb is set to something other than the constant \"open\" (\"runas\" elevates, \"print\" and \"edit\" start other programs). " + Fix));
                        }
                        break;
                }
            }

            if (fileNames.Count == 0)
            {
                ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR038, creation.GetLocation(),
                    "the ProcessStartInfo given to Process.Start sets no FileName the code shows. " + Fix));
            }
            foreach (var fileName in fileNames)
                CheckTarget(ctx, fileName, "Process.Start");
        }

        /// <summary>A bare reference that is the only argument of a Process.Start(ProcessStartInfo) call.</summary>
        private static bool IsStartArgument(IdentifierNameSyntax use, SemanticModel model, System.Threading.CancellationToken ct) =>
            use.Parent is ArgumentSyntax { RefKindKeyword.RawKind: (int)SyntaxKind.None, Parent: ArgumentListSyntax { Arguments.Count: 1, Parent: InvocationExpressionSyntax inv } } &&
            KindOf(Util.MethodOf(inv, model, ct)) == Overload.StartInfo;
    }
}
