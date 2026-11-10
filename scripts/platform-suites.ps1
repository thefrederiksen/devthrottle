#Requires -Version 5.1
<#
.SYNOPSIS
    Which test projects this operating system cannot RUN, so the gate can name them instead of
    silently dropping them or pretending they passed (issue #4806).

.DESCRIPTION
    Some test projects target Windows ALONE. With EnableWindowsTargeting they BUILD on a Mac or a Linux
    box, but a Windows-targeted assembly cannot EXECUTE there: the run reports zero tests, and
    test-local.ps1's own honesty check correctly calls that "NEVER RAN" and refuses a verdict on
    anything. Before this the gate on a Mac could not go green however good the change was.

    This lives in its own file, dot-sourced by scripts\test-local.ps1, so the rule can be tested
    directly by scripts\test\test-gate-records.ps1. Keeping it testable has already paid for itself
    twice: the first draft normalized separators with a two-character literal (two backslashes in
    PowerShell, which matched nothing) and the second asked the project file's XML directly, which
    under-detects in three ordinary shapes - see below.

    IT ASKS MSBUILD RATHER THAN READING THE XML, and that is the whole point of the design. Reading
    //TargetFramework out of the csproj sees only what that one file says, so it misses a framework set
    in Directory.Build.props or an imported .props (the element is simply absent), a project carrying
    the old xmlns="http://schemas.microsoft.com/developer/msbuild/2003" default namespace (the XPath
    matches nothing under it), and a conditional PropertyGroup (every branch is counted, whatever its
    Condition). EVERY one of those failures sends the gate back to reporting NEVER RAN - the exact
    defect this file exists to prevent - so a textual guess is the wrong instrument however convenient.

        dotnet msbuild <csproj> -getProperty:TargetFrameworks -getProperty:TargetFramework

    returns the EVALUATED values, with imports, property files and conditions resolved for THIS
    operating system. Measured on a Mac mini: 0.16 to 0.26 seconds per project, about three seconds for
    the fifteen projects the gate lists, and it is paid only on the platforms that need it because
    Get-SuitesThisPlatformCannotRun returns at once on Windows. It needs software development kit 8 or
    later for -getProperty; this repository is on 10.0.403. It does NOT need EnableWindowsTargeting,
    because evaluating a property does not trip NETSDK1100. Both shapes above were measured resolving
    correctly through it before this was written.
#>

<#
.SYNOPSIS
    The target frameworks MSBuild evaluates for a project, or an EMPTY set when that cannot be
    established - which callers must read as "cannot tell", never as "none".
#>
function Get-ProjectTargetFrameworks([string] $ProjectPath) {
    try {
        if ([string]::IsNullOrWhiteSpace($ProjectPath)) { return @() }

        # The gate's project list is written with Windows separators. Normalized here so the lookup does
        # not depend on the host tolerating them. (PowerShell does not use the backslash as an escape;
        # the character code is used so the literal cannot be misread, which the first draft was.)
        $resolved = $ProjectPath.Replace([string] [char] 92, [IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path $resolved)) { return @() }

        $raw = & dotnet msbuild $resolved -getProperty:TargetFrameworks -getProperty:TargetFramework 2>$null
        if ($LASTEXITCODE -ne 0 -or $null -eq $raw) { return @() }

        $parsed = ($raw -join "`n") | ConvertFrom-Json
        $values = @($parsed.Properties.TargetFrameworks, $parsed.Properties.TargetFramework)

        return @($values |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            ForEach-Object { $_.Split(";") } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            ForEach-Object { $_.Trim() })
    } catch {
        return @()
    }
}

<#
.SYNOPSIS
    True when EVERY target framework the project evaluates to is a Windows one, so this operating
    system cannot run its tests.

.DESCRIPTION
    THE RULE IS POSITIVE. A project is held back only when it can be SHOWN to be Windows-only. A
    multi-targeted project (CcDirector.Launcher.Tests evaluates to net10.0-windows AND net10.0) is NOT
    held back, because its portable target runs here. A project whose frameworks cannot be established
    is NOT held back either - it is run, and a real failure is better than a silent omission.
#>
function Test-ProjectIsWindowsOnly([string] $ProjectPath) {
    $declared = Get-ProjectTargetFrameworks $ProjectPath
    if ($declared.Count -eq 0) { return $false }   # cannot tell - run it
    return -not ($declared | Where-Object { $_ -notmatch "-windows" })
}

<#
.SYNOPSIS
    The subset of $Projects this operating system cannot run. Empty on Windows, and empty anywhere
    that every project has a portable target.
#>
function Get-SuitesThisPlatformCannotRun([string[]] $Projects, [string] $RepoRoot) {
    if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) { return @() }
    if ($null -eq $Projects) { return @() }
    return @($Projects | Where-Object { Test-ProjectIsWindowsOnly (Join-Path $RepoRoot $_) })
}
