# PowerShell checks

Read the core file for each pass first. PSScriptAnalyzer results get their verdicts through [triage.md](triage.md).

<a id="p7-errors"></a>
## P7: errors

Error handling is where PowerShell scripts fail most, so it comes first.

- **Strict mode:** `Set-StrictMode -Version Latest` at the top. Without it, an undefined variable or a misspelled property reads as `$null` and the script carries on with wrong data.
- **`$ErrorActionPreference = 'Stop'`** at script scope, so cmdlet errors stop the script and reach `try/catch`. Without it, `try/catch` doesn't see non-terminating errors: `Get-Item` on a missing path writes an error and the next line runs.
- **Native commands don't throw.** git, dotnet, docker and python report failure only through `$LASTEXITCODE`. Check it after each one (`if ($LASTEXITCODE -ne 0) { throw ... }`). PowerShell 7.3 and later can turn non-zero exits into errors with `$PSNativeCommandUseErrorActionPreference = $true`; 5.1 can't.
- **stderr in Windows PowerShell 5.1:** redirected stderr from a native command (`2>&1`, or captured) becomes error records, so with `Stop` the first progress line git writes to stderr ends the script. PowerShell 7.2 and later don't do this. Check which host runs the script.
- **`Write-Error` versus `throw`:** `Write-Error` is non-terminating unless the preference is `Stop`. A check function that writes an error and returns has reported success to its caller.
- **`-ErrorAction SilentlyContinue`/`Ignore`** hide failures. Each one needs a reason; `Ignore` doesn't even record the error in `$Error`.
- **`$?`** reflects only the last command.
- **Exit codes:** a script run with `-File` exits with the code given to `exit`, and 1 after an uncaught `throw`. With `-Command`, the exit code reflects only whether the last command succeeded, unless the script calls `exit`. A script other tools call must exit non-zero on failure, and say which code means what.
- **Pipelines keep going:** an error on one item in `ForEach-Object` doesn't stop the rest unless the preference is `Stop`.

<a id="p13-language-traps"></a>
## P13: language traps

- **Output pollution:** every value a function doesn't capture goes to its output. `$list.Add($x)` on an `ArrayList` returns the index, `New-Item` returns the item, and a function meant to return `$true` returns `@(0, $true)`. Discard with `$null = ...` or `[void]`. `return $x` adds `$x` to whatever was already output; it doesn't replace it.
- **Arrays unroll:** a function returning a one-element array returns the element, and an empty array becomes `$null`. Wrap calls in `@(...)` before using `.Count` or indexing.
- **`$null` on the left:** `$x -eq $null` with an array `$x` filters the array instead of testing it. Write `$null -eq $x`.
- **Case-insensitive by default:** `-eq`, `-like`, `-match`, `-contains`, `-replace`, and hashtable keys. Use `-ceq`, `-cmatch` and friends where case matters (Linux paths, tokens).
- **The left operand decides the type:** `"10" -lt 9` compares as strings and is true; `1 -eq "1.0"` is true while `"1.0" -eq 1` is false. Values from `Get-Content`, CSV and environment variables are strings.
- **`-match`** takes a regex (escape input with `[regex]::Escape`) and sets `$Matches`.
- **Strings:** double quotes expand `$var` and `$(...)`; single quotes don't. `"$obj.Name"` expands `$obj` and appends `.Name`; write `"$($obj.Name)"`. A backtick line continuation breaks when a space follows it.
- **Scope:** a variable set in a function or script block doesn't change the caller's unless written as `$script:` (or the code is dot-sourced). Preferences such as `$ErrorActionPreference` are scoped too, so a function can quietly run with a different one.
- **Automatic variables:** assigning to `$input`, `$args`, `$_`, `$Matches`, `$Error`, `$Host`, `$PID` or `$HOME` breaks things (PSAvoidAssignmentToAutomaticVariable). Inside `catch`, `$_` is the error, not the pipeline item.
- **`switch`** runs every matching clause unless one calls `break`, and iterates when given an array.
- **Numbers:** `5/2` is 2.5; `[int]2.5` is 2 and `[int]3.5` is 4 (halves round to even).

<a id="p4-windows-powershell-51-versus-powershell-7"></a>
## P4: Windows PowerShell 5.1 versus PowerShell 7

- **Which one runs:** `powershell.exe` is 5.1 on .NET Framework; `pwsh` is 7 on .NET. Read the caller's command line and the script's `#Requires -Version` and `#Requires -PSEdition` lines. A script both run must work on both.
- **Syntax only in 7:** `&&`, `||`, the ternary `? :`, `??`, `??=`, `?.`, `ForEach-Object -Parallel`, `ConvertFrom-Json -AsHashtable`, `Test-Json`. One such line makes the whole file fail to parse on 5.1.
- **File encodings:** 5.1's `Out-File` and `>` write UTF-16 LE, `Set-Content` writes the ANSI code page, and `-Encoding UTF8` writes a BOM. 7 writes UTF-8 without a BOM everywhere. A file another tool reads gets an explicit encoding; on 5.1, a BOM-less UTF-8 file needs `[IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))`.
- **Script files:** 5.1 reads a script without a BOM in the ANSI code page, so non-ASCII text in a BOM-less UTF-8 script turns to mojibake (PSUseBOMForUnicodeEncodedFile). Keep scripts ASCII, or save them with a BOM when 5.1 runs them.
- **Arguments to native programs:** 5.1, and 7 before 7.3, pass embedded quotes and empty strings wrongly. 7.3+ has `$PSNativeCommandArgumentPassing`. Test the exact arguments that reach the program (a commit message with quotes, JSON on a command line). `--%` stops PowerShell's parsing for the rest of the line.
- **JSON:** `ConvertTo-Json` stops at depth 2 by default on both, silently on 5.1; always pass `-Depth`. 7 turns date-like strings into `DateTime` objects on `ConvertFrom-Json`, 5.1 keeps them as strings, so a round trip can change the text.
- **Cmdlets that differ:** `Get-WmiObject` is gone in 7 (use `Get-CimInstance`). `Invoke-WebRequest` in 5.1 parses HTML with Internet Explorer unless `-UseBasicParsing`, and on old .NET settings may refuse modern TLS.
- **Linux (`pwsh`):** case-sensitive paths, `/` separators, and aliases such as `ls`, `cat` and `sort` run the native commands instead of the cmdlets. Use full cmdlet names.

<a id="p8-files-and-encoding"></a>
## P8: files and encoding

- **Explicit encoding** on every read and write (P4).
- **The .NET current folder isn't `$PWD`:** .NET methods resolve relative paths against `[Environment]::CurrentDirectory`, which `Set-Location` doesn't change. `[IO.File]::WriteAllText('out.json', ...)` after `cd C:\x` writes wherever PowerShell started. Pass full paths to .NET (`Join-Path $PSScriptRoot ...`, or `$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath(...)`).
- **Atomic writes:** write a temp file in the same folder, then `Move-Item -Force` or `[IO.File]::Replace` ([core persistence](../../checks/core/persistence.md#writes)). `Set-Content` on a file another process reads can leave it empty for a moment.
- **`Get-Content`** returns an array of lines with the newlines removed; `-Raw` returns one string. Large files: `[IO.File]::ReadLines` or `-ReadCount`.
- **CSV:** 5.1's `Export-Csv` writes a type header unless `-NoTypeInformation`; `Import-Csv` gives strings.

<a id="p9-parameters-and-paths"></a>
## P9: parameters and paths

- **Validate at the boundary:** `[CmdletBinding()]`, typed `param()` entries, `[Parameter(Mandatory)]`, `[ValidateSet()]`, `[ValidateRange()]`, `[ValidatePattern()]`, `[ValidateScript()]`.
- **Mandatory parameters prompt when missing.** A script run by a tool or an agent runs non-interactive, where a prompt fails or hangs. Give defaults, or throw a clear error.
- **`-Path` expands wildcards** (`*`, `?`, `[`, `]`), so a file named `a[1].txt` is missed or the wrong file is hit. Paths from input always go through `-LiteralPath`.
- **Containment:** `Join-Path` doesn't stop `..`. Resolve with `[IO.Path]::GetFullPath` and compare with the root plus a separator, case-insensitive on Windows ([core inputs](../../checks/core/inputs.md#paths)).
- **Pipeline input:** a function with `ValueFromPipeline` needs a `process {}` block, or it handles only the last item.
- **Defaults aren't bound:** `$PSBoundParameters` holds only the parameters the caller passed, not defaults.
- **Environment variables** (`$env:X`) are strings, and `$null` when unset.

<a id="p10-security"></a>
## P10: security

- **`Invoke-Expression`** (`iex`) on any string built from input is code injection (PSAvoidUsingInvokeExpression). Call with the call operator and an argument array: `& $exe @arguments`.
- **`Start-Process -ArgumentList`** joins its arguments with spaces and doesn't quote them, so an argument with a space splits and input can add arguments. Prefer `& $exe @arguments`, or `[Diagnostics.Process]` with `ArgumentList` in 7.
- **`cmd /c` with input:** cmd's own metacharacters (`&`, `|`, `^`, `%VAR%`) apply on top of PowerShell's.
- **Downloads:** `iwr ... | iex` and scripts fetched at run time need a pinned version and a hash check, or they don't belong ([core security](../../checks/core/security.md#supply-chain)).
- **`ExecutionPolicy` is not a security boundary.** `-ExecutionPolicy Bypass` in a launcher is normal and makes nothing safe. A script never changes the machine's policy: that is a system setting.
- **Secrets:**
  - `ConvertTo-SecureString -AsPlainText` with a literal is a hard-coded secret (PSAvoidUsingConvertToSecureStringWithPlainText). Stored secrets come from SecretManagement or a protected file.
  - `Start-Transcript` records everything printed and typed, tokens included.
  - `Write-Host`, `Write-Verbose` and error text never carry a secret.
  - A secret on a native command line is visible to other processes.
  - `$env:TOKEN = ...` lasts for the whole session and reaches every later child. Set it just around the one call and remove it after, or build the child's environment on its own.
- **Interactive cmdlets** (`Get-Credential`, `Read-Host -AsSecureString`) never appear in automation.
- **System settings** (the registry under `HKLM`, services, scheduled tasks, the firewall, Defender exclusions, machine environment variables) are changed only when that is the script's stated purpose and the user agreed.

<a id="p11-other-sessions-and-machine-state"></a>
## P11: other sessions and machine state

- **Shared machine files** (`machine\queue.json`, the lease files): one writer, following the owning session's rules. Only the PluginCheck session edits `machine/leases.json`, and the lease guard blocks the rest.
- **Guard and gate scripts** (`guard.ps1`, `send.ps1`): every path that writes or sends goes through the guard first. A refusal stops the run; nothing retries by another route.
- **The desktop:** `Start-Process` without `-NoNewWindow` or `-WindowStyle Hidden`, `Invoke-Item`, `explorer.exe` and `cmd /c start` open windows ([core host-api](../../checks/core/host-api.md#the-desktop)).
- **Elevation:** `#Requires -RunAsAdministrator` only where needed, with what it changes stated.

<a id="p5-scope-exit-and-cleanup"></a>
## P5: scope, exit and cleanup

- **`try/finally`** for temp files, `Pop-Location` after `Push-Location`, and restoring preferences and the current folder. `finally` runs on Ctrl+C, but not when the process is killed.
- **Jobs and runspaces** (`Start-Job`, `Start-ThreadJob`, runspace pools, `-Parallel`): results received with `Receive-Job` (that is where their errors are) and jobs removed. A job left running outlives the script in the session.
- **Dot-sourcing** runs a script in the caller's scope, changing its variables, functions, location and preferences. A script meant to be dot-sourced is written for that, and one that isn't says so.
- **Module state** survives between calls in one session: a second run sees the first run's script-scope variables.
- **`exit`** in a dot-sourced script or a profile ends the whole session.

<a id="p3-concurrency"></a>
## P3: concurrency

- **`ForEach-Object -Parallel`** (7) runs each item in its own runspace. Outer variables arrive only through `$using:`, as copies; mutating a shared collection through `$using:` races, so use a thread-safe collection. `-ThrottleLimit` defaults to 5, and output order isn't kept.
- **Thread jobs and runspace pools** share .NET objects, which mostly aren't thread-safe.
- **`Start-Job`** runs in another process with its own environment, and returns deserialized objects without their methods.
- **Files shared with other scripts** follow the core rules: atomic writes, and a lock against lost updates ([core concurrency](../../checks/core/concurrency.md#shared-state-without-locks)).

<a id="p6-cmdlets-and-the-host"></a>
## P6: cmdlets and the host

- **Non-interactive hosts:** `Read-Host`, `Get-Credential`, `$Host.UI.PromptForChoice`, `pause` and confirmation prompts fail or hang. A cmdlet with a high `ConfirmImpact` prompts unless called with `-Confirm:$false`, where that is meant.
- **`SupportsShouldProcess`** on scripts that change state, with `$PSCmdlet.ShouldProcess` around each change, so `-WhatIf` works.
- **Recursive deletes and links:** `Remove-Item -Recurse -Force` on a junction or symbolic link can follow it into the target and delete what's there, notably in 5.1. Check `(Get-Item $path).LinkType` before a recursive delete.
- **`Copy-Item -Recurse`** into an existing folder nests the source inside it; into a missing one, it copies the contents. The result depends on whether the destination exists.
- **`Invoke-WebRequest`/`Invoke-RestMethod`:** set `-TimeoutSec` (no timeout by default); a non-2xx response throws (7 has `-SkipHttpErrorCheck`).
- **Script-relative paths:** `$PSScriptRoot`, not the current folder.
- **Aliases** differ by version and OS. Full cmdlet names in scripts (PSAvoidUsingCmdletAliases).

<a id="p12-performance"></a>
## P12: performance

- **`+=` on arrays** copies the whole array every time, so a loop is quadratic. Collect loop output (`$items = foreach (...) { ... }`) or use a `List[T]`.
- **`| Out-Null`** is slow in loops; use `$null =`.
- **Pipelines per item** cost more than `foreach` statements on large inputs. `Get-Content` without `-Raw` on large files is slow.
- **Starting `pwsh` or `powershell.exe` in a loop** costs hundreds of milliseconds each.

<a id="p14-help-and-release"></a>
## P14: help and release

- **Comment-based help** (`.SYNOPSIS`, `.PARAMETER`, `.EXAMPLE`) matches the parameters.
- **`#Requires`** lines state the version, edition and modules the script needs.
- **Module manifests:** version bumped, and `FunctionsToExport` listed explicitly, not `*`.
- **Gate scripts:** staged copies of `send.ps1` are reviewed and copied into Gate by the master; the user runs `Update-Send` only.

<a id="p15-tests"></a>
## P15: tests

- **Pester 5:** `Describe`/`It` with `Should` assertions, `Mock` for native commands and anything that changes the machine, `TestDrive:` for files. Code outside `BeforeAll` and `It` runs at discovery time in Pester 5, not during the test.
- **Run headless:** `pwsh -NoProfile -NonInteractive` in a temp folder, and under `powershell.exe` too when 5.1 runs the script. No window, no focus change.
- **Native tools** are faked for exit codes and stderr output, plus one real run where safe.
- **No tests:** a script with machine side effects and no tests needs its live test named in the record; `-WhatIf` runs where supported.
