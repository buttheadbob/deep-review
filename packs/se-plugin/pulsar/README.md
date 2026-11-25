# Host note: Pulsar client plugins

**Loads when** `projectKinds` contains `pulsar-plugin` (a hub manifest whose code is client-side: `Sandbox.Graphics.GUI`, `VRage.Input`, `<Runtimes>`), on top of the [se-plugin pack](../README.md). Skills: `se-dev-game-code` for the client; `se-dev-plugin` for other plugins (and to find plugins patching the same members); `se-dev-plugin-guide` for how players' PCs differ.

**Stage 1:** `se-dev-plugin-review` on the StarCpt/PluginHub PR, preview only, never posting.

## What differs on Pulsar

- **Runtimes:** CLR (net48), Mono and CoreCLR, as the manifest's `<Runtimes>` declares. `CLR;Mono` hides the plugin from CoreCLR players (L33). Pulsar compiles from source with `LanguageVersion.CSharp14` and the `PULSAR` symbol ([loader-compile.md](../loader-compile.md)).
- **Exceptions stop the game loop:** `PluginInstance.Update` has no try/catch around `plugin.Update()` (L34, [errors.md](../errors.md)).
- **No authority:** a client plugin can't enforce anything on the server. Apply ReviewPolicy.md "Multiplayer fairness" to every feature: what the player learns and what the player can do that vanilla wouldn't allow ([multiplayer.md](../multiplayer.md)).
- **The client UI:**
  - Hotkeys ignored while chat, a terminal or a text field has focus (L31).
  - GUI screens closed on unload; reconnect re-runs world load in the same process ([lifecycle.md](../lifecycle.md)).
  - Layout that survives localization (L24), and controls hidden while another plugin owns the panel (L20).
  - Game state the plugin changed (volume, settings) restored on close. Real: DoomForSe restored `MyAudio.Static.VolumeMusic`.
- **Players' PCs differ:** GPU vendor and driver, OS (some players run the game through Proton on Linux), other plugins and mods. A patch for one kind of hardware applies only there, and fails closed (L40, [harmony.md](../harmony.md)).
- **Manifest:** description at most 1000 characters with even indentation (L19); `<AssetFolder>` ignored by Pulsar 2.4+ and `<SoftDependencyIds>` not a Pulsar element (L37); single-file assets stored without their extension (L15) ([hub-release.md](../hub-release.md)).

## Tests

- **Hub-style install** with one folder Asset before a hub PR, not a dev folder ([tests.md](../tests.md#hub-style-install)).
- **Isolated storage:** DoomForSe's live tests run with `-appdata .test\AppData`, so the user's real storage is never touched; a debug-only self-test drives the game itself.
- deep_tools' known gap: there is no local Pulsar `Libraries` folder, so Harmony and friends resolve from the game folder only.
