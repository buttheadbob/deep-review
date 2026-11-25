# Deep review tools

The automated half of the `deep-review` skill, and the gate every project passes before it may take a
machine slot. For a plugin it compiles the code the way the hub loaders do, on every runtime it ships on; for any .NET
repository it builds every project on every target framework it declares; it runs every analyzer pack plus DeepRules and a
checker for every other language in the tree, compares a head against a base, maps the diff to members and their callers,
and writes one report. In gate mode it gives a single PASS/FAIL verdict (the record format is in `GATE-RECORD.md`).

Everything is headless: no prompts, no windows, all output to files.

| Part | What it is |
|---|---|
| `deep_tools.py` | Orchestrator (Python 3.11+, standard library only) |
| `DeepScan/` | Loader-faithful Roslyn compiler and analyzer host (C#, net10.0) |
| `Whitelist/` | `DeepWhitelist`: the game's script whitelist, built and checked by the game's own code (C#, net48, see [Script whitelist](#script-whitelist)) |
| `analyzers/` | Third-party analyzer pack (`AnalyzerPack.csproj`), the rule set (`deep.globalconfig`, `RULESET.md`) and `BannedSymbols.txt` |
| `bin/analyzers/` | Custom analyzers: `DeepRules.dll` (built by the DeepRules project, not here). Every DLL in this folder is loaded. |
| `runtime-matrix/` | net48 vs net10 harness template, `run_matrix.py`, and the worked example `examples/cancel-reentrancy` |
| `linters/` | pinned JS/TS linters (`js/`, run with node, never fetched at run time) and `pssa-run.ps1` (PSScriptAnalyzer one rule at a time) |
| `bin/gitleaks/` | gitleaks (secrets scan) |
| `tests/` | `test_check_record.py` (the record checker) and `test_helpers.py` (MDK detection, compiles[] evaluation, whitelist/test gate handling, host test environment): `python -I -B -m unittest discover -s tools/tests -v` |
| `GATE-RECORD.md` | the gate record, schema 1 (frozen): fields, consumer check, `contentSha256` |
| `cache/` | Build inputs only (see [Caches](#caches)). Never results. |

## Prerequisites

- Windows with the .NET 10 SDK (`dotnet`) and .NET Framework 4.8 (for the runtime matrix's net48 run).
- Python 3.11 or newer.
- Docker Desktop with these images already present (nothing is pulled or built):
  `ghcr.io/cometworks/magnetar:latest` (Interim libraries, PluginSdk, .NET 10 runtime),
  `plugincheck-magnetar-legacy:latest` (MagnetarLegacy libraries), `plugincheck-torch-proton:latest` (Torch DLLs),
  `ghcr.io/cometworks/quasar:latest` (Quasar.Plugin.Abstractions, Magnetar.Protocol), `mcr.microsoft.com/dotnet/sdk:10.0`
  (every build, restore and test of the plugin's own projects). The dedicated server comes from `SE_SERVER_ROOT`,
  `--ds-dir`, a Steam library, or the local `plugincheck-ds-magnetar` volume (copied read-only).
- Network access to nuget.org for restores (analyzer pack, manifest NuGetReferences, the container restores).
- Other languages (each used only when present): ruff, mypy, bandit and pytest on PATH (uv tools); the PowerShell module
  PSScriptAnalyzer; node with `tools/linters/js` installed (ESLint, typescript-eslint, TypeScript); gitleaks in
  `tools/bin/gitleaks`. shellcheck if it is ever installed. Nothing is downloaded by the tools.

## Running it

```bash
# Review a commit range (the usual hub-PR case)
python deep_tools.py --repo <repo> --head <sha> --base <sha> --out <dir>

# Review uncommitted work: the working tree (git ls-files -co --exclude-standard) against a base
python deep_tools.py --repo <repo> --head WORKTREE --base <sha> --out <dir>

# Gate: one verdict, exit 0 PASS / 3 FAIL / 1 could not run
python deep_tools.py --repo <repo> --head <sha|WORKTREE> --gate --nonce <nonce> --out <dir>

# Gate every project of a list ([{name, repo, head, base?}]) or every active project of MasterProjects.json (read-only)
python deep_tools.py --all <projects.json|MasterProjects.json> [--include-watch] [--exclude <name>]... --nonce <nonce> --out <dir>

# A folder that is not a git repository (scripts, a website): language checkers, suppressions and secrets only.
# Its record has head DIR and never qualifies for a slot.
python deep_tools.py --lint-dir <dir> [--gate] --out <dir>

# Reference consumer check of a gate record (GATE-RECORD.md checks 1 and 3-10): exit 0 passes, 3 fails with reasons
python deep_tools.py --check-record <gate.json> --nonce <n> --nonce-issued <UTC> --repo <repo> --head <sha|WORKTREE>

python deep_tools.py --version    # prints toolsVersion (sha256 of the tools, central configs, analyzers and checker versions)
python deep_tools.py --ruleset    # regenerates analyzers/RULESET.md from the central configs
```

Options: `--manifest <xml>` (extra hub manifests, repeatable, e.g. the hub repo's copy), `--affinity-mask 0x00FFFFFF`
(cores for this process and every child; Docker gets the same set as `--cpuset-cpus`), `--ds-dir`, `--emit` (DLL+PDB per
target for IL tools), and `--skip-tests/--skip-vuln/--skip-csproj-build/--skip-optional` for quick report runs (in gate
mode each skip is a gap, so the gate fails).

`--all` on MasterProjects.json takes only `active` projects (plus `watch` with `--include-watch`) that have a local git
repository, reads the file read-only, and gates each one's working tree. Leave out projects the user has ruled out of
testing with `--exclude` (for example WeaponCore). `all-excludes.json` holds the standing exclusions (`exclude`) and the
projects the user exempted from the gate under MasterRules 29a (`exempt`: Flow, GridGenerator); both are listed as
`SKIPPED` with the reason (`excluded: ...` or `exempt: ...`). `--rebuild-deepscan` with `--all` rebuilds DeepScan once
before the first project.

Machine rules it follows: telemetry variables on every dotnet call, process affinity pinned before any child starts,
`-m:4` builds, `dotnet build-server shutdown` at the end, read-only git only (`rev-parse archive diff ls-files show log
merge-base cat-file`, with `--no-optional-locks`), Docker limited to `image ls/inspect`, `volume ls`, `create`+`cp`+`rm` of a
never-started container, and `run --rm` of local images. .NET project code is never built, restored or tested
on the host (the SDK container does it). Python tests (pytest) run on the host as instructed, in the copied tree only,
pinned to the same cores, with no cache or bytecode written and a 30-minute limit.

## Project kinds

The kind is detected from the tree and written to the record as `projectKind` (the first) and `projectKinds` (all):

| projectKind | Detected by | What runs |
|---|---|---|
| `magnetar-plugin` | a hub manifest (`<PluginData>`) with SourceDirectories and server-side code | DeepScan as Magnetar compiles it (net10 Interim, net48 Legacy) + csproj build of every project |
| `pulsar-plugin` | a hub manifest whose code is client-side (`Sandbox.Graphics.GUI`, `VRage.Input`, `<Runtimes>`) | DeepScan as Pulsar compiles it + csproj build |
| `torch-plugin` | `manifest.xml` with `<PluginManifest>` next to a csproj | DeepScan of the csproj (net48, WPF stand-ins) + csproj build |
| `quasar-plugin` | a hub manifest with `<PluginKind>QuasarUiPlugin` | DeepScan of the ProjectPath with the Razor generator, companions on net48 + csproj build |
| `se-mod` | a `Data/Scripts` folder with `.cs` files | DeepScan as the game compiles mod scripts (C# 6, game assemblies) + the game's ModAPI whitelist check, per `Data/Scripts/<folder>` |
| `pb-script` | an MDK programmable block project: a `*.mdk.ini` with `type=programmableblock`, or (no ini) a csproj referencing `Mal.Mdk2.Pb*` or MDK1. `Mal.Mdk2.ModAnalyzers` projects are mods | DeepScan as the game compiles PB scripts (C# 6) + the game's Ingame whitelist check |
| `dotnet` | any other `.sln`/`.slnx`/`.csproj` (game references or not) | csproj build of every project on every declared TFM, with the analyzer pack and DeepRules |
| `non-dotnet` | no .NET project | the language checkers and the secrets scan |

Every repository, whatever its kind, also gets the language inventory, the language checkers, the suppression scan and
gitleaks.

## Languages

Every source language in the tree is listed by extension (C#, Razor, VB, F#, Python, PowerShell, JavaScript,
TypeScript, Shell, Batch, C, C++, Go, Rust, Java, Kotlin, Lua, SQL, Ruby, PHP, Swift, HLSL; markup and data such as XML,
JSON, HTML, CSS, XAML and YAML are not languages here; `node_modules`, `vendor` and `third_party` folders are left out).
**A language that no checker covered is a gap, so the gate fails and names the language and its file count. A project
in an unchecked language never passes.**

| Language | Checkers (central config in `analyzers/`) | Category |
|---|---|---|
| C#, Razor | DeepScan and the csproj build: every analyzer pack, DeepRules, `deep.globalconfig`, `BannedSymbols.txt` | compile, analyzer, banned-api, deeprules, architecture |
| Python | ruff `ALL` (`deep-ruff.toml`), mypy `--strict` (`deep-mypy.ini`), bandit every test (`deep-bandit.yaml`), pytest when the repo has tests | python |
| PowerShell | PSScriptAnalyzer, every rule at every severity, one rule per call (`deep-psscriptanalyzer.psd1`, `linters/pssa-run.ps1`) | powershell |
| JavaScript, TypeScript | ESLint: `@eslint/js` recommended, typescript-eslint `strictTypeChecked` (`deep-eslint.config.mjs`) | js |
| Shell | shellcheck when installed (not installed today: a gap) | shell |
| Batch and the rest | no checker: always a gap | - |
| (all files) | gitleaks with `--redact` over the tree under review, and `gitleaks git` over base..head when a base is given | secret |

The same central-config rule holds for every checker: everything runs, the central configs are the only place a rule is
turned off (each with a `# reason:`, listed in `analyzers/RULESET.md`), and the repository's own ruff, mypy, bandit,
PSScriptAnalyzer, ESLint and gitleaks configs are never used. Inline `# noqa`, `# type: ignore`, `# nosec`,
`eslint-disable`, `@ts-ignore`, PowerShell `SuppressMessage`, `# shellcheck disable` and `gitleaks:allow` are suppression
issues (ruff runs with `--ignore-noqa`, bandit with `--ignore-nosec`, ESLint with inline config off, so the hidden findings
are reported too). A missing mypy stub, a Python project with no tests, a checker that failed and a skipped stage are gaps.
Only the code under review is scanned: the git-tracked files at the commit, or for WORKTREE the tracked files plus
untracked files that are not ignored; never ignored runtime folders. Nothing is written into the repository (ruff
`--no-cache`, the mypy cache under the out folder, `PYTHONDONTWRITEBYTECODE=1`, pytest `-p no:cacheprovider`).

## What a run does

1. Builds inputs if missing: restores the analyzer pack into `cache/nuget/packages` and copies each package's C# analyzer
   DLLs into `cache/analyzers/<package>/` (the newest `roslynX.Y` variant the host Roslyn supports); probes
   SecurityCodeScan and drops it if it does not load cleanly; builds DeepScan into `cache/deepscan/<source hash>/`.
2. Gets the head tree (`git archive <sha>` into `<out>/work/trees/head`, or the working tree) and the base tree.
3. Finds targets: hub manifests (`<PluginData>`): Magnetar server plugins (net10 Interim + net48 Legacy), Pulsar client
   plugins (`<Runtimes>`), Quasar UI plugins (`<ProjectPath>`, net10) and their companions from `quasar-plugin.json`;
   Torch plugins (`manifest.xml` + csproj, net48). A repo with no manifest is a generic .NET repo: its own projects are
   built and analysed. A repo with no .NET project is reported as such (Python linting is not covered).
4. Resolves references and records each path and where it came from.
5. Runs DeepScan per target x runtime on head (and base), with every analyzer folder, `deep.globalconfig` and
   `BannedSymbols.txt`.
6. Classifies: NEW / FIXED / EXISTING against base (rule + file + normalised line text, renames followed); scope from
   `git diff --unified=0 base head` (or the working tree): in-diff-line, in-changed-member, in-affected-member (callers up
   to entry points, callees one level down), elsewhere.
7. Filters: ClrHeapAllocationAnalyzer results are kept only in per-tick members (Update*/Draw*/HandleInput/Simulate*/
   *Tick*, Harmony patches on them) and their direct callees; net10-only API suggestions on code that also ships on net48
   are dropped; MA0182 (unused internal type) results on compiler polyfills are dropped: `IsExternalInit`,
   `RequiredMemberAttribute`, `CompilerFeatureRequiredAttribute` (System.Runtime.CompilerServices) and
   `SetsRequiredMembersAttribute` (System.Diagnostics.CodeAnalysis), which the compiler needs on netstandard2.0/net48 and
   references itself, recognised only by exact full name, `internal`, at the top level of that namespace and with the real
   type's shape (no other members, modifiers, attributes or bases; see `POLYFILLS` and `checks/core/runtime-differences.md`).
   All three counts are reported, and each dropped polyfill is listed in `compilerPolyfills[]`.
8. Architecture rules from DeepScan's type references (ARCH001-004, below).
9. Container stages in the SDK image: tests per declared TFM (net48 test projects are built there and run on this PC, see
   [net48 tests](#net48-tests)),
   coverage when coverlet is referenced, `dotnet list package --vulnerable --include-transitive` per project (plus
   `packages.config` and manifest NuGetReferences), and the csproj-build cross-check.
10. Release consistency, suppressions in the code under review, optional tools, then the reports.

### Loader facts DeepScan mirrors

From `magnetar` (`Pulsar/Compiler/RoslynCompiler.cs`, `RoslynReferences.cs`, `PublicizedAssemblies.cs`, `Publicizer.cs`,
`Pulsar/Shared/Compiler/CompilerFactory.cs`, `Pulsar/Shared/Data/GitHubPlugin.cs`, `Legacy/References.cs`,
`Legacy/Tools/CompilationSymbols.cs`, `Pulsar/Shared/Tools.cs`):

- Sources: every `*.cs` whose repo-relative path starts with a `<SourceDirectories>` entry (ordinal prefix, no separator
  check), or every `*.cs` when none are listed.
- Parse: `LanguageVersion.CSharp14`; symbols `TRACE` + `NETCOREAPP` (Interim) or `NETFRAMEWORK` (Legacy) + `MAGNETAR`
  (Pulsar: `PULSAR`), `DEBUG` only for debug builds, `LINUX` on a Linux host (not added by default: pass `--define`).
  Note: no `NET`, `NET10_0_OR_GREATER` or `NET48` symbols exist in the loader compile.
- Options: DLL, Release, `allowUnsafe: true`, nullable **disabled** (so `string?` outside `#nullable enable` gives CS8632,
  as hub logs show), warning level 4, AnyCPU.
- References: root names = `SpaceEngineers*.dll VRage*.dll Sandbox*.dll ProtoBuf*.dll protobuf*.dll` in the game folder
  (minus `VRage.Native.dll`) + `Microsoft.CSharp 0Harmony Newtonsoft.Json Mono.Cecil NLog PluginSdk System.Text.Json`;
  each resolved in the probe folders in order (runtime folder, game folder, `Libraries/Magnetar*`), `.exe` before `.dll`,
  then every assembly reference followed recursively; names not found are skipped silently (listed as
  `skippedReferences`). So a framework assembly no game DLL references is NOT available to the plugin.
- `[IgnoresAccessChecksTo("X")]` publicizes X with the loader's exact rules; NuGet compile assets and asset DLLs replace a
  closure entry of the same name.
- net10 probe = the Interim image's `Microsoft.NETCore.App` runtime folder + `/opt/magnetar` + DS64 + `Libraries/MagnetarInterim`;
  net48 probe = Microsoft.NETFramework.ReferenceAssemblies.net48 (`v4.8` + `Facades`) + DS64 + `Libraries/MagnetarLegacy`.

Torch, Quasar and companion projects are compiled from their csproj (read as XML, no MSBuild): explicit and default
compile items, `.projitems`, ProjectReference sources merged into the one compilation, `LangVersion`, `Nullable`,
`DefineConstants`, `AllowUnsafeBlocks`, implicit usings, every DLL in the reference folders. WPF pages get code-behind
stand-ins; Quasar `.razor` files go through the SDK's Razor source generator.

## Outputs

`<out>/tools-report.json` (schema 1). Top-level fields:

| Field | Meaning |
|---|---|
| `schema`, `kind`, `gateMode` | 1; `gate` or `report`; whether `--gate` was used |
| `nonce`, `toolsVersion`, `options`, `startedUtc`, `finishedUtc`, `fresh` | the run record (see [Gate record](#gate-record)) |
| `repo`, `head`, `headTree`, `contentSha256`, `headSha`, `base` | what was reviewed |
| `projectKind`, `projectKinds` | see [Project kinds](#project-kinds) |
| `targets[]` | name, kind (magnetar/pulsar/torch/quasar/quasar-companion/se-mod/pb-script), runtimes, manifest, project, why, note |
| `languages` | per language: files, checked, by (checkers that ran), each checker's status |
| `secrets` | gitleaks runs (tree, and git range with a base) and their finding counts |
| `runtimes[]` | what actually compiled: `target/runtime` (DeepScan) and `csproj:<project>/<tfm>` (csproj build) |
| `compiles[]` | every compile and the settings it really used: `id` (`target/runtime`, `whitelist:<job>`, `csproj:<project>/<tfm>`), `project`, `tfm`, `profile` (the loader: magnetar/pulsar/torch/quasar/quasar-companion/se-mod/pb-script; `game-mod`/`game-pb` for the game's script compile; `csproj` for the MSBuild build), `nullable`, `langVersion`, `defines`, `unsafe`, `fileCount`. csproj entries come from MSBuild's own evaluation in the SDK container after the build (`dotnet msbuild -getProperty -getItem:Compile`, with `-t:AddImplicitDefineConstants` for SDK projects so the TFM symbols are included), so Directory.Build.*, imports and linked files count; `evaluated: false` + `evaluationError` when MSBuild could not evaluate. Also written to gate.json (an additive optional field). Every diagnostic carries `compiles` and every gate issue `compile` |
| `whitelist` | the script whitelist check: game version, Sandbox.Game.dll sha, checker and registration steps, symbols, references, entry counts, the dump folder, and per game compile (`jobs`) the file count, whitelist hits and game compile errors |
| `refs{}` | every resolved reference folder with its source (path, image, volume, Steam library) |
| `deepscan[]` | per run: langVersion, defines, nullable, warning level, counts, skipped closure names, timing |
| `analyzers` | loaded assemblies (analyzers and rules each), load failures, dropped packs and why, pack versions, DeepRules present, off rules with reasons |
| `diagnostics[]` | `id severity message file line column endLine tool helpUri category member lineText` plus `seenOn` (target/runtime list), `targets`, `runtimes`, `runtimeSpecific` + `onlyOn` (appears on one runtime of a two-runtime target), `status` NEW/EXISTING/FIXED, `scope`, `suppressed` + `suppression` |
| `csprojBuildDiagnostics[]` | the csproj build's results on files DeepScan also compiled (compared; their compiler warnings count). Results on every other file are in `diagnostics[]`, limited to the tools' rule set |
| `csprojBuildOutsideRuleSet` | csproj-build results dropped because the tools' rule set turns the rule off |
| `buildComparison` | `onlyLoaderCompile` (the hub compile shows it, the IDE build hides it), `onlyCsprojBuild` (the reverse), analyzer count differences |
| `changedMembers[]` | id, file, lines, entry points reached, coverage |
| `affectedMembers[]` | id, via (changed member), direction (caller/callee), distance, isEntryPoint |
| `entryPoints[]` | member, reasons (framework-override, interface-implementation, lifecycle-name, command, harmony, event-handler, background-*, timer, game-thread-queue, nameof-reference), detail |
| `counts` | by scope x status, by rule, totals |
| `dropped` | ClrHeapAllocationAnalyzer results outside per-tick code; net10-only API suggestions; MA0182 on compiler polyfills |
| `compilerPolyfills[]` | each dropped MA0182 on a compiler polyfill: id, file, line, type (full name), seenOn |
| `tests`, `coverage` | test projects, per-TFM results (TRX counts, `host`/`exit`/`aborted`/`killedAtLimit` for net48 runs on this PC), net48 targeted/tested, `host` (stray-write check: roots, files changed under them); changed member coverage |
| `vulnerabilities` | per project: vulnerable packages (framework, package, version, severity, advisory, transitive) |
| `csprojBuild` | builds (project, TFM, status), notes, repo analyzer config lines stripped in the copy |
| `release` | versions found (Version.Build.props, assembly attributes, manifests, quasar-plugin.json, newest CHANGELOG heading), consistent, TODO/FIXME/HACK in compiled sources, manifest problems (description > 1000, uneven indentation, Commit/RepoId placeholders, TODO) |
| `suppressions` | `sites[]` (every #pragma, [SuppressMessage], NoWarn, WarningsNotAsErrors, TreatWarningsAsErrors=false, low WarningLevel, analyzers off, rule sets, repo analyzer config severity lines) and `suppressedDiagnostics[]` |
| `optional` | InferSharp, CodeQL, Mono, Trivy, OWASP Dependency-Check: results, or `not run: not installed`; committed binaries |
| `gate` | verdict and counts (computed in report mode too, for information) |
| `gaps[]` | everything that reduced coverage. In gate mode each gap fails the gate. |

`<out>/tools-report.md` groups diagnostics by scope then rule (NEW first), lists every gap at the top, then changed
members, release, tests, vulnerabilities, the csproj cross-check and references. `<out>/tools-report.sarif` is SARIF 2.1.0
with one run per tool x runtime, `ruleIndex`, repo-relative URIs (`SRCROOT`), `baselineState` (new/unchanged/absent) and
properties `scope`, `status`, `runtimes`, `seenOn`, `runtimeSpecific`, `member`. `<out>/deep-tools.log` is the run log;
`<out>/work/` holds every intermediate (DeepScan JSON and command lines, container logs, SARIF from the csproj build).

Architecture rules (tool `architecture`): ARCH001 Shared/Common/Core code references a host type (Torch, Magnetar,
PluginSdk, Pulsar, Quasar) outside a host `#if`; ARCH002 server code references client-only game types
(Sandbox.Graphics(.GUI), VRage.Input, VRageRender, MyGuiScreen*, MyHud*, MyInput...); ARCH003 Quasar UI code references
game assemblies; ARCH004 code the Magnetar manifest compiles references Torch.

## Gate

`--gate` gives PASS only when all of these hold, on every target x runtime: no compiler error or warning (DeepScan and the
csproj build), no analyzer diagnostic under `deep.globalconfig`, no RS0030 banned-API hit, no DeepRules hit (NEW or
EXISTING), no architecture finding, no finding from any language checker (python, powershell, js) and no secret, no
vulnerable package, no release/manifest/version issue or TODO, tests passing on every declared TFM, no analyzer load
failure, **no suppression anywhere in the code under review** (`#pragma warning disable`, `[SuppressMessage]`, `<NoWarn>`,
`WarningsNotAsErrors`, `TreatWarningsAsErrors=false`, `WarningLevel` 0-3, analyzers turned off, rule sets,
`.editorconfig`/`.globalconfig` severity none/silent/suggestion, and the inline suppressions of the other languages), every
language checked, and **no gaps** (a reference not resolved, a runtime not compiled, a stage that could not run or was
skipped, DeepRules missing, tests not on net48 for a Legacy plugin, Python without tests, a missing mypy stub, the game's
script whitelist not checked), no whitelist hit in mods and PB scripts. `counts.byCategory` uses: compile, whitelist, analyzer, banned-api, deeprules,
architecture, suppression, python, powershell, js, shell, secret, vulnerable-package, release, tests, analyzer-load.
The gate never passes on something it did not check. The only place a rule may be turned off is
`analyzers/deep.globalconfig` (and the other central configs in `analyzers/`), each with a `# reason:` (listed in
`analyzers/RULESET.md`); a line without a reason is a gap.
In the csproj build the repo's own analyzer configs are stripped in the copy and the tools' config is applied last.

Nothing is cached between runs except build inputs: every gate runs the compile, analyzers, tests and scans fresh.

Exit codes: gate mode 0 PASS, 3 FAIL, 1 could not run. Report mode 0 ran, 2 ran with gaps, 1 could not run.
`--all`: 0 all PASS, 3 any FAIL, 1 any could not run; writes `gate-summary.json` and `gate-summary.txt`.

`<out>/gate.txt` is the short human-readable verdict. `<out>/gate.json` is the gate record.

### Gate record

The record (`gate.json`), its fields, the consumer check and the `contentSha256` algorithm are defined in one place:
[`GATE-RECORD.md`](GATE-RECORD.md) (schema 1, frozen). `python deep_tools.py --check-record ...` is the reference checker
for its consumer checks 1 and 3-10; its tests are in `tests/test_check_record.py`.

## DeepScan

```bash
dotnet cache/deepscan/<hash>/DeepScan.dll --manifest <xml> --repo <tree> --runtime net10|net48 --profile magnetar \
    --game-dir <DS64> --probe <runtime dir> <DS64> <Libraries dir> --analyzers <dirs...> \
    --globalconfig analyzers/deep.globalconfig --additional-files analyzers/BannedSymbols.txt --out result.json
```

Other options: `--sources <dirs/files>` + `--source-root`, `--profile plain --refs <dirs>` (every managed DLL), `--ref-files`,
`--root-names`, `--langversion`, `--define`, `--nullable`, `--unsafe`, `--debug`, `--warnlevel`, `--generators`,
`--analyzer-option key=value`, `--global-usings`, `--emit <dir>`, `--include-info`, `--no-analyzers`, `--no-graph`,
`--timeout`. `--help` prints them all. Exit 0 ran, 2 ran with gaps, 1 could not run.

The JSON holds: compile settings, sources, references (name, path, source) and skipped closure names, publicized
assemblies, analyzers (loaded, failed, rule counts, enabled and disabled rule ids, slowest analyzers), `diagnostics[]`
(with suppression kind and written justification for suppressed ones), `members[]` (every method, constructor,
operator, property, indexer, event, accessor with a body, field initializer, and `L:` pseudo-members for lambdas handed to
threads, tasks, timers, the game-thread queue or event subscriptions; file + line span; `perTick`), `calls[]` (from, to,
kind: call/new/get/set/event/delegate/override/contains/creates), `entryPoints[]` and `typeRefs[]` (external types per
file, for architecture rules). Analyzer load failures and analyzer exceptions are gaps, never crashes.

## Runtime matrix

```bash
python runtime-matrix/run_matrix.py --new my-case --dest <dir>      # copy the template
python runtime-matrix/run_matrix.py <harness dir> --out <dir>      # build net48 + net10.0, run both, compare
```

Paste the plugin code under test (read-only, `git show <sha>:<path>`) next to `Program.cs`, stub game types (never
reference game DLLs), and write checks with `Matrix.Check/Repeat/Count`. `GameThread` is a stand-in for the game thread:
one thread with no SynchronizationContext draining a queue. The net48 exe runs natively, net10 on the local runtime,
Mono in Docker only if a local `mono` image exists. Output: `matrix.json`, `matrix.txt` with a DIFFERS line naming every
check whose result differs between runtimes.

`examples/cancel-reentrancy` reproduces SensEssentials `AutoCommand.Cancel` at 0b51e8a: on net48 the cancelled run's
continuation runs inline inside `Cancel`'s `foreach` under `lock (_sync)`, its `finally` removes itself from `_runs`, and
the loop throws "Collection was modified"; on net10 it does not. The fixed version (snapshot under the lock, cancel
outside it) passes on both.

## Caches

Build inputs only, all under `tools/cache/`: `nuget/packages` (analyzer pack and reference packages), `analyzers/<package>`
(collected analyzer DLLs, `pack.json` with versions and dropped packs), `deepscan/<source hash>` (built DeepScan),
`refs/<kind>-<image id>` and `refs/ds-<Sandbox.Game.dll sha>` (reference DLLs copied out of local images and the DS
volume), `nuget-docker` (the SDK container's NuGet cache). Delete any of them to force a fresh copy; results are never
cached. Nothing in the cache depends on where the tools folder is: `pack.json` stores each pack's folder by name and the
folders are always resolved under this `cache/`, and DeepScan/DeepWhitelist are built with `PathMap`, so the folder can
be moved without a rebuild.

Container scripts are written to a file next to the stage's `out` folder and mounted read-only (`/work/script`), never
passed on the command line (one line per build exceeded Windows' 32,767-character limit for large solutions).

## Script whitelist

`Whitelist/` (`DeepWhitelist`, net48, built on this PC against the resolved DedicatedServer64 copy into
`cache/whitelist/<hash>/`, run headless with no window) repeats what the game does at start-up and when it loads scripts,
with the game's own code:

1. `MySpaceGameDefaultIlCompiler.InitIlCompiler`: the reference list, the implicit in-game namespace types and the symbols are
   read from its IL and given to `MyScriptCompiler.Static` the way `MyVRageScriptingInternal.Initialize` does (`VERSION_` and
   `BUILD_` from the constant `SpaceEngineersGame.SetupBasicGameInfo` sets).
2. The checker `SpaceEngineersGame` picks (`MyFakes.USE_CUSTOM_IL_CHECKER_WHITELIST`, read from MyFakes' static constructor):
   `MySpaceGameDefaultIlChecker.InitIlChecker`'s registration steps, found in its IL and invoked on
   `MyScriptCompiler.Static.Whitelist.OpenBatch()`.
3. `MySandboxGame.InitIlChecker`'s in-game blacklist calls, replayed from its IL.

Any change in those methods that the tool cannot replay stops it with a reason (a gap), never a guess. The whitelist is
written to `cache/refs/whitelist-<Sandbox.Game.dll sha>/whitelist-mod.json`, `whitelist-pb.json` and `whitelist-info.json`
(a record of what the game registers; the check itself always uses the live, freshly built whitelist).

Scripts are compiled with the game's `MyScriptCompiler.CreateCompilation` (C# 6, the game's references and symbols) and
checked with the game's `WhitelistDiagnosticAnalyzer` (ModApi for mods, Ingame for PB scripts) and `MyBlacklistSyntaxVisitor`.
Mods: one compile per `Data/Scripts/<folder>` (as `MyScriptManager.LoadScripts`), after `MyScriptManager.UpdateCompatibility`
(its usings prefix and replacement table, read from the game). PB scripts: the MDK project's compile items minus the
`*.mdk.ini` `ignores`. Every prohibited type or member is an issue (category `whitelist`, rules `ProhibitedMemberRule`,
`ProhibitedLanguageElement`, `BlacklistedAttribute`); an error in the game's compile that DeepScan's compile does not show
(the game's reference set is narrower) is a compile issue; a checker that did not complete is a gap.

## net48 tests

.NET Framework 4.8 test projects are built in the SDK container like every other build (never on the host: plugin repos have
deploy targets that copy into game or Torch folders), into the work folder. They then run on this PC, headless: the user's
decision, 2026-11-16 ("On this PC, headless").

- `dotnet test <TestAssembly>.dll` (vstest), started with CreateProcess, `CREATE_NO_WINDOW`, suspended, put into a job object
  (affinity 0x00FFFFFF, kill on close) and only then resumed, so nothing it starts escapes the job.
- `--blame-hang --blame-hang-timeout 300s --blame-hang-dump-type none`, and our own limit (1200 s) that kills the whole
  process tree. xUnit's shadow copy is turned off by inline run settings (it copies under %TEMP% and can pass MAX_PATH).
- cwd, TEMP/TMP and DOTNET_CLI_HOME inside the work folder; an environment without tokens, keys or passwords
  (PULSAR_GITHUB_TOKEN, GITHUB_TOKEN, GH_*, ...) and without game, Torch, Magnetar, Quasar or Steam variables and PATH entries.
- Stray writes: the mtimes and sizes of every file under `E:\Torch` and the game client's `Bin64` are compared before and
  after the runs; files that changed are listed in `tests.host.strayWrites` (whatever wrote them).
- A failed test is a `tests` issue (runtime net48); a run that stopped without finishing (a hang stopped by blame-hang, a crash,
  or our limit) is a `tests` `aborted` issue. A test project that could not be built or started is a gap.

## Known limitations

- Quasar companions (`netstandard2.0`, built by Quasar) are compiled against net48 reference assemblies + DS64 +
  Magnetar.Protocol; Pulsar client plugins have no local Pulsar `Libraries` folder (Harmony etc. resolve from the game
  folder only).
- ProjectReference sources are merged into one compilation for Torch/Quasar targets.
- net48 tests run on this PC (the user's decision, 2026-11-16), not in a container: see [net48 tests](#net48-tests). A
  Legacy/Torch plugin with no net48 tests is still a gap.
- The call graph is syntactic plus semantic binding: reflection, `dynamic` and delegates stored in fields are not followed
  (nameof references count as entry points).
- Files only the csproj build compiles (tests, tools, samples, generic repos) have no member map, so their diff scope is
  by changed lines only, and ClrHeapAllocationAnalyzer results there are dropped (no per-tick members to keep them for).
- NetAnalyzers' data-flow rules can run for 20+ minutes on very large methods. DeepScan limits the interprocedural call
  chain to 1 (`deep.globalconfig`) and stops after its own time limit; each container build step is stopped after
  1200 s and reported as a gap. The tools' config reaches csc through `EditorConfigFiles` (a `GlobalAnalyzerConfigFiles` item added
  in Directory.Build.targets never did), so the limit applies in the csproj build too: PluginCheck's `stages/commandcheck` and
  `stages/threadcheck`, which used to hit the 1200 s limit, now finish in about a minute (the seven PluginCheck builds took 72 s together).
- Razor components of several merged projects are generated under one root namespace (a gap is recorded).
- A project's own packaging/copy steps can fail in the Linux SDK container. When one fails only because its output folder
  is missing from the copy ("Could not find a part of the path", e.g. APBL's and AutoStation's Torch zip into the git-ignored
  `..\Build`), that folder is created inside the copy and the full build runs again, so packaging really runs (a note
  lists the folders). When packaging still fails (MSB3943, MSB3021, MSB3026, MSB3027, MSB3030, MSB3073), the project is
  compiled and analysed again with `-t:Compile`; the skipped targets are listed under `notCovered` (which a consumer
  refuses), never the code.
- MDK2 in the container: each `Mal.Mdk2.References` project gets `<Project>.mdk.local.ini` in the copy (binarypath
  `/refs/ds`, interactive off, output under `/work/out/mdk`). PbPackager 2.0.x ships Windows programs only (`mdk.exe`,
  `mdknotify-win.exe`), so its MdkRestore/MdkPack targets are switched off in the copy and listed under `notCovered`.
  ModPackager's Linux `mdk` is tried; 2.2.3 is a self-contained .NET 9 program whose MSBuildLocator finds no MSBuild it can
  load in the SDK 10 image, so MdkPack fails, the project is built again with MdkPack off and that is listed under `notCovered`.
- Old-style projects with `packages.config`: the Linux SDK cannot restore them (MSBuild.exe only), so the exact versions are
  fetched with PackageDownload into the container's NuGet cache and copied to the `packages` folder their HintPaths use.
- Client plugins: the SE client's `Bin64` (Steam library) is mounted read-only at `/refs/bin64` and passed as `$(Bin64)`;
  Pulsar's `Libraries/Legacy` and `Libraries/Interim` come from a local Pulsar install (`%APPDATA%\Pulsar` or `PULSAR_DIR`).
- net48 tests are built in the container and run on this PC; the game, Torch and MagnetarLegacy assemblies they reference
  with `Private=false` (from `/refs/...`, which exists only in the container) are copied into the test's output folder first.
- Script whitelist: PB scripts are checked as the MDK2 project's files (minus the `*.mdk.ini` `ignores`), not as MDK's packed
  script inside the game's `Program` wrapper; symbols bind the same, but packing problems are not checked. `BRANCH_STABLE` is
  assumed (the default Steam branch). System.Runtime and System.Collections are added as the game's DLLs reference them (the
  game adds them when they are loaded in its AppDomain). Mod scripts get the game's `UpdateCompatibility` rewrite, so columns
  can be off on rewritten lines (lines are not). Batch files have no checker (a gap).
- DeepRules' game-specific rules (DR010, DR015, DR023, DR024, DR030) also fire on non-game code today; the DeepRules
  project owns the fix.
- InferSharp, CodeQL, Mono, Trivy and Dependency-Check run only when already installed; none is installed here today.
