# Host note: Magnetar

**Loads when** `projectKinds` contains `magnetar-plugin` (a hub manifest, `<PluginData>`, with server-side code), on top of the [se-plugin pack](../README.md). Skills: `se-dev-plugin-sdk` for PluginSdk (config variables, the UI layout Quasar renders, commands, lifecycle control, cluster services, logging, statistics); `se-dev-server-code` for the game; `se-dev-plugin` for other plugins' patterns.

**Stage 1:** `se-dev-plugin-review` on the CometWorks/magnetar-hub PR, preview only, never posting.

## What differs on Magnetar

- **Two runtimes from one source tree:** the loader compiles the manifest's `SourceDirectories` itself, for Legacy (net48, Windows) and Interim (net10, often Linux in a container). The csproj's settings don't apply ([loader-compile.md](../loader-compile.md)). Every finding gets a runtime tag, and tests need both targets ([tests.md](../tests.md)).
- **PluginSdk versions:** a member used from a newer PluginSdk is missing on servers with an older Magnetar. Name the minimum Magnetar version. Real: quasar-hub#18, `Logger.EntryEmitted` and `ServerCommands.Execute` (CS0117, L07).
- **Commands:**
  - `help` is reserved under every command root (REVIEW.md).
  - There is no per-command permission override, unlike Torch ([game-api.md](../game-api.md#identities-and-players)).
  - `RegisterAssembly` stops at the first exception, so one bad command module removes every module after it ([lifecycle.md](../lifecycle.md)).
- **Config:** `ConfigStorage` writes only non-default values, so a default change reaches every existing server on update (L12, [persistence.md](../persistence.md#config-stores)). A config change applies while running: removed commands must stop their work ([lifecycle.md](../lifecycle.md)).
- **Exceptions:** nothing above `Update` catches file errors ([errors.md](../errors.md)).
- **Patching:** Harmony ([harmony.md](../harmony.md)).
- **Clusters:** Magnetar nodes linked into a Quasar cluster share files and move grids between nodes (`NODE-FILE`, `NODE-STATE`, [persistence.md](../persistence.md)).

## Tests

- PluginCheck's Magnetar images (`ghcr.io/cometworks/magnetar` for Interim, `plugincheck-magnetar-legacy` for Legacy under Proton).
- Magnetar may download its companion mod MagnetarMod and send usage statistics in test servers (MasterRules 28).
