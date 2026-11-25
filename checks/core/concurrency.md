# Concurrency, async and locking

Pass P3. Read with [runtime-differences.md](runtime-differences.md): most concurrency defects are timing- or runtime-dependent, so they pass every test on one runtime and fail on another.

This file is the core for every project: .NET first, because most of the code here is C#, and the ideas carry to every language. Packs add their own threading model:
- the game thread: [se-plugin threading](../../packs/se-plugin/threading.md);
- mod and PB scripts: [se-mod](../../packs/se-mod/mod-api.md#where-mod-code-runs), [pb-script](../../packs/pb-script/checks.md#p3-and-p12-instruction-limit-and-per-run-cost);
- Blazor circuits, hosted services and UI dispatchers: [dotnet-app](../../packs/dotnet-app/README.md);
- callers' synchronization contexts: [dotnet-library](../../packs/dotnet-library/api-design.md#async-apis);
- the GIL, asyncio, threads and processes: [python](../../packs/python/checks.md#p3-concurrency);
- runspaces, jobs and `ForEach-Object -Parallel`: [powershell](../../packs/powershell/checks.md#p3-concurrency);
- the event loop: [web](../../packs/web/checks.md#p3-the-event-loop-and-async).

For every changed or affected member, write down first:
- which threads, tasks, event loops or processes can run it: a host's main thread (game, UI, renderer), the thread pool, a timer, a dedicated thread, a network callback, another process;
- which locks it takes;
- which shared state it reads and writes, including files and other processes' state.

A member you cannot place on a thread is a gap, not a pass.

## Locks and re-entrancy

- **Anything that can run other code while a lock is held is a lead.** That includes `CancellationTokenSource.Cancel`, `TaskCompletionSource.Set*`/`TrySet*`, `ManualResetEventSlim.Set`, `SemaphoreSlim.Release`, raising an event, invoking a delegate, running a command or step, and calling an interface or virtual member. Any of these can run a continuation or callback inline, on this thread, inside the lock. C# `lock` is re-entrant, so that code can re-enter the same lock and change the state the outer code is in the middle of using. (DeepRules DR001, DR003. Real: SensEssentials `AutoCommand.Cancel`, magnetar-hub#172.)
- **The fix pattern:** under the lock, copy what you need and update the state. Release the lock, then do the calls.
- **Iterating shared collections:** a `foreach` over a field collection is safe only if nothing reachable from the loop body can mutate that collection. That includes completions that run inline, events and callbacks, and the same lock re-entered. Otherwise snapshot it first with `ToArray()` under the lock. (DR002)
- **Lock objects:** private and readonly only. Never `this`, `typeof(...)`, a string, or anything public. (DR007)
- **Lock ordering:** write down every pair of locks taken nested. Two code paths that take the same two locks in opposite orders can deadlock.
- **Deadlock with a single owner thread:** a background thread holds a lock and waits for work it queued to a single owner thread (a game thread, a UI dispatcher, a Blazor circuit), while that thread waits on the same lock. This hangs with no exception. Trace both directions. The game-thread form is in [se-plugin threading](../../packs/se-plugin/threading.md).
- **`System.Threading.Lock` (.NET 9+)** does not exist on net48. Shared code compiled for net48 must keep using an `object` lock.
- **Holding a lock across a wait:** `Monitor.Wait`, `Thread.Sleep`, blocking I/O or a `.Result` inside a lock serializes everything behind it. On a single owner thread (game, UI), it freezes everything that thread runs.
- **Locks across processes:** a lock file, a named mutex or an exclusive open protects only against processes that use the same mechanism. Check what happens when the holder dies: a lock file left behind blocks every later run unless it carries an owner (PID plus start time) and a staleness rule. Two processes that both "check, then create" race unless the create is atomic (`O_EXCL`, `FileMode.CreateNew`).

## Async

- **`async void`:** outside a real `(object, EventArgs)` event handler, this is a lead. An exception there is rethrown on the thread pool and terminates the process on both runtimes, so the server or app dies. Callers also cannot await it, so "the step finished" can never be known. Async lambdas passed to `Action` are the same thing. A real event handler that is `async void` still needs its own try/catch. (DR004)
- **Fire-and-forget (`_ = DoAsync()`, a discarded Task):** every one needs an exception path that logs, and an owner that stops it on unload or shutdown.
- **Sync-over-async (`.Result`, `.Wait()`, `GetAwaiter().GetResult()`, `WaitAll`):** this blocks the calling thread. On a single owner thread it freezes that thread's work, and where a `SynchronizationContext` exists (UI, Blazor's renderer, legacy ASP.NET) it can deadlock. (DR005, VSTHRD002)
- **Where code after an `await` runs:** on the captured `SynchronizationContext` if there is one (a UI thread, Blazor's renderer), otherwise on the thread pool. A host thread with no context does not get its continuations back, whatever `ConfigureAwait` says. Write down which case applies for each `await` in a changed member, and check every piece of thread-bound state touched after it.
- **`ConfigureAwait(false)`:** library code uses it, because a caller may have a context and block on the task ([dotnet-library](../../packs/dotnet-library/api-design.md#async-apis)). App code running with no context gains nothing from it, so don't "fix" its absence there ([se-plugin threading](../../packs/se-plugin/threading.md)). App code with a context (UI, Blazor) must not use it before touching UI state. Never count its presence as thread safety.
- **Cancellation tokens:** a method that accepts a token must pass it to every wait and every downstream async call, and check it before side effects that can't be undone. A queued callback must check the token, and the owner it was started for (a session, a circuit, a request), **right before it runs**, not only when it was queued. (Real: SensEssentials quasar-hub#18, a queued cleanup step ran after cancel and after unload.)
- **Cancellation versus a completed result:** when a result can be recorded before the token is observed, define which wins. A later `IsCancellationRequested` check must not rewrite an answer the user already gave. If the contract says the token is authoritative, it must say so in the public docs and test both orders. (Real: Quasar PR2's message box returned `null` after a recorded Yes when the token fired during the native-close round trip.)
- **No completion path:** a `TaskCompletionSource`, channel or pending request that is completed only by a host, component, mount or callback must have a cancellation or abandonment path. A caller can wait forever when that consumer never arrives; that is a finding, not a documented limitation. (Real: Quasar PR2's message box without a mounted dialog host.)
- **The order of side effects and delays:** "queue the work, then start the delay" means the delay counts from queueing, not from execution. On a busy host, several steps then run back to back. Await the queued work, then start the delay. (Real: quasar-hub#18, a 1000 ms gap became 0.2 ms.)
- **`TaskCompletionSource` without `TaskCreationOptions.RunContinuationsAsynchronously`:** completing it runs every awaiter inline on the completing thread. Use the option, or complete it outside locks.

## CancellationTokenSource lifecycle

- **Disposing while still reachable:** a CTS stored in a field or collection can be disposed while another path can still call `Cancel` on it. That throws `ObjectDisposedException`. Either don't dispose a CTS that has no timer (`CancelAfter`) and no wait handle, or make every `Cancel` caller tolerate disposal. (DR006. Real: SensEssentials `RunNow`'s finally, found while fixing magnetar-hub#172.)
- **Linked sources:** `CreateLinkedTokenSource` must be disposed, because it registers on its parents. If it isn't, it leaks for as long as the parent lives. `using var` is the norm.
- **`CancellationTokenRegistration`:** from `token.Register(...)`, it must be disposed when the work ends. If not, the callback can fire long after.
- **Cancel callbacks run synchronously inside `Cancel()`,** on both runtimes, in reverse order of registration. An exception in one is wrapped in an `AggregateException` from `Cancel()`.

## Timers and threads

- **`System.Threading.Timer`:** callbacks can overlap when one runs longer than the period. They run on the thread pool, not on any owner thread. A timer not kept in a field can be collected and silently stop. It must be disposed on unload. (DR009)
- **`System.Timers.Timer.Elapsed`:** also runs on the thread pool, and also overlaps. `AutoReset` and `Stop` race with an `Elapsed` already running.
- **`PeriodicTimer` (.NET 6+):** doesn't overlap, but a tick that takes longer than the period is skipped, not queued. Check the code doesn't count ticks as elapsed time.
- **Dedicated threads:** each one needs a stop signal, a join with a timeout on unload, and catch-all logging at its root.
- **`Parallel.For`/`ForEach`, PLINQ and `Task.Run` loops:** these must not touch single-threaded objects: game objects, UI controls, an EF `DbContext`, non-thread-safe collections. The game's own `ParallelTasks` has its own rules ([se-plugin threading](../../packs/se-plugin/threading.md)).
- **Child processes:** a parent that redirects both stdout and stderr must read them concurrently (async reads, or one on another thread). Reading one to the end first deadlocks when the child fills the other pipe's buffer. Every wait on a child has a timeout, and a timeout kills the whole process tree, not only the direct child (`Process.Kill(entireProcessTree: true)` on .NET 5+; net48 has no tree kill).

## Shared state without locks

- **Check-then-act on `ConcurrentDictionary`:** `ContainsKey` followed by `Add`, or `TryGetValue` followed by `[key] =`, is a race. `GetOrAdd`'s factory can run more than once.
- **Flags read across threads:** these need `volatile` or `Volatile.Read`/`Interlocked`. A plain bool read in a loop can be hoisted by the JIT, so the loop never exits.
- **Unsynchronized lazy initialization:** of statics, used from more than one thread. (DR008)
- **Events raised from background threads:** handlers written for an owner thread (game, UI, circuit) then run on the pool.
- **Collections handed out from under a lock:** a reader enumerates the live list while a writer changes it. Return a copy.
- **Files as shared state:** read-modify-write of a shared file by two processes loses one update even when each write is atomic ([persistence.md](persistence.md#writes)). A lease file, a queue file or a status file written by several sessions needs one writer or a cross-process lock.

## How to confirm

- **.NET:** every Medium+ suspicion here gets a runtime-matrix harness: [`tools/runtime-matrix`](../../tools/runtime-matrix). Paste the members under test, stub the host, and run on net48 and net10, plus Mono if present. Show the failing runtime and the passing one. If it can't be reproduced, label it `unable to verify` and say what you tried.
- **Other languages:** the pack names the harness (a pytest file in a temp copy, a `pwsh -NoProfile` script, a node script).
- **Race windows:** widen them on purpose in the harness (a `Thread.Sleep` or a barrier at the window), and run many iterations. One clean run proves nothing.
