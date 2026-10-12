#Requires -Version 5.1
<#
.SYNOPSIS
    Checks scripts\assert-gated.ps1 against every kind of gate record it must accept or refuse - a green
    run, and a red run whose failures were proven flaky and then cleared on main.

.DESCRIPTION
    Builds a throwaway git repository with a candidate commit and a later "fix" commit, copies
    assert-gated.ps1 into it beside a stand-in gate-lock.ps1 whose record directories point into that
    throwaway folder, writes one set of records per case, and checks the exit code. Nothing on this
    machine's real gate record directory is read or written.

    It starts git and powershell, so it is a script you run by hand when you change assert-gated.ps1 or
    the record format in test-local.ps1 - deliberately NOT a test in the gate, whose suites may not
    start processes.

.EXAMPLE
    .\scripts\test\test-assert-gated.ps1
#>
$ErrorActionPreference = "Stop"
$scriptsDir = Split-Path -Parent $PSScriptRoot
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("cc-assert-gated-test-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
$repo = Join-Path $work "repo"
$gatedDir = Join-Path $work "records\gated"
$clearDir = Join-Path $work "records\cleared"

function Invoke-Git([string[]] $GitArgs) {
    $out = & git -C $repo @GitArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git $($GitArgs -join ' ') failed: $out" }
    return $out
}

$failures = 0
try {
    New-Item -ItemType Directory -Force (Join-Path $repo "scripts") | Out-Null
    Invoke-Git @("init", "-q") | Out-Null
    Invoke-Git @("config", "user.email", "gate-test@example.invalid") | Out-Null
    Invoke-Git @("config", "user.name", "gate test") | Out-Null
    Set-Content -Path (Join-Path $repo "a.txt") -Value "candidate"
    Invoke-Git @("add", "-A") | Out-Null
    Invoke-Git @("commit", "-q", "-m", "candidate") | Out-Null
    $candidate = ([string] (Invoke-Git @("rev-parse", "HEAD"))).Trim()
    Set-Content -Path (Join-Path $repo "a.txt") -Value "fix"
    Invoke-Git @("commit", "-q", "-am", "fix") | Out-Null
    $fix = ([string] (Invoke-Git @("rev-parse", "HEAD"))).Trim()

    Copy-Item (Join-Path $scriptsDir "assert-gated.ps1") (Join-Path $repo "scripts\assert-gated.ps1")
    Set-Content -Path (Join-Path $repo "scripts\gate-lock.ps1") -Encoding Ascii -Value @"
function Get-GateRecordDirectory { return '$gatedDir' }
function Get-GateClearanceDirectory { return '$clearDir' }
"@

    function New-Suite([string] $Name, [string] $Outcome) {
        return [ordered]@{ name = $Name; outcome = $Outcome; total = 10; executed = 10; exitCode = $(if ($Outcome -eq "Completed") { 0 } else { 1 }) }
    }
    function New-GreenRecord {
        return [ordered]@{
            commit = $candidate; treeClean = $true; dirtyFiles = @(); parked = $true; gateway = $false
            configuration = "Release"; filter = ""; startedUtc = "2026-10-11T10:00:00.0000000Z"
            machine = "TEST"; session = "test"; directory = $repo; exitCode = 0; verdict = "GREEN"
            suites = @((New-Suite "Alpha.Tests" "Completed"), (New-Suite "Beta.Tests" "Completed"))
        }
    }
    function New-FlakyRecord {
        $r = New-GreenRecord
        $r.exitCode = 10
        $r.verdict = "FLAKY"
        $r.suites = @((New-Suite "Alpha.Tests" "Completed"), (New-Suite "Beta.Tests" "Failed"))
        $r.rerun = [ordered]@{
            verdict = "FLAKY"; runFolder = "x"; startedUtc = "2026-10-11T12:00:00.0000000Z"
            flaky = @([ordered]@{ suite = "Beta.Tests"; test = "Beta.Tests.WobblyTests.SometimesFails" })
            stillFailing = @(); notRun = @()
        }
        return $r
    }
    function New-Clearance([string] $At, [string] $Resolution) {
        return [ordered]@{
            candidate = $candidate; clearedAt = $At; configuration = "Release"; verdict = "CLEARED"
            tests = @([ordered]@{ suite = "Beta.Tests"; test = "Beta.Tests.WobblyTests.SometimesFails"; resolution = $Resolution; passes = 5 })
        }
    }

    function Test-Case([string] $Name, [int] $Expected, $Record, $Clearance, [string] $OriginMain, [string] $Says, [int] $RedGates = 1) {
        foreach ($d in @($gatedDir, $clearDir)) {
            if (Test-Path $d) { Remove-Item -Recurse -Force $d }
            New-Item -ItemType Directory -Force $d | Out-Null
        }
        # The runs log: $RedGates failed release gates of the candidate, plus one unrelated run.
        $runsLines = @()
        for ($i = 0; $i -lt $RedGates; $i++) {
            $runsLines += ([ordered]@{ commit = $candidate; treeClean = $true; parked = $true; filter = ""; configuration = "Release"; verdict = "FAILED" } | ConvertTo-Json -Compress)
        }
        # A failed release gate of the candidate on a DIRTY tree, which tested something else and never counts.
        $runsLines += ([ordered]@{ commit = $candidate; treeClean = $false; parked = $true; filter = ""; configuration = "Release"; verdict = "FAILED" } | ConvertTo-Json -Compress)
        $runsLines += ([ordered]@{ commit = $fix; parked = $false; filter = ""; configuration = "Debug"; verdict = "GREEN" } | ConvertTo-Json -Compress)
        Set-Content -Path (Join-Path $work "records\runs.jsonl") -Value $runsLines -Encoding Ascii
        if ($null -ne $Record) {
            $Record | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $gatedDir "$candidate-20261011T100000Z.json") -Encoding Ascii
        }
        if ($null -ne $Clearance) {
            $Clearance | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $clearDir "$candidate-20261011T130000Z.json") -Encoding Ascii
        }
        if ($OriginMain -eq "") {
            # rev-parse --quiet first: update-ref -d on a missing ref writes to stderr, which throws under "Stop".
            $null = (& git -C $repo rev-parse --verify --quiet refs/remotes/origin/main)
            if ($LASTEXITCODE -eq 0) { Invoke-Git @("update-ref", "-d", "refs/remotes/origin/main") | Out-Null }
        }
        else { Invoke-Git @("update-ref", "refs/remotes/origin/main", $OriginMain) | Out-Null }
        $output = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo "scripts\assert-gated.ps1") $candidate 2>&1
        $code = $LASTEXITCODE
        # The REASON is checked as well as the exit code: a refusal for the wrong reason (a record that
        # could not be read, say) would pass on exit code alone and prove nothing about the rule.
        $text = ($output | ForEach-Object { [string] $_ }) -join "`n"
        if ($code -eq $Expected -and $text.Contains($Says)) {
            Write-Host ("PASS  {0}  (exit {1})" -f $Name, $code)
        } else {
            Write-Host ("FAIL  {0}  expected exit {1} saying '{2}', got exit {3}" -f $Name, $Expected, $Says, $code)
            foreach ($l in $output) { Write-Host "        $l" }
            $script:failures++
        }
    }

    Test-Case "no record is refused" 1 $null $null $fix "no green -Parked run is recorded"
    Test-Case "a green release run is accepted" 0 (New-GreenRecord) $null $fix "GATED: " 0
    Test-Case "a green run after a failed release gate of the candidate is a re-roll and is refused" 1 (New-GreenRecord) $null $fix "never run again whole" 1
    $r = New-GreenRecord; $r.configuration = "Debug"
    Test-Case "a green Debug run is refused" 1 $r $null $fix "its configuration was Debug"
    $r = New-GreenRecord; $r.filter = "FullyQualifiedName~X"
    Test-Case "a green filtered run is refused" 1 $r $null $fix "it was filtered"
    Test-Case "a FLAKY record with no clearance is refused" 1 (New-FlakyRecord) $null $fix "no clearance shows them deleted or fixed"
    Test-Case "a FLAKY record cleared (fixed) on origin/main is accepted" 0 (New-FlakyRecord) (New-Clearance $fix "FIXED in Beta.Tests/WobblyTests.cs") $fix "since deleted or fixed on main"
    Test-Case "a FLAKY record cleared (deleted) on origin/main is accepted" 0 (New-FlakyRecord) (New-Clearance $fix "DELETED") $fix "since deleted or fixed on main"
    Test-Case "a clearance not yet on origin/main is refused" 1 (New-FlakyRecord) (New-Clearance $fix "DELETED") $candidate "is not on origin/main"
    Test-Case "a clearance with no origin/main to check is refused" 1 (New-FlakyRecord) (New-Clearance $fix "DELETED") "" "has no origin/main"
    Test-Case "a clearance that left the test unchanged is refused" 1 (New-FlakyRecord) (New-Clearance $fix "UNCHANGED: passed 5 times") $fix "as deleted or fixed"
    $c = New-Clearance $fix "DELETED"; $c.tests = @()
    Test-Case "a clearance that does not name the flaky test is refused" 1 (New-FlakyRecord) $c $fix "as deleted or fixed"
    $c = New-Clearance $fix "DELETED"; $c.configuration = "Debug"
    Test-Case "a clearance in another configuration is refused" 1 (New-FlakyRecord) $c $fix "it ran Debug"
    $c = New-Clearance $fix "DELETED"; $c.candidate = $fix
    Test-Case "a clearance for another candidate is refused" 1 (New-FlakyRecord) $c $fix "it clears $fix, not $candidate"
    $r = New-FlakyRecord; $r.rerun.stillFailing = @([ordered]@{ suite = "Beta.Tests"; test = "Beta.Tests.RealTests.Broken" })
    Test-Case "a re-run that failed again is refused even with a clearance" 1 $r (New-Clearance $fix "DELETED") $fix "failed again"
    $r = New-FlakyRecord; $r.suites = @((New-Suite "Alpha.Tests" "Failed"), (New-Suite "Beta.Tests" "Failed"))
    Test-Case "a failing suite the re-run did not cover is refused" 1 $r (New-Clearance $fix "DELETED") $fix "suite Alpha.Tests failed, and the re-run named no flaky test in it"
    $r = New-FlakyRecord; $r.suites = @((New-Suite "Alpha.Tests" "Aborted"), (New-Suite "Beta.Tests" "Failed"))
    Test-Case "a FLAKY record of a run that did not finish is refused" 1 $r (New-Clearance $fix "DELETED") $fix "so the run did not finish"
    $r = New-FlakyRecord; $r.treeClean = $false
    Test-Case "a FLAKY record of a dirty tree is refused" 1 $r (New-Clearance $fix "DELETED") $fix "the tree was dirty"
    Test-Case "a second failed release gate of the candidate is a re-roll and is refused" 1 (New-FlakyRecord) (New-Clearance $fix "DELETED") $fix "a re-roll" 2
    Test-Case "a FLAKY record with no failed release gate in the runs log is refused" 1 (New-FlakyRecord) (New-Clearance $fix "DELETED") $fix "shows 0 failed release gates" 0
    $r = New-FlakyRecord; $r.suites[0].exitCode = 3
    Test-Case "a suite that exited nonzero outside any test is refused" 1 $r (New-Clearance $fix "DELETED") $fix "a failure outside any test"
} finally {
    if (Test-Path $work) { Remove-Item -Recurse -Force $work }
}

Write-Host ""
if ($failures -gt 0) {
    Write-Host "RESULT: $failures case(s) FAILED."
    exit 1
}
Write-Host "RESULT: every case behaved as the rule says."
exit 0
