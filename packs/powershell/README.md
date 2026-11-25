# Pack: powershell

**Loads when** gate.json's `languages` has PowerShell with `files` above 0, whatever the `projectKind`: build and test scripts in plugin repositories, the Outbox and Gate scripts (`send.ps1`, `guard.ps1`), `Check-Upstream.ps1`, PSScriptAnalyzer's runner in the tools. Its checks apply to the PowerShell scripts and functions on the P2 list.

**What it adds:** strict mode and error preferences, native exit codes, Windows PowerShell 5.1 versus PowerShell 7 (encodings, syntax, argument passing), output pollution and other language traps, quoting and `Invoke-Expression`, `-LiteralPath`, the .NET current folder, `ExecutionPolicy`, secrets in transcripts and environments, jobs and runspaces, machine state and the desktop, Pester, and how to give verdicts to PSScriptAnalyzer results.

**Stage 1:** the built-in `code-review` at high effort plus `security-review` ([Report.md](../../Report.md#the-stage-1-record)), unless the script is part of a hub plugin.

## Pass map

All checks are in [checks.md](checks.md), in sections by pass; PSScriptAnalyzer triage is in [triage.md](triage.md).

| Pass | Section |
|---|---|
| P0 | which host runs each script (`powershell.exe` 5.1 or `pwsh` 7, the `#Requires` lines, the caller's command line), OSes, elevation |
| P1 | [triage.md](triage.md) |
| P2 | entry points: the script's `param()` block, exported functions, scheduled or hooked invocations, script blocks passed to jobs or `-Parallel` |
| P3 | [Concurrency](checks.md#p3-concurrency) |
| P4 | [Windows PowerShell 5.1 versus PowerShell 7](checks.md#p4-windows-powershell-51-versus-powershell-7) |
| P5 | [Scope, exit and cleanup](checks.md#p5-scope-exit-and-cleanup) |
| P6 | [Cmdlets and the host](checks.md#p6-cmdlets-and-the-host) |
| P7 | [Errors](checks.md#p7-errors) |
| P8 | [Files and encoding](checks.md#p8-files-and-encoding) |
| P9 | [Parameters and paths](checks.md#p9-parameters-and-paths) |
| P10 | [Security](checks.md#p10-security) |
| P11 | [Other sessions and machine state](checks.md#p11-other-sessions-and-machine-state) |
| P12 | [Performance](checks.md#p12-performance) |
| P13 | [Language traps](checks.md#p13-language-traps) |
| P14 | [Help and release](checks.md#p14-help-and-release) |
| P15 | [Tests](checks.md#p15-tests) |
| P16 | Lessons rows with Pack `core` |
| P18 | a Pester test or plain script in a temp folder, run with `pwsh -NoProfile -NonInteractive` (and `powershell.exe` when 5.1 runs it), opening no window |

## Runtime tags

`PS 5.1`, `PS 7`, `Windows`, `Linux`, `All`.
