# Pack: dotnet-library (.NET libraries for other people's code)

**Loads when** gate.json's `projectKinds` contains `dotnet` and the repository ships a library that code outside it compiles against: a NuGet package (`<IsPackable>`, `<PackageId>`, `dotnet pack`), a shared assembly other repositories reference, or a source generator. The user's libraries: SimpleDiscordDotNet (a dependency-free Discord bot SDK, BCL only, used by Orion), SimpleORMDotNet (a dependency-free, source-generated ORM for Native AOT), and GridGenerator's library. A repository with an app and a library loads [dotnet-app](../dotnet-app/README.md) too. Decide in P0, and write the reason.

**What it adds:** the public API as a contract (binary and source compatibility, versioning, analyzers that track it), API design (nullability, async, cancellation, exceptions, thread safety, callbacks, disposal, dependencies, logging), and packaging (NuGet metadata, target frameworks, trimming and AOT compatibility, SourceLink, source generators). A library's defects reach every caller, and its mistakes can't be fixed without breaking someone, so the bar is higher than for an app.

**Stage 1:** the built-in `code-review` at high effort plus `security-review` ([Report.md](../../Report.md#the-stage-1-record)).

## Pass map

| Pass | Files |
|---|---|
| P0 | the last released version (the API baseline), target frameworks, consumers named in MasterProjects.json (Orion uses SimpleDiscordDotNet) |
| P2 | entry points: every public and protected member, every extension method, every interface a caller implements, generator entry points |
| P3 | [api-design.md](api-design.md#async-apis), [api-design.md](api-design.md#thread-safety-and-callbacks) |
| P4 | [packaging.md](packaging.md#target-frameworks), core runtime-differences |
| P5 | [api-design.md](api-design.md#disposal-and-ownership) |
| P6 | [api-design.md](api-design.md#dependencies) (the contracts of what it depends on) |
| P7 | [api-design.md](api-design.md#exceptions) |
| P8 | [api-compat.md](api-compat.md#data-contracts) (persisted and wire formats) |
| P9 | [api-design.md](api-design.md#arguments) |
| P10 | [packaging.md](packaging.md#supply-chain), [api-design.md](api-design.md#dependencies) |
| P11 | [api-design.md](api-design.md#thread-safety-and-callbacks), [api-compat.md](api-compat.md) (consumers on other versions) |
| P12 | [api-design.md](api-design.md#performance-of-public-apis) |
| P13 | [api-compat.md](api-compat.md) (breaking changes are correctness for callers) |
| P14 | [packaging.md](packaging.md), [api-compat.md](api-compat.md#versioning) |
| P15 | [packaging.md](packaging.md#tests) |
| P16 | Lessons rows with Pack `core` |

## Skills

No se-dev skill covers general libraries. For protocol libraries (Discord, a database), read the protocol's own documentation and quote it; for source generators, the Roslyn source.

## Runtime tags

One tag per target framework (`netstandard2.0`, `net8.0`, `net10.0`), plus `Published (trimmed/AOT)` for AOT consumers, and `All`.
