# Host and platform API

Pass P6. Every project runs inside something it doesn't own: the game, a plugin host, ASP.NET Core, the .NET runtime, the Python interpreter and its OS, the PowerShell host, a browser. What a call really does is in that thing's code or documentation, not in the method's name. For every external member a changed line uses, read its source (decompiled where needed) or its documentation, and write down what it does on the paths the change uses.

Packs add the facts of their hosts:
- the game: [se-plugin game-api](../../packs/se-plugin/game-api.md) (plugins, and the base for mods and PB scripts), [se-mod mod-api](../../packs/se-mod/mod-api.md), [pb-script](../../packs/pb-script/checks.md#p6-the-grid-api);
- patching other code: [se-plugin harmony](../../packs/se-plugin/harmony.md);
- ASP.NET Core, Blazor, EF Core and desktop UI stacks: [dotnet-app](../../packs/dotnet-app/README.md);
- the standard library and the OS: [python](../../packs/python/checks.md#p6-standard-library-and-os), [powershell](../../packs/powershell/checks.md#p6-cmdlets-and-the-host);
- the DOM and browser APIs: [web](../../packs/web/checks.md#p6-dom-and-browser-apis).

## Contracts you don't own

- **Null and empty results:** a host API with no nullable annotations can still return null, or an empty value that means "not ready". Find each case in its source, not in its signature.
- **Thread affinity:** most host objects are single-threaded (game state, UI controls, Blazor components, `DbContext`). Write down which thread each call needs, and check it in P3 ([concurrency.md](concurrency.md)).
- **DOM identity and accessibility hooks:** an id, `aria-describedby`, live region or focus target is a host contract. It must exist when focus enters, survive replacement and point at the current element. Check generated ids against collisions, preserve caller-owned ids, and test late content. ([accessibility.md](accessibility.md#names-roles-and-descriptions))
- **Side effects of the public path:** a call that skips the host's public method also skips its side effects, such as sync, events, validation, ownership and statistics. Read the public method, and say which side effects are lost. Real: REVIEW.md "skipped side effects"; APBL's Storage loss in magnetar-hub#126 (L13).
- **Library defaults that hide failure:** read the default of every option the code doesn't set: timeouts, retries, buffering, encoding, culture, padding. Real: Atomic.FM's dropped stream went undetected because NAudio's `WaveChannel32.PadWithZeroes` defaults to true and `PlaybackStopped` wasn't handled (L36). `HttpClient` waits 100 s by default; Python's `requests` and the browser's `fetch` wait forever.
- **Version availability:** a member that exists in the version you build against can be missing in the oldest version you run on, so the build passes and the load fails. Name the minimum host version, and check it in the docs or manifest. Real: quasar-hub#18, where an older PluginSdk lacked `Logger.EntryEmitted`/`ServerCommands.Execute` (CS0117, L07).
- **Behaviour changes across supported versions:** a host that changed a default between versions (an ASP.NET Core default, a Python deprecation, a PowerShell 7 parsing change) gives each version different behaviour. Check the change log of every version in the P0 runtime list.
- **Reflection into privates:** every private field or method reached by name can be renamed, moved or change visibility in an update. Resolve it at start-up, fail loudly there, and never use `!` to hide a failed lookup. (DR031. The game form is in [se-plugin game-api](../../packs/se-plugin/game-api.md#reflection-into-game-privates).)
- **Undocumented internals:** code that depends on internals (driver behaviour, a private field, an undocumented file format or endpoint) must check they are what it expects, and fail closed when they aren't. Real: DLSS PluginHub#177 relied on undocumented driver internals and patched on every GPU (L40).
- **Workarounds:** a workaround for a host bug the host has since fixed should be removed. Read the host's current code and compare.
- **Ignored results:** a host call that reports failure through a return value (`bool`, an error code, a result object, `$?`, a process exit code) must have that value checked. See [errors.md](errors.md#checks).

## The desktop

MasterRules 33a: nothing a session runs may interfere with the user's desktop without their permission. That covers the code under review when it runs in tests, scripts and tools, not only the review itself.

- **Process starts:** a helper, tool or test starts its children with no window and no focus change: `CreateProcess` with `CREATE_NO_WINDOW`, `ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true }`, Python `creationflags=subprocess.CREATE_NO_WINDOW`, PowerShell `Start-Process -NoNewWindow` or `-WindowStyle Hidden`. Never `cmd /c start` or `ShellExecute` for a helper. Real: a `cmd start` helper opened a window on the user's desktop on 2026-11-14.
- **Opening things for the user:** opening a URL, a file, Explorer or the browser, and showing toasts or tray balloons, are desktop actions. They belong only in features the user starts by hand, never in tests, start-up or background work.
- **Apps with a UI in tests:** a test that starts the app's window, or the game or Torch with its UI, is a finding unless it runs headless. PAM's testing on `E:\Torch` with the game client is the user's own setup and is allowed.
- **Screen capture:** only the app's own render, from inside the app, never the desktop.

## How to confirm

- **Quote the source.** Every claim about host behaviour names the host code line (decompiled where needed) or the documentation passage. A claim from memory is `unable to verify`.
- **Use the skills.** Each pack README names the skills that read its host's code. List the skills you used in the report.
