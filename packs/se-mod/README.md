# Pack: se-mod (Space Engineers 1 mods)

**Loads when** gate.json's `projectKinds` contains `se-mod` (a `Data/Scripts` folder with `.cs` files). Load it too for a mod with no scripts that a review covers by hand (SBC and framework config only): gate.json will say `non-dotnet`, and the review records why the pack was loaded.

**What it adds:** the game's script compiler and whitelist (which nothing on this machine checks, so the gate always reports it as a gap and this review reads it by hand), the mod API (session and game logic components, networking, storage, terminal controls, chat), SBC definitions and framework config (MES, WeaponCore, Rich HUD), and Workshop release. A mod is a C# program that runs on the game thread of every client and the server at once, so the [se-plugin game API](../se-plugin/game-api.md) sections on threading, entities and blocks, identities and timing apply here as well.

**Stage 1:** the built-in `code-review` at high effort plus `security-review` ([Report.md](../../Report.md#the-stage-1-record)).

## Pass map

| Pass | Files |
|---|---|
| P0 | the Workshop item, the frameworks used (MES, WeaponCore, Rich HUD: ask when unclear, since servers differ), the servers it runs on |
| P2 | entry points: session component methods, game logic component methods, message handlers, chat handlers, terminal control callbacks, `MyAPIGateway.Parallel` callbacks |
| P3 | [mod-api.md](mod-api.md#where-mod-code-runs) |
| P4 | [whitelist.md](whitelist.md) |
| P5 | [mod-api.md](mod-api.md#components-and-lifecycle) |
| P6 | [whitelist.md](whitelist.md), [mod-api.md](mod-api.md), [se-plugin game-api](../se-plugin/game-api.md) (threading, entities, identities, timing) |
| P7 | [mod-api.md](mod-api.md#errors) |
| P8 | [mod-api.md](mod-api.md#storage-and-config), [definitions.md](definitions.md#definitions-are-save-keys) |
| P9 | [mod-api.md](mod-api.md#networking), [mod-api.md](mod-api.md#chat-commands-and-terminal-controls) |
| P10 | [whitelist.md](whitelist.md) (the whitelist is the sandbox), core security |
| P11 | [mod-api.md](mod-api.md#networking) (authority), [definitions.md](definitions.md) (other mods' ids) |
| P12 | [mod-api.md](mod-api.md#performance) |
| P13 | [definitions.md](definitions.md) |
| P14 | [definitions.md](definitions.md#workshop-release) |
| P15 | [Tests](#tests) below |
| P16 | Lessons rows with Pack `core` (no mod rows yet; add them as reviews find defects) |

## Skills

- `se-dev-mod` for mod code examples and patterns.
- `se-dev-game-code` and `se-dev-server-code` for the game's side of every mod API call, and for the whitelist registration itself.
- `se-dev-mes` for MES-based mods: SpawnGroup and profile tags, value types, defaults, spawn rules, MESApi.
- `se-dev-weaponcore` for WeaponCore definitions and WcApi. No WeaponCore fix work ever (the user, 2026-11-13); reviewing a mod's use of WeaponCore is fine.
- `se-dev-richhud` for Rich HUD Framework UI.

## Runtime tags

`Game client`, `Dedicated server`, `Offline` (single player: client and server in one process), `All`. A dedicated server may run on Windows or, under Magnetar Interim, on Linux.

<a id="tests"></a>
## Tests

- **Compile in the game:** the only real whitelist check is the game's own compile. Load the mod in a headless dedicated server (a container or a test copy, never `E:\Torch`) and read the log for compile and whitelist errors.
- **Multiplayer paths:** a headless dedicated server shows server-side behaviour. Client-side behaviour (HUD, input, Rich HUD menus) needs the game client, which needs the user's permission first (MasterRules 33a).
- **Offline copies:** a copy-to-Mods script for offline testing writes only to the local Mods folder. Nothing goes to the Workshop: the user uploads.
- **Unit tests:** pure logic can be tested in a separate project that compiles the same files with stubs, as long as the files keep to the whitelist.
