# Game API: Space Engineers specifics

Pass P6, after [core host-api](../../checks/core/host-api.md). The se-mod and pb-script packs reuse the sections on threading, entities and blocks, identities, and timing. The game has no nullable annotations and no thread safety, and much of its behaviour is only visible in its decompiled code. For any game member a changed line uses, read it with **se-dev-server-code** (server plugins) or **se-dev-game-code** (clients), and use the matching `*-book` skill first if it's installed. Don't answer a server question from client code.

<a id="threading"></a>
## Threading

- **Thread affinity:** game state must be touched on the game thread. PluginCheck's `threads` stage lists background use. Read every lead, plus every `threadSensitive` member.
- **Lookups off the game thread:** `MyEntities.GetEntityById`/`TryGetEntityById` from a background thread can return null, because `MyEntities.Load` can leave entities in the game thread's `[ThreadStatic]` list. Look entities up on the game thread, and pass IDs or snapshots to background work. (DR032, REVIEW.md)
- **Members that depend on the calling thread:** for example, `MyCubeGrid.BigOwners` gives a subgrid with no owned blocks its parent's owners on one path. Read the game code each `threadSensitive` entry names.
- **`MySandboxGame.Static.Invoke` vs `MyAPIGateway.Utilities.InvokeOnGameThread`:** both queue work. Neither runs it now. Code that needs the result must await completion, and work that must not run late must check its session and cancellation when it runs. See [threading.md](threading.md).
- **Flags set and cleared inside one game call:** a flag like `MySession.ServerSaving` can never be seen as true from the same thread (REVIEW.md).

## Entities and blocks

- **Lifetime:** entities can be closed or marked for close between frames. Check `Closed`/`MarkedForClose` before use, and never keep strong references across frames without handling `OnClose`.
- **Grid changes:** splits, merges, ownership changes and block replacement swap the objects you hold. A cached grid or block reference can point at a dead object.
- **Functional state:** `IsFunctional`, `Enabled` and `IsWorking` differ. Integrity versus accumulated damage decides when a block is removed. Real: APBL matched `MySlimBlock.DoDamage`'s condition.
- **Values the game sets late:** entity add events fire before the caller finishes setting the entity up, so a value read in the handler can still be the default (REVIEW.md).
- **Properties that don't sync:** setting a property on the server doesn't always replicate. Use the terminal property or the replicated path the game uses, and check it in the game code.
- **CustomData:** parse it once and cache it, re-parsing on `CustomDataChanged`. Never parse it per frame (Atomic.FM PluginHub#175, L28).

## Identities and players

- **IDs:** Steam ID (ulong), identity ID (long), player ID and entity ID are different things. A key of 0 for every entry makes "per player" mean "everyone" with no visible error (REVIEW.md).
- **Who ran the command:** a command from the console has no player, so `Player`/`Caller` are null. A command from an auto command step runs as the console caller. A dead player has no character. NPC identities have no Steam ID.
- **Privileges:** admin level checks, `MySession.Static.IsUserAdmin`, promote levels and Torch per-command permissions. Magnetar has no per-command permission override (REVIEW.md).

## Server versus client

- **What exists where:**
  - On a dedicated server, `MyAPIGateway.Session.Player` is null, there's no GUI and no `MyAudio` output, and `Sandbox.Graphics` and `VRage.Input` types must not be used (ARCH002 flags them in server code).
  - On a client, the player isn't authoritative, and changes are corrected by the server. Fairness rules come from se-dev-plugin-review `ReviewPolicy.md`.
- **Network messages:** validate the sender. Never trust an identity claimed in the payload. Rate limit, and size limit.
- **Safe zones, ownership and permissions:** server code acting for a player must apply the same checks the game would.

<a id="reflection-into-game-privates"></a>
## Reflection into game privates

- **Version changes:** every private field or method reached by name can be renamed, moved or change visibility in a game update. Resolve it at Init, throw a clear error there, and never use `!` to hide a failed lookup. (DR031, REVIEW.md)
- **BindingFlags:** they must match the member's real visibility and static-ness. Check against the decompiled game.
- **Workarounds:** a call that skips the game's public method also skips its side effects, such as sync, events, ownership and statistics. Read the public method, and say which side effects are lost (REVIEW.md). Real: APBL's Storage loss on recompile of a turned-off PB, magnetar-hub#126 (L13).
- **Reflection instead of typed calls:** calling a library through reflection (Atomic.FM's NAudio) hides missing members until run time, and skips the compiler's checks (DR031, L30).
- **Redundant patches:** see [harmony.md](harmony.md).

<a id="timing"></a>
## Timing

- **Ticks:** 60 simulation ticks per second at sim speed 1.0. Tick-counted delays stretch when sim speed drops. Use wall time (`Stopwatch`, `DateTime.UtcNow`) where real seconds are meant, and ticks where simulation time is meant. Say which one the setting text promises.
- **Update frequency:** `Update1`, `Update10` and `Update100` call frequencies decide how often a punishment, message or scan repeats. Real: APBL magnetar-hub#156, where Damage punishment repeated on every PB run, 60 times a second (L11).
- **Saves:** they run on the game thread. Serialization work in `BeforeSave` adds directly to the save stall.

## Commands (Torch and Magnetar)

- **Matching:** Torch finds command arguments by searching the whole line, so `on` in `!monitor everything on` is found inside "monitor" (Agent.md `TORCH-ARGS`). Command names are matched case by case.
- **Reserved words:** `help` is reserved under every Magnetar command root (REVIEW.md).
- **Output from server-run commands:** replies are lost when the server runs a command through Torch's `HandleCommandFromServer(string)`. Use the callback overload (REVIEW.md).
- **Failed auto steps:** `RunCommand` returns false and nothing is logged. Every auto step needs an error path (REVIEW.md).
