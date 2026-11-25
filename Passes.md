# The mandatory passes

Run every pass, in order, for every kind of project. A pass is never skipped because "nothing changed there" or "this kind of project has none of that". It ends either with entries in the ledger, or with a `not present` entry that says what you looked at. The ledger is the proof that the review was done: a pass with no ledger entry makes the review incomplete ([Report.md](Report.md)).

**The unit of work is the member.** P2 builds the member list: every changed member, plus every affected member (callers up to the entry points, and callees one level down). Passes P3 to P13 cover each member on that list. For a new registration or a first review, every member in the code under review is on the list. A member is:
- in C#: a method, constructor, property, indexer, event, operator, field initializer, or a lambda handed to a thread, task, timer, queue or event;
- in Python: a function, a method, or a module-level block that runs on import;
- in PowerShell: a function, a script block passed to a job or pipeline, or the script body;
- in JavaScript and TypeScript: a function, a handler, a component, or a module's top-level code;
- in SBC and framework config: a definition, or one tag block (a SpawnGroup, a profile, a part definition).

**Core plus packs.** Each pass from P3 to P15 has one core file in [checks/core](checks/core) that applies to every project. P0 chooses the packs ([SKILL.md](SKILL.md#choosing-packs)). Each loaded pack adds checks to the passes listed in its README's pass map. For every member, walk the core file first, then each loaded pack's additions. Every ledger row names the file its check came from.

| Pass | Name | Core | Packs that add checks | Output |
|---|---|---|---|---|
| P0 | Scope, gate and packs | this file; [docs-release.md](checks/core/docs-release.md) Release metadata | every loaded pack's README (P0 row); se-dev-plugin-review `ReviewPaths.md` for hub plugins | scope block, gate.json read, pack list with reasons |
| P1 | Tools | [tools/README.md](tools/README.md) | se-plugin (loader compile); python, powershell, web (`triage.md`) | `tools-report.json` read in full; every lead in scope queued for a verdict |
| P2 | Change map and state matrix | this file | every pack README (entry points) | member list, entry points, matrix |
| P3 | Concurrency and async | [concurrency.md](checks/core/concurrency.md) | se-plugin, se-mod, pb-script, dotnet-app, dotnet-library, python, powershell, web | ledger rows per member |
| P4 | Runtime differences | [runtime-differences.md](checks/core/runtime-differences.md) | se-plugin and its host notes, se-mod, pb-script, dotnet-app, dotnet-library, python, powershell, web | ledger rows; runtime tag on every finding |
| P5 | Lifecycle | [lifecycle.md](checks/core/lifecycle.md) | se-plugin, se-mod, pb-script, dotnet-app, dotnet-library, python, powershell, web | ledger rows |
| P6 | Host and platform API | [host-api.md](checks/core/host-api.md), [accessibility.md](checks/core/accessibility.md) | se-plugin, se-mod, pb-script, dotnet-app, dotnet-library, python, powershell, web | ledger rows; host code read and quoted; accessible name/description and DOM identity |
| P7 | Errors and failure paths | [errors.md](checks/core/errors.md) | se-plugin, se-mod, pb-script, dotnet-app, dotnet-library, python, powershell, web | failure table per member |
| P8 | Persistence and config | [persistence.md](checks/core/persistence.md) | se-plugin, se-mod, pb-script, dotnet-app, dotnet-library, python, powershell, web | ledger rows |
| P9 | Inputs, operations, permissions | [inputs.md](checks/core/inputs.md), [accessibility.md](checks/core/accessibility.md) | se-plugin, se-mod, pb-script, dotnet-app, dotnet-library, python, powershell, web | input table per handler; accessible input naming |
| P10 | Security and supply chain | [security.md](checks/core/security.md) | se-plugin, se-mod, dotnet-app, dotnet-library, python, powershell, web | labelled security inventory |
| P11 | Authority, trust and interop | [authority-interop.md](checks/core/authority-interop.md) | se-plugin, se-mod, pb-script, dotnet-app, dotnet-library, python, powershell, web | authority and interop notes |
| P12 | Performance | [performance.md](checks/core/performance.md) | se-plugin, se-mod, pb-script, dotnet-app, dotnet-library, python, powershell, web | frequency x cost per hot member |
| P13 | Correctness details | [correctness.md](checks/core/correctness.md) | se-plugin (ticks and units), se-mod (definitions), dotnet-library (API breaks), python, powershell, web | line-by-line notes |
| P14 | Docs, UI and release | [docs-release.md](checks/core/docs-release.md), [accessibility.md](checks/core/accessibility.md) | se-plugin, se-mod, pb-script, dotnet-app, dotnet-library, python, powershell, web | text-vs-code table; visual and accessible state |
| P15 | Tests and evidence | [tests.md](checks/core/tests.md), [accessibility.md](checks/core/accessibility.md) | se-plugin, se-mod, pb-script, dotnet-app, dotnet-library, python, powershell, web | test matrix per runtime, base-fails/head-passes; accessible and visual assertions |
| P16 | Lessons sweep | [Lessons.md](Lessons.md) | rows by their Pack column; se-plugin adds REVIEW.md "Common defects" | one row per lesson |
| P17 | Fresh-eyes pass | this file | the loaded packs' headings | independent findings merged |
| P18 | Reproduce and settle | this file | each pack README's P18 row | every Medium+ reproduced or labelled |

## P0 Scope, gate and packs

Record:
- the repository, the head (a full SHA, or `WORKTREE` for uncommitted work), the base SHA (or "new registration" / "first review");
- the nonce for this request, from whoever asked for the review (the PluginCheck session for a lease, the master for an Outbox item). An ad-hoc review with no nonce uses a fresh random one and says that its record qualifies for nothing;
- the release targets: hub manifests, a Workshop item, a package, a published app or site, and the runtimes each ships on;
- the host versions you read.

**Run the gate**, once, with the base:

```
python tools/deep_tools.py --gate --repo <repo> --head <sha|WORKTREE> --base <sha> --nonce <nonce> --out <new empty dir>
```

The run writes `gate.json` (the gate record, [tools/GATE-RECORD.md](tools/GATE-RECORD.md)) and the full `tools-report.json`, `.md` and `.sarif` that P1 reads. Exit codes: 0 PASS, 3 FAIL, 1 could not run. A FAIL doesn't stop the review: every gate issue becomes a lead, and every gap goes into "Not checked". The gate record stays its own record; the review notes its verdict and path.

**Choose the packs** from gate.json's `projectKinds` and `languages` ([SKILL.md](SKILL.md#choosing-packs)). Write the list with the reason for each pack. For a `dotnet` project, say whether it is an app, a library or both, and why: gate.json doesn't tell them apart. A language with files and no pack goes into "Not checked" as "no pack for <language>".

**Provenance:** for a hub plugin, check pin reachability from the default branch, drift from HEAD, and the PR text against the actual range. If se-dev-plugin-review already ran on this exact commit, reuse its scope (`review.json`), but re-check the SHAs. For any other project, check the release reference (tag, package version, Workshop version) and what the PR or commit text claims against the range.

## P1 Tools

**What the gate run did** ([tools/README.md](tools/README.md)):
- compiled a plugin the way each hub loader does, for each runtime it ships on, and built every other .NET project on every target framework it declares;
- ran NetAnalyzers (all), Sonar, Meziantou, Roslynator, IDisposableAnalyzers, ErrorProne.NET, VS Threading, AsyncFixer, SecurityCodeScan, BannedApiAnalyzers (with our banned list), ClrHeapAllocationAnalyzer (hot paths) and our own DeepRules on C#; ruff, mypy, bandit and pytest on Python; PSScriptAnalyzer on PowerShell; ESLint on JavaScript and TypeScript; gitleaks on everything;
- checked vulnerable packages, versions, manifests and suppressions, and ran the tests on each target framework;
- mapped everything to the diff, the changed members and their callers.

Read `tools-report.md` from top to bottom, gaps first.
- **Gaps:** every one goes into the report's "Not checked" list. If a gap can be closed (a missing reference, a missing test target, a missing stub), close it, or say why you can't.
- **Leads:** every diagnostic scoped in-diff, in a changed member or in an affected member gets a verdict later (`real`, `intended`, `not a problem`, `unable to verify`), with the line that proves it. That covers compiler, analyzer, DeepRules, BannedApi, architecture, InferSharp and CodeQL results, and the language checkers (ruff, mypy, bandit, pytest, PSScriptAnalyzer, ESLint) and gitleaks. The language packs' `triage.md` files say how to read their tools.
- **NEW versus EXISTING:** a lead that is EXISTING (also at base) and outside the changed members still gets a verdict, but it can be grouped by rule.
- **One-runtime diagnostics:** a diagnostic seen on only one runtime is always read individually.
- **Suppressions:** each suppression site in the code under review is a gate issue; the diagnostic it hid is a lead like any other.

## P2 Change map and state matrix

For each changed member, read the whole member and its callers up to the entry points. For C#, use deep_tools' `callGraph`, `changedMembers` and `entryPoints`, then confirm by reading. For other languages, deep_tools scopes by changed lines only, so build the member list by reading. Flow's `code_find`, `code_refs` and `code_graph` help where the project is indexed (MasterRules 25a). The pack READMEs list each kind's entry points. Write a matrix:

| Member | Entry points that reach it | Threads, tasks or processes | Runtimes | Inputs | Shared state read/written | Locks | Frequency |
|---|---|---|---|---|---|---|---|

This matrix drives every later pass. Wrong threads or frequencies here make P3 and P12 wrong. A cell you can't fill is written as `unknown`, along with what you tried.

For UI members, add: render mode, whether the element is present at first render or arrives later, the accessible name/description and their owners, generated/caller DOM ids, and the visual state (variant, severity, focus). These drive [accessibility.md](checks/core/accessibility.md) and the web/Blazor pack checks.

## P3 to P15

Work through each checklist against each member on the list: the core file, then each loaded pack's file for that pass. For each check that applies, write a ledger row: pass, member, check, source (the check file), result (`finding Fn` / `not present` / `unable to verify` + reason), and evidence (`file:line`).

Write rows for the checks that come out clean too. "Checked, not present" is what tells the reader the check was made.

## P16 Lessons sweep

Go through every row of [Lessons.md](Lessons.md).
- **Rows whose Pack is `core` or a loaded pack:** mark each one `not present`, `present → Fn`, or `not applicable (why)`.
- **Rows from packs not loaded:** `not applicable (pack <name> not loaded)`.
- **se-plugin:** also every row of PluginCheck REVIEW.md "Common defects".

## P17 Fresh-eyes pass

Spawn one independent reviewer agent (general-purpose), and give it:
- the diff;
- the member list;
- the headings of the core checklists and of every loaded pack;
- the paths to the code.

Don't give it your findings or conclusions. Ask it to find defects of any severity, Minor included, with `file:line` and a causal path.

For any change that touches UI, DOM, accessibility, CSS or browser tests, also ask it to walk these explicitly:

- accessible name and description at open, and whether either is duplicated;
- `aria-describedby` targets: empty, missing, replaced, or added after the first render;
- live-region roles and whether severity is carried by more than colour;
- generated and caller-supplied DOM ids, and attribute ownership (`AdditionalAttributes`);
- plain-text whitespace, CSS shorthand order, and the computed style of each variant;
- host mount/unmount and tasks that can wait forever for a component;
- cancellation versus a recorded result;
- browser-test locators, sequential dialogs, and stale `.first()` elements.

Merge what it returns. Each item must be either confirmed by your own reading (it becomes a finding) or refuted (with the line that rules it out). Never drop one silently. If agents aren't available, label this pass `not run` and list it in "Not checked".

## P18 Reproduce and settle

- **Medium and above:** every finding gets a reproduction, or `unable to verify` with what was tried. Use one of:
  - a runtime-matrix harness for .NET ([tools/runtime-matrix](tools/runtime-matrix)), with output per runtime;
  - for other languages, the harness the pack names: a pytest file or script in a temp copy (`python -I -B`), a `pwsh -NoProfile -NonInteractive` script, a node script or a headless browser run;
  - for plugins, a PluginCheck scenario: request it through the user, who manages PluginCheck;
  - for mods, a headless dedicated server (a container or a test copy, never `E:\Torch`);
  - for PAM, the user's own `E:\Torch` setup;
  - a forced failure.
  Every harness runs headless, opens no window, and stays on cores 0-23 (MasterRules 33a).
- **Test fixes:** where a fix is proposed, re-run the harness with the fix, and show it passing on every runtime.
- **Documented-only closures:** a finding closed by documentation is still a finding. Keep it in `findings` and `forHuman` with the behaviour that remains; never change it to `not present` or a clean note just because it is documented. If the user accepted the limitation, record that acceptance; documentation alone is not acceptance.
- **No completion path:** a pending task, dialog or component that can wait forever for a host, mount, event or callback is a finding even when the current UI makes it unlikely. Reproduce the no-consumer path, or state why it cannot occur.
- **Severity:** settle it last, after the reproduction (see [Report.md](Report.md)).

## DeepRules help links

DeepRules gives each lead a `helpUri` of `checks/<file>.md#<anchor>`, relative to the skill folder. In this layout the same files and anchors live under `checks/core/`, so read `checks/<file>.md` as `checks/core/<file>.md` until DeepRules' help base moves (one line in `tools\DeepRules\src\DeepRules\Rules.cs`). Every anchor the rules use is kept. DR013 (Harmony), DR030 (nullable in the loader compile) and DR032 (entity lookups off the game thread) land on core sections that point on to the se-plugin pack.
