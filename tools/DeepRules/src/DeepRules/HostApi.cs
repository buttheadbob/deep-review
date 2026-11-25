using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace DeepRules
{
    /// <summary>
    /// The game's and Torch's real callback members, resolved from the compilation's references. A method is a host
    /// callback only if it overrides or implements one of them, and runs every tick only if that member is one the game
    /// calls every frame. Nothing here goes by a method's own name.
    /// </summary>
    internal sealed class HostApi
    {
        private sealed class Entry
        {
            public Entry(string? callbackPrefix, string[] perTick)
            {
                CallbackPrefix = callbackPrefix;
                PerTick = perTick;
            }

            /// <summary>Null: every method the type declares is a callback; otherwise only methods whose name starts with this.</summary>
            public string? CallbackPrefix { get; }

            /// <summary>Members the game calls every frame.</summary>
            public string[] PerTick { get; }
        }

        private static readonly (string type, string? callbackPrefix, string[] perTick)[] Known =
        {
            ("VRage.Plugins.IPlugin", null, new[] { "Update" }),
            ("VRage.Plugins.IHandleInputPlugin", null, new[] { "HandleInput" }),
            ("Torch.API.Plugins.ITorchPlugin", null, new[] { "Update" }),
            ("Torch.TorchPluginBase", null, new[] { "Update" }),
            ("VRage.Game.Components.MySessionComponentBase", null, new[] { "UpdateBeforeSimulation", "UpdateAfterSimulation", "Simulate", "Draw", "HandleInput" }),
            ("VRage.Game.Components.MyGameLogicComponent", null, new[] { "UpdateBeforeSimulation", "UpdateAfterSimulation" }),
            ("VRage.Game.Entity.EntityComponents.Interfaces.IMyGameLogicComponent", null, new[] { "UpdateBeforeSimulation", "UpdateAfterSimulation" }),
            ("VRage.Game.Entity.MyEntity", "Update", new[] { "UpdateBeforeSimulation", "UpdateAfterSimulation" }),
        };

        private readonly Dictionary<INamedTypeSymbol, Entry> _types;
        private readonly INamedTypeSymbol? _disposable;

        private HostApi(Dictionary<INamedTypeSymbol, Entry> types, INamedTypeSymbol? disposable)
        {
            _types = types;
            _disposable = disposable;
        }

        /// <summary>True when any of the game's or Torch's callback types is referenced.</summary>
        public bool Any => _types.Count > 0;

        public static HostApi Of(Compilation compilation)
        {
            var types = new Dictionary<INamedTypeSymbol, Entry>(SymbolEqualityComparer.Default);
            foreach (var (name, prefix, perTick) in Known)
                if (compilation.GetTypeByMetadataName(name) is INamedTypeSymbol t)
                    types[t] = new Entry(prefix, perTick);
            return new HostApi(types, compilation.GetTypeByMetadataName("System.IDisposable"));
        }

        /// <summary>The host member this method overrides or implements ("MySessionComponentBase.UpdateBeforeSimulation"), or null.</summary>
        public string? CallbackOf(IMethodSymbol m) => Find(m, perTickOnly: false);

        /// <summary>The every-frame host member this method overrides or implements, or null.</summary>
        public string? PerTickOf(IMethodSymbol m) => Find(m, perTickOnly: true);

        /// <summary>
        /// A Harmony patch method whose [HarmonyPatch] target (type and method name, on the method or its containing types)
        /// resolves to a method the game calls every frame; returns that target, or null.
        /// </summary>
        public string? PerTickHarmonyTarget(IMethodSymbol m)
        {
            if (!Util.IsHarmonyPatchMethod(m)) return null;
            INamedTypeSymbol? targetType = null;
            string? targetName = null;
            for (ISymbol? s = m; s != null; s = s.ContainingType)
            {
                foreach (var a in s.GetAttributes())
                {
                    if (a.AttributeClass.FullName() != "HarmonyLib.HarmonyPatch") continue;
                    foreach (var arg in a.ConstructorArguments)
                    {
                        if (arg.Kind == TypedConstantKind.Type && arg.Value is INamedTypeSymbol t) targetType ??= t;
                        else if (arg.Kind == TypedConstantKind.Primitive && arg.Value is string n) targetName ??= n;
                    }
                }
            }
            if (targetType == null || targetName == null) return null;
            for (var t = targetType; t != null; t = t.BaseType)
                foreach (var candidate in t.GetMembers(targetName).OfType<IMethodSymbol>())
                    if (PerTickOf(candidate) is string member)
                        return targetType.Name + "." + targetName + " (" + member + ")";
            return null;
        }

        private string? Find(IMethodSymbol m, bool perTickOnly)
        {
            if (_types.Count == 0 || m.MethodKind != MethodKind.Ordinary && m.MethodKind != MethodKind.ExplicitInterfaceImplementation) return null;
            m = m.OriginalDefinition;

            // overrides, up the chain (Object members are never host callbacks)
            var root = m;
            while (root.OverriddenMethod != null) root = root.OverriddenMethod;
            if (root.ContainingType?.SpecialType != SpecialType.System_Object)
            {
                for (var o = m; o != null; o = o.OverriddenMethod)
                    if (Match(o.ContainingType, o.Name, perTickOnly) is string hit) return hit;
            }

            // interface implementations (explicit or implicit)
            var type = m.ContainingType;
            if (type == null) return null;
            foreach (var iface in type.AllInterfaces)
            {
                var known = _types.ContainsKey(iface.OriginalDefinition);
                var disposeOfPlugin = !perTickOnly && _disposable != null && Util.Same(iface, _disposable) && ImplementsKnown(type);
                if (!known && !disposeOfPlugin) continue;
                foreach (var member in iface.GetMembers().OfType<IMethodSymbol>())
                {
                    if (!Util.Same(type.FindImplementationForInterfaceMember(member), m)) continue;
                    if (disposeOfPlugin) return "IDisposable.Dispose of a plugin";
                    if (Match(iface, member.Name, perTickOnly) is string hit) return hit;
                }
            }
            return null;
        }

        private bool ImplementsKnown(INamedTypeSymbol type)
        {
            foreach (var i in type.AllInterfaces)
                if (_types.ContainsKey(i.OriginalDefinition)) return true;
            for (var b = type.BaseType; b != null; b = b.BaseType)
                if (_types.ContainsKey(b.OriginalDefinition)) return true;
            return false;
        }

        private string? Match(INamedTypeSymbol? declaring, string member, bool perTickOnly)
        {
            if (declaring == null || !_types.TryGetValue(declaring.OriginalDefinition, out var e)) return null;
            if (perTickOnly ? !e.PerTick.Contains(member) : e.CallbackPrefix != null && !member.StartsWith(e.CallbackPrefix, StringComparison.Ordinal))
                return null;
            return declaring.Name + "." + member;
        }
    }
}
