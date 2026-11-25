# Performance and allocation

Pass P12. Cost only matters together with frequency. For each changed or affected member, find how often it runs: per tick, per frame, per entity per tick, per run, per request, per message, per render, per item in a batch, per command, per save, or once. Multiply. Every allocation also feeds the GC, and on a busy process GC pauses stop every thread.

Packs add their budgets: the game's 60 ticks a second shared with every plugin ([se-plugin performance](../../packs/se-plugin/performance.md)), per-block components ([se-mod](../../packs/se-mod/mod-api.md#performance)), the PB instruction limit and PB limiters ([pb-script](../../packs/pb-script/checks.md#p3-and-p12-instruction-limit-and-per-run-cost)), queries, circuits and rendering ([dotnet-app](../../packs/dotnet-app/data-access.md#queries), [blazor-signalr](../../packs/dotnet-app/blazor-signalr.md#rendering-and-circuits)), allocation in public hot APIs ([dotnet-library](../../packs/dotnet-library/api-design.md#performance-of-public-apis)), and the language packs' own costs.

<a id="hot-path-checks-per-tick-per-frame-per-entity"></a>
## Hot-path checks

A hot path is anything that runs per tick, per frame, per entity, per request, per message or per item.

- **Allocations:**
  - `new` collections and arrays, LINQ, closures capturing locals (and their delegate allocations), `params` arrays, boxing (non-generic collections, `Enum.HasFlag` on net48, `object` parameters), string concatenation and interpolation, and `ToString()` of numbers.
  - ClrHeapAllocationAnalyzer and DR023 list them inside per-tick members. Read each one, and say whether it matters at that frequency.
- **Reflection per call:** `GetMethod`/`GetField`/`Invoke`/`GetValue`. Cache delegates at start-up, using compiled expressions or `CreateDelegate`.
- **I/O per call:** `File.Exists`, `Directory.*`, `Environment.GetEnvironmentVariable`. Real cases:
  - DoomForSe PluginHub#342/#349: `File.Exists` 7 times per frame (L17).
  - PAM: `LiveClientTest.Active` re-read an environment variable per ship per tick (L48).
  - Resolve these once.
- **Full scans:**
  - Enumerating every entity, row, file or process each second or each tick.
  - Track instead with add/remove events and caches, invalidated on change.
  - Real: Atomic.FM PluginHub#175, a world scan every 60 frames (L28). DLSS PluginHub#177 rescanned assemblies per frame when a dependency was absent (L43).
- **Parsing:** CustomData, names, config text and records parsed per frame, per tick or per request. Parse on change, and cache.
- **Logging:** per tick, per entity, per run or per request, without a rate limit (DR024). Also costly string formatting for log lines that are filtered out anyway.
- **Observers and listeners per component:** a `MutationObserver`, `ResizeObserver`, scroll listener or pointer handler per dialog, row or item multiplies with the number on screen. Keep it scoped, disconnect it on close, and do not observe a live subtree when one element would do. See [accessibility.md](accessibility.md#names-roles-and-descriptions).
- **Locks on a busy owner thread:** contention with background threads stalls it. Measure, or reason about, the hold time on both sides.
- **Algorithms:**
  - O(n²) over entities, rows, files or users.
  - Sorting inside loops.
  - `Count()`/`Any()` on `IEnumerable` re-enumerating expensive sources.
  - Multiple enumeration of LINQ queries (CA1851).
  - Dictionary keys that are structs without `IEquatable<T>` (boxing in the comparer).
- **Large buffers:** arrays of 85,000 bytes or more go on the Large Object Heap and are collected only in Gen 2. Reuse them (`ArrayPool`/a kept buffer), and drop oversized ones. Real: PAM `PamFiles.Write` kept one buffer per thread and dropped it above 8 MB.

## Off the hot path

- **Saves and shutdown:** work that runs while a host waits (a game save, a request, a shutdown timeout) adds directly to that wait.
- **Background work that holds the host's locks:** it can still stall the host. In PAM, a scan inside the game's `ReadRange` couldn't be stopped for a GC; smaller chunks cut the suspend wait from 20 ms to 11 ms.
- **Redundant work:** two different requests doing the same expensive scan. Real: Ore to Auto GPS PluginHub#187, where removing a cross-material dedup brought back overlapping voxel scans (L45).
- **Backoff:** failing operations retried on a fixed short period with no backoff ([errors.md](errors.md)).
- **Start-up time:** work done at start that could be lazy, for tools run many times (a gate run per lease request, a CLI per file).
- **Machine use:** builds, tests and tools that run many processes stay on the cores they are given (the user keeps cores 24-31; tools pin to 0-23) and run no more parallel work than asked ([SKILL.md](../../SKILL.md#ground-rules), Machine).

## Evidence

- **Name the frequency and the per-call cost** of every Medium+ performance finding, with the code path that sets the frequency. "Could be slow" is not a finding.
- **Measure where it's cheap:** a harness microbenchmark (Stopwatch over N iterations on every runtime), or a run of the real host with its own timing. Heavier profiling (PAM's WPR plus allocation stacks) only when the decision needs it.
- **Read analyzer performance rules in context:** CA18xx, Meziantou, Roslynator and ClrHeapAllocationAnalyzer. Many are irrelevant off the hot path, so give each one a verdict.

<a id="dr025"></a>
## Unbounded growth (DR025)

- **The pattern:** a field collection the type adds to but never removes from, and never checks against a limit. It grows for the whole life of the process.
- **Common cases:** per-user, per-entity or per-request maps keyed by IDs that never come back, and caches with no eviction.
- **Required:** an eviction or removal path (entity close, user leave, session end, unload) or a cap, and the matching clean-up on unload ([lifecycle.md](lifecycle.md)).
