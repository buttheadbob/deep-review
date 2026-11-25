# Plugin lifecycle: worlds, sessions and reloads

Pass P5, after [core lifecycle](../../checks/core/lifecycle.md). Harmony has its own file: [harmony.md](harmony.md).

Plugins live through many state changes that tests rarely cover:
- world load, unload, and loading another world in the same process;
- server stop;
- plugin disable and reload;
- client reconnect;
- a Magnetar config change applied while running;
- a Quasar profile applied to a server that is offline or still loading.

For each changed or affected member, ask what happens if it runs in each state.

## Checks

- **Init:**
  - Partial failure: if `Init` throws halfway, what is left registered, such as commands, patches, events, timers or threads? Does `Dispose` still run, and can it cope with half-initialized fields?
  - Magnetar's `RegisterAssembly` stops at the first exception, so one bad command module removes every module after it (REVIEW.md, regcheck).
- **Null windows:** `MySession.Static`, `MyAPIGateway.Session`, `MyMultiplayer.Static`, `MySandboxGame.Static` and `MyAPIGateway.Entities` are null, or half-built, before load and after unload. A command run from the console during startup, a timer firing during unload, or an event delivered late all hit these windows.
- **Work that outlives its world:** queued game-thread work, timers, `Task.Delay` continuations, retries and background loops must stop, or check that they still belong to the current world, right before they act.
  - Real: SensEssentials quasar-hub#18. A queued cleanup step ran after `cancelauto` and after the session started unloading. A "server running" check alone would still let old work act in the next world loaded in the same process (L05).
  - The fix is a session identity, captured at start and rechecked before acting.
- **Statics:** static fields, static collections and static events survive world unload, and plugin reload in the same process. They then keep references to closed entities and old sessions, plus handlers that run twice. Every static must be reset in `Dispose`/unload, or be immutable. (DR010)
- **Events:** every `+=` on a static or longer-lived publisher (game events, `MyAPIGateway.*`, `MyEntities`, `MySession`, another plugin) needs a `-=` with the same handler instance on unload. Handlers that are subscribed twice, for example in `Init` and again in `LoadData`, fire twice. (DR011)
- **Process-wide hooks:** a plugin shares the process with the game and every other plugin, so `AppDomain.AssemblyResolve` and its kind affect them all. (DR012. Real: Atomic.FM PluginHub#175 added an AppDomain-wide `AssemblyResolve` and never removed it, L29.)
- **Dispose:**
  - It is idempotent, and it never throws: an exception in `Dispose` can stop the host's unload of other plugins.
  - It stops threads with a bounded join, disposes timers, cancels and drains outstanding work, flushes and closes files, and puts back game state it changed. Real: DoomForSe restored `MyAudio.Static.VolumeMusic` on close and in `Dispose`.
- **Config changes applied live:**
  - The old and new config can briefly coexist. Commands removed from the config must stop their running work. Real: SensEssentials `MagnetarConfig` cancelled removed commands, and that loop stopped early on the first throw.
  - A setting that "applies at once" must apply at once, and the text must say which settings need a restart (REVIEW.md).
- **Offline or loading targets:** a change sent to a server that is offline or still loading must either queue until it is ready or fail visibly. Real: the APBL profiles feature handles "offline or still loading" by applying the profile when the server is ready. Verify that path.
- **Client-specific:**
  - A reconnect, or leaving and joining another server, re-runs world load in the same process.
  - GUI screens must be closed on unload.
  - Hotkeys must be ignored while a text field, chat or terminal has focus. Real: Atomic.FM PluginHub#175, where the hotkey fired while typing (L31).
- **Clusters (Quasar):**
  - In-memory state per player or per grid stays behind when a grid moves to another node.
  - Node-local files are not seen by other nodes.
  - See PluginCheck REVIEW.md "Clusters and network mounts" (`NODE-STATE`, `NODE-FILE`).

## How to confirm

- **Read the host's code** for the order of calls: Magnetar's plugin host and session events in `Public\magnetar`, Pulsar's `PluginInstance`, Torch's plugin manager and session states (se-dev-torch). Use the se-dev-server-code/se-dev-game-code skills to read the game's session load/unload order.
- **Reload paths:** PluginCheck behaviour scenarios can restart a world. Where a lifecycle defect needs a reload to show, request a scenario that loads, unloads and loads again, and check for doubled handlers and stale references in the log.
