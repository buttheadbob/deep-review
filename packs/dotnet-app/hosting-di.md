# Hosting, DI, configuration and logging

Passes P3, P5, P7, P8 and P11, after the core files.

<a id="di-lifetimes"></a>
## DI lifetimes

- **Captive dependencies:** a scoped service, or a transient one with state, injected into a singleton lives as long as the singleton. Its state then leaks between users, circuits and requests, and it is used from several threads at once. `ValidateScopes` and `ValidateOnBuild` catch many cases, and are on by default in Development only. Check the release configuration too.
- **Blazor Server scopes last as long as the circuit:** a scoped service there is per user tab, not per request. A scoped `DbContext` lives for the whole circuit and overlapping events use it at once. Use `IDbContextFactory<T>` and a context per operation ([data-access.md](data-access.md#contexts-and-connections)).
- **Scoped services resolved from the root provider** (`app.Services.GetService<T>()`, or in a singleton's constructor) become singletons in all but name.
- **Options:** `IOptionsSnapshot<T>` can't go into a singleton. `IOptionsMonitor<T>` sees reloads; `IOptions<T>` never does.
- **`HttpClient`:** from `IHttpClientFactory` (named or typed clients), not `new` per call, and not captured in a singleton for good ([core security](../../checks/core/security.md#code), HttpClient).
- **Disposal:** the container disposes what it created. Code never disposes an injected service, and always disposes what it created itself. An instance registered with `AddSingleton(instance)` is not disposed by the container.
- **Service locator in statics:** a static that holds a service hides its lifetime, and leaks the same way as a captive dependency.

<a id="hosted-and-background-services"></a>
## Hosted and background services (P3, P5)

- **`stoppingToken`:** every wait and call in `ExecuteAsync` takes it.
- **Exceptions stop the host:** since .NET 6, an exception escaping `ExecuteAsync` stops the whole app by default (`BackgroundServiceExceptionBehavior.StopHost`). Catch and log per iteration unless stopping the app is the intent.
- **Before the first `await`:** depending on the .NET version, the synchronous start of `ExecuteAsync` can run during host start-up and delay it. Keep blocking work out of it.
- **Scopes:** a hosted service is a singleton. It creates a scope per unit of work (`IServiceScopeFactory.CreateAsyncScope()`) for scoped services, and disposes it.
- **Shutdown:** stopping must finish within `HostOptions.ShutdownTimeout`. Work that ignores the token is cut off mid-write, so its writes must be atomic ([core persistence](../../checks/core/persistence.md#writes)). `ApplicationStopping` callbacks run synchronously; keep them short.
- **Periodic work:** a `PeriodicTimer` loop doesn't overlap, but skips ticks ([core concurrency](../../checks/core/concurrency.md#timers-and-threads)).
- **More than one instance:** a hosted service in an app that can run twice (two machines, a second launch) does its job twice. A lock or a single owner where it must run once.

<a id="configuration"></a>
## Configuration (P8)

- **Where a value really comes from:** `appsettings.json`, then `appsettings.{Environment}.json`, then user secrets (Development only), then environment variables, then the command line, each overriding the one before. A setting only in `appsettings.Development.json` is missing in release.
- **Validate at start:** options bound with validation and `ValidateOnStart()` fail at start-up, not at first use.
- **Reload:** with `reloadOnChange`, code that copied a value at start never sees a change ([core lifecycle](../../checks/core/lifecycle.md#checks), configuration changes applied live).
- **Data protection keys:** cookies, antiforgery tokens and protected data depend on them. Keys kept in memory or in a container's temp folder are lost on restart: everyone is signed out, and protected data can't be read back. Persist the keys to a folder that survives, and protect them at rest on Linux, where they are stored unencrypted by default.

## Logging

- **Message templates:** named placeholders, never interpolated strings (CA2254), so values stay parameters and user text can't forge log lines.
- **Hot paths:** the `LoggerMessage` source generator, and level checks before costly arguments.
- **No secrets or personal data** in log scopes, HTTP logging (bodies and headers carry tokens and cookies), or exception messages.
- **Volume:** Information per request on a busy server is a cost; rate limit repeated failures ([core errors](../../checks/core/errors.md#checks), error spam).

<a id="errors-at-the-edges"></a>
## Errors at the edges (P7)

- **Production exception handling:** `UseExceptionHandler` or an `IExceptionHandler`, with ProblemDetails. `UseDeveloperExceptionPage` in Development only.
- **`HttpContext` outside its request:** it isn't thread-safe and isn't valid after the request ends. Background work never captures it; `IHttpContextAccessor` on a background thread returns null or the wrong request.
- **Fire-and-forget from a handler:** the request ends and the task's failure goes unseen. Hand the work to a queue a hosted service owns.
- **Client disconnects:** `HttpContext.RequestAborted` goes to downstream calls. An `OperationCanceledException` from a disconnect is not an error to log as one.
- **Circuits and hosted services:** see [blazor-signalr.md](blazor-signalr.md#errors) and above.
