# Pack: python

**Loads when** gate.json's `languages` has Python with `files` above 0, whatever the `projectKind`: PluginCheck and deep_tools are Python, and plugin repositories carry Python helper scripts too. Its checks apply to the Python members on the P2 list.

**What it adds:** subprocess use and shell injection, executable resolution on Windows, Windows paths and containment checks, file and console encodings, JSON records read by other consumers, exceptions and exit codes, threads, asyncio, multiprocessing and child processes, files shared with other sessions, typing, tests, and how to give verdicts to ruff, mypy, bandit and pytest results.

**Stage 1:** the built-in `code-review` at high effort plus `security-review` ([Report.md](../../Report.md#the-stage-1-record)), unless the Python is part of a hub plugin.

## Pass map

All checks are in [checks.md](checks.md), in sections by pass; tool triage is in [triage.md](triage.md).

| Pass | Section |
|---|---|
| P0 | Python versions (`requires-python`, the interpreters that run it), OSes, whether it runs untrusted repositories |
| P1 | [triage.md](triage.md) |
| P2 | entry points: `main()` and `__main__` blocks, CLI subcommands, functions other tools import, thread and task targets, signal and `atexit` handlers |
| P3 | [Concurrency](checks.md#p3-concurrency) |
| P4 | [Versions and operating systems](checks.md#p4-versions-and-operating-systems) |
| P5 | [Start, exit and cleanup](checks.md#p5-start-exit-and-cleanup) |
| P6 | [Standard library and OS](checks.md#p6-standard-library-and-os) |
| P7 | [Exceptions and exit codes](checks.md#p7-exceptions-and-exit-codes) |
| P8 | [Files, encoding and JSON](checks.md#p8-files-encoding-and-json) |
| P9 | [Inputs and paths](checks.md#p9-inputs-and-paths) |
| P10 | [Security](checks.md#p10-security) |
| P11 | [Other processes and sessions](checks.md#p11-other-processes-and-sessions) |
| P12 | [Performance](checks.md#p12-performance) |
| P13 | [Language traps](checks.md#p13-language-traps) |
| P14 | [Docs and release](checks.md#p14-docs-and-release) |
| P15 | [Tests](checks.md#p15-tests) |
| P16 | Lessons rows with Pack `core` |
| P18 | a pytest file or plain script in a temp copy, run with `python -I -B`, pinned to the tool cores |

## Runtime tags

`Python 3.x` per version in the P0 list, `Windows`, `Linux`, `All`.
