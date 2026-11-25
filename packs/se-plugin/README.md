# Pack: se-plugin (Space Engineers 1 plugins)

**Loads when** gate.json's `projectKinds` contains `magnetar-plugin`, `torch-plugin`, `quasar-plugin` or `pulsar-plugin`. Also load the host note for each of those kinds: [magnetar](magnetar/README.md), [torch](torch/README.md), [quasar](quasar/README.md), [pulsar](pulsar/README.md). A repository with several hosts (SensEssentials ships Torch, Magnetar and Quasar from one Shared folder) loads every matching note. A Quasar plugin also loads [dotnet-app](../dotnet-app/README.md), for its ASP.NET Core and Blazor checks.

**What it adds:** the game thread, the hub loaders' compile on every runtime, a plugin's life inside a world, the game API, Harmony, multiplayer fairness and cross-plugin interop, commands and permissions, the 60-tick budget, PluginCheck's live tests, and hub manifests and pins. This is the content the old `se-dev-plugin-deep-review` skill carried beyond the core, so a plugin review with this pack loaded is at least as deep as before.

**Stage 1:** for a plugin with a hub manifest (`magnetar-plugin`, `pulsar-plugin`, `quasar-plugin`), `se-dev-plugin-review` in preview only, never posting, plus PluginCheck `REVIEW.md` and a full PluginCheck run. A Torch-only plugin has no hub PR, so its stage 1 is the built-in `code-review` at high effort plus `security-review` ([Report.md](../../Report.md#the-stage-1-record)).

## Pass map

For each pass, read the core file first, then these.

| Pass | Files |
|---|---|
| P0 | [hub-release.md](hub-release.md) Manifest (targets, runtimes, pins); se-dev-plugin-review `ReviewPaths.md` |
| P1 | DeepScan's loader compile and `buildComparison` in `tools-report.json`; [loader-compile.md](loader-compile.md) |
| P2 | entry points: per-tick methods, game events, Harmony patches, commands, timers, game-thread queue (deep_tools `entryPoints[]`) |
| P3 | [threading.md](threading.md) |
| P4 | [loader-compile.md](loader-compile.md), each host note |
| P5 | [lifecycle.md](lifecycle.md), [harmony.md](harmony.md) |
| P6 | [game-api.md](game-api.md), [harmony.md](harmony.md) |
| P7 | [errors.md](errors.md) |
| P8 | [persistence.md](persistence.md) |
| P9 | [inputs.md](inputs.md) |
| P10 | [security.md](security.md), [harmony.md](harmony.md#patches-on-checks) |
| P11 | [multiplayer.md](multiplayer.md), [harmony.md](harmony.md#other-plugins-on-the-same-target) |
| P12 | [performance.md](performance.md) |
| P13 | core only; [game-api.md](game-api.md#timing) for ticks and units |
| P14 | [hub-release.md](hub-release.md) |
| P15 | [tests.md](tests.md) |
| P16 | Lessons rows with Pack `se-plugin` (and `se-plugin/<host>` for loaded hosts), plus every row of PluginCheck REVIEW.md "Common defects" (L50) |
| P18 | runtime-matrix harnesses with a `GameThread` stand-in; PluginCheck scenarios requested through the user |

## Skills

Read game behaviour from the decompiled game, never from memory:
- `se-dev-server-code` for server plugins and `se-dev-game-code` for clients (the `*-book` skills first, if installed). Don't answer a server question from client code.
- `se-dev-plugin` for plugin patterns, and to download and search other hub plugins' sources (interop).
- `se-dev-plugin-sdk` for Magnetar config, commands and the UI layout Quasar renders.
- `se-dev-torch` for Torch, `se-dev-quasar-plugin` and `quasar-book` for Quasar UI plugins.
- `se-dev-plugin-guide` for templates, how players' PCs differ, and hub submission.

List the skills you used, and any `not run`, in the report.

## Baselines

- se-dev-plugin-review: `ReviewPolicy.md` (security, multiplayer fairness, filesystem, compatibility, performance) and `ReviewPaths.md`.
- PluginCheck: `REVIEW.md` ("Common defects", "Security", "Supply chain", "Legal", "Copied code", "Loader facts", "Clusters and network mounts", "Test the UI by hand") and `Agent.md` (`TORCH-ARGS`, reading every reply).
- MasterRules 22 and 23: C# `LangVersion` latest, never preview, no `null!`; commands are Admin or player level only, and authority flows down.

## Runtime tags

`Legacy (net48)`, `Interim (net10)`, `Torch (net48)`, `Pulsar CLR`, `Pulsar CoreCLR`, `Pulsar Mono`, `Quasar`, `All`. Runtime-specific defects are at least Medium when the plugin ships on that runtime.

## Machine

The user manages PluginCheck. Ask them, through the PluginCheck session, for any PluginCheck run or scenario. Plugins never run outside the containers; the one exception is PAM on the user's `E:\Torch`.
