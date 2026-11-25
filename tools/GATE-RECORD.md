# Gate record: schema 1 (frozen 2026-11-15)

This is the record the analyser gate writes. A consumer checks it before granting a machine slot, or before anything else that needs proof of a fresh, clean gate run. The consumers are:
- the PluginCheck session's lease check (MasterRules 29a);
- Flow's slot gate in the Blazor rework;
- later, Send's hub-pin check.

Schema 1 is frozen. A later version may add optional fields without a schema change, so consumers must ignore fields they don't know. Renaming or removing a field, or changing what one means, makes it schema 2. The master announces that to every consumer first.

## Producing a record

```
python tools/deep_tools.py --gate --repo <repo> --head <40-hex sha | WORKTREE> [--base <sha>] --nonce <nonce> --out <new empty dir>
```

- **Exit codes:** 0 PASS, 3 FAIL, 1 could not run.
- **Output files:** the record is `<out>/gate.json` (UTF-8 JSON). `<out>/gate.txt` is the short human-readable verdict. The full detail is in `tools-report.json`, `.md` and `.sarif`.
- **Run it yourself.** The consumer should run the gate itself, from its own process, with a nonce it issued. A record handed over by a requester is accepted only when every check below holds.
- **Skip flags:** any `--skip-*` flag (tests, vuln, csproj-build, optional) is a gap in gate mode, so the verdict is FAIL. The flags are recorded in `options`.
- **Records that never qualify:** a `--lint-dir` run (head `DIR`) never qualifies for a slot. Nor does a report-mode run (no `--gate`).

## Fields (all required in schema 1)

| Field | Type | Meaning |
|---|---|---|
| `schema` | int | `1` |
| `kind` | string | `"gate"` |
| `gateMode` | bool | `true` when run with `--gate` |
| `nonce` | string | the `--nonce` value, unchanged |
| `repo` | string | absolute path of the reviewed repository |
| `head` | string | the full lowercase 40-hex commit sha; or `WORKTREE@<HEAD sha>`, with `+dirty` added when the working tree differs from HEAD; or `DIR` |
| `headTree` | string | for a commit, `git rev-parse <sha>^{tree}`; otherwise the same label as `head` |
| `contentSha256` | string | WORKTREE and DIR only: lowercase hex, using the algorithm below |
| `toolsVersion` | string | sha256 over the tools, their central configs and every analyzer/checker that runs. `python deep_tools.py --version` prints the installed value |
| `options` | object | the flags the run used (`skip`: list of skipped stages, `base`, `manifests`, `affinityMask`) |
| `startedUtc`, `finishedUtc` | string | ISO 8601 UTC, `YYYY-MM-DDTHH:MM:SSZ` |
| `fresh` | bool | always `true`: no result was reused |
| `projectKind`, `projectKinds` | string, list | the detected kind(s): `magnetar-plugin`, `torch-plugin`, `quasar-plugin`, `pulsar-plugin`, `se-mod`, `pb-script`, `dotnet`, `non-dotnet` |
| `languages` | object | per language: `files`, `checked` (bool) and `by` (the checkers that ran) |
| `runtimes` | list | what compiled: `target/runtime` and `csproj:<project>/<tfm>` |
| `verdict` | string | `PASS`, `FAIL` or `ERROR` |
| `counts` | object | `issues`, `gaps`, `byCategory`, `byRule`, `byRuntime` |
| `issues` | list | the first 200 issues: `category`, `rule`, `file`, `line`, `runtime`, `text` |
| `gaps` | list | every gap. Each one fails the gate |
| `suppressions` | list | every suppression site in the code under review. Each one is an issue |
| `suppressedDiagnostics` | list | the diagnostics those suppressions hid |
| `notCovered` | list | parts of the tree the gate does not check at all |

## Consumer check

Grant nothing unless **every** line holds:

1. `gate.json` parses as UTF-8 JSON, with `schema == 1`, `kind == "gate"` and `gateMode == true`.
2. When you ran the gate yourself, its exit code was 0.
3. `nonce` equals the nonce you issued for this request. A nonce is single-use: keep the used ones and refuse a repeat.
4. `startedUtc` is at or after the time you issued the nonce, `finishedUtc` is at or after `startedUtc`, and `finishedUtc` is not in the future.
5. `fresh == true`.
6. `toolsVersion` passes one of these two:
   - it equals the output (trimmed) of `python deep_tools.py --version`, run by you at check time; or
   - it is the toolsVersion of Flow's C# port of the gate, and that exact version is listed in the host's local `lease-approved-tools.json`.

   The second rule is the user's decision, 2026-11-15, in the PluginCheck session: "Yes, if you approve". The PluginCheck session approves a Flow version only from a parity record: the same verdict and the same full list of findings as deep_tools on the same commits, the tools' own test fixtures included. Each new Flow version needs its own approval. Only the PluginCheck session writes that file, and the lease guard protects it.

   The file's format is set by the PluginCheck session:
   - It is UTF-8 JSON: `{"schema": "plugincheck-approved-tools/1", "about": ..., "approved": [{"toolsVersion": ..., "producer": "flow-native-gate", "approvedUtc": ..., "approvedBy": ..., "parity": ..., "note": ...}]}`.
   - **Match:** a record matches when its `toolsVersion` equals some `approved[i].toolsVersion` exactly: case-sensitive, no trimming, no prefix match.
   - **Can't be parsed:** a different `schema`, a missing `approved` array, or an entry with no `toolsVersion` string means the file can't be parsed, so the check fails. When the file is missing, only the first rule counts.
   - **Other fields:** ignore them. An approval is withdrawn by removing its entry.
   - **Read it in code:** never name the file in a shell command, because the lease guard blocks those, even reads.
7. `repo` is the repository you will run, comparing normalised absolute paths, case-insensitive on Windows.
8. The head matches what you will run:
   - **A commit:** `head` equals the full sha you will run, and `headTree` equals `git -C <repo> rev-parse <sha>^{tree}`.
   - **A working tree:** `head` is `WORKTREE@<sha>` (optionally `+dirty`) where `<sha>` equals `git -C <repo> rev-parse HEAD`, and `contentSha256` equals your own hash (algorithm below) of the exact tree you will run. Hash it from the copy you run, so it can't change in between.
   - **Anything else, `DIR` included:** refuse.
9. `verdict == "PASS"`, and `counts.issues == 0`, `counts.gaps == 0`, `issues == []`, `gaps == []`, `suppressions == []`, `notCovered == []`, `options.skip == []`.
10. Every entry in `languages` has `checked == true`.

On any failure, refuse and return the reason with `gate.txt` to the requester.

`python deep_tools.py --check-record <gate.json> --nonce <n> --nonce-issued <UTC> --repo <repo> --head <sha|WORKTREE>` is the reference checker: exit 0 means the record passes all ten checks, and 3 means it fails, with the reasons printed. A consumer may call it, or implement the same checks itself (Flow enforces its own rules in its own code).

## contentSha256

```python
import hashlib, os, subprocess

def content_sha256(repo):
    out = subprocess.run(['git', '-C', repo, 'ls-files', '-co', '--exclude-standard', '-z'],
                         capture_output=True, check=True).stdout.decode('utf-8')
    paths = sorted(p for p in out.split('\0') if p and os.path.isfile(os.path.join(repo, p)))
    h = hashlib.sha256()
    for p in paths:                     # git's '/'-separated path, sorted by code point
        h.update(p.encode('utf-8') + b'\0')
        with open(os.path.join(repo, p), 'rb') as f:
            h.update(hashlib.sha256(f.read()).digest())
    return h.hexdigest()
```

- **What it covers:** every tracked file, plus every untracked file that isn't ignored, that exists on disk. Bytes are hashed as they are on disk, so line endings count.
- **No folders are skipped.** A tracked file under `bin/`, `obj/`, `packages/` or similar counts too.

## The review records

The gate is only the first of the three checks. A slot also needs both review skills, run fresh with the same nonce:
- **Stage 2 (deep review):** `deep-review.json`, defined in `../Report.md`, with its consumer rule. That means `complete: true`, no finding at any severity, no open or real lead, no gap, the `head` matches and the `nonce` matches.
- **Stage 1:** its machine-readable record comes with the skill restructure for every project kind. The master will announce it to the consumers. Until then, a consumer can't verify stage 1 mechanically, so it grants nothing that needs it (MasterRules 29a).
