# Desktop and console apps

Passes P3 to P7, P9 and P14, after the core files. The user's apps of this kind: ProcessMonitor (Avalonia, AOT, trimmed, single-file, tray icon, toasts), XML-Element-Nuker (a CLI), GridGenerator's harness and MCP server, and Flow on Windows and Linux.

<a id="ui-threads"></a>
## UI threads (P3)

- **One UI thread:** Avalonia's `Dispatcher.UIThread`, WPF's `Dispatcher`, WinUI's `DispatcherQueue`. Controls, bound properties and bound collections (`ObservableCollection`) changed from another thread throw or corrupt the view. Marshal with `Post` or `InvokeAsync`.
- **`async void` UI event handlers** are allowed, and each needs its own try/catch: an exception there goes to the dispatcher's unhandled-exception handler or ends the app.
- **Blocking the UI thread** (`.Result`, synchronous I/O, `Thread.Sleep`) freezes the window, and can deadlock with the UI's synchronization context ([core concurrency](../../checks/core/concurrency.md#async)).

<a id="trimming-aot-and-single-file"></a>
## Trimming, AOT and single-file (P4)

- **Trim and AOT warnings are defects:** IL2xxx and IL3xxx warnings mean the published app can fail where the debug build works. None is suppressed without a reason, and `[DynamicallyAccessedMembers]`/`[RequiresUnreferencedCode]` are applied where the warning points.
- **Serialization:** reflection-based JSON breaks under trimming and AOT. System.Text.Json source generation (`JsonSerializerContext`) covers every serialized type, including ones only reached through `object` or generics.
- **Single-file:** `Assembly.Location` is empty (IL3000). Use `AppContext.BaseDirectory`. Files the app expects beside the exe are published with it or embedded.
- **Per-OS builds:** native libraries per runtime identifier, AOT per OS, and a run of the published output on each OS and version it targets. Real: ProcessMonitor's WinUI Activity page crashed natively on a Windows 10 client, which led to the move to Avalonia.
- **Platform APIs:** registry, WMI, Win32 calls and Windows toast APIs are guarded with `OperatingSystem.IsWindows()`. Read every CA1416 warning.

## Processes and the OS (P6)

- **Processes vanish:** a process can exit between listing and querying it (`InvalidOperationException`, `ArgumentException`). `MainModule`, `StartTime` and the command line throw `Win32Exception` for elevated, protected or other-bitness processes. Guard each query.
- **Handles:** `Process` objects are disposed. A monitor that polls every second and leaks one handle per process per poll runs out.
- **Acting on a stored PID:** PIDs are reused. Compare the start time before killing or watching a PID saved earlier. Never kill a process the user didn't choose.
- **Watchdogs:** restarts with backoff and a limit, and no tight restart loop when the child fails at once.
- **Elevation:** say what needs admin rights, and fail visibly without them rather than quietly doing less.
- **The desktop:** no window or focus change from helpers and background work ([core host-api](../../checks/core/host-api.md#the-desktop)).

<a id="lifetime"></a>
## Lifetime (P5)

- **Single instance:** a named mutex or pipe. A second launch hands over to the first and exits.
- **Shutdown:** the tray icon is disposed (or it stays until the mouse passes over it), background loops are cancelled, settings are saved atomically, and child processes are stopped.
- **Ctrl+C and SIGTERM:** console apps and services handle `Console.CancelKeyPress` (set `Cancel` and stop cleanly), `PosixSignalRegistration` for SIGTERM on Linux, or use the Generic Host's console lifetime. They exit within a bound.
- **Start with Windows:** a Run key or scheduled task is added only when the user asks, and removed on uninstall.

<a id="console-tools"></a>
## Console tools (P7, P9)

- **Exit codes:** 0 only on success, distinct codes for usage errors and failures, and documented. deep_tools is the model: 0 PASS, 3 FAIL, 1 could not run.
- **stdout is for data:** a tool whose output is parsed writes logs to stderr. An MCP server over stdio writes nothing to stdout but protocol messages; a stray `Console.WriteLine` breaks the client.
- **Arguments:** parsed strictly, unknown options rejected, relative paths resolved against the current folder on purpose.
- **Non-interactive:** no prompts when stdin isn't a terminal. A flag or config for confirmations, so scripts and agents can run it.

<a id="publishing"></a>
## Publishing (P14)

- **The publish profile:** runtime identifier, self-contained, single-file, trimmed and AOT settings as the project intends, and the version stamped in the build.
- **Builds for another PC go to the user to install.** A session changes nothing on a client PC (ProcessMonitor's rule).
- **Notifications:** Windows toasts need the app's identity registered; Linux may have no notification daemon. Both fail quietly unless the code checks.
- **Updates:** no self-update without a signed or pinned source and the user's say.
