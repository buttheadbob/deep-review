# Pack: dotnet-app (.NET applications)

**Loads when** gate.json's `projectKinds` contains `dotnet` and the repository builds something that runs: an ASP.NET Core or Blazor app (`Sdk="Microsoft.NET.Sdk.Web"` or `Microsoft.NET.Sdk.Razor` with pages), a desktop app (`<OutputType>WinExe</OutputType>`, Avalonia, WPF, WinUI), a console tool or service (`<OutputType>Exe</OutputType>`, the Generic Host), or an MCP server. Also loads for every `quasar-plugin` (a Blazor component inside Quasar). A repository with both an app and libraries meant for others loads this pack and [dotnet-library](../dotnet-library/README.md). Decide in P0, and write the reason in the report: gate.json doesn't tell apps and libraries apart.

**What it adds:** web exposure, authorization, CSRF, XSS and CORS; Blazor render modes, circuits and component lifetimes; accessible names, descriptions, ids and focus; SignalR; DI lifetimes, hosted services, configuration, data protection and logging; EF Core and other data access, including migrations; and desktop and console concerns (UI threads, trimming, AOT, single-file, processes, exit codes, publishing). The user's apps here: Flow (Blazor, Windows and Linux), ProcessMonitor (Avalonia, AOT, trimmed, single-file, tray), XML-Element-Nuker (CLI), GridGenerator (library, harness and MCP server), Nebula (server manager), and Quasar plugins.

**Stage 1:** the built-in `code-review` at high effort plus `security-review` ([Report.md](../../Report.md#the-stage-1-record)), unless the project is a hub plugin.

## Pass map

| Pass | Files |
|---|---|
| P0 | hosting model and render modes, bind addresses, target OSes, publish settings, database engine |
| P2 | entry points: pages and components (event handlers, lifecycle methods), endpoints and controllers, hub methods, hosted services, `Main`, UI event handlers, MCP tools |
| P3 | [blazor-signalr.md](blazor-signalr.md#rendering-and-circuits), [hosting-di.md](hosting-di.md#hosted-and-background-services), [desktop-console.md](desktop-console.md#ui-threads) |
| P4 | [desktop-console.md](desktop-console.md#trimming-aot-and-single-file) |
| P5 | [blazor-signalr.md](blazor-signalr.md#component-lifetime), [hosting-di.md](hosting-di.md#hosted-and-background-services), [desktop-console.md](desktop-console.md#lifetime) |
| P6 | all files: ASP.NET Core, Blazor, EF Core and UI framework semantics; [core accessibility](../../checks/core/accessibility.md) for names, descriptions, ids and focus |
| P7 | [hosting-di.md](hosting-di.md#errors-at-the-edges), [blazor-signalr.md](blazor-signalr.md#errors), [desktop-console.md](desktop-console.md#console-tools) |
| P8 | [data-access.md](data-access.md), [hosting-di.md](hosting-di.md#configuration) |
| P9 | [web-security.md](web-security.md#input-binding), [desktop-console.md](desktop-console.md#console-tools), [core accessibility](../../checks/core/accessibility.md) input naming |
| P10 | [web-security.md](web-security.md), [data-access.md](data-access.md#injection) |
| P11 | [web-security.md](web-security.md#authorization), [hosting-di.md](hosting-di.md#di-lifetimes) |
| P12 | [data-access.md](data-access.md#queries), [blazor-signalr.md](blazor-signalr.md#rendering-and-circuits) |
| P14 | [desktop-console.md](desktop-console.md#publishing), [core accessibility](../../checks/core/accessibility.md) UI state and text |
| P15 | [Tests](#tests) below |
| P16 | Lessons rows with Pack `core` (no app rows yet; add them as reviews find defects) |

## Skills

No se-dev skill covers general .NET apps. Read the framework's own source where behaviour matters (the ASP.NET Core and EF Core repositories are public), and quote it. For an app that calls Claude or the Anthropic SDK, the `claude-api` skill.

## Runtime tags

`Windows`, `Linux`, `Server (InteractiveServer)`, `Browser (WebAssembly)`, `Published (trimmed/AOT)`, `All`.

<a id="tests"></a>
## Tests

- **What the user sees (MasterRules 14a):** for a build the user will run, headless renders of the main screens at the usual window sizes, the new features driven by a headless UI test (bUnit for components; a headless browser for pages), and a smoke pass over existing everyday features. For Flow, the redesign phase from 9 on wants styling and compile only, with no screenshot re-takes: follow the project's own rule.
- **UI assertions:** a browser test asserts the accessible name/description and the computed style for visual contracts, not only that a class exists. Test the late-content, empty, duplicate-id and cancellation-order paths. Use stable locators (title, id, test id), not a positional `.first()` that can resolve to a stale dialog.
- **Headless only:** no test opens a window or takes focus (MasterRules 33a). Desktop apps test their view models and services; their windows are rendered headless where the framework allows (Avalonia's headless platform).
- **Integration tests** with `WebApplicationFactory` for endpoints and authorization (an anonymous request to every protected endpoint gets 401 or 403), and a real database engine for data access (SQLite in a temp folder, not the in-memory provider, which hides translation and concurrency failures).
- **Published build:** run the trimmed or AOT build once, not only the debug build: trimming failures appear only there.
