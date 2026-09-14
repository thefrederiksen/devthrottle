#Requires -Version 5.1
<#
.SYNOPSIS
    Run this repository's tests locally. THE one command - do not hand-roll a dotnet test invocation.

.DESCRIPTION
    THE DEFAULT RUN IS ABOUT TWO MINUTES, AND THAT IS THE POINT. Local is the gate for ordinary changes
    (issue #1156), so the gate has to be cheap enough that nobody is tempted to skip it.

    WHAT THE DEFAULT RUNS: every suite that finishes inside the two-minute budget, PLUS the two installer
    test projects. They start together and the wall clock is the slowest of them, not the sum.

    COUNTS, MEASURED 2026-09-13 FROM THE TRX FILES OF A FULL RUN - not estimated, and not copied forward:
      Avalonia 423    Launcher 191    HostedAgent 88    Core.UnitTests 278    Engine 63    Terminal 25
      installer: setup.Tests 25, setup-engine.Tests 541
    1068 in the default suites, 1634 including the installer.

    Gateway.UnitTests (4,267) LEFT this list on 2026-09-13 - see the parked block below and issue #2824.
    Re-measure before changing these numbers; the per-project comments below were carried forward for
    months after they stopped being true.

    The installer projects live outside cc-director.sln and are built separately here. They are in the
    default run because they are fast and because the thing they cover - the first screen a new user
    ever sees - was running nowhere locally at all.

    WHAT THE DEFAULT NO LONGER RUNS - AND THIS IS DELIBERATE, NOT AN OVERSIGHT. Three suites are PARKED
    behind -Parked because none can meet the budget:

      CcDirector.Gateway.Tests   - serializes machine-wide (GatewayTestSuiteLock), so its cost is not its
                                   own runtime but the QUEUE behind every other working tree on the
                                   machine. On 2026-08-02 it burned two waits of 45 minutes that executed
                                   ZERO tests, and aborted a third. Its pure tests were split out into
                                   CcDirector.Gateway.UnitTests, which was in the default run until
                                   2026-09-13 and is now parked beside it (issue #2824); what is parked
                                   here is the host-bound remainder.
      CcDirector.Core.Tests      - 11 minutes on a quiet machine and 33 with the fleet busy. Nothing is
                                   wrong with it; it is simply far outside the budget.

    THE TRADE, STATED PLAINLY SO NOBODY DISCOVERS IT THE HARD WAY: those three suites hold real coverage,
    including the Gateway's host-bound endpoint, tenancy and boundary tests. Parked means a regression in
    them can reach main without a local red. That is a deliberate, temporary choice to fix the speed
    problem first - a gate so slow that a day of work becomes a day of waiting is not protecting anything,
    because it stops being run. Run -Parked before a release, and move suites back into the default the
    moment they can meet the budget.

    THE RELEASE GATE IS NOW ONE COMMAND: .\scripts\test-local.ps1 -Parked -Configuration Release, run on
    merged main at the commit about to be tagged. It was three, because the two installer projects had to
    be invoked by hand; a gate that depends on remembering two extra commands is one that will eventually
    be run without them, and a release is the one place there is no fixing it forward.

    -PARKED NEEDS DOCKER, AND SAYS SO RATHER THAN SKIPPING (issue #2834). Two of the parked suites carry
    PostgreSQL-backed proofs. This script now BUILDS a throwaway PostgreSQL for the run that needs one,
    hands its connection strings to the test processes, and destroys it when the run ends - so there is
    no shared container for a person to start and nothing to go stale between runs. It IGNORES whatever
    the machine has in its user environment. With Docker absent the run STOPS: those proofs would report
    SKIPPED, and a skip is indistinguishable from a pass in the console summary, in the TRX counters and
    in every report built from them. The default run starts no database and needs no Docker.

    A RUN THAT COLLECTED ZERO TESTS - OR ONLY PART OF WHAT IT WAS ASKED FOR - IS REFUSED, WITH ITS OWN
    EXIT CODE. A filter that matches nothing
    used to exit 0 from every project and end on "all projects exited zero" - a green that means nothing
    ran. Red-first evidence is gathered with this command, so that shape of green is now a failure:
    exit 3 means zero tests were collected anywhere, exit 4 means a project exited zero without writing a
    result file, and exit 5 means part of the filter matched nothing (or -ExpectTests was not met). None of
    them is a test failure (exit 1) and none of them is ever evidence.

    EVERY RUN WRITES A TRX FILE AND PRINTS ITS OUTCOME AND TEST COUNT. That pair, not the console
    "Passed!" line, is the verdict - see the comment above the run loop for why. A green with a collapsed
    count is the result most worth being able to go back and check.

.PARAMETER Gateway
    Run ONLY the parked Gateway suite (host-bound, machine-wide lock). Expect a queue.

.PARAMETER Parked
    Also run the three parked suites - Gateway.Tests, Core.Tests and Gateway.UnitTests. This is the
    RELEASE gate. Expect tens of minutes, most of it queueing for the Gateway lock.

.PARAMETER Fast
    Retained for callers that pass it. The default IS fast now, so this is a no-op.

.PARAMETER Filter
    An xUnit filter passed through to every project (e.g. "FullyQualifiedName~Dictation").

    EVERY "FullyQualifiedName~" TERM MUST MATCH SOMETHING. A composite filter whose second term is a typo
    used to collect from the first, exit 0, and read as a pass. Exit 5 means part of the filter matched
    nothing anywhere - the run is not empty, which is exactly why it would otherwise have gone unnoticed.

.PARAMETER ExpectTests
    The number of tests this evidence command is expected to collect across the whole run. Exit 5 when the
    run collects a different number. Use it when a claim rests on a COUNT: a count that is checked by
    nothing is a count that drifts.

.EXAMPLE
    .\scripts\test-local.ps1
    .\scripts\test-local.ps1 -Fast
    .\scripts\test-local.ps1 -Gateway -Filter "FullyQualifiedName~Tombstone"
#>
param(
    [switch]$Gateway,
    [switch]$Parked,
    [switch]$Fast,
    [string]$Filter = "",
    [int]$ExpectTests = 0,
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$sln = Join-Path $repoRoot "cc-director.sln"

if ($Gateway -and $Parked) {
    Write-Error "-Gateway already runs a parked suite on its own; pass one or the other."
    exit 2
}

# THE TWO-MINUTE BUDGET IS THE RULE THIS LIST ENCODES. A suite is in the default run if it finishes
# inside it, and parked if it does not. DURATIONS ONLY - the test counts live once in the header, with
# the date they were measured, because keeping them in two places is what let one drift by a factor of
# thirty. Measured 2026-09-13 from a full run:
#   Avalonia 32s   HostedAgent 40s   Terminal 33s   Launcher 20s   Engine 10s   Core.UnitTests 5s
#   installer: setup.Tests 12s, setup-engine.Tests 13s (plus about 3s each to build)
# They start together, so the default costs about the slowest one - now HostedAgent at about 40 seconds,
# comfortably inside the budget. Gateway.UnitTests used to be that suite at 56 seconds; it grew to about
# 180 and was parked (issue #2824), which is why the budget has room again.
$defaultProjects = @(
    # The PARALLEL half of the Core tests. The project they came from runs sequentially
    # (DisableTestParallelization) and takes eleven minutes for the same kind of work - that attribute is
    # deliberately NOT in this one, and must never be added to it.
    #
    # This comment claimed 2858 tests until 2026-08-03, when a run of the TRX files put it at 82. Nobody
    # noticed, because a count in a comment is checked by nothing. Do not restore a number here without
    # measuring it; the header carries the measured set and the date it was taken.
    "src\CcDirector.Core.UnitTests\CcDirector.Core.UnitTests.csproj",
    "src\CcDirector.Avalonia.Tests\CcDirector.Avalonia.Tests.csproj",
    "src\CcDirector.Engine.Tests\CcDirector.Engine.Tests.csproj",
    "src\CcDirector.HostedAgent.Tests\CcDirector.HostedAgent.Tests.csproj",
    "src\CcDirector.Launcher.Tests\CcDirector.Launcher.Tests.csproj",
    "src\CcDirector.Terminal.Avalonia.Tests\CcDirector.Terminal.Avalonia.Tests.csproj"
)

# THE INSTALLER, WHICH IS IN THE DEFAULT RUN AND IS NOT IN THE SOLUTION.
#
# These two are NOT parked and were never slow - about seven seconds of tests between them, counts in the
# header. They were missing for a
# plumbing reason: they are not in cc-director.sln, so the single solution build above never produced them
# and the run list never named them. Nothing local ran them at all. The continuous integration job ran them
# as a separate step, so while that job was waited on the gap was invisible; the moment local became the
# gate, the installer - the first thing a new user ever sees - had no test behind it, and a release could
# ship it untested. Found by review on 2026-08-03, measured before being added.
#
# They are built individually below because a solution build cannot reach them. That is the whole reason
# they get their own list rather than a line in $defaultProjects.
$installerProjects = @(
    "tools\cc-director-setup.Tests\CcDirectorSetup.Tests.csproj",
    "tools\cc-director-setup-engine.Tests\CcDirector.Setup.Engine.Tests.csproj"
)

# PARKED. Not deleted, not broken - excluded from the default because they cannot meet the budget.
# Gateway.Tests costs a machine-wide QUEUE (45-minute waits that ran nothing); Core.Tests costs 11 to 33
# minutes of its own; Gateway.UnitTests grew past the ceiling (see its block below). Run them with
# -Parked before a release, and move any of them back into the list above the day it fits.
$gatewayProject = "src\CcDirector.Gateway.Tests\CcDirector.Gateway.Tests.csproj"
$parkedProjects = @(
    $gatewayProject,
    "src\CcDirector.Core.Tests\CcDirector.Core.Tests.csproj",
    # PARKED AGAIN 2026-09-13 (issue #2824), having been brought back when the migration-template fix took
    # it under a minute. It has since grown from 2,777 tests to 4,267 and from about 56 seconds to about
    # 180, so the ceiling STOPPED it on every run - and a stopped suite writes no TRX, which is the part
    # that mattered: its 4,259 passing tests were contributing NOTHING to the gate's verdict, and a change
    # that broke every one of them would have produced the same output as a change that broke none. Every
    # run also ended red for a reason that was not a failure, which is how people learn to stop reading red.
    #
    # Parking restores a verdict that means something and keeps the suite gating RELEASES via -Parked. It
    # does not restore per-change coverage, and that is a real loss, recorded here rather than glossed.
    #
    # MEASURED before parking, from the TRX of a full run: 713 seconds of test time over 4,267 tests in 335
    # classes, against MaxParallelThreads = 4 in AssemblyParallelism.cs - 713/4 = 178, which is the wall
    # clock observed. The cost is concentrated: the ten slowest classes are 312 seconds (44 percent) from
    # about 175 tests.
    #
    # WHAT WOULD BRING IT BACK, and why neither half is enough alone:
    #   - Two classes are wall-clock bound and should be FIXED, not moved: WingmanVoiceServiceTests (22
    #     sleeps, 62s) and StatsStoreReopensAfterAnUnreachableStoreTests (55s across FOUR tests, via
    #     Thread.Sleep(Patience)). That recovers about 115 seconds - leaving roughly 150, still over.
    #   - The next tier is slow for a different reason and has no cheap fix: MissionNoteStoreTests and
    #     DictionarySuggestionServiceTests cost about 3 seconds per test with NO sleeps, and the harness
    #     already caches the migrated schema, so this is not the database-setup cost that was fixed last
    #     time. DictionarySuggestionServiceTests does not open a database at all.
    # Both halves are needed, which is why this is parked today rather than half-fixed. See issue #2824.
    "src\CcDirector.Gateway.UnitTests\CcDirector.Gateway.UnitTests.csproj"
)

# THE TWO-MINUTE BUDGET IS ENFORCED, NOT DOCUMENTED. A suite that exceeds it is KILLED and the run is
# failed, naming it. This is a hard ceiling because the soft version did not hold: the budget was written
# in a comment, Core.Tests drifted to eleven minutes on a quiet machine and thirty-three on a busy one, and
# the gate became something people worked around instead of ran. A number that nothing checks is a wish.
#
# Exceeding it is not a test failure and must not be read as one - it is a statement that the suite no
# longer belongs in the default run. Park it, and put it back the day it fits.
$BudgetSeconds = 120

$toRun = @()
if ($Gateway) {
    # -Gateway is the "only that one suite" switch, so it stays exactly that and pulls in nothing else.
    $toRun = @($gatewayProject)
} else {
    $toRun = $defaultProjects + $installerProjects
    if ($Parked) { $toRun += $parkedProjects }
}


# ---------------------------------------------------------------------------------------------------
# THE RUN OWNS ITS OWN POSTGRESQL (issue #2834)
#
# THE DEFECT THIS CLOSES, and it cost a release. The Postgres-backed proofs used to read a connection
# string a PERSON had exported by hand, pointing at a container a PERSON had started by hand and that
# nothing kept alive. On 14 September 2026 that container stopped at 12:02 UTC, nothing noticed, and the
# release gate for v2.1.2 came back red with eight socket errors on a change that touched an Avalonia
# window and a React file. The variables on that machine were ALSO stale - naming a database and a
# password from an older generation of the rig script - so they had been pointing at nothing for as long
# as anyone could tell. Three states (right, stale, pointing at a corpse) all looked identical from
# inside the test.
#
# The rig is now born and destroyed inside the run that needs it. There is no shared container to die,
# no variable for a person to set, and nothing to go stale between runs - so that failure has nowhere
# left to live.
#
# IT IS NOT PROVISIONED FOR EVERY RUN. Only the two suites below carry Postgres-backed proofs, and both
# are parked - so the everyday gate pays nothing for this, and a change to a dialog never starts a
# database.
#
# INHERITED VARIABLES ARE OVERRIDDEN, DELIBERATELY. Whatever a machine has in its user environment is
# ignored: this run sets both variables in its own process, so its children see the rig it just built.
# A run that trusted an inherited value would be back in the failure above.
$postgresProjects = @(
    $gatewayProject,
    "src\CcDirector.Gateway.UnitTests\CcDirector.Gateway.UnitTests.csproj"
)
$needsPostgres = @($toRun | Where-Object { $postgresProjects -contains $_ }).Count -gt 0

# The label every rig this script creates carries, and the ONLY thing the stale sweep will act on.
$RigLabel = 'cc-test-local-rig'
# The owning process id, stamped on every rig this script starts. It is what lets a later run prove a
# leftover container is nobody's, rather than guessing from its age.
$RigOwnerLabel = 'cc-test-local-rig-owner'

# The rig this run must destroy when it ends, whatever way it ends. Null until one is started; read only
# by the finally block at the foot of this script.
$script:RigToTearDown = $null

function Stop-RigForThisRun {
    if ($null -eq $script:RigToTearDown) { return }
    $rig = $script:RigToTearDown
    $script:RigToTearDown = $null
    & powershell -NoProfile -File $rig.Script -Instance $rig.Instance -Port $rig.Port -Verb down 2>&1 |
        Out-Null
}

function Get-FreeTcpPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return $listener.LocalEndpoint.Port } finally { $listener.Stop() }
}

<#
    Remove rigs an EARLIER run left behind, and nothing else.

    A run that never reaches its teardown - killed, Ctrl+C, a reboot - would otherwise leave its
    container holding a port forever. This is the backstop for exactly that, and it leans hard toward
    keeping: it enumerates what to DELETE by a positive test and never what to skip.

    THE TEST IS "THE PROCESS THAT CREATED IT IS GONE", not an elapsed time. Every rig this script starts
    is stamped with the owning process id, so a container whose owner is no longer running is provably
    nobody's - which is true one second after a kill, where an age rule would have to wait out its window
    holding a port. A process id can be REUSED by an unrelated process, and that error only ever points
    one way: a recycled id reads as alive, so the container is KEPT. Nothing about reuse can make a live
    rig look dead.

    A container carrying no owner stamp - one written by an older version of this script - falls back to
    an age rule, and six hours is chosen so it cannot overlap a live run: the longest suite in this
    repository measured 1 hour 3 minutes. A container whose stamp and whose age are both unreadable is
    kept, because an unreadable age is not evidence of being old.
#>
function Remove-AbandonedRigs {
    # The whole label set is asked for as ONE field and parsed here, rather than reaching into a named
    # label with a Go template: a template carrying quoted strings has to survive PowerShell's quoting and
    # then Docker's own parser, and it did not - `failed to parse template: unterminated quoted string`.
    # {{.Labels}} needs no quoting at all.
    $rows = @(& docker ps -a --filter "label=$RigLabel" --format "{{.ID}}|{{.CreatedAt}}|{{.Labels}}" 2>&1)
    if ($LASTEXITCODE -ne 0) { return }

    $cutoff = (Get-Date).AddHours(-6)
    foreach ($row in $rows) {
        $parts = "$row" -split '\|', 3
        if ($parts.Count -lt 3) { continue }
        $id = $parts[0]

        # The owner stamp, picked out of the comma-separated label list this rig carries.
        $ownerPid = ""
        foreach ($label in ($parts[2] -split ',')) {
            $kv = $label -split '=', 2
            if ($kv.Count -eq 2 -and $kv[0].Trim() -eq $RigOwnerLabel) { $ownerPid = $kv[1].Trim() }
        }

        $disposable = $false
        $because = ""
        if ($ownerPid -match '^\d+$') {
            # The owner is named. Disposable ONLY when that process is positively not running.
            if ($null -eq (Get-Process -Id ([int]$ownerPid) -ErrorAction SilentlyContinue)) {
                $disposable = $true
                $because = "the run that created it (process $ownerPid) is gone"
            }
        }
        else {
            # No owner stamp: a rig from an older version of this script. Fall back to age, and keep
            # anything whose age cannot be read - an unreadable age is not evidence of being old.
            $created = [datetime]::MinValue
            if ([datetime]::TryParse($parts[1], [ref]$created) -and $created -lt $cutoff) {
                $disposable = $true
                $because = "it carries no owner stamp and was created $($parts[1])"
            }
        }

        if (-not $disposable) { continue }
        Write-Host "Removing abandoned test rig $id - $because."
        & docker rm -f -v $id 2>&1 | Out-Null
    }
}

if ($needsPostgres) {
    # NO FALLBACK. A run that cannot build its database does not quietly run the suites without one:
    # those proofs would SKIP, and a skip is indistinguishable from a pass in every report we produce.
    # It says what is missing and what to do about it, and stops.
    & docker version --format '{{.Server.Version}}' 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "RESULT: CANNOT RUN - Docker is not available, and these suites need a PostgreSQL server."
        Write-Host ""
        Write-Host "  Suites needing it: CcDirector.Gateway.Tests, CcDirector.Gateway.UnitTests"
        Write-Host "  Start Docker Desktop and run this again."
        Write-Host ""
        Write-Host "This is deliberately fatal rather than a skip. The Postgres-backed proofs would report"
        Write-Host "SKIPPED, which reads exactly like a pass, and this run is the release gate."
        exit 6
    }

    Remove-AbandonedRigs

    $rigScript = Join-Path $PSScriptRoot "pg-stats-proof-rig.ps1"
    $rigInstance = "run" + [Guid]::NewGuid().ToString("N").Substring(0, 8)
    $rigPort = Get-FreeTcpPort
    Write-Host "Starting a throwaway PostgreSQL for this run (instance $rigInstance, port $rigPort)..."

    # THE RIG NAME IS RECORDED BEFORE THE RIG EXISTS, so the finally block at the end of this script
    # tears down a container that failed half way through provisioning as readily as a healthy one.
    #
    # IT IS A try/finally AND NOT AN EVENT, and the difference is the whole of a defect found in review.
    # This first used Register-EngineEvent PowerShell.Exiting, on the strength of a check that ran the
    # script as `powershell -File ...` - a FRESH engine, which exits when the script does, so the
    # teardown fired and the check passed. The documented way to run this gate is `.\scripts	est-local.ps1`
    # from a shell you already have open, and there the engine does NOT exit when the script does:
    # reproduced, the container survived the run, and every further run stacked another subscriber and
    # another container. A proof against the wrong invocation is not a proof.
    $script:RigToTearDown = @{ Script = $rigScript; Instance = $rigInstance; Port = $rigPort }

    & powershell -NoProfile -File $rigScript -Instance $rigInstance -Port $rigPort -Verb up `
        -Label $RigLabel -OwnerLabel "$RigOwnerLabel=$PID" 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "RESULT: CANNOT RUN - the throwaway PostgreSQL for this run could not be provisioned."
        Write-Host "  Re-run the rig by hand to see why:"
        Write-Host "    powershell -NoProfile -File scripts\pg-stats-proof-rig.ps1 -Instance $rigInstance -Port $rigPort -Verb up"
        exit 6
    }

    # The connection strings come from the rig itself rather than being composed here, so exactly one
    # place knows the database names, the role and the password.
    $envLines = & powershell -NoProfile -File $rigScript -Instance $rigInstance -Port $rigPort -Verb print-env
    foreach ($line in $envLines) {
        if ($line -match '^\s*\$env:([A-Z_]+)\s*=\s*''(.+)''\s*$') {
            Set-Item -Path "Env:$($Matches[1])" -Value $Matches[2]
        }
    }
    if ([string]::IsNullOrWhiteSpace($env:CC_GATEWAY_TEST_PG_CONNECTION) -or
        [string]::IsNullOrWhiteSpace($env:CC_GATEWAY_TEST_PG_STATS_CONNECTION)) {
        Write-Host ""
        Write-Host "RESULT: CANNOT RUN - the rig started but did not hand back both connection strings."
        exit 6
    }

    # What turns a SKIP into a FAILURE inside the test assemblies. The run is asserting that it HAS
    # provided a database; PostgresRigIsPresentWhenRequiredTests holds it to that, so a rig that started
    # and then died mid-run is one red test with a sentence, not eight socket errors nobody can read.
    $env:CC_TEST_REQUIRE_POSTGRES = "1"
    Write-Host "PostgreSQL ready. It is destroyed when this run ends."
}

# EVERYTHING FROM HERE IS INSIDE A try/finally, SO THE THROWAWAY DATABASE IS DESTROYED WHATEVER HAPPENS.
# PowerShell runs a finally block on a normal end, on a terminating error, AND on every `exit` in this
# script, with the exit code preserved - all three verified. The body below is left at its original
# indentation deliberately: re-indenting three hundred unchanged lines would bury the change that
# matters in a whitespace diff. Read it with `git diff -w`.
#
# A process that is KILLED outright runs no finally block and never will. That case belongs to
# Remove-AbandonedRigs at the start of the next run, which removes a rig whose owning process is gone.
try {
Write-Host "Building once, then running $($toRun.Count) test project(s)..."
& dotnet build $sln -c $Configuration -v q --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "RESULT: BUILD FAILED - no tests were run."
    exit 1
}

# The installer projects are outside cc-director.sln, so the build above did not produce them and the run
# loop's --no-build would fail against a stale or absent assembly. Build them here, before anything starts.
# A failure is fatal for the same reason a solution build failure is: tests that never ran must not be
# reported as tests that passed.
foreach ($proj in ($toRun | Where-Object { $installerProjects -contains $_ })) {
    & dotnet build (Join-Path $repoRoot $proj) -c $Configuration -v q --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "RESULT: BUILD FAILED for $proj - no tests were run."
        exit 1
    }
}

$logDir = Join-Path ([System.IO.Path]::GetTempPath()) ("cc-test-local-" + [Guid]::NewGuid().ToString("N").Substring(0,8))
New-Item -ItemType Directory -Path $logDir -Force | Out-Null

$filterArgs = @()
if ($Filter -ne "") { $filterArgs = @("--filter", $Filter) }

# WHY EVERY RUN WRITES A TRX FILE, AND WHY THE CONSOLE SUMMARY BELOW IS NOT THE VERDICT.
#
# "Passed! - Failed: 0" is printed by a run that passed everything it managed to START. When a test host
# crashes part way through, the surviving processes still print that line, with a smaller count that
# nobody looks at - which has already very nearly certified a change that silently stopped 1,340 tests
# from running. A green with a collapsed count is the most dangerous result this script can produce,
# because it is indistinguishable from a real green at a glance.
#
# The TRX file carries the two things that make the difference checkable: ResultSummary/@outcome, which
# says whether the run COMPLETED rather than merely whether its assertions passed, and Counters/@total,
# which says how many tests there were. Judge a run by those two against a recorded baseline, never by
# the console line. scripts/test-qualification.ps1 already judges its soak this way.
$running = @()
foreach ($proj in $toRun) {
    $name = Split-Path -Leaf ([System.IO.Path]::GetDirectoryName((Join-Path $repoRoot $proj)))
    $out = Join-Path $logDir "$name.log"
    $trx = Join-Path $logDir "$name.trx"
    $args = @("test", (Join-Path $repoRoot $proj), "--no-build", "-c", $Configuration, "--nologo", "-v", "q",
              "--logger", "trx;LogFileName=$name.trx", "--results-directory", $logDir) + $filterArgs
    $p = Start-Process -FilePath "dotnet" -ArgumentList $args -NoNewWindow -PassThru `
                       -RedirectStandardOutput $out -RedirectStandardError "$out.err"

    # Touching .Handle keeps the process handle OPEN, which is the only reason ExitCode is readable
    # after the process ends. Without it Start-Process -PassThru hands back an object whose ExitCode
    # is EMPTY once the child exits - and "$null -eq 0" is false, so every project was classified
    # FAIL. This script is the merge gate, and it was reporting "RESULT: FAILED in 7 project(s)"
    # over seven lines that each said "Passed!  - Failed: 0". A gate that fails on green is worse
    # than no gate: it trains everyone to ignore it, or to go and wait fifty minutes for CI.
    #
    # This mission met the same bug independently and wrote the same fix; main's landed first and is kept
    # as the incumbent, with the duplicate dropped. TWO copies would cache the handle twice, and a
    # careless resolution of the same conflict would leave NEITHER - the original bug back, with two
    # commits in the history each claiming to have fixed it.
    $null = $p.Handle


    $running += [pscustomobject]@{ Name = $name; Process = $p; Log = $out; Trx = $trx }
    Write-Host "  started $name"
}

Write-Host ""
if ($toRun -contains $gatewayProject) {
    Write-Host "Waiting. The Gateway suite serializes machine-wide, so it may queue behind another run;"
    Write-Host "that is not a hang - it prints its holder every 30s into its log."
} else {
    Write-Host "Waiting. No parked suite is in this run, so nothing here queues for the machine-wide lock."
}
Write-Host ""

$failed = @()
$overBudget = @()
foreach ($r in $running) {
    # Wait only up to the budget. A suite that has not finished by then is over the ceiling: kill it, so a
    # single slow project cannot hold the whole gate, and record it separately from a real failure.
    # -Parked deliberately suspends the ceiling: that run is the release gate and is EXPECTED to be slow.
    if ($Parked -or $Gateway) {
        $r.Process.WaitForExit()
    } elseif (-not $r.Process.WaitForExit($BudgetSeconds * 1000)) {
        $overBudget += $r.Name
        try { $r.Process.Kill($true) } catch { }
        try { $r.Process.WaitForExit(10000) | Out-Null } catch { }
    }
    $summary = ""
    if (Test-Path $r.Log) {
        $summary = (Select-String -Path $r.Log -Pattern "^(Passed!|Failed!)" -ErrorAction SilentlyContinue |
                    Select-Object -Last 1).Line
    }
    if ($null -eq $summary -or $summary -eq "") { $summary = "(no summary line - see $($r.Log))" }

    # The authoritative pair, read from the TRX rather than from the console line above.
    $outcome = "NO-TRX"
    $total = 0
    if (Test-Path $r.Trx) {
        [xml] $doc = Get-Content $r.Trx -Raw
        $outcome = [string] $doc.TestRun.ResultSummary.outcome
        $total = [int] $doc.TestRun.ResultSummary.Counters.total
    }
    $r | Add-Member -NotePropertyName Outcome -NotePropertyValue $outcome
    $r | Add-Member -NotePropertyName Total -NotePropertyValue $total

    if ($r.Process.ExitCode -eq 0) {
        Write-Host ("  PASS  {0}  {1}" -f $r.Name, $summary.Trim())
    } else {
        Write-Host ("  FAIL  {0}  {1}" -f $r.Name, $summary.Trim())
        $failed += $r
    }
}

Write-Host ""
Write-Host "TRX verdict - THIS is the gate. Outcome must be 'Completed' AND total at or above the baseline:"
foreach ($r in $running) {
    Write-Host ("  {0,-40} outcome={1,-12} total={2}" -f $r.Name, $r.Outcome, $r.Total)
}
Write-Host ""
Write-Host "TRX files: $logDir"
Write-Host ""

# COVERAGE WARNING. The default run is fast because three suites are parked - but "parked" must never
# quietly mean "this change was never tested". select-tests.ps1 works out, from the reference graph,
# which suites this change could actually affect; if a PARKED one is in that set, say so loudly.
#
# It WARNS rather than running them, and the measurement is why. Replaying the last hundred merges,
# a parked suite was implicated in 69 to 80 per cent of changes - because CcDirector.Core and
# Gateway.Contracts are referenced by nearly everything. Running them automatically would restore the
# twelve-to-forty-five-minute gate for seven changes in ten, which is the problem this whole exercise
# removed. So the fast gate stands, and the reader is told exactly what it did not cover.
$parkedNames = @($parkedProjects | ForEach-Object { Split-Path -Leaf ([System.IO.Path]::GetDirectoryName((Join-Path $repoRoot $_))) })
$coverageGap = @()
if (-not $Parked -and -not $Gateway) {
    try {
        $sel = & (Join-Path $PSScriptRoot "select-tests.ps1")
        $coverageGap = @($sel.Suites | Where-Object { $parkedNames -contains $_ })
    } catch {
        # A selector that cannot run must not fail the gate, but it must not be silent either.
        Write-Host "NOTE: could not compute test selection ($($_.Exception.Message)); coverage gap unknown."
    }
}

if ($coverageGap.Count -gt 0) {
    Write-Host ""
    Write-Host "COVERAGE GAP - this change touches code covered by PARKED suite(s) that did not run:"
    foreach ($n in $coverageGap) { Write-Host "  $n" }
    Write-Host ""
    Write-Host "Run '.\scripts	est-local.ps1 -Parked' before merging, or say in the pull request why not."
    Write-Host "(Explain the reasoning with: .\scripts\select-tests.ps1 -Explain)"
}

if ($overBudget.Count -gt 0) {
    Write-Host ("RESULT: OVER BUDGET - {0} suite(s) exceeded the {1}-second ceiling and were STOPPED:" -f $overBudget.Count, $BudgetSeconds)
    foreach ($n in $overBudget) { Write-Host "  $n" }
    Write-Host ""
    Write-Host "This is NOT a test failure. It means the suite no longer belongs in the default run."
    Write-Host "Park it in `$parkedProjects, or make it fit. Do not raise the ceiling to make this go away -"
    Write-Host "the ceiling is the point, and every second added to it is paid by every person and agent"
    Write-Host "on every change, forever."
    exit 1
}

# A RUN THAT COLLECTED ZERO TESTS IS A BROKEN INSTRUMENT, NOT A PASS.
#
# This is the fail-open that let a mission report two red-first claims that could not be reproduced. A
# filtered run whose filter matches nothing exits ZERO from every project, prints "Passed!", writes a TRX
# saying outcome=Completed with total=0, and this script used to end on "RESULT: all projects exited zero."
# Nothing anywhere said that nothing had run. Red-first evidence is gathered with exactly this command, and
# a filter that has drifted from the test name it was written for - or a test file that is not in the
# checkout yet - produces a green that means nothing.
#
# So the pass condition is stated as a PRESENCE: at least one test must have been COLLECTED across the run.
# A per-project zero is normal and is not failed - a filter naming a Gateway test legitimately collects
# nothing in the Avalonia suite - but a run in which NOTHING ran anywhere is refused, loudly, with its own
# exit code so a caller can tell it apart from a test failure.
$collected = 0
foreach ($r in $running) { $collected += [int] $r.Total }
$noTrx = @($running | Where-Object { $_.Outcome -eq "NO-TRX" -and $_.Process.ExitCode -eq 0 })

if ($noTrx.Count -gt 0) {
    Write-Host ""
    Write-Host "RESULT: NO RESULT FILE - $($noTrx.Count) project(s) exited zero without writing a TRX:"
    foreach ($r in $noTrx) { Write-Host "  $($r.Name) -> $($r.Log)" }
    Write-Host ""
    Write-Host "A project that exited zero and produced no result file did not report a run. That is a"
    Write-Host "broken instrument, not a pass. Do not quote a number from this run."
    exit 4
}

# A RUN THAT COLLECTED ONLY PART OF WHAT IT WAS ASKED FOR IS NOT EVIDENCE EITHER.
#
# The all-zero refusal below catches a filter that matched nothing ANYWHERE. It does not catch a filter
# that matched something somewhere, which is the shape a composite filter produces when one of its terms
# has drifted from the test it was written for: on the landing this was found in,
#
#   -Filter "FullyQualifiedName~RuleReasonGroundingTests|FullyQualifiedName~DefinitelyNoSuchTest_dnkeyz"
#
# collected eight tests from the first term, collected NOTHING from the second, and exited 0. Every term
# after the first could be a typo and the run would still read as a pass. Removing or renaming a required
# test does the same thing, quietly, on the day it happens.
#
# So the pass condition is stated as a PRESENCE, per term: each "FullyQualifiedName~TOKEN" the caller
# named must have COLLECTED at least one test whose name contains that token. It is derived from the
# filter the caller passed - never a second list kept here, which would be one more thing to keep in step.
if ($Filter -ne "") {
    $names = New-Object System.Collections.Generic.List[string]
    foreach ($r in $running) {
        if (-not (Test-Path $r.Trx)) { continue }
        [xml] $doc = Get-Content $r.Trx -Raw
        $defs = $doc.TestRun.TestDefinitions
        if ($null -eq $defs) { continue }
        foreach ($u in @($defs.UnitTest)) {
            if ($null -ne $u -and $null -ne $u.name) { $names.Add([string]$u.name) }
        }
    }

    # Each OR term of the filter, in the caller's own words.
    $terms = @($Filter -split '\|' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne "" })
    $absent = @()
    foreach ($term in $terms) {
        # Only the "contains" form can be checked against a collected test name. Anything else (a Category
        # trait, an equality match, a negation) is left alone rather than guessed at - a checker that
        # invented a verdict for a form it does not understand would be worse than one that says nothing.
        if ($term -notmatch '^FullyQualifiedName~(.+)$') { continue }
        $token = $Matches[1]
        $hit = $false
        foreach ($n in $names) { if ($n -like "*$token*") { $hit = $true; break } }
        if (-not $hit) { $absent += $term }
    }

    if ($absent.Count -gt 0) {
        Write-Host ""
        Write-Host "RESULT: PART OF THE FILTER MATCHED NOTHING - this run is not evidence for what it named."
        Write-Host "These terms collected no test anywhere in the run:"
        foreach ($t in $absent) { Write-Host "  $t" }
        Write-Host ""
        Write-Host "$($names.Count) test(s) were collected in total, so the run is not empty - which is exactly"
        Write-Host "why it would otherwise have passed. A term that matches nothing is a test name that has"
        Write-Host "drifted, a test that has been removed, or a typo; in all three the claim this run was"
        Write-Host "gathered to support is unproven."
        exit 5
    }

    if ($ExpectTests -gt 0 -and $collected -ne $ExpectTests) {
        Write-Host ""
        Write-Host "RESULT: EXPECTED $ExpectTests TEST(S), COLLECTED $collected."
        Write-Host "The caller declared the inventory this evidence needs and the run did not match it."
        exit 5
    }
}

if ($collected -eq 0) {
    Write-Host ""
    Write-Host "RESULT: ZERO TESTS COLLECTED - nothing ran, so this is not a pass."
    if ($Filter -ne "") {
        Write-Host "The filter was: $Filter"
        Write-Host "No test in any project matched it. Check the test name, and check that the test file is"
        Write-Host "actually in THIS checkout - a filter naming a class that does not exist here exits zero"
        Write-Host "with 'No test matches', which is the shape of a green and the substance of nothing."
    } else {
        Write-Host "No filter was passed, so a total of zero means the run did not execute at all."
    }
    Write-Host ""
    Write-Host "A run that collected zero tests is a broken instrument. It is never evidence, and a red-first"
    Write-Host "claim must never be quoted from one."
    exit 3
}

if ($failed.Count -eq 0) {
    Write-Host "RESULT: all projects exited zero. Check the TRX outcome and totals above before calling it green."
    Write-Host "This is the gate - you do not need to wait for GitHub CI to merge."
    exit 0
}

Write-Host "RESULT: FAILED in $($failed.Count) project(s):"
foreach ($f in $failed) {
    Write-Host "  $($f.Name) -> $($f.Log)"
    $lines = Get-Content $f.Log -Tail 25 -ErrorAction SilentlyContinue
    foreach ($l in $lines) { Write-Host "      $l" }
}
Write-Host ""
Write-Host "Logs kept in $logDir"
exit 1
}
finally {
    # The rig, if this run started one. Nothing else belongs in here: a finally that does real work can
    # mask the failure that brought it here.
    Stop-RigForThisRun
}
