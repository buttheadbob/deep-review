# Docs, UI text and release

Pass P14. Reviewers read the README, the release metadata and the PR text against the code. They also check that what users install is what the developer tested.

Packs add their release channels: hub manifests, pins and assets ([se-plugin hub-release](../../packs/se-plugin/hub-release.md)), the Workshop ([se-mod definitions](../../packs/se-mod/definitions.md#workshop-release), [pb-script](../../packs/pb-script/checks.md#p14-release)), deployment and published builds ([dotnet-app desktop-console](../../packs/dotnet-app/desktop-console.md#publishing)), NuGet packages ([dotnet-library packaging](../../packs/dotnet-library/packaging.md)), script help ([powershell](../../packs/powershell/checks.md#p14-help-and-release)), Python entry points ([python](../../packs/python/checks.md#p14-docs-and-release)), site text versus live features ([web](../../packs/web/checks.md#p14-site-text-and-release)).

## Release metadata

- **No template values:** placeholders, `TODO` owners and commit hashes, sample ids and repository names left from a template. Real: DoomForSe PluginHub#342 shipped `todo-github-user/project-name` as its RepoId (L14). OreYeeter's and Orion's manifests still had TODOs before release.
- **The release is the reviewed commit:** the artifact users get is built from the reviewed head, with no local changes, and the reference to it (a pin, a tag, a package version) points at that commit on the default branch (MasterRules 11).
- **Install like a user:** test the installed form, not the dev folder: a hub install, a published single-file app, a NuGet package restored into a fresh project, a Workshop download. Paths and asset names differ. Real: DoomForSe's single-file asset arrived with no extension on a hub install (L15).

## Versions and changelog

- **Every version place:** bumped together. The packs name them (for plugins, `Version.Build.props` plus the assembly attributes; for packages, `<Version>`; for scripts, a version in the help or the manifest). `deep_tools.py` compares the ones it knows.
- **CHANGELOG:**
  - One entry per user-visible change, in the version being released.
  - It must mention default changes that reach existing installs. Real: APBL magnetar-hub#156, Destroy to TurnOff on existing Magnetar configs (L12).
  - It must mention data or file-name changes that need migration. Real: DoomForSe PluginHub#349, autosave names (L18).
  - A Note that is only a release-note item is handled by writing it here ([Report.md](../../Report.md#deep-reviewjson)).
- **Version semantics:** a breaking change gets the version bump the project's rules give it (SemVer major for libraries; Flow keeps MAJOR at 1 and treats breaking as minor).
- **PR and commit text:** describe only this range. Real: SensEssentials magnetar-hub#172, where the PR text claimed a setting that was already in the previous pin. Commit and PR text hold facts only, with no authors, tools or credits, and never mention PluginCheck (MasterRules 1-7, CommitRules.md).
- **TODO/FIXME/HACK left in shipped sources:** report each one (REVIEW.md manifests; deep_tools flags them in compiled sources).

## UI and text

- **Settings and help text:** match behaviour ([correctness.md](correctness.md) "Text and behaviour agree").
- **UI:**
  - Absolute positions overlap when labels are localized or longer than expected. Real: Sort by Available Cargo PluginHub#190 (L24).
  - Controls hide or disable when another component owns the area.
  - Keyboard shortcuts are ignored while a text field has focus. Real: Atomic.FM PluginHub#175 fired its hotkey while typing in chat (L31).
  - Plain-text values keep the shape the caller expects: newlines survive (`pre-line`/`pre-wrap`) or the contract says the value is single-line. A message with two paragraphs collapsing into one line is a finding.
  - A default shape is documented: a one-button message box is a confirmation, not a notification; a blank optional label hides that button; a cancelled token and a recorded choice have a stated precedence.
  - Visible and accessible text agree: a visible title and an `aria-label` carrying the same words can be announced twice; the docs and the DOM say which one names the control.
  - The project's own UI text rules apply (for plugins, [se-plugin hub-release](../../packs/se-plugin/hub-release.md#ui-and-text)).
- **Samples compile:** every code sample in a README, docs page or XML comment uses the namespaces, using directives, type names, parameter names and defaults of the reviewed head. A sample that needs an unstated `@using`/`using` is a finding. Real: Quasar PR2's plugin notice sample omitted `@using Quasar.Ui` while the fixture required it.
- **A documented limitation is not a fix:** when the code still behaves the old way, keep the finding and say what remains. Documentation alone does not close a behavioural finding; only an explicit user decision can accept it.
- **Review what the user sees, not just the code (MasterRules 14a):** for a build the user will run, headless renders of the main screens at the usual sizes (appearance), the new features exercised through a headless UI test (function), and a smoke pass over existing everyday features (regressions). A review that skipped any of the three says so.
- **Disclosures:** network endpoints, data sent, telemetry, plain HTTP, and the author's own servers, in the README and the release description. Real: Atomic.FM PluginHub#175 (L26).
- **Licence and notices:** the licence file and third-party notices ship with the release and match its content ([security.md](security.md#legal-pointers-only)).
