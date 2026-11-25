# Host note: Torch

**Loads when** `projectKinds` contains `torch-plugin` (a `manifest.xml` with `<PluginManifest>` next to a csproj), on top of the [se-plugin pack](../README.md). Skills: `se-dev-torch` for Torch's source, plugin API, managers, commands and UI; `se-dev-server-code` for the game.

**Stage 1:** Torch plugins are published on torchapi.com, not a hub that `se-dev-plugin-review` covers. A Torch-only repository gets the built-in `code-review` at high effort plus `security-review`. A repository that also has hub manifests gets `se-dev-plugin-review` for them.

## What differs on Torch

- **Runtime:** .NET Framework 4.8 only (`Torch (net48)`). The plugin is built from its csproj by MSBuild, not compiled from source by a loader, so the csproj's settings do apply here, unlike on Magnetar ([loader-compile.md](../loader-compile.md)). deep_tools compiles it with DeepScan from the csproj, with WPF code-behind stand-ins.
- **Commands:**
  - Torch finds command arguments by searching the whole line, so `on` in `!monitor everything on` is found inside "monitor" (Agent.md `TORCH-ARGS`). Command names are matched case by case.
  - Replies are lost when the server runs a command through `HandleCommandFromServer(string)`. Use the callback overload (REVIEW.md).
  - Torch has per-command permissions; Magnetar doesn't, so a shared command's permission level must hold on both.
  - Known host defect: the user's Torch fork has open work on an argument fault in `GetCommand` (`Torch/Commands/CommandTree.cs:109`). Check a plugin's commands with one word, several words, repeated spaces, quoted arguments and no arguments.
- **Sessions:** Torch reports session state changes (loading, loaded, unloading, unloaded) through its session manager. Read the order in Torch's source with se-dev-torch, and check that work started in one session stops before the next ([lifecycle.md](../lifecycle.md)).
- **Patching:** Torch has its own `PatchManager`; plugins use it or Harmony. Unpatch on unload either way ([harmony.md](../harmony.md)).
- **UI:** the Torch server UI is WPF. Properties bound to it and set from the game thread or the pool cross threads. Torch also runs with no UI, so nothing may depend on the UI being there.
- **Config:** Torch configs keep every value, unlike Magnetar's `ConfigStorage`. APBL's `KeepOlderDefaults` path kept old defaults for old files ([persistence.md](../persistence.md)).
- **Shared code:** a Torch project with an older `LangVersion` limits the Shared code it compiles. OreYeeter's Shared stays at C# 8 for this reason ([loader-compile.md](../loader-compile.md#preprocessor-and-build-settings)). ARCH004 flags Torch types in code the Magnetar manifest compiles.

## Tests

- PluginCheck's `plugincheck-torch-proton` image runs Torch under Proton ([loader-compile.md](../loader-compile.md#mono-and-proton)). Torch analytics stay off (MasterRules 28).
- PAM may run on the user's `E:\Torch` server and game client: that is the user's own setup. Close the game and Torch as soon as no running test needs them (MasterRules 33a).
- Torch fixes of the user's fork reach the Outbox only after a live test on a running headless Torch server, before and after; harness tests alone aren't enough.
