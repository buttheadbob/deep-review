# The hub loaders' compile and runtimes

Pass P4, after [core runtime-differences](../../checks/core/runtime-differences.md), which holds the .NET differences themselves (inline continuations, hashing, ICU, number formatting, encoding, paths, the net48-missing API list). This file says which runtimes a plugin meets, and how the loaders compile it.

## Which runtime each host uses

- **Magnetar** compiles a server plugin from source twice. Legacy uses .NET Framework 4.8 on Windows. Interim uses .NET 10, often on Linux in a container. Both compile the same sources.
- **Torch** is net48 only, built from the csproj.
- **Pulsar** clients run on CLR (net48), and also on Mono or CoreCLR, depending on the manifest's `Runtimes`.
- **Quasar** is .NET 10 on Linux. A Quasar plugin's companion (`netstandard2.0`) runs inside Magnetar, so it meets both Magnetar runtimes.

Tag every finding with its runtime scope: `Legacy (net48)`, `Interim (net10)`, `Torch (net48)`, `Pulsar CLR`, `Pulsar CoreCLR`, `Pulsar Mono`, `Quasar`, or `All`.

## Compile errors on one runtime

- **net48-only APIs:** Magnetar compiles the same sources for both runtimes, so using an API from the core's net48-missing list is a Legacy compile error. Pulsar's CLR build fails the same way. DeepScan's loader compile shows each one as CS0117/CS1061/CS0246 on the net48 run. Read every net48-only compile error.
- **Language version:** Pulsar compiles with `LanguageVersion.CSharp14`. Read the Magnetar Interim and Legacy compilers in `Public\magnetar` for their exact settings. A feature the language accepts can still need a runtime type that net48 lacks.
- **References the loader gives:** the loader resolves the game's assemblies, a fixed list of root names (`Microsoft.CSharp 0Harmony Newtonsoft.Json Mono.Cecil NLog PluginSdk System.Text.Json`) and everything they reference, and silently skips names it can't find. A framework assembly that no game DLL references is not available to the plugin, even though the IDE build had it ([tools/README.md](../../tools/README.md), Loader facts).
- **Serialization:** `System.Text.Json` comes on net48 only as a dependency that the host supplies (APBL #170 used it via PluginSdk). Confirm the loader supplies it on both runtimes before relying on it.
- **Newtonsoft.Json:** Interim's copy lacks `JToken.ToString(Formatting)` (from REVIEW.md). Method-exists-at-build-but-not-at-run defects show up only on a real server.
- **`buildComparison`:** `onlyLoaderCompile` lists what the hub compile shows and the IDE build hides. Read every entry.

## Preprocessor and build settings

- **Symbols:** the hub loaders compile with their own preprocessor symbols: `TRACE`, plus `NETCOREAPP` (Interim) or `NETFRAMEWORK` (Legacy), plus `MAGNETAR` (Pulsar: `PULSAR`). There is no `DEBUG` and no `LOCAL_BUILD`, and no `NET`, `NET10_0_OR_GREATER` or `NET48` symbol either, so `#if NET48` is never true in a hub build. Code under `#if DEBUG`/`#if LOCAL_BUILD` never ships, and code only in the `#else` branch is never run locally. List every region, and say what differs. (DR029)
- **Versions:** the version shown in game comes from the `AssemblyVersion`/`AssemblyFileVersion` attributes, not `Version.Build.props`. Both must be bumped (se-dev-plugin skill). `deep_tools.py` compares them.
- **Host assemblies:** the loader compiles against the host's assemblies, which can be a different version from the IDE's. A member present in a newer SDK can be missing on servers with an older Magnetar. Real: quasar-hub#18, where an older PluginSdk lacked `Logger.EntryEmitted`/`ServerCommands.Execute` (CS0117), and hid a member with CS0108 (L07). Name the minimum host version.
- **Shared code across hosts:** Shared code compiled by a host with an older language version keeps to that version. OreYeeter's Shared stays at C# 8 at most because its Torch project is limited to it, and has no Torch, NLog, Harmony or PluginSdk references. Architecture rules ARCH001-ARCH004 flag host types in Shared code, client types in server code, game types in Quasar UI code and Torch types in Magnetar-compiled code.

<a id="nullable-in-the-loader-compile-dr030"></a>
## Nullable in the loader compile (DR030)

- **csproj settings don't apply:** the csproj's `<Nullable>`, `<NoWarn>`, `<LangVersion>`, analyzers and `<DefineConstants>` do not apply to the hub compile, which has nullable disabled.
- **`?` without a directive:** `?` on a reference type in a file with no `#nullable enable` (or `#nullable enable annotations`) gives CS8632 in the hub's compile log. `!` does nothing there.
- **Fix:** add the directive per file, or drop the annotations. Real case: PluginHub#190, 42 warnings (L21).

## Mono and Proton

PluginCheck runs Legacy and Torch under Proton. A failure that only shows there may be a Proton or Wine Mono difference. Confirm on Windows .NET Framework (the runtime-matrix harness runs natively) before reporting it as a Legacy defect.

The reverse matters as well. The hub reviewer reproduced the SensEssentials re-entrancy on Mono 6.8, because its TPL is the .NET Framework reference source. When Windows isn't available, Mono is a good stand-in for net48 TPL behaviour.

## How to confirm

- **net48-only compile errors:** quote DeepScan's diagnostic with its runtime.
- **Behaviour differences:** use a runtime-matrix harness, with the output from each runtime in the finding.
- **Tests on net48:** no container runs net48 tests here, so a Legacy or Torch plugin with net48 tests always has that gap ([tests.md](tests.md)).
