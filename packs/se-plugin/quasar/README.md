# Host note: Quasar UI plugins

**Loads when** `projectKinds` contains `quasar-plugin` (a hub manifest with `<PluginKind>QuasarUiPlugin`), on top of the [se-plugin pack](../README.md). Also load [dotnet-app](../../dotnet-app/README.md): a Quasar plugin is an ASP.NET Core and Blazor component inside the Quasar web host, so the web security, Blazor and DI checks all apply. Skills: `se-dev-quasar-plugin` (IQuasarPlugin, `quasar-plugin.json`, nav items, extension targets, the companion channel, authorization, local testing, QuasarHub), `quasar-book` (the Quasar server itself), `se-dev-plugin-sdk` (Magnetar config UI declared through PluginSdk, which is not a Quasar plugin).

**Stage 1:** `se-dev-plugin-review` on the CometWorks/quasar-hub PR, preview only, never posting.

## What differs on Quasar

- **Runtime:** .NET 10 on Linux. The UI project compiles from the manifest's `ProjectPath`, with the Razor source generator. Its Magnetar companion (`netstandard2.0`) runs in Magnetar on both runtimes ([loader-compile.md](../loader-compile.md)). ARCH003 flags game assemblies referenced from Quasar UI code.
- **Config:** the `PluginConfig` property is found through the plugin class (REVIEW.md Loader facts). Values from Quasar must be validated on the server too ([persistence.md](../persistence.md#config-stores)).
- **Offline or loading servers:** a profile or setting sent to a server that is offline or still loading must queue until it is ready or fail visibly. Real: APBL profiles ([lifecycle.md](../lifecycle.md)).
- **Clusters:** in-memory state per player or grid stays behind when a grid moves to another node, and node-local files are not seen by other nodes (REVIEW.md "Clusters and network mounts").

## Security (ASP.NET Core, Blazor)

- **Authorization:** every page, endpoint and companion message handler checks authorization (`CanEditConfigs` and similar). A hidden button is not authorization.
- **Antiforgery:** state-changing HTTP endpoints outside Blazor's circuit need antiforgery.
- **DI lifetimes:** a scoped service, such as a per-circuit or per-request service, injected into a singleton becomes a captive dependency: state leaks between admins and circuits. Static helpers holding scoped services cause the same leak.
- **JS interop:** values passed to JS and rendered as HTML must be encoded.
- **Companion messages:** validate them on the server side, check their version, and never let the UI send a raw command string that runs with server rights.

The general forms are in [dotnet-app web-security](../../dotnet-app/web-security.md) and [hosting-di](../../dotnet-app/hosting-di.md).

## UI review

- Styled, with each tab working and no console errors (REVIEW.md, Test the UI by hand).
- Setting text equals the effect, and a change reaches the running server through the companion.
- Headless renders at the usual sizes, a headless UI test of the new features, and a smoke pass over existing pages (MasterRules 14a).

## Tests

- Never answer yes to Quasar's data consent prompt (MasterRules 28).
- deep_tools' known gap: Razor components of several merged projects are generated under one root namespace.
