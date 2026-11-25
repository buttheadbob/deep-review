# Hub manifests, pins and release

Pass P14 (and P0 for the scope), after [core docs-release](../../checks/core/docs-release.md). Hub reviewers read the manifest, the README and the PR text against the code. They also check that the hub will install what the developer tested.

## Manifest and hub install

- **Commit:** a full SHA, reachable from the default branch, with no drift from HEAD unless that's explained. Real: Atomic.FM PluginHub#175, a pin on an orphan branch with a different tree and license (L25).
  - The pin must include the fixes the PR claims. Real: Anomaly PluginHub#177, a stale pin that predated its crash fixes (L38).
  - A commit hash goes into a manifest only after that commit is pushed, and it must be on the default branch (MasterRules 11). Before a plugin is merged, a repin is a commit to its open PR's branch; after, an update is a new PR that changes only `Commit` (MasterRules 12).
- **RepoId and Id:** RepoId is not a template placeholder (Real: DoomForSe PluginHub#342, `todo-github-user/project-name`, L14). `<Id>` never changes after publishing.
- **Elements:**
  - Only elements the current loader reads. Real: PluginHub#177. `<AssetFolder>` is ignored by Pulsar 2.4+, and `<SoftDependencyIds>` is not a Pulsar element (L37).
  - Check each element against the loader source in `Public\magnetar` (Pulsar `PluginData`/`GitHubPlugin`), not against old manifests.
- **Assets:**
  - Single-file assets are stored under the asset name, with no extension. Folder assets keep their relative names.
  - Test with a hub-style install, not only a dev folder: the dev-folder path returns the source path itself, so local tests can't catch this. Real: DoomForSe PluginHub#342 (L15).
  - Each archive asset gets a SHA-256 check and its own license.
- **`Runtimes` (Pulsar):** match what the plugin supports. `CLR;Mono` hides it from CoreCLR. Real: Atomic.FM PluginHub#175 (L33).
- **Description:**
  - At most 1000 characters, or the Pulsar list truncates it.
  - Paragraphs share one indentation, because `RemoveIndent` strips only the common part. Real: DoomForSe PluginHub#342 (L19).
  - It says what the plugin does by default, and anything a player or admin must know: network use, data sent, destructive options.
- **NuGetReferences:** every non-host dependency the loader needs is declared. IDE-only references are not.
- **Branches on the hub fork:** each plugin has its own branch, cut from clean upstream main; a hub PR goes from that branch to upstream main, never from the fork's main (MasterRules 13).

## Versions and changelog

- **Two version places:** bumped together. `Version.Build.props` `<Version>`, plus the `AssemblyVersion`/`AssemblyFileVersion` attributes in each `Plugin.cs`. The loader shows the attributes (se-dev-plugin skill). `deep_tools.py` compares them, along with the manifests, `quasar-plugin.json` and the newest CHANGELOG heading.
- **CHANGELOG and PR text:** as in the core. The real cases are APBL magnetar-hub#156 (defaults), DoomForSe PluginHub#349 (file names) and SensEssentials magnetar-hub#172 (PR text claiming a setting already in the previous pin).
- **Hub PR text** stays short, and not item by item (MasterRules 6). It never mentions PluginCheck (MasterRules 4).
- **TODO/FIXME left in compiled sources:** report each one (REVIEW.md manifests).

<a id="ui-and-text"></a>
## UI and text

- **Client UI:**
  - Absolute positions overlap when labels are localized. Real: Sort by Available Cargo PluginHub#190 (L24).
  - Controls must hide or disable when another plugin owns the panel (L20).
  - Hotkeys are gated on text focus ([lifecycle.md](lifecycle.md)).
- **Quasar pages:**
  - Styled, with each tab working and no console errors (REVIEW.md, Test the UI by hand).
  - Setting text equals the effect, and a change reaches the running server (companion).
- **No explanation text in plugin UIs:** the wiki explains; the UI carries a `?` link to the wiki only on the features the user named.
- **Disclosures:** network endpoints, data sent, telemetry, plain HTTP, and the author's own servers, in the README and description. Real: Atomic.FM PluginHub#175 (L26).
