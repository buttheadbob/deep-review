# Library API design

Passes P3, P5, P6, P7, P9, P10, P11 and P12, after the core files. These are the checks a library reviewer makes on each public member the change adds or touches.

<a id="arguments"></a>
## Arguments (P9)

- **Validate public arguments** even with nullable annotations on: callers may have nullable off, use `!`, or call from another language. `ArgumentNullException.ThrowIfNull` (net6+; a helper on netstandard2.0), `ArgumentOutOfRangeException` with the parameter name (`nameof` or `CallerArgumentExpression`).
- **Validate before side effects:** an invalid argument throws before anything changes, and, in async methods, before the first `await` where the API promises that.
- **Collections and buffers:** a caller-owned collection or array passed in is copied if the library keeps it; a buffer is not kept beyond the call unless the API says so.

## Nullability

- **`#nullable enable` throughout,** and the annotations are true: a member annotated non-null never returns null.
- **Attributes where the type alone can't say it:** `[NotNullWhen(true)]` on `TryGet` out parameters, `[MaybeNullWhen(false)]`, `[MemberNotNull]`, `[DisallowNull]`, `[AllowNull]`, `[NotNullIfNotNull]`.
- **No `null!` and no `default!`** to silence the compiler (MasterRules 22): each one is an unchecked promise to every caller.
- **Generic `T?`** means different things for unconstrained, class and struct type parameters. Check the annotation says what the code does.

<a id="async-apis"></a>
## Async APIs (P3)

- **`ConfigureAwait(false)` on every await** in library code. A caller with a `SynchronizationContext` (WinForms, WPF, Avalonia, Blazor's renderer) that blocks on the task otherwise deadlocks. (The opposite of app code with no context: [core concurrency](../../checks/core/concurrency.md#async).)
- **No sync-over-async inside the library,** and no async-over-sync wrappers (`Task.Run` around a blocking call) offered as async APIs. Offer the real async path, or only the sync one.
- **`CancellationToken`** as the last parameter, optional, passed to every downstream call, and honoured before side effects. A token that is accepted and ignored is a defect.
- **Cancellation versus a result:** document whether a token that fires after a result was recorded overrides it. "First result wins" is the usual contract for a user choice; if the token is authoritative, say so and test both orders. A later `IsCancellationRequested` check must not silently discard a value the caller already recorded.
- **`ValueTask`** only where the hot path completes synchronously; a `ValueTask` may be awaited once. Document it, and don't return one from an interface callers implement unless they can follow the rule.
- **No `async void`,** and no fire-and-forget work the caller can't observe or stop.
- **`IAsyncEnumerable<T>`** methods take a token with `[EnumeratorCancellation]`.

<a id="exceptions"></a>
## Exceptions (P7)

- **Exceptions are part of the contract.** Document which a member throws and when; throw specific types (`ArgumentException` family, `InvalidOperationException`, `ObjectDisposedException`, a library exception type for protocol failures), never `Exception` itself.
- **Don't swallow callers' exceptions** thrown from their callbacks or handlers; don't let one subscriber's exception stop the others without a rule for it.
- **Messages** say what went wrong in plain words and carry no secrets (tokens, connection strings).
- **Transient versus permanent:** a protocol client tells callers which failures are worth retrying (rate limits with the server's `Retry-After`, timeouts, disconnects) and which aren't (auth, validation).

<a id="thread-safety-and-callbacks"></a>
## Thread safety and callbacks (P3, P11)

- **Say it:** every public type documents whether instances are safe to share between threads. The code matches the document.
- **Callbacks and events:** raised outside the library's locks ([core concurrency](../../checks/core/concurrency.md#locks-and-re-entrancy)), on a documented thread or context, and never synchronously from inside a call the caller made while holding its own lock, unless documented.
- **Re-entrancy:** a callback that calls back into the library (disposing the client from its own event, sending from a receive handler) is either supported or refused clearly.
- **Statics:** no global mutable state. Two consumers in one process (two bots, two contexts) must not see each other's settings.

<a id="disposal-and-ownership"></a>
## Disposal and ownership (P5)

- **Own what you create, borrow what you're given:** an `HttpClient`, stream, socket or `ILoggerFactory` passed in is not disposed by the library; one it created is.
- **`IDisposable` and `IAsyncDisposable`** both, where cleanup is async. Dispose is idempotent, never throws, and members called after dispose throw `ObjectDisposedException`.
- **Background loops** a library starts (a gateway heartbeat, a reconnect loop) stop on dispose, within a bound.

<a id="dependencies"></a>
## Dependencies (P6, P10)

- **A promise of no dependencies is kept:** SimpleDiscordDotNet and SimpleORMDotNet are BCL only. A new `PackageReference` there is a finding unless the user agreed.
- **Dependency types in the public API** tie every caller to that dependency's version.
- **Version ranges** in the package allow the versions the library was tested with; analyzer and build-only packages are `PrivateAssets="all"`.
- **Logging:** take `ILogger`/`ILoggerFactory` optionally (`NullLogger` by default) or an event; never write to `Console`.
- **Protocol clients:** honour the server's rate limits (Discord's 429 responses and bucket headers), reconnect with backoff and jitter, detect a dead connection (missed heartbeat acknowledgements), and resume where the protocol allows.

<a id="performance-of-public-apis"></a>
## Performance of public APIs (P12)

- **Hot APIs don't allocate needlessly:** no LINQ or closures per call in methods callers use in loops; spans or pooled buffers where the API takes data; `LoggerMessage` for logging on hot paths.
- **No hidden I/O:** a property getter or a cheap-looking method doesn't block on the network or disk.
- **Caches** have bounds and eviction ([core performance](../../checks/core/performance.md#dr025)).
