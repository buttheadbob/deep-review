# Web security: ASP.NET Core and Blazor

Passes P9, P10 and P11, after the core files. These are the checks a web security reviewer makes on an ASP.NET Core app; each one is something to find in the code, not a property to assume.

<a id="exposure"></a>
## Exposure

- **Bind address:** read the release configuration (`urls` in appsettings, `ASPNETCORE_URLS`, `UseUrls`, `Listen*` calls), not `launchSettings.json`, which is for development only. A local app (Flow, an MCP server over HTTP) binds to `127.0.0.1` or `localhost`. `0.0.0.0`, `*`, `+` or `ListenAnyIP` puts it on the network.
- **Host header and DNS rebinding:** a loopback server is still reachable from a web page the user visits, through DNS rebinding, unless it checks the Host header. `AllowedHosts` lists the exact names, never `*`. A local API that changes state also needs a token or a header a browser won't send cross-site.
- **HTTPS:** anything not on loopback uses HTTPS, with HSTS in production. Auth cookies are `Secure`.
- **Forwarded headers:** `UseForwardedHeaders` trusts only the configured `KnownProxies`/`KnownNetworks`. Without them a client can claim any IP or scheme.
- **Development features:** `UseDeveloperExceptionPage`, Swagger UI, detailed health checks, `/metrics` and debug endpoints are on only where meant. Check the environment name the release really runs with.

<a id="authorization"></a>
## Authorization

- **Closed by default:** a fallback policy (`FallbackPolicy` requiring an authenticated user) makes new endpoints closed until opened. Every `[AllowAnonymous]` and `.AllowAnonymous()` has a reason.
- **Every entry point:** pages (`@attribute [Authorize]`), controllers and actions, minimal API endpoints (`.RequireAuthorization()`), SignalR hubs and hub methods, gRPC services, and file-serving endpoints. Middleware order: `UseRouting`, then `UseAuthentication`, then `UseAuthorization`, then `UseAntiforgery`, then the endpoints.
- **Blazor checks run on the server:** `AuthorizeView` and hidden buttons change only what renders. An interactive event handler runs for whoever holds the circuit, so the handler, or the service it calls, checks authorization again.
- **Resource ownership (IDOR):** an id from the route, query string, form, or a component parameter (`[SupplyParameterFromQuery]`, `[Parameter]` set from the URL) is checked against the current user before the resource is read or changed. `/threads/{id}` returns only threads that user may see.
- **Policies, not string checks:** roles and claims are checked through named policies, in one place. Claims from an external provider are mapped on purpose, not trusted wholesale.
- **Per-user data:** caches, singletons and statics keyed without the user leak between users ([hosting-di.md](hosting-di.md#di-lifetimes)).

## Cross-site requests

- **Antiforgery:** state-changing requests (POST, PUT, PATCH, DELETE) authenticated by cookie need antiforgery. Blazor forms get it from `EditForm`/`<AntiforgeryToken />` with `app.UseAntiforgery()`. Every `.DisableAntiforgery()` has a reason. A GET never changes state.
- **CORS:** origins listed exactly. `SetIsOriginAllowed(_ => true)` with `AllowCredentials()` opens the API to every site.
- **WebSockets and SignalR:** browsers don't apply CORS to the WebSocket handshake, so a cookie-authenticated hub (Blazor Server's circuit included) can be opened from another site. Check the `Origin` header on the endpoint, or authenticate the hub with a token instead of a cookie.
- **Cookies:** auth cookies are `HttpOnly`, `Secure` and `SameSite=Lax` or `Strict`, with a sensible lifetime. Sign-out ends the server-side session too.

## Output encoding (XSS)

- **Razor encodes `@value`.** The sinks that don't: `MarkupString`, `Html.Raw`, `HtmlString` and other `IHtmlContent` built by hand, and `AddMarkupContent` in a render tree. Each one is traced to its input.
- **Markdown:** user Markdown rendered to HTML (chat, notes, comments) has raw HTML turned off (Markdig's `DisableHtml()`) or sanitized, and its link and image URLs checked for `javascript:` and `data:` schemes. Flow's chat renders Markdown with images and links: check this there.
- **URLs in attributes:** Razor encodes `href` and `src`, but doesn't stop `javascript:` URLs. Allow `http`, `https` and app-relative URLs only.
- **JS interop:** values passed to JavaScript and placed with `innerHTML`, `insertAdjacentHTML` or `eval` are DOM XSS ([web](../web/checks.md#p10-security)). `IJSRuntime.InvokeAsync("eval", ...)` with any input is code injection.
- **Data in scripts:** JSON embedded in a page is serialized with an encoder that escapes `<` (System.Text.Json's default does). Never concatenate data into a `<script>` block.
- **Headers:** a Content-Security-Policy ([web](../web/checks.md#p10-security)), `X-Content-Type-Options: nosniff`, and `frame-ancestors` (or `X-Frame-Options`) against clickjacking.

<a id="input-binding"></a>
## Input binding (P9)

- **Over-posting:** binding a request straight to an entity lets a client set fields it shouldn't (`IsAdmin`, `OwnerId`). Bind to a DTO with only the editable fields.
- **Validation on the server:** every endpoint validates its input. `[ApiController]` returns 400 for invalid models; minimal APIs and Blazor handlers validate only what the code wires up. Check each one.
- **Size limits:** `MaxRequestBodySize`, form limits, SignalR's `MaximumReceiveMessageSize` (32 KB by default), and uploads: size checked, type judged by content and not by extension, stored outside `wwwroot` under a generated name.
- **Rate limits:** login, expensive endpoints, and anything that posts to an outside service (`AddRateLimiter`).
- **Open redirects:** `returnUrl` and similar go through `Url.IsLocalUrl` or `LocalRedirect`.

## Secrets and error text

- **No secrets in `appsettings.json` in the repository.** User secrets in Development only; production secrets from the environment or a secret store. Connection strings, tokens and keys are never logged, and request logging doesn't record auth headers or cookies.
- **Error responses** in production carry no stack traces, SQL or file paths (ProblemDetails, `UseExceptionHandler`).
- **Log injection:** user text goes into logs as structured parameters, never interpolated into the message (CA2254).
