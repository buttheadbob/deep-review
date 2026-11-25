# Plugin performance: the 60-tick budget

Pass P12, after [core performance](../../checks/core/performance.md).

Space Engineers runs the simulation on one game thread at 60 ticks per second, so each tick has about 16.6 ms in total, for the whole game and every plugin. A server with many grids has little left. Every allocation also feeds the GC, and on a busy server GC pauses stop every thread. Measured on PAM: 54% of Torch's garbage came from one plugin reading an environment variable per ship per tick.

## Frequencies

For each changed or affected member, find how often it runs: per tick, per frame, per entity per tick, per PB run, per command, per save, or once. Per entity per tick on a server with thousands of blocks is the multiplier that matters most.

- **Per-tick members** are `Update*`, `Draw*`, `HandleInput`, `Simulate*`, `*Tick*`, and Harmony patches on them. deep_tools keeps ClrHeapAllocationAnalyzer results only in these members and their direct callees, and DR023 checks them for LINQ, allocations, string building, captured lambdas, reflection, file I/O and logging. Read each lead, and say whether it matters at that frequency.
- **`Update1`/`Update10`/`Update100`** decide the multiplier ([game-api.md](game-api.md#timing)).

## Game-specific costs

- **World scans:** enumerating every entity, grid or block each second or each tick. Track instead with entity add/remove events and per-grid caches, invalidated on change. Real: Atomic.FM PluginHub#175, a world scan every 60 frames (L28).
- **CustomData** parsed per frame (Atomic.FM split it 3 times per frame per anchor). Parse on `CustomDataChanged` ([game-api.md](game-api.md#entities-and-blocks)).
- **Locks on the game thread:** contention with background threads stalls the simulation.
- **Saves:** they run on the game thread. Serialization in `BeforeSave` adds to the save stall.
- **Voxel reads:** a background scan inside the game's `ReadRange` couldn't be stopped for a GC in PAM; smaller chunks cut the suspend wait from 20 ms to 11 ms.
- **`Enum.HasFlag`** boxes on Legacy and Torch (net48) only.

## Evidence

- **Measure where it's cheap:** a harness microbenchmark on both runtimes, or a PluginCheck behaviour run with the plugin's own timing (requested through the user).
- **The gold standard is heavy:** PAM's measurement approach, WPR plus allocation stacks. Use it only when the decision needs it, and size the test to the decision.
