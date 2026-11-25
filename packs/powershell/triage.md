# PSScriptAnalyzer triage

Pass P1. deep_tools runs PSScriptAnalyzer with every rule at every severity, one rule per call (`tools\linters\pssa-run.ps1`, central settings in `tools\analyzers\deep-psscriptanalyzer.psd1`), never the repository's own settings. A `[Diagnostics.CodeAnalysis.SuppressMessageAttribute]` in the code under review is a suppression issue. A script PSScriptAnalyzer can't parse is a gap.

Every lead in scope gets a verdict (`real`, `intended`, `not a problem`, `unable to verify`) with the line that proves it. A rule that is wrong for every project is turned off in the central settings with a `# reason:`, which the tools own.

## Rules that find real defects

Read each lead individually:
- **Injection and secrets:** PSAvoidUsingInvokeExpression, PSAvoidUsingPlainTextForPassword, PSAvoidUsingConvertToSecureStringWithPlainText, PSAvoidUsingUsernameAndPasswordParams, PSUsePSCredentialType, PSAvoidUsingComputerNameHardcoded.
- **Logic slips:** PSPossibleIncorrectComparisonWithNull (`$null` on the right), PSPossibleIncorrectUsageOfAssignmentOperator (`=` inside `if`), PSPossibleIncorrectUsageOfRedirectionOperator (`>` inside `if`), PSAvoidAssignmentToAutomaticVariable, PSAvoidDefaultValueSwitchParameter.
- **Errors:** PSAvoidUsingEmptyCatchBlock.
- **State changes:** PSUseShouldProcessForStateChangingFunctions and PSShouldProcess (a `SupportsShouldProcess` declared but not used, or the reverse).
- **Encoding:** PSUseBOMForUnicodeEncodedFile, real when Windows PowerShell 5.1 runs the script ([checks.md](checks.md#p4-windows-powershell-51-versus-powershell-7)).
- **Compatibility:** PSUseCompatibleSyntax, PSUseCompatibleCommands, PSUseCompatibleCmdlets and PSUseCompatibleTypes are off in the central settings today, because they need target version profiles. So nothing automated checks that a script runs on 5.1: check it by reading ([checks.md](checks.md#p4-windows-powershell-51-versus-powershell-7)). A 7-only construct in a script 5.1 runs is a parse error there.
- **Old APIs:** PSAvoidUsingWMICmdlet (gone in 7).
- **Scope:** PSAvoidGlobalVars; PSUseDeclaredVarsMoreThanAssignments (often a false positive when a script block or `$script:` use reads the variable: trace it before the verdict); PSReviewUnusedParameter.

## Rules that need judgement

- **PSAvoidUsingWriteHost:** in a script whose interface is the console, `Write-Host` is the UI (`intended`, with the reason). In a function whose output callers capture, it's a finding: the text never reaches the pipeline.
- **PSAvoidUsingPositionalParameters** and **PSAvoidUsingCmdletAliases:** real in scripts that run on several versions or on Linux, where aliases differ ([checks.md](checks.md#p6-cmdlets-and-the-host)).
- **PSUseOutputTypeCorrectly:** a lead for output pollution ([checks.md](checks.md#p13-language-traps)).

## Style rules

PSUseApprovedVerbs, PSUseSingularNouns, PSAvoidTrailingWhitespace, PSAvoidLongLines and the placement and indentation rules can be grouped by rule, with one verdict per rule and file.
