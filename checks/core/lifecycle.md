# Lifecycle: start, stop, reload, dispose

Pass P5. Code lives through state changes that tests rarely cover. In P0, list the ones that apply to the project kind:
- start, stop and restart of the process or host, including a crash and a forced kill;
- load and unload of the unit the code belongs to: a plugin in a world, a mod in a session, a Blazor circuit, a request scope, a hosted service, a script run;
- a reload in the same process: a plugin reload, a world change, a client reconnect, a hot reload, a module imported again, a script dot-sourced twice;
- a configuration change applied while running;
- a dependency that is offline, still starting, or gone.

Packs add the states of their hosts: world and session states and Harmony ([se-plugin lifecycle](../../packs/se-plugin/lifecycle.md), [harmony](../../packs/se-plugin/harmony.md)), mod components ([se-mod](../../packs/se-mod/mod-api.md#components-and-lifecycle)), PB recompiles ([pb-script](../../packs/pb-script/checks.md#p5-and-p8-lifecycle-and-storage)), circuits and hosted services ([dotnet-app](../../packs/dotnet-app/README.md)), interpreter and script exit ([python](../../packs/python/checks.md#p5-start-exit-and-cleanup), [powershell](../../packs/powershell/checks.md#p5-scope-exit-and-cleanup)), page and component lifetimes ([web](../../packs/web/checks.md#p5-page-and-component-lifetimes)).

For each changed or affected member, ask what happens if it runs in each state.

## Checks

- **Start and init:**
  - Partial failure: if start-up throws halfway, what is left registered, such as commands, patches, events, timers, threads, ports or temp files? Does the stop path still run, and can it cope with half-initialized fields?
  - A loop that registers many modules and stops at the first exception removes every module after it. Real: Magnetar's `RegisterAssembly` ([se-plugin lifecycle](../../packs/se-plugin/lifecycle.md)).
- **Null windows:** the host's services are null, or half-built, before start and after stop. A command run during start-up, a timer firing during shutdown, or an event delivered late all hit these windows. The pack names the host's objects.
- **Work that outlives its owner:** queued work, timers, `Task.Delay` continuations, retries and background loops must stop, or check that they still belong to the current owner, right before they act.
  - Real: SensEssentials quasar-hub#18. A queued cleanup step ran after `cancelauto` and after the session started unloading. A "still running" check alone would still let old work act in the next session loaded in the same process.
  - The fix is an owner identity (a session, circuit or generation id), captured at start and rechecked before acting.
  - The same shape elsewhere: a Blazor component disposed while an awaited call finishes and calls `StateHasChanged`; a hosted service loop still running after `StopAsync`; a callback from a child process after its job was cancelled.
- **Completion that depends on a mount:** a pending task, dialog or request whose result is completed only by a component, host, window or page mount is a finding when that consumer may never arrive. Track mount/unmount, complete or cancel pending work on unmount, and test the no-host path. (Real: Quasar PR2's dialog service left a canceled task pending forever when no `QuasarDialogHost` was mounted.)
- **Statics:** static fields, static collections and static events survive unload, and a reload in the same process. They then keep references to closed objects and old sessions, plus handlers that run twice. Every static must be reset on unload, or be immutable. (DR010)
- **Events:** every `+=` on a static or longer-lived publisher (the host's events, another plugin, a singleton service) needs a `-=` with the same handler instance on unload. Lambdas subscribed directly can't be removed. Handlers subscribed twice, for example in two init methods, fire twice. (DR011)
- **Process-wide hooks:** `AppDomain.AssemblyResolve`/`UnhandledException`/`FirstChanceException`, `TaskScheduler.UnobservedTaskException` and `Console.CancelKeyPress` affect every component in the process and the host. They must be removed on unload, and they rarely belong in a plugin or a library at all. (DR012. Real: Atomic.FM PluginHub#175.)
- **Harmony (DR013):** unpatch scope, ids and ordering are in [se-plugin harmony](../../packs/se-plugin/harmony.md).
- **Dispose and stop:**
  - Idempotent, and never throws: an exception in `Dispose` or a stop handler can stop the host from unloading or stopping everything else.
  - It stops threads with a bounded join, disposes timers, cancels and drains outstanding work, flushes and closes files, and puts back any state it changed in something it doesn't own. Real: DoomForSe restored `MyAudio.Static.VolumeMusic` on close and in `Dispose`.
- **Configuration changes applied live:**
  - The old and new configuration can briefly coexist. Work started under a setting that was removed must stop. Real: SensEssentials `MagnetarConfig` cancelled removed commands, and that loop stopped early on the first throw.
  - A setting that "applies at once" must apply at once, and the text must say which settings need a restart.
- **Offline or loading targets:** a change sent to a target that is offline or still loading must either queue until it is ready or fail visibly. Real: the APBL profiles feature applies a profile when the server is ready. Verify that path.
- **What the code starts, it cleans up:** child processes, containers, temp folders, copies, ports and lock files are removed on every exit path, failure and Ctrl+C included. Real: on 2026-11-15 a secret scan found 16 copies of a GitHub token left in PluginCheck's `runs/<name>/` folders (MasterRules 30a).
- **Two copies at once:** a second launch, or a watchdog restart before the old process has exited, must not corrupt shared files, ports or locks. Name what stops it (a named mutex, a lock with an owner, a port bind that fails cleanly).
- **Desktop focus on start and stop (MasterRules 33a):** start-up, shutdown and tests open no window and take no focus unless the feature is a visible app the user started ([host-api.md](host-api.md#the-desktop)).

## How to confirm

- **Read the host's code** for the order of calls. Each pack README says where: the plugin hosts, the game's session load and unload, ASP.NET Core's host and circuit lifetimes, the interpreter's exit handling.
- **Reload paths:** run start, stop, start in a harness or a scenario, and check for doubled handlers and stale references in the log. For plugins, PluginCheck behaviour scenarios can restart a world: request one through the user.
- **Cleanup:** force the failure path (kill the process, throw from the middle of start-up, cancel during a run) and list what is left behind.
