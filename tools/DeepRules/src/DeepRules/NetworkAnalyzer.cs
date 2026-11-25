using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DeepRules
{
    /// <summary>
    /// DR037: outgoing HTTP must go only to hosts on the allowlist, and the code must show which host. One shared
    /// HttpClient per process is the only way to create one.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class NetworkAnalyzer : DiagnosticAnalyzer
    {
        /// <summary>The allowlist key in the analyzer config: host names separated by commas.</summary>
        public const string AllowedHostsKey = "deep_review.http_allowed_hosts";

        /// <summary>Allowed when the config gives no list: Discord's API hosts, for webhooks.</summary>
        public static readonly ImmutableArray<string> DefaultAllowedHosts = ImmutableArray.Create(
            "discord.com", "ptb.discord.com", "canary.discord.com", "discordapp.com", "ptb.discordapp.com", "canary.discordapp.com");

        // HttpClient methods whose first argument is the request URI.
        private static readonly HashSet<string> UriMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "GetAsync", "GetStringAsync", "GetByteArrayAsync", "GetStreamAsync", "PostAsync", "PutAsync", "PatchAsync",
            "DeleteAsync", "GetFromJsonAsync", "PostAsJsonAsync", "PutAsJsonAsync", "PatchAsJsonAsync", "DeleteFromJsonAsync"
        };

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Rules.DR037);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(start =>
            {
                var allowed = AllowedHosts(start.Options.AnalyzerConfigOptionsProvider.GlobalOptions);
                start.RegisterSyntaxNodeAction(c => OnCreation(c, allowed), SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
                start.RegisterSyntaxNodeAction(c => OnInvocation(c, allowed), SyntaxKind.InvocationExpression);
                start.RegisterSyntaxNodeAction(c => OnAssignment(c, allowed), SyntaxKind.SimpleAssignmentExpression);
            });
        }

        private static HashSet<string> AllowedHosts(AnalyzerConfigOptions options)
        {
            var hosts = options.TryGetValue(AllowedHostsKey, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                : DefaultAllowedHosts.ToArray();
            return new HashSet<string>(hosts.Select(h => h.Trim().ToLowerInvariant()), StringComparer.Ordinal);
        }

        private static void OnCreation(SyntaxNodeAnalysisContext ctx, HashSet<string> allowed)
        {
            var creation = (BaseObjectCreationExpressionSyntax)ctx.Node;
            var type = ctx.SemanticModel.GetTypeInfo(creation, ctx.CancellationToken).Type.FullName();
            if (type == "System.Net.Http.HttpClient")
            {
                if (!IsSharedField(creation, ctx.SemanticModel, ctx))
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR037, creation.GetLocation(),
                        "new HttpClient outside a static readonly field: a client made per call or per object exhausts sockets on .NET Framework (no pooled handler) and is easy to point anywhere. Keep one shared instance in a static readonly field, and send every request through it"));
                }
                return;
            }

            if (type == "System.Net.Http.HttpRequestMessage" && creation.ArgumentList?.Arguments.Count >= 2)
                CheckUri(ctx, creation.ArgumentList.Arguments[1].Expression, allowed, "HttpRequestMessage");
        }

        private static void OnInvocation(SyntaxNodeAnalysisContext ctx, HashSet<string> allowed)
        {
            var call = (InvocationExpressionSyntax)ctx.Node;
            if (call.ArgumentList.Arguments.Count == 0) return;
            if (Util.MethodOf(call, ctx.SemanticModel, ctx.CancellationToken) is not IMethodSymbol m) return;
            var declaring = (m.ReducedFrom ?? m).ContainingType.FullName();
            var isClient = declaring == "System.Net.Http.HttpClient"
                           || declaring == "System.Net.Http.Json.HttpClientJsonExtensions";
            if (!isClient || !UriMethods.Contains(m.Name)) return;

            // The URI is the first argument of the call, after the client for extension methods called as static ones.
            var index = m.ReducedFrom == null && m.IsExtensionMethod ? 1 : 0;
            if (call.ArgumentList.Arguments.Count <= index) return;
            CheckUri(ctx, call.ArgumentList.Arguments[index].Expression, allowed, "HttpClient." + m.Name);
        }

        private static void OnAssignment(SyntaxNodeAnalysisContext ctx, HashSet<string> allowed)
        {
            var assignment = (AssignmentExpressionSyntax)ctx.Node;
            if (ctx.SemanticModel.GetSymbolInfo(assignment.Left, ctx.CancellationToken).Symbol is not IPropertySymbol p) return;
            var owner = p.ContainingType.FullName();
            if ((owner == "System.Net.Http.HttpRequestMessage" && p.Name == "RequestUri") ||
                (owner == "System.Net.Http.HttpClient" && p.Name == "BaseAddress"))
                CheckUri(ctx, assignment.Right, allowed, p.ContainingType.Name + "." + p.Name);
        }

        /// <summary>
        /// The URI must show its host in the code: a constant, or an interpolation or concatenation that starts with
        /// constant text holding the scheme and host, directly or through a new Uri(...) or a local set once.
        /// </summary>
        private static void CheckUri(SyntaxNodeAnalysisContext ctx, ExpressionSyntax uri, HashSet<string> allowed, string where)
        {
            if (uri.IsKind(SyntaxKind.NullLiteralExpression)) return;
            var host = HostOf(uri, ctx.SemanticModel, ctx.CancellationToken);
            if (host == null)
            {
                ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR037, uri.GetLocation(),
                    "the request URI given to " + where + " does not show its host in the code (it comes from a parameter, field, config value or method), so nobody can tell where this request goes. Build the URI from constant text that holds the scheme and an allowed host, for example $\"https://discord.com/api/webhooks/{id}/{token}\", and take only the variable parts from input"));
            }
            else if (!allowed.Contains(host))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR037, uri.GetLocation(),
                    "the request given to " + where + " goes to '" + host + "', which is not on the allowlist (" + string.Join(", ", allowed.OrderBy(h => h, StringComparer.Ordinal)) +
                    "). Every host a plugin calls must be known and allowed; add it to " + AllowedHostsKey + " in deep.globalconfig only with the owner's agreement, and disclose it"));
            }
        }

        /// <summary>
        /// The host of a URI the code shows: a constant; an interpolation or concatenation whose leading constant text holds
        /// the scheme and host; a new Uri(...) of one of those; a local set once, where it is declared; or a static readonly
        /// field in the same file that nothing assigns again. Null when the code does not show the host. Also used by DR038.
        /// </summary>
        internal static string? HostOf(ExpressionSyntax e, SemanticModel model, CancellationToken ct, int depth = 0)
        {
            if (depth > 4) return null;
            e = Strip(e);

            var constant = model.GetConstantValue(e, ct);
            if (constant.HasValue && constant.Value is string text)
                return ParseHost(text, complete: true);

            switch (e)
            {
                case InterpolatedStringExpressionSyntax interp:
                    {
                        var parts = new List<Part>();
                        foreach (var content in interp.Contents)
                        {
                            if (content is InterpolatedStringTextSyntax t)
                                parts.Add(new Part(t.TextToken.ValueText, null, true));
                            else if (content is InterpolationSyntax hole)
                                parts.Add(new Part(null, hole.Expression, hole.AlignmentClause == null && hole.FormatClause == null));
                        }
                        return HostOfParts(parts, model, ct);
                    }
                case BinaryExpressionSyntax add when add.IsKind(SyntaxKind.AddExpression):
                    {
                        var parts = new List<Part>();
                        Flatten(add, model, ct, parts);
                        return HostOfParts(parts, model, ct);
                    }
                case BaseObjectCreationExpressionSyntax created when model.GetTypeInfo(created, ct).Type.FullName() == "System.Uri"
                                                                    && created.ArgumentList?.Arguments.Count > 0:
                    // new Uri(text) or new Uri(baseUri, relative): the host is in the first argument.
                    return HostOf(created.ArgumentList.Arguments[0].Expression, model, ct, depth + 1);
                case IdentifierNameSyntax id when model.GetSymbolInfo(id, ct).Symbol is ILocalSymbol local:
                    {
                        // A local set exactly once, where it is declared.
                        var declarator = local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct) as VariableDeclaratorSyntax;
                        var body = declarator?.FirstAncestorOrSelf<BlockSyntax>();
                        if (declarator?.Initializer == null || body == null) return null;
                        return IsWritten(body, local, model, ct) ? null : HostOf(declarator.Initializer.Value, model, ct, depth + 1);
                    }
                case IdentifierNameSyntax or MemberAccessExpressionSyntax when model.GetSymbolInfo(e, ct).Symbol is IFieldSymbol
                    {
                        IsStatic: true, IsReadOnly: true
                    } field:
                    {
                        // A static readonly field is set where it is declared, and may be set again in a static constructor.
                        // Only fields of a type declared wholly in this file are followed (the semantic model in hand covers
                        // only this file), and only when nothing in the type assigns them; keep a shared base URI beside its use.
                        var declarator = field.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct) as VariableDeclaratorSyntax;
                        if (declarator?.Initializer == null || declarator.SyntaxTree != e.SyntaxTree)
                            return null;
                        var typeParts = field.ContainingType.DeclaringSyntaxReferences;
                        if (typeParts.Any(r => r.SyntaxTree != e.SyntaxTree) || typeParts.Any(r => IsWritten(r.GetSyntax(ct), field, model, ct)))
                            return null;
                        return HostOf(declarator.Initializer.Value, model, ct, depth + 1);
                    }
                default:
                    return null;
            }
        }

        /// <summary>A piece of an interpolation or concatenation: constant text, or a hole (plain: no alignment or format).</summary>
        private readonly struct Part
        {
            public Part(string? text, ExpressionSyntax? hole, bool plain)
            {
                Text = text;
                Hole = hole;
                Plain = plain;
            }

            public string? Text { get; }
            public ExpressionSyntax? Hole { get; }
            public bool Plain { get; }
        }

        private static void Flatten(ExpressionSyntax e, SemanticModel model, CancellationToken ct, List<Part> parts)
        {
            e = Strip(e);
            var constant = model.GetConstantValue(e, ct);
            if (constant.HasValue && constant.Value is string s)
            {
                parts.Add(new Part(s, null, true));
                return;
            }
            if (constant.HasValue && constant.Value is char c)
            {
                parts.Add(new Part(c.ToString(), null, true));
                return;
            }
            if (e is BinaryExpressionSyntax add && add.IsKind(SyntaxKind.AddExpression) &&
                model.GetTypeInfo(add, ct).Type?.SpecialType == SpecialType.System_String)
            {
                Flatten(add.Left, model, ct, parts);
                Flatten(add.Right, model, ct, parts);
                return;
            }
            parts.Add(new Part(null, e, true));
        }

        /// <summary>
        /// The host from the leading constant text. When that text ends inside the port ("http://127.0.0.1:" + port), the
        /// host counts only if every following piece up to the end of the authority is an integer hole with no format, or
        /// digits, so nothing can add "@other.host" (which would turn the host into user info).
        /// </summary>
        private static string? HostOfParts(List<Part> parts, SemanticModel model, CancellationToken ct)
        {
            var i = 0;
            var lead = new StringBuilder();
            while (i < parts.Count && parts[i].Text != null) lead.Append(parts[i++].Text);
            var host = ParseAuthority(lead.ToString(), complete: i == parts.Count, out var openPort);
            if (host == null || !openPort) return host;

            for (; i < parts.Count; i++)
            {
                var part = parts[i];
                if (part.Text == null)
                {
                    if (!part.Plain || !IsInteger(model.GetTypeInfo(part.Hole!, ct).Type)) return null;
                    continue;
                }
                var end = part.Text.IndexOfAny(AuthorityEnd);
                var digits = end < 0 ? part.Text : part.Text.Substring(0, end);
                if (!digits.All(IsAsciiDigit)) return null;
                if (end >= 0) return host;
            }
            return host;
        }

        private static bool IsInteger(ITypeSymbol? type)
        {
            switch (type?.SpecialType)
            {
                case SpecialType.System_Byte:
                case SpecialType.System_SByte:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';

        /// <summary>Whether anything in the scope assigns the symbol, or passes it by ref or out.</summary>
        internal static bool IsWritten(SyntaxNode scope, ISymbol symbol, SemanticModel model, CancellationToken ct)
        {
            bool Same(ExpressionSyntax x) => SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(x, ct).Symbol, symbol);
            foreach (var node in scope.DescendantNodes())
            {
                switch (node)
                {
                    case AssignmentExpressionSyntax a when Same(a.Left):
                    case ArgumentSyntax arg when !arg.RefKindKeyword.IsKind(SyntaxKind.None) && !arg.RefKindKeyword.IsKind(SyntaxKind.InKeyword) && Same(arg.Expression):
                    case RefExpressionSyntax r when Same(r.Expression):
                        return true;
                }
            }
            return false;
        }

        internal static ExpressionSyntax Strip(ExpressionSyntax e)
        {
            while (e is ParenthesizedExpressionSyntax p) e = p.Expression;
            return e;
        }

        private static readonly char[] HostEnd = { ':', '/', '?', '#' };
        private static readonly char[] AuthorityEnd = { '/', '?', '#' };

        /// <summary>
        /// The host of an absolute http(s) URI at the start of the text, or null. When the text is only the start of the URI,
        /// the host counts only if it is closed by '/', ':', '?' or '#' within that text, so "https://discord.com" + x (which
        /// could become discord.com.example.org) does not count. A host closed by ':' whose port runs to the end of partial
        /// text is returned with <paramref name="openPort"/> set: the caller must check what follows. User info ('@' before
        /// the host, or a port that is not digits) gives null. IPv6 hosts keep their brackets ("[::1]").
        /// </summary>
        public static string? ParseAuthority(string text, bool complete, out bool openPort)
        {
            openPort = false;
            var scheme = text.IndexOf("://", StringComparison.Ordinal);
            if (scheme <= 0) return null;
            var schemeName = text.Substring(0, scheme).ToLowerInvariant();
            if (schemeName != "https" && schemeName != "http") return null;
            var rest = text.Substring(scheme + 3);

            string host, after;
            if (rest.StartsWith("[", StringComparison.Ordinal))
            {
                var close = rest.IndexOf(']');
                if (close < 0) return null;
                host = rest.Substring(0, close + 1);
                after = rest.Substring(close + 1);
            }
            else
            {
                var end = rest.IndexOfAny(HostEnd);
                host = end < 0 ? rest : rest.Substring(0, end);
                after = end < 0 ? "" : rest.Substring(end);
                if (host.IndexOf('[') >= 0 || host.IndexOf(']') >= 0) return null;
            }
            host = host.ToLowerInvariant();
            if (host.Length == 0 || host.Any(c => c == '@' || c == '\\' || char.IsWhiteSpace(c) || char.IsControl(c))) return null;

            if (after.Length == 0) return complete ? host : null;
            if (after[0] == '/' || after[0] == '?' || after[0] == '#') return host;
            if (after[0] != ':') return null;

            var authorityEnd = after.IndexOfAny(AuthorityEnd);
            var port = authorityEnd < 0 ? after.Substring(1) : after.Substring(1, authorityEnd - 1);
            if (!port.All(IsAsciiDigit)) return null;
            if (authorityEnd < 0 && !complete) openPort = true;
            return host;
        }

        /// <summary>
        /// The host of an absolute http(s) URI at the start of the text, closed within the text (see <see cref="ParseAuthority"/>).
        /// </summary>
        public static string? ParseHost(string text, bool complete)
        {
            var host = ParseAuthority(text, complete, out var openPort);
            return openPort ? null : host;
        }

        private static bool IsSharedField(SyntaxNode creation, SemanticModel model, SyntaxNodeAnalysisContext ctx)
        {
            // static readonly HttpClient Http = new HttpClient();
            if (creation.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator } &&
                model.GetDeclaredSymbol(declarator, ctx.CancellationToken) is IFieldSymbol field)
                return field.IsStatic && field.IsReadOnly;

            // A static constructor assigning a static readonly field.
            if (creation.Parent is AssignmentExpressionSyntax assignment && assignment.Right == creation &&
                model.GetSymbolInfo(assignment.Left, ctx.CancellationToken).Symbol is IFieldSymbol assigned &&
                creation.FirstAncestorOrSelf<ConstructorDeclarationSyntax>() is { } ctor &&
                ctor.Modifiers.Any(SyntaxKind.StaticKeyword))
                return assigned.IsStatic && assigned.IsReadOnly;

            return false;
        }
    }
}
