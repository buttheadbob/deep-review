# Plugin errors: what the hosts do with an exception

Pass P7, after [core errors](../../checks/core/errors.md).

## Entry points

- **Pulsar:** `PluginInstance.Update` has no try/catch around `plugin.Update()`, so an exception there reaches the game loop and ticks stop.
- **Magnetar:** nothing above a Magnetar plugin's `Update` catches file errors, so they reach the server (REVIEW.md, PluginCheck `storage` stage `UNGUARDED`).
- **Every entry point needs a guard** that logs and keeps the plugin's state consistent: per-tick methods (`Update*`, `UpdateBeforeSimulation*`, `UpdateAfterSimulation*`, `Draw`, `HandleInput`), game event handlers, Harmony prefixes, postfixes and finalizers, command handlers, timer and thread roots, and `Init`/`LoadData`/`UnloadData`/`Dispose`. (DR015 checks these names.)
- **Rethrow into Pulsar's loop:** Atomic.FM PluginHub#175 ended a catch with `throw;`; the ambient caller caught it, but the hotkey path reached `PluginInstance.Update` uncaught and crashed the client (L34).

## Game values that are null

- A dead player, the console caller, an unloaded session, an entity closed this frame. PluginCheck's `nulls` stage lists the game ones. Read every lead ([game-api.md](game-api.md#identities-and-players)).

## Failures that go unseen

- **`RunCommand`:** returns false and logs nothing. Every auto step needs an error path (REVIEW.md, [game-api.md](game-api.md#commands-torch-and-magnetar)).
- **Off-by-default integrations:** turn them on and break them on purpose, for example Discord logging with a dead webhook (REVIEW.md).
- **Chat and HUD spam:** a failure shown to players per tick or per PB run floods them. Real: APBL magnetar-hub#156, 60 chat messages a second (L11).

## How to confirm

- **Force the failure in a PluginCheck scenario** (requested through the user): a missing file, a read-only folder, a bad URL, a null session, a command from the console with no player. Read every reply.
