# Runtime differences

Pass P4. In P0, write down every runtime the code ships on: framework versions, OSes, language versions and hosts. A defect that exists on only one of them passes every test on the others. Tag every finding with its runtime scope ([Report.md](../../Report.md#severity) lists the tags, and each pack adds its own).

Packs add their own matrices:
- the hub loaders' runtimes and compile settings: [se-plugin loader-compile](../../packs/se-plugin/loader-compile.md);
- the game's script compiler: [se-mod whitelist](../../packs/se-mod/whitelist.md), [pb-script](../../packs/pb-script/checks.md#p4-compile-whitelist-and-size);
- trimming, AOT and single-file: [dotnet-app desktop-console](../../packs/dotnet-app/desktop-console.md), [dotnet-library packaging](../../packs/dotnet-library/packaging.md);
- Python versions: [python](../../packs/python/checks.md#p4-versions-and-operating-systems);
- Windows PowerShell 5.1 versus PowerShell 7: [powershell](../../packs/powershell/checks.md#p4-windows-powershell-51-versus-powershell-7);
- browsers and Node: [web](../../packs/web/checks.md#p4-browsers-and-node).

## Every language: Windows versus Linux

Many of the user's projects run on both: Magnetar's Interim build and Quasar on Linux containers, Flow on Windows and Linux, PluginCheck's Python on Windows driving Linux containers.

- **Paths:** Linux paths are case-sensitive and use `/`. A literal `\` in a path, or a case mismatch between the code and a file on disk, works on Windows only. Windows reserves `CON`, `NUL`, `COM1` and similar names, and forbids `<>:"|?*` in names.
- **Path length:** Windows APIs fail past 260 characters unless long paths are enabled for both the OS and the process. Real: PluginCheck's Legacy and Torch containers failed to bind-mount host folders 18 or more parts deep, so its copies run from a short path.
- **Line endings:** CRLF and LF differ in files you hash, compare, diff or parse. Git's `core.autocrlf` and `.gitattributes` change the bytes on checkout, so a hash of a working tree depends on them (GATE-RECORD.md's `contentSha256` hashes bytes as they are on disk).
- **File locking:** Windows refuses to delete or rename a file another handle has open, and antivirus or the indexer can hold a new file for a moment, so a replace fails with access denied now and then. Linux allows both, so a defect there shows up as two writers both succeeding.
- **Environment variables:** names are case-insensitive on Windows and case-sensitive on Linux.
- **Processes:** Windows passes one command-line string that each program splits itself, so quoting rules differ by program. Windows finds executables through `PATHEXT` and can search the current folder first. There is no SIGTERM on Windows: a console app gets Ctrl+C or Ctrl+Break events, and a process with no console gets neither.
- **Time zones:** Linux uses IANA ids (`Europe/London`); Windows uses its own (`GMT Standard Time`).

## .NET Framework 4.8, .NET 10 and Mono

This applies to any project that builds one source tree for more than one .NET runtime: net48 plus net10 targets, `netstandard2.0` libraries loaded by both, and plugins compiled by several loaders (the plugin matrix is in [se-plugin loader-compile](../../packs/se-plugin/loader-compile.md)).

### Behaviour that differs

**Threading and TPL**
- **Inline continuations:** when a task an `await` is waiting on completes, for example on cancel, its continuation can run inline on the completing thread on net48 and Mono. On .NET 10 the same code queued it to the pool. Real: SensEssentials `Task.Delay(d, token)` plus `Cancel()` under a lock. On net48 it re-entered and threw "Collection was modified"; on net10 it didn't. Never complete or cancel under a lock, and never rely on either behaviour.
- **Exceptions:** an exception escaping `async void` or a thread-pool callback terminates the process on both runtimes. The legacy `legacyUnhandledExceptionPolicy` switch is not set in the hosts here.
- **`Thread.Abort`:** throws `PlatformNotSupportedException` on .NET 10. `AppDomain.CreateDomain`, Remoting and `BinaryFormatter` are gone on .NET 9+. `BinaryFormatter` throws there.

**Hashing and ordering**
- **`string.GetHashCode()`:** randomized per process on .NET 10, stable on net48. A persisted hash, or a hash used as a file name, cache key or seed across restarts, breaks on .NET 10 only.
- **Enumeration order:** `Dictionary`/`HashSet` order is not guaranteed on either runtime, and it differs after removals. Output, file content or "first match" logic that depends on it is a defect.
- **Sorting:** `List.Sort`/`Array.Sort` are unstable on both runtimes, but they can order equal keys differently between runtimes. `OrderBy` is stable. A tie-break that relies on input order needs `OrderBy` or an explicit key.

**Text, numbers and encoding**
- **Globalization:** .NET 10 on Linux uses ICU, and containers often run in invariant globalization mode. Culture-sensitive `IndexOf`, `StartsWith`, `Compare` and `ToUpper` can return different results from net48's NLS. Under ICU, culture-sensitive `IndexOf` ignores zero-width characters, and `"\r\n".IndexOf("\n")` differs. Use `StringComparison.Ordinal`/`OrdinalIgnoreCase` for anything that is not shown to people. `new CultureInfo("xx-YY")` can throw, or act invariant, in invariant mode.
- **Number formatting:** `double.ToString()` with no format gives the shortest round-trippable text on .NET Core 3.0+, but up to 15 significant digits on net48. Persisted text, file names and comparisons of formatted numbers differ between runtimes. Use `"R"`/`"G17"` or a fixed format, always with `CultureInfo.InvariantCulture`.
- **`Encoding.Default`:** the ANSI code page on net48, UTF-8 on .NET 10. Any `Encoding.Default`, or an API defaulting to it, such as `StreamReader` with no encoding in some overloads, `File.ReadAllText(path, Encoding.Default)` or `Console`, differs.

**Paths and files**
- **`Path.Combine(root, input)`:** returns `input` when it is rooted (`/etc/x`, `C:\x`), silently discarding `root`, on both runtimes. See [inputs.md](inputs.md#paths).
- **File locking and replace:** `FileShare` is advisory on Linux, so two writers both succeed. `File.Replace` on Linux needs the destination to exist, and on both platforms it fails across volumes. `File.Move(src, dst, overwrite: true)` does not exist on net48.
- **Network mounts:** `File.GetLastWriteTimeUtc` on a missing path returns the 1601 date on Windows. On Linux it throws for any errno other than ENOENT/ENOTDIR.
- **`Process.Start(string)`:** defaults to `UseShellExecute=true` on net48 and `false` on .NET 10. Opening a URL works on one runtime only.

**Time**
- **Tick counters:** `Environment.TickCount` wraps after 24.9 days on both runtimes. `TickCount64` doesn't exist on net48.
- **Time zones:** on Linux, `TimeZoneInfo` ids are IANA. .NET 6+ converts Windows ids, but net48 accepts only Windows ids.

**Performance**
- **`Enum.HasFlag`:** boxes on net48, but the .NET 10 JIT removes the box. It's a hot-path allocation on net48 only.
- **Struct enumerators:** `foreach` over an interface-typed collection (`IEnumerable<T>`, `IList<T>`) boxes the enumerator on both runtimes. It's worse on net48 because there is no dynamic PGO.

### APIs that don't exist on net48

Code compiled for net48 from the same sources gets a compile error for each of these (CS0117/CS1061/CS0246 on the net48 build only). Read every net48-only compile error. The APIs:

- **Strings and dictionaries:** `Dictionary.TryAdd`, `GetValueOrDefault` (CollectionExtensions), `KeyValuePair.Deconstruct`, `string.Contains(char)`, `Split(char, StringSplitOptions)`, `StartsWith(char)`, `string.Create`, `string.Join(char, ...)`.
- **Math and helpers:** `Math.Clamp`, `MathF`, `HashCode`, `Random.Shared`, `BitOperations`, `ArgumentNullException.ThrowIfNull`, `ObjectDisposedException.ThrowIf`.
- **Language support types:** `Index`/`Range` (the `^1` and `..` syntax), `IAsyncEnumerable` and `await foreach`, `IsExternalInit` (needed by `init` and `record`; a shim is allowed), `RequiredMemberAttribute`, default interface members (CS8701 on net48).
- **Compiler polyfills and MA0182:** the compiler needs `IsExternalInit` (for `init` and `record`) and `RequiredMemberAttribute` plus `CompilerFeatureRequiredAttribute` (for `required`) to exist, and it emits references to them itself, so no source names them. MA0182 (unused internal type) would report a shim that the build needs, so deep_tools drops MA0182 on these four exact full names: `System.Runtime.CompilerServices.IsExternalInit`, `RequiredMemberAttribute` and `CompilerFeatureRequiredAttribute`, and `System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute`. The shim must be declared `internal`, at the top level of that namespace, with the real type's shape and nothing more: an empty body (`static` allowed for `IsExternalInit`), `: Attribute` for the attributes, only `[AttributeUsage]`/`[EditorBrowsable]`, and for `CompilerFeatureRequiredAttribute` only its `(string featureName)` constructor, `FeatureName`, `IsOptional` and the `RefStructs`/`RequiredMembers` constants. A lookalike (wrong namespace, `public`, `partial`, extra members) keeps its MA0182. Source generators such as PolySharp are not an option for hub plugins, because the loaders compile the source themselves. Nullable flow attributes (`NotNullWhen` and the rest) are left out: source names them wherever they are used, so one MA0182 reports is really unused. Dropped results are listed in tools-report.json `compilerPolyfills[]`.
- **Spans and pools:** `Span<T>` APIs beyond System.Memory, `SearchValues`, `ArrayPool` (only with System.Buffers), `Stopwatch.GetElapsedTime`.
- **Threading and tasks:** `Task.WaitAsync`, `CancellationTokenSource.TryReset`, `System.Threading.Lock`, `PeriodicTimer`, `Parallel.ForEachAsync`.
- **LINQ:** `Enumerable.TakeLast`/`SkipLast`/`Chunk`/`MaxBy`/`MinBy`/`DistinctBy`/`Order`.
- **Serialization:** `System.Text.Json` is not part of net48; it comes only as a package or from a host. Confirm what supplies it on every runtime before relying on it.

A feature the language accepts can still need a runtime type that net48 lacks. Collection expressions targeting `Span`, `params ReadOnlySpan<T>`, and `ref struct` interfaces are examples.

### Preprocessor and build settings

- **Per-target branches:** `#if NETFRAMEWORK`, `#if NET8_0_OR_GREATER` and similar make each target a different program. Each branch needs its own tests, on its own runtime. Check that the symbols used are ones the real compile defines: the hub loaders define their own set ([se-plugin loader-compile](../../packs/se-plugin/loader-compile.md#preprocessor-and-build-settings)).
- **Debug-only code:** see [correctness.md](correctness.md#dr029) (DR029).
- **Settings per target:** `Directory.Build.props`, per-TFM `Condition`s and `<Nullable>`/`<LangVersion>`/`<DefineConstants>` can differ by target. Check each setting the change relies on applies to every target that ships.

### Mono

Mono's TPL is the .NET Framework reference source, so when Windows isn't available, Mono is a good stand-in for net48 task behaviour. The hub reviewer reproduced the SensEssentials re-entrancy on Mono 6.8. The reverse also holds: a failure that only shows under Mono or Wine may be a Mono difference, so confirm on Windows .NET Framework (the runtime-matrix harness runs natively) before reporting it as a net48 defect.

## How to confirm

- **net48-only compile errors:** quote the diagnostic with its runtime.
- **Behaviour differences:** use a runtime-matrix harness ([`tools/runtime-matrix`](../../tools/runtime-matrix)), with the output from each runtime in the finding. For other languages, the pack names the harness.
- **A difference you can only read about, not run:** label it `unable to verify`, and name the documentation or reference source you read.
