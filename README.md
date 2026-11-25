# deep-review

Deep, forensic second-stage review for code changes. It runs after the first-stage `code-review` and `security-review` skills, reads the repository's analyser gate, loads the core checks and language packs, walks every pass member by member, reproduces Medium+ findings, and writes `deep-review.md` and `deep-review.json`.

The checklists are portable. The automated gate is Windows-first and expects the toolchain described in [`tools/README.md`](tools/README.md).

## Layout

| Path | Purpose |
|---|---|
| `SKILL.md` | Skill entry point and rules |
| `Passes.md` | The mandatory P0–P18 passes and pack mapping |
| `Report.md` | Report contract, severity, and the JSON schema |
| `Lessons.md` | Defects real reviewers found, and the check that catches each |
| `checks/core/` | Core checklists for every project |
| `packs/` | Host and language packs |
| `tools/` | The analyser gate and helper tools |

## Requirements

- Windows with the .NET 10 SDK and .NET Framework 4.8.
- Python 3.11 or newer.
- Docker Desktop with the images listed in [`tools/README.md`](tools/README.md#prerequisites).
- Network access to nuget.org for restores.
- Node.js for the JS/TS linters.
- Optional per-language tools: `ruff`, `mypy`, `bandit`, `pytest`, PSScriptAnalyzer, `shellcheck`.
- `gitleaks` placed at `tools/bin/gitleaks/gitleaks.exe`. The binary is not committed.

## Prepare the tools

```powershell
# DeepRules analyzer (deployed into tools/bin/analyzers)
Set-Location tools/DeepRules
dotnet build DeepRules.slnx -c Release -m:4
Copy-Item src/DeepRules/bin/Release/netstandard2.0/DeepRules.dll ../bin/analyzers/DeepRules.dll -Force
Set-Location ../..

# JS/TS linters
Set-Location tools/linters/js
npm ci
Set-Location ../../..

# Optional PowerShell rules
Install-Module PSScriptAnalyzer -Scope CurrentUser

# gitleaks
# Install it from https://github.com/gitleaks/gitleaks and put gitleaks.exe in
# tools/bin/gitleaks/. The review never downloads it.
```

`DeepScan` and `DeepWhitelist` are built by `tools/deep_tools.py` into `tools/cache/` on first use. `tools/cache/` and all `bin/`, `obj/`, `node_modules/` folders are ignored by Git.

## Install as a skill

Clone or link this repository into the skills directory your agent reads.

```powershell
# Claude Code
git clone https://github.com/buttheadbob/deep-review.git "$HOME/.claude/skills/deep-review"

# OpenCode and other agents: use the equivalent skills directory, keeping the
# folder name `deep-review`.
```

The repository root is the skill root; do not nest another `deep-review` folder inside it.

## Use

Run the gate first, then load the skill:

```powershell
python tools/deep_tools.py --gate --repo <repo> --head <sha|WORKTREE> --base <sha> --nonce <nonce> --out <dir>
```

Then ask the agent to run `deep-review` on the same head. The skill will read `gate.json`, run the passes, and write its report.

See [`tools/README.md`](tools/README.md) for the full gate, gate-record and tool details.

## License

AGPL-3.0-or-later. See [`LICENSE`](LICENSE) and [`NOTICE`](NOTICE).
