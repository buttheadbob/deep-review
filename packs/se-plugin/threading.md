# The game thread

Pass P3, after [core concurrency](../../checks/core/concurrency.md). Most concurrency defects in plugins are timing- or runtime-dependent: they pass every test on one runtime and fail on the other ([loader-compile.md](loader-compile.md)). The game's own thread rules for specific members are in [game-api.md](game-api.md#threading).

When you place each member on a thread in P2, the threads here are: the game thread, the thread pool, a timer, a dedicated thread, a network callback, and (Torch) the WPF UI thread.

## The game thread and async

- **Game state is touched on the game thread only.** PluginCheck's `threads` stage lists background use. Read every lead, plus every `threadSensitive` member.
- **Code after an `await` is not on the game thread.** The game thread has no `SynchronizationContext`, so continuations resume on the thread pool on both runtimes. `ConfigureAwait(true)` does not bring them back. Any game state touched after an `await` must go back through the game-thread invoke. PluginCheck's `threads` stage follows this; read every lead it gives.
- **`ConfigureAwait(false)`:** mostly irrelevant here, because there is no context to capture. Don't "fix" its absence, and don't count its presence as thread safety.
- **Inline continuations on the game thread:** on net48, a cancelled run's continuation can run inline on whatever thread cancelled it, so a cancel called from the game thread runs the continuation there. Real: SensEssentials `WorldSession.Next()` sat outside the class's own `Run(...)` guard, among guarded calls (L04).

## Locks and the game thread

- **Game-thread deadlock:** a background thread holds a lock and waits for game-thread work (`MySandboxGame.Static.Invoke` plus a wait, `InvokeAsync(...).Wait()`), while the game thread waits on the same lock. This hangs the server with no exception. Trace both directions.
- **Holding a lock across a wait** on the game thread freezes the simulation.
- **`System.Threading.Lock`:** Shared code compiled for Legacy or Torch must keep using an `object` lock.

## Queued game-thread work

- **Queued, not run:** `MySandboxGame.Static.Invoke` and `MyAPIGateway.Utilities.InvokeOnGameThread` both queue work. Neither runs it now ([game-api.md](game-api.md#threading)).
- **Check right before running:** a queued game-thread callback must check the token and the session it was started in **right before it runs**, not only when it was queued. (Real: SensEssentials quasar-hub#18, a queued cleanup step ran after cancel and after unload, L05.)
- **Delays count from execution:** "queue the command, then start the delay" means the delay counts from queueing, not from execution. On a busy server, several steps then run back to back. Await the game-thread work, then start the delay. (Real: quasar-hub#18, L06.)

## Timers, events and parallel work

- **Timers** (`System.Threading.Timer`, `System.Timers.Timer`) fire on the thread pool, not the game thread. A timer callback that touches game state must queue to the game thread.
- **Events raised from background threads:** handlers written for the game thread then run on the pool.
- **`Parallel.For`/`ForEach`, PLINQ and `Task.Run` loops** must not touch game objects. The game's `ParallelTasks` has its own rules; read the game code.
- **Torch's WPF UI:** bound properties set from the game thread or the pool cross threads; Torch can also run with no UI at all ([torch](torch/README.md)).

## How to confirm

- **Runtime-matrix:** `GameThread` in [`tools/runtime-matrix`](../../tools/runtime-matrix) is a stand-in for the game thread: one thread with no `SynchronizationContext` draining a queue. Paste the members under test, stub the game, and run on net48 and net10. The worked example `examples/cancel-reentrancy` reproduces SensEssentials `AutoCommand.Cancel` at 0b51e8a (L01).
- **PluginCheck:** a behaviour scenario with two concurrent runs and a cancel during a delay, requested through the user.
