#Requires -Version 5.1
<#
.SYNOPSIS
    Run the Gateway's PostgreSQL tests against a throwaway PostgreSQL this script builds, and nothing else.

.DESCRIPTION
    THE ONE PLACE A TEST RUN STARTS A DATABASE. Every test that needs a real PostgreSQL lives in ONE project,
    src\CcDirector.Gateway.DatabaseTests, and this script is the only thing that runs it against one.
    scripts\test-local.ps1 - the everyday gate AND the release gate - starts no database and needs no Docker.

    WHY THE SPLIT. Until October 2026 these tests lived inside CcDirector.Gateway.Tests and
    CcDirector.Gateway.UnitTests, so `test-local.ps1 -Parked` had to build a database for two suites that are
    otherwise about something else, and needed Docker for all of it. The two test processes then shared one rig
    at the same time: one dropped and rebuilt the statistics schema while the other was reading it. Two runs of
    the database tests can no longer share anything - each builds its own rig - and one run executes its tests
    one at a time, in one process.

    WHAT IT DOES, IN ORDER:
      1. Stops at once, with exit 6, if Docker is not running. There is no fallback: these tests would SKIP,
         and a skip reads exactly like a pass in every report built from a run.
      2. Removes rigs an earlier run of THIS script left behind, and only those (see Remove-AbandonedRigs).
      3. Builds a throwaway PostgreSQL with scripts\pg-stats-proof-rig.ps1, under a fresh instance name and a
         free port, and hands its two connection strings to the test process. Whatever the machine has in its
         user environment is ignored.
      4. Sets CC_TEST_REQUIRE_POSTGRES to the rig's instance name. That is the PROMISE: PostgresRigGate refuses
         to let the test assembly load unless both connections lead to the exact databases, and the restricted
         role, this run built.
      5. Builds and runs ONLY the database project, writing a result file.
      6. Judges the run from the result file, not from the console line, and destroys the rig whatever happened.

    WHAT COUNTS AS GREEN - every one of these, stated as something that must be TRUE:
      - the result file exists and says the run finished (outcome Completed), with no failed test;
      - at least one test executed;
      - NO test was skipped. Under this script every test here has its database, so a skip is a defect, not a
        convenience - it is a test whose rule stopped matching the rig, and it proves nothing;
      - the rig is still running when the run ends, so nothing ran against a database that had died.

    EXIT CODES (the same meanings scripts\test-local.ps1 uses where they overlap):
      0  green, by every rule above
      1  build failed, or a test failed
      2  bad arguments
      3  zero tests collected
      4  no result file - the test host did not report a run (the rig guard refusing to load is one cause)
      6  the database could not be built: Docker absent, the rig failed, or its output could not be read
      7  the database this run built is gone by the end of the run
      8  a test was skipped, or nothing executed
      9  the run did not finish (aborted, timed out)

.PARAMETER Filter
    An xUnit filter passed to the run (e.g. "FullyQualifiedName~Teams"). Every rule above still applies:
    a filter that skips or executes nothing is not a pass.

.PARAMETER Configuration
    Debug (default) or Release.

.EXAMPLE
    .\scripts\test-database.ps1
    .\scripts\test-database.ps1 -Filter "FullyQualifiedName~HostedSchemaRefusesAnUnownedRowTests"
#>
param(
    [string]$Filter = "",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\CcDirector.Gateway.DatabaseTests\CcDirector.Gateway.DatabaseTests.csproj"
$projectName = "CcDirector.Gateway.DatabaseTests"

# The label every rig this script creates carries, and the ONLY thing the abandoned-rig sweep will act on.
$RigLabel = 'cc-test-database-rig'
# The owning process id, stamped on every rig this script starts. It is what lets a later run prove a leftover
# container is nobody's, rather than guessing from its age.
$RigOwnerLabel = 'cc-test-database-rig-owner'

# The labels test-local.ps1 stamped on its rigs before October 2026, when it built the database itself. Nothing
# else looks for them any more, so a rig a killed -Parked run left behind is swept here, by the same rule: only
# when its owner stamp names a process that is gone. Remove these once no such rig can still exist.
$LegacyRigLabel = 'cc-test-local-rig'
$LegacyRigOwnerLabel = 'cc-test-local-rig-owner'

# The rig this run must destroy when it ends, whatever way it ends. Null until one is started.
$script:RigToTearDown = $null

# WHAT THE CALLER'S ENVIRONMENT HELD BEFORE THIS RUN TOUCHED IT, so the finally can put it back.
#
# Run the documented way, `.\scripts\test-database.ps1` from a shell you already have open, this script's
# process IS the caller's shell. Without the restore, the variables outlive the run while the database they
# name is destroyed by the same run - and the next `dotnet test` in that shell sees a promise of a database
# that no longer exists and refuses to load the assembly. It RESTORES rather than blanks, because a developer
# may have set these deliberately.
$script:PriorRigEnvironment = $null
$script:RigEnvironmentVars = @(
    "CC_GATEWAY_TEST_PG_CONNECTION",
    "CC_GATEWAY_TEST_PG_STATS_CONNECTION",
    "CC_TEST_REQUIRE_POSTGRES"
)

<#
    Run a docker command and hand back its exit code and output, without letting it terminate the script.

    Under $ErrorActionPreference = 'Stop', Windows PowerShell 5.1 turns anything a NATIVE executable writes to
    stderr into a terminating error, so `& docker ...` followed by a check of $LASTEXITCODE never reaches the
    check, and every tailored message and exit code below would be unreachable. Every docker call goes through
    here so that decision is made once.
#>
function Invoke-Docker {
    param([Parameter(Mandatory = $true)][string[]] $DockerArgs)

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = (& docker @DockerArgs 2>&1 | Out-String)
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
    }
    catch {
        return [pscustomobject]@{ ExitCode = -1; Output = $_.Exception.Message }
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

function Restore-RigEnvironment {
    if ($null -eq $script:PriorRigEnvironment) { return }
    foreach ($name in $script:RigEnvironmentVars) {
        [Environment]::SetEnvironmentVariable($name, $script:PriorRigEnvironment[$name])
    }
    $script:PriorRigEnvironment = $null
}

<#
    Destroy this run's rig. Called from the finally block at the foot of the script, which runs while a real
    failure may be on its way out - so NOTHING in here may throw, and nothing in here may change the exit code.
    The target is cleared only when the removal succeeded; on failure it is reported with the exact command.
#>
function Stop-RigForThisRun {
    if ($null -eq $script:RigToTearDown) { return }
    $rig = $script:RigToTearDown

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & powershell -NoProfile -File $rig.Script -Instance $rig.Instance -Port $rig.Port -Verb down *>&1 |
            Out-Null
        if ($LASTEXITCODE -ne 0) {
            # Second attempt, by the one container name this run created. The sweep would not help here: it
            # removes a rig whose OWNING PROCESS is gone, and run the documented way the owning process is the
            # operator's own shell, which outlives the run.
            $retry = Invoke-Docker @("rm", "-f", "-v", "cc-pg-stats-proof-$($rig.Instance)")
            $global:LASTEXITCODE = $retry.ExitCode
        }

        if ($LASTEXITCODE -eq 0) {
            $script:RigToTearDown = $null
        }
        else {
            Write-Host ""
            Write-Host "WARNING: the throwaway PostgreSQL for this run could not be removed."
            Write-Host "         It is holding port $($rig.Port). Remove it with:"
            Write-Host "           docker rm -f -v cc-pg-stats-proof-$($rig.Instance)"
            Write-Host "         This run's own result above is unaffected."
        }
    }
    catch {
        Write-Host ""
        Write-Host "WARNING: removing the throwaway PostgreSQL threw: $($_.Exception.Message)"
        Write-Host "         Remove it with: docker rm -f -v cc-pg-stats-proof-$($rig.Instance)"
        Write-Host "         This run's own result above is unaffected."
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

function Get-FreeTcpPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return $listener.LocalEndpoint.Port } finally { $listener.Stop() }
}

<#
    Remove rigs an EARLIER run of this script left behind, and nothing else.

    A run that never reaches its teardown - killed, Ctrl+C, a reboot - would otherwise leave its container
    holding a port forever. This leans hard toward keeping: it enumerates what to DELETE by a positive test.

    THE TEST IS "THE PROCESS THAT CREATED IT IS GONE". Every rig this script starts is stamped with the owning
    process id, so a container whose owner is no longer running is provably nobody's. A process id can be
    reused by an unrelated process, and that error only ever points one way: a recycled id reads as alive, so
    the container is KEPT. A container with no readable owner stamp is kept too - it is not this script's.
#>
function Remove-AbandonedRigs {
    # The whole label set is asked for as ONE field and parsed here: a Go template carrying quoted strings has
    # to survive PowerShell's quoting and then Docker's own parser, and it did not.
    foreach ($pair in @(@($RigLabel, $RigOwnerLabel), @($LegacyRigLabel, $LegacyRigOwnerLabel))) {
        $sweepLabel = $pair[0]
        $ownerKey = $pair[1]
        $listed = Invoke-Docker @("ps", "-a", "--filter", "label=$sweepLabel", "--format", "{{.ID}}|{{.Labels}}")
        if ($listed.ExitCode -ne 0) { continue }
        $rows = @($listed.Output -split "`r?`n" | Where-Object { $_.Trim() -ne "" })

        foreach ($row in $rows) {
            $parts = "$row" -split '\|', 2
            if ($parts.Count -lt 2) { continue }
            $id = $parts[0]

            $ownerPid = ""
            foreach ($label in ($parts[1] -split ',')) {
                $kv = $label -split '=', 2
                if ($kv.Count -eq 2 -and $kv[0].Trim() -eq $ownerKey) { $ownerPid = $kv[1].Trim() }
            }

            if ($ownerPid -notmatch '^\d+$') { continue }
            if ($null -ne (Get-Process -Id ([int]$ownerPid) -ErrorAction SilentlyContinue)) { continue }

            Write-Host "Removing abandoned database test rig $id - the run that created it (process $ownerPid) is gone."
            $removal = Invoke-Docker @("rm", "-f", "-v", $id)
            if ($removal.ExitCode -ne 0) {
                Write-Host "WARNING: that rig could NOT be removed (docker exited $($removal.ExitCode)):"
                Write-Host "         $($removal.Output.Trim())"
            }
        }
    }
}

# EVERYTHING FROM HERE IS INSIDE A try/finally, SO THE THROWAWAY DATABASE IS DESTROYED WHATEVER HAPPENS.
# PowerShell runs a finally block on a normal end, on a terminating error, AND on every `exit`, with the exit
# code preserved. It opens BEFORE the rig is built, so a rig that fails half way through provisioning is torn
# down as readily as a healthy one. A process that is KILLED outright runs no finally block; that case belongs
# to Remove-AbandonedRigs at the start of the next run.
try {
    $dockerCheck = Invoke-Docker @("version", "--format", "{{.Server.Version}}")
    if ($dockerCheck.ExitCode -ne 0) {
        Write-Host ""
        Write-Host "RESULT: CANNOT RUN - Docker is not available, and these tests need a PostgreSQL server."
        Write-Host "  Start Docker Desktop and run this again."
        Write-Host ""
        Write-Host "This is deliberately fatal rather than a skip. A skipped database test reads exactly like a"
        Write-Host "passed one in every report built from a run."
        exit 6
    }

    $script:PriorRigEnvironment = @{}
    foreach ($name in $script:RigEnvironmentVars) {
        $script:PriorRigEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
    }

    Remove-AbandonedRigs

    $rigScript = Join-Path $PSScriptRoot "pg-stats-proof-rig.ps1"
    $rigInstance = "db" + [Guid]::NewGuid().ToString("N").Substring(0, 8)
    $rigPort = Get-FreeTcpPort
    Write-Host "Starting a throwaway PostgreSQL for this run (instance $rigInstance, port $rigPort)..."

    # Recorded BEFORE the rig exists, so the finally tears down a container that failed half way through
    # provisioning. A try/finally and not an engine event: an event fires only when the ENGINE exits, and run
    # from an open shell the engine outlives the script.
    $script:RigToTearDown = @{ Script = $rigScript; Instance = $rigInstance; Port = $rigPort }

    # Under Continue, as every docker call here is. The rig script fails by THROWING, which writes to stderr,
    # and under Stop Windows PowerShell 5.1 turns that into a terminating error in THIS script - so the message
    # and exit 6 below were unreachable and the run exited 1, the code for a failed test. Found in review.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $rigOutput = (& powershell -NoProfile -File $rigScript -Instance $rigInstance -Port $rigPort -Verb up `
            -Label $RigLabel -OwnerLabel "$RigOwnerLabel=$PID" 2>&1 | Out-String)
        $rigExit = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    if ($rigExit -ne 0) {
        Write-Host ""
        Write-Host "RESULT: CANNOT RUN - the throwaway PostgreSQL for this run could not be provisioned."
        Write-Host "  The rig said:"
        foreach ($l in ($rigOutput -split "`r?`n" | Where-Object { $_.Trim() -ne "" })) { Write-Host "    $l" }
        Write-Host "  Re-run the rig by hand to see why:"
        Write-Host "    powershell -NoProfile -File scripts\pg-stats-proof-rig.ps1 -Instance $rigInstance -Port $rigPort -Verb up"
        exit 6
    }

    # The connection strings come from the rig itself, so exactly one place knows the database names, the role
    # and the password. The inherited values are cleared first and EVERY line must be understood: a line this
    # cannot parse is a change in the rig, and skipping it would leave one variable naming a stale database
    # from an older rig while the other names the new one.
    $expectedVars = @("CC_GATEWAY_TEST_PG_CONNECTION", "CC_GATEWAY_TEST_PG_STATS_CONNECTION")
    foreach ($name in $expectedVars) { [Environment]::SetEnvironmentVariable($name, $null) }

    # Stdout only (the assignments); stderr is kept apart so a warning cannot be read as a line to parse.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $envLines = & powershell -NoProfile -File $rigScript -Instance $rigInstance -Port $rigPort -Verb print-env 2>$null
        $printExit = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    if ($printExit -ne 0) {
        Write-Host ""
        Write-Host "RESULT: CANNOT RUN - the rig started but print-env failed (exit $printExit). See why with:"
        Write-Host "    powershell -NoProfile -File scripts\pg-stats-proof-rig.ps1 -Instance $rigInstance -Port $rigPort -Verb print-env"
        exit 6
    }
    $seenVars = @{}
    foreach ($line in $envLines) {
        if ([string]::IsNullOrWhiteSpace("$line")) { continue }
        if ("$line" -match '^\s*\$env:([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?:''([^'']*)''|"([^"]*)")\s*$') {
            $name = $Matches[1]
            $value = if ($null -ne $Matches[2] -and $Matches[2] -ne "") { $Matches[2] } else { $Matches[3] }
            [Environment]::SetEnvironmentVariable($name, $value)
            $seenVars[$name] = $true
        }
        else {
            Write-Host ""
            Write-Host "RESULT: CANNOT RUN - the rig's print-env produced a line this script cannot read:"
            Write-Host "    $line"
            Write-Host "  pg-stats-proof-rig.ps1 and this script disagree about that output's shape."
            Write-Host "  Fix them together; a line skipped here means a test running against the wrong database."
            exit 6
        }
    }

    $missingVars = @($expectedVars | Where-Object { -not $seenVars.ContainsKey($_) })
    if ($missingVars.Count -gt 0) {
        Write-Host ""
        Write-Host "RESULT: CANNOT RUN - the rig started but named no value for: $($missingVars -join ', ')"
        exit 6
    }

    # THE PROMISE. It carries the rig's INSTANCE NAME, from which every database and role the rig creates is
    # derived, so PostgresRigGate can hold the tests to the exact databases this run built - from a module
    # initializer, where -Filter cannot exclude it - and the shared [RequiresPostgresFact] runs every test.
    $env:CC_TEST_REQUIRE_POSTGRES = $rigInstance
    Write-Host "PostgreSQL ready. It is destroyed when this run ends."
    Write-Host ""

    Write-Host "Building $projectName ($Configuration)..."
    & dotnet build $project -c $Configuration -v q --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "RESULT: BUILD FAILED - no tests were run."
        exit 1
    }

    $logDir = Join-Path ([System.IO.Path]::GetTempPath()) ("cc-test-database-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    $trx = Join-Path $logDir "$projectName.trx"

    # In the foreground, with its output on the console as it runs. One project and one process: there is
    # nothing to run beside it, and a run a person can watch is a run a person can tell from a hang.
    $testArgs = @("test", $project, "--no-build", "-c", $Configuration, "--nologo",
                  "--logger", "trx;LogFileName=$projectName.trx", "--results-directory", $logDir)
    if ($Filter -ne "") { $testArgs += @("--filter", $Filter) }

    Write-Host "Running $projectName..."
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & dotnet @testArgs } finally { $ErrorActionPreference = $previous }
    $testExit = $LASTEXITCODE

    Write-Host ""
    Write-Host "Result file: $trx"

    # THE VERDICT IS READ FROM THE RESULT FILE. The console's "Passed!" line is printed by a run that passed
    # everything it managed to start; the file says whether the run finished and how many tests executed.
    if (-not (Test-Path $trx)) {
        Write-Host ""
        Write-Host "RESULT: NO RESULT FILE - the test host did not report a run (dotnet test exited $testExit)."
        Write-Host "  If the output above says 'This run set CC_TEST_REQUIRE_POSTGRES', the rig guard refused to"
        Write-Host "  load the assembly because a connection did not lead to the database this run built."
        exit 4
    }

    [xml] $doc = Get-Content $trx -Raw
    $outcome = [string] $doc.TestRun.ResultSummary.outcome
    $counters = $doc.TestRun.ResultSummary.Counters
    $total = [int] $counters.total
    $executed = [int] $counters.executed
    $passed = [int] $counters.passed
    $failedCount = [int] $counters.failed
    # SKIPS ARE COUNTED FROM THE RESULT ROWS, NOT FROM A SUMMARY COUNTER. The result file's notExecuted
    # counter reads 0 for a run in which xUnit skipped tests - the skips appear only as rows whose outcome is
    # NotExecuted, and as the gap between total and executed. The first version of this script read the
    # counter, and a run with a skipped test printed "none skipped" and exited 0: found in review, and
    # reproduced through this script with a deliberately skipped test before it was fixed. Both measures are
    # taken, and the larger wins, so neither can hide a skip the other sees.
    $skippedRows = @(@($doc.TestRun.Results.UnitTestResult) |
        Where-Object { $null -ne $_ -and [string]$_.outcome -eq "NotExecuted" })
    $notExecuted = [Math]::Max($skippedRows.Count, $total - $executed)
    Write-Host ("Verdict: outcome={0} total={1} executed={2} passed={3} failed={4} skipped={5}" -f `
        $outcome, $total, $executed, $passed, $failedCount, $notExecuted)

    # The rig must still be RUNNING. A database that died mid-run means every test after that moment ran
    # against nothing, whatever the counts say.
    $rigContainer = "cc-pg-stats-proof-$rigInstance"
    $probe = Invoke-Docker @("ps", "-q", "--filter", "name=$rigContainer")
    $stillRunning = @($probe.Output -split "`r?`n" | Where-Object { $_.Trim() -ne "" })
    if ($probe.ExitCode -ne 0 -or $stillRunning.Count -eq 0) {
        Write-Host ""
        Write-Host "RESULT: THE DATABASE THIS RUN BUILT IS GONE - the run is not evidence for anything."
        Write-Host "  '$rigContainer' was provisioned for this run and is no longer running."
        Write-Host "  Inspect it before it is removed:  docker logs $rigContainer"
        exit 7
    }

    # Finished means Completed (all passed) or Failed (finished, something failed). Anything else stopped part
    # way through, and its remaining tests were never reached.
    if (@("Completed", "Failed") -notcontains $outcome) {
        Write-Host ""
        Write-Host "RESULT: THE RUN DID NOT FINISH (outcome=$outcome) - it is not a verdict on anything."
        exit 9
    }

    if ($total -eq 0) {
        Write-Host ""
        Write-Host "RESULT: ZERO TESTS COLLECTED - nothing ran, so this is not a pass."
        if ($Filter -ne "") { Write-Host "  No test matched the filter: $Filter" }
        exit 3
    }

    if ($failedCount -gt 0 -or $outcome -eq "Failed" -or $testExit -ne 0) {
        Write-Host ""
        Write-Host "RESULT: FAILED - $failedCount test(s) failed (dotnet test exited $testExit). The output is above."
        exit 1
    }

    # A SKIP IS A DEFECT HERE. Every test in this project has its database under this script, so a test that
    # skipped is one whose rule no longer matches the rig - and a skip proves nothing.
    if ($notExecuted -gt 0 -or $executed -eq 0) {
        Write-Host ""
        Write-Host "RESULT: $notExecuted TEST(S) SKIPPED, $executed EXECUTED - this is not a pass."
        Write-Host "  This script built the database every test here needs, so nothing should skip. A skipped"
        Write-Host "  test here is one whose rule stopped matching the rig."
        foreach ($u in $skippedRows) { Write-Host "    $($u.testName)" }
        exit 8
    }

    Write-Host ""
    Write-Host "RESULT: GREEN - $executed test(s) executed against the throwaway PostgreSQL, none failed, none skipped."
    exit 0
}
finally {
    Stop-RigForThisRun
    Restore-RigEnvironment
}
