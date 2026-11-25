# The mod API

Passes P3, P5 to P9, P11 and P12. Read each API call's game side with se-dev-game-code or se-dev-server-code before claiming what it does. The general game rules (threading, entity lifetime, identities, timing) are in [se-plugin game-api](../se-plugin/game-api.md).

<a id="where-mod-code-runs"></a>
## Where mod code runs (P3)

- **Every machine runs the mod.** Each client and the server load their own copy and run the same scripts. For each changed feature, decide which side acts, and check the guard: `MyAPIGateway.Session.IsServer`, `MyAPIGateway.Multiplayer.IsServer`, `MyAPIGateway.Utilities.IsDedicated`.
  - Server work that also runs on clients happens once per machine: N players mean N spawns, N payments, N messages.
  - Client work that runs on a dedicated server meets nulls: `MyAPIGateway.Session.Player` is null there, and there is no HUD, input or audio.
  - Offline, one process is both server and client, so both branches run there. Check nothing applies twice.
- **The game thread:** mod code runs on the game thread. `MyAPIGateway.Parallel` runs work on worker threads and hands a callback back to the game thread. The worker part must not touch entities, the same rule as for plugins ([se-plugin threading](../se-plugin/threading.md)).

<a id="components-and-lifecycle"></a>
## Components and lifecycle (P5)

- **Session components** (`MySessionComponentBase` with `[MySessionComponentDescriptor(...)]`): `LoadData`, `Init`, `BeforeStart`, the update methods the update order enables, `Draw`, `SaveData`, `UnloadData`. Check the update order flags match the methods the component relies on, and that `UnloadData` undoes everything the load methods did.
- **Game logic components** (`MyGameLogicComponent` with `[MyEntityComponentDescriptor(...)]`): one instance per matching block. `Init` runs before the block is fully in the world, so work that needs the grid waits for `UpdateOnceBeforeFrame` (requested through `NeedsUpdate`). A component attached to a common block type runs on every such block in every world: its per-tick cost is multiplied by that count.
- **Statics:** a mod's assembly stays loaded after the world unloads, so its statics keep the old session's entities and handlers. Null or clear every static in `UnloadData` (core DR010).
- **Events:** every handler added to a game publisher (`MyAPIGateway.Entities.OnEntityAdd`, `MyAPIGateway.Utilities.MessageEntered*`, `MyVisualScriptLogicProvider` events, terminal control getters, grid and block events) is removed in `UnloadData` or on the entity's close, with the same handler instance (DR011).
- **Entities close any frame:** check `MarkedForClose`/`Closed` before use, and drop references on `OnClose` ([se-plugin game-api](../se-plugin/game-api.md#entities-and-blocks)).

<a id="networking"></a>
## Networking (P9, P11)

- **Secure handlers:** register with `MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(channel, handler)`, which gives the handler the sender's Steam ID from the transport. Never trust a sender ID inside the payload. Unregister in `UnloadData` with the same handler.
- **Channel ids are shared** with every mod in the world. Pick a distinctive one, and parse defensively: a message from another mod on the same channel must be ignored, not crash the handler.
- **The server validates every client message:** is the sender allowed (`MyAPIGateway.Session.GetUserPromoteLevel(steamId)` for admin actions), does the target exist, does the sender own or have access to it, is it in range, and is the rate sane.
- **Serialization:** `MyAPIGateway.Utilities.SerializeToBinary` uses ProtoBuf. `[ProtoMember(n)]` numbers are never reused or renumbered, and subtypes need `[ProtoInclude]`. Clients and the server can briefly run different versions of the mod while the Workshop update spreads, so a new field must be optional on both sides.
- **Sync:** a setting changed on one machine stays local unless it travels: through a terminal property, a sync field on the game logic component, or the mod's own message. Check each changed setting reaches every machine that needs it.

<a id="chat-commands-and-terminal-controls"></a>
## Chat commands and terminal controls (P9)

- **Chat:** `MyAPIGateway.Utilities.MessageEntered` (or `MessageEnteredSender`) runs on the machine of the player who typed. Set `sendToOthers = false` for commands, and send anything with server effects to the server as a message, where it is validated like any other.
- **Terminal controls:** created once per block type, not per block, and guarded so a world reload doesn't add them twice. `Visible`, `Enabled` and getter callbacks run often, so keep them cheap and null-safe.
- **Admin menus:** a menu hidden from non-admins on the client is not enforcement; the server checks the promote level of each request (core authority-interop).

<a id="storage-and-config"></a>
## Storage and config (P8)

- **Files:** `MyAPIGateway.Utilities` world storage (per world), local storage (per machine) and global storage. Readers and writers must be disposed. Writes are not atomic: a crash mid-write leaves a broken file, so write a new name and switch over, and keep reading the old one if the new one fails (core [persistence](../../checks/core/persistence.md#writes)).
- **World saves:** `SaveData` runs on world save, on the game thread.
- **Entity storage:** `Entity.Storage` keyed by a GUID is saved only if the GUID is declared as a mod storage component in the mod's `EntityComponents.sbc`. Check the declaration exists for every key used.
- **CustomData:** parse with `MyIni` once and again on change. A parse failure never overwrites the player's text with defaults (core READ-DEFAULT).
- **Config by XML:** `SerializeToXML`/`SerializeFromXML`: enums by name, defaults omitted, and a failed read kept apart instead of replaced.

<a id="errors"></a>
## Errors (P7)

- **An unhandled exception in mod code can take down the game or the server, for every player.** Guard every update method, event handler, message handler and terminal callback. Log with `MyLog.Default.WriteLineAndConsole`, tell the player once, and turn the failing feature off instead of throwing again every tick (core error spam).

<a id="performance"></a>
## Performance (P12)

- **Multipliers:** per tick, times components, times blocks, on every machine. A cost that is fine on one block is not fine on every block of a common type.
- **Common costs:** `GetBlocks`/`GetFatBlocks` and entity queries allocate lists (reuse them); `MyAPIGateway.Entities.GetEntities` scans the world; physics casts per tick; HUD and `Draw` work per frame on clients; LINQ and string building in update methods.
- **Background work:** `MyAPIGateway.Parallel` for heavy pure computation, with results applied on the game thread.
