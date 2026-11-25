# Plugin tests: runtimes, PluginCheck and hub installs

Pass P15, after [core tests](../../checks/core/tests.md). The SensEssentials cancel bug shipped with passing tests, because the harness ran on .NET 10 only, and with a passing Legacy live test, because the live scenario never cancelled during a delay with two runs going.

## Unit and harness tests

- **Runtimes:** a Magnetar plugin needs `net48` and `net10.0` test targets. Windows runs net48 natively, and Mono can stand in. `deep_tools.py` reports "tests not on net48" as a gap. Treat it as one. No container runs net48 tests here, so a Legacy or Torch plugin with net48 tests still has that gap until they run natively.
- **Harness drift:** a harness that copies plugin code must match the pinned code. Diff the copy against the commit.

## Live tests (PluginCheck behaviour)

- **Who runs them:** the user manages PluginCheck. Request runs and scenarios through the PluginCheck session. Plugins never run outside the containers; PAM on `E:\Torch` is the user's own setup.
- **Coverage:** a live test proves only the commands that reached their logic. Read `behaviour-replies.txt`. A "not found", "usage" or "in-game only" reply means the command didn't run (Agent.md).
- **The hard case:** the scenario must include it, not only the normal one:
  - Concurrency changes: two or more concurrent runs, cancel during a delay, cancel during execution, unload during a run.
  - Persistence changes: old-format files, a missing file, a corrupt file, a read-only folder.
  - Defaults: turn on each off-by-default feature that the change touches, and break it on purpose (REVIEW.md).
  - Runtime: each runtime the plugin ships on (Interim and Legacy, Torch, Pulsar CLR/CoreCLR as declared).
- **Telemetry in tests (MasterRules 28):** Torch analytics off, never answer yes to Quasar's data consent prompt, servers private. Magnetar may download its companion mod MagnetarMod and send usage statistics.

## Hub-style install

- **Client plugins and assets** get a hub-style install (a local hub entry, the real loader compile), not a dev folder ([hub-release.md](hub-release.md#manifest-and-hub-install), Assets). The dev-folder path returns the source path itself, so local tests can't catch asset-name defects. Real: DoomForSe PluginHub#342 (L15).
- **Before a hub PR:** a full PluginCheck run plus REVIEW.md on the exact commit, and this review on the same commit.

## How to confirm

- **Write down what the live test did not cover,** as named gaps.
- **Quasar UI:** headless renders of each tab, the setting changed in the UI reaching the running server through the companion, and no console errors ([quasar](quasar/README.md)).
