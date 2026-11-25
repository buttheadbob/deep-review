using System;
using Microsoft.CodeAnalysis;

namespace DeepRules
{
    /// <summary>
    /// What a compilation is built against, read once per compilation from its referenced assembly names. Rules use it
    /// only where the thing they look for cannot exist without that reference (the game's API, Harmony's API) or to pick
    /// which lifecycle a static field lives in; never as a guess about how the code is used.
    /// </summary>
    internal sealed class HostContext
    {
        /// <summary>Space Engineers: Sandbox.Game/Common/Graphics, VRage and VRage.*, SpaceEngineers.Game/ObjectBuilders, HavokWrapper.</summary>
        private static readonly string[] GamePrefixes = { "Sandbox.", "VRage", "SpaceEngineers.", "HavokWrapper" };

        /// <summary>Harmony: the 0Harmony assembly (Lib.Harmony, HarmonyX) and older or repackaged Harmony assemblies.</summary>
        private static readonly string[] HarmonyPrefixes = { "0Harmony", "Harmony", "Lib.Harmony" };

        /// <summary>Plugin hosts, matched as the whole name or the name followed by '.': Torch, Torch.API, Torch.Server, Magnetar.Protocol.</summary>
        private static readonly string[] HostNames = { "Torch", "Magnetar" };

        /// <summary>Quasar UI plugins (Quasar.Plugin.Abstractions), which need not reference the game.</summary>
        private static readonly string[] HostPrefixes = { "Quasar.Plugin" };

        private HostContext(bool game, bool harmony, bool pluginHost)
        {
            Game = game;
            Harmony = harmony;
            PluginHost = pluginHost;
        }

        /// <summary>The game's own assemblies are referenced (MyEntities, game components, the game's plugin interface).</summary>
        public bool Game { get; }

        /// <summary>Harmony is referenced (patch attributes, Harmony ids, AccessTools).</summary>
        public bool Harmony { get; }

        /// <summary>A plugin host's API is referenced (Torch, Magnetar, Quasar).</summary>
        public bool PluginHost { get; }

        /// <summary>Game or plugin compilation: code a game or plugin host loads, unloads and reloads inside a running process.</summary>
        public bool GameOrPlugin => Game || Harmony || PluginHost;

        public static HostContext Of(Compilation compilation)
        {
            bool game = false, harmony = false, host = false;
            foreach (var identity in compilation.ReferencedAssemblyNames)
            {
                var name = identity.Name;
                game |= StartsWithAny(name, GamePrefixes);
                harmony |= StartsWithAny(name, HarmonyPrefixes);
                host |= IsAny(name, HostNames) || StartsWithAny(name, HostPrefixes);
            }
            return new HostContext(game, harmony, host);
        }

        private static bool StartsWithAny(string name, string[] prefixes)
        {
            foreach (var p in prefixes)
                if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool IsAny(string name, string[] names)
        {
            foreach (var n in names)
                if (name.Equals(n, StringComparison.OrdinalIgnoreCase) ||
                    name.Length > n.Length && name[n.Length] == '.' && name.StartsWith(n, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }
}
