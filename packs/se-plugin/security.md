# Plugin security, supply chain and legal

Pass P10, after [core security](../../checks/core/security.md). se-dev-plugin-review's `ReviewPolicy.md` is the baseline, and PluginCheck REVIEW.md "Security", "Supply chain" and "Legal" come next. Patches on permission, ownership, safe-zone and damage checks are in [harmony.md](harmony.md#patches-on-checks). Quasar UI security is in [quasar](quasar/README.md) and [dotnet-app web-security](../dotnet-app/web-security.md).

## Hub policy (ReviewPolicy.md)

- **Self-update:** banned.
- **Opening a fixed HTTPS URL** is not arbitrary execution.
- **Exported grids and blueprints** must not carry owner IDs or Steam IDs the destination doesn't need.
- **Committed binaries:** a byte-for-byte match with an official artifact, or a blocker. Real: DLSS PluginHub#177, 58 MB committed (L41).
- **Filesystem:** the plugin's own folder under `MyFileSystem.UserDataPath` ([persistence.md](persistence.md)).

## Process-wide effects

- **`ServicePointManager` and certificate callbacks** on net48 change TLS and connection behaviour for the whole process: the game and every other plugin.
- **Undisclosed endpoints:** Atomic.FM PluginHub#175 streamed over plain HTTP from the author's server, undisclosed, exposing listener IPs (L26). Every endpoint goes in the README and the manifest description ([hub-release.md](hub-release.md)).

## Supply chain

- **References:** package, project and assembly references and hub `NuGetReferences`, each with provenance (ReviewPolicy.md). IDE-only references versus loader dependencies.
- **Host assemblies brought along:** PluginCheck's `hostpackages` stage covers this. A plugin must not ship its own copy of a host assembly.

## Legal

License claim versus content, inherited code, notices kept, and assets' licenses: PluginCheck REVIEW.md "Legal" and "Copied code". Real cases:
- DoomForSe PluginHub#342: an MIT `LICENSE` over GPL code, and removed GPL notices (L16).
- Anomaly PluginHub#177: redistributed modified Keen shader sources, third-party game content (L39).
- APBL magnetar-hub#156: a relicense with all contributors identified.

Decompiled game code is a reference only: never copied into a plugin, and never published. The physics and performance work reads it and copies none of it.
