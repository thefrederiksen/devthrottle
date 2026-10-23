#Requires -Version 5.1
<#
.SYNOPSIS
    Decide WHICH test suites a change actually needs, from the files it touched.

.DESCRIPTION
    The gate runs 1,634 tests in about three minutes, and three suites are parked because they cannot
    meet that budget - so most of the time they run for nobody. Selection is how the parked coverage
    comes back: a change that touches the Gateway's host-bound surface picks up the host-bound suite
    automatically, whether or not anyone remembered to pass -Parked, while a change to the phone app
    keeps paying nothing for tests it cannot possibly break.

    THE DEPENDENCY MAP IS DERIVED, NOT WRITTEN DOWN. A hand-maintained list of "this folder needs
    that suite" is wrong the day somebody adds a ProjectReference, and wrong silently. This reads the
    .csproj files, builds the reference graph, and computes each test project's transitive closure.
    A test project is selected when the change touched any project inside its own closure. Nobody has
    to remember to update it.

    IT FAILS TOWARD RUNNING MORE, ALWAYS. A wrong selection that runs too much costs seconds; a wrong
    selection that skips costs a regression that reaches main behind a green gate, and a silent skip
    is indistinguishable from a pass. So: an unrecognised path selects EVERYTHING, a project file
    change selects EVERYTHING (the graph itself moved), and a change to the gate or to this script
    selects EVERYTHING. Only paths whose irrelevance is obvious and stated are allowed to select
    nothing.

.PARAMETER Base
    The ref to diff against. Defaults to origin/main.

.PARAMETER Explain
    Print the reasoning - which paths were seen, which projects they belong to, and why each suite
    was or was not selected.

.EXAMPLE
    .\scripts\select-tests.ps1
    .\scripts\select-tests.ps1 -Explain
    .\scripts\select-tests.ps1 -Base HEAD~1 -Explain
#>
param(
    [string]$Base = "origin/main",
    # An explicit file list instead of a diff. The validation harness feeds historical changes through
    # here, so the rules being validated are THE rules, not a second copy of them that can drift.
    [string[]]$Files,
    [switch]$Explain
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

function Get-ProjectGraph {
    $projs = @{}
    Get-ChildItem -Path (Join-Path $repoRoot "src") -Filter *.csproj -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object {
            $text = Get-Content $_.FullName -Raw
            $name = $_.BaseName
            $refs = [regex]::Matches($text, 'ProjectReference\s+Include="([^"]+)"') |
                    ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension($_.Groups[1].Value) }
            $dir = (Split-Path -Parent $_.FullName).Substring($repoRoot.Length).TrimStart('\','/') -replace '\\','/'
            $projs[$name] = [pscustomobject]@{
                Name   = $name
                Dir    = $dir
                Refs   = @($refs)
                # Both spellings are in use: the six historical ".Tests" projects and the newer
                # ".UnitTests" split-out. Matching on IsTestProject alone missed one of them.
                IsTest = ($text -replace '\s','') -match '<IsTestProject>true</IsTestProject>' -or $name -match '\.(Unit)?Tests$'
            }
        }
    return $projs
}

function Get-Closure {
    param($Projs, [string]$Name, $Seen = $null)
    if ($null -eq $Seen) { $Seen = New-Object 'System.Collections.Generic.HashSet[string]' }
    foreach ($r in $Projs[$Name].Refs) {
        if ($Projs.ContainsKey($r) -and $Seen.Add($r)) { Get-Closure $Projs $r $Seen | Out-Null }
    }
    return $Seen
}

$projs = Get-ProjectGraph
$testProjects = @($projs.Values | Where-Object { $_.IsTest } | Sort-Object Name)

# Each test project's own closure, plus itself: the set of projects whose change can affect it.
$affects = @{}
foreach ($t in $testProjects) {
    $c = Get-Closure $projs $t.Name
    $set = New-Object 'System.Collections.Generic.HashSet[string]'
    [void]$set.Add($t.Name)
    foreach ($x in $c) { [void]$set.Add($x) }
    $affects[$t.Name] = $set
}

if ($PSBoundParameters.ContainsKey('Files')) {
    $changed = @($Files)
} else {
    $changed = @(& git -C $repoRoot diff --name-only "$Base...HEAD" 2>$null)
    if ($LASTEXITCODE -ne 0) { $changed = @() }
}
if ($changed.Count -eq 0) {
    # No diff, or the base is unknown. Both mean "cannot reason about this change" -> run everything.
    $changed = @()
}

$selected = New-Object 'System.Collections.Generic.HashSet[string]'
$needWeb  = $false
$runAll   = $false
$reasons  = @()

if ($changed.Count -eq 0) {
    $runAll = $true
    $reasons += "no diff against $Base could be read - running everything"
}

foreach ($f in $changed) {
    $p = $f -replace '\\','/'
    switch -Regex ($p) {
        # The graph itself moved, or the gate did. Either way no selection can be trusted.
        '\.csproj$|\.sln$'                    { $runAll = $true; $reasons += "$p - a project or solution file changed, so the dependency graph itself moved"; continue }
        '^scripts/(test-local|select-tests)\.ps1$' { $runAll = $true; $reasons += "$p - the gate itself changed"; continue }
        '^Directory\.Build\.'                 { $runAll = $true; $reasons += "$p - a build-wide property changed"; continue }

        # Web only. No .NET assembly can be affected by a React or TypeScript change.
        '^(apps|packages)/'                   { $needWeb = $true; $reasons += "$p - web workspace"; continue }

        # Documented as affecting no test. Keep this list SHORT and obvious.
        '^docs/|\.md$|^\.github/|\.(png|jpg|jpeg|gif|svg|ico)$' { $reasons += "$p - affects no test"; continue }

        '^src/' {
            # Longest matching project directory owns the file.
            $owner = $projs.Values | Where-Object { $p.StartsWith($_.Dir + "/") } |
                     Sort-Object { $_.Dir.Length } -Descending | Select-Object -First 1
            if ($null -eq $owner) {
                $runAll = $true; $reasons += "$p - under src/ but inside no known project, so nothing can be ruled out"
            } else {
                $hit = @($testProjects | Where-Object { $affects[$_.Name].Contains($owner.Name) })
                foreach ($t in $hit) { [void]$selected.Add($t.Name) }
                $reasons += "$p - $($owner.Name) -> $(($hit | ForEach-Object Name) -join ', ')"
            }
            continue
        }

        default { $runAll = $true; $reasons += "$p - unrecognised path, so nothing can be ruled out" }
    }
}

if ($runAll) {
    $final = @($testProjects | ForEach-Object Name)
    $needWeb = $true
} else {
    $final = @($selected)
}

if ($Explain) {
    Write-Host "Base: $Base    files changed: $($changed.Count)"
    Write-Host ""
    foreach ($r in $reasons) { Write-Host "  $r" }
    Write-Host ""
    Write-Host "Run all: $runAll    Web tests: $needWeb"
    Write-Host "Selected suites:"
    if ($final.Count -eq 0) { Write-Host "  (none - this change cannot affect any .NET test)" }
    foreach ($s in ($final | Sort-Object)) { Write-Host "  $s" }
}

# The machine-readable answer, for test-local.ps1 and for the validation harness.
[pscustomobject]@{
    RunAll   = $runAll
    Web      = $needWeb
    Suites   = @($final | Sort-Object)
    Changed  = $changed
    Reasons  = $reasons
}
