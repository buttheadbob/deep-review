# Errors and failure paths

Pass P7. Most review misses are failure paths: tests cover the happy path, and production hits the other one. For every changed or affected member, list what can fail inside it (I/O, parsing, host lookups returning null, network, cancellation, a disposed object, a child process) and follow each failure to where it ends.

Packs add their hosts' entry points and error models: game callbacks and PluginCheck's leads ([se-plugin errors](../../packs/se-plugin/errors.md)), mod and PB scripts ([se-mod](../../packs/se-mod/mod-api.md#errors), [pb-script](../../packs/pb-script/checks.md#p7-errors)), hosted services and Blazor circuits ([dotnet-app](../../packs/dotnet-app/hosting-di.md#errors-at-the-edges)), library exceptions as a contract ([dotnet-library](../../packs/dotnet-library/api-design.md#exceptions)), Python exceptions ([python](../../packs/python/checks.md#p7-exceptions-and-exit-codes)), terminating and non-terminating errors ([powershell](../../packs/powershell/checks.md#p7-errors)), promise rejections ([web](../../packs/web/checks.md#p7-errors-and-fetch)).

## Checks

- **Exceptions escaping into the host:**
  - Every entry point needs a guard that logs and keeps the component's state consistent: per-tick or per-frame methods, event handlers, patch methods, command and request handlers, timer and thread roots, message handlers, and start-up and stop. (DR015)
  - Find out what the host does with an exception from each entry point: some catch and log, some stop the loop, some end the process. Read it in the host's code. The game's answers are in [se-plugin errors](../../packs/se-plugin/errors.md).
- **Rethrow into an unguarded caller:**
  - `catch { ...; throw; }` is fine only if every caller up to the entry point catches it.
  - Real: Atomic.FM PluginHub#175. The ambient caller caught the rethrow, but the hotkey and button paths didn't, so a bad URL crashed the client (L34).
  - Trace every caller of a method that rethrows.
- **Swallowed exceptions:**
  - `catch {}`, or catch-all with no log, hides real defects. It is acceptable only for narrowly understood optional behaviour, with a comment saying why. (DR014)
  - `try/catch` does not make a race safe: a race gives wrong data or a hang, and neither throws (REVIEW.md).
- **`throw ex;`:** destroys the stack trace. Use `throw;`.
- **Failures the caller never sees:**
  - Work started on a background task reports its failure only to its own log. The caller's retry and backoff logic never learns about it, so it retries at full speed.
  - Real: Atomic.FM PluginHub#175. Connect failures inside `Task.Run` never reached the backoff, so a full stack trace was logged every 5 s (L35).
  - Report async failures back through a flag, result or callback, and apply backoff to them.
- **Error spam:**
  - A failure that repeats per tick, per run, per request or per user must be logged with its stack trace once, then summarized or rate limited.
  - Real: APBL magnetar-hub#156 sent 60 chat messages a second (L11). Atomic.FM logged anchor counts every second and showed status notifications every 10 s (L27). (DR024)
- **Partial failure:**
  - A multi-step change must leave a consistent state when step N throws.
  - Remove-before-add loses the old item when add throws (DR028. Real: PluginHub#187, lost GPS markers, L44).
  - Commit to memory only after the external call succeeds, and roll back on failure.
- **Ignored results:** `TryParse`/`TryGetValue`/`TryAdd`/`Remove` return values that are ignored. `File.Exists` checked and then not used. A command, process or API call that returns false or a non-zero exit code with no log. Analyzer rules: CA1806, MA0045 and others. Read each one.
- **Null paths:**
  - `FirstOrDefault()`/`Find()`/`as` cast results dereferenced without a check.
  - `Dictionary[key]` where the key may be absent (`KeyNotFoundException`).
  - Host values that are null in some states (the pack lists them).
  - Each `!` and `null!` is a promise nothing checks (REVIEW.md; MasterRules 22 bans `null!` in C#).
- **Cancellation exceptions:**
  - `OperationCanceledException`/`TaskCanceledException` must be caught where cancellation is expected, and nowhere else.
  - Catching them broadly can hide a real timeout.
- **`ObjectDisposedException` after dispose:** callbacks, timers and continuations that run after `Dispose` touch disposed objects. Either stop them first or tolerate it.
- **Failed reads overwrite good data:**
  - A catch that continues with defaults, followed by a save, replaces the real file with defaults.
  - A failed read must keep what it had and must never be saved (REVIEW.md `READ-DEFAULT`).
- **Exit codes and results of tools:** a script or tool that fails must say so to its caller: a non-zero exit code, a failed status in its record, a thrown error. A tool that logs an error and exits 0 makes every caller treat the failure as success. A gate that can't run must never report PASS (deep_tools uses exit 1 for "could not run").
- **User-facing error text:**
  - Says what to do, in plain words.
  - Doesn't leak paths, tokens, webhook URLs, connection strings or other users' data.
  - For a dropped connection, the state shown must change. Real: Atomic.FM, where a dropped stream kept "playing" silence because `PadWithZeroes` defaulted on and `PlaybackStopped` was never handled (L36).

## How to confirm

- **For each failure path:** name the input or state that triggers it, and the code that runs. Where a runtime check is practical, force the failure in a harness or a scenario: a missing file, a read-only folder, a bad URL, a null session, a cancelled token, a child process that exits non-zero or hangs.
- **Off-by-default features** must be turned on and broken on purpose, for example Discord logging with a dead webhook (REVIEW.md).
