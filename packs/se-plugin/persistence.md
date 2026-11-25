# Plugin storage and config

Pass P8, after [core persistence](../../checks/core/persistence.md).

## Where plugins write

- **Location:** under `MyFileSystem.UserDataPath`, in a plugin-owned folder. Never `Environment.GetFolderPath`, because the game supports `-appdata` (ReviewPolicy.md). Never next to game or mod files.
- **Saves:** they run on the game thread. Serialization in `BeforeSave` adds to the save stall ([performance.md](performance.md)).
- **Clusters and shared folders:** two cluster nodes can write the same file, and node-local files are not seen by other nodes (REVIEW.md "Clusters and network mounts").

## PluginCheck's storage leads

Read every PluginCheck `storage` lead and give it a verdict:
- `UNGUARDED`: a file operation with no guard above it, reaching the server ([errors.md](errors.md));
- `GAME-THREAD`: file I/O on the game thread;
- `READ-DEFAULT`: a failed read that continues with defaults, which a later save writes over the real file;
- `NODE-FILE`: a node-local file other nodes need;
- `NODE-STATE`: in-memory state per player or grid that stays behind when a grid moves to another node.

## Config stores

- **Magnetar's `ConfigStorage`** writes only the values that differ from the default. A default change therefore reaches every existing Magnetar server on update. Real: APBL magnetar-hub#156, Destroy to TurnOff (L12). Say so in the release notes.
- **Torch configs** keep every value. APBL's Torch `KeepOlderDefaults` path kept old defaults for old files.
- **Quasar-managed config:**
  - The `PluginConfig` property is found through the plugin class (REVIEW.md Loader facts).
  - Values from Quasar must be validated on the server too: the UI is not enforcement ([quasar](quasar/README.md)).
- **Serializers through the host:** `System.Text.Json` on Legacy only through the host (APBL #170 used it via PluginSdk), and `[JsonConverter]` precedence ([loader-compile.md](loader-compile.md), [core persistence](../../checks/core/persistence.md#formats-and-migration)).

## How to confirm

- **Old files:** write fixture files in the old format (base commit) and load them with the head build in a harness or PluginCheck scenario. Check the log line and the effective values on both Magnetar runtimes.
