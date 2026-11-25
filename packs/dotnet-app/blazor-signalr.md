# Blazor and SignalR

Passes P3, P5, P7 and P12, after the core files. Name the render mode of every changed component in P2: most of the checks below depend on it.

## Render modes

- **Where the code runs:** static server rendering, `InteractiveServer`, `InteractiveWebAssembly` or `InteractiveAuto`. Code in a WebAssembly or Auto component runs in the browser: every line, constant and config value in its project is public, its authorization checks are only UI, and secrets never go there.
- **Prerendering runs initialization twice:** `OnInitialized{Async}` runs once to prerender and again when the component becomes interactive. A side effect there (posting, counting, starting work, sending a message) happens twice. JS interop isn't available while prerendering; use `OnAfterRenderAsync(firstRender)`. Data fetched in the prerender can be carried over with `PersistentComponentState`.
- **Static server rendering has no interactivity:** event handlers on a statically rendered component never run. Forms there post back and bind with `[SupplyParameterFromForm]`.

<a id="rendering-and-circuits"></a>
## Rendering and circuits

- **One circuit per browser tab** (`InteractiveServer`). Its component state and its DI scope live in server memory for the circuit's life, plus the time a disconnected circuit is kept. Multiply per-circuit memory by users and tabs.
- **The renderer runs component code one call at a time.** Code arriving from another thread (a timer, a service event, a background task, a SignalR callback) calls `InvokeAsync(...)` before touching component state or calling `StateHasChanged`. Without it, the call throws or races with rendering.
- **Blocking work** in an event handler or lifecycle method freezes that circuit's UI. Await I/O instead.
- **`StateHasChanged`:** automatic after an event handler's awaits; needed (through `InvokeAsync`) when a background event changes state.
- **Rendering cost:**
  - Long lists without `Virtualize`.
  - Lists without `@key`: elements are reused by position, so state shows up on the wrong row after an insert or delete.
  - Children that re-render on every parent render: a parameter of a mutable reference type always counts as changed. `ShouldRender` and immutable parameters help.
  - Heavy work in `OnParametersSet{Async}`, which runs on every parent render.
- **Circuit traffic:** large render batches and big payloads sent through the circuit. Calls from JavaScript into .NET are limited by `MaximumReceiveMessageSize` (32 KB by default).
- **First render versus late content:** a native dialog, popover or accessibility hook wired in `OnAfterRenderAsync(firstRender)` sees only what the first render produced. Content that arrives later (an async load, a plugin component, a replacement body) needs an observer or an explicit re-wire. Test the late path. Real: Quasar PR2's `aria-describedby` was correct only when the description existed at open.

<a id="component-lifetime"></a>
## Component lifetime (P5)

- **Unsubscribe on dispose:** a component that subscribes to a singleton's or a scoped service's event implements `IDisposable` or `IAsyncDisposable` and removes its handler. Otherwise the service keeps every visited page alive, and calls into components whose renderer is gone.
- **Cancel on dispose:** work a component starts is cancelled from `Dispose` (a `CancellationTokenSource` field). A call that finishes after dispose must not touch the component ([core lifecycle](../../checks/core/lifecycle.md), work that outlives its owner).
- **JS interop during dispose:** `DisposeAsync` calling into JavaScript can throw `JSDisconnectedException` once the circuit is gone. Catch that one exception, nothing broader.
- **References:** `IJSObjectReference`, `DotNetObjectReference` and timers created by the component are disposed with it.
- **Host mount and unmount:** when a service's result is completed only by a rendered host, track whether the host is mounted. Complete or cancel pending work when no host exists and when the last host unmounts; otherwise a caller can wait forever. Test the no-host path.
- **Cancellation versus a recorded result:** if a token and a button/native close can both finish the same dialog, define first-wins. Do not let a later `IsCancellationRequested` check overwrite a choice the user already made. Test both orders.

<a id="component-api"></a>
## Component API and accessibility (P6, P9, P14)

- **`AdditionalAttributes`:** a reusable component that wraps a native element should accept `[Parameter(CaptureUnmatchedValues = true)]` so callers can pass `id`, `data-*`, `dir`, `lang` and extra `aria-*`. Keep `class`, `style`, `role` and `aria-atomic` component-owned when they carry the component's contract, and document which is which.
- **Namespace ownership:** a public component in a Razor class library must not share a namespace with host-internal components. A later same-named type makes every consumer's using ambiguous. Move it before release, not after.
- **Accessible name and description:** a dialog needs a name, and its description must point at a real, non-empty element when focus enters. A visible heading plus an `aria-label` with the same words can be announced twice; prefer `aria-labelledby` or suppress one.
- **Attribute forwarding is not sanitization:** forwarded `Style`/`Class` values remain trusted CSS. Document that, and do not let forwarded `role`/`aria-atomic` silently break the component's severity contract.
- **Tests:** assert roles, names, descriptions and computed style, not only the presence of a class. Test the empty, late and duplicate-id paths.

<a id="errors"></a>
## Errors (P7)

- **An unhandled exception in an interactive server component's handler or lifecycle method ends the circuit.** The user sees the error or reconnect UI and loses unsaved state. Catch expected failures in the handler; use `ErrorBoundary` around parts that may fail.
- **Navigation:** `NavigationManager.NavigateTo` during static rendering throws a `NavigationException` that the framework expects. A broad catch around it breaks the redirect.

## SignalR hubs

- **Identity:** hub methods use `Context.User`, never a user id passed as an argument. `[Authorize]` on the hub, and on methods that need more.
- **No state in the hub:** a hub instance lives for one call. Per-connection state is keyed by `Context.ConnectionId` and removed in `OnDisconnectedAsync`.
- **Reconnects:** a client that reconnects gets a new connection id, and its group memberships are gone. The server adds it to its groups again.
- **Sending from background services:** through `IHubContext`, with the same checks on what each user may receive ([core authority-interop](../../checks/core/authority-interop.md#information)).
- **Origin check** for cookie-authenticated hubs ([web-security.md](web-security.md#cross-site-requests)).
