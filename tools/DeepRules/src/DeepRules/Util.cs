using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DeepRules
{
    /// <summary>Symbol and syntax helpers shared by the analyzers.</summary>
    internal static class Util
    {
        // ---- symbols ----

        /// <summary>Namespace + containing types + metadata name of the original definition, e.g. System.Threading.Tasks.Task`1.</summary>
        public static string FullName(this ITypeSymbol? type)
        {
            if (type is not INamedTypeSymbol named)
                return type?.ToDisplayString() ?? "";
            var t = named.OriginalDefinition;
            var name = t.MetadataName;
            for (var c = t.ContainingType; c != null; c = c.ContainingType)
                name = c.MetadataName + "." + name;
            var ns = t.ContainingNamespace;
            return ns == null || ns.IsGlobalNamespace ? name : ns.ToDisplayString() + "." + name;
        }

        public static bool Is(this ITypeSymbol? type, string fullName) => type != null && type.FullName() == fullName;

        public static bool IsAny(this ITypeSymbol? type, params string[] fullNames)
        {
            if (type == null) return false;
            var n = type.FullName();
            foreach (var f in fullNames)
                if (n == f) return true;
            return false;
        }

        public static bool InheritsFrom(this ITypeSymbol? type, string fullName)
        {
            for (var c = type; c != null; c = c.BaseType)
                if (c.FullName() == fullName) return true;
            return false;
        }

        public static bool ImplementsOrIs(this ITypeSymbol? type, string fullName)
        {
            if (type == null) return false;
            if (type.FullName() == fullName) return true;
            foreach (var i in type.AllInterfaces)
                if (i.FullName() == fullName) return true;
            return false;
        }

        public static string Ns(this ISymbol? symbol)
        {
            var ns = symbol as INamespaceSymbol ?? symbol?.ContainingNamespace;
            return ns == null || ns.IsGlobalNamespace ? "" : ns.ToDisplayString();
        }

        public static bool NsStarts(this ISymbol? symbol, string prefix)
        {
            var ns = symbol.Ns();
            return ns == prefix || ns.StartsWith(prefix + ".", StringComparison.Ordinal);
        }

        /// <summary>True for types and members in System.* or Microsoft.* namespaces.</summary>
        public static bool IsBcl(this ISymbol? symbol) => symbol.NsStarts("System") || symbol.NsStarts("Microsoft");

        public static bool Same(ISymbol? a, ISymbol? b) =>
            a != null && b != null && SymbolEqualityComparer.Default.Equals(a.OriginalDefinition, b.OriginalDefinition);

        public static bool IsSameOrBaseOf(INamedTypeSymbol? candidateBase, INamedTypeSymbol? type)
        {
            for (var t = type; t != null; t = t.BaseType)
                if (Same(t, candidateBase)) return true;
            return false;
        }

        public static ITypeSymbol? TypeOfMember(ISymbol? s) => s switch
        {
            IFieldSymbol f => f.Type,
            IPropertySymbol p => p.Type,
            ILocalSymbol l => l.Type,
            IParameterSymbol p => p.Type,
            IEventSymbol e => e.Type,
            _ => null
        };

        public static bool HasAttribute(this ISymbol symbol, params string[] names)
        {
            foreach (var a in symbol.GetAttributes())
            {
                var n = a.AttributeClass?.Name ?? "";
                foreach (var x in names)
                    if (n == x || n == x + "Attribute") return true;
            }
            return false;
        }

        public static bool IsIntegral(ITypeSymbol? t) => t != null && t.SpecialType switch
        {
            SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_UInt16 or
            SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64 => true,
            _ => false
        };

        public static bool IsEventHandlerSignature(IMethodSymbol m) =>
            m.Parameters.Length == 2 &&
            m.Parameters[0].Type.SpecialType == SpecialType.System_Object &&
            m.Parameters[1].Type.InheritsFrom("System.EventArgs");

        /// <summary>Collections whose Add/Remove invalidate enumerators (not concurrent or immutable ones, not arrays).</summary>
        public static bool IsMutableCollection(ITypeSymbol? t)
        {
            if (t == null || t is IArrayTypeSymbol || t.SpecialType == SpecialType.System_String) return false;
            if (t.NsStarts("System.Collections.Concurrent") || t.NsStarts("System.Collections.Immutable")) return false;
            if (t.Name.StartsWith("ReadOnly", StringComparison.Ordinal) || t.Name.StartsWith("IReadOnly", StringComparison.Ordinal)) return false;
            return t.ImplementsOrIs("System.Collections.Generic.ICollection`1") || t.ImplementsOrIs("System.Collections.ICollection")
                   || t.ImplementsOrIs("System.Collections.IDictionary") || t.ImplementsOrIs("System.Collections.IList");
        }

        /// <summary>Any collection type a field could grow in (includes concurrent collections).</summary>
        public static bool IsGrowableCollection(ITypeSymbol? t)
        {
            if (t == null || t is IArrayTypeSymbol || t.SpecialType == SpecialType.System_String) return false;
            if (t.NsStarts("System.Collections.Immutable")) return false;
            if (t.Name.StartsWith("ReadOnly", StringComparison.Ordinal) || t.Name.StartsWith("IReadOnly", StringComparison.Ordinal)) return false;
            if (t.NsStarts("System.Collections.Concurrent")) return true;
            return IsMutableCollection(t);
        }

        // ---- syntax ----

        public static ExpressionSyntax Strip(ExpressionSyntax e)
        {
            while (true)
            {
                switch (e)
                {
                    case ParenthesizedExpressionSyntax p:
                        e = p.Expression;
                        continue;
                    case PostfixUnaryExpressionSyntax pu when pu.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                        e = pu.Operand;
                        continue;
                }
                return e;
            }
        }

        public static ISymbol? SymbolOf(ExpressionSyntax? e, SemanticModel model, CancellationToken ct)
        {
            if (e == null) return null;
            e = Strip(e);
            var info = model.GetSymbolInfo(e, ct);
            return info.Symbol ?? (info.CandidateSymbols.Length > 0 ? info.CandidateSymbols[0] : null);
        }

        public static IMethodSymbol? MethodOf(SyntaxNode call, SemanticModel model, CancellationToken ct)
        {
            var info = model.GetSymbolInfo(call, ct);
            return (info.Symbol ?? (info.CandidateSymbols.Length > 0 ? info.CandidateSymbols[0] : null)) as IMethodSymbol;
        }

        /// <summary>The receiver of x.M(...) or x?.M(...); null for unqualified calls.</summary>
        public static ExpressionSyntax? Receiver(InvocationExpressionSyntax inv)
        {
            switch (inv.Expression)
            {
                case MemberAccessExpressionSyntax ma:
                    return ma.Expression;
                case MemberBindingExpressionSyntax:
                    for (var p = inv.Parent; p != null; p = p.Parent)
                        if (p is ConditionalAccessExpressionSyntax ca && ca.WhenNotNull.Span.Contains(inv.Span))
                            return ca.Expression;
                    return null;
                default:
                    return null;
            }
        }

        public static string InvokedName(InvocationExpressionSyntax inv) => inv.Expression switch
        {
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
            MemberBindingExpressionSyntax mb => mb.Name.Identifier.Text,
            SimpleNameSyntax sn => sn.Identifier.Text,
            _ => ""
        };

        public static string LastName(ExpressionSyntax e) => Strip(e) switch
        {
            SimpleNameSyntax sn => sn.Identifier.Text,
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
            MemberBindingExpressionSyntax mb => mb.Name.Identifier.Text,
            _ => ""
        };

        public static bool IsNestedFunction(SyntaxNode n) => n is AnonymousFunctionExpressionSyntax || n is LocalFunctionStatementSyntax;

        private static bool IsMemberRoot(SyntaxNode n) =>
            n is BaseMethodDeclarationSyntax || n is AccessorDeclarationSyntax || n is BasePropertyDeclarationSyntax || n is BaseFieldDeclarationSyntax;

        /// <summary>The nearest lambda, local function, method, accessor, property or field that contains the node.</summary>
        public static SyntaxNode? EnclosingFunction(SyntaxNode node)
        {
            for (var p = node.Parent; p != null; p = p.Parent)
                if (IsNestedFunction(p) || IsMemberRoot(p)) return p;
            return null;
        }

        /// <summary>The nearest member (method, accessor, property, field); lambdas and local functions belong to it.</summary>
        public static SyntaxNode? EnclosingMember(SyntaxNode node)
        {
            for (var p = node.Parent; p != null; p = p.Parent)
                if (IsMemberRoot(p)) return p;
            return null;
        }

        /// <summary>Descendants of a function body, not entering nested lambdas or local functions.</summary>
        public static IEnumerable<SyntaxNode> FunctionNodes(SyntaxNode root) =>
            root.DescendantNodes(n => n == root || !IsNestedFunction(n));

        /// <summary>Descendants of a type declaration, not entering nested types.</summary>
        public static IEnumerable<SyntaxNode> TypeNodes(TypeDeclarationSyntax type) =>
            type.DescendantNodes(n => n == type || !(n is BaseTypeDeclarationSyntax || n is DelegateDeclarationSyntax));

        public static string MemberName(SyntaxNode? member) => member switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text,
            ConstructorDeclarationSyntax c => c.Identifier.Text + " constructor",
            DestructorDeclarationSyntax d => "~" + d.Identifier.Text,
            AccessorDeclarationSyntax a => (a.Parent?.Parent is PropertyDeclarationSyntax p ? p.Identifier.Text : a.Parent?.Parent is EventDeclarationSyntax e ? e.Identifier.Text : "this[]") + "." + a.Keyword.Text,
            PropertyDeclarationSyntax p => p.Identifier.Text,
            OperatorDeclarationSyntax o => "operator " + o.OperatorToken.Text,
            BaseFieldDeclarationSyntax f => f.Declaration.Variables.FirstOrDefault()?.Identifier.Text ?? "field initializer",
            LocalFunctionStatementSyntax l => l.Identifier.Text,
            _ => "member"
        };

        public static int Line(SyntaxNode n) => n.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        public static int Line(SyntaxToken t) => t.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        public static int Line(Location l) => l.GetLineSpan().StartLinePosition.Line + 1;

        public static string Short(SyntaxNode n, int max = 70)
        {
            var s = Regex.Replace(n.ToString(), @"\s+", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max - 3) + "...";
        }

        public static bool IsAsync(SyntaxNode? function) => function switch
        {
            MethodDeclarationSyntax m => m.Modifiers.Any(SyntaxKind.AsyncKeyword),
            LocalFunctionStatementSyntax l => l.Modifiers.Any(SyntaxKind.AsyncKeyword),
            AnonymousFunctionExpressionSyntax a => a.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword),
            _ => false
        };

        /// <summary>True if the node sits in the try block of a try statement (inside its function) with a catch that matches.</summary>
        public static bool InTryWithCatch(SyntaxNode node, Func<CatchClauseSyntax, bool>? catches = null)
        {
            var stop = EnclosingFunction(node);
            for (var p = node.Parent; p != null && p != stop; p = p.Parent)
                if (p is TryStatementSyntax t && t.Block.Span.Contains(node.Span) && t.Catches.Any(c => catches == null || catches(c)))
                    return true;
            return false;
        }

        public static bool InAnyTry(SyntaxNode node, SyntaxNode? stop)
        {
            for (var p = node.Parent; p != null && p != stop; p = p.Parent)
                if (p is TryStatementSyntax) return true;
            return false;
        }

        public static string CatchTypeName(CatchClauseSyntax c)
        {
            if (c.Declaration == null) return "";
            var t = c.Declaration.Type.ToString();
            var dot = t.LastIndexOf('.');
            return dot >= 0 ? t.Substring(dot + 1) : t;
        }

        public static bool IsZeroConstant(ExpressionSyntax e, SemanticModel model, CancellationToken ct)
        {
            var c = model.GetConstantValue(e, ct);
            if (c.HasValue && c.Value != null)
            {
                try { return Convert.ToDouble(c.Value, System.Globalization.CultureInfo.InvariantCulture) == 0; }
                catch (FormatException) { return false; }
                catch (InvalidCastException) { return false; }
            }
            return SymbolOf(e, model, ct) is IFieldSymbol f && f.Name == "Zero" && f.ContainingType.Is("System.TimeSpan");
        }

        // ---- logging ----

        private static readonly Regex LogTypeName = new Regex("Log($|[A-Z_]|ger|ging)", RegexOptions.CultureInvariant);

        /// <summary>
        /// A call that writes a log line: methods named Log*, Warn*, Trace*, Error, Info, Debug, Fatal, WriteLine,
        /// or any method on a type whose name contains Log/Logger.
        /// </summary>
        public static bool IsLogging(IMethodSymbol m)
        {
            var n = m.Name;
            if (n.StartsWith("Log", StringComparison.Ordinal) && (n.Length == 3 || !char.IsLower(n[3]))) return true;
            if (n.StartsWith("Warn", StringComparison.Ordinal) || n.StartsWith("Trace", StringComparison.Ordinal)) return true;
            if (n == "Error" || n == "Info" || n == "Debug" || n == "Fatal" || n == "WriteLine") return true;
            return LogTypeName.IsMatch(m.ContainingType?.Name ?? "");
        }

        // ---- plugin entry points ----

        public static bool IsResetName(string n) =>
            n.StartsWith("Dispose", StringComparison.Ordinal) || n.StartsWith("Unload", StringComparison.Ordinal) ||
            n.StartsWith("Reset", StringComparison.Ordinal) || n.StartsWith("Clear", StringComparison.Ordinal) ||
            n.StartsWith("OnSessionUnload", StringComparison.Ordinal);

        public static bool IsTeardownName(string n) =>
            IsResetName(n) || n.StartsWith("Shutdown", StringComparison.Ordinal) || n == "Close" || n == "Stop" ||
            n.StartsWith("Detach", StringComparison.Ordinal);

        public static bool IsHarmonyPatchMethod(IMethodSymbol m)
        {
            foreach (var a in m.GetAttributes())
            {
                var n = a.AttributeClass.FullName();
                if (n == "HarmonyLib.HarmonyPrefix" || n == "HarmonyLib.HarmonyPostfix" || n == "HarmonyLib.HarmonyFinalizer") return true;
            }
            if (m.Name != "Prefix" && m.Name != "Postfix" && m.Name != "Finalizer") return false;
            for (var t = m.ContainingType; t != null; t = t.ContainingType)
                if (t.GetAttributes().Any(a => a.AttributeClass.FullName() == "HarmonyLib.HarmonyPatch")) return true;
            return false;
        }

        /// <summary>Methods the game calls every frame (DR023/DR024): real host overrides, or Harmony patches on them.</summary>
        public static bool IsPerTick(IMethodSymbol m, HostApi api, out string why)
        {
            if (api.PerTickOf(m) is string member)
            {
                why = m.ContainingType.Name + "." + m.Name + " (overrides " + member + ")";
                return true;
            }
            if (api.PerTickHarmonyTarget(m) is string target)
            {
                why = "Harmony patch " + m.ContainingType.Name + "." + m.Name + " on " + target;
                return true;
            }
            why = "";
            return false;
        }

        /// <summary>
        /// A call into a logging API, identified by its declaring type: NLog, Microsoft.Extensions.Logging, Serilog, log4net,
        /// the game's MyLog, Console and System.Diagnostics Debug/Trace.
        /// </summary>
        public static bool IsKnownLogging(IMethodSymbol m)
        {
            var t = m.ContainingType;
            if (t == null) return false;
            switch (t.FullName())
            {
                case "NLog.Logger":
                case "NLog.ILogger":
                case "NLog.ILoggerBase":
                case "Microsoft.Extensions.Logging.ILogger":
                case "Microsoft.Extensions.Logging.ILogger`1":
                case "Microsoft.Extensions.Logging.LoggerExtensions":
                case "Serilog.ILogger":
                case "Serilog.Log":
                case "log4net.ILog":
                case "VRage.Utils.MyLog":
                case "System.Diagnostics.Debug":
                case "System.Diagnostics.Trace":
                    return true;
                case "System.Console":
                    return m.Name.StartsWith("Write", StringComparison.Ordinal);
            }
            return false;
        }

        // ---- config detection ----

        private static readonly Regex ConfigName = new Regex("Config|Settings|Options|Profile", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public static bool LooksLikeConfigName(string name) => ConfigName.IsMatch(name);
    }

    /// <summary>Works out whether a node runs while a lock is held (DR001, DR003, DR008).</summary>
    internal static class Locks
    {
        public static string? Context(SyntaxNode node, SemanticModel model, CancellationToken ct)
        {
            SyntaxNode child = node;
            SyntaxNode? function = null;
            for (var p = node.Parent; p != null; child = p, p = p.Parent)
            {
                if (Util.IsNestedFunction(p) || p is BaseMethodDeclarationSyntax || p is AccessorDeclarationSyntax ||
                    p is BasePropertyDeclarationSyntax || p is BaseFieldDeclarationSyntax)
                {
                    function = p;
                    break;
                }
                switch (p)
                {
                    case LockStatementSyntax ls when ls.Statement == child:
                        return "lock (" + ls.Expression + ")";
                    case UsingStatementSyntax us when us.Statement == child &&
                                                      IsAwaitedLock(us.Expression ?? us.Declaration?.Variables.FirstOrDefault()?.Initializer?.Value):
                        return "using (" + (us.Expression?.ToString() ?? us.Declaration?.ToString()) + ")";
                    case BlockSyntax block:
                        foreach (var st in block.Statements)
                        {
                            if (st.SpanStart >= child.SpanStart) break;
                            if (st is LocalDeclarationStatementSyntax ld && ld.UsingKeyword.IsKind(SyntaxKind.UsingKeyword) &&
                                ld.Declaration.Variables.Any(v => IsAwaitedLock(v.Initializer?.Value)))
                                return Util.Short(ld);
                        }
                        break;
                }
            }
            return function == null ? null : ManualRegion(node, function, model, ct);
        }

        private static bool IsAwaitedLock(ExpressionSyntax? e)
        {
            if (e is not AwaitExpressionSyntax aw) return false;
            var op = Util.Strip(aw.Expression);
            if (op is InvocationExpressionSyntax ci && ci.Expression is MemberAccessExpressionSyntax cma && cma.Name.Identifier.Text == "ConfigureAwait")
                op = cma.Expression;
            if (op is not InvocationExpressionSyntax inv) return false;
            var name = Util.InvokedName(inv);
            return name.StartsWith("Lock", StringComparison.Ordinal) || name.StartsWith("Acquire", StringComparison.Ordinal) ||
                   name.StartsWith("Enter", StringComparison.Ordinal) || name.Contains("LockAsync") ||
                   name.StartsWith("Reader", StringComparison.Ordinal) || name.StartsWith("Writer", StringComparison.Ordinal);
        }

        /// <summary>Monitor.Enter/TryEnter ... Monitor.Exit and SemaphoreSlim.Wait/WaitAsync ... Release in the same function.</summary>
        private static string? ManualRegion(SyntaxNode node, SyntaxNode function, SemanticModel model, CancellationToken ct)
        {
            List<(int pos, string key, string text)>? enters = null;
            var exits = new List<(int pos, string key)>();
            foreach (var inv in Util.FunctionNodes(function).OfType<InvocationExpressionSyntax>())
            {
                var name = Util.InvokedName(inv);
                if (name != "Enter" && name != "TryEnter" && name != "Exit" && name != "Wait" && name != "WaitAsync" && name != "Release") continue;
                var m = Util.MethodOf(inv, model, ct);
                if (m == null) continue;
                var tn = m.ContainingType.FullName();
                if (tn == "System.Threading.Monitor")
                {
                    var arg = inv.ArgumentList.Arguments.FirstOrDefault()?.Expression;
                    if (arg == null) continue;
                    var key = Key(arg, model, ct);
                    if (name == "Enter" || name == "TryEnter") (enters ??= new List<(int, string, string)>()).Add((inv.SpanStart, key, "Monitor." + name + "(" + arg + ")"));
                    else if (name == "Exit") exits.Add((inv.SpanStart, key));
                }
                else if (tn == "System.Threading.SemaphoreSlim")
                {
                    var recv = Util.Receiver(inv);
                    if (recv == null) continue;
                    var key = Key(recv, model, ct);
                    if (name == "Wait" || name == "WaitAsync") (enters ??= new List<(int, string, string)>()).Add((inv.SpanStart, key, recv + "." + name + "()"));
                    else if (name == "Release") exits.Add((inv.SpanStart, key));
                }
            }
            if (enters == null) return null;
            var pos = node.SpanStart;
            foreach (var e in enters)
            {
                if (e.pos >= pos) continue;
                var closed = exits.Any(x => x.key == e.key && x.pos > e.pos && x.pos <= pos);
                if (!closed) return "the region after " + e.text;
            }
            return null;
        }

        private static string Key(ExpressionSyntax e, SemanticModel model, CancellationToken ct)
        {
            var s = Util.SymbolOf(e, model, ct);
            return s != null ? s.Kind + ":" + s.ToDisplayString() : e.ToString();
        }
    }

    /// <summary>Calls that can run waiting continuations or callbacks inline (DR001, DR002).</summary>
    internal static class Signals
    {
        public static string? Trigger(InvocationExpressionSyntax inv, IMethodSymbol m, SemanticModel model, CancellationToken ct)
        {
            var type = m.ContainingType;
            if (type == null) return null;
            switch (type.FullName())
            {
                case "System.Threading.CancellationTokenSource":
                    if (m.Name == "Cancel" || m.Name == "Dispose") return "CancellationTokenSource." + m.Name + "()";
                    if (m.Name == "CancelAfter" && inv.ArgumentList.Arguments.Count == 1 &&
                        Util.IsZeroConstant(inv.ArgumentList.Arguments[0].Expression, model, ct))
                        return "CancellationTokenSource.CancelAfter(0)";
                    return null;
                case "System.Threading.Tasks.TaskCompletionSource`1":
                case "System.Threading.Tasks.TaskCompletionSource":
                    return m.Name.StartsWith("Set", StringComparison.Ordinal) || m.Name.StartsWith("TrySet", StringComparison.Ordinal)
                        ? "TaskCompletionSource." + m.Name + "()"
                        : null;
                case "System.Threading.ManualResetEventSlim":
                    return m.Name == "Set" ? "ManualResetEventSlim.Set()" : null;
                case "System.Threading.SemaphoreSlim":
                    return m.Name == "Release" ? "SemaphoreSlim.Release()" : null;
                case "System.Threading.Monitor":
                    return m.Name == "PulseAll" ? "Monitor.PulseAll()" : null;
                case "System.Threading.Barrier":
                    return m.Name.StartsWith("SignalAndWait", StringComparison.Ordinal) ? "Barrier.SignalAndWait()" : null;
                case "System.Threading.CountdownEvent":
                    return m.Name == "Signal" ? "CountdownEvent.Signal()" : null;
            }
            return null;
        }

        /// <summary>
        /// The receiver cannot have waiters yet (a local created in this function and not referenced since), or it is a
        /// TaskCompletionSource created with RunContinuationsAsynchronously.
        /// </summary>
        public static bool IsHarmless(InvocationExpressionSyntax inv, IMethodSymbol m, SemanticModel model, CancellationToken ct)
        {
            var recv = Util.Receiver(inv);
            var sym = Util.SymbolOf(recv, model, ct);
            if (sym == null) return false;
            if (m.ContainingType.Name == "TaskCompletionSource" && CreatedWithAsyncContinuations(sym, inv, ct)) return true;
            return sym is ILocalSymbol local && NeverHandedOut(local, inv, model, ct);
        }

        private static bool NeverHandedOut(ILocalSymbol local, InvocationExpressionSyntax inv, SemanticModel model, CancellationToken ct)
        {
            var declRef = local.DeclaringSyntaxReferences.FirstOrDefault();
            if (declRef?.GetSyntax(ct) is not VariableDeclaratorSyntax v || v.Initializer?.Value is not BaseObjectCreationExpressionSyntax)
                return false;
            var function = Util.EnclosingFunction(v);
            if (function == null) return false;
            foreach (var id in function.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (id.SpanStart <= v.SpanStart || id.SpanStart >= inv.SpanStart) continue;
                if (id.Identifier.Text != local.Name) continue;
                if (SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id, ct).Symbol, local)) return false;
            }
            return true;
        }

        private static bool CreatedWithAsyncContinuations(ISymbol sym, SyntaxNode at, CancellationToken ct)
        {
            foreach (var r in sym.DeclaringSyntaxReferences)
                if (r.GetSyntax(ct) is VariableDeclaratorSyntax v && v.Initializer != null &&
                    v.Initializer.Value.ToString().Contains("RunContinuationsAsynchronously"))
                    return true;
            var type = at.FirstAncestorOrSelf<TypeDeclarationSyntax>();
            if (type == null) return false;
            foreach (var a in type.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                if (a.Right.ToString().Contains("RunContinuationsAsynchronously") && Util.LastName(a.Left) == sym.Name)
                    return true;
            return false;
        }
    }

    /// <summary>Calls that run code the current type does not control (DR002, DR003).</summary>
    internal static class UnknownCode
    {
        private static readonly string[] Prefixes = { "Run", "Execute", "Invoke", "Handle", "Dispatch", "Raise", "Notify", "Fire", "Send", "Publish" };

        public static bool NameSuggestsCallback(string name)
        {
            foreach (var p in Prefixes)
                if (name.StartsWith(p, StringComparison.Ordinal) && (name.Length == p.Length || !char.IsLower(name[p.Length])))
                    return true;
            return name.Length > 2 && name[0] == 'O' && name[1] == 'n' && char.IsUpper(name[2]);
        }

        private static bool IsQuietBcl(INamedTypeSymbol type)
        {
            if (type.NsStarts("System.Collections") || type.NsStarts("System.Linq") || type.NsStarts("System.Text")) return true;
            switch (type.FullName())
            {
                case "System.String":
                case "System.Math":
                case "System.MathF":
                case "System.Convert":
                case "System.Threading.Tasks.Task":
                case "System.Threading.Tasks.Task`1":
                case "System.Threading.Tasks.TaskFactory":
                case "System.Threading.Tasks.TaskFactory`1":
                case "System.Threading.Interlocked":
                case "System.Threading.Volatile":
                case "System.Threading.Monitor":
                case "System.Threading.ThreadPool":
                    return true;
            }
            return false;
        }

        private static bool IsObjectMember(IMethodSymbol m) => m.Name == "ToString" || m.Name == "Equals" || m.Name == "GetHashCode";

        public static string? Reason(InvocationExpressionSyntax inv, IMethodSymbol m, SemanticModel model, INamedTypeSymbol? current, bool oneLevel, CancellationToken ct)
        {
            if (m.MethodKind == MethodKind.DelegateInvoke)
            {
                ExpressionSyntax target = inv.Expression is MemberAccessExpressionSyntax ma && ma.Name.Identifier.Text == "Invoke"
                    ? ma.Expression
                    : inv.Expression is MemberBindingExpressionSyntax ? (Util.Receiver(inv) ?? inv.Expression) : inv.Expression;
                var s = Util.SymbolOf(target, model, ct);
                return s is IEventSymbol ev ? "raises event " + ev.Name : "invokes delegate " + (s?.Name ?? Util.Short(target, 40));
            }
            var type = m.ContainingType;
            if (type == null || IsQuietBcl(type)) return null;
            var own = current != null && Util.Same(type, current);
            var dispatch = type.TypeKind == TypeKind.Interface || m.IsVirtual || m.IsAbstract || m.IsOverride;
            if (dispatch && !own && !type.IsBcl() && !IsObjectMember(m))
            {
                var kind = type.TypeKind == TypeKind.Interface ? "interface" : m.IsAbstract ? "abstract" : "virtual";
                return "calls " + kind + " member " + type.Name + "." + m.Name;
            }
            if (own && !dispatch && oneLevel)
            {
                var (scanned, inner) = ScanOwn(m, model, current, ct);
                if (inner != null) return "calls " + m.Name + "(), which " + inner;
                if (scanned) return null;
            }
            if (NameSuggestsCallback(m.Name))
                return "calls " + type.Name + "." + m.Name + ", whose name says it runs commands, handlers or callbacks";
            return null;
        }

        private static (bool scanned, string? reason) ScanOwn(IMethodSymbol m, SemanticModel model, INamedTypeSymbol? current, CancellationToken ct)
        {
            foreach (var r in m.DeclaringSyntaxReferences)
            {
                if (r.SyntaxTree != model.SyntaxTree) return (false, null);
                var decl = r.GetSyntax(ct);
                if (decl is MethodDeclarationSyntax md && md.Body == null && md.ExpressionBody == null) continue;
                foreach (var call in Util.FunctionNodes(decl).OfType<InvocationExpressionSyntax>())
                {
                    var cm = Util.MethodOf(call, model, ct);
                    if (cm == null) continue;
                    var trigger = Signals.Trigger(call, cm, model, ct);
                    var why = trigger != null
                        ? (Signals.IsHarmless(call, cm, model, ct) ? null : "calls " + trigger)
                        : Reason(call, cm, model, current, false, ct);
                    if (why != null) return (true, why);
                }
                return (true, null);
            }
            return (false, null);
        }
    }
}
