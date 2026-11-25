# Multiplayer authority and fairness

Pass P11, after [core authority-interop](../../checks/core/authority-interop.md). The baseline is se-dev-plugin-review `ReviewPolicy.md` "Multiplayer fairness". This pass applies it to every changed member that runs on a client or handles something a client sent.

- **Server authority:** for each changed feature, name the game path that enforces it on the server.
  - A client-side UI gate, a hidden button or a disabled control is not enforcement, because a modified client bypasses it.
  - For server plugins: does the server accept or rebroadcast a state or action a vanilla client couldn't cause? Trace the network message, RPC or replicated property to its validation.
- **Information:**
  - What does a player learn through the plugin that vanilla wouldn't show them in the same spot? Positions, ores, inventories, other players' GPS, hidden grids, admin data.
  - Compare with the vanilla route. Real: Ore to Auto GPS PluginHub#187. The reviewer checked that the new yield display derived nothing from the game, and that the planet/asteroid tag could be read from the marker's own position.
- **Actions:** what can a player do through the plugin that vanilla wouldn't allow? Automation is not cheating by itself. Judge it by whether it bypasses gameplay limits, ownership, access, safe zones or cooldowns.
- **Server messages to clients:** chat, notifications and HUD text must not leak other players' data, and must be rate limited (error spam in [core errors](../../checks/core/errors.md)).
- **Cross-plugin interop:**
  - Plugins that patch the same game methods, use the same UI area, or own the same state.
  - Real: Sort by Available Cargo PluginHub#190. Its reflection called private methods that Unified Storage's prefixes didn't cover, which corrupted the unified view. Its checkbox stayed visible while another plugin owned the panel (L20).
  - For each changed Harmony target, private method call or UI injection point, search the hub for plugins touching the same members (se-dev-plugin skill: download and search plugin sources). Trace the interaction ([harmony.md](harmony.md#other-plugins-on-the-same-target)).
- **Ambiguous cases:** ask the short, concrete question from ReviewPolicy.md. Never present suspicion as a finding.
