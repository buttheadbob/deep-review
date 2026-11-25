# Tests and evidence

Pass P15. A test is evidence only for what it runs, on the runtime it runs on, with the assertions it makes. The SensEssentials cancel bug shipped with passing tests, because the harness ran on .NET 10 only, and with a passing Legacy live test, because the live scenario never cancelled during a delay with two runs going.

Packs add their test hosts: PluginCheck live tests and hub-style installs ([se-plugin tests](../../packs/se-plugin/tests.md)), a headless dedicated server ([se-mod](../../packs/se-mod/README.md#tests)), `E:\Torch` for PAM ([pb-script](../../packs/pb-script/checks.md#p15-tests)), headless UI tests ([dotnet-app](../../packs/dotnet-app/README.md#tests)), API and package tests ([dotnet-library packaging](../../packs/dotnet-library/packaging.md#tests)), pytest ([python](../../packs/python/checks.md#p15-tests)), Pester ([powershell](../../packs/powershell/checks.md#p15-tests)), and browser tests ([web](../../packs/web/checks.md#p15-tests)).

## Unit and harness tests

- **Runtimes:** tests must run on every runtime the code ships on: each target framework, each OS, each language version in the P0 list. `deep_tools.py` reports a declared target with no test run as a gap. Treat it as one.
- **A test for each change:** for each changed behaviour, the test must fail on the base commit and pass on the head commit. Run it on base to prove that. A test that passes on both proves nothing about the change.
- **Assertions:** check the result, not only "no exception". Removed or weakened asserts in the diff are findings. Real: GridGenerator's review, where a test block was left with no assert.
- **Coverage that samples:** a test that checks one member of a group can't catch a fault in another (L49). Cover every case the change touches, or say which are left.
- **Assert the contract, not the shape:** a test that only checks a class name, an element exists or "no exception" does not prove behaviour, CSS, or accessibility. Assert the result, the computed style, the role/name/description and the target element.
- **Dynamic and edge cases:** for each changed UI or async behaviour, cover the empty, null, late, duplicate and cancellation-order cases the change can meet. A description that appears after open, an id that collides, a message with a newline and a recorded result followed by a cancel are separate tests.
- **Locator identity:** a browser test must identify its target by a stable attribute (title, id, test id), not a positional `.first()` that can resolve to a previous dialog or a stale element. Reusing one locator across sequential dialogs is a finding.
- **Timing asserts** ("completes in under 2 s") are flaky on a loaded machine. Assert results, and measure timing separately.
- **Harness drift:** a harness that copies code under review must match the pinned code. Diff the copy against the commit.
- **Coverage:** `deep_tools.py` maps coverage to changed members. A changed member with no coverage and no live-test step reaching it is an untested change. Say so.
- **Tests that touch the machine:** a test must not write outside its temp folder, open windows, take focus, reach the network unless that is the thing under test, or use the user's real data folders. Real: DoomForSe's live tests use `-appdata .test\AppData` so the user's real storage is never touched.

## Live tests

- **Required:** reading code is never enough (MasterRules 21a). Every commit item carries a live test in the real host: where it ran, the scenario, before (what failed without the change), after, and evidence on disk. Only when no live test is possible does it carry the reason instead.
- **Coverage:** a live test proves only the paths that reached their logic. Read every reply and log line. A "not found", "usage" or "in-game only" reply means that line didn't run (Agent.md).
- **The hard case:** the scenario must include it, not only the normal one:
  - Concurrency changes: two or more concurrent runs, cancel during a delay, cancel during execution, unload during a run.
  - Persistence changes: old-format files, a missing file, a corrupt file, a read-only folder.
  - Defaults: turn on each off-by-default feature that the change touches, and break it on purpose (REVIEW.md).
  - Runtime: each runtime and OS the code ships on.
- **Installed form:** test what users install ([docs-release.md](docs-release.md#release-metadata)).
- **Documented-only is not tested:** a documentation update does not exercise the behaviour. If the behaviour remains, keep the finding and say which path is untested; do not count the doc change as a live test.
- **Write down what the live test did not cover,** as named gaps.

## Evidence standard for findings

- **Causal path:** each finding gives it from the trigger to the consequence, with file and line at the reviewed commit.
- **Proof:**
  - Medium and above: a reproduction (harness, container scenario or forced failure), with the output from each runtime, or `unable to verify` with what was tried.
  - Low and Minor: reading is enough, but give the exact lines.
- **No unread claims:** never "probably", "likely" or "seems" about code that can be read (Agent.md).
