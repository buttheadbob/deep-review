# Pack: pb-script (programmable block scripts, MDK2)

**Loads when** gate.json's `projectKinds` contains `pb-script` (a csproj referencing MDK, `Mal.Mdk2*`, or a `*.mdk.ini`).

**What it adds:** the PB compiler and its smaller whitelist (no checker here: the gate reports it as a gap, and the review reads it by hand), the 100,000-character script limit and MDK2's packer, the per-run instruction limit and server PB limiters, per-run allocation, the `Storage` string, the grid terminal API, arguments and IGC from other grids, and live testing. PAM's script (the published PAM MDK2 project) is the main user of this pack.

**Stage 1:** the built-in `code-review` at high effort plus `security-review` ([Report.md](../../Report.md#the-stage-1-record)).

## Pass map

All checks are in [checks.md](checks.md), in sections by pass.

| Pass | Section |
|---|---|
| P0 | the published base the fix starts from (PAM: 947555b), the servers' PB limits, MDK2 settings |
| P2 | entry points: the constructor, `Main(argument, updateSource)` per update type and argument, `Save()`, IGC callbacks |
| P3, P12 | [Instruction limit and per-run cost](checks.md#p3-and-p12-instruction-limit-and-per-run-cost) |
| P4 | [Compile, whitelist and size](checks.md#p4-compile-whitelist-and-size) |
| P5, P8 | [Lifecycle and storage](checks.md#p5-and-p8-lifecycle-and-storage) |
| P6 | [The grid API](checks.md#p6-the-grid-api), [se-plugin game-api](../se-plugin/game-api.md) (entities, timing) |
| P7 | [Errors](checks.md#p7-errors) |
| P9, P11 | [Arguments and IGC](checks.md#p9-arguments-and-igc) |
| P14 | [Release](checks.md#p14-release) |
| P15 | [Tests](checks.md#p15-tests) |
| P16 | Lessons rows with Pack `core` (L13 and L47 came from PB work) |

## Skills

- `se-dev-script` for PB script examples and patterns.
- `se-dev-game-code` (and `se-dev-server-code` for server-side limits) for the Ingame API's real behaviour and the whitelist registration.

## Runtime tags

`PB (server)`, `PB (offline)`, `All`. A PB runs where its grid is simulated: the server in multiplayer, the local game offline.
