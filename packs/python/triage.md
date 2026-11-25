# Python tool triage: ruff, mypy, bandit, pytest

Pass P1. deep_tools runs ruff with every rule (`deep-ruff.toml`), mypy `--strict` (`deep-mypy.ini`) and bandit with every test (`deep-bandit.yaml`), from the central configs in `tools\analyzers`, never the repository's own configs. Inline suppressions are issues: ruff runs with `--ignore-noqa` and bandit with `--ignore-nosec`, so findings hidden by `# noqa` or `# nosec` are still reported, and each `# type: ignore` is listed. A missing mypy stub, a Python project with no tests, and a checker that failed are gaps.

Every lead in scope gets a verdict (`real`, `intended`, `not a problem`, `unable to verify`) with the line that proves it. A rule that is wrong for every project is turned off in the central config with a `# reason:`, which the tools own. It is never silenced with an inline comment in the project.

## ruff

Read these families one lead at a time; they find real defects most often:
- **`S`** (the bandit rules inside ruff): subprocess, shell, temp files, hard-coded passwords. They overlap bandit; one verdict covers both reports.
- **`B`** (bugbear): mutable defaults (B006), closures over loop variables (B023), `raise` without `from` inside `except` (B904), `zip` without `strict=` (B905), `assert False` (B011).
- **`BLE`** (blind `except`) and **`TRY`** (exception handling shape).
- **`DTZ`** (naive datetimes: `utcnow`, `now()` without a zone), **`PLW1514`** (`open` without an encoding), **`ASYNC`** (blocking calls in coroutines), **`PTH`** (string paths where `pathlib` avoids bugs).
- **`ERA`** (commented-out code): dead code goes (MasterRules 19).
- **`T20`** (`print`): in a CLI, printing is the interface (`intended`, with the reason); in a library, a finding.

These can be grouped by rule, with one verdict per rule and file: **`D`** (docstrings), **`ANN`** (annotations), **`FBT`** (boolean positional arguments), **`C90`** and **`PLR`** complexity and size rules, **`PLR2004`** (magic values), and formatting rules.

## mypy --strict

- **Missing stubs are gaps:** install the stubs or use a typed dependency. `ignore_missing_imports` in the project would be a suppression.
- **`Any` spreads quietly** from `json.loads`, subprocess output and untyped libraries. Give each parsed record a `TypedDict` or dataclass and validate it where it enters ([checks.md](checks.md#p8-files-encoding-and-json)).
- **Optional values:** every `X | None` is narrowed before use.
- **`cast()`** is a claim mypy doesn't check. Each one gets a verdict.
- **`# type: ignore[code]`** is a suppression, and the gate counts it.

## bandit

- **The usual real ones:** B102 `exec`, B103 permissive `chmod`, B104 binding all interfaces, B108 hard-coded temp paths, B113 requests without a timeout, B301 pickle, B306 `mktemp`, B310 `urlopen` with unchecked schemes, B311 `random` for security, B314/B405 XML parsers, B324 weak hashes, B501 `verify=False`, B506 `yaml.load`, B602/B605 shell, B608 SQL built from strings.
- **B101 `assert`:** real wherever the assert guards behaviour at run time; `not a problem` in tests.
- **B105-B107 hard-coded passwords:** often fire on names like `token_url`. Read the value: a real secret is a blocker, a name is `not a problem` with the line.
- **B603 (subprocess without a shell)** fires on every subprocess call. The verdict depends on whether any argument comes from input: trace it.
- **B607 (partial executable path):** real wherever the working folder or `PATH` isn't trusted ([checks.md](checks.md#p10-security)).

## pytest

- **Failures** are findings until explained; a flaky test is a finding ([core tests](../../checks/core/tests.md), timing asserts).
- **Counts:** compare collected tests with the base run. Tests removed, skipped or marked `xfail` in the diff are findings unless the change explains them.
- **Warnings** in the run (`DeprecationWarning`, `ResourceWarning` for unclosed files, `EncodingWarning`) are leads.
