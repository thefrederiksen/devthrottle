#Requires -Version 5.1
<#
.SYNOPSIS
    Exits 0 only for a commit that the release gate certified on this machine: a green, unfiltered,
    clean-tree run of scripts\test-local.ps1 -Parked -Configuration Release at exactly that commit.

.DESCRIPTION
    scripts\new-release.ps1 -Tag <candidate> calls this before it tags, and refuses the tag when this
    refuses the commit. The release workflow runs no tests and a pushed tag cannot be un-pushed, so this
    is the only thing between an ungated commit and the users.

    WHAT COUNTS AS GATED. The gate writes one record per green, unfiltered -Parked run, named by the
    commit it ran on, in the directory scripts\gate-lock.ps1 derives (per user, per machine). A record
    is accepted only when every one of these holds, and the refusal names the first that does not:
      - its commit is the commit asked about - a run at the parent, or at the pull-request head that
        was squashed into the candidate, is a run at a different commit;
      - it was a -Parked run with no -Filter, so every suite ran, including the three the default run
        skips;
      - its configuration was Release, which is what users download; the gate defaults to Debug;
      - the tree was clean - a run on a dirty tree tested something that is not the commit;
      - it exited 0, and every suite in it reported outcome=Completed with at least one test executed.

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
. (Join-Path $PSScriptRoot "gate-lock.ps1")

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
$howToRun = "Run the release gate on it, in a worktree detached at that commit:`n" +
            "  git worktree add ../devthrottle-gate --detach $sha`n" +
            "  cd ../devthrottle-gate`n" +
            "  .\scripts\test-local.ps1 -Parked -Configuration Release`n" +
            "A green run writes its record to $recordDir and this check then accepts the commit."

$records = @()
if (Test-Path $recordDir) {
    $records = @(Get-ChildItem -Path $recordDir -Filter "$sha-*.json" | Sort-Object Name)
}
if ($records.Count -eq 0) {
    Refuse "no green -Parked run is recorded for $sha on this machine ($recordDir)." $howToRun
}

# Every requirement is checked against the record's own fields, and the first unmet one is the reason.
# Reading the record back is the point: the gate wrote what it saw, and this script does not trust the
# file name alone.
function Test-GateRecord($Record) {
    if ($Record.commit -ne $sha) { return "its commit is $($Record.commit), not $sha" }
    if (-not $Record.parked) { return "it was not a -Parked run, so the three parked suites did not run" }
    if (-not [string]::IsNullOrEmpty($Record.filter)) { return "it was filtered (-Filter '$($Record.filter)'), so not every test ran" }
    if ($Record.configuration -ne "Release") { return "its configuration was $($Record.configuration), not Release" }
    if (-not $Record.treeClean) { return "the tree was dirty ($(@($Record.dirtyFiles).Count) changed or untracked files), so it did not test exactly this commit" }
    if ($Record.exitCode -ne 0) { return "its exit code was $($Record.exitCode), not 0" }
    if ($Record.verdict -ne "GREEN") { return "its verdict was '$($Record.verdict)', not GREEN" }
    $suites = @($Record.suites)
    if ($suites.Count -eq 0) { return "it recorded no suites" }
    foreach ($suite in $suites) {
        if ($suite.outcome -ne "Completed") { return "suite $($suite.name) reported outcome=$($suite.outcome), not Completed" }
        if ([int] $suite.executed -lt 1) { return "suite $($suite.name) executed zero tests" }
    }
    return $null
}

$refusals = @()
foreach ($file in $records) {
    try {
        $record = Get-Content $file.FullName -Raw | ConvertFrom-Json
    } catch {
        $refusals += "$($file.Name): cannot be read as JSON ($($_.Exception.Message))"
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
