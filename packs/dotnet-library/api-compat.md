# Public API compatibility

Passes P8, P11, P13 and P14. Every public and protected member, and every public type's shape, is a promise to code you can't see. In P0, find the API baseline: the last released version's assembly or `PublicAPI.Shipped.txt`. Compare every change to it.

## Binary breaks (callers fail at run time without recompiling)

- **Removed or renamed** public types and members, including a property turned into a field or the reverse.
- **Changed signatures:** a parameter added (even an optional one: the old signature no longer exists), a parameter type or return type changed, `ref`/`out`/`in` changed, a method made generic.
- **Optional parameter defaults** are compiled into the caller. Changing a default changes nothing for callers until they recompile, then changes their behaviour silently.
- **`const` values** are compiled into the caller too. A value that can change is `static readonly`.
- **Abstract members or interface members added** break every implementer, unless the interface member has a default implementation (which netstandard2.0 and net48 can't use). A note in the PR is not a resolution: record the version/changelog decision and require an explicit accepted break, or add a default/companion interface. (Real: Quasar PR2 added `ShowMessageBoxAsync` to a public dialog interface; it was accepted as an implementer break, so the report stayed a finding until that decision was explicit.)
- **Type kind changes:** class to struct, sealed added, a base class changed, a type moved to another assembly without a type forward.
- **Struct layout:** fields added to a public struct used in interop or `Unsafe` code.

## Source breaks (callers fail when they recompile)

- **Parameter renames** break named arguments.
- **New overloads** can make existing calls ambiguous (a `null` argument, a lambda, `params`).
- **New members on a type** can collide with callers' extension methods of the same name, which then stop being chosen.
- **Nullability annotation changes:** a return that becomes nullable, or a parameter that stops accepting null, gives every caller new warnings, and errors where warnings are errors.
- **Enum changes:** a reordered or removed member changes values callers stored ([core persistence](../../checks/core/persistence.md#formats-and-migration)); a new member breaks callers' exhaustive switches.

## Behaviour breaks (the same signature, a different result)

- **Exceptions:** a different exception type thrown, or an exception where there used to be a result.
- **Thread safety:** a type that was safe to share and no longer is, or the reverse documented wrongly.
- **Callbacks and events:** raised on a different thread, in a different order, or under a lock now ([api-design.md](api-design.md#thread-safety-and-callbacks)).
- **Defaults:** a new default timeout, retry count, encoding or culture changes every caller that relied on the old one.

## Tools that track it

- **Public API analyzers** (`Microsoft.CodeAnalysis.PublicApiAnalyzers`, `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt`): every change to the files is an API change to justify.
- **Package validation** (`EnablePackageValidation` with `PackageValidationBaselineVersion`): compares the package with the last released one, per target framework.
- If the project has neither, the review compares the public surface by reading, and names that as a gap the project could close.

<a id="data-contracts"></a>
## Data contracts (P8)

- **Wire formats:** a library that talks a protocol (Discord's gateway and REST, a database) keeps to it across versions. Field names, casing, enum spellings, null versus missing fields: check each against the protocol's documentation.
- **Formats callers persist:** types callers serialize (options, results, ids) change only in compatible ways. Ids stay as `ulong` or string end to end; a Discord snowflake as `double` or JavaScript number loses digits.

<a id="versioning"></a>
## Versioning (P14)

- **SemVer:** a binary or source break is a major version, a new API a minor, a fix a patch, unless the project's rules say otherwise. The changelog names every break and how to move to the new API.
- **Obsolete before removal:** `[Obsolete]` with a message naming the replacement, kept for at least one release, where the project's rules allow.
- **Namespace moves:** a public type moved to another namespace is a source break for every caller's using declarations. Do it before the first release, or add a forwarding type and an obsolete alias.
- **Component API:** a reusable component's `AdditionalAttributes` is additive; a missing one is a design gap when callers need `id`, `data-*` or `aria-*`. Moving a component into a namespace shared with host internals is a future collision, not a neutral choice.
- **Assembly version:** strong-named libraries change `AssemblyVersion` only on breaking releases, or every consumer's binding breaks.
