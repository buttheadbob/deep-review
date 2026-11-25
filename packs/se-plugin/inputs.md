# Plugin inputs, commands and permissions

Pass P9, after [core inputs](../../checks/core/inputs.md).

Every value from outside the plugin is an input: chat and console commands, auto command steps, Quasar UI fields, config files, profiles, network messages, CustomData, block names, GPS text, file contents and environment variables. For each changed member that takes one, trace it from where it enters to every place it is used.

## Game values

- **IDs:** a Steam ID versus an identity ID versus a name ([game-api.md](game-api.md#identities-and-players)). A command that takes either must say which one it resolved, and must refuse when the input is ambiguous.
- **Names:** grid, player and faction names can match several entities, or none. An inverted "not found" condition can match everything on a typo. Try `notype` with a misspelled value; names must be checked before grids are (REVIEW.md).
- **Player-editable text:** CustomData, block names and GPS names are written by players. Treat them as hostile wherever they go: commands, chat, Discord, file names.

## Commands and permissions

- **Permission levels:**
  - Each command's permission level matches what it can do. A command that can delete grids, change money, kick, ban or run other commands is admin-only.
  - Commands are Admin or player level only, and authority flows down (MasterRules 23).
  - For each command, write down who can run it: player, admin, console, auto step, Quasar.
- **Commands running other commands:**
  - Auto steps, presets and macros run as the console caller, with full rights.
  - A player-editable value must never reach a command string run with admin rights. That's command injection: CustomData, block names or GPS names concatenated into a command line.
- **In-game-only checks:** these need a `player` line and an `admin` line in PluginCheck's classification, or they haven't been tested (Agent.md).
- **Rate limits:** commands a player can spam that cost server time (scans, exports, Discord posts) need a cooldown.
- **Torch argument matching:** `TORCH-ARGS` warnings mean a command didn't get what was typed (Agent.md, [torch](torch/README.md)).
- **Replies:** confirm what was done, with the resolved target. Real: SensEssentials magnetar-hub#172, where `cancelauto` replied "cancelled" and then failed.

## Network and UI inputs

- **Network messages:** validate the sender against the transport's sender ID, never against the payload. Check size, version and rate.
- **Quasar UI values:** the server must validate them again. The UI's ranges and authorization (`CanEditConfigs` and similar) protect the page, not the companion channel ([quasar](quasar/README.md)).
- **Discord webhooks:** SensEssentials restricted them to the Discord domains, and never logs the full URL.

## How to confirm

- **Commands:** for each changed handler, write a table: input, then the parsed value, then the check, then the effect. Run the edge rows through PluginCheck examples (requested through the user), or a harness for pure parsing code, on both runtimes, since culture differs.
