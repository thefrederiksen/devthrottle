<#
.SYNOPSIS
    The lock-removal qualification soak (issue #1156 step 4).

    Runs N ordinary Gateway suite processes CONCURRENTLY from separate worktrees, each with its own
    results file and console log, using the controlled GatewayTestSuiteLock bypass. The point is to
    accumulate evidence that concurrent runs no longer corrupt each other, so the machine-wide lock
    can come off (step 5). ONE GREEN PAIR IS NOT PROOF - the historical race was intermittent. Run
    this repeatedly (use -Rounds, or rerun the script) until the ledger covers thousands of host
    starts.

    The soak holds the release-gate lock and the Gateway suite lock (scripts\gate-lock.ps1) for its
    whole run. If either is held when it starts, it stops at once with exit 6 naming the holder; it
    never queues. While it holds them, every other run that includes the Gateway suite is refused.

.DESCRIPTION
    What one round does:
      1. Ensures N worktrees exist under -WorkRoot, detached at the SAME commit this repo is on,
         and builds the Gateway test project in each (once - later rounds reuse the build).
      2. Launches N 'dotnet test --no-build' processes at once. Each child gets:
           CC_GATEWAY_TEST_LOCK_QUALIFICATION=isolated-worktree-soak   (the bypass token)
           CC_GATEWAY_TEST_PG_CONNECTION=                              (cleared - fail-closed pair)
           CC_GATEWAY_TEST_PG_STATS_CONNECTION=                        (cleared)
           CC_GATEWAY_DB_CONNECTION=                                   (cleared)
         The lock itself refuses to bypass if any live-proof variable leaks through - a forgotten
         variable is a loud stop, never quiet false evidence.
      3. Judges every process by its TRX file: ResultSummary outcome must be 'Completed' AND the
         total/executed/passed counters must be IDENTICAL across all N siblings (same tree, same
         filter - any divergence is exactly the cross-process interference this soak hunts).
      4. Appends one JSON line per process to the ledger (qual-ledger.jsonl in -WorkRoot), plus a
         round summary with the running host-start estimate.

    Host-start accounting: the Gateway suite boots a real GatewayHost in roughly 106 fixture files
    plus per-test hosts; the ledger books a conservative 106 host starts per completed process run.
    The number to beat is in issue #1156: thousands.

.PARAMETER Processes
    Concurrent suite processes per round. Start with 2; qualify with 4.

.PARAMETER Rounds
    How many rounds to run back to back in this invocation.

.PARAMETER WorkRoot
    Where the qualification worktrees and the ledger live. Default: <repo-parent>\dt-qual.

.PARAMETER TearDown
    Remove the qualification worktrees after the last round (the ledger is kept).

.EXAMPLE
    .\scripts\test-qualification.ps1 -Processes 2 -Rounds 3
.EXAMPLE
    .\scripts\test-qualification.ps1 -Processes 4 -Rounds 5
#>
[CmdletBinding()]
param(
    [ValidateRange(2, 8)]
    [int] $Processes = 2,

    [ValidateRange(1, 100)]
    [int] $Rounds = 1,

    [string] $WorkRoot = "",

    [switch] $TearDown
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'gate-lock.ps1')

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$TestProject = 'src\CcDirector.Gateway.Tests\CcDirector.Gateway.Tests.csproj'
$QualificationToken = 'isolated-worktree-soak'
$HostStartsPerRun = 106   # fixture files that boot a real GatewayHost; deliberately conservative

if ([string]::IsNullOrWhiteSpace($WorkRoot)) {
    $WorkRoot = Join-Path (Split-Path $RepoRoot -Parent) 'dt-qual'
}
$Ledger = Join-Path $WorkRoot 'qual-ledger.jsonl'

function Write-Step([string] $Message) {
    Write-Host ("[qual] " + $Message)
}

# Every native call goes through cmd.exe with the redirection INSIDE cmd. Under Windows PowerShell 5.1
# with $ErrorActionPreference='Stop', a native command's stderr - even git's ordinary progress lines -
# becomes a terminating NativeCommandError the moment it is redirected in PowerShell (and when the host
# process's own stderr is a pipe, which is exactly how agents run this script). cmd absorbs all of it
# and hands back plain strings plus an exit code.
function Invoke-Native([string] $CommandLine) {
    $out = & $env:ComSpec /d /c "$CommandLine 2>&1"
    $script:NativeExit = $LASTEXITCODE
    return $out
}

function Get-Commit {
    $c = [string] (Invoke-Native "git -C `"$RepoRoot`" rev-parse HEAD")
    $c = $c.Trim()
    if ($script:NativeExit -ne 0 -or [string]::IsNullOrWhiteSpace($c)) {
        throw "Could not resolve HEAD in $RepoRoot"
    }
    return $c
}

function Ensure-Worktree([int] $Index, [string] $Commit) {
    $path = Join-Path $WorkRoot ("p" + $Index)
    if (Test-Path (Join-Path $path $TestProject)) {
        $null = Invoke-Native "git -C `"$path`" checkout --detach $Commit"
        if ($script:NativeExit -ne 0) { throw "Worktree $path could not check out $Commit" }
        return $path
    }
    # An interrupted earlier run can leave any combination of: the directory without registration,
    # the registration without the directory, and a LOCK on the registration (which prune ignores).
    # Clear all three unconditionally - each step is a no-op when its half is absent.
    $null = Invoke-Native "git -C `"$RepoRoot`" worktree unlock `"$path`""
    $null = Invoke-Native "git -C `"$RepoRoot`" worktree remove `"$path`" --force"
    if (Test-Path $path) {
        Write-Step "Clearing incomplete worktree $path"
        Remove-Item -Recurse -Force $path
    }
    $null = Invoke-Native "git -C `"$RepoRoot`" worktree prune"
    New-Item -ItemType Directory -Force (Split-Path $path -Parent) | Out-Null
    Write-Step "Creating worktree $path at $($Commit.Substring(0,9))"
    $out = Invoke-Native "git -C `"$RepoRoot`" worktree add --detach `"$path`" $Commit"
    if ($script:NativeExit -ne 0) { throw "git worktree add failed for ${path}: $out" }
    return $path
}

function Build-Worktree([string] $Path) {
    $marker = Join-Path $Path '.qual-built-commit'
    $commit = ([string] (Invoke-Native "git -C `"$Path`" rev-parse HEAD")).Trim()
    if ((Test-Path $marker) -and ((Get-Content $marker -TotalCount 1) -eq $commit)) {
        return
    }
    Write-Step "Building $Path (once per commit)"
    $buildLog = Join-Path $WorkRoot ("build-" + (Split-Path $Path -Leaf) + ".log")
    $null = Invoke-Native "dotnet build `"$(Join-Path $Path $TestProject)`" --nologo -v q > `"$buildLog`""
    if ($script:NativeExit -ne 0) { throw "Build failed in $Path - see $buildLog" }
    Set-Content -Path $marker -Value $commit -Encoding Ascii
}

function Read-Trx([string] $TrxPath) {
    [xml] $trx = Get-Content $TrxPath -Raw
    $summary = $trx.TestRun.ResultSummary
    $c = $summary.Counters
    return [pscustomobject]@{
        Outcome  = [string] $summary.outcome
        Total    = [int] $c.total
        Executed = [int] $c.executed
        Passed   = [int] $c.passed
        Failed   = [int] $c.failed
    }
}

function Acquire-GateLocks([string] $Commit) {
    # The parent holds BOTH locks for the whole soak: the release-gate lock, so no gate run that includes
    # the Gateway suite starts beside it, and the suite's own lock, so no hand-run "dotnet test" of the
    # suite joins the children (which bypass that lock by design). A held lock is a refusal, never a
    # queue - the same rule as scripts\test-local.ps1 and for the same reason (see scripts\gate-lock.ps1):
    # until October 2026 this waited up to 45 minutes, and a queue hides the conflict it is in.
    $fields = [ordered]@{
        session = (Get-GateLockSessionName); commit = $Commit
        command = "test-qualification.ps1 -Processes $Processes -Rounds $Rounds"; directory = $RepoRoot
    }
    $gatePath = Get-ReleaseGateLockPath
    $gate = Enter-GateLock $gatePath $fields
    if ($null -eq $gate) {
        Write-Step ("REFUSED: another run that includes the Gateway suite is in progress on this machine. Holder: " +
            (Format-GateLockHolder (Read-GateLockHolder $gatePath)) + ". Lock file: $gatePath. Wait for it to finish and run the soak again.")
        exit 6
    }
    $suitePath = Get-GatewaySuiteLockPath
    $suite = Enter-GateLock $suitePath $fields
    if ($null -eq $suite) {
        Exit-GateLock $gate
        Write-Step ("REFUSED: a run of CcDirector.Gateway.Tests is in progress on this machine. Holder: " +
            (Format-GateLockHolder (Read-GateLockHolder $suitePath)) + ". Lock file: $suitePath. Wait for it to finish and run the soak again.")
        exit 6
    }
    Write-Step "Holding the release-gate lock and the suite lock for the whole soak; other runs are refused, not queued."
    return @{ Gate = $gate; Suite = $suite }
}

function Get-LedgerHostStarts {
    if (-not (Test-Path $Ledger)) { return 0 }
    $sum = 0
    foreach ($line in Get-Content $Ledger) {
        try {
            $row = $line | ConvertFrom-Json
            if ($row.kind -eq 'process' -and $row.outcome -eq 'Completed') { $sum += $HostStartsPerRun }
        } catch { }
    }
    return $sum
}

# ---- provisioning ----

$commit = Get-Commit
New-Item -ItemType Directory -Force $WorkRoot | Out-Null
$worktrees = @()
for ($i = 1; $i -le $Processes; $i++) { $worktrees += Ensure-Worktree -Index $i -Commit $commit }
foreach ($w in $worktrees) { Build-Worktree -Path $w }

# ---- rounds ----

$locks = Acquire-GateLocks -Commit $commit
$anyFailure = $false
try {
for ($round = 1; $round -le $Rounds; $round++) {
    $stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
    Write-Step "Round $round of $Rounds : launching $Processes concurrent suite runs (commit $($commit.Substring(0,9)))"

    $running = @()
    for ($i = 1; $i -le $Processes; $i++) {
        $w = $worktrees[$i - 1]
        $tag = "r$round-p$i-$stamp"
        $trxName = "qual-$tag.trx"
        $log = Join-Path $WorkRoot "qual-$tag.log"
        $cmdFile = Join-Path $WorkRoot "qual-$tag.cmd"
        $exitFile = Join-Path $WorkRoot "qual-$tag.exit"

        # A per-process launcher file, so each child gets EXACTLY this environment: the bypass token
        # set, and every live-proof connection variable cleared ('set NAME=' unsets in cmd). The lock
        # fails closed if one leaks through anyway.
        $lines = @(
            '@echo off',
            "set CC_GATEWAY_TEST_LOCK_QUALIFICATION=$QualificationToken",
            'set CC_GATEWAY_TEST_PG_CONNECTION=',
            'set CC_GATEWAY_TEST_PG_STATS_CONNECTION=',
            'set CC_GATEWAY_DB_CONNECTION=',
            "cd /d `"$w`"",
            "dotnet test $TestProject --no-build --nologo --logger `"trx;LogFileName=$trxName`" > `"$log`" 2>&1",
            # The child's exit code, written where the parent can read it even if the parent died. It is
            # the ONLY artefact that separates a silent Environment.Exit(0) from a crash from an external
            # kill - a distinction a vanished test host does not otherwise offer (issue #1203).
            "echo %ERRORLEVEL% > `"$exitFile`"",
            'exit /b %ERRORLEVEL%'
        )
        Set-Content -Path $cmdFile -Value $lines -Encoding Ascii

        $p = Start-Process -FilePath $env:ComSpec -ArgumentList '/c', "`"$cmdFile`"" -PassThru -WindowStyle Hidden
        $running += [pscustomobject]@{ Index = $i; Process = $p; Worktree = $w; TrxName = $trxName; Log = $log; ExitFile = $exitFile }
    }

    $running | ForEach-Object { $_.Process.WaitForExit() }

    # ---- judge the round: TRX outcome and counters, never the console ----
    $results = @()
    $exitCodes = @()
    foreach ($r in $running) {
        $childExit = if (Test-Path $r.ExitFile) { (Get-Content $r.ExitFile -TotalCount 1).Trim() } else { 'unrecorded' }
        $exitCodes += $childExit

        $trxPath = Join-Path $r.Worktree ("src\CcDirector.Gateway.Tests\TestResults\" + $r.TrxName)
        if (-not (Test-Path $trxPath)) {
            $anyFailure = $true
            Write-Step ("FAILURE round $round p$($r.Index): NO TRX WAS WRITTEN (exit code $($r.Process.ExitCode)). " +
                "A vanished results file is a crashed or refused host, never a pass. Console: $($r.Log)")
            $results += [pscustomobject]@{ Outcome = 'NoTrx'; Total = 0; Executed = 0; Passed = 0; Failed = 0 }
            continue
        }
        $results += Read-Trx $trxPath
    }

    $reference = $results | Where-Object { $_.Outcome -eq 'Completed' } | Select-Object -First 1
    for ($i = 0; $i -lt $results.Count; $i++) {
        $res = $results[$i]
        $verdict = 'pass'
        if ($res.Outcome -ne 'Completed') { $verdict = 'fail-outcome'; $anyFailure = $true }
        elseif ($null -ne $reference -and ($res.Total -ne $reference.Total -or $res.Executed -ne $reference.Executed)) {
            # Same commit, same filter, sibling processes: any counter divergence is interference.
            $verdict = 'fail-divergence'; $anyFailure = $true
        }
        elseif ($res.Failed -gt 0) { $verdict = 'fail-tests'; $anyFailure = $true }

        $row = [ordered]@{
            kind = 'process'; utc = $stamp; commit = $commit; round = $round; process = ($i + 1)
            outcome = $res.Outcome; total = $res.Total; executed = $res.Executed
            passed = $res.Passed; failed = $res.Failed; verdict = $verdict; childExit = $exitCodes[$i]
        }
        Add-Content -Path $Ledger -Value (([pscustomobject]$row) | ConvertTo-Json -Compress) -Encoding Ascii
        Write-Step "round $round p$($i + 1): outcome=$($res.Outcome) total=$($res.Total) passed=$($res.Passed) failed=$($res.Failed) exit=$($exitCodes[$i]) verdict=$verdict"
    }

    $hostStarts = Get-LedgerHostStarts
    $roundRow = [ordered]@{
        kind = 'round'; utc = $stamp; commit = $commit; round = $round; processes = $Processes
        cumulativeHostStartsEstimate = $hostStarts
    }
    Add-Content -Path $Ledger -Value (([pscustomobject]$roundRow) | ConvertTo-Json -Compress) -Encoding Ascii
    Write-Step "Ledger now covers an estimated $hostStarts host starts ($Ledger)"

    if ($anyFailure) {
        Write-Step "STOPPING after round $round : a failure or divergence is the soak's most valuable output. Investigate before running more."
        break
    }
}

} finally {
    Exit-GateLock $locks.Suite
    Exit-GateLock $locks.Gate
    Write-Step "Released the suite lock and the release-gate lock."
}

# ---- teardown ----
if ($TearDown) {
    foreach ($w in $worktrees) {
        Write-Step "Removing worktree $w"
        $null = Invoke-Native "git -C `"$RepoRoot`" worktree remove `"$w`" --force"
    }
}

if ($anyFailure) { exit 1 }
Write-Step "All rounds green. Remember: the deliverable is the accumulated ledger, not one invocation."
exit 0
