# Security and supply chain

Pass P10. Stage 1 is the baseline: `se-dev-plugin-review`'s `ReviewPolicy.md` for hub plugins, the built-in `security-review` for everything else. This pass goes deeper on the changed and affected code, and on what the tools found. Every claim gets a label: verified, not present, not run, or unable to verify.

Packs add their sinks and policies: the hub policy, host packages, legal pointers and Harmony on checks ([se-plugin security](../../packs/se-plugin/security.md), [harmony](../../packs/se-plugin/harmony.md)), the whitelist as a sandbox ([se-mod](../../packs/se-mod/whitelist.md)), web authorization, CSRF, XSS and CORS ([dotnet-app web-security](../../packs/dotnet-app/web-security.md)), packages and dependencies ([dotnet-library packaging](../../packs/dotnet-library/packaging.md)), subprocess, pickle and tar ([python](../../packs/python/checks.md#p10-security)), `Invoke-Expression`, quoting and secrets ([powershell](../../packs/powershell/checks.md#p10-security)), DOM sinks, CSP and dependencies ([web](../../packs/web/checks.md#p10-security)).

## Code

- **Deserialization:**
  - `BinaryFormatter`, `SoapFormatter`, `NetDataContractSerializer`, `LosFormatter`, `ObjectStateFormatter` and `JavaScriptSerializer` with type resolvers are remote-code-execution sinks. So is Newtonsoft with `TypeNameHandling` other than `None`, and so are `DataContractSerializer`/`XmlSerializer` with types chosen from input.
  - Any of these on data that a user, a file someone else can write, or the network controls is a blocker. (BannedSymbols, CA2300-CA2330)
- **XML:**
  - `XmlTextReader`, and `XmlReaderSettings` with `DtdProcessing.Parse` and a resolver, resolve external entities on net48.
  - Set `DtdProcessing = Prohibit` and `XmlResolver = null` explicitly in shared code. Don't rely on runtime defaults, which differ by framework version.
  - `XmlDocument` that loads only the program's own fixed file is low risk. Say so, and give the evidence. (CA3075/CA3077)
- **Process and dynamic code:**
  - `Process.Start` and shell commands. Arguments go in `ArgumentList` (one item per argument), never a string built from input; never through `cmd /c` or a shell with input in it.
  - The executable is a full path, or resolved once from a trusted folder. A bare name can resolve to a file planted in the current folder or early in `PATH`, and a tool that runs in a reviewed repository's folder hands that repository the choice.
  - `Assembly.Load(byte[])`/`LoadFrom`/`LoadFile`, `AppDomain.CreateDomain`, `CSharpCodeProvider`/Roslyn at runtime.
  - `Activator.CreateInstance`/`Type.GetType` with names from input.
  - Reflection driven by input.
  - Each one is traced to its input.
  - Opening a fixed HTTPS URL is not arbitrary execution (ReviewPolicy.md).
  - **Opening a local page (DR038):** `Process.Start(string)` and `Process.Start(ProcessStartInfo)` are allowed only for a fixed local web page, because opening a page on this machine at an address the code fixes runs nothing the code doesn't show (the old ban text already said so for fixed links; the user, 2026-11-17: "add an exception to the banned list. if the address opened only shows local pages"). The target, or the ProcessStartInfo's `FileName`, is an `http`/`https` URL whose scheme and host (`127.0.0.1`, `localhost` or `[::1]`) are constant text, in the forms DR037 accepts: a constant, an interpolation or concatenation that starts with that text and closes the host with `:`, `/`, `?` or `#`, a local set once, or a static readonly field in the same file. A port after `host:` must be an integer hole with no format, so it can't add `@other.host`. No `Arguments` or `ArgumentList`, and no `Verb` except `"open"`. Everything else is DR038: other hosts, programs, paths, URLs from parameters, fields or config, a ProcessStartInfo the code hands to other methods, and method groups. `Start(string, string)`, `Start(string, IEnumerable<string>)` and the instance `Start()` stay banned (RS0030). DR038 doesn't check `UseShellExecute`: without it, a URL opens on net48 only ([runtime-differences](runtime-differences.md)).
  - **SHA-1 (DR039):** SHA-1 is allowed only for the WebSocket handshake answer of RFC 6455 section 4.2.2, `Sec-WebSocket-Accept = Base64(SHA1(Sec-WebSocket-Key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"))`, because browsers reject any other hash (the user, 2026-11-21: "SHA handshakes are an exception"). The key is the request's `Sec-WebSocket-Key` header value, the GUID is that exact constant, and the Base64 text only becomes the `Sec-WebSocket-Accept` header value or its line in the 101 response. DR039 reports every other SHA-1 use, and a handshake lookalike that hashes other bytes or sends the result elsewhere; CA5350, S4790 and SCS0006 are dropped only at the accepted call. Exact accepted forms: DeepRules README, "DR039, the WebSocket handshake". A parameter named `secWebSocketKey` is accepted by its name, so read the caller. Never accept SHA-1 for a password, token, signature, cache key that crosses a trust boundary or file integrity check.
- **Network:**
  - Enumerate every host contacted, the data sent, and when.
  - Plain HTTP, certificate validation callbacks returning true, and `ServicePointManager` changes: the last two affect the whole process on net48.
  - Undisclosed endpoints. Real: Atomic.FM PluginHub#175, a plain-HTTP stream from the author's server, undisclosed, exposing listener IPs (L26).
  - Telemetry: disclosed, and off where the user's rules say so (MasterRules 28).
  - Self-update: banned for plugins (ReviewPolicy.md), and for any other project it needs a signed or pinned source and the user's say.
  - Listening sockets: what address a server binds. A local tool bound to `0.0.0.0` or `*` is reachable from the network; bind to loopback unless the feature needs more ([dotnet-app web-security](../../packs/dotnet-app/web-security.md#exposure)).
- **HttpClient:**
  - `new HttpClient()` per call exhausts sockets on net48 (TIME_WAIT).
  - A static instance forever ignores DNS changes. On .NET 10 use `SocketsHttpHandler.PooledConnectionLifetime`; on net48, one shared instance plus `ServicePoint` connection lease timeouts.
  - `WebClient`/`HttpWebRequest`: obsolete, synchronous, and they block a thread. They must never run on a host's main thread.
- **Secrets:**
  - Webhook URLs, tokens, passwords, connection strings and keys in config are secrets. They must not be logged, echoed to chat or a page, included in error text, exported in profiles, passed on a command line (other processes can read it), or copied into run and output folders (MasterRules 30a).
  - Hard-coded keys are a blocker. gitleaks runs over the tree and, with a base, over the commit range: a secret removed in the head is still in the history.
  - A secret passed to a child process goes in an environment variable set for that child only, never the whole session's environment.
- **Personal data:**
  - Steam IDs, IPs, GPS and private coordinates, emails and Discord IDs sent to Discord or any external service must be needed, documented and opt-in.
  - Exports must not carry owner IDs or Steam IDs the destination doesn't need (ReviewPolicy.md).
- **Crypto and randomness:**
  - MD5/SHA1/DES/3DES/RC2 and `RNGCryptoServiceProvider` (obsolete) for anything security-relevant.
  - `System.Random` for anything a user shouldn't predict, such as tokens, codes or nonces. (CA5350/CA5351, BannedSymbols)
  - Secrets and nonces compared in constant time (`CryptographicOperations.FixedTimeEquals`).
- **Injection:**
  - Text from users concatenated into commands run with rights, into SQL, into Discord markdown, into HTML (`MarkupString`, raw HTML, `innerHTML`), into log lines, or into a script (PowerShell, a shell).
  - SecurityCodeScan's taint results (SCS*) are leads. Read each one.
- **Patches and overrides of checks:** code that changes permissions, ownership, safe zones, damage or authorization in someone else's code is reviewed as an authority change ([authority-interop.md](authority-interop.md)).

## Supply chain

- **Vulnerable packages:** `dotnet list package --vulnerable --include-transitive` results, plus `pip-audit`, `npm audit`, Trivy or OWASP Dependency-Check where they ran. Each vulnerable package gets a verdict: reachable or not, and fixed version available.
- **References:**
  - Package, project and assembly references, each with provenance (ReviewPolicy.md for plugins).
  - New dependencies in a project that promises few or none (SimpleDiscordDotNet is BCL only) are a finding unless the user agreed.
  - Versions pinned and a lock file committed where the ecosystem has one (`packages.lock.json`, `package-lock.json`, `uv.lock`).
  - IDE-only references versus runtime dependencies.
- **Committed binaries:** a byte-for-byte match with an official artifact, or a blocker for plugins (ReviewPolicy.md), and a finding anywhere else. Real: DLSS PluginHub#177 committed a 58 MB binary (L41).
- **Build scripts, generators, MSBuild targets, analyzers, package install scripts and CI workflows in the repo:** these run on the developer's machine and in CI, so read them.
- **Downloads at run or build time:** a script that downloads and runs code (`iwr | iex`, `curl | sh`, a tool fetched at run time) needs a pinned version and a hash check, or it is a finding. deep_tools fetches nothing at run time for this reason.

## Legal (pointers only)

License claim versus content, inherited code, notices kept, and assets' licenses. For plugins: PluginCheck REVIEW.md "Legal" and "Copied code". Real cases:
- DoomForSe PluginHub#342: an MIT `LICENSE` over GPL code, and removed GPL notices (L16).
- Anomaly PluginHub#177: redistributed Keen shader sources (L39).
- APBL magnetar-hub#156: a relicense with all contributors identified.
- CaptureZones: a private paid mod, all rights reserved; its code is never reused elsewhere.
- Decompiled game code is reference only: never copied into a project or published.

This pass checks only what changed: new files, new inherited code, and changed notices.
