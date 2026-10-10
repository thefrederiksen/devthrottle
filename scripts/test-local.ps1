#Requires -Version 5.1
<#
.SYNOPSIS
    Run this repository's tests locally. THE one command - do not hand-roll a dotnet test invocation.

.DESCRIPTION
    THE DEFAULT RUN IS ABOUT TWO MINUTES, AND THAT IS THE POINT. Local is the gate for ordinary changes
    (issue #1156), so the gate has to be cheap enough that nobody is tempted to skip it.

    WHAT THE DEFAULT RUNS: every suite that finishes inside the two-minute budget, PLUS the three installer
    test projects. They start together and the wall clock is the slowest of them, not the sum.

    COUNTS, MEASURED 2026-09-13 FROM THE TRX FILES OF A FULL RUN - not estimated, and not copied forward:
      Avalonia 423    Launcher 191    HostedAgent 88    Core.UnitTests 278    Engine 63    Terminal 25
      installer: setup.Tests 25, setup-engine.Tests 541, setup-cli.Tests 34 (setup-cli added 2026-10-01)
    1068 in the default suites, 1634 including the installer.

    Gateway.UnitTests (4,267) LEFT this list on 2026-09-13 - see the parked block below and issue #2824.
    Re-measure before changing these numbers; the per-project comments below were carried forward for
    months after they stopped being true.

    The installer projects live outside cc-director.sln and are built separately here. They are in the
    default run because they are fast and because the thing they cover - the first screen a new user
    ever sees - was running nowhere locally at all.

    WHAT THE DEFAULT NO LONGER RUNS - AND THIS IS DELIBERATE, NOT AN OVERSIGHT. Three suites are PARKED
    behind -Parked because none can meet the budget:

      CcDirector.Gateway.Tests   - host-bound, and one run of it at a time per user per machine
                                   (GatewayTestSuiteLock). Its own running time is most of an hour. Until
                                   October 2026 its cost was the QUEUE behind every other working tree -
                                   waits of 45 minutes that executed ZERO tests - which the release-gate
                                   lock above now turns into an immediate refusal. Its pure tests were
                                   split out into CcDirector.Gateway.UnitTests, which was in the default
                                   run until 2026-09-13 and is now parked beside it (issue #2824); what is
                                   parked here is the host-bound remainder.
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

    ONE RUN THAT INCLUDES THE GATEWAY SUITE AT A TIME, AND A SECOND ONE IS REFUSED, NEVER QUEUED. Before
    building, a -Parked or -Gateway run takes the release-gate lock (scripts\gate-lock.ps1) and probes
    the Gateway suite's own lock. If either is held by a live process, this run stops within seconds
    with exit 6 naming the holder - the release-gate lock carries its process, session, commit and
    command; the suite's own lock, written by the test process, carries its process, session and
    directory - and it does not build, it does not start, and it does not wait. Between 8 and 10 October 2026 nineteen release-gate runs each
    queued forty-five minutes behind another and then executed nothing: fourteen hours that proved
    nothing, with up to seven gates overlapping. A queue hides a conflict; a refusal shows it. The
    default run takes no lock, because it includes no suite that cannot overlap.

    THE GATE STARTS NO DATABASE, EVER. The tests that need a real PostgreSQL live in their own project,
    src\CcDirector.Gateway.DatabaseTests, and only scripts\test-database.ps1 runs it - it builds a
    throwaway PostgreSQL in Docker for its own run and destroys it after. Neither the default run nor
    -Parked needs Docker. Until October 2026 -Parked built a database here for two suites whose tests are
    otherwise about something else, and the two suites then shared that one database at the same time.

    A RUN THAT COLLECTED ZERO TESTS - OR ONLY PART OF WHAT IT WAS ASKED FOR - IS REFUSED, WITH ITS OWN
    EXIT CODE. A filter that matches nothing
    used to exit 0 from every project and end on "all projects exited zero" - a green that means nothing
    ran. Red-first evidence is gathered with this command, so that shape of green is now a failure:
    exit 3 means zero tests were collected anywhere, exit 4 means a project exited zero without writing a
    result file, exit 5 means part of the filter matched nothing (or -ExpectTests was not met), and exit 6
    means another run holding the Gateway suite is in progress and this one was refused before building
    (and exit 2, as for a bad argument, means the lock's own location is broken). None of them is a test
    failure (exit 1) and none of them is ever evidence.

    EVERY RUN WRITES A TRX FILE AND PRINTS ITS OUTCOME AND TEST COUNT. That pair, not the console
    "Passed!" line, is the verdict - see the comment above the run loop for why. A green with a collapsed
    count is the result most worth being able to go back and check.

.PARAMETER Gateway
    Run ONLY the parked Gateway suite (host-bound). Takes the release-gate lock; refused with exit 6 if
    another run holding the Gateway suite is in progress.

.PARAMETER Parked
    Also run the three parked suites - Gateway.Tests, Core.Tests and Gateway.UnitTests. This is the
    RELEASE gate. Takes the release-gate lock before building; a second one on this machine is refused
    with exit 6, not queued. Expect the Gateway suite's own running time, which is most of an hour.

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
. (Join-Path $PSScriptRoot "gate-lock.ps1")

if ($Gateway -and $Parked) {
    Write-Error "-Gateway already runs a parked suite on its own; pass one or the other."
    exit 2
}

# THE TWO-MINUTE BUDGET IS THE RULE THIS LIST ENCODES. A suite is in the default run if it finishes
# inside it, and parked if it does not. DURATIONS ONLY - the test counts live once in the header, with
# the date they were measured, because keeping them in two places is what let one drift by a factor of
# thirty. Measured 2026-09-13 from a full run:
#   Avalonia 32s   HostedAgent 40s   Terminal 33s   Launcher 20s   Engine 10s   Core.UnitTests 5s
#   installer: setup.Tests 12s, setup-engine.Tests 13s, setup-cli.Tests 1s (plus about 3s each to build)
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
    "src\CcDirector.Terminal.Avalonia.Tests\CcDirector.Terminal.Avalonia.Tests.csproj",
    # Reclaim the Disk, phase 1. 116 tests in about six seconds, measured 2026-09-19: it builds small
    # trees on disk and walks them, so the cost is real work and not startup. It covers the engine AND
    # the cc-cleanup-storage command line tool, which is why there is one suite here and not two.
    "src\CcDirector.Reclaim.Tests\CcDirector.Reclaim.Tests.csproj"
)

# THE INSTALLER, WHICH IS IN THE DEFAULT RUN AND IS NOT IN THE SOLUTION.
#
# These are NOT parked and were never slow - about seven seconds of tests between them, counts in the
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
    "tools\cc-director-setup-engine.Tests\CcDirector.Setup.Engine.Tests.csproj",
    # The setup command line's own tests (issue #3506). Like the two above it is outside the solution, and
    # until 2026-10-01 nothing ran it - not this script, not continuous integration - so the test that pins
    # where 'enroll' connects the Director ran nowhere. About one second; every test in it is offline and
    # fakes the browser sign-in, so a regression fails fast instead of waiting on a browser.
    "tools\cc-director-setup-cli.Tests\CcDirector.Setup.Cli.Tests.csproj"
)

# PARKED. Not deleted, not broken - excluded from the default because they cannot meet the budget.
# Gateway.Tests runs for most of an hour and one at a time; Core.Tests costs 11 to 33 minutes of its
# own; Gateway.UnitTests grew past the ceiling (see its block below). Run them with -Parked before a
# release, and move any of them back into the list above the day it fits.
$gatewayProject = "src\CcDirector.Gateway.Tests\CcDirector.Gateway.Tests.csproj"
$parkedProjects = @(
    $gatewayProject,
    # THE WINGMAN GUARDS LEFT THIS SUITE on 2026-09-16, the same way and for the same reason as the skill
    # guards below. WingmanCharterAuditTests (the charter's invariants, and that the model in force is the one
    # the charter names) and WingmanVerdictBoundaryAuditTests (the verdict boundary read from JudgeAsync, with the
    # charter required to agree) were in Core.Tests. They read files, reference no timing and no shared state,
    # and exist to catch an edit to the charter or to the verdict seat at the moment it is made. In a parked
    # suite they told nobody anything at commit time. They now live in Core.UnitTests. Do not move them back.
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
    #
    # A GUARD MUST NOT LIVE IN A PARKED SUITE. The two skill guards - one that a shipped skill never
    # teaches a command the product refuses, one that a built-in has a single source - were written here
    # on 2026-09-14 and MOVED OUT the same day, into Core.UnitTests. They read files and take 33ms
    # between them, so parking was never about their cost: it was that a parked suite tells a developer
    # nothing at commit time, and both exist to catch an edit at the moment it is made. Anything cheap
    # whose whole value is fast feedback belongs in a project that actually runs.
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
# THE RUN'S IDENTITY AND ITS FOLDER, settled before anything is built, because the lock below names both.
$commit = (& git -C $repoRoot rev-parse HEAD).Trim()
$logDir = Join-Path ([System.IO.Path]::GetTempPath()) ("cc-test-local-" + [Guid]::NewGuid().ToString("N").Substring(0,8))
New-Item -ItemType Directory -Path $logDir -Force | Out-Null

# ONE RUN HOLDING THE GATEWAY SUITE AT A TIME. A SECOND ONE IS REFUSED HERE, BEFORE THE BUILD, NEVER
# QUEUED. See the header and scripts\gate-lock.ps1 for why: a queue hid nineteen conflicting release
# gates for forty-five minutes each, and every one of them then executed nothing.
#
# Two locks are consulted. The Gateway suite's OWN lock is only probed: the test process takes that one
# itself when it starts, so a hand-run "dotnet test" of the suite holds it and a gate that built for
# minutes would then find its child queued behind that run. The release-gate lock is TAKEN and held
# until this script ends, in the finally block below - held in a variable, not in a scope, so that a
# script run from an interactive shell (whose process survives "exit") releases it too.
$gateLock = $null
if ($toRun -contains $gatewayProject) {
    $command = "test-local.ps1"
    if ($Parked) { $command += " -Parked" }
    if ($Gateway) { $command += " -Gateway" }
    $command += " -Configuration $Configuration"
    if ($Filter -ne "") { $command += " -Filter `"$Filter`"" }
    $suiteLockPath = Get-GatewaySuiteLockPath
    $gateLockPath = Get-ReleaseGateLockPath
    $suiteHeld = $false
    try {
        $suiteHeld = Test-GateLockHeld $suiteLockPath
        if (-not $suiteHeld) {
            $gateLock = Enter-GateLock $gateLockPath ([ordered]@{
                session = (Get-GateLockSessionName); commit = $commit; command = $command
                directory = $repoRoot; runFolder = $logDir })
        }
    } catch {
        # Only a sharing conflict means "held", and the helper answers that without throwing. Anything that
        # reaches here is a fault in the lock's home - permissions, a directory at the path, a read-only
        # file, a full disk - which no amount of waiting clears. Named here, as the C# lock names it,
        # instead of dying with a raw constructor error and an empty run folder left behind.
        $fault = Get-GateLockInnerException $_.Exception
        Write-Host ""
        Write-Host "RESULT: CANNOT SET UP THE GATE LOCK - nothing was built and nothing was started."
        Write-Host ("  " + $fault.GetType().Name + ": " + $fault.Message)
        Write-Host "  Lock files: $gateLockPath and $suiteLockPath"
        Write-Host ""
        Write-Host "This is NOT another run holding the lock. It is a fault in the lock's location that will not"
        Write-Host "clear by waiting: fix the path above (permissions, a directory or read-only file sitting at it,"
        Write-Host "a full disk) and run this again."
        Remove-Item $logDir -Force
        exit 2
    }
    if ($suiteHeld) {
        Write-Host ""
        Write-Host "RESULT: REFUSED - a run of CcDirector.Gateway.Tests is in progress on this machine, and this"
        Write-Host "run includes that suite. Nothing was built and nothing was started."
        Write-Host ("  Holder: " + (Format-GateLockHolder (Read-GateLockHolder $suiteLockPath)))
        Write-Host "  Lock file: $suiteLockPath"
        Write-Host ""
        Write-Host "Two runs of that suite corrupt each other, so there is one at a time per user per machine."
        Write-Host "This run does not queue: a queue hides the conflict until it expires. Wait for the holder to"
        Write-Host "finish - or, if it is stuck, end that process - and run this again."
        Remove-Item $logDir -Force
        exit 6
    }
    if ($null -eq $gateLock) {
        Write-Host ""
        Write-Host "RESULT: REFUSED - another run that includes the Gateway suite is in progress on this machine."
        Write-Host "Nothing was built and nothing was started."
        Write-Host ("  Holder: " + (Format-GateLockHolder (Read-GateLockHolder $gateLockPath)))
        Write-Host "  Lock file: $gateLockPath"
        Write-Host ""
        Write-Host "One release gate at a time, by design, and the loser is told at once instead of queueing for"
        Write-Host "forty-five minutes and then executing nothing. The holder's session, commit and command are"
        Write-Host "above: wait for it to finish - or, if it is stuck, end that process - and run this again."
        Remove-Item $logDir -Force
        exit 6
    }
}

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
        Write-Host "Waiting. This run holds the release-gate lock ($gateLockPath); another run that includes"
        Write-Host "the Gateway suite is refused at once rather than queued behind this one."
    } else {
        Write-Host "Waiting. No parked suite is in this run, so it takes no lock and nothing queues behind it."
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
        $executed = 0
        $total = 0
        if (Test-Path $r.Trx) {
            [xml] $doc = Get-Content $r.Trx -Raw
            $outcome = [string] $doc.TestRun.ResultSummary.outcome
            $total = [int] $doc.TestRun.ResultSummary.Counters.total
            # EXECUTED is not the same number as TOTAL, and the difference is load-bearing (issue #2834).
            # A test carrying a static Skip is COLLECTED and counted in total while executing nothing, so a
            # run can report total=1, executed=0 and "Test Run Successful". Anything that asks "did this run
            # actually enforce something" has to read executed.
            $executed = [int] $doc.TestRun.ResultSummary.Counters.executed
        }
        $r | Add-Member -NotePropertyName Outcome -NotePropertyValue $outcome
        $r | Add-Member -NotePropertyName Total -NotePropertyValue $total
        $r | Add-Member -NotePropertyName Executed -NotePropertyValue $executed

        if ($r.Process.ExitCode -eq 0) {
            Write-Host ("  PASS  {0}  {1}" -f $r.Name, $summary.Trim())
        } else {
            Write-Host ("  FAIL  {0}  {1}" -f $r.Name, $summary.Trim())
            $failed += $r
        }
    }

    Write-Host ""
    Write-Host "TRX verdict - THIS is the gate. Every suite must report outcome=Completed:"
    foreach ($r in $running) {
        Write-Host ("  {0,-40} outcome={1,-12} total={2,-6} executed={3}" -f $r.Name, $r.Outcome, $r.Total, $r.Executed)
    }

    # AND NOW IT IS ACTUALLY CHECKED. Until review round four of issue #2834 this block PRINTED a sentence
    # saying outcome had to be Completed and then compared nothing - the line the whole fleet reads as "THIS
    # is the gate" was a display. A suite that aborted or timed out while exiting zero was shown as not
    # Completed and the run still ended on "all projects exited zero".
    #
    # The sentence also promised "total at or above the baseline", and there is no baseline anywhere in this
    # repository to compare against. That half is not implemented here - a per-project floor needs a recorded
    # file and a way to update it, which is its own change - so the claim has been REMOVED from the sentence
    # rather than left standing. A gate that advertises a check nobody performs is the exact defect this
    # issue is about, and leaving the words there while fixing only half would repeat it.
    # WHICH OUTCOMES MEAN THE RUN FINISHED. Both of these do, and the difference between them is whether
    # the assertions passed - which is NOT what this check is about:
    #   Completed - it finished and everything passed.
    #   Failed    - it finished and something failed. MEASURED, not assumed: a real run of
    #               cc-director-setup-engine.Tests with one assertion failure writes
    #               outcome="Failed" total="541" executed="541" passed="540" failed="1".
    # An ordinary failing gate is therefore a FINISHED run, and it must fall through to the established
    # failure report below, which names the projects and their logs and exits 1.
    #
    # THE FIRST VERSION OF THIS CHECK GOT THAT WRONG and called anything that was not "Completed"
    # incomplete - so every ordinary red run would have exited 9 here, losing its exit code and its detailed
    # output. Caught in review round five before it ever ran in anger.
    #
    # What is left is the genuinely unfinished: Aborted, Timeout, Error, Disconnected. Those can carry
    # passing assertions up to the point they stopped, which is the shape that has "very nearly certified a
    # change that silently stopped 1,340 tests from running" - the warning this script already carries
    # twenty lines above, now actually enforced.
    $finishedOutcomes = @("Completed", "Failed")
    $notCompleted = @($running | Where-Object { $_.Outcome -ne "NO-TRX" -and $finishedOutcomes -notcontains $_.Outcome })
    if ($notCompleted.Count -gt 0) {
        Write-Host ""
        Write-Host "RESULT: A SUITE DID NOT FINISH - this run is not a verdict on anything."
        foreach ($r in $notCompleted) { Write-Host ("  {0} reported outcome={1} -> {2}" -f $r.Name, $r.Outcome, $r.Log) }
        Write-Host ""
        Write-Host "This is NOT an assertion failure - a run that finished with failures reports 'Failed' and is"
        Write-Host "reported below, in full. This is a suite that stopped part way through, whose assertions may"
        Write-Host "all have passed up to the point it stopped, and whose remaining tests were never reached."
        exit 9
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
        Write-Host "Run '.\scripts\test-local.ps1 -Parked' before merging, or say in the pull request why not."
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
    $executedAll = 0
    foreach ($r in $running) {
        $collected += [int] $r.Total
        $executedAll += [int] $r.Executed
    }

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
        # THE NAMES THAT ACTUALLY RAN, NOT THE NAMES THAT WERE COLLECTED (issue #2834, review round four).
        #
        # This read TestDefinitions, which lists every test the run COLLECTED - including one carrying a
        # static Skip, which executes nothing. So a caller who named a skipped test in their filter got a
        # green for it: reproduced, when the database rig checks still ran in this gate, with the filter
        # "PostgresRigIsPresentWhenRequiredTests|DT_TEN_3" and -ExpectTests 5, where four ran, the named
        # DT_TEN_3 was skipped, and the gate exited 0. One live term laundered the dead one, which is the same
        # shape as the defect this whole issue is about. (Those rig checks now live in the database tests
        # project, which this script never runs; the rule they exposed still applies to every suite here.)
        #
        # So a term is satisfied only by a test that EXECUTED. The results carry the outcome; NotExecuted is
        # what xUnit writes for a skip, and it is exactly what must not count as evidence.
        $names = New-Object System.Collections.Generic.List[string]
        foreach ($r in $running) {
            if (-not (Test-Path $r.Trx)) { continue }
            [xml] $doc = Get-Content $r.Trx -Raw
            $results = $doc.TestRun.Results
            if ($null -eq $results) { continue }
            foreach ($u in @($results.UnitTestResult)) {
                if ($null -eq $u -or $null -eq $u.testName) { continue }
                if ([string]$u.outcome -eq "NotExecuted") { continue }
                $names.Add([string]$u.testName)
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
            Write-Host "These terms RAN no test anywhere in the run - a test that was collected and then"
            Write-Host "skipped does not count, because a skip proves nothing:"
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

    # NOTHING EXECUTED ANYWHERE IS NOT A PASS, WHATEVER SHAPE THE FILTER TOOK (review round five).
    #
    # The refusal below counts what was COLLECTED, and a statically skipped test IS collected - so a run in
    # which every selected test was skipped had a non-zero count and sailed through. The per-term checker did
    # not catch it either: it only understands the "FullyQualifiedName~TOKEN" contains form and deliberately
    # says nothing about any other, so an EXACT-name filter was checked by nothing at all. Reproduced at
    # b8368c9c0 with a -Parked run naming one statically skipped test by its exact name: all eleven suites
    # executed zero, and the gate printed "RESULT: all projects exited zero".
    #
    # This is the same fault as the collected-zero one below, one step along: a run that collected tests and
    # executed none of them is as empty as a run that collected none. It is stated separately because it
    # needs its own sentence - "your filter matched something, and every one of them was skipped" is a
    # different thing for a reader to fix.
    if ($collected -gt 0 -and $executedAll -eq 0) {
        Write-Host ""
        Write-Host "RESULT: EVERY TEST THIS RUN SELECTED WAS SKIPPED - nothing executed, so this is not a pass."
        Write-Host ""
        Write-Host "  $collected test(s) were collected across the run and NONE of them ran. A test carrying a"
        Write-Host "  static Skip is collected and counted like any other, so a count alone cannot tell this"
        Write-Host "  apart from a real run - which is why it is checked separately."
        if ($Filter -ne "") { Write-Host "  The filter was: $Filter" }
        Write-Host ""
        Write-Host "  A skip proves nothing. Name a test that runs."
        exit 8
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
} finally {
    # Released here on EVERY path - a normal end, every exit code above, an exception, Ctrl+C. The next
    # run on this machine is refused only while a live process holds the handle, so the handle must not
    # outlive the run.
    if ($null -ne $gateLock) { Exit-GateLock $gateLock }
}
