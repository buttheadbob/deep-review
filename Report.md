# Report contract

The review writes two files into its output folder (default `~/.se-dev/deep-reviews/<repo>-<head12>/`):
- `deep-review.md`, for people;
- `deep-review.json`, for PluginCheck, Flow, Send and the master's Outbox gate.

Both must agree. A review that can't fill the required parts is written anyway, with `complete: false` and the reason.

This file also defines the **stage-1 record**, `stage1.json`, that the runner writes from the stage-1 skills ([below](#the-stage-1-record)). A machine slot or an Outbox code item needs three fresh, clean records with the same nonce: `gate.json` ([tools/GATE-RECORD.md](tools/GATE-RECORD.md)), `stage1.json` and `deep-review.json` (MasterRules 29a and the Outbox gate).

## Severity

| Severity | Meaning |
|---|---|
| Blocking | Breaks install or load, loses data, crashes the host, or is a security hole. Must be fixed before any hub PR, release or Outbox item. |
| High | Wrong behaviour that users (players, admins, operators, callers) will meet in normal use, or a crash path in a common case. |
| Medium | Wrong behaviour in a realistic but less common case, a runtime-specific failure, or a misleading setting that causes harm. |
| Low | Real defect with small impact or a contrived trigger. |
| Minor | Small but real: wrong text, a log that misleads, a needless per-frame cost, a missing guard that can't fire today. |
| Note | Not a defect: a disclosure, a release-note item, a maintainability point a reviewer will raise. |

Say why the severity fits, in one line. Runtime-specific defects are at least Medium if the runtime is one the project ships on.

**Documentation is not a fix.** A finding closed by documenting the limitation stays a finding at the severity of the remaining behaviour. Do not remove it from `findings`, do not change it to `not present`, and do not downgrade it to a clean Note. Its `fix` says what remains and its `forHuman` entry says who accepted it. Only an explicit user decision can close a behavioural finding that way.

**Runtime scope tags:** `Legacy (net48)`, `Interim (net10)`, `Torch`, `Pulsar CLR/CoreCLR/Mono`, `Quasar`, `All`, as before, plus the tags each loaded pack's README lists (for example `Windows`, `Linux`, `PS 5.1`, `PS 7`, `Python 3.12`, `Dedicated server`, `Browser (WebAssembly)`, `Safari`).

## deep-review.md

1. **Scope:** repository, head (and base) SHA, review type, project kind(s) from gate.json, the packs loaded and why each was loaded (and, for `dotnet`, app or library and why), targets × runtimes, host and loader versions read, skills used (and any `not run`), tool versions, the gate's verdict and the path of `gate.json`, and time spent.
2. **Summary:** blockers and the material risks, in a few lines. Never write "approved", "safe", "secure" or "LGTM". The strongest conclusion allowed is "No agent-detected blockers".
3. **Findings**, most severe first. Each one has:
   - id, severity and the reason for it, category, and runtime scope (the tags above);
   - location: `file:line` at the reviewed commit (a GitHub link when the repo is public);
   - trigger: the input or state that causes it;
   - causal path: from the trigger to the consequence, through named members;
   - impact: what a user, admin, operator or caller experiences;
   - evidence: the code read, the tool rule, the reproduction (command plus output per runtime), or `unable to verify` with what was tried;
   - fix: a concrete change (code where useful), and the tradeoff;
   - lesson: the Lessons.md id if it matches, or "new: add to Lessons.md".
4. **Leads with verdicts:** every in-scope tool diagnostic, grouped by rule, with its verdict and proving line. EXISTING and out-of-scope leads are summarized by rule, with counts.
5. **Ledger:** pass, then member or area, then the check's source file, then result, then evidence. This can go in the JSON only, if the markdown would run very long, but the markdown must then give the counts per pass and per pack.
6. **Lessons sweep:** the counts, plus every `present` row.
7. **Tests and runtimes:** what ran where, base-fails/head-passes results, and coverage of changed members.
8. **Not checked:** every gap, from the tools, the passes, the packs (a language with no pack) and the live tests.
9. **For a human:** the exact files and decisions a person must look at.

## deep-review.json

```json
{
  "schema": 1,
  "nonce": "<the nonce from the request, unchanged>",
  "complete": true,
  "incompleteReason": null,
  "scope": {
    "repo": "owner/name", "repoPath": "<absolute path, as gate.json repo>",
    "head": "<sha or WORKTREE@<sha>[+dirty]>", "headTree": "<as gate.json>", "contentSha256": "<WORKTREE only, as gate.json>",
    "base": "<sha or null>", "type": "version_bump|new_registration|working_tree",
    "projectKind": "magnetar-plugin", "projectKinds": ["magnetar-plugin", "quasar-plugin"],
    "packs": ["se-plugin", "se-plugin/magnetar", "se-plugin/quasar", "dotnet-app", "powershell"],
    "targets": [{"name": "ServerPlugin", "loader": "Magnetar", "runtimes": ["net48", "net10"]}],
    "loaderVersions": {}, "skills": [{"name": "se-dev-server-code", "status": "used|not run", "why": ""}]
  },
  "tools": {"report": "<path to tools-report.json>", "gateRecord": "<path to gate.json>", "gaps": []},
  "findings": [{
    "id": "F1", "severity": "Medium", "severityReason": "", "category": "concurrency",
    "runtimeScope": ["Legacy (net48)"], "title": "",
    "locations": [{"file": "Shared/Config/AutoCommand.cs", "line": 361, "endLine": 372}],
    "trigger": "", "causalPath": "", "impact": "",
    "evidence": {"read": ["file:line"], "toolRules": ["DR001"], "reproduction": {"harness": "<path>", "results": {"net48": "throws InvalidOperationException", "net10": "passes"}}, "label": "verified|unable to verify"},
    "fix": "", "tradeoff": "", "lesson": "L01|new"
  }],
  "leads": [{"tool": "DeepRules", "rule": "DR001", "file": "", "line": 0, "scope": "in-diff|changed-member|affected-member|elsewhere", "age": "NEW|EXISTING|FIXED", "verdict": "real|intended|not a problem|unable to verify", "reason": "", "finding": "F1|null"}],
  "ledger": [{"pass": "P3", "member": "AutoCommand.Cancel()", "check": "lock re-entrancy", "source": "checks/core/concurrency.md", "result": "finding F1|not present|unable to verify", "evidence": "file:line"}],
  "lessons": [{"id": "L01", "status": "present|not present|not applicable", "finding": "F1|null", "why": ""}],
  "tests": {"byRuntime": {"net48": {"ran": true, "passed": 0, "failed": 0}, "net10": {}}, "baseFailsHeadPasses": [], "uncoveredChangedMembers": []},
  "notChecked": [""],
  "forHuman": [""]
}
```

**What changed from the plugin-only skill, and why the schema stays 1:** `nonce` is written at the top level, where the consumer rule below has always needed it. `scope` gains `repoPath`, `headTree` and `contentSha256` (so the head can be checked the way gate.json's is), `projectKind`, `projectKinds` and `packs`; `tools` gains `gateRecord`; ledger rows gain `source`. These are added fields only: nothing was renamed or removed, and no field's meaning changed, so consumers that ignore unknown fields keep working.

`complete` is true only when all of these hold:
- every pass P0 to P18 has at least one ledger row;
- every member on the P2 list has rows from P3 to P13, from the core file and from every loaded pack that adds checks to that pass;
- every in-scope lead has a verdict;
- every lesson row is filled (rows from packs not loaded are `not applicable`);
- every Medium+ finding has a reproduction or an `unable to verify` label.

The consumers treat a result as clean only when **all** of these hold. The consumers are:
- the PluginCheck session's lease check;
- Flow's slot gate;
- Send's hub-pin check;
- the master's Outbox gate (code items only).

The conditions (the user's rule: "no errors, no warnings, no issues"):
- `complete: true`;
- no finding at any severity, Note included;
- no lead without a verdict, and no lead whose verdict is `real` or `unable to verify`;
- no gap in `notChecked`;
- the `head` is the commit being run or pinned, checked by the same rules as gate.json's head ([tools/GATE-RECORD.md](tools/GATE-RECORD.md), consumer check 8);
- the `nonce` matches the request (see [tools/GATE-RECORD.md](tools/GATE-RECORD.md), consumer checks 3 and 4).

A Note that is only a release-note item must be handled, by writing it in the changelog, before the result can be clean.

<a id="the-stage-1-record"></a>
## The stage-1 record

Stage 1 is the first-stage review this skill builds on. Its machine-readable record lets a consumer check it the way it checks the gate.

### Which stage-1 skills, by project kind

| Project | Stage-1 skills | How they are run |
|---|---|---|
| Hub plugins (`projectKinds` contains `magnetar-plugin`, `pulsar-plugin` or `quasar-plugin`) **with an open hub PR for this head** | `se-dev-plugin-review` | on the hub PR, **preview only**: the runner never replies `confirm` and never posts. Its `review.json` and `comment.preview.md` are the output. |
| Everything else: hub plugins with no hub PR yet, Torch-only plugins, mods, PB scripts, .NET apps and libraries, Python, PowerShell, web | the built-in `code-review` at **high** effort, plus the built-in `security-review` | on the exact head, against the base where there is one. No `--comment`, no `--fix`, no posting. |

`se-dev-plugin-review` derives its range from a hub PR, so a hub plugin with no PR yet uses the second row (the user, 2026-11-16: "Code + security review"). Once the hub PR exists, the hub submission also needs `se-dev-plugin-review` on that PR (the full-review rule for hub submissions).

### Who writes it

The runner writes `stage1.json` from what those skills report: PluginCheck when it serves a lease request, or Flow's own agent in Flow's slot gate. A session never writes its own stage-1 record for a lease. The runner copies every finding the skills report, with the skill's own severity word, and every check a skill says it couldn't make.

### Schema

```json
{
  "schema": "review-stage1/1",
  "nonce": "<the nonce issued for this request, unchanged>",
  "repo": "<absolute path of the reviewed repository, as gate.json>",
  "head": "<full lowercase 40-hex sha, or WORKTREE@<HEAD sha>[+dirty]>",
  "headTree": "<as gate.json: git rev-parse <sha>^{tree} for a commit, else the same label as head>",
  "contentSha256": "<WORKTREE only: as gate.json>",
  "skills": ["code-review", "security-review"],
  "skillArgs": {"code-review": "high"},
  "complete": true,
  "incompleteReason": null,
  "findings": [{"skill": "code-review", "severity": "high", "file": "src/Foo.cs", "line": 42, "text": ""}],
  "notChecked": [""],
  "forHuman": [""],
  "startedUtc": "YYYY-MM-DDTHH:MM:SSZ",
  "finishedUtc": "YYYY-MM-DDTHH:MM:SSZ"
}
```

| Field | Meaning |
|---|---|
| `schema` | `"review-stage1/1"` |
| `nonce` | the nonce issued for this request, unchanged; the same one the gate and stage 2 use |
| `repo`, `head` | the same rules as gate.json: an absolute path; a full lowercase SHA, or `WORKTREE@<sha>` with `+dirty` when the tree differs from HEAD. `DIR` never qualifies |
| `headTree`, `contentSha256` | what gate.json's head rule checks against: the tree SHA for a commit, the content hash (GATE-RECORD.md algorithm) for a working tree |
| `skills` | the names of the skills that ran, exactly: `se-dev-plugin-review`, or `code-review` and `security-review` |
| `skillArgs` | the arguments each skill ran with: `code-review` must be `high`; `se-dev-plugin-review` is the hub PR URL, previewed only |
| `complete` | true only when every skill the project kind requires ran to its end on this head, and the runner read its whole output |
| `incompleteReason` | why not, when `complete` is false |
| `findings[]` | every finding any skill reported, at any severity: `skill`, `severity` (the skill's own word), `file`, `line` (0 when none), `text` |
| `notChecked[]` | every check a skill reported it could not make: verification gaps, unaccounted paths, unresolved provenance, unevaluated imports, skipped files |
| `forHuman[]` | optional: se-dev-plugin-review's "Manual review required" checklist. It is a list for the user, not a gap, and doesn't affect cleanliness |
| `startedUtc`, `finishedUtc` | ISO 8601 UTC, as gate.json |

Schema `review-stage1/1` follows the same rule as gate.json: a later version may add optional fields; renaming or removing one, or changing its meaning, is `review-stage1/2`, announced to every consumer first. Consumers ignore fields they don't know.

### Clean only when

A consumer treats `stage1.json` as clean only when **all** of these hold:
- it parses as UTF-8 JSON with `schema == "review-stage1/1"`;
- `complete == true`, and `skills` contains every skill the project kind requires (the table above), with `skillArgs` showing `code-review` at `high` where it is required;
- no finding at any severity: `findings == []`;
- nothing not checked: `notChecked == []`;
- the head matches what will be run, by gate.json's consumer check 8, and `repo` matches by its check 7;
- the nonce matches the request, is used once, and the times pass gate.json's check 4 (`startedUtc` at or after the nonce was issued, `finishedUtc` after `startedUtc` and not in the future).

On any failure, the consumer refuses and returns the reason to the requester.
