# PB script checks

Read the core file for each pass first. The game's rules for entities, identities and timing are in [se-plugin game-api](../se-plugin/game-api.md); a PB script sees the same grid through the smaller Ingame API.

<a id="p4-compile-whitelist-and-size"></a>
## P4: compile, whitelist and size

- **Language and whitelist:** the game compiles PB scripts as C# 6 (deep_tools compiles them that way) against the Ingame whitelist (`MyWhitelistTarget.Ingame`), which is smaller than the mod whitelist: no file access, no threads or tasks, no reflection, no `MyAPIGateway`. Check every non-Ingame type a changed line uses against the whitelist registration ([se-mod whitelist](../se-mod/whitelist.md#the-whitelist)). MDK2's analyzers flag many of these in the IDE build; read their results, and still check by hand, because the gate doesn't run them.
- **Size:** the game rejects scripts over 100,000 characters. What counts is the packed output MDK2 writes, not the source. Check the packed length after the change, with the project's minifier setting.
- **The packed output is what ships:** MDK2 merges the project's files into one script and, depending on the `minify` setting in the `.mdk.ini`, strips comments, whitespace and renames symbols. Read the packed script's diff too: a region the packer keeps or drops, or a name the minifier renamed but a string still uses, changes behaviour.
- **Local settings stay local:** `*.mdk.local.ini` holds the machine's output path and is not committed. Build output stays out of the game's folders (PAM's rule); the user publishes.

<a id="p3-and-p12-instruction-limit-and-per-run-cost"></a>
## P3 and P12: instruction limit and per-run cost

- **One thread, no async:** a PB script runs on the game thread in short runs. "Concurrency" here means work split across runs: a state machine (`IEnumerator<bool>` advanced one step per run) must leave consistent state between steps, and must cope with a block disappearing between them.
- **Instruction limit:** each run has a budget (`Runtime.MaxInstructionCount`). Going over throws "Script Too Complex" and stops the script until the player runs or recompiles it. Find the worst-case run: loops over every block, every inventory item, every waypoint, text built for every LCD. Spread large work over runs, and check `Runtime.CurrentInstructionCount` against a margin. `Runtime.MaxCallChainDepth` limits recursion the same way.
- **Server limiters:** servers run a PB limiter that measures run time (`Runtime.LastRunTimeMs`) and turns off or punishes scripts over a budget. The user's Advanced PB Limiter runs on UpsideDown and Stone Industries. Measure the average, not one run.
- **Update frequency:** `Runtime.UpdateFrequency` (`Update1`, `Update10`, `Update100`, `Once`). Update1 is 60 runs a second: multiply every per-run cost by it. `updateSource` is a set of flags: test with `&`, so a run with both a terminal argument and an update flag does both jobs once each, and an update run doesn't redo argument work.
- **Allocation per run:** new lists, LINQ, lambdas that capture, string concatenation and `string.Format` in `Main` all make garbage on the server, every run. Keep lists in fields and clear them; reuse one `StringBuilder` for `Echo` and LCD text.
- **Block discovery:** `GridTerminalSystem.GetBlocksOfType` and `SearchBlocksOfName` walk the grid. Cache the blocks, and refresh on a slow timer or when a cached block is closed or no longer accessible, not every run.
- **LCD text:** `WriteText` sends the text to clients. Write only when it changed.
- **CustomData:** parse it only when `Me.CustomData` changed since the last parse (compare the string), never every run. Real: per-frame CustomData parsing was a hub finding for plugins (L28).

<a id="p5-and-p8-lifecycle-and-storage"></a>
## P5 and P8: lifecycle and storage

- **The constructor** runs at compile, world load and every recompile. **`Save()`** runs on world save. **`Storage`** is the script's only persistence.
- **`Storage` format:** a version prefix, a tolerant parser, and a failed parse that keeps the script stopped with a clear message instead of starting over with defaults (core READ-DEFAULT). A new field must read old strings.
- **Empty `Storage` when it shouldn't be:** real case: APBL found Storage lost on recompile of a turned-off PB, because skipping a game step also skipped its side effects (L13). Check what the script does when Storage is empty on a grid that was running.
- **CustomData as config:** written by the player. Parse failures never overwrite it; write defaults only when it is empty, and keep the player's own lines.
- **Statics:** compiled scripts may be shared between programmable blocks running the same script text, so a static can carry state between blocks. Check in the game code before relying on statics at all; instance fields are safe.

<a id="p6-the-grid-api"></a>
## P6: the grid API

- **Blocks come and go:** destroyed, ground down, removed, or turned off between runs; a change of owner makes a block inaccessible. Check `Closed`, `IsFunctional`, `IsWorking` and `GridTerminalSystem.CanAccess(block)` as the feature needs ([se-plugin game-api](../se-plugin/game-api.md#entities-and-blocks)).
- **Which grid:** subgrids on rotors, pistons and hinges, and ships docked by connector, all appear in `GridTerminalSystem`. Use `IsSameConstructAs(Me)` or a grid check to keep to the intended construct.
- **Names, tags and groups:** duplicate names, tags in names (`[PAM]`), case, and a missing group (`GetBlockGroupWithName` returns null).
- **Inventory:** `TransferItemTo` returns whether it moved anything; conveyor connections decide whether it can (`CanTransferItemTo`). Amounts are `MyFixedPoint`.
- **Time:** `Runtime.TimeSinceLastRun` is game time and stretches when sim speed drops. Say whether a setting means game time or real time ([se-plugin game-api](../se-plugin/game-api.md#timing)).

<a id="p7-errors"></a>
## P7: errors

- **An exception stops the script** and shows in the PB's detail area until the player runs it again. Catch at the top of `Main` only for states the script can recover from, `Echo` the error once, and leave the block in a safe state (thrusters off, no half-finished transfer).

<a id="p9-arguments-and-igc"></a>
## P9 and P11: arguments and IGC

- **Arguments:** `Main(argument, updateSource)` gets text from toolbars, timer blocks, sensors, other scripts and any player with access to the block. Parse with `MyCommandLine`; an unknown or malformed command gets a clear `Echo`, not an exception.
- **IGC broadcasts reach any grid in antenna range, other players' included.** Check the data's type (`msg.Data is T`) and content, and the sender (`msg.Source`) where the feature is meant for the owner's own grids. Never act on another faction's broadcast unless that is the feature.
- **Tags:** a common broadcast tag collides with other scripts on the server. Use a distinctive one.

<a id="p14-release"></a>
## P14: release

- **The user publishes to the Workshop.** The packed script fits the limit, the version shown in the script and the description match, and the description says what the script needs (block names, tags, CustomData keys).

<a id="p15-tests"></a>
## P15: tests

- **Live:** each PAM fix gets a live before and after on the user's `E:\Torch` with the game client (the user's own setup), using a throwaway test plugin, and the game is closed when no test needs it (MasterRules 33a).
- **Unit:** pure logic can be tested in a separate project that references the script's classes and stubs the Ingame interfaces (MDK2's reference package). Tests can't catch instruction-limit or whitelist failures: those need the game.
- **Fixes against the published base:** PAM fixes start from the published base on a new branch in a worktree outside the repository, leaving the user's uncommitted changes on master as they are.
