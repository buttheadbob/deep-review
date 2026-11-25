using System;
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
    /// <summary>DR010-DR013, DR034: static state and hooks that outlive their owner, a world or a plugin reload.</summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class LifecycleAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(Rules.DR010, Rules.DR011, Rules.DR012, Rules.DR013, Rules.DR034);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(start =>
            {
                var compilation = start.Compilation;
                var host = HostContext.Of(compilation);
                // Static state: in game and plugin code the host unloads and reloads worlds and plugins inside one process, so
                // statics must be reset on unload (DR034); elsewhere a static set from instance code is shared by every
                // instance (DR010).
                if (host.GameOrPlugin)
                    start.RegisterSyntaxNodeAction(OnType, SyntaxKind.ClassDeclaration, SyntaxKind.StructDeclaration,
                        SyntaxKind.RecordDeclaration, SyntaxKind.RecordStructDeclaration);
                else
                    start.RegisterSyntaxNodeAction(OnStaticAssignment, SyntaxKind.SimpleAssignmentExpression, SyntaxKind.CoalesceAssignmentExpression);
                // DR012: an executable owns its process, so its process-wide hooks live exactly as long as they should.
                var library = compilation.Options.OutputKind is OutputKind.DynamicallyLinkedLibrary or OutputKind.NetModule or OutputKind.WindowsRuntimeMetadata;
                var removedEvents = new Lazy<HashSet<string>>(() => NamesUsed(compilation, ct: start.CancellationToken, subtract: true));
                start.RegisterSyntaxNodeAction(c => OnSubscribe(c, library, removedEvents), SyntaxKind.AddAssignmentExpression);
                // DR013 looks for Harmony's own API, which exists only when Harmony is referenced.
                if (host.Harmony)
                {
                    var unpatches = new Lazy<HashSet<string>>(() => NamesUsed(compilation, ct: start.CancellationToken, subtract: false));
                    start.RegisterSyntaxNodeAction(c => OnInvocation(c, unpatches), SyntaxKind.InvocationExpression);
                    start.RegisterSyntaxNodeAction(OnCreation, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
                }
            });
        }

        /// <summary>
        /// Syntax only, across the whole compilation: the names of events that some -= removes from (subtract), or the names
        /// of invoked methods that start with Unpatch. A rule that finds the name anywhere stays silent.
        /// </summary>
        private static HashSet<string> NamesUsed(Compilation compilation, CancellationToken ct, bool subtract)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tree in compilation.SyntaxTrees)
            {
                foreach (var n in tree.GetRoot(ct).DescendantNodes())
                {
                    if (subtract && n is AssignmentExpressionSyntax a && a.IsKind(SyntaxKind.SubtractAssignmentExpression))
                        names.Add(Util.LastName(a.Left));
                    else if (!subtract && n is InvocationExpressionSyntax inv && Util.InvokedName(inv).StartsWith("Unpatch", StringComparison.Ordinal))
                        names.Add(Util.InvokedName(inv));
                }
            }
            return names;
        }

        // ---------------- DR010 ----------------

        private static void OnStaticAssignment(SyntaxNodeAnalysisContext ctx)
        {
            var a = (AssignmentExpressionSyntax)ctx.Node;
            var right = Util.Strip(a.Right);
            if (right.IsKind(SyntaxKind.NullLiteralExpression) || right.IsKind(SyntaxKind.DefaultLiteralExpression) || right is DefaultExpressionSyntax) return;
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            var target = Util.SymbolOf(a.Left, model, ct);
            ITypeSymbol type;
            string kind;
            switch (target)
            {
                case IFieldSymbol f when f.IsStatic && !f.IsReadOnly && !f.IsConst && !f.HasAttribute("ThreadStatic"):
                    type = f.Type;
                    kind = "static field";
                    break;
                case IPropertySymbol p when p.IsStatic && p.SetMethod != null:
                    type = p.Type;
                    kind = "static property";
                    break;
                default:
                    return;
            }
            if (!type.IsReferenceType || IsLoggerType(type) || !target.Locations.Any(l => l.IsInSource)) return;

            var member = Util.EnclosingMember(a);
            if (member == null || member is ConstructorDeclarationSyntax { } c0 && c0.Modifiers.Any(SyntaxKind.StaticKeyword)) return;
            string how;
            if (!IsStaticMember(member))
                how = "from instance member " + Util.MemberName(member);
            else if (InstanceArgument(right, model, ct) is IParameterSymbol arg)
                how = "from " + Util.MemberName(member) + " with its argument '" + arg.Name + "' (type " + arg.Type.Name + ", passed in by the caller)";
            else
                return;
            ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR010, a.GetLocation(),
                kind + " '" + target.ContainingType.Name + "." + target.Name + "' is set " + how + ": one value is shared by every instance in the process, so two clients, bots, plugins or tests overwrite each other's and each reads whichever was set last. Keep it per instance and pass it to the code that needs it"));
        }

        private static bool IsStaticMember(SyntaxNode member) => member switch
        {
            AccessorDeclarationSyntax acc => acc.Parent?.Parent is MemberDeclarationSyntax owner && owner.Modifiers.Any(SyntaxKind.StaticKeyword),
            MemberDeclarationSyntax m => m.Modifiers.Any(SyntaxKind.StaticKeyword) || m.Modifiers.Any(SyntaxKind.ConstKeyword),
            _ => true
        };

        /// <summary>A parameter of class or interface type (not string, not the implicit 'value') used in the assigned value.</summary>
        private static IParameterSymbol? InstanceArgument(ExpressionSyntax value, SemanticModel model, CancellationToken ct)
        {
            foreach (var id in value.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                if (model.GetSymbolInfo(id, ct).Symbol is IParameterSymbol p && !p.IsImplicitlyDeclared && !p.IsThis &&
                    p.Type.TypeKind is TypeKind.Class or TypeKind.Interface && p.Type.SpecialType != SpecialType.System_String)
                    return p;
            }
            return null;
        }

        private static void OnType(SyntaxNodeAnalysisContext ctx)
        {
            var typeDecl = (TypeDeclarationSyntax)ctx.Node;
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            var resetBodies = ResetBodies(typeDecl, model, ct);

            foreach (var member in typeDecl.Members)
            {
                switch (member)
                {
                    case FieldDeclarationSyntax fd when fd.Modifiers.Any(SyntaxKind.StaticKeyword) && !fd.Modifiers.Any(SyntaxKind.ConstKeyword):
                        {
                            var readOnly = fd.Modifiers.Any(SyntaxKind.ReadOnlyKeyword);
                            foreach (var v in fd.Declaration.Variables)
                            {
                                if (model.GetDeclaredSymbol(v, ct) is not IFieldSymbol f) continue;
                                var kind = Classify(f.Type, readOnly);
                                if (kind == null || IsLoggerType(f.Type) || IsReset(f, resetBodies, model, ct)) continue;
                                Report(v.Identifier.GetLocation(), kind, f.Name);
                            }
                            break;
                        }
                    case EventFieldDeclarationSyntax ef when ef.Modifiers.Any(SyntaxKind.StaticKeyword):
                        foreach (var v in ef.Declaration.Variables)
                            if (model.GetDeclaredSymbol(v, ct) is IEventSymbol e && !IsReset(e, resetBodies, model, ct))
                                Report(v.Identifier.GetLocation(), "static event", e.Name);
                        break;
                    case PropertyDeclarationSyntax pd when pd.Modifiers.Any(SyntaxKind.StaticKeyword) && IsAutoPropertyWithSetter(pd):
                        if (model.GetDeclaredSymbol(pd, ct) is IPropertySymbol p && Classify(p.Type, false) is string pk &&
                            !IsLoggerType(p.Type) && !IsReset(p, resetBodies, model, ct))
                            Report(pd.Identifier.GetLocation(), pk.Replace("field", "auto-property"), p.Name);
                        break;
                }
            }

            void Report(Location location, string kind, string name) =>
                ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR034, location,
                    kind + " '" + name + "' lives as long as the process: a world reload or plugin reload keeps its old entities, handlers and values, and this type never resets or clears it in a Dispose/Unload*/Reset*/Clear*/OnSessionUnload*/UnloadData method. Reset it on unload or make it instance state"));
        }

        private static string? Classify(ITypeSymbol type, bool readOnly)
        {
            if (!readOnly && type.IsReferenceType) return "static mutable field";
            if (readOnly && Util.IsGrowableCollection(type)) return "static readonly collection field";
            return null;
        }

        private static bool IsLoggerType(ITypeSymbol t) =>
            t.Name.EndsWith("Logger", StringComparison.Ordinal) || t.Name == "ILog" || t.Name == "Log" || t.Name == "MyLog";

        private static bool IsAutoPropertyWithSetter(PropertyDeclarationSyntax pd) =>
            pd.AccessorList != null &&
            pd.AccessorList.Accessors.All(a => a.Body == null && a.ExpressionBody == null) &&
            pd.AccessorList.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration) || a.IsKind(SyntaxKind.InitAccessorDeclaration));

        /// <summary>Bodies of reset-style methods, plus the bodies of this type's own methods they call (one level).</summary>
        private static List<SyntaxNode> ResetBodies(TypeDeclarationSyntax typeDecl, SemanticModel model, CancellationToken ct)
        {
            var bodies = new List<SyntaxNode>();
            foreach (var md in typeDecl.Members.OfType<MethodDeclarationSyntax>())
            {
                if (!Util.IsResetName(md.Identifier.Text)) continue;
                bodies.Add(md);
                foreach (var call in md.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (Util.MethodOf(call, model, ct) is not IMethodSymbol cm) continue;
                    foreach (var r in cm.DeclaringSyntaxReferences)
                        if (r.SyntaxTree == model.SyntaxTree && typeDecl.Span.Contains(r.Span))
                            bodies.Add(r.GetSyntax(ct));
                }
            }
            return bodies;
        }

        private static bool IsReset(ISymbol symbol, List<SyntaxNode> bodies, SemanticModel model, CancellationToken ct)
        {
            foreach (var body in bodies)
            {
                foreach (var n in body.DescendantNodes())
                {
                    switch (n)
                    {
                        case AssignmentExpressionSyntax a when (a.IsKind(SyntaxKind.SimpleAssignmentExpression) || a.IsKind(SyntaxKind.CoalesceAssignmentExpression)) &&
                                                               Util.LastName(a.Left) == symbol.Name && Util.Same(Util.SymbolOf(a.Left, model, ct), symbol):
                            return true;
                        case InvocationExpressionSyntax inv:
                            {
                                var name = Util.InvokedName(inv);
                                if (name != "Clear" && name != "Dispose" && name != "Reset" && !name.StartsWith("Remove", StringComparison.Ordinal) && name != "TryRemove") break;
                                if (Util.Receiver(inv) is ExpressionSyntax r && Util.LastName(r) == symbol.Name && Util.Same(Util.SymbolOf(r, model, ct), symbol)) return true;
                                break;
                            }
                    }
                }
            }
            return false;
        }

        // ---------------- DR011, DR012 ----------------

        private static readonly Dictionary<string, string[]> ProcessWideEvents = new Dictionary<string, string[]>
        {
            ["System.AppDomain"] = new[] { "AssemblyResolve", "TypeResolve", "ResourceResolve", "UnhandledException", "FirstChanceException", "ProcessExit", "DomainUnload", "AssemblyLoad", "ReflectionOnlyAssemblyResolve" },
            ["System.Threading.Tasks.TaskScheduler"] = new[] { "UnobservedTaskException" },
            ["System.Console"] = new[] { "CancelKeyPress" },
            ["System.Runtime.Loader.AssemblyLoadContext"] = new[] { "Resolving", "ResolvingUnmanagedDll", "Unloading" },
        };

        private static bool IsProcessWide(IEventSymbol ev)
        {
            var tn = ev.ContainingType.FullName();
            if (tn == "Microsoft.Win32.SystemEvents") return true;
            return ProcessWideEvents.TryGetValue(tn, out var names) && names.Contains(ev.Name);
        }

        private static void OnSubscribe(SyntaxNodeAnalysisContext ctx, bool library, Lazy<HashSet<string>> removedEvents)
        {
            var a = (AssignmentExpressionSyntax)ctx.Node;
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            if (Util.SymbolOf(a.Left, model, ct) is not IEventSymbol ev) return;
            var typeDecl = a.FirstAncestorOrSelf<TypeDeclarationSyntax>();
            if (typeDecl == null || model.GetDeclaredSymbol(typeDecl, ct) is not INamedTypeSymbol typeSym) return;

            var right = Util.Strip(a.Right);
            var lambda = right is AnonymousFunctionExpressionSyntax;
            var handler = lambda ? null : HandlerSymbol(right, model, ct);
            var hasRemove = handler != null && HasMatchingRemove(typeDecl, ev, handler, model, ct);
            var handlerText = lambda ? "a lambda" : "'" + Util.Short(right, 40) + "'";

            var staticCtor = a.FirstAncestorOrSelf<ConstructorDeclarationSyntax>() is ConstructorDeclarationSyntax c && c.Modifiers.Any(SyntaxKind.StaticKeyword);
            if (IsProcessWide(ev) || (staticCtor && ev.IsStatic))
            {
                if (hasRemove || !library || !lambda && removedEvents.Value.Contains(ev.Name)) return;
                ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR012, a.GetLocation(),
                    ev.ContainingType.Name + "." + ev.Name + " += " + handlerText + (staticCtor ? " in a static constructor" : "") +
                    " hooks the whole process from a library" + (lambda ? ", and a lambda can never be removed" : ", and nothing in this compilation removes it with -=") +
                    ": when the library is unloaded or reloaded, the old handler (and the assembly it lives in) keeps running and stays loaded alongside the new one (Atomic.FM's AssemblyResolve was never removed). Keep the handler and remove it in Dispose/Unload"));
                return;
            }

            if (!IsExternalPublisher(a.Left, ev, typeDecl, typeSym, model, ct)) return;
            if (!lambda && hasRemove) return;
            ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR011, a.GetLocation(),
                Util.Short(a.Left, 50) + " += " + handlerText + (lambda
                    ? ": a lambda subscribed directly can never be removed"
                    : ": nothing in this type removes this handler with -=") +
                "; the publisher outlives this object, so it keeps this object alive and keeps calling it after unload or reload (duplicate handlers after a reload). Keep the handler in a method or field and -= it on unload"));
        }

        private static ISymbol? HandlerSymbol(ExpressionSyntax e, SemanticModel model, CancellationToken ct)
        {
            e = Util.Strip(e);
            if (e is BaseObjectCreationExpressionSyntax oc && oc.ArgumentList?.Arguments.Count == 1)
                return HandlerSymbol(oc.ArgumentList.Arguments[0].Expression, model, ct);
            return Util.SymbolOf(e, model, ct);
        }

        private static bool HasMatchingRemove(TypeDeclarationSyntax typeDecl, IEventSymbol ev, ISymbol handler, SemanticModel model, CancellationToken ct)
        {
            foreach (var r in typeDecl.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (!r.IsKind(SyntaxKind.SubtractAssignmentExpression) || Util.LastName(r.Left) != ev.Name) continue;
                if (!Util.Same(Util.SymbolOf(r.Left, model, ct), ev)) continue;
                if (Util.Same(HandlerSymbol(r.Right, model, ct), handler)) return true;
            }
            return false;
        }

        /// <summary>A static event of another type, or an event reached through a field or property (not a local, a parameter or an owned collection).</summary>
        private static bool IsExternalPublisher(ExpressionSyntax left, IEventSymbol ev, TypeDeclarationSyntax typeDecl, INamedTypeSymbol typeSym, SemanticModel model, CancellationToken ct)
        {
            if (ev.IsStatic) return !Util.IsSameOrBaseOf(ev.ContainingType, typeSym);
            ExpressionSyntax? recv = left switch
            {
                MemberAccessExpressionSyntax ma => ma.Expression,
                _ => null
            };
            if (recv == null || recv is ThisExpressionSyntax || recv is BaseExpressionSyntax) return false;
            var rs = Util.SymbolOf(recv, model, ct);
            if (rs is not (IFieldSymbol or IPropertySymbol)) return false;
            return !IsOwned(rs, typeDecl, model, ct);
        }

        /// <summary>A field or property of this type that this type creates itself (its publisher dies with it).</summary>
        private static bool IsOwned(ISymbol member, TypeDeclarationSyntax typeDecl, SemanticModel model, CancellationToken ct)
        {
            if (!Util.Same(member.ContainingType, model.GetDeclaredSymbol(typeDecl, ct))) return false;
            foreach (var r in member.DeclaringSyntaxReferences)
            {
                var init = r.GetSyntax(ct) switch
                {
                    VariableDeclaratorSyntax v => v.Initializer?.Value,
                    PropertyDeclarationSyntax p => p.Initializer?.Value,
                    _ => null
                };
                if (init is BaseObjectCreationExpressionSyntax || init is CollectionExpressionSyntax || init is ArrayCreationExpressionSyntax) return true;
            }
            foreach (var ctor in typeDecl.Members.OfType<ConstructorDeclarationSyntax>())
                foreach (var a in ctor.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                    if (Util.Same(Util.SymbolOf(a.Left, model, ct), member) && Util.Strip(a.Right) is BaseObjectCreationExpressionSyntax)
                        return true;
            return false;
        }

        // ---------------- DR013 ----------------

        private const string HarmonyType = "HarmonyLib.Harmony";

        private static void OnCreation(SyntaxNodeAnalysisContext ctx)
        {
            var oc = (BaseObjectCreationExpressionSyntax)ctx.Node;
            var model = ctx.SemanticModel;
            if (!model.GetTypeInfo(oc, ctx.CancellationToken).Type.Is(HarmonyType)) return;
            var arg = oc.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
            if (arg == null) return;
            var c = model.GetConstantValue(arg, ctx.CancellationToken);
            if (!c.HasValue || c.Value is not string id || id.Contains(".")) return;
            ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR013, arg.GetLocation(),
                "Harmony id \"" + id + "\" is not namespaced (no '.'); another plugin that picks the same id shares its patch owner, so its UnpatchAll(id) or Unpatch calls remove these patches too. Use a reverse-domain id such as author.plugin"));
        }

        private static void OnInvocation(SyntaxNodeAnalysisContext ctx, Lazy<HashSet<string>> unpatches)
        {
            var inv = (InvocationExpressionSyntax)ctx.Node;
            var name = Util.InvokedName(inv);
            if (name != "UnpatchAll" && name != "PatchAll") return;
            var model = ctx.SemanticModel;
            var ct = ctx.CancellationToken;
            var m = Util.MethodOf(inv, model, ct);
            if (m == null || !m.ContainingType.Is(HarmonyType)) return;

            if (name == "UnpatchAll")
            {
                var arg = inv.ArgumentList.Arguments.FirstOrDefault()?.Expression;
                var everything = arg == null || arg.IsKind(SyntaxKind.NullLiteralExpression);
                ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR013, inv.GetLocation(),
                    (everything
                        ? "Harmony.UnpatchAll() with no id removes every Harmony patch in the process, including other plugins' and the loader's"
                        : "Harmony.UnpatchAll(" + Util.Short((SyntaxNode?)arg ?? inv, 40) + ") removes every patch registered under that id, including patches from other instances or plugins that reuse the id (DLSS plugin Dispose called harmony.UnpatchAll(Name))") +
                    ". Use harmony.UnpatchSelf() or Unpatch(original, ...) for the methods this plugin patched"));
                return;
            }

            // PatchAll with no Unpatch call anywhere in the compilation: nothing ever removes these patches
            if (unpatches.Value.Count > 0) return;
            ctx.ReportDiagnostic(Diagnostic.Create(Rules.DR013, inv.GetLocation(),
                "Harmony.PatchAll() and nothing in this compilation calls UnpatchSelf/Unpatch: the patches stay applied after the plugin unloads, and a reload patches the same methods a second time. Call harmony.UnpatchSelf() on unload"));
        }
    }
}
