using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DeepRules
{
    /// <summary>DR016-DR020: files, saved formats, culture and clocks.</summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class PersistenceAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(Rules.DR016, Rules.DR017, Rules.DR018, Rules.DR019, Rules.DR020);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(OnInvocation, SyntaxKind.InvocationExpression);
            context.RegisterSyntaxNodeAction(OnCreation, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
            context.RegisterSyntaxNodeAction(OnMemberDeclaration, SyntaxKind.PropertyDeclaration, SyntaxKind.FieldDeclaration);
            context.RegisterSyntaxNodeAction(OnCast, SyntaxKind.CastExpression);
            context.RegisterSyntaxNodeAction(OnInterpolation, SyntaxKind.InterpolatedStringExpression);
            context.RegisterSyntaxNodeAction(OnConcat, SyntaxKind.AddExpression);
            context.RegisterSyntaxNodeAction(OnMemberAccess, SyntaxKind.SimpleMemberAccessExpression);
            context.RegisterSyntaxNodeAction(OnAssignment, SyntaxKind.SimpleAssignmentExpression);
            context.RegisterSyntaxNodeAction(OnComparison, SyntaxKind.GreaterThanExpression, SyntaxKind.GreaterThanOrEqualExpression,
                SyntaxKind.LessThanExpression, SyntaxKind.LessThanOrEqualExpression);
        }

        private static void OnInvocation(SyntaxNodeAnalysisContext ctx)
        {
            var inv = (InvocationExpressionSyntax)ctx.Node;
            var m = Util.MethodOf(inv, ctx.SemanticModel, ctx.CancellationToken);
            if (m?.ContainingType == null) return;
            InPlaceWrite(ctx, inv, m, inv.ArgumentList);
            CultureCall(ctx, inv, m);
            TimeSpanParse(ctx, inv, m);
            TimeSpanUse(ctx, inv, m, inv.ArgumentList);
        }

        private static void OnCreation(SyntaxNodeAnalysisContext ctx)
        {
            var oc = (BaseObjectCreationExpressionSyntax)ctx.Node;
            if (oc.ArgumentList == null) return;
            if (ctx.SemanticModel.GetSymbolInfo(oc, ctx.CancellationToken).Symbol is not IMethodSymbol ctor) return;
            InPlaceWrite(ctx, oc, ctor, oc.ArgumentList);
            TimeSpanUse(ctx, oc, ctor, oc.ArgumentList);
        }

        private static ExpressionSyntax? ArgFor(ArgumentListSyntax args, IMethodSymbol m, int index)
        {
            if (index >= m.Parameters.Length) return null;
            var p = m.Parameters[index];
            foreach (var a in args.Arguments)
                if (a.NameColon != null && a.NameColon.Name.Identifier.Text == p.Name) return a.Expression;
            return index < args.Arguments.Count && args.Arguments[index].NameColon == null ? args.Arguments[index].Expression : null;
        }

        private static IParameterSymbol? ParamFor(ArgumentSyntax arg, IMethodSymbol m)
        {
            if (arg.NameColon != null) return m.Parameters.FirstOrDefault(p => p.Name == arg.NameColon.Name.Identifier.Text);
            if (arg.Parent is not ArgumentListSyntax list) return null;
            var i = list.Arguments.IndexOf(arg);
            if (i < 0) return null;
            if (i < m.Parameters.Length) return m.Parameters[i];
            return m.Parameters.Length > 0 && m.Parameters[m.Parameters.Length - 1].IsParams ? m.Parameters[m.Parameters.Length - 1] : null;
        }

        // ---------------- DR016 ----------------

        private static readonly HashSet<string> FileWrites = new HashSet<string>(StringComparer.Ordinal)
            { "WriteAllText", "WriteAllBytes", "WriteAllLines", "WriteAllTextAsync", "WriteAllBytesAsync", "WriteAllLinesAsync", "Create", "OpenWrite" };

        private static void InPlaceWrite(SyntaxNodeAnalysisContext ctx, SyntaxNode call, IMethodSymbol m, ArgumentListSyntax args)
        {
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            var tn = m.ContainingType.FullName();
            var firstIsString = m.Parameters.Length > 0 && m.Parameters[0].Type.SpecialType == SpecialType.System_String;
            string? api = null;
            if (m.MethodKind == MethodKind.Constructor)
            {
                if (tn == "System.IO.FileStream" && firstIsString && m.Parameters.Length > 1 && m.Parameters[1].Type.Is("System.IO.FileMode") &&
                    ArgFor(args, m, 1) is ExpressionSyntax mode && IsOverwriteMode(mode, model, ct))
                    api = "new FileStream(..., " + Util.Short(mode, 30) + ")";
                else if (tn == "System.IO.StreamWriter" && firstIsString)
                {
                    var appendIndex = -1;
                    for (var i = 0; i < m.Parameters.Length; i++)
                        if (m.Parameters[i].Name == "append") appendIndex = i;
                    var append = appendIndex >= 0 ? ArgFor(args, m, appendIndex) : null;
                    if (append == null || append.IsKind(SyntaxKind.FalseLiteralExpression))
                        api = "new StreamWriter(path" + (append == null ? "" : ", false") + ")";
                }
            }
            else if (tn == "System.IO.File" && FileWrites.Contains(m.Name))
                api = "File." + m.Name;
            else if (tn == "System.IO.File" && m.Name == "Open" && ArgFor(args, m, 1) is ExpressionSyntax openMode && IsOverwriteMode(openMode, model, ct))
                api = "File.Open(..., " + Util.Short(openMode, 30) + ")";
            else if ((tn == "System.Xml.XmlDocument" || tn == "System.Xml.Linq.XDocument" || tn == "System.Xml.Linq.XElement") && m.Name == "Save" && firstIsString)
                api = m.ContainingType.Name + ".Save(path)";
            else if (tn == "System.Xml.XmlWriter" && m.Name == "Create" && firstIsString)
                api = "XmlWriter.Create(path)";
            if (api == null) return;

            var path = ArgFor(args, m, 0);
            if (path == null || LooksTemporary(path, model, ct)) return;
            var member = Util.EnclosingMember(call);
            if (member == null || CallsReplaceOrMove(member, model, ct) || CallerReplaces(member, model, ct)) return;
            ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR016, call.GetLocation(),
                api + " overwrites '" + Util.Short(path, 40) + "' in place; a crash, kill or full disk mid-write leaves it truncated or empty, and the next load falls back to defaults or fails (APBL InstantPunishmentFile.ReplaceDamage rewrote the config with File.WriteAllBytes). Write a temporary file beside it and File.Replace/File.Move it over the original"));
        }

        private static bool IsOverwriteMode(ExpressionSyntax mode, SemanticModel model, CancellationToken ct)
        {
            var c = model.GetConstantValue(mode, ct);
            if (!c.HasValue || c.Value == null) return false;
            var v = Convert.ToInt32(c.Value, CultureInfo.InvariantCulture);
            return v == 2 || v == 4 || v == 5; // Create, OpenOrCreate, Truncate
        }

        private static readonly Regex TempWord = new Regex("tmp|temp|\\.new", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static bool LooksTemporary(ExpressionSyntax path, SemanticModel model, CancellationToken ct)
        {
            if (TempWord.IsMatch(path.ToString())) return true;
            if (Util.SymbolOf(path, model, ct) is ILocalSymbol local)
                foreach (var r in local.DeclaringSyntaxReferences)
                    if (r.GetSyntax(ct) is VariableDeclaratorSyntax v && v.Initializer != null && TempWord.IsMatch(v.Initializer.Value.ToString()))
                        return true;
            return false;
        }

        private static bool CallsReplaceOrMove(SyntaxNode member, SemanticModel model, CancellationToken ct)
        {
            foreach (var inv in member.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = Util.InvokedName(inv);
                if (name != "Replace" && name != "Move") continue;
                if (Util.MethodOf(inv, model, ct)?.ContainingType.Is("System.IO.File") == true) return true;
            }
            return false;
        }

        private static bool CallerReplaces(SyntaxNode member, SemanticModel model, CancellationToken ct)
        {
            if (member is not MethodDeclarationSyntax md || model.GetDeclaredSymbol(md, ct) is not IMethodSymbol self) return false;
            var typeDecl = md.FirstAncestorOrSelf<TypeDeclarationSyntax>();
            if (typeDecl == null) return false;
            foreach (var inv in typeDecl.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (Util.InvokedName(inv) != self.Name || !Util.Same(Util.MethodOf(inv, model, ct), self)) continue;
                var caller = Util.EnclosingMember(inv);
                if (caller != null && CallsReplaceOrMove(caller, model, ct)) return true;
            }
            return false;
        }

        // ---------------- DR017 ----------------

        private static List<string>? ImplicitMembers(ITypeSymbol? type, CancellationToken ct)
        {
            if (type is INamedTypeSymbol nt && nt.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T) type = nt.TypeArguments[0];
            if (type is not INamedTypeSymbol e || e.TypeKind != TypeKind.Enum) return null;
            var list = new List<string>();
            foreach (var f in e.GetMembers().OfType<IFieldSymbol>())
                foreach (var r in f.DeclaringSyntaxReferences)
                    if (r.GetSyntax(ct) is EnumMemberDeclarationSyntax em && em.EqualsValue == null)
                        list.Add(f.Name);
            return list.Count > 0 ? list : null;
        }

        private static string? SerializedReason(INamedTypeSymbol type)
        {
            foreach (var a in type.GetAttributes())
            {
                var n = a.AttributeClass?.Name ?? "";
                if (n is "SerializableAttribute" or "XmlRootAttribute" or "XmlTypeAttribute" or "DataContractAttribute" or "JsonObjectAttribute" or "ProtoContractAttribute")
                    return "[" + n.Replace("Attribute", "") + "]";
            }
            foreach (var m in type.GetMembers())
                foreach (var a in m.GetAttributes())
                {
                    var n = a.AttributeClass?.Name ?? "";
                    if (n is "XmlElementAttribute" or "XmlAttributeAttribute" or "XmlIgnoreAttribute" or "JsonPropertyAttribute" or "JsonPropertyNameAttribute" or
                        "DataMemberAttribute" or "ProtoMemberAttribute" or "JsonConverterAttribute" or "JsonIgnoreAttribute" or "JsonIncludeAttribute")
                        return "members with [" + n.Replace("Attribute", "") + "]";
                }
            return Util.LooksLikeConfigName(type.Name) ? "a type named " + type.Name : null;
        }

        private static void OnMemberDeclaration(SyntaxNodeAnalysisContext ctx)
        {
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            var targets = new List<(ISymbol sym, SyntaxToken id)>();
            if (ctx.Node is PropertyDeclarationSyntax pd && model.GetDeclaredSymbol(pd, ct) is IPropertySymbol p) targets.Add((p, pd.Identifier));
            else if (ctx.Node is FieldDeclarationSyntax fd)
                foreach (var v in fd.Declaration.Variables)
                    if (model.GetDeclaredSymbol(v, ct) is IFieldSymbol f) targets.Add((f, v.Identifier));

            foreach (var (sym, id) in targets)
            {
                if (sym.IsStatic || (sym is IFieldSymbol fs && fs.IsConst)) continue;
                var type = Util.TypeOfMember(sym);
                var implicitMembers = ImplicitMembers(type, ct);
                if (implicitMembers == null) continue;
                if (sym.HasAttribute("XmlIgnore", "JsonIgnore", "NonSerialized", "IgnoreDataMember")) continue;
                if (sym.DeclaredAccessibility != Accessibility.Public &&
                    !sym.HasAttribute("DataMember", "JsonProperty", "JsonInclude", "JsonPropertyName", "ProtoMember", "XmlElement", "XmlAttribute"))
                    continue;
                var why = SerializedReason(sym.ContainingType);
                if (why == null) continue;
                var enumType = type is INamedTypeSymbol nt && nt.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T ? nt.TypeArguments[0] : type;
                ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR017, id.GetLocation(),
                    "'" + sym.Name + "' stores enum " + enumType?.Name + " in a serialized type (" + why + "), and its members " + string.Join(", ", implicitMembers) +
                    " have implicit numbers; any serializer or converter that writes or accepts numbers ties saved files to member order, so adding, removing or reordering a member silently changes what an old file means (APBL: 1 was Damage, then Destroy). Give every member an explicit value"));
            }
        }

        private static string? ConverterKind(INamedTypeSymbol? cls)
        {
            for (var t = cls?.BaseType; t != null; t = t.BaseType)
            {
                var n = t.FullName();
                if (n == "System.Text.Json.Serialization.JsonConverter`1") return "System.Text.Json JsonConverter";
                if (n == "Newtonsoft.Json.JsonConverter" || n == "Newtonsoft.Json.JsonConverter`1") return "Newtonsoft JsonConverter";
                if (n == "System.ComponentModel.TypeConverter") return "TypeConverter";
            }
            return null;
        }

        private static void OnCast(SyntaxNodeAnalysisContext ctx)
        {
            var cast = (CastExpressionSyntax)ctx.Node;
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            if (model.GetTypeInfo(cast.Type, ct).Type is not INamedTypeSymbol target || target.TypeKind != TypeKind.Enum) return;
            if (!Util.IsIntegral(model.GetTypeInfo(cast.Expression, ct).Type)) return;

            var cls = cast.FirstAncestorOrSelf<ClassDeclarationSyntax>();
            var clsSym = cls == null ? null : model.GetDeclaredSymbol(cls, ct);
            var kind = ConverterKind(clsSym);
            if (kind != null)
            {
                var implicitMembers = ImplicitMembers(target, ct);
                if (implicitMembers == null) return;
                ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR017, cast.GetLocation(),
                    kind + " " + clsSym?.Name + " accepts a number for enum " + target.Name + ", whose members " + string.Join(", ", implicitMembers) +
                    " have implicit values; a number written by an older version maps to whichever member now has that position (APBL's converter read a numeric 1, once Damage, as Destroy). Reject numbers, or give the members explicit values and map retired numbers"));
                return;
            }

            if (!FromExternalInput(cast.Expression, model, ct, out var source)) return;
            var function = Util.EnclosingFunction(cast);
            if (function != null && function.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i => Util.InvokedName(i) == "IsDefined")) return;
            ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR017, cast.GetLocation(),
                "(" + target.Name + ") casts a number read from " + source + " with no Enum.IsDefined check; an out-of-range or stale number becomes an undefined " + target.Name + " value that switch statements and comparisons silently miss"));
        }

        private static readonly Regex InputCall = new Regex("^(Parse|ToInt\\d+|ToUInt\\d+|ToByte|ToSByte|GetInt\\d+|GetUInt\\d+|GetByte|ReadInt\\d+|ReadUInt\\d+|ReadByte)$", RegexOptions.CultureInvariant);
        private static readonly Regex InputOut = new Regex("^(TryParse|TryGetInt\\d+|TryGetUInt\\d+|TryGetByte|TryGetValue|TryRead)", RegexOptions.CultureInvariant);

        private static bool FromExternalInput(ExpressionSyntax e, SemanticModel model, CancellationToken ct, out string source)
        {
            e = Util.Strip(e);
            source = "";
            if (e is InvocationExpressionSyntax inv && InputCall.IsMatch(Util.InvokedName(inv)))
            {
                source = Util.InvokedName(inv) + "()";
                return true;
            }
            if (Util.SymbolOf(e, model, ct) is not ILocalSymbol local) return false;
            foreach (var r in local.DeclaringSyntaxReferences)
            {
                switch (r.GetSyntax(ct))
                {
                    case VariableDeclaratorSyntax v when v.Initializer?.Value is ExpressionSyntax init && Util.Strip(init) is InvocationExpressionSyntax i2 && InputCall.IsMatch(Util.InvokedName(i2)):
                        source = Util.InvokedName(i2) + "()";
                        return true;
                    case SingleVariableDesignationSyntax { Parent: DeclarationExpressionSyntax { Parent: ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax call } } } when InputOut.IsMatch(Util.InvokedName(call)):
                        source = Util.InvokedName(call) + "()";
                        return true;
                }
            }
            return false;
        }

        // ---------------- DR018 ----------------

        private static string? CultureType(ITypeSymbol? t)
        {
            if (t == null) return null;
            if (t is INamedTypeSymbol nt && nt.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T) return CultureType(nt.TypeArguments[0]);
            switch (t.SpecialType)
            {
                case SpecialType.System_Double: return "double";
                case SpecialType.System_Single: return "float";
                case SpecialType.System_Decimal: return "decimal";
                case SpecialType.System_DateTime: return "DateTime";
            }
            var fn = t.FullName();
            return fn == "System.DateTimeOffset" ? "DateTimeOffset" : fn == "System.TimeSpan" ? "TimeSpan" : null;
        }

        private static bool FormatIsInvariant(string type, string? format)
        {
            if (type == "TimeSpan") return format != "g" && format != "G";
            if (type == "DateTime" || type == "DateTimeOffset") return format is "o" or "O" or "r" or "R" or "s" or "u";
            return false;
        }

        private static bool HasProvider(IMethodSymbol m) => m.Parameters.Any(p => p.Type.Is("System.IFormatProvider"));

        private static bool InLoggingCall(SyntaxNode node, SemanticModel model, CancellationToken ct)
        {
            var stop = Util.EnclosingFunction(node);
            for (var p = node.Parent; p != null && p != stop; p = p.Parent)
                if (p is ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax call } && Util.MethodOf(call, model, ct) is IMethodSymbol m && Util.IsLogging(m) && !IsDataApi(m))
                    return true;
            return false;
        }

        private static void CultureCall(SyntaxNodeAnalysisContext ctx, InvocationExpressionSyntax inv, IMethodSymbol m)
        {
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            if (m.Name == "ToString" && CultureType(m.ContainingType) is string tt && !HasProvider(m))
            {
                string? format = null;
                if (m.Parameters.Length > 0 && ArgFor(inv.ArgumentList, m, 0) is ExpressionSyntax f && model.GetConstantValue(f, ct) is { HasValue: true } c)
                    format = c.Value as string;
                if (FormatIsInvariant(tt, format) || InLoggingCall(inv, model, ct)) return;
                ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR018, inv.GetLocation(),
                    tt + ".ToString(" + (format == null ? "" : "\"" + format + "\"") + ") uses the server's current culture; written to a file, name, key or message it changes with the server's locale (comma decimal separator, other calendars) and may not read back on another server. Pass CultureInfo.InvariantCulture"));
                return;
            }
            if ((m.Name == "Parse" || m.Name == "TryParse" || m.Name == "ParseExact" || m.Name == "TryParseExact") &&
                CultureType(m.ContainingType) is string pt && pt != "TimeSpan" && !HasProvider(m))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR018, inv.GetLocation(),
                    pt + "." + m.Name + "(...) without an IFormatProvider reads with the server's culture; on a comma-decimal locale \"1.5\" parses as 15 or fails, so the same config means different values on different servers. Pass CultureInfo.InvariantCulture"));
                return;
            }
            if (m.ContainingType.Is("System.Convert") && (m.Name == "ToDouble" || m.Name == "ToSingle" || m.Name == "ToDecimal" || m.Name == "ToDateTime") &&
                m.Parameters.Length == 1 && m.Parameters[0].Type.SpecialType == SpecialType.System_String)
            {
                ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR018, inv.GetLocation(),
                    "Convert." + m.Name + "(string) reads with the server's culture; pass CultureInfo.InvariantCulture"));
                return;
            }
            if (m.Name == "Format" && m.ContainingType.SpecialType == SpecialType.System_String)
                CultureSinkForStringFormat(ctx, inv, m, inv.ArgumentList);
        }

        private static string? CultureSinkForStringFormat(SyntaxNodeAnalysisContext ctx, SyntaxNode call, IMethodSymbol m, ArgumentListSyntax args)
        {
            if (m.Name != "Format" || m.ContainingType.SpecialType != SpecialType.System_String || HasProvider(m)) return null;
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            string? culprit = null;
            foreach (var a in args.Arguments.Skip(1))
            {
                var t = CultureType(model.GetTypeInfo(a.Expression, ct).Type);
                if (t != null && t != "TimeSpan") { culprit = t + " '" + Util.Short(a.Expression, 30) + "'"; break; }
            }
            if (culprit == null || call is not ExpressionSyntax expr) return null;
            var sink = DataSink(expr, model, ct, 0);
            if (sink == null) return null;
            Report(ctx, call, "string.Format", culprit, sink);
            return sink;
        }

        private static void OnInterpolation(SyntaxNodeAnalysisContext ctx)
        {
            var interp = (InterpolatedStringExpressionSyntax)ctx.Node;
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            string? culprit = null;
            foreach (var hole in interp.Contents.OfType<InterpolationSyntax>())
            {
                var t = CultureType(model.GetTypeInfo(hole.Expression, ct).Type);
                if (t == null || FormatIsInvariant(t, hole.FormatClause?.FormatStringToken.ValueText)) continue;
                culprit = t + " '" + Util.Short(hole.Expression, 30) + "'";
                break;
            }
            if (culprit == null) return;
            var converted = model.GetTypeInfo(interp, ct).ConvertedType;
            if (converted.IsAny("System.FormattableString", "System.IFormattable")) return;
            if (interp.Parent is ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax call } && Util.MethodOf(call, model, ct) is IMethodSymbol cm && HasProvider(cm)) return;
            var sink = DataSink(interp, model, ct, 0);
            if (sink == null) return;
            Report(ctx, interp, "string interpolation", culprit, sink);
        }

        private static void OnConcat(SyntaxNodeAnalysisContext ctx)
        {
            var b = (BinaryExpressionSyntax)ctx.Node;
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            if (model.GetTypeInfo(b, ct).Type?.SpecialType != SpecialType.System_String) return;
            if (b.Parent is BinaryExpressionSyntax pb && pb.IsKind(SyntaxKind.AddExpression)) return; // only the outermost +
            var operands = new List<ExpressionSyntax>();
            Flatten(b, operands);
            string? culprit = null;
            foreach (var o in operands)
            {
                var t = CultureType(model.GetTypeInfo(o, ct).Type);
                if (t != null && t != "TimeSpan") { culprit = t + " '" + Util.Short(o, 30) + "'"; break; }
            }
            if (culprit == null) return;
            var sink = DataSink(b, model, ct, 0);
            if (sink == null) return;
            Report(ctx, b, "string concatenation", culprit, sink);

            static void Flatten(ExpressionSyntax e, List<ExpressionSyntax> into)
            {
                e = Util.Strip(e);
                if (e is BinaryExpressionSyntax be && be.IsKind(SyntaxKind.AddExpression))
                {
                    Flatten(be.Left, into);
                    Flatten(be.Right, into);
                }
                else into.Add(e);
            }
        }

        private static void Report(SyntaxNodeAnalysisContext ctx, SyntaxNode node, string kind, string culprit, string sink) =>
            ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR018, node.GetLocation(),
                kind + " formats " + culprit + " with the server's current culture, and the text goes into " + sink +
                "; a server with another locale writes different text (Monitor's history file names and numbers changed with culture). Use FormattableString.Invariant(...) or string.Create(CultureInfo.InvariantCulture, ...)"));

        private static readonly Regex DataWord = new Regex("file|path|name|key|json|xml|csv|save|write", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static bool IsDataApi(IMethodSymbol m)
        {
            var t = m.ContainingType;
            var ns = t.Ns();
            if (ns == "System.IO")
                return t.Name is "File" or "Path" or "Directory" or "FileInfo" or "DirectoryInfo" or "FileStream" or "StreamWriter" ||
                       t.InheritsFrom("System.IO.TextWriter") || t.InheritsFrom("System.IO.BinaryWriter");
            if (t.NsStarts("System.Xml")) return true;
            if (t.NsStarts("System.Text.Json") && t.Name.Contains("Writer")) return true;
            if (t.NsStarts("Newtonsoft.Json") && t.Name.Contains("Writer")) return true;
            return m.Name.StartsWith("Write", StringComparison.Ordinal) && t.Name.Contains("Writer");
        }

        private static string? DataSink(ExpressionSyntax expr, SemanticModel model, CancellationToken ct, int depth)
        {
            SyntaxNode cur = expr;
            while (cur.Parent is ParenthesizedExpressionSyntax ||
                   (cur.Parent is ConditionalExpressionSyntax ce && ce.Condition != cur) ||
                   (cur.Parent is BinaryExpressionSyntax pb && (pb.IsKind(SyntaxKind.AddExpression) || pb.IsKind(SyntaxKind.CoalesceExpression))))
                cur = cur.Parent;
            switch (cur.Parent)
            {
                case ArgumentSyntax arg:
                    {
                        var callNode = arg.Parent?.Parent;
                        var m = callNode == null ? null : Util.MethodOf(callNode, model, ct);
                        if (m?.ContainingType == null) return null;
                        if (IsDataApi(m)) return m.ContainingType.Name + "." + (m.MethodKind == MethodKind.Constructor ? "ctor" : m.Name);
                        if (HasProvider(m) || Util.IsLogging(m)) return null;
                        if (m.ContainingType.Is("System.Text.StringBuilder"))
                        {
                            var function = Util.EnclosingFunction(arg);
                            return function != null && WritesData(function, model, ct) ? "a StringBuilder this method writes to a file" : null;
                        }
                        if (DataWord.IsMatch(m.Name)) return m.Name + "(...)";
                        var p = ParamFor(arg, m);
                        return p != null && DataWord.IsMatch(p.Name) ? "parameter '" + p.Name + "' of " + m.Name : null;
                    }
                case EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax v }:
                    if (DataWord.IsMatch(v.Identifier.Text)) return "'" + v.Identifier.Text + "'";
                    if (depth == 0 && model.GetDeclaredSymbol(v, ct) is ILocalSymbol local) return FollowLocal(local, v, model, ct);
                    return null;
                case EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax pd }:
                    return DataWord.IsMatch(pd.Identifier.Text) ? "property '" + pd.Identifier.Text + "'" : null;
                case AssignmentExpressionSyntax a when a.Right == cur:
                    {
                        var target = Util.SymbolOf(a.Left, model, ct);
                        if (target == null) return null;
                        if (DataWord.IsMatch(target.Name)) return "'" + target.Name + "'";
                        if (target is ILocalSymbol l && depth == 0) return FollowLocal(l, a, model, ct);
                        if (target is IPropertySymbol tp && tp.ContainingType.NsStarts("System.Xml")) return tp.ContainingType.Name + "." + tp.Name;
                        return null;
                    }
                case ReturnStatementSyntax:
                case ArrowExpressionClauseSyntax:
                    {
                        var member = Util.EnclosingFunction(cur);
                        var name = member switch
                        {
                            MethodDeclarationSyntax md => md.Identifier.Text,
                            PropertyDeclarationSyntax pd2 => pd2.Identifier.Text,
                            LocalFunctionStatementSyntax lf => lf.Identifier.Text,
                            AccessorDeclarationSyntax { Parent.Parent: PropertyDeclarationSyntax ap } => ap.Identifier.Text,
                            _ => ""
                        };
                        return DataWord.IsMatch(name) ? "the return value of " + name : null;
                    }
            }
            return null;
        }

        private static string? FollowLocal(ILocalSymbol local, SyntaxNode decl, SemanticModel model, CancellationToken ct)
        {
            var function = Util.EnclosingFunction(decl);
            if (function == null) return null;
            foreach (var id in Util.FunctionNodes(function).OfType<IdentifierNameSyntax>())
            {
                if (id.SpanStart <= decl.SpanStart || id.Identifier.Text != local.Name) continue;
                if (!Util.Same(model.GetSymbolInfo(id, ct).Symbol, local)) continue;
                var s = DataSink(id, model, ct, 1);
                if (s != null) return s + " (through '" + local.Name + "')";
            }
            return null;
        }

        private static bool WritesData(SyntaxNode function, SemanticModel model, CancellationToken ct)
        {
            foreach (var inv in Util.FunctionNodes(function).OfType<InvocationExpressionSyntax>())
                if (Util.MethodOf(inv, model, ct) is IMethodSymbol m && m.ContainingType != null && IsDataApi(m)) return true;
            return false;
        }

        // ---------------- DR019 ----------------

        private static void TimeSpanParse(SyntaxNodeAnalysisContext ctx, InvocationExpressionSyntax inv, IMethodSymbol m)
        {
            if ((m.Name != "Parse" && m.Name != "TryParse") || !m.ContainingType.Is("System.TimeSpan")) return;
            ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR019, inv.GetLocation(),
                "TimeSpan." + m.Name + " has no format: \"24:00:00\" is read as 24 days, \"1:30\" as 1 h 30 min and \"90\" as 90 days, and the fraction separator follows the server culture. Use ParseExact with an explicit format (and InvariantCulture), or store a number of seconds"));
        }

        /// <summary>A config-derived TimeSpan passed to Task.Delay, Thread.Sleep, a timer, or assigned to Interval.</summary>
        private static void TimeSpanUse(SyntaxNodeAnalysisContext ctx, SyntaxNode call, IMethodSymbol m, ArgumentListSyntax args)
        {
            var tn = m.ContainingType.FullName();
            string? use = null;
            if (tn == "System.Threading.Tasks.Task" && m.Name == "Delay") use = "Task.Delay";
            else if (tn == "System.Threading.Thread" && m.Name == "Sleep") use = "Thread.Sleep";
            else if (tn == "System.Threading.Timer" && (m.Name == "Change" || m.MethodKind == MethodKind.Constructor)) use = m.MethodKind == MethodKind.Constructor ? "new Timer" : "Timer.Change";
            else if (tn == "System.Timers.Timer" && m.MethodKind == MethodKind.Constructor) use = "new System.Timers.Timer";
            if (use == null) return;
            foreach (var a in args.Arguments)
                if (CheckConfigTimeSpan(ctx, a.Expression, use, call)) return;
        }

        private static void OnAssignment(SyntaxNodeAnalysisContext ctx)
        {
            var a = (AssignmentExpressionSyntax)ctx.Node;
            if (a.Left is not MemberAccessExpressionSyntax ma || ma.Name.Identifier.Text != "Interval") return;
            if (Util.SymbolOf(ma, ctx.SemanticModel, ctx.CancellationToken) is not IPropertySymbol p || !p.ContainingType.Is("System.Timers.Timer")) return;
            CheckConfigTimeSpan(ctx, a.Right, "Timer.Interval", a);
        }

        private static void OnComparison(SyntaxNodeAnalysisContext ctx)
        {
            var b = (BinaryExpressionSyntax)ctx.Node;
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            if (!model.GetTypeInfo(b.Left, ct).Type.Is("System.TimeSpan") || !model.GetTypeInfo(b.Right, ct).Type.Is("System.TimeSpan")) return;
            if (IsConstantSpan(b.Left, model, ct) || IsConstantSpan(b.Right, model, ct)) return;
            if (!CheckConfigTimeSpan(ctx, b.Right, "an interval comparison", b))
                CheckConfigTimeSpan(ctx, b.Left, "an interval comparison", b);
        }

        private static bool IsConstantSpan(ExpressionSyntax e, SemanticModel model, CancellationToken ct)
        {
            e = Util.Strip(e);
            if (Util.SymbolOf(e, model, ct) is IFieldSymbol f && f.ContainingType.Is("System.TimeSpan")) return true; // Zero, MaxValue, MinValue
            return e is InvocationExpressionSyntax inv && Util.InvokedName(inv).StartsWith("From", StringComparison.Ordinal) &&
                   inv.ArgumentList.Arguments.All(a => model.GetConstantValue(a.Expression, ct).HasValue);
        }

        private static bool CheckConfigTimeSpan(SyntaxNodeAnalysisContext ctx, ExpressionSyntax value, string use, SyntaxNode usage)
        {
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            var typ = model.GetTypeInfo(value, ct).Type;
            var numeric = typ != null && (Util.IsIntegral(typ) || typ.SpecialType == SpecialType.System_Double || typ.SpecialType == SpecialType.System_Single);
            if (!typ.Is("System.TimeSpan") && !numeric) return false;
            if (model.GetConstantValue(value, ct).HasValue) return false;
            var member = ConfigMember(value, model, ct, 1);
            if (member == null) return false;
            var function = Util.EnclosingFunction(usage);
            if (function != null && Guarded(function, member, usage, model, ct)) return true;
            ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR019, value.GetLocation(),
                use + " uses '" + member.Name + "' from configuration with no check in this method that it is above zero; a blank or 0 value turns this into a run-every-tick loop (and a negative one throws). Clamp or reject values below a minimum"));
            return true;
        }

        private static ISymbol? ConfigMember(ExpressionSyntax e, SemanticModel model, CancellationToken ct, int depth)
        {
            foreach (var n in e.DescendantNodesAndSelf())
            {
                if (n is not ExpressionSyntax x || (n is not IdentifierNameSyntax && n is not MemberAccessExpressionSyntax)) continue;
                var s = Util.SymbolOf(x, model, ct);
                if (s is IFieldSymbol or IPropertySymbol)
                {
                    if (Util.LooksLikeConfigName(s.ContainingType?.Name ?? "") || Util.LooksLikeConfigName(s.Name)) return s;
                    if (x is MemberAccessExpressionSyntax ma)
                    {
                        var rs = Util.SymbolOf(ma.Expression, model, ct);
                        var rt = model.GetTypeInfo(ma.Expression, ct).Type;
                        if ((rs != null && Util.LooksLikeConfigName(rs.Name)) || (rt != null && Util.LooksLikeConfigName(rt.Name))) return s;
                    }
                }
                else if (s is ILocalSymbol local && depth > 0)
                {
                    foreach (var r in local.DeclaringSyntaxReferences)
                        if (r.GetSyntax(ct) is VariableDeclaratorSyntax v && v.Initializer != null && ConfigMember(v.Initializer.Value, model, ct, depth - 1) is ISymbol hit)
                            return hit;
                }
            }
            return null;
        }

        private static bool Guarded(SyntaxNode function, ISymbol member, SyntaxNode usage, SemanticModel model, CancellationToken ct)
        {
            foreach (var n in Util.FunctionNodes(function))
            {
                if (n == usage) continue;
                var isGuard = n switch
                {
                    BinaryExpressionSyntax b => b.Kind() is SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression or
                        SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression or SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression,
                    IsPatternExpressionSyntax => true,
                    InvocationExpressionSyntax inv => Util.InvokedName(inv) is "Max" or "Clamp",
                    _ => false
                };
                if (isGuard && References(n, member, model, ct)) return true;
            }
            return false;
        }

        /// <summary>The node mentions the member, or a local initialised from it.</summary>
        private static bool References(SyntaxNode n, ISymbol member, SemanticModel model, CancellationToken ct)
        {
            foreach (var id in n.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                var s = model.GetSymbolInfo(id, ct).Symbol;
                if (Util.Same(s, member)) return true;
                if (s is ILocalSymbol local)
                    foreach (var r in local.DeclaringSyntaxReferences)
                        if (r.GetSyntax(ct) is VariableDeclaratorSyntax v && v.Initializer != null &&
                            v.Initializer.Value.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(x => x.Identifier.Text == member.Name && Util.Same(model.GetSymbolInfo(x, ct).Symbol, member)))
                            return true;
            }
            return false;
        }

        // ---------------- DR020 ----------------

        private static void OnMemberAccess(SyntaxNodeAnalysisContext ctx)
        {
            var ma = (MemberAccessExpressionSyntax)ctx.Node;
            if (ma.Name.Identifier.Text != "Now") return;
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            if (model.GetSymbolInfo(ma, ct).Symbol is not IPropertySymbol p || !p.IsStatic || p.ContainingType.SpecialType != SpecialType.System_DateTime) return;

            var use = NowUse(ma, model, ct, true);
            if (use == null) return;
            ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR020, ma.GetLocation(),
                "DateTime.Now is " + use + "; it is local wall-clock time, so a DST change or clock adjustment moves it by an hour and durations, timeouts and stored timestamps jump or go negative. Use DateTime.UtcNow (or Stopwatch) for durations, and keep Now only for local time-of-day display or schedules"));
        }

        private static string? NowUse(ExpressionSyntax e, SemanticModel model, CancellationToken ct, bool followLocal)
        {
            SyntaxNode cur = e;
            while (cur.Parent is ParenthesizedExpressionSyntax) cur = cur.Parent;
            switch (cur.Parent)
            {
                case BinaryExpressionSyntax b when b.Kind() is SyntaxKind.AddExpression or SyntaxKind.SubtractExpression:
                    return model.GetTypeInfo(b, ct).Type?.SpecialType == SpecialType.System_String ? null : "used in arithmetic";
                case BinaryExpressionSyntax b when b.Kind() is SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression or
                    SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression or SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression:
                    return "compared";
                case MemberAccessExpressionSyntax pm when pm.Expression == cur:
                    {
                        var n = pm.Name.Identifier.Text;
                        return n.StartsWith("Add", StringComparison.Ordinal) || n == "Subtract" || n == "CompareTo" || n == "Ticks" ? "used in arithmetic (" + n + ")" : null;
                    }
                case AssignmentExpressionSyntax a when a.Right == cur:
                    {
                        var target = Util.SymbolOf(a.Left, model, ct);
                        if (target is IFieldSymbol || target is IPropertySymbol) return "stored in '" + target.Name + "'";
                        if (target is ILocalSymbol l && followLocal) return FollowNowLocal(l, a, model, ct);
                        return null;
                    }
                case EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax v }:
                    {
                        var declared = model.GetDeclaredSymbol(v, ct);
                        if (declared is IFieldSymbol) return "stored in field '" + declared.Name + "'";
                        if (declared is ILocalSymbol l && followLocal) return FollowNowLocal(l, v, model, ct);
                        return null;
                    }
                case EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax pd }:
                    return "stored in '" + pd.Identifier.Text + "'";
            }
            return null;
        }

        private static string? FollowNowLocal(ILocalSymbol local, SyntaxNode decl, SemanticModel model, CancellationToken ct)
        {
            var function = Util.EnclosingFunction(decl);
            if (function == null) return null;
            foreach (var id in Util.FunctionNodes(function).OfType<IdentifierNameSyntax>())
            {
                if (id.SpanStart <= decl.SpanStart || id.Identifier.Text != local.Name || !Util.Same(model.GetSymbolInfo(id, ct).Symbol, local)) continue;
                var use = NowUse(id, model, ct, false);
                if (use != null) return "kept in '" + local.Name + "' and " + use;
            }
            return null;
        }
    }
}
