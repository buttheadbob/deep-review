<#
.SYNOPSIS
Runs PSScriptAnalyzer for deep_tools.py, one rule at a time.

.DESCRIPTION
One Invoke-ScriptAnalyzer call with every rule runs the rules in parallel, and on large scripts that
intermittently hangs, throws a NullReferenceException or returns a different number of findings from run
to run. One rule per call is deterministic. The settings come from the central deep-psscriptanalyzer.psd1:
its Severity list, its Rules configuration and its ExcludeRules.

.PARAMETER ListFile
A text file with one script path per line.

.PARAMETER SettingsFile
The central settings file (deep-psscriptanalyzer.psd1).

.PARAMETER OutFile
Where the JSON result goes: Version, Results and Failures.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ListFile,

    [Parameter(Mandatory = $true)]
    [string] $SettingsFile,

    [Parameter(Mandatory = $true)]
    [string] $OutFile
)

$ErrorActionPreference = 'Stop'
Import-Module -Name PSScriptAnalyzer
$settings = Import-PowerShellDataFile -LiteralPath $SettingsFile
$excluded = @($settings.ExcludeRules)
$rules = Get-ScriptAnalyzerRule |
    Where-Object { $excluded -notcontains $_.RuleName } |
    Select-Object -ExpandProperty RuleName
$results = [System.Collections.Generic.List[object]]::new()
$failures = [System.Collections.Generic.List[object]]::new()

foreach ($file in Get-Content -LiteralPath $ListFile) {
    foreach ($rule in $rules) {
        $one = @{
            Severity     = [string[]]$settings.Severity
            IncludeRules = [string[]]@($rule)
            Rules        = $settings.Rules
        }
        try {
            foreach ($finding in Invoke-ScriptAnalyzer -Path $file -Settings $one) {
                $results.Add([pscustomobject]@{
                        RuleName   = $finding.RuleName
                        Severity   = [string]$finding.Severity
                        Message    = $finding.Message
                        ScriptPath = $finding.ScriptPath
                        Line       = $finding.Line
                        Column     = $finding.Column
                    })
            }
        }
        catch {
            $failures.Add([pscustomobject]@{
                    File  = $file
                    Rule  = $rule
                    Error = $_.Exception.Message
                })
        }
    }
}

[pscustomobject]@{
    Version  = (Get-Module -Name PSScriptAnalyzer).Version.ToString()
    Rules    = @($rules).Count
    Results  = $results
    Failures = $failures
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $OutFile -Encoding utf8
