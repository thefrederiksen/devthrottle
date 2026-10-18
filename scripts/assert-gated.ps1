#Requires -Version 5.1
<#
.SYNOPSIS
    Exits 0 only for a commit that the release gate certified on this machine: a green, unfiltered,
    clean-tree run of scripts\test-local.ps1 -Parked -Configuration Release at exactly that commit - or
    such a run whose only failures were proven FLAKY and then deleted or fixed on main.

.DESCRIPTION
    scripts\new-release.ps1 -Tag <candidate> calls this before it tags, and refuses the tag when this
    refuses the commit. The release workflow runs no tests and a pushed tag cannot be un-pushed, so this
    is the only thing between an ungated commit and the users.

    WHAT COUNTS AS GATED. The gate writes one record per green, unfiltered -Parked run, named by the
    commit it ran on, in the directory scripts\gate-records.ps1 derives (per user, per machine). A record
    is accepted only when every one of these holds, and the refusal names the first that does not:
      - its commit is the commit asked about - a run at the parent, or at the pull-request head that
        was squashed into the candidate, is a run at a different commit;
      - it was a -Parked run with no -Filter, so every suite ran, including the three the default run
        skips;
      - its configuration was Release, which is what users download; the gate defaults to Debug;
      - the tree was clean - a run on a dirty tree tested something that is not the commit;
      - it exited 0, and every suite in it reported outcome=Completed with at least one test executed;
      - the runs log beside the records shows NO failed release gate of this commit - a red candidate
        followed by another whole run until one comes out green is a re-roll, not a gate. That has a
        price, paid on purpose: a release gate that went red because of the MACHINE (a full disk, say,
        with far more failures than -RerunFailed takes) cannot be rescued by running the gate again, and
        the way forward is a new candidate. The free-space check before a release gate exists so that
        this almost never happens.

    OR, A RED GATE WHOSE EVERY FAILURE WAS A FLAKY TEST (THE FLAKY TEST RULE in scripts\test-local.ps1).
    When the release gate fails, "test-local.ps1 -RerunFailed <run folder>" re-runs only the failed tests
    on the same commit. If every one of them passes, the gate record is written with verdict FLAKY and the
    flaky tests named. That record ALONE is refused: a flaky test is a defective test, and it is never
    quietly accepted. It is accepted only together with a CLEARANCE, written by
    "test-local.ps1 -ClearFlaky <candidate>" on a later commit, which holds when:
      - the FLAKY record meets every condition above except the exit code: it finished (every suite
        Completed or Failed, each with tests executed, and none exited nonzero outside a failed test),
        its re-run named every failing suite, and the re-run found no test that failed again and none it
        could not run;
      - the runs log beside the records shows exactly ONE failed release gate of this commit - a second
        whole gate followed by a re-run of the better one is a re-roll;
      - the clearance is for this candidate, in the same configuration, with verdict CLEARED;
      - it names every flaky test as DELETED (not collected, and no method of that name left in any test
        project) or FIXED (its code changed since the candidate, and it then passed five times in a row
        against the CANDIDATE'S product code with main's tests);
      - the commit it was made at contains the candidate AND is on origin/main - the fix is merged, not
        sitting on a branch. origin/main is read as this checkout last fetched it; new-release.ps1
        fetches before it calls this.
    The candidate is then tagged as it is: each test that failed was either deleted or repaired and
    proven against the candidate's own product code. A FLAKY verdict alone proves only that a test passed
    when run by itself - see THE FLAKY TEST RULE for why it is never accepted without that repair.

    THE RECORD IS LOCAL TO THE MACHINE THAT RAN THE GATE. Run this where the gate ran. Nothing here
    looks at GitHub, and nothing here runs a test: if there is no record, the answer is to run the gate,
    not to run this again.

.PARAMETER Commit
    The commit to check, in any form git resolves (a full or short hash, a branch, a tag).

.EXAMPLE
    .\scripts\assert-gated.ps1 1a2b3c4d
#>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $Commit
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "gate-records.ps1")

function Refuse([string] $Message, [string] $HowToFix) {
    Write-Host ""
    Write-Host "NOT GATED: $Message"
    if ($HowToFix) { Write-Host $HowToFix }
    Write-Host ""
    exit 1
}

$sha = (& git -C $repoRoot rev-parse --verify --quiet "$Commit^{commit}" 2>$null)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sha)) {
    Write-Host ""
    Write-Host "ERROR: '$Commit' is not a commit in this repository."
    Write-Host ""
    exit 2
}
$sha = $sha.Trim()

$recordDir = Get-GateRecordDirectory
$clearDir = Get-GateClearanceDirectory
$howToRun = "Run the release gate on it, in a worktree detached at that commit:`n" +
            "  git worktree add ../devthrottle-gate --detach $sha`n" +
            "  cd ../devthrottle-gate`n" +
            "  .\scripts\test-local.ps1 -Parked -Configuration Release`n" +
            "A green run writes its record to $recordDir and this check then accepts the commit.`n" +
            "If that run fails, re-run ONLY its failed tests - never the whole gate:`n" +
            "  .\scripts\test-local.ps1 -RerunFailed <the run folder it printed>"

$records = @()
if (Test-Path $recordDir) {
    $records = @(Get-ChildItem -Path $recordDir -Filter "$sha-*.json" | Sort-Object Name)
}
if ($records.Count -eq 0) {
    Refuse "no green -Parked run is recorded for $sha on this machine ($recordDir)." $howToRun
}

# The conditions every accepted run shares, green or flaky: it is a release gate run of exactly this commit.
function Test-ReleaseRun($Record) {
    if ($Record.commit -ne $sha) { return "its commit is $($Record.commit), not $sha" }
    if (-not $Record.parked) { return "it was not a -Parked run, so the three parked suites did not run" }
    if (-not [string]::IsNullOrEmpty($Record.filter)) { return "it was filtered (-Filter '$($Record.filter)'), so not every test ran" }
    if ($Record.configuration -ne "Release") { return "its configuration was $($Record.configuration), not Release" }
    if (-not $Record.treeClean) { return "the tree was dirty ($(@($Record.dirtyFiles).Count) changed or untracked files), so it did not test exactly this commit" }
    return $null
}

# ONE RELEASE GATE PER CANDIDATE. A red gate followed by another whole run until one comes out green is
# a re-roll - re-running until it passes, the very thing the flaky test rule exists to prevent - and it
# would also ship a test that failed AGAIN on a re-run. The runs log beside the gate records holds every
# run, so this counts the FAILED release gates of the commit: none for a green record, exactly one (the
# run whose failures were re-run) for a FLAKY one. -1 when the log is missing.
function Get-RunsLogPath { return (Join-Path (Split-Path -Parent $recordDir) "runs.jsonl") }
function Get-FailedReleaseGateCount {
    $runsLog = Get-RunsLogPath
    if (-not (Test-Path $runsLog)) { return -1 }
    $count = 0
    foreach ($line in (Get-Content $runsLog)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $run = $line | ConvertFrom-Json
        # Only a run of the commit itself counts: a dirty tree tested something else, by this script's own rule.
        if ($run.commit -eq $sha -and $run.treeClean -and $run.parked -and [string]::IsNullOrEmpty([string] $run.filter) -and $run.configuration -eq "Release" -and $run.verdict -eq "FAILED") { $count++ }
    }
    return $count
}

# Every requirement is checked against the record's own fields, and the first unmet one is the reason.
# Reading the record back is the point: the gate wrote what it saw, and this script does not trust the
# file name alone.
function Test-GateRecord($Record) {
    $reason = Test-ReleaseRun $Record
    if ($null -ne $reason) { return $reason }
    if ($Record.exitCode -ne 0) { return "its exit code was $($Record.exitCode), not 0" }
    if ($Record.verdict -ne "GREEN") { return "its verdict was '$($Record.verdict)', not GREEN" }
    $suites = @($Record.suites)
    if ($suites.Count -eq 0) { return "it recorded no suites" }
    foreach ($suite in $suites) {
        if ($suite.outcome -ne "Completed") { return "suite $($suite.name) reported outcome=$($suite.outcome), not Completed" }
        if ([int] $suite.executed -lt 1) { return "suite $($suite.name) executed zero tests" }
    }
    $redGates = Get-FailedReleaseGateCount
    if ($redGates -lt 0) { return "the runs log $(Get-RunsLogPath) is missing, so it cannot be shown that no release gate of this commit failed" }
    if ($redGates -gt 0) { return "the runs log shows $redGates FAILED release gate(s) of $sha before or beside this green one - a red candidate is never run again whole; re-run only its failed tests (-RerunFailed)" }
    return $null
}

# A FLAKY record: the run finished, it failed only on tests that then passed on a re-run of this commit.
function Test-FlakyRecord($Record) {
    $reason = Test-ReleaseRun $Record
    if ($null -ne $reason) { return $reason }
    if ($Record.exitCode -ne 10) { return "its exit code was $($Record.exitCode), not 10 (FLAKY)" }
    $suites = @($Record.suites)
    if ($suites.Count -eq 0) { return "it recorded no suites" }
    $failedSuites = @()
    foreach ($suite in $suites) {
        if (@("Completed", "Failed") -notcontains $suite.outcome) { return "suite $($suite.name) reported outcome=$($suite.outcome), so the run did not finish" }
        if ([int] $suite.executed -lt 1) { return "suite $($suite.name) executed zero tests" }
        if ($suite.outcome -eq "Failed") { $failedSuites += [string] $suite.name }
        # Nonzero exit with no failed test: it failed outside any test, which no re-run could judge.
        if ($suite.outcome -ne "Failed" -and $null -ne $suite.exitCode -and [int] $suite.exitCode -ne 0) { return "suite $($suite.name) exited $($suite.exitCode) with outcome=$($suite.outcome) - a failure outside any test" }
    }
    $redGates = Get-FailedReleaseGateCount
    if ($redGates -lt 0) { return "the runs log $(Get-RunsLogPath) is missing, so it cannot be shown that this was the candidate's only release gate" }
    if ($redGates -ne 1) { return "the runs log shows $redGates failed release gates of $sha, not one - a candidate gets one release gate, and a second run followed by a re-run is a re-roll" }
    if ($null -eq $Record.rerun) { return "it carries no re-run" }
    if ($Record.rerun.verdict -ne "FLAKY") { return "its re-run's verdict was '$($Record.rerun.verdict)', not FLAKY" }
    if (@($Record.rerun.stillFailing).Count -gt 0) { return "its re-run found $(@($Record.rerun.stillFailing).Count) test(s) that failed again - a code defect" }
    if (@($Record.rerun.notRun).Count -gt 0) { return "its re-run could not run $(@($Record.rerun.notRun).Count) of the failed test(s)" }
    $flaky = @($Record.rerun.flaky)
    if ($flaky.Count -eq 0) { return "its re-run named no flaky test" }
    foreach ($name in $failedSuites) {
        # A Gateway.Tests shard fails under its shard name ("CcDirector.Gateway.Tests.shard2of4") and its tests are
        # re-run, and recorded, under the suite's own name (scripts\gateway-test-shards.ps1, Get-SuiteBaseName).
        $base = $name -replace '\.shard\d+of\d+$', ''
        if (@($flaky | Where-Object { $_.suite -eq $base }).Count -eq 0) { return "suite $name failed, and the re-run named no flaky test in it" }
    }
    return $null
}

# A clearance for a FLAKY record: every flaky test deleted or fixed, at a commit on origin/main.
function Test-Clearance($Clearance, $FlakyRecord) {
    if ($Clearance.candidate -ne $sha) { return "it clears $($Clearance.candidate), not $sha" }
    if ($Clearance.verdict -ne "CLEARED") { return "its verdict was '$($Clearance.verdict)', not CLEARED" }
    if ($Clearance.configuration -ne $FlakyRecord.configuration) { return "it ran $($Clearance.configuration), and the gate ran $($FlakyRecord.configuration)" }
    $at = [string] $Clearance.clearedAt
    $null = (& git -C $repoRoot rev-parse --verify --quiet "$at^{commit}")
    if ($LASTEXITCODE -ne 0) { return "the commit it was made at ($at) is not in this repository" }
    & git -C $repoRoot merge-base --is-ancestor $sha $at
    if ($LASTEXITCODE -ne 0) { return "the commit it was made at ($at) does not contain the candidate" }
    & git -C $repoRoot rev-parse --verify --quiet "refs/remotes/origin/main" | Out-Null
    if ($LASTEXITCODE -ne 0) { return "this checkout has no origin/main to check the fix against; run 'git fetch origin'" }
    & git -C $repoRoot merge-base --is-ancestor $at "refs/remotes/origin/main"
    if ($LASTEXITCODE -ne 0) { return "the commit it was made at ($at) is not on origin/main - the fix is not merged (or this checkout has not fetched it)" }
    foreach ($f in @($FlakyRecord.rerun.flaky)) {
        $hit = @(@($Clearance.tests) | Where-Object {
            $_.suite -eq $f.suite -and $_.test -eq $f.test -and ([string] $_.resolution -match '^(DELETED|FIXED)')
        })
        if ($hit.Count -eq 0) { return "it does not show the flaky test $($f.suite) $($f.test) as deleted or fixed" }
    }
    return $null
}

function Read-JsonFile($File) {
    try {
        return (Get-Content $File.FullName -Raw | ConvertFrom-Json)
    } catch {
        return $null
    }
}

$refusals = @()
foreach ($file in $records) {
    $record = Read-JsonFile $file
    if ($null -eq $record) {
        $refusals += "$($file.Name): cannot be read as JSON"
        continue
    }

    if ($record.verdict -eq "FLAKY") {
        $reason = Test-FlakyRecord $record
        if ($null -ne $reason) {
            $refusals += "$($file.Name): $reason"
            continue
        }
        $clearances = @()
        if (Test-Path $clearDir) { $clearances = @(Get-ChildItem -Path $clearDir -Filter "$sha-*.json" | Sort-Object Name) }
        if ($clearances.Count -eq 0) {
            $names = (@($record.rerun.flaky) | ForEach-Object { "$($_.suite) $($_.test)" }) -join "; "
            $refusals += "$($file.Name): its failures were FLAKY ($names), and no clearance shows them deleted or fixed on main. Fix or delete them in a pull request, merge it, then run '.\scripts\test-local.ps1 -ClearFlaky $sha' from a clean checkout of main"
            continue
        }
        foreach ($clearFile in $clearances) {
            $clearance = Read-JsonFile $clearFile
            if ($null -eq $clearance) {
                $refusals += "$($file.Name) with $($clearFile.Name): the clearance cannot be read as JSON"
                continue
            }
            $why = Test-Clearance $clearance $record
            if ($null -ne $why) {
                $refusals += "$($file.Name) with clearance $($clearFile.Name): $why"
                continue
            }
            $executed = 0
            foreach ($suite in @($record.suites)) { $executed += [int] $suite.executed }
            Write-Host ""
            Write-Host "GATED: $sha (its only failures were flaky tests, since deleted or fixed on main)"
            Write-Host ("  -Parked Release run started {0}, {1} suites, {2} tests executed, tree clean" -f $record.startedUtc, @($record.suites).Count, $executed)
            foreach ($t in @($clearance.tests)) { Write-Host ("  flaky: {0} {1} -> {2}" -f $t.suite, $t.test, $t.resolution) }
            Write-Host "  cleared at $($clearance.clearedAt), which is on origin/main"
            Write-Host "  record: $($file.FullName)"
            Write-Host "  clearance: $($clearFile.FullName)"
            Write-Host ""
            exit 0
        }
        continue
    }

    $reason = Test-GateRecord $record
    if ($null -eq $reason) {
        $executed = 0
        foreach ($suite in @($record.suites)) { $executed += [int] $suite.executed }
        Write-Host ""
        Write-Host "GATED: $sha"
        Write-Host ("  green -Parked Release run started {0}, {1} suites, {2} tests executed, tree clean" -f $record.startedUtc, @($record.suites).Count, $executed)
        Write-Host ("  by {0} on {1} in {2}" -f $record.session, $record.machine, $record.directory)
        Write-Host "  record: $($file.FullName)"
        Write-Host ""
        exit 0
    }
    $refusals += "$($file.Name): $reason"
}

Refuse ("$($records.Count) run(s) are recorded for $sha and none of them qualifies:`n  " + ($refusals -join "`n  ")) $howToRun
