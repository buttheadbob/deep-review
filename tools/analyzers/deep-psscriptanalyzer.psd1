# Deep review PSScriptAnalyzer settings. Passed with -Settings, so a repository's own settings file is ignored.
# Every default rule runs at every severity (Error, Warning, Information, ParseError), and the configurable rules that
# are off by default are turned on below. A rule is turned off only in ExcludeRules, each with a "# reason:".
# [Diagnostics.CodeAnalysis.SuppressMessage] in scripts is counted as a suppression issue.
@{
    Severity            = @('Error', 'Warning', 'Information', 'ParseError')
    IncludeDefaultRules = $true
    ExcludeRules        = @(
        'PSUseCompatibleCmdlets'      # reason: needs a target platform profile list; not a defect check without one
        'PSUseCompatibleCommands'     # reason: needs target platform profiles to compare against
        'PSUseCompatibleSyntax'       # reason: needs a target PowerShell version list
        'PSUseCompatibleTypes'        # reason: needs target platform profiles to compare against
    )
    Rules               = @{
        PSPlaceOpenBrace                           = @{ Enable = $true }
        PSPlaceCloseBrace                          = @{ Enable = $true }
        PSUseConsistentIndentation                 = @{ Enable = $true }
        # IgnoreAssignmentOperatorInsideHashTable: without it this rule flags the alignment PSAlignAssignmentStatement asks for
        # (the PSScriptAnalyzer docs name this setting for that conflict); every other whitespace check stays on.
        PSUseConsistentWhitespace                  = @{ Enable = $true; IgnoreAssignmentOperatorInsideHashTable = $true }
        PSAlignAssignmentStatement                 = @{ Enable = $true }
        PSUseCorrectCasing                         = @{ Enable = $true }
        PSAvoidLongLines                           = @{ Enable = $true }
        PSAvoidSemicolonsAsLineTerminators         = @{ Enable = $true }
        PSAvoidExclaimOperator                     = @{ Enable = $true }
        PSAvoidUsingDoubleQuotesForConstantString  = @{ Enable = $true }
        PSProvideCommentHelp                       = @{ Enable = $true }
    }
}
