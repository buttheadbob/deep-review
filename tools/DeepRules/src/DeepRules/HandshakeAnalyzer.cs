using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DeepRules
{
    /// <summary>
    /// DR039: SHA-1 may only compute the WebSocket handshake answer (see <see cref="Handshake"/>). Replaces the RS0030 ban on
    /// System.Security.Cryptography.SHA1, which also covered every type derived from it; so every mention of SHA1 or a derived
    /// type, and every call of one of its static methods, is reported unless it is the accepted handshake call.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class HandshakeAnalyzer : DiagnosticAnalyzer
    {
        private const string Fix = "SHA-1 is a weak hash: fine only for non-security checksums, use SHA256 for anything security related. " +
                                   "The one allowed use is the WebSocket handshake answer of RFC 6455 section 4.2.2, written as " +
                                   "Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(key + \"" + Handshake.Guid + "\"))) (or SHA1.Create().ComputeHash with the same bytes), " +
                                   "where key is the request's Sec-WebSocket-Key header value and the Base64 text is only the Sec-WebSocket-Accept value of the response";

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Rules.DR039);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(start =>
            {
                if (start.Compilation.GetTypeByMetadataName("System.Security.Cryptography.SHA1") == null) return;
                start.RegisterSyntaxNodeAction(OnInvocation, SyntaxKind.InvocationExpression);
                start.RegisterSyntaxNodeAction(OnName, SyntaxKind.IdentifierName);
                start.RegisterSyntaxNodeAction(OnImplicitCreation, SyntaxKind.ImplicitObjectCreationExpression);
            });
        }

        private static void OnInvocation(SyntaxNodeAnalysisContext ctx)
        {
            var call = (InvocationExpressionSyntax)ctx.Node;
            var method = Util.MethodOf(call, ctx.SemanticModel, ctx.CancellationToken);
            if (method == null || !method.IsStatic || !Handshake.IsSha1(method.ContainingType)) return;

            if (Handshake.SiteOf(call, ctx.SemanticModel, ctx.CancellationToken) == Handshake.Site.None)
            {
                ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR039, call.GetLocation(),
                    "SHA1." + method.Name + " is not one of the two calls the handshake allows (SHA1.Create() and SHA1.HashData(byte[])). " + Fix));
                return;
            }
            var reason = Handshake.Evaluate(call, ctx.SemanticModel, ctx.CancellationToken);
            if (reason != null)
                ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR039, call.GetLocation(),
                    "this SHA-1 is not the WebSocket handshake answer: " + reason + ". " + Fix));
        }

        /// <summary>A mention of SHA1 or a derived type: a variable type, new SHA1Managed(), typeof, a generic argument, a method group.</summary>
        private static void OnName(SyntaxNodeAnalysisContext ctx)
        {
            var name = (IdentifierNameSyntax)ctx.Node;
            if (name.IsVar) return; // 'var sha = SHA1.Create()': the call itself is judged
            if (ctx.SemanticModel.GetSymbolInfo(name, ctx.CancellationToken).Symbol is not INamedTypeSymbol type || !Handshake.IsSha1(type)) return;
            if (IsQualifierOfSha1Call(name, ctx) || IsTypeOfAcceptedLocal(name, ctx)) return;
            ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR039, name.GetLocation(),
                type.Name + " (SHA-1) is used outside the one allowed WebSocket handshake call. " + Fix));
        }

        private static void OnImplicitCreation(SyntaxNodeAnalysisContext ctx)
        {
            var creation = (ImplicitObjectCreationExpressionSyntax)ctx.Node;
            if (ctx.SemanticModel.GetTypeInfo(creation, ctx.CancellationToken).Type is { } type && Handshake.IsSha1(type))
                ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR039, creation.GetLocation(), type.Name + " (SHA-1) is created here, outside the one allowed WebSocket handshake call. " + Fix));
        }

        /// <summary>The "SHA1" in SHA1.Create(): the call itself is judged (and reported once) by OnInvocation.</summary>
        private static bool IsQualifierOfSha1Call(IdentifierNameSyntax name, SyntaxNodeAnalysisContext ctx)
        {
            foreach (var call in name.Ancestors().OfType<InvocationExpressionSyntax>())
            {
                if (!call.Expression.Span.Contains(name.Span)) continue;
                var method = Util.MethodOf(call, ctx.SemanticModel, ctx.CancellationToken);
                return method != null && method.IsStatic && Handshake.IsSha1(method.ContainingType);
            }
            return false;
        }

        /// <summary>"SHA1 sha = SHA1.Create();" as the single declaration of an accepted handshake call.</summary>
        private static bool IsTypeOfAcceptedLocal(IdentifierNameSyntax name, SyntaxNodeAnalysisContext ctx)
        {
            SyntaxNode type = name;
            while (type.Parent is QualifiedNameSyntax q && q.Right == type) type = q;
            return type.Parent is VariableDeclarationSyntax { Variables.Count: 1 } declaration && declaration.Type == type &&
                   declaration.Variables[0].Initializer?.Value is { } value && Util.Strip(value) is InvocationExpressionSyntax call &&
                   Handshake.SiteOf(call, ctx.SemanticModel, ctx.CancellationToken) == Handshake.Site.Create &&
                   Handshake.Evaluate(call, ctx.SemanticModel, ctx.CancellationToken) == null;
        }
    }

    /// <summary>
    /// Drops CA5350, S4790 and SCS0006 on the one SHA-1 call DR039 accepts (the WebSocket handshake answer), and nowhere else:
    /// the diagnostic must sit exactly on that call or on its "SHA1.Create"/"SHA1.HashData" part, and the call must pass the
    /// same check DR039 uses. The three rules stay on, unchanged, everywhere else. Dropped results are listed by the gate
    /// as suppressed diagnostics, never hidden.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class HandshakeSuppressor : DiagnosticSuppressor
    {
        private const string Why = "The WebSocket handshake answer is Base64(SHA1(Sec-WebSocket-Key + the RFC 6455 GUID)); browsers reject any other hash " +
                                   "(the user, 2026-11-21: \"SHA handshakes are an exception\"). Allowed only where DR039 accepts the call.";

        private static readonly SuppressionDescriptor CA5350 = new SuppressionDescriptor("DR039A", "CA5350", Why);
        private static readonly SuppressionDescriptor S4790 = new SuppressionDescriptor("DR039B", "S4790", Why);
        private static readonly SuppressionDescriptor SCS0006 = new SuppressionDescriptor("DR039C", "SCS0006", Why);

        public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions { get; } = ImmutableArray.Create(CA5350, S4790, SCS0006);

        public override void ReportSuppressions(SuppressionAnalysisContext context)
        {
            foreach (var diagnostic in context.ReportedDiagnostics)
            {
                var descriptor = diagnostic.Id switch
                {
                    "CA5350" => CA5350,
                    "S4790" => S4790,
                    "SCS0006" => SCS0006,
                    _ => null
                };
                if (descriptor == null || diagnostic.Location.SourceTree is not { } tree) continue;

                var span = diagnostic.Location.SourceSpan;
                var model = context.GetSemanticModel(tree);
                var root = tree.GetRoot(context.CancellationToken);
                var token = root.FindToken(span.Start);
                foreach (var call in (token.Parent?.AncestorsAndSelf() ?? Enumerable.Empty<SyntaxNode>()).OfType<InvocationExpressionSyntax>())
                {
                    if (call.Span != span && !call.Expression.Span.Contains(span)) continue;
                    if (Handshake.SiteOf(call, model, context.CancellationToken) == Handshake.Site.None) continue;
                    if (Handshake.Evaluate(call, model, context.CancellationToken) != null) continue;
                    context.ReportSuppression(Suppression.Create(descriptor, diagnostic));
                    break;
                }
            }
        }
    }
}
