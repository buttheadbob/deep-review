# Persistence, configuration and migration

Pass P8. Everything a program writes outlives the build that wrote it, including the next version, a crash mid-write, and another process or node that shares the folder. For each changed member that reads or writes persistent data, such as config, saves, history, profiles, caches, records, databases or exports, go through the checks below.

Packs add their stores: the plugin data folder, Magnetar's config store and PluginCheck's storage leads ([se-plugin persistence](../../packs/se-plugin/persistence.md)), world storage, mod storage and SBC definitions ([se-mod](../../packs/se-mod/mod-api.md#storage-and-config), [definitions](../../packs/se-mod/definitions.md)), the PB's `Storage` string ([pb-script](../../packs/pb-script/checks.md#p5-and-p8-lifecycle-and-storage)), databases, migrations and data protection keys ([dotnet-app data-access](../../packs/dotnet-app/data-access.md)), file encodings per language ([python](../../packs/python/checks.md#p8-files-encoding-and-json), [powershell](../../packs/powershell/checks.md#p8-files-and-encoding)), browser storage ([web](../../packs/web/checks.md#p8-browser-storage-and-caches)).

## Writes

- **Atomic replace:**
  - Write to a temp file in the same folder, flush, then `File.Replace` (Windows) or `File.Move` with overwrite (.NET 10), or delete-then-move with a backup. In Python, `os.replace`; in PowerShell, `[IO.File]::Replace` or `Move-Item -Force` from a temp file in the same folder.
  - `File.WriteAllText`/`WriteAllBytes`/`File.Create`/`FileMode.Create` on the real file truncates it first, so a crash or power loss in that window leaves a broken file. The next start then runs on defaults, dropping limits and privileges the file held.
  - (DR016. Real: APBL magnetar-hub#170 `ReplaceDamage`, L09. Good example: DoomForSe wrote saves to `.tmp`, then swapped them in.)
- **Concurrent writers:** two threads, two commands, two processes or two cluster nodes writing the same file. On Linux `FileShare` doesn't stop them. Atomic replace stops a torn file, not a lost update: read-modify-write by two writers still loses one. Serialize writes, use a cross-process lock, or make the file per writer.
- **Location:** under the data folder the host provides, in a folder the program owns. Never next to the program's or the host's own files, which an update replaces, and never a folder guessed from the user profile when the host lets the user move it. The packs name the folder: `MyFileSystem.UserDataPath` for plugins, because the game supports `-appdata` ([se-plugin persistence](../../packs/se-plugin/persistence.md)).
- **Growth:** history, logs, caches and run folders need rotation or a cap. Something that grows per tick, per user or per run without bound fills the disk over a long-running server's life.
- **Encoding:** UTF-8 explicitly. `Encoding.Default` differs by runtime ([runtime-differences.md](runtime-differences.md)), and Python and Windows PowerShell 5.1 have their own defaults (the packs).
- **Culture:**
  - Numbers, dates and times in files and file names are formatted and parsed with `CultureInfo.InvariantCulture`.
  - Real: Monitor, where history file names and whole numbers changed with the server's culture. Fixed in buttheadbob/Monitor#3 (L46).
  - `double.ToString()` output also differs between runtimes. (DR018, CA1305/CA1304)
- **Secrets at rest:** a file that holds tokens or keys is written only where it must be, with permissions that keep other users out, and is never copied into run, output or log folders (MasterRules 30a).

## Reads

- **Failed reads:** keep the cached value and never save defaults over the real file (REVIEW.md `READ-DEFAULT`). Rename a corrupt file aside (`.bad`), log it once, and say what happened. Good example: DoomForSe.
- **Missing versus empty versus corrupt:** each is a different state. Handle each one on purpose.
- **Shared folders and network mounts:** a folder another process or node also writes can change between a check and a use, and network mounts fail in ways local disks don't ([runtime-differences.md](runtime-differences.md)). For plugins, read every PluginCheck `storage` lead ([se-plugin persistence](../../packs/se-plugin/persistence.md)).

## Formats and migration

- **Enums by number:**
  - An enum written as a number changes meaning when members are reordered or removed. A converter that accepts numbers must map them by the old enum's values.
  - Real: APBL magnetar-hub#170. The new `InstantPunishment {TurnOff, Destroy}` read `1` as Destroy, but in the old enum `1` was Damage (L10). (DR017)
- **Renamed or removed values:**
  - Old configs that hold a value the new code doesn't accept must migrate with a log line naming exactly which setting changed.
  - Real: APBL #170, where the JSON warning named both settings when only one held Damage (L08).
- **Default changes reach existing installs:**
  - A config store that writes only the values that differ from the default switches every existing install to a new default on update.
  - Real: APBL magnetar-hub#156, Destroy to TurnOff (L12). Say so in the release notes, or keep old defaults for old files the way the Torch `KeepOlderDefaults` path did.
- **File names and keys:**
  - A changed file name, path or key loses the user's data.
  - Real: DoomForSe PluginHub#349. The autosave name gained the WAD name, so existing saves were no longer found (L18).
  - Migrate the old file on first run, or keep reading the old name.
- **Schema versions:** a version field, or a tolerant reader. Unknown fields kept or ignored, not fatal. A newer file read by an older build must not destroy data. A frozen record format (gate.json schema 1) is changed only by adding optional fields; renaming or removing one is a new schema version.
- **Serializers:**
  - XmlSerializer: type or namespace changes, and default-value omission.
  - System.Text.Json versus Newtonsoft: availability per runtime ([runtime-differences.md](runtime-differences.md)).
  - Converters that throw on unknown names make the whole config fail to load, with no partial recovery.
  - `[JsonConverter]` on a property takes precedence over global converters (APBL #170 review). Check the effective converter.
  - Duplicate keys, comments, trailing commas, `NaN` and very large numbers: each parser treats them differently. A record read by more than one consumer (C#, Python, JS) must mean the same thing to all of them.
- **Profiles and exports:** a file written by Export must read back identically with Read. Round-trip it in a test, including old-format files.

## Configuration semantics

- **Text versus code:** setting text must match what the code does. Compare every changed setting's description, tooltip and README line with its effect (REVIEW.md, the table's "text describes less than it does" and "applies at once but text says Save" rows).
- **Ranges:** validate them at load and in the UI. Zero, negative, blank and unreadable values must not become a tight loop, a divide by zero or a destroy-everything setting (REVIEW.md: "An interval of zero loops every tick"; "24:00:00 means 24 days" from `TimeSpan.Parse`). (DR019)
- **Destructive options:** a default that destroys, deletes, kicks or bans needs a safe default, and docs that say plainly what happens. Real: APBL's instant kill default moved to TurnOff after #126.
- **Live versus restart:** say which settings apply live. Test the live path ([lifecycle.md](lifecycle.md)).
- **Values from a UI or another process:** validate them again where they are used. The UI is not enforcement ([inputs.md](inputs.md#network-and-ui-inputs)).

## How to confirm

- **Old files:** write fixture files in the old format (base commit) and load them with the head build in a harness or scenario. Check the log line and the effective values.
- **Atomicity:** simulate a crash mid-write (kill between truncate and write, or write a truncated file), then load. Show what the program does.
- **Culture:** run the read and write paths under `de-DE`/`tr-TR` cultures in a harness, on every runtime.
- **Concurrent writers:** run two writers in a loop in a harness and count lost updates.

<a id="dr020"></a>
## Wall clock in stored or scheduled times (DR020)

- **`DateTime.Now`:** stored in a file or field, or used in arithmetic, it jumps at DST changes and differs between servers in different time zones.
- **What to use:** `DateTime.UtcNow` for stored times and schedules, and `Stopwatch` for durations. Show local time only when displaying to a person. The same rule holds in Python (aware UTC datetimes, `time.monotonic()`) and PowerShell (`[DateTime]::UtcNow`, `[Diagnostics.Stopwatch]`).
