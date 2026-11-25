# Inputs, operations and permissions

Pass P9. Every value from outside the code under review is an input: commands, request parameters, form and UI fields, config files, profiles, network messages, files written by others, environment variables, command-line arguments, records handed over by another session, and anything read from a repository under review. For each changed member that takes one, trace it from where it enters to every place it is used.

Packs add their entry points: chat and console commands, auto steps, CustomData and Quasar fields ([se-plugin inputs](../../packs/se-plugin/inputs.md)), mod messages and chat ([se-mod](../../packs/se-mod/mod-api.md#networking)), PB arguments and IGC ([pb-script](../../packs/pb-script/checks.md#p9-arguments-and-igc)), model binding, route and query values ([dotnet-app web-security](../../packs/dotnet-app/web-security.md)), public method arguments ([dotnet-library](../../packs/dotnet-library/api-design.md#arguments)), argv, files and records ([python](../../packs/python/checks.md#p9-inputs-and-paths)), parameters and pipeline input ([powershell](../../packs/powershell/checks.md#p9-parameters-and-paths)), URL, DOM and message input ([web](../../packs/web/checks.md#p9-inputs)).

## Values

- **Numbers:**
  - Parse with `TryParse` plus `CultureInfo.InvariantCulture` and an explicit `NumberStyles`, and check the result.
  - Then bound-check: negative, zero, huge, `NaN`, `Infinity` and `-0`.
  - Money, counts, balances and scores need overflow checks (`checked`, or clamp) and a rejected negative amount. Real: SensEssentials `!econ give/take/set` once accepted negative amounts, and balances could pass the largest value. (DR022)
  - 64-bit IDs (Steam IDs, Discord snowflakes) lose precision in a JavaScript number or a JSON parser that reads doubles. Keep them as strings or 64-bit integers end to end.
- **Times:**
  - `TimeSpan.Parse("24:00:00")` is 24 days.
  - Blank or unreadable values become zero, and a zero interval loops as fast as it can.
  - Use one strict parser for every time field (REVIEW.md). (DR019)
- **Names:**
  - A name can match several things, or none: grids, players, users, files, processes.
  - Matching rules: case, partial matches, ordinal versus culture.
  - An inverted "not found" condition can match everything on a typo. Try a misspelled value and check what it matches (REVIEW.md).
- **IDs:** an input that can be either of two ID kinds, or a name, must say which one it resolved, and must refuse when the input is ambiguous. The game's ID kinds are in [se-plugin inputs](../../packs/se-plugin/inputs.md).
- **Strings:** empty, whitespace, very long, containing quotes, newlines, `<`, `&`, color codes, control characters, or the command prefix itself. Text echoed back to chat, Discord, a log or a page must not let a user inject formatting, mentions (`@everyone`), log lines, markup or commands.
- **Regex from input:** must have a timeout (`RegexOptions` plus a `TimeSpan`) and a length limit (ReDoS, MA0009).
- **Structured input** (JSON, XML, YAML, records): check types as well as values. A field that should be a bool can arrive as a number or a string, a list as an object, a required field missing. Reject what doesn't match the schema instead of coercing it.

## Paths

- **`Path.Combine(root, input)`:**
  - Silently returns `input` when `input` is rooted: `/etc/x`, `C:\x` or `\\server\x`. `..` walks out of `root`. Python's `os.path.join` and `pathlib` `/` behave the same way. PowerShell's `Join-Path` doesn't drop the root, but doesn't stop `..` either.
  - Normalize with `Path.GetFullPath`, then check the result starts with the full root plus a separator. Or accept a bare file name only (`Path.GetFileName(input) == input`).
  - Real: SensEssentials `!grids export/import` accepted names pointing outside ExportedGrids. (DR021)
- **Invalid file name characters:** differ on Windows and Linux. Reserved names on Windows: `CON`, `NUL`, `COM1`.
- **Archives:** extraction must check every entry's full path, to prevent zip slip, and must not follow symbolic links in the archive.
- **Links and junctions:** a path inside the root can point outside it through a symbolic link or a junction. A recursive delete or copy that follows them acts on the target.

## Operations and permissions

- **Permission levels:**
  - Each operation's permission level matches what it can do. An operation that can delete data, change money, kick, ban, run other operations or reach the user's machine is admin-only.
  - For each operation, write down who can run it: anonymous, user, admin, console, an automated step, another process.
  - Authority flows down: a lower level never gains a higher level's rights through an operation (MasterRules 23).
- **Operations running other operations:**
  - Automated steps, presets and macros often run with full rights.
  - A user-editable value must never reach a command string, shell line, query or script run with higher rights. That's command injection.
- **Rate limits:** operations a user can spam that cost server time or reach an outside service (scans, exports, Discord posts, API calls) need a cooldown.
- **Replies:** confirm what was done, with the resolved target. Never say "cancelled" when the action threw afterwards. Real: SensEssentials magnetar-hub#172, where `cancelauto` replied "cancelled" and then failed.

## Network and UI inputs

- **Network messages:** validate the sender against the transport's identity, never against the payload. Check size, version and rate.
- **UI values:** the server or back end must validate them again. A UI's ranges and authorization protect the page, not the channel behind it. Real: Quasar UI values reach the Magnetar companion ([se-plugin quasar](../../packs/se-plugin/quasar/README.md)).
- **URLs from config:**
  - Allowlist hosts where the code posts data. Real: SensEssentials restricted Discord webhooks to the Discord domains.
  - Don't log the full webhook URL, since it is a secret.
- **Environment variables and hidden switches** that change behaviour must be documented, or removed before release. Real: DoomForSe's `DOOMFORSE_*` test overrides were removed after review.
- **Repositories under review are input:** a tool that reviews or builds someone else's code (PluginCheck, deep_tools) treats every file, config and name in it as hostile: no config of theirs is used, no script of theirs runs on the host, and no file name of theirs reaches a shell.

## How to confirm

- **Operations:** for each changed handler, write a table: input, then the parsed value, then the check, then the effect. Run the edge rows through a harness for pure parsing code, on every runtime, since culture differs. For plugins, PluginCheck examples cover commands.
- **Paths:** feed `..\..\x`, `/etc/passwd`, `C:\x`, `\\h\s\x`, a name with an invalid character and a link pointing outside. Report what happens on Windows and on Linux.
