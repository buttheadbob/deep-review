# Mod scripts: the game's compiler and whitelist

Passes P4, P6 and P10. The game compiles every mod's `Data/Scripts` C# files itself, at world load, on every client and server that loads the mod, with its own compiler settings and a whitelist of allowed namespaces, types and members. deep_tools compiles mod scripts the way the game does (C# 6, game assemblies), but nothing here checks the whitelist: the gate reports it as a gap every time. So this pass is done by reading, and proven by a load in the game.

## The compile

- **Language version:** the game's mod compiler accepts C# 6 (deep_tools compiles mods as C# 6; SenX KOTH's Agents.md: "Mods are language version 6"). Anything newer is a compile error for every player. Look for: `out var`, tuples and deconstruction, pattern matching (`is T x`, `switch` on types, switch expressions), local functions, `default` literals, `in` parameters, `ref` locals and returns, `private protected`, `??=`, ranges and indices, records, `init`, target-typed `new`, file-scoped namespaces, collection expressions and raw string literals. DeepScan's C# 6 compile flags these; read every error.
- **No project settings:** the game ignores the csproj. `<LangVersion>`, `<Nullable>`, analyzers and defines in it apply to the IDE only, the same trap as the hub loaders ([se-plugin loader-compile](../se-plugin/loader-compile.md#nullable-in-the-loader-compile-dr030)).
- **One failure stops the mod:** a compile or whitelist error in any file means the mod's scripts don't run at all, on that machine, and the game log names the file and line. A mod that compiles on the server but fails on a client (or the reverse) splits behaviour between them.

## The whitelist

- **Where it is:** the game registers the allowed namespaces, types and members in its script whitelist (`MyScriptWhitelist`, with batches that call `AllowNamespaceOfTypes`, `AllowTypes` and `AllowMembers` for a `MyWhitelistTarget`: `ModApi` for mods, `Ingame` for PB scripts, or both). Read the registration with se-dev-game-code, at the game version the servers run.
- **Check every used type:** for each BCL or game type and member a changed line uses, find the registration that allows it. A namespace allowed for some types is not allowed for all its types; a type allowed does not mean all its members are.
- **What mods use instead:** file access goes through `MyAPIGateway.Utilities` (world, local and global storage readers and writers); background work goes through `MyAPIGateway.Parallel`, with results applied on the game thread; reflection, process starts, raw threads, sockets and P/Invoke are not available. Code that needs any of them is plugin code, not mod code.
- **Shared with other projects:** a file compiled both as a mod script and in a plugin or test project must keep to the mod rules, or each project gets its own copy.
- **Embedded libraries:** a library compiled into the mod (the Rich HUD client, `RichHudFramework.Client`) is compiled under the same rules, and its licence notice ships with the mod (CaptureZones keeps the RHF MIT notice in THIRD-PARTY-NOTICES).

## Security

The whitelist is the sandbox that protects players: a mod runs on every player's PC. The review checks that the mod doesn't try to get around it: no obfuscated code, no encoded payloads, no attempts to reach blocked APIs through allowed ones, and no data sent off the server beyond what the mod's description discloses.

## How to confirm

- **Read, then load:** list every non-game type the change uses with the whitelist line that allows it, then load the mod in a headless dedicated server and quote the log's compile result. A type you couldn't find in the whitelist is `unable to verify` until the load passes.
