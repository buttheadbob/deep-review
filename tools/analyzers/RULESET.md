# Deep review rule set

Generated from `deep.globalconfig` by `python deep_tools.py --ruleset`. Do not edit by hand: change the config and regenerate.

Every analyzer rule that is enabled by default runs as a warning (`dotnet_analyzer_diagnostic.severity = warning`). The rules below are
the only ones turned off, for every project alike; code under review may not turn any rule off itself (a `#pragma`, `[SuppressMessage]`,
`<NoWarn>` or a repo analyzer config is a gate issue).

Off: 190 entries.

| rule or category | severity | reason |
|---|---|---|
| `category-Style` | none | style category: code shape only, never a defect |
| `category-Naming` | none | naming category: names only, never a defect |
| `category-Documentation` | none | documentation-comment rules: docs, not behaviour |
| `category-Formatting` | none | formatting category: whitespace and layout only |
| `category-Readability` | none | readability category: style only |
| `category-Layout` | none | layout category: formatting only |
| `category-Ordering` | none | member ordering: style only |
| `category-Spacing` | none | spacing: formatting only |
| `category-Info Code Smell` | none | Sonar informational smells: style (Sonar rules that find defects are turned on by id below) |
| `category-Minor Code Smell` | none | Sonar minor smells: style and taste (defect-finding ones are turned on by id below) |
| `CA1002` | none | API design (List<T> in public API): plugins are not reusable libraries |
| `CA1003` | none | API design (EventHandler<T> signatures): game and Torch events use their own delegates |
| `CA1005` | none | API design (generic parameter count): taste |
| `CA1008` | none | API design (enum zero value): config enums map to fixed saved values |
| `CA1010` | none | API design (generic collection interfaces): taste |
| `CA1012` | none | API design (abstract constructors): style |
| `CA1014` | none | CLSCompliant attribute: not applicable to plugins |
| `CA1016` | none | assembly version attribute: covered by the release consistency check |
| `CA1017` | none | ComVisible attribute: not applicable to plugins |
| `CA1018` | none | AttributeUsage on custom attributes: style |
| `CA1019` | none | attribute argument accessors: style |
| `CA1021` | none | out parameters: style; game APIs use out parameters |
| `CA1024` | none | use properties where appropriate: style |
| `CA1027` | none | Flags attribute on enums: style |
| `CA1028` | none | enum storage type: saved config enums keep their type |
| `CA1030` | none | use events where appropriate: style |
| `CA1031` | none | contradicts DR015 (a catch-all is required at game entry points: Update, handlers, commands, so one exception does not stop the server); catches that hide errors are found by EPC12/ERP022/RCS1075 |
| `CA1032` | none | standard exception constructors: style |
| `CA1033` | none | interface methods callable by child types: API design for libraries |
| `CA1034` | none | nested visible types: style |
| `CA1036` | none | comparison operators with IComparable: API design |
| `CA1040` | none | empty interfaces: style |
| `CA1041` | none | Obsolete message: documentation |
| `CA1043` | none | indexer argument types: API design |
| `CA1044` | none | write-only properties: API design |
| `CA1045` | none | ref parameters: style; game APIs use ref |
| `CA1046` | none | equality operator on reference types: API design |
| `CA1050` | none | types in namespaces: style |
| `CA1051` | none | visible instance fields: style; XML-serialised configs and game patterns use fields |
| `CA1052` | none | static holder types: style |
| `CA1054` | none | URI parameters as System.Uri: API design |
| `CA1055` | none | URI return values as System.Uri: API design |
| `CA1056` | none | URI properties as System.Uri: API design; XML configs store strings |
| `CA1060` | none | P/Invokes in a NativeMethods class: code organisation only |
| `CA1062` | none | public argument null checks: plugins are not libraries; nullable warnings cover this where enabled |
| `CA1063` | none | full Dispose(bool) pattern: style for sealed and finalizer-less types; real disposal bugs are found by CA2213/IDISP* |
| `CA1064` | none | public exceptions: API design |
| `CA1066` | none | IEquatable when overriding Equals: API design |
| `CA1067` | none | override Equals for IEquatable: API design |
| `CA1068` | none | CancellationToken last: API design |
| `CA1303` | none | localized strings: plugins log and reply in English |
| `CA1501` | none | inheritance depth: game and WPF types are deep by design |
| `CA1502` | none | cyclomatic complexity: metric, not a defect |
| `CA1505` | none | maintainability index: metric, not a defect |
| `CA1506` | none | class coupling: metric, not a defect |
| `CA1507` | none | nameof for parameter names: style |
| `CA1510` | none | ArgumentNullException.ThrowIfNull does not exist on net48 |
| `CA1511` | none | ArgumentException.ThrowIfNullOrEmpty does not exist on net48 |
| `CA1512` | none | ArgumentOutOfRangeException.ThrowIf* does not exist on net48 |
| `CA1513` | none | ObjectDisposedException.ThrowIf does not exist on net48 |
| `CA1515` | none | make public types internal: plugin types are public on purpose (the loaders find them by reflection) |
| `CA1700` | none | naming: enum value names |
| `CA1707` | none | naming: underscores (Harmony patch parameters such as __instance need them) |
| `CA1708` | none | naming: case-only differences |
| `CA1710` | none | naming: collection suffixes |
| `CA1711` | none | naming: reserved suffixes |
| `CA1712` | none | naming: enum value prefixes |
| `CA1714` | none | naming: flags enum plurals |
| `CA1715` | none | naming: interface and type parameter prefixes |
| `CA1716` | none | naming: reserved keywords |
| `CA1717` | none | naming: enum plurals |
| `CA1720` | none | naming: type names in identifiers |
| `CA1721` | none | naming: property vs method names |
| `CA1724` | none | naming: type names that match namespaces |
| `CA1725` | none | naming: parameter names in overrides |
| `CA1727` | none | naming: logging placeholders |
| `CA1805` | none | explicit default initialisation: style |
| `CA1812` | none | uninstantiated internal classes: the loaders and XML serialisers create them by reflection |
| `CA1815` | none | Equals on value types: API design |
| `CA1819` | none | array properties: XML-serialised configs use arrays |
| `CA1822` | none | mark members static: style |
| `CA1848` | none | LoggerMessage delegates: plugins log through NLog or the PluginSdk, not Microsoft.Extensions.Logging |
| `CA1852` | none | seal internal types: style-level micro-optimisation |
| `CA2007` | none | ConfigureAwait: plugin code has no UI SynchronizationContext to capture (the game thread queue is not one); deadlock risks are found by VSTHRD002/VSTHRD103 |
| `CA2211` | none | non-constant static fields visible: plugin singletons (Instance) by design; races are found by S2696 and DeepRules |
| `CA2217` | none | Flags enum values: style |
| `CA2225` | none | operator alternates: API design |
| `CA2227` | none | settable collection properties: XML serialisation needs setters |
| `CA2234` | none | pass System.Uri instead of string: API design |
| `CA2252` | none | preview features: not used (LangVersion latest rule) |
| `CA2263` | none | generic Enum overloads do not exist on net48 |
| `CA5394` | none | insecure randomness: game randomness is not security (crypto rules CA5350/CA5351 stay on) |
| `MA0003` | none | named boolean arguments: style |
| `MA0004` | none | ConfigureAwait(false): same as CA2007, no UI context in plugin code |
| `MA0006` | none | string.Equals instead of ==: == on strings is ordinal already; culture issues are MA0001/CA1307/CA1309 |
| `MA0007` | none | trailing commas: formatting |
| `MA0015` | none | parameter name in ArgumentException: message quality, not behaviour |
| `MA0016` | none | prefer collection abstractions: API design |
| `MA0025` | none | NotImplementedException: covered by the TODO check |
| `MA0026` | none | TODO comments: duplicate of the release check, which counts them |
| `MA0036` | none | make class static: style |
| `MA0038` | none | make method static: style |
| `MA0041` | none | make property static: style |
| `MA0046` | none | EventHandler delegate shape: API design |
| `MA0047` | none | types in namespaces: style |
| `MA0048` | none | file name matches type name: style |
| `MA0051` | none | method length: metric, not a defect |
| `MA0053` | none | seal classes: style |
| `MA0069` | none | non-constant static fields visible: same as CA2211 |
| `MA0071` | none | avoid redundant else: style |
| `MA0097` | none | IComparable operators: API design |
| `MA0127` | none | string.Equals instead of 'is' pattern: style |
| `MA0154` | none | langword in XML comments: documentation |
| `MA0158` | none | System.Threading.Lock does not exist on net48 |
| `MA0184` | none | interpolated string without parameters: style |
| `RCS1001` | none | braces: formatting |
| `RCS1003` | none | braces on if-else: formatting |
| `RCS1018` | none | accessibility modifiers: style |
| `RCS1021` | none | lambda body shape: style |
| `RCS1037` | none | trailing whitespace: formatting |
| `RCS1049` | none | boolean comparison: style |
| `RCS1061` | none | merge nested if: style |
| `RCS1073` | none | if to return: style |
| `RCS1085` | none | auto-property: style |
| `RCS1090` | none | ConfigureAwait: same as CA2007 |
| `RCS1102` | none | make class static: style |
| `RCS1112` | none | combine Where calls: style |
| `RCS1123` | none | parentheses for clarity: formatting |
| `RCS1124` | none | inline local: style |
| `RCS1129` | none | redundant field initialisation: style |
| `RCS1136` | none | merge switch sections: style |
| `RCS1139` | none | summary element: documentation |
| `RCS1140` | none | exception documentation: documentation |
| `RCS1141` | none | param documentation: documentation |
| `RCS1142` | none | typeparam documentation: documentation |
| `RCS1146` | none | conditional access: style |
| `RCS1161` | none | enum explicit values: style |
| `RCS1163` | none | unused parameter: event handlers and Harmony patches need fixed signatures (S1172 is off for the same reason) |
| `RCS1169` | none | make field read-only: style |
| `RCS1170` | none | read-only auto-property: style |
| `RCS1181` | none | comment placement: formatting |
| `RCS1186` | none | Regex instance vs static: style |
| `RCS1189` | none | region directives: formatting |
| `RCS1194` | none | exception constructors: style |
| `RCS1197` | none | StringBuilder call shape: style |
| `RCS1201` | none | method chaining: style |
| `RCS1226` | none | documentation paragraphs: documentation |
| `RCS1231` | none | ref read-only parameters: style |
| `RCS1234` | none | duplicate enum values: style (CA1069 stays on for real duplicates) |
| `RCS1240` | none | unnecessary operator: style |
| `RCS1241` | none | IComparable implementation: API design |
| `RCS1246` | none | element access instead of First(): style |
| `RCS1251` | none | braces on record declarations: formatting |
| `S101` | none | naming: type names |
| `S107` | none | parameter count: metric |
| `S1066` | none | merge nested if: style |
| `S1104` | none | public fields: XML-serialised configs use fields |
| `S1118` | none | utility class constructors: style |
| `S1135` | none | TODO comments: duplicate of the release check, which counts them |
| `S1172` | none | unused parameter: event handlers and Harmony patches need fixed signatures |
| `S1192` | none | duplicated string literals: style |
| `S125` | none | commented-out code: style |
| `S138` | none | method length: metric |
| `S1450` | none | field could be local: style |
| `S1541` | none | complexity: metric |
| `S2245` | none | weak random: game randomness is not security (same as CA5394) |
| `S2325` | none | make member static: style |
| `S3011` | none | reflection past private does not apply to SE plugins as a defect: they must reach game internals with no public API (the user, 2026-11-17: exempt for all tests); reflection targets are checked against the decompiled game instead |
| `S3242` | none | parameter type could be more general: API design |
| `S3267` | none | loops to LINQ: style (LINQ allocates on hot paths) |
| `S3358` | none | nested ternaries: style |
| `S3776` | none | cognitive complexity: metric |
| `S4136` | none | overload ordering: style |
| `S4144` | none | identical method bodies: style |
| `S9422` | none | System.Threading.Lock does not exist on net48 |
| `SCS0005` | none | weak random: game randomness is not security (same as CA5394) |
| `IDISP017` | none | prefer using over Dispose(): disposal style only |
| `IDISP020` | none | SuppressFinalize(this) shape: style |
| `IDISP021` | none | Dispose(true) shape: style |
| `IDISP022` | none | Dispose(false) shape: style |
| `IDISP023` | none | finalizer member access shape: style |
| `IDISP025` | none | sealed class disposal shape: style |
| `VSTHRD001` | none | JoinableTaskFactory.SwitchToMainThreadAsync: Visual Studio only; the game thread queue is checked by DeepRules |
| `VSTHRD004` | none | await SwitchToMainThreadAsync: Visual Studio only |
| `VSTHRD010` | none | VS main-thread affinity: Visual Studio only |
| `VSTHRD012` | none | JoinableTaskFactory overloads: Visual Studio only |
| `VSTHRD108` | none | thread affinity assertions: Visual Studio only |
| `VSTHRD111` | none | ConfigureAwait(bool): same as CA2007 |
| `VSTHRD200` | none | Async suffix: naming |
| `CA2249` | none | Contains instead of IndexOf: readability only, same cost; off by default (the user, 2026-11-21) |

Turned on explicitly (shipped disabled by default, or kept on whatever their category): 137 rules.

`CA1001`, `CA1065`, `CA1069`, `CA1304`, `CA1305`, `CA1307`, `CA1309`, `CA1310`, `CA1311`, `CA1508`, `CA1806`, `CA1816`, `CA1821`, `CA1823`, `CA1826`, `CA1827`, `CA1828`, `CA1829`, `CA1836`, `CA1841`, `CA1849`, `CA1851`, `CA1854`, `CA2000`, `CA2002`, `CA2008`, `CA2009`, `CA2011`, `CA2012`, `CA2016`, `CA2100`, `CA2101`, `CA2201`, `CA2213`, `CA2214`, `CA2215`, `CA2216`, `CA2218`, `CA2219`, `CA2224`, `CA2229`, `CA2231`, `CA2235`, `CA2237`, `CA2241`, `CA2242`, `CA2250`, `CA2251`, `CA2300`, `CA2301`, `CA2302`, `CA2305`, `CA2310`, `CA2311`, `CA2312`, `CA2315`, `CA2321`, `CA2322`, `CA2326`, `CA2327`, `CA2328`, `CA2329`, `CA2330`, `CA2350`, `CA2351`, `CA2352`, `CA2353`, `CA2354`, `CA2355`, `CA2356`, `CA2361`, `CA2362`, `CA3001`, `CA3003`, `CA3006`, `CA3007`, `CA3011`, `CA3012`, `CA3061`, `CA3075`, `CA3076`, `CA3077`, `CA3147`, `CA5350`, `CA5351`, `CA5359`, `CA5360`, `CA5363`, `CA5364`, `CA5365`, `CA5369`, `CA5370`, `CA5371`, `CA5372`, `CA5373`, `CA5374`, `CA5379`, `CA5384`, `CA5385`, `CA5386`, `CA5397`, `CA5398`, `MA0011`, `MA0042`, `MA0045`, `MA0076`, `MA0134`, `MA0147`, `MA0155`, `RCS1210`, `RCS1213`, `RCS1229`, `S108`, `S1144`, `S1854`, `S2223`, `S2551`, `S2696`, `S2930`, `S2931`, `S2952`, `S2953`, `S3168`, `S3881`, `S3966`, `S4462`, `S4487`, `S6562`, `S6580`, `VSTHRD002`, `VSTHRD100`, `VSTHRD101`, `VSTHRD103`, `VSTHRD110`, `RS0030`, `RS0031`, `RS0035`

## Other languages

Same rule: every rule of every checker runs, and these central configs are the only place one is turned off. The
repository's own ruff, mypy, bandit, PSScriptAnalyzer, ESLint and gitleaks configs are never used; inline `# noqa`,
`# type: ignore`, `# nosec`, `eslint-disable`, `@ts-ignore`, PowerShell `SuppressMessage` and `gitleaks:allow` are gate issues.

| checker | config | starts from |
|---|---|---|
| ruff | `deep-ruff.toml` | `select = ["ALL"]`, inline noqa ignored (`--ignore-noqa`) |
| mypy | `deep-mypy.ini` | `--strict` plus warn_unreachable, strict_equality, extra_checks; missing stubs are gaps |
| bandit | `deep-bandit.yaml` | every test, every severity and confidence, `--ignore-nosec` |
| pytest | (none) | the repo's tests, `-p no:cacheprovider`, addopts cleared, 30 min limit; no tests is a gap |
| PSScriptAnalyzer | `deep-psscriptanalyzer.psd1` | every default rule at every severity plus the configurable rules |
| ESLint | `deep-eslint.config.mjs` | `@eslint/js` recommended (JS), typescript-eslint `strictTypeChecked` (TS), inline config off |
| gitleaks | `deep-gitleaks.toml` | the built-in rules, `--redact`, repo ignore files not used |

Off: 8 entries.

| checker | rule | reason |
|---|---|---|
| ruff | `D203` | conflicts with D211 (no blank line before a class docstring); D211 is kept |
| ruff | `D213` | conflicts with D212 (multi-line summary on the first line); D212 is kept |
| ruff | `COM812` | conflicts with the ruff formatter (trailing commas); ruff itself warns when both are on |
| ruff | `ISC001` | conflicts with the ruff formatter (implicit string concatenation on one line) |
| PSScriptAnalyzer | `PSUseCompatibleCmdlets` | needs a target platform profile list; not a defect check without one |
| PSScriptAnalyzer | `PSUseCompatibleCommands` | needs target platform profiles to compare against |
| PSScriptAnalyzer | `PSUseCompatibleSyntax` | needs a target PowerShell version list |
| PSScriptAnalyzer | `PSUseCompatibleTypes` | needs target platform profiles to compare against |

## Review-only rules

Rules whose descriptor carries the custom tag `ReviewOnly` (the rule cannot be made precise). Their hits do not count
in the gate; they are kept in tools-report.json under `reviewOnly[]` as input for the stage-2 review. Reasons come from
the DeepRules README.

| rule | reason |
|---|---|
| `DR002` | review-only: The change runs through callbacks or other members; whether it happens during the loop depends on re-entrancy and timing (and `Dictionary.Remove` is legal during enumeration on .NET Core 3.0+) |
| `DR003` | review-only: "Unknown code" is partly recognised by method names (`Run*`, `Handle*`, `On*`), and whether it re-enters needs the callee |
| `DR005` | review-only: Blocking is harmless where no synchronization context captures the continuation (console apps, Dispose bridges); not decidable at the call |
| `DR006` | review-only: Whether Dispose can run before a later Cancel depends on call order across threads |
| `DR008` | review-only: A double initialisation is often harmless (idempotent, immutable value) |
| `DR011` | review-only: Whether the publisher outlives the subscriber depends on lifetimes the code does not state |
| `DR016` | review-only: An in-place overwrite matters only for files read back; the temp-path exemption is name-based |
| `DR017` | review-only: Serialized types are partly recognised by name (`*Config*`), and XmlSerializer and DataContract store enum names, not numbers |
| `DR018` | review-only: "Goes to data" is recognised by names (file, path, key, ...) |
| `DR019` | review-only: Config values are recognised by names (`*Config*`, `*Settings*`) |
| `DR020` | review-only: `DateTime.Now` is right for local-time schedules and display; only elapsed-time arithmetic is wrong, which needs intent |
| `DR021` | review-only: Whether a parameter carries outside input depends on the callers |
| `DR022` | review-only: Money and counts are recognised by names |
| `DR024` | review-only: Whether a logging condition stays true pass after pass needs judgement; guards are recognised by names |
| `DR025` | review-only: A collection over a finite key space is bounded by design |
| `DR027` | review-only: "Should be continue" is a guess about intent |
| `DR028` | review-only: Whether the add can throw depends on the callee |
| `DR029` | review-only: `#if DEBUG` behaviour can be intended (asserts, diagnostics); the logging-only test is a text heuristic |
| `DR031` | review-only: Whether a reflected member can disappear depends on who owns it; the BindingFlags part is exact but rare |
| `DR033` | review-only: Whether the called code can throw, and whether the host guards the callback, needs the callee and the host |
| `DR034` | review-only: A static re-assigned on every load is not stale, resets done by other types are not seen, and resets are recognised by method names |
| `DR035` | review-only: No code that the call runs inline is proven (other triggers, a polled token, a plain await that may resume on a context, flows through delegates or other types), so whether it re-enters needs judgement |
| `DR036` | review-only: Behind a guard, in a lambda or through an override, whether the work runs in steady state needs judgement (a throttle and a null check look the same) |

## Rules that replace bans

Where a ban in `BannedSymbols.txt` (RS0030) also blocked the safe use of an API, the ban was replaced by a counted
DeepRules rule that allows only that use. Details in the DeepRules README and `checks/core/security.md`.

| rule | replaces | allows only |
|---|---|---|
| `DR037` | the RS0030 ban on every `HttpClient` constructor (the user, 2026-11-17: allowed "after knowing where it goes and that it only goes to whitelisted locations... starting with discord") | one `HttpClient` in a static readonly field, and requests whose host is constant text in the code and on `deep_review.http_allowed_hosts` |
| `DR038` | the RS0030 bans on `Process.Start(string)` and `Process.Start(ProcessStartInfo)` (the user, 2026-11-17: "add an exception to the banned list. if the address opened only shows local pages") | opening a fixed local web page: an http(s) URL whose scheme and host (`127.0.0.1`, `localhost`, `[::1]`) are constant text in the code, with no Arguments and no verb but "open". Opening a fixed local page is safe, as the old ban text already said for fixed links. The other `Process.Start` overloads stay banned |
| `DR039` | the RS0030 ban on `System.Security.Cryptography.SHA1` and every type derived from it (the user, 2026-11-21: "SHA handshakes are an exception") | the WebSocket handshake answer of RFC 6455 section 4.2.2 only: `Convert.ToBase64String` of the SHA-1 of the `Sec-WebSocket-Key` header value of the request plus the exact GUID `258EAFA5-E914-47DA-95CA-C5AB0DC85B11` (`SHA1.HashData(...)`, or `SHA1.Create()` and `ComputeHash`), whose result only becomes the `Sec-WebSocket-Accept` header value. At that one call DR039 is silent and CA5350, S4790 and SCS0006 are dropped by a DiagnosticSuppressor (listed as suppressed diagnostics); they stay on everywhere else, and any other SHA-1 use, a lookalike with other input or another destination, `SHA1Managed` and `SHA1CryptoServiceProvider` are DR039 hits |

## Compiler polyfills

MA0182 (unused internal type) stays on. Its results on the types the C# compiler itself needs on netstandard2.0 and net48,
and references without any source naming them, are dropped as used (the user, 2026-11-17: "Fix the tool"): `System.Runtime.CompilerServices.IsExternalInit`, `System.Runtime.CompilerServices.RequiredMemberAttribute`, `System.Runtime.CompilerServices.CompilerFeatureRequiredAttribute`, `System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute`.
A type counts only by its exact full name, declared `internal` at the top level of that namespace, with the real type's
shape and nothing more (an empty body, or for CompilerFeatureRequiredAttribute only its string constructor, FeatureName,
IsOptional and the RefStructs/RequiredMembers constants; only AttributeUsage/EditorBrowsable attributes; `: Attribute` for
the attributes). A lookalike in another namespace, a public or partial one, or one with extra members keeps its MA0182.
Dropped results are counted in `dropped.compilerPolyfills` and listed in `compilerPolyfills[]`, never as suppressions.
See `checks/core/runtime-differences.md`.

## Host test runs

.NET Framework 4.8 tests are built in the SDK container and run on this PC, headless (the user's decision,
2026-11-16: "On this PC, headless"). See README.md, "net48 tests".

## Script whitelist

Mods and PB scripts are checked against the game's own whitelist, built and applied by the game's code from the
resolved DedicatedServer64 (tools/Whitelist). Nothing in it is configured here: every prohibited type or member is an
issue (category `whitelist`). See README.md, "Script whitelist".
