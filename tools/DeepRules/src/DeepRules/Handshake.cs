using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DeepRules
{
    /// <summary>
    /// The one allowed use of SHA-1 (the user, 2026-11-21: "SHA handshakes are an exception"): the WebSocket handshake answer of
    /// RFC 6455 section 4.2.2, Sec-WebSocket-Accept = Base64(SHA1(Sec-WebSocket-Key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")).
    /// Browsers reject any other hash. <see cref="Evaluate"/> decides, from the code alone, whether one SHA1.Create() or
    /// SHA1.HashData(...) call is exactly that and nothing else. DR039 (<see cref="HandshakeAnalyzer"/>) reports every call it does
    /// not accept, and <see cref="HandshakeSuppressor"/> drops CA5350, S4790 and SCS0006 on the calls it accepts, so both always agree.
    /// </summary>
    internal static class Handshake
    {
        /// <summary>The constant RFC 6455 section 4.2.2 appends to the client's key. Compared exactly.</summary>
        public const string Guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        public const string KeyHeader = "Sec-WebSocket-Key";
        public const string AcceptHeader = "Sec-WebSocket-Accept";

        private const string Sha1Type = "System.Security.Cryptography.SHA1";
        private const int MaxDepth = 8;

        public enum Site
        {
            None,
            /// <summary>SHA1.Create(): an object that then hashes with ComputeHash.</summary>
            Create,
            /// <summary>SHA1.HashData(byte[]) or SHA1.HashData(ReadOnlySpan&lt;byte&gt;).</summary>
            HashData
        }

        /// <summary>SHA1 itself or any type derived from it (SHA1Managed, SHA1CryptoServiceProvider, SHA1Cng): what the old T: ban covered.</summary>
        public static bool IsSha1(ITypeSymbol? type) => type.InheritsFrom(Sha1Type);

        public static Site SiteOf(InvocationExpressionSyntax call, SemanticModel model, CancellationToken ct)
        {
            var method = Util.MethodOf(call, model, ct);
            if (method == null || !method.IsStatic || method.ContainingType.FullName() != Sha1Type) return Site.None;
            if (method.Name == "Create" && method.Parameters.Length == 0) return Site.Create;
            if (method.Name == "HashData" && method.Parameters.Length == 1 && IsByteBuffer(method.Parameters[0].Type)) return Site.HashData;
            return Site.None;
        }

        /// <summary>Null when the call is the handshake answer; otherwise why it is not.</summary>
        public static string? Evaluate(InvocationExpressionSyntax call, SemanticModel model, CancellationToken ct)
        {
            var checker = new Checker(model, ct);
            switch (SiteOf(call, model, ct))
            {
                case Site.HashData:
                    return checker.Digest(call, call.ArgumentList.Arguments[0].Expression);
                case Site.Create:
                    return checker.Create(call);
                default:
                    return "this is not SHA1.Create() or SHA1.HashData(byte[])";
            }
        }

        private static bool IsByteBuffer(ITypeSymbol? type) =>
            type is IArrayTypeSymbol { Rank: 1, ElementType.SpecialType: SpecialType.System_Byte } ||
            (type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } n && n.FullName() == "System.ReadOnlySpan`1" &&
             n.TypeArguments[0].SpecialType == SpecialType.System_Byte);

        private sealed class Checker
        {
            private readonly SemanticModel _model;
            private readonly CancellationToken _ct;

            public Checker(SemanticModel model, CancellationToken ct)
            {
                _model = model;
                _ct = ct;
            }

            // ---------------------------------------------------------------- the hash object and the call

            public string? Create(InvocationExpressionSyntax create)
            {
                // SHA1.Create().ComputeHash(bytes)
                if (create.Parent is MemberAccessExpressionSyntax ma && ma.Expression == create && ma.Name.Identifier.ValueText == "ComputeHash" &&
                    ma.Parent is InvocationExpressionSyntax chained && chained.Expression == ma)
                    return ComputeHash(chained);

                // using var sha = SHA1.Create();  using (var sha = SHA1.Create()) { ... }  var sha = SHA1.Create();
                if (create.Parent is not EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator } ||
                    declarator.Parent is not VariableDeclarationSyntax { Variables.Count: 1 })
                    return "the SHA1 object is not used directly for one ComputeHash call (it is passed on, stored in a field or returned)";
                if (_model.GetDeclaredSymbol(declarator, _ct) is not ILocalSymbol local || declarator.FirstAncestorOrSelf<BlockSyntax>() is not { } scope)
                    return "the SHA1 object is not held in a local variable";
                if (NetworkAnalyzer.IsWritten(scope, local, _model, _ct))
                    return "the SHA1 variable '" + local.Name + "' is assigned again";

                var calls = 0;
                foreach (var use in References(scope, local))
                {
                    if (use.Parent is MemberAccessExpressionSyntax access && access.Expression == use &&
                        access.Parent is InvocationExpressionSyntax invocation && invocation.Expression == access)
                    {
                        var name = access.Name.Identifier.ValueText;
                        if (name == "ComputeHash")
                        {
                            calls++;
                            var reason = ComputeHash(invocation);
                            if (reason != null) return reason;
                            continue;
                        }
                        if (name == "Dispose" && invocation.ArgumentList.Arguments.Count == 0) continue;
                    }
                    return "the SHA1 object '" + local.Name + "' is used for something other than ComputeHash(byte[]) and Dispose()";
                }
                return calls == 0 ? "the SHA1 object '" + local.Name + "' never calls ComputeHash" : null;
            }

            private string? ComputeHash(InvocationExpressionSyntax call)
            {
                var method = Util.MethodOf(call, _model, _ct);
                if (method == null || method.Name != "ComputeHash" || !method.ContainingType.InheritsFrom("System.Security.Cryptography.HashAlgorithm") ||
                    method.Parameters.Length != 1 || !IsByteBuffer(method.Parameters[0].Type) || call.ArgumentList.Arguments.Count != 1)
                    return "only ComputeHash(byte[]) is accepted";
                return Digest(call, call.ArgumentList.Arguments[0].Expression);
            }

            /// <summary>The bytes hashed must be the request's key plus the GUID, and the digest may only become the Accept value.</summary>
            public string? Digest(ExpressionSyntax digest, ExpressionSyntax input) => Input(input, 0) ?? DigestUse(digest, 0);

            // ---------------------------------------------------------------- what is hashed

            private string? Input(ExpressionSyntax expression, int depth)
            {
                if (depth > MaxDepth) return "the hashed bytes are built through too many steps to follow";
                var e = Util.Strip(expression);

                if (e is IdentifierNameSyntax)
                {
                    var init = SingleAssignment(e, out var reason);
                    if (init == null) return "the hashed bytes are a variable that is " + reason;
                    return Input(init, depth + 1);
                }

                if (e is InvocationExpressionSyntax call)
                {
                    var method = Util.MethodOf(call, _model, _ct);
                    if (method != null && method.Name == "GetBytes" && IsAcceptedEncoding(call) && call.ArgumentList.Arguments.Count == 1 &&
                        method.Parameters.Length == 1 && method.Parameters[0].Type.SpecialType == SpecialType.System_String)
                        return Text(call.ArgumentList.Arguments[0].Expression, depth + 1);

                    // Encoding.UTF8.GetBytes(key).Concat(Encoding.UTF8.GetBytes(guid)).ToArray()
                    if (method != null && method.Name == "ToArray" && method.ContainingType.FullName() == "System.Linq.Enumerable" &&
                        Util.Receiver(call) is { } mid && Util.Strip(mid) is InvocationExpressionSyntax concat &&
                        Util.MethodOf(concat, _model, _ct) is { Name: "Concat" } concatMethod && concatMethod.ContainingType.FullName() == "System.Linq.Enumerable" &&
                        Util.Receiver(concat) is { } first && concat.ArgumentList.Arguments.Count == 1)
                    {
                        return BytesOf(first, key: true, depth + 1) ?? BytesOf(concat.ArgumentList.Arguments[0].Expression, key: false, depth + 1);
                    }
                }

                return "the hashed bytes are not Encoding.UTF8.GetBytes(key + GUID) (or the ASCII form, or the two GetBytes arrays joined with Concat) built from the request's Sec-WebSocket-Key header";
            }

            private string? BytesOf(ExpressionSyntax expression, bool key, int depth)
            {
                var e = Util.Strip(expression);
                if (e is IdentifierNameSyntax)
                {
                    var init = SingleAssignment(e, out var reason);
                    return init == null ? "a byte array is a variable that is " + reason : BytesOf(init, key, depth + 1);
                }
                if (e is InvocationExpressionSyntax call && Util.MethodOf(call, _model, _ct) is { Name: "GetBytes" } method && IsAcceptedEncoding(call) &&
                    call.ArgumentList.Arguments.Count == 1 && method.Parameters.Length == 1 && method.Parameters[0].Type.SpecialType == SpecialType.System_String)
                {
                    var part = call.ArgumentList.Arguments[0].Expression;
                    return key ? KeyPart(part) : GuidPart(part);
                }
                return "a joined byte array is not Encoding.UTF8.GetBytes(...) of the key or of the GUID";
            }

            /// <summary>Encoding.UTF8 or Encoding.ASCII: the key and the GUID are ASCII text, so the bytes are the same.</summary>
            private bool IsAcceptedEncoding(InvocationExpressionSyntax call)
            {
                if (Util.Receiver(call) is not { } receiver) return false;
                return Util.SymbolOf(receiver, _model, _ct) is IPropertySymbol { IsStatic: true, Name: "UTF8" or "ASCII" } property &&
                       property.ContainingType.FullName() == "System.Text.Encoding";
            }

            private string? Text(ExpressionSyntax expression, int depth)
            {
                if (depth > MaxDepth) return "the hashed text is built through too many steps to follow";
                var e = Util.Strip(expression);

                if (e is IdentifierNameSyntax && !_model.GetConstantValue(e, _ct).HasValue)
                {
                    var init = SingleAssignment(e, out var reason);
                    return init == null ? "the hashed text is a variable that is " + reason : Text(init, depth + 1);
                }

                switch (e)
                {
                    case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression):
                        return KeyPart(binary.Left) ?? GuidPart(binary.Right);

                    case InterpolatedStringExpressionSyntax interpolated:
                        if (interpolated.Contents.Count == 2 && interpolated.Contents[0] is InterpolationSyntax { AlignmentClause: null, FormatClause: null } head)
                        {
                            var key = KeyPart(head.Expression);
                            if (key != null) return key;
                            return interpolated.Contents[1] switch
                            {
                                InterpolatedStringTextSyntax text => text.TextToken.ValueText == Guid ? null : WrongGuid(text.TextToken.ValueText),
                                InterpolationSyntax { AlignmentClause: null, FormatClause: null } tail => GuidPart(tail.Expression),
                                _ => "the second part of the hashed text is not the GUID"
                            };
                        }
                        break;

                    case InvocationExpressionSyntax call when Util.MethodOf(call, _model, _ct) is { Name: "Concat", IsStatic: true } method &&
                                                             method.ContainingType.SpecialType == SpecialType.System_String &&
                                                             method.Parameters.Length == 2 && call.ArgumentList.Arguments.Count == 2 &&
                                                             method.Parameters.All(p => p.Type.SpecialType == SpecialType.System_String):
                        return KeyPart(call.ArgumentList.Arguments[0].Expression) ?? GuidPart(call.ArgumentList.Arguments[1].Expression);
                }

                return "the hashed text is not exactly the request's Sec-WebSocket-Key value followed by the RFC 6455 GUID";
            }

            private static string WrongGuid(string text) =>
                "the constant text hashed after the key is '" + text + "', not the RFC 6455 GUID " + Guid + " (compared exactly)";

            private string? GuidPart(ExpressionSyntax expression)
            {
                var text = ConstantText(expression);
                if (text == null) return "the text hashed after the key is not constant text in the code";
                return text == Guid ? null : WrongGuid(text);
            }

            /// <summary>The text of a constant or of a static readonly string field with a constant initializer that nothing assigns again.</summary>
            private string? ConstantText(ExpressionSyntax expression)
            {
                var value = _model.GetConstantValue(expression, _ct);
                if (value.HasValue) return value.Value as string;

                var e = Util.Strip(expression);
                if (e is not (IdentifierNameSyntax or MemberAccessExpressionSyntax) ||
                    Util.SymbolOf(e, _model, _ct) is not IFieldSymbol { IsStatic: true, IsReadOnly: true } field ||
                    field.Type.SpecialType != SpecialType.System_String ||
                    field.DeclaringSyntaxReferences.Length != 1 ||
                    field.DeclaringSyntaxReferences[0].GetSyntax(_ct) is not VariableDeclaratorSyntax { Initializer: { } initializer } declarator)
                    return null;

                var other = _model.Compilation.GetSemanticModel(declarator.SyntaxTree);
                foreach (var part in field.ContainingType.DeclaringSyntaxReferences)
                {
                    var partModel = _model.Compilation.GetSemanticModel(part.SyntaxTree);
                    if (NetworkAnalyzer.IsWritten(part.GetSyntax(_ct), field, partModel, _ct)) return null;
                }
                var constant = other.GetConstantValue(initializer.Value, _ct);
                return constant.HasValue ? constant.Value as string : null;
            }

            // ---------------------------------------------------------------- where the key comes from

            private string? KeyPart(ExpressionSyntax expression) => IsKey(expression, 0) ? null : "the text hashed before the GUID is not the request's Sec-WebSocket-Key header value";

            /// <summary>
            /// The value of a header named Sec-WebSocket-Key: an indexer or Get/TryGetValue with that constant name (any receiver, the name
            /// compared without case, as headers are), the first group of a Regex.Match whose constant pattern contains that name, a local
            /// set once from one of these, or a string parameter named secWebSocketKey / webSocketKey. Trim() and ToString() may follow.
            /// </summary>
            private bool IsKey(ExpressionSyntax expression, int depth)
            {
                if (depth > MaxDepth) return false;
                var e = Util.Strip(expression);

                while (e is InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } unwrap && Util.InvokedName(unwrap) is "Trim" or "ToString" &&
                       Util.Receiver(unwrap) is { } inner)
                    e = Util.Strip(inner);

                switch (e)
                {
                    case ElementAccessExpressionSyntax { ArgumentList.Arguments.Count: 1 } element:
                        return IsName(element.ArgumentList.Arguments[0].Expression, KeyHeader);

                    case InvocationExpressionSyntax { ArgumentList.Arguments.Count: 1 } get when Util.InvokedName(get) == "Get":
                        return IsName(get.ArgumentList.Arguments[0].Expression, KeyHeader);

                    case MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Value" } value when Util.Strip(value.Expression) is ElementAccessExpressionSyntax groups &&
                                                                                                    Util.Strip(groups.Expression) is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Groups" } groupsAccess:
                        return IsKeyMatch(groupsAccess.Expression, depth + 1);

                    case IdentifierNameSyntax:
                        switch (Util.SymbolOf(e, _model, _ct))
                        {
                            case IParameterSymbol parameter:
                                return parameter.Type.SpecialType == SpecialType.System_String && IsKeyName(parameter.Name);
                            case ILocalSymbol local:
                                if (IsKeyOutVariable(local)) return true;
                                var init = SingleAssignment(e, out _);
                                return init != null && IsKey(init, depth + 1);
                        }
                        return false;
                }
                return false;
            }

            private static bool IsKeyName(string name)
            {
                var normal = name.Replace("_", "").Replace("-", "").ToLowerInvariant();
                return normal == "secwebsocketkey" || normal == "websocketkey";
            }

            private bool IsName(ExpressionSyntax expression, string name) =>
                string.Equals(ConstantText(expression), name, StringComparison.OrdinalIgnoreCase);

            /// <summary>Regex.Match(request, "Sec-WebSocket-Key: (.*)"), directly or through a local set once.</summary>
            private bool IsKeyMatch(ExpressionSyntax expression, int depth)
            {
                if (depth > MaxDepth) return false;
                var e = Util.Strip(expression);
                if (e is IdentifierNameSyntax && Util.SymbolOf(e, _model, _ct) is ILocalSymbol)
                {
                    var init = SingleAssignment(e, out _);
                    return init != null && IsKeyMatch(init, depth + 1);
                }
                return e is InvocationExpressionSyntax call && Util.MethodOf(call, _model, _ct) is { Name: "Match", IsStatic: true } method &&
                       method.ContainingType.FullName() == "System.Text.RegularExpressions.Regex" &&
                       call.ArgumentList.Arguments.Any(a => _model.GetConstantValue(a.Expression, _ct).Value is string pattern &&
                                                             pattern.IndexOf(KeyHeader, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            /// <summary>headers.TryGetValue("Sec-WebSocket-Key", out var key): the local is only ever set by that call.</summary>
            private bool IsKeyOutVariable(ILocalSymbol local)
            {
                if (local.DeclaringSyntaxReferences.Length != 1 ||
                    local.DeclaringSyntaxReferences[0].GetSyntax(_ct) is not SingleVariableDesignationSyntax designation ||
                    designation.Parent is not DeclarationExpressionSyntax ||
                    designation.FirstAncestorOrSelf<ArgumentSyntax>() is not { } argument ||
                    !argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword) ||
                    argument.Parent is not ArgumentListSyntax { Arguments.Count: 2 } list ||
                    list.Arguments[1] != argument ||
                    list.Parent is not InvocationExpressionSyntax call ||
                    Util.InvokedName(call) != "TryGetValue" ||
                    !IsName(list.Arguments[0].Expression, KeyHeader) ||
                    call.FirstAncestorOrSelf<BlockSyntax>() is not { } scope)
                    return false;

                foreach (var node in scope.DescendantNodes())
                {
                    switch (node)
                    {
                        case AssignmentExpressionSyntax a when Is(a.Left, local):
                        case PrefixUnaryExpressionSyntax p when Is(p.Operand, local):
                        case PostfixUnaryExpressionSyntax q when Is(q.Operand, local):
                        case ArgumentSyntax r when r != argument && !r.RefKindKeyword.IsKind(SyntaxKind.None) && !r.RefKindKeyword.IsKind(SyntaxKind.InKeyword) && Is(r.Expression, local):
                            return false;
                    }
                }
                return true;
            }

            private bool Is(ExpressionSyntax e, ISymbol symbol) => SymbolEqualityComparer.Default.Equals(_model.GetSymbolInfo(e, _ct).Symbol, symbol);

            // ---------------------------------------------------------------- where the digest goes

            private string? DigestUse(ExpressionSyntax digest, int depth)
            {
                if (depth > MaxDepth) return "the digest is passed through too many steps to follow";
                var parent = ParentOf(digest, out _);
                if (parent is ArgumentSyntax { Parent: ArgumentListSyntax { Arguments.Count: 1, Parent: InvocationExpressionSyntax call } } && IsBase64(call))
                    return AcceptUse(call, depth + 1);
                if (LocalOf(parent) is { } declarator)
                    return Uses(declarator, use => DigestUse(use, depth + 1));
                return "the digest bytes are used for something other than Convert.ToBase64String (a token, a password hash, a file, a log line, a comparison)";
            }

            private bool IsBase64(InvocationExpressionSyntax call) =>
                Util.MethodOf(call, _model, _ct) is { Name: "ToBase64String", IsStatic: true } method && 
                method.ContainingType.FullName() == "System.Convert" && call.ArgumentList.Arguments.Count == 1 && method.Parameters.Length >= 1 &&
                IsByteBuffer(method.Parameters[0].Type) && method.Parameters.Skip(1).All(p => p.HasExplicitDefaultValue);

            /// <summary>The Base64 text: only a Sec-WebSocket-Accept header value, or the Sec-WebSocket-Accept line of a response written to the client's socket.</summary>
            private string? AcceptUse(ExpressionSyntax text, int depth)
            {
                if (depth > MaxDepth) return "the Base64 text is passed through too many steps to follow";
                var parent = ParentOf(text, out var child);

                if (LocalOf(parent) is { } declarator)
                    return Uses(declarator, use => AcceptUse(use, depth + 1));

                // response.Headers["Sec-WebSocket-Accept"] = text;
                if (parent is AssignmentExpressionSyntax assignment && assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) && assignment.Right == child &&
                    Util.Strip(assignment.Left) is ElementAccessExpressionSyntax { ArgumentList.Arguments.Count: 1 } element &&
                    IsName(element.ArgumentList.Arguments[0].Expression, AcceptHeader))
                    return null;

                // response.Headers.Add("Sec-WebSocket-Accept", text);
                if (parent is ArgumentSyntax { Parent: ArgumentListSyntax { Arguments.Count: >= 2 } list } argument && list.Arguments[1] == argument &&
                    list.Parent is InvocationExpressionSyntax call && HeaderMethods.Contains(Util.InvokedName(call)) &&
                    IsName(list.Arguments[0].Expression, AcceptHeader))
                    return null;

                // "...\r\nSec-WebSocket-Accept: " + text + "\r\n\r\n"  and  $"...Sec-WebSocket-Accept: {text}..."
                if (parent is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.AddExpression))
                {
                    var root = ConcatenationRoot(binary);
                    var leaves = new List<ExpressionSyntax>();
                    Flatten(root, leaves);
                    var index = leaves.FindIndex(l => l.Span.Contains(text.Span));
                    if (index < 0 || !FollowsHeader(leaves.Take(index).Select(ConstantText).ToList()))
                        return "the Base64 text is joined to other text, but not right after a line that ends 'Sec-WebSocket-Accept: '";
                    return LineUse(root, depth + 1);
                }
                if (parent is InterpolationSyntax { AlignmentClause: null, FormatClause: null } hole && hole.Parent is InterpolatedStringExpressionSyntax interpolated)
                {
                    var index = interpolated.Contents.IndexOf(hole);
                    var before = new List<string?>();
                    for (var i = 0; i < index; i++)
                        before.Add(interpolated.Contents[i] is InterpolatedStringTextSyntax piece ? piece.TextToken.ValueText : null);
                    if (!FollowsHeader(before))
                        return "the Base64 text is in an interpolated string, but not right after a line that ends 'Sec-WebSocket-Accept: '";
                    return LineUse(interpolated, depth + 1);
                }

                // private string AcceptKey(string key) => Convert.ToBase64String(...);   every caller must use the value as above.
                if (parent is ReturnStatementSyntax or ArrowExpressionClauseSyntax)
                    return ReturnUse(parent, depth + 1);

                return "the Base64 text is used for something other than the Sec-WebSocket-Accept header value or the Sec-WebSocket-Accept line of the 101 response (a token, a file, a log line, a comparison)";
            }

            private static readonly string[] HeaderMethods = { "Add", "Append", "Set", "TryAdd", "AddHeader", "SetHeader", "AppendHeader" };

            private static ExpressionSyntax ConcatenationRoot(ExpressionSyntax e)
            {
                while (e.Parent is ParenthesizedExpressionSyntax || (e.Parent is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.AddExpression)))
                    e = (ExpressionSyntax)e.Parent;
                return e;
            }

            private void Flatten(ExpressionSyntax e, List<ExpressionSyntax> leaves)
            {
                var stripped = Util.Strip(e);
                if (stripped is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.AddExpression) &&
                    _model.GetTypeInfo(binary, _ct).Type?.SpecialType == SpecialType.System_String && !_model.GetConstantValue(binary, _ct).HasValue)
                {
                    Flatten(binary.Left, leaves);
                    Flatten(binary.Right, leaves);
                }
                else
                {
                    leaves.Add(e);
                }
            }

            /// <summary>The constant text before the value ends the line "Sec-WebSocket-Accept:" (a space may follow), and that line starts the text or follows a line feed.</summary>
            private static bool FollowsHeader(List<string?> before)
            {
                var sb = new StringBuilder();
                for (var i = before.Count - 1; i >= 0 && before[i] != null; i--) sb.Insert(0, before[i]);
                var text = sb.ToString().TrimEnd(' ');
                if (!text.EndsWith(AcceptHeader + ":", StringComparison.OrdinalIgnoreCase)) return false;
                var lead = text.Substring(0, text.Length - AcceptHeader.Length - 1);
                return lead.Length == 0 ? before.All(b => b != null) : lead[lead.Length - 1] == '\n';
            }

            /// <summary>The response text: turned into bytes and written to a network stream or socket.</summary>
            private string? LineUse(ExpressionSyntax line, int depth)
            {
                if (depth > MaxDepth) return "the response text is passed through too many steps to follow";
                var parent = ParentOf(line, out _);
                if (LocalOf(parent) is { } declarator)
                    return Uses(declarator, use => LineUse(use, depth + 1));
                if (parent is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Length" })
                    return null;
                if (parent is ArgumentSyntax { Parent: ArgumentListSyntax { Arguments.Count: 1, Parent: InvocationExpressionSyntax call } } &&
                    Util.MethodOf(call, _model, _ct) is { Name: "GetBytes" } method && method.Parameters.Length == 1 &&
                    method.Parameters[0].Type.SpecialType == SpecialType.System_String && IsAcceptedEncoding(call))
                    return WireUse(call, depth + 1);
                return "the response line is used for something other than Encoding.UTF8.GetBytes and a write to the client";
            }

            private string? WireUse(ExpressionSyntax bytes, int depth)
            {
                if (depth > MaxDepth) return "the response bytes are passed through too many steps to follow";
                var parent = ParentOf(bytes, out _);
                if (LocalOf(parent) is { } declarator)
                    return Uses(declarator, use => WireUse(use, depth + 1));
                if (parent is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Length" })
                    return null;
                if (parent is ArgumentSyntax { Parent: ArgumentListSyntax list } argument && list.Arguments[0] == argument &&
                    list.Parent is InvocationExpressionSyntax write && Util.InvokedName(write) is "Write" or "WriteAsync" or "Send" or "SendAsync" &&
                    Util.Receiver(write) is { } receiver && _model.GetTypeInfo(receiver, _ct).Type is { } type &&
                    type.FullName() is "System.Net.Sockets.NetworkStream" or "System.Net.Security.SslStream" or "System.Net.Sockets.Socket")
                    return null;
                return "the response bytes are used for something other than a write to a NetworkStream, SslStream or Socket (or their Length)";
            }

            private string? ReturnUse(SyntaxNode returned, int depth)
            {
                if (depth > MaxDepth) return "the value is returned through too many methods to follow";
                var owner = returned.Ancestors().FirstOrDefault(a => a is MethodDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax or
                                                                     AccessorDeclarationSyntax or ConstructorDeclarationSyntax or OperatorDeclarationSyntax);
                if (owner is not MethodDeclarationSyntax declaration || _model.GetDeclaredSymbol(declaration, _ct) is not IMethodSymbol method ||
                    method.DeclaredAccessibility != Accessibility.Private || method.ReturnType.SpecialType != SpecialType.System_String)
                    return "the Base64 text is returned from something other than a private method returning string (a public or internal method, a lambda or a property, whose callers cannot be checked)";

                var callers = 0;
                foreach (var part in method.ContainingType.DeclaringSyntaxReferences)
                {
                    var other = new Checker(_model.Compilation.GetSemanticModel(part.SyntaxTree), _ct);
                    foreach (var name in part.GetSyntax(_ct).DescendantNodes().OfType<SimpleNameSyntax>())
                    {
                        if (name.Identifier.ValueText != method.Name ||
                            !SymbolEqualityComparer.Default.Equals(other._model.GetSymbolInfo(name, _ct).Symbol?.OriginalDefinition, method.OriginalDefinition))
                            continue;
                        ExpressionSyntax reference = name.Parent is MemberAccessExpressionSyntax access && access.Name == name ? access : name;
                        if (reference.Parent is not InvocationExpressionSyntax call || call.Expression != reference)
                            return "the private method '" + method.Name + "' that returns the Base64 text is used as a delegate, where its result cannot be followed";
                        callers++;
                        var reason = other.AcceptUse(call, depth + 1);
                        if (reason != null) return "a call of '" + method.Name + "' (which returns the Base64 text): " + reason;
                    }
                }
                return callers == 0 ? "the private method '" + method.Name + "' that returns the Base64 text is never called" : null;
            }

            // ---------------------------------------------------------------- locals

            /// <summary>The expression a local was set to where it is declared, when nothing assigns, increments or passes it by ref/out afterwards.</summary>
            private ExpressionSyntax? SingleAssignment(ExpressionSyntax identifier, out string reason)
            {
                reason = "not a local variable (a parameter, field, property or method result)";
                if (Util.SymbolOf(identifier, _model, _ct) is not ILocalSymbol local) return null;
                reason = "not set where it is declared";
                if (local.DeclaringSyntaxReferences.Length != 1 ||
                    local.DeclaringSyntaxReferences[0].GetSyntax(_ct) is not VariableDeclaratorSyntax { Initializer: { } initializer } declarator ||
                    declarator.FirstAncestorOrSelf<BlockSyntax>() is not { } scope)
                    return null;
                reason = "assigned again";
                if (NetworkAnalyzer.IsWritten(scope, local, _model, _ct)) return null;
                return initializer.Value;
            }

            private static VariableDeclaratorSyntax? LocalOf(SyntaxNode? parent) =>
                parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Variables.Count: 1, Parent: LocalDeclarationStatementSyntax } } declarator }
                    ? declarator
                    : null;

            private IEnumerable<IdentifierNameSyntax> References(SyntaxNode scope, ISymbol local) =>
                scope.DescendantNodes().OfType<IdentifierNameSyntax>().Where(id => id.Identifier.ValueText == local.Name &&
                    SymbolEqualityComparer.Default.Equals(_model.GetSymbolInfo(id, _ct).Symbol, local));

            /// <summary>Every use of a local declared with an initializer must pass the check, and it must be set only there.</summary>
            private string? Uses(VariableDeclaratorSyntax declarator, Func<IdentifierNameSyntax, string?> check)
            {
                if (_model.GetDeclaredSymbol(declarator, _ct) is not ILocalSymbol local || declarator.FirstAncestorOrSelf<BlockSyntax>() is not { } scope)
                    return "a value is held in something other than a local variable";
                if (NetworkAnalyzer.IsWritten(scope, local, _model, _ct))
                    return "the variable '" + local.Name + "' is assigned again";
                var any = false;
                foreach (var use in References(scope, local))
                {
                    any = true;
                    var reason = check(use);
                    if (reason != null) return reason;
                }
                return any ? null : "the variable '" + local.Name + "' is never used";
            }

            private static SyntaxNode? ParentOf(ExpressionSyntax e, out ExpressionSyntax child)
            {
                child = e;
                var parent = e.Parent;
                while (parent is ParenthesizedExpressionSyntax paren)
                {
                    child = paren;
                    parent = paren.Parent;
                }
                return parent;
            }
        }
    }
}
