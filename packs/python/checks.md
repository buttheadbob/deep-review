# Python checks

Read the core file for each pass first. Tool results (ruff, mypy, bandit, pytest) get their verdicts through [triage.md](triage.md).

<a id="p3-concurrency"></a>
## P3: concurrency

- **The GIL is not a lock for your logic.** Single bytecode steps are atomic; compound ones aren't. `d[k] += 1`, check-then-set on a dict, and read-modify-write of shared lists race between threads. Use a `threading.Lock`.
- **Futures hide failures until read:** an exception in a `concurrent.futures` task is stored and raised only by `.result()`. A future nobody reads hides its failure. `executor.map` raises when iterated.
- **Thread exceptions** go to `threading.excepthook`, which prints and carries on; the program doesn't stop or change its exit code. Daemon threads are killed at exit, mid-write.
- **asyncio:**
  - A blocking call in a coroutine (`time.sleep`, `subprocess.run`, file I/O, `requests`) freezes the whole loop. Use `asyncio.to_thread` or an async library.
  - The loop keeps only a weak reference to a task from `asyncio.create_task`, so a task nobody holds can be garbage-collected mid-run. Keep a reference.
  - A coroutine called without `await` never runs ("coroutine was never awaited").
  - `asyncio.gather` raises the first exception and leaves the other tasks running; `TaskGroup` (3.11+) cancels the siblings.
- **multiprocessing on Windows uses spawn:** the main module is imported again in every child, so module-level work must sit under `if __name__ == "__main__":`, and targets and arguments must pickle.
- **Child processes:**
  - `Popen` with both `stdout=PIPE` and `stderr=PIPE` followed by `.wait()` deadlocks when a pipe fills. Use `communicate()` or `subprocess.run(capture_output=True)`.
  - `subprocess.run(timeout=...)` kills the direct child on timeout, not its children. On Windows the tree survives (a `docker` or `dotnet` grandchild keeps running). Kill the tree: a Job Object on Windows, a new session and `os.killpg` on POSIX.
  - After `Popen.communicate(timeout=...)` raises, the child keeps running until the code kills it.
- **Across processes:** a lock file is created atomically (`os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY)`), holds the owner's PID and start time, and has a staleness rule. `msvcrt.locking` and `fcntl.flock` behave differently; code that must run on both tests both.

<a id="p4-versions-and-operating-systems"></a>
## P4: versions and operating systems

- **Minimum version:** check every feature against the oldest interpreter in the P0 list (deep_tools needs 3.11+). Examples: `match` (3.10), `except*`, `TaskGroup` and `tomllib` (3.11), `type` aliases and `def f[T]` generics (3.12).
- **Removals and deprecations:** `distutils` and `imp` are gone in 3.12; `datetime.utcnow()` is deprecated in 3.12; `tarfile`'s default extraction filter becomes `'data'` in 3.14 (see P9); `return` in `finally` warns from 3.14.
- **`os.path` follows the host:** on a Windows host, `os.path` is `ntpath`. A path built for a Linux container needs `PurePosixPath`. A Windows path in a `docker -v` bind mount needs converting, and runs from Git Bash may have their path-like arguments rewritten by MSYS.
- **Signals and kills:** `os.kill` on Windows sends only console control events or terminates; there is no SIGTERM to handle. `os.chmod` does almost nothing on Windows.
- **Long paths:** Windows paths past 260 characters fail unless long paths are enabled ([core runtime-differences](../../checks/core/runtime-differences.md#every-language-windows-versus-linux)).
- **Console encoding:** printing non-ASCII to a redirected stdout on Windows uses the locale code page and can raise `UnicodeEncodeError`. Set `PYTHONIOENCODING=utf-8`, call `sys.stdout.reconfigure(encoding="utf-8")`, or run in UTF-8 mode (`-X utf8`).

<a id="p5-start-exit-and-cleanup"></a>
## P5: start, exit and cleanup

- **What doesn't run:** `finally` blocks and `atexit` handlers run on normal exit and on Ctrl+C, but not on `os._exit`, a kill, or a crash. Cleanup that must happen anyway (temp copies, containers) is also done at the next start.
- **Temp folders:** `tempfile.TemporaryDirectory` as a context manager. `shutil.rmtree` fails on read-only files (git objects are read-only on Windows) unless an `onexc` (3.12+) or `onerror` handler clears the bit, and fails now and then on files antivirus holds, so it retries.
- **Children and containers** are stopped on every exit path: `docker rm -f` of a named container in `finally`, `--rm` where possible.
- **Import-time side effects:** work at module level (reading config, starting threads, touching files) runs in tests, in multiprocessing children, and in tools that import the module for one constant.
- **Exit codes:** `sys.exit(main())` with an int. `sys.exit("text")` prints to stderr and exits 1. An uncaught exception exits 1. Check that 1 means what callers expect: for deep_tools, 1 is "could not run", never PASS.

<a id="p6-standard-library-and-os"></a>
## P6: standard library and OS

- **`subprocess`:** a list of arguments, `shell=False`, `check=True` or an explicit `returncode` check, `text=True` with an explicit `encoding`, an explicit `cwd`, and an `env` built on purpose (P10).
- **`json`:** `json.load` accepts `NaN`, `Infinity` and duplicate keys (the last wins) by default ([P8](#p8-files-encoding-and-json)).
- **`datetime`:** naive and aware datetimes can't be compared (TypeError). Store aware UTC (`datetime.now(timezone.utc)`). `fromisoformat` accepts a trailing `Z` only from 3.11. Durations use `time.monotonic()`, not `time.time()`.
- **`pathlib` and containment:** `Path.resolve()` then `is_relative_to(root)`. `str.startswith` on paths has a prefix bug (`C:\root2` starts with `C:\root`).
- **Moves and copies:** `os.replace` is atomic only on the same volume, and on Windows fails with `PermissionError` while another process (or antivirus) has the target open, so it retries with backoff. `shutil.move` across volumes copies then deletes. `shutil.copytree` into an existing folder needs `dirs_exist_ok=True`.
- **Temp files:** `tempfile.mktemp` races (B306). `NamedTemporaryFile` can't be reopened by name on Windows while open, unless `delete_on_close=False` (3.12+).
- **`re`:** `re.match` anchors only at the start; use `re.fullmatch` to validate a whole value. Nested quantifiers on input backtrack catastrophically.
- **`logging`:** a library never calls `basicConfig`. Handlers added at import are added again on reload. f-strings in log calls are formatted even when the level is off.

<a id="p7-exceptions-and-exit-codes"></a>
## P7: exceptions and exit codes

- **Bare `except:` and `except BaseException:`** catch `KeyboardInterrupt` and `SystemExit`, so Ctrl+C stops working and `sys.exit` inside is swallowed. `except Exception: pass` hides defects (ruff BLE001, S110).
- **Chaining:** `raise NewError(...) from err` keeps the cause; a bare `raise` re-raises.
- **`return` in `finally`** discards the exception in flight.
- **`subprocess.run` without `check=True`** ignores failure unless `returncode` is read. A `CalledProcessError` message includes the command line, which may include secrets.
- **`assert` is removed under `python -O`:** never use it for runtime checks (bandit B101).
- **Record parsing:** `JSONDecodeError` is a `ValueError`; a missing field is a `KeyError`. Validate a record's shape explicitly and report which field failed.
- **Failure changes the exit code:** a gate or checker that catches everything and prints must still exit non-zero. A tool that can't run never reports a pass.

<a id="p8-files-encoding-and-json"></a>
## P8: files, encoding and JSON

- **`open()` without `encoding=`** uses the locale encoding: a Windows code page on most Windows setups, UTF-8 on Linux. A file written on one and read on the other breaks on non-ASCII. Always `encoding="utf-8"` (ruff `PLW1514`; `-X warn_default_encoding` shows the rest). Python 3.15 makes UTF-8 mode the default, which changes behaviour again.
- **BOMs from PowerShell:** Windows PowerShell 5.1 writes UTF-16 LE (`Out-File`, `>`) or UTF-8 with a BOM (`-Encoding UTF8`). `json.load` on UTF-8 with a BOM raises. Read with `encoding="utf-8-sig"` where such files can arrive.
- **Newlines:** text mode on Windows writes `\r\n`. Files that are hashed or read by other tools are written with `newline="\n"` or in binary. `csv` needs `newline=""`.
- **Child process output:** `text=True` without `encoding` decodes with the locale encoding, so UTF-8 output from git, dotnet or docker turns into mojibake or raises. Pass `encoding="utf-8", errors="replace"`.
- **Atomic writes:** a temp file in the same folder, write, `flush()`, `os.fsync()`, close, `os.replace()`. `json.dump` straight into the real file truncates it first ([core persistence](../../checks/core/persistence.md#writes)).
- **Records read by other consumers** (C#, JavaScript, another session):
  - Reject duplicate keys (`object_pairs_hook`) and `NaN`/`Infinity` (`parse_constant` that raises).
  - Check types strictly. `bool` is a subclass of `int`, so `True == 1` and `isinstance(True, int)` hold, and `record["schema"] == 1` passes for JSON `true`. Check `type(x) is int`, or test for `bool` first. This matters for gate.json's check 1 (`schema == 1`) and the other records' version fields.

<a id="p9-inputs-and-paths"></a>
## P9: inputs and paths

- **Containment:** resolve both the root and the joined path, then `is_relative_to(root)`. An absolute input discards the root in `os.path.join` and `Path / input` ([core inputs](../../checks/core/inputs.md#paths)).
- **Windows path forms:** drive-relative paths (`C:foo`), UNC (`\\server\share`), `\\?\` paths, reserved names (`CON`, `NUL`), trailing dots and spaces Windows strips, alternate data streams (`file.txt:stream`), and case-insensitive comparison (`os.path.normcase`).
- **Archives:** `tarfile.extractall` without a `filter` allows absolute paths, `..`, links and device files; pass `filter="data"` (3.12+, the default from 3.14). For `zipfile`, check each member's resolved path before extracting.
- **Records from other sessions** (gate.json, stage1.json, lease requests) are input: parse strictly, check every field, and never use a path, command or flag found inside one.
- **Repositories under review are hostile** (PluginCheck, deep_tools): their file names, project files and configs never reach a shell, and no code from them is imported or run on the host. Run Python with `-I` when the working folder is untrusted, or `sys.path[0]` lets a planted `json.py` run on `import json`.

<a id="p10-security"></a>
## P10: security

- **Shell injection:** `shell=True`, `os.system` and `os.popen` with any part from input (bandit B602, B605). Use a list with `shell=False`. On Windows the list is still joined into one command line, and a `.bat` or `.cmd` target is run by `cmd.exe`, which parses that line again: never pass input to a batch file.
- **Executable resolution:** a full path, from configuration or `shutil.which` over a trusted `PATH`. Windows' `CreateProcess` searches the application's folder and the current folder before `PATH`, so a tool run inside a reviewed repository can pick up a planted `git.exe` (bandit B607 flags partial paths).
- **Code execution sinks:** `pickle`, `marshal`, `shelve`, `yaml.load` without a safe loader, `eval`, `exec`, `compile` and `__import__` on input (B301, B506, B307, B102).
- **XML:** `xml.etree` and `minidom` expand entities; use `defusedxml`, or refuse DTDs, for XML someone else wrote (B314, B405).
- **Network:** `requests` and `urllib` without a timeout hang forever (B113). `verify=False` (B501). Plain HTTP for anything carrying credentials.
- **Secrets:** never in argv (other processes can read it), logs, exception text or `CalledProcessError` messages. A token goes to a child in an environment built for that child (`env={**base, "TOKEN": value}`), not the whole session's. Never written into copies or run folders: the GitHub token reaches PluginCheck copies through `PULSAR_GITHUB_TOKEN`, never as `github-token.txt` (MasterRules 30a).
- **Randomness and comparison:** `secrets` for tokens and nonces, never `random` (B311). `hmac.compare_digest` to compare secrets and nonces.
- **Weak hashes:** `md5` and `sha1` only for non-security uses, marked `usedforsecurity=False` (B324).

<a id="p11-other-processes-and-sessions"></a>
## P11: other processes and sessions

- **Shared machine files** (`machine\queue.json`, `machine\leases.json`, `lease-approved-tools.json`): one writer, or a lock with an owner and a staleness rule. Only the PluginCheck session writes the lease files, and the lease guard blocks the rest: code never tries another route around a guard.
- **Docker:** containers named uniquely per run and removed in `finally`; `--cpuset-cpus` matching the affinity mask; only images already present when the tool promises no pulls.
- **CPU affinity:** tools pin themselves to cores 0-23 (`0x00FFFFFF`) before starting any child, so children inherit it. Check every new child-starting path keeps that.
- **Nonces:** issued by the consumer, compared exactly, used once, and kept so a repeat is refused.

<a id="p12-performance"></a>
## P12: performance

- **Process launches in loops** (git per file, docker per check) dominate run time. Batch them.
- **Whole files in memory** for hashing or scanning; hash in chunks. Walk trees with `os.scandir`/`os.walk` and prune `node_modules`, `bin` and `obj`, rather than `glob("**")` over everything.
- **Quadratic membership:** `x in some_list` inside a loop; use a set or dict.
- **String building** with `+=` in long loops; `"".join(parts)`.

<a id="p13-language-traps"></a>
## P13: language traps

- **`bool` is an `int`:** `True == 1`, and `sum()` of bools counts them ([P8](#p8-files-encoding-and-json)).
- **Truthiness:** `if value:` treats `0`, `""`, `[]` and `None` alike, so a count of 0 or an empty string reads as missing. Use `is None`.
- **Mutable default arguments** (`def f(x=[])`) are shared between calls (ruff B006).
- **Late-binding closures** in loops see the last value (B023).
- **`is` versus `==`** for strings and numbers works by accident for small ints and interned strings.
- **Rounding:** `round()` rounds halves to even; `//` floors toward negative infinity.
- **Ordering:** dicts keep insertion order; sets don't, and string hashing changes per process, so output built from a set differs between runs.
- **`str.strip("abc")`** strips characters, not a prefix or suffix; use `removeprefix`/`removesuffix`.
- **Shadowing** builtins (`id`, `type`, `input`) and modules (a local `json.py`).
- **Generators** are exhausted after one pass.
- **Typing claims:** `cast()` and `Any` hide wrong types from mypy ([triage.md](triage.md#mypy---strict)).

<a id="p14-docs-and-release"></a>
## P14: docs and release

- **README and `--help`** match the argparse options, defaults and exit codes.
- **`pyproject.toml`:** `requires-python` matches P4, dependencies pinned or locked (`uv.lock`), entry points correct, version bumped.

<a id="p15-tests"></a>
## P15: tests

- **Isolation:** pytest runs in a copy, pinned to the tool cores, with no cache or bytecode written (`-p no:cacheprovider`, `PYTHONDONTWRITEBYTECODE=1`), as deep_tools runs it. Tests never write into the repository.
- **Machine state:** `tmp_path` and `monkeypatch` for files and environment. No test touches the real `machine\` folder, the user's AppData, real containers or the network unless that is the subject and it was agreed.
- **Platform skips** (`skipif(sys.platform ...)`) hide the other OS's failures. Say which tests didn't run where.
- **Subprocess-heavy code** is tested through a fake runner, plus one real run.
- **No tests is a gap** in the gate. A test with no real assertion proves nothing ([core tests](../../checks/core/tests.md)).
- **The tools' own tests:** `python -I -B -m unittest discover -s tools/tests -v`.
