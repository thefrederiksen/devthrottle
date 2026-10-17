#Requires -Version 5.1
<#
.SYNOPSIS
    Run this repository's tests locally. THE one command - do not hand-roll a dotnet test invocation.

.DESCRIPTION
    THE DEFAULT RUN IS ABOUT TWO MINUTES, AND THAT IS THE POINT. Local is the gate for ordinary changes
    (issue #1156), so the gate has to be cheap enough that nobody is tempted to skip it.

    WHAT THE DEFAULT RUNS: every suite that finishes inside the two-minute budget, PLUS the three installer
    test projects. They start together and the wall clock is the slowest of them, not the sum.

    COUNTS, MEASURED 2026-10-10 FROM THE TRX FILES OF A DEFAULT RUN - not estimated, and not copied forward:
      Core.UnitTests 1418    Avalonia 937    Reclaim 310    Launcher 218    Engine 102    HostedAgent 89
      Terminal 82    installer: setup.Tests 39, setup-engine.Tests 875, setup-cli.Tests 74
    3156 in the default suites, 4144 including the installer. (The 2026-09-13 figures this header carried
    until October - 1068 and 1634 - were a third of the truth within four weeks.)

    Gateway.UnitTests LEFT this list on 2026-09-13 at 4,267 tests - see the parked block below and issue
    #2824 - and stood at 10,344 on 2026-10-10. Re-measure before changing these numbers; the per-project
    comments below were carried forward for months after they stopped being true.

    The installer projects live outside cc-director.sln and are built separately here. They are in the
    default run because they are fast and because the thing they cover - the first screen a new user
    ever sees - was running nowhere locally at all.

    WHAT THE DEFAULT NO LONGER RUNS - AND THIS IS DELIBERATE, NOT AN OVERSIGHT. Three suites are PARKED
    behind -Parked because none can meet the budget:

      CcDirector.Gateway.Tests   - host-bound. Run as several concurrent processes, each given part of
                                   the suite (scripts\gateway-test-shards.ps1): about 10 minutes in
                                   Release on SOREN_NORTH (October 2026), where it took 2 hours 9
                                   minutes as one process on the v2.18.0 candidate. It no longer has a lock of
                                   its own, so a hand-run "dotnet test" of it blocks nobody. Its pure tests were
                                   split out into CcDirector.Gateway.UnitTests, which was in the default
                                   run until 2026-09-13 and is now parked beside it (issue #2824); what is
                                   parked here is the host-bound remainder.
      CcDirector.Core.Tests      - 11 minutes on a quiet machine and 33 with the fleet busy. Nothing is
                                   wrong with it; it is simply far outside the budget. 4,929 tests on
                                   2026-10-10.
      CcDirector.Gateway.UnitTests - the pure half of the Gateway tests, parked 2026-09-13 when it grew
                                   past the ceiling (issue #2824); 10,344 tests on 2026-10-10. Its block
                                   in $parkedProjects below says what would bring it back.

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

    ONE RELEASE GATE AT A TIME, AND A SECOND ONE IS REFUSED, NEVER QUEUED. Before building, a -Parked
    or -Gateway run takes the release-gate lock (scripts\gate-lock.ps1). If it is held by a live
    process, this run stops within seconds with exit 6 naming the holder's process, session, commit and
    command - and it does not build, it does not start, and it does not wait. Between 8 and 10 October 2026 nineteen release-gate runs each
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
    result file, exit 5 means part of the filter matched nothing (or -ExpectTests was not met), exit 6
    means another release gate is in progress and this one was refused before building
    (and exit 2, as for a bad argument, means the lock's own location is broken), exit 7 means a -Parked
    or -Gateway run was refused before building because the disk is too full to finish it, exit 10 means
    a -RerunFailed found every failed test FLAKY (still a failure - see THE FLAKY TEST RULE), and exit 9 means a
    suite NEVER RAN or did not finish: it executed zero tests with no filter, its result file says the
    run was aborted, or its outcome is one of the unfinished ones. None of them is a test failure
    (exit 1) and none of them is ever evidence.

    THE TWO-MINUTE BUDGET IS ONE DEADLINE FROM LAUNCH, NOT TWO MINUTES PER SUITE. Until October 2026 each
    suite was waited for in turn with its own full budget, so a suite later in the list had every earlier
    wait on top of its own: Avalonia ran past the ceiling in 102 of 246 default runs and was never
    stopped. Now every suite is measured against the same deadline set when they were launched.

    A -Parked OR -Gateway RUN NAMES A HUNG TEST. Every suite in those runs gets dotnet test's hang and
    crash detection: a test that runs longer than the hang timeout is dumped, named in a Sequence file
    in the run folder, and its host is killed, which turns "the release gate is slow" into "test X hung".
    A crashed host leaves a dump the same way. The default run does not need it - its ceiling is two
    minutes and the kill is already here.

    EVERY TEST PROCESS WRITES ITS TEMPORARY FILES INSIDE THE RUN FOLDER, AND THEY ARE DELETED WHEN THE RUN
    ENDS, green or red. Test runs had left tens of thousands of folders in the user's TEMP and filled the
    disk under the v2.18.0 gate; see $tempRoot below. A -Parked or -Gateway run also refuses to start
    (exit 7) when the disk is too full to finish it.

    EVERY RUN WRITES A TRX FILE AND PRINTS ITS OUTCOME AND TEST COUNT. That pair, not the console
    "Passed!" line, is the verdict - see the comment above the run loop for why. A green with a collapsed
    count is the result most worth being able to go back and check.

    QUARANTINED TESTS NEVER DECIDE A VERDICT. A test tagged [Trait("Gate", "Quarantine")] is excluded from
    EVERY run of this script - default, -Parked, -Gateway - by a filter added to each suite, and runs only
    under -Quarantine, whose result never fails anything. Quarantine is where a test goes that depends on
    the machine (real git, real processes, the clock) on its way out: out of the gate the same day, so the
    gate's red keeps meaning a code defect. It is a waiting room, not a home - under the owner's ruling of
    13 October 2026 a quarantined test is deleted. What it covered, if that matters, is new work: a test
    against a fake that cannot depend on the machine.

.PARAMETER Quarantine
    Run ONLY the quarantined tests (trait Gate=Quarantine), in every suite including the parked Core.Tests
    and Gateway.UnitTests. Gateway.Tests is left out unless -Gateway is passed too, and -Quarantine -Gateway
    then runs that suite's quarantined tests alone: a run that includes the Gateway suite takes the
    release-gate lock, and with it included a look at the quarantine was REFUSED for the whole length of
    someone else's release gate (found on 2026-10-11, when another session's full run held the lock for
    about two hours).
    Prints what passed and failed and exits 0 whenever the tests ran, failures or not: a quarantined test
    never decides a verdict. (A build failure, a held lock or too little disk space still stop it, because
    then nothing ran.) Writes no gate record, keeps its run folder so the failures can be read, and takes
    the release-gate lock only with -Gateway, the one quarantine run that includes the Gateway suite.

    A SUITE MAY NOT BE QUARANTINED WHOLE. Every gate suite must execute at least one test; a suite with
    every test quarantined executes none and the run ends NEVER RAN. Delete such a suite from the list, or
    fix its tests.

    THE FLAKY TEST RULE - A FAILURE IS RE-RUN TEST BY TEST, NEVER BY RE-RUNNING THE GATE (issue #3879).
    A test failure must mean a code defect. When the release gate fails:

      1. Re-run ONLY its failed tests, on the same commit and configuration, once:
             .\scripts\test-local.ps1 -RerunFailed <the run folder the red run printed>
         It refuses a run that did not finish (a crash, a hang, a full disk: fix that and run the gate
         again), more than 50 distinct failed tests (that is the machine or a real defect, not flakiness),
         a different commit, a dirty tree, and a second re-run of the same run.
      2. A test that fails AGAIN is a code defect. Exit 1: fix it on main and cut a new candidate.
      3. A test that PASSES on the re-run is FLAKY - a defective test. Exit 10, and the gate still fails:
         a FLAKY gate record is written beside the green ones, naming each flaky test, and
         scripts\assert-gated.ps1 refuses it on its own. A test that failed on correct code is DELETED
         the same day in a test-only pull request merged to main - pre-approved by the owner (ruling of
         13 October 2026) - unless it shows a real product defect, and then the product is fixed.
      4. Then, from a clean checkout of main that contains the fix:
             .\scripts\test-local.ps1 -ClearFlaky <candidate>
         builds a throwaway worktree of the CANDIDATE'S product code with MAIN'S test projects, and runs
         each flaky test there five times. A test is cleared as DELETED when it is no longer collected AND
         no method of that name is left in any test project on main (a rename or a move is not a delete),
         or as FIXED when it passes all five AND a file declaring its class changed since the candidate.
         A test that passes five times with nobody having changed it is NOT cleared: a test that failed on
         correct code and then happened to pass still does not work. DELETED is the expected route under
         the ruling in step 3; FIXED remains as a mechanism, and is not how such a test is normally
         handled. If main's tests do not build or do not pass against the candidate's product code, the
         fix needed a product change: the candidate is not cleared, and the answer is a new candidate. All cleared: a clearance is recorded, exit 0.
      5. scripts\assert-gated.ps1 then accepts the candidate, once the commit the clearance was made at is
         on origin/main and the runs log shows this was the candidate's ONLY failed release gate (a second
         whole gate followed by a re-run of the better one is a re-roll). It is tagged AS IT IS. Nothing
         re-runs the whole gate to learn whether a failure was FLAKY, and nothing accepts a FLAKY test
         that is still there unchanged.

    WHAT "FLAKY" DOES AND DOES NOT PROVE. FLAKY is the name of this verdict, its record and its exit code;
    in prose, say the test failed on correct code. It proves the test passed when run on its own, on the same
    commit. A real product defect that only shows under the load of the full parallel suite - a race, an
    ordering fault - can pass alone, and would then read as FLAKY. That is why such a test is never
    accepted as it is: it is deleted, and whoever deletes it reads it first, because that is the place the
    defect would be. If it shows a real product defect, the product is fixed, and that is a new candidate. A suite that
    exited nonzero without naming a failed test cannot be re-run test by test and is refused.

    A re-run that hangs or crashes is named the same way as in the release gate (hang and crash
    detection is always on for a targeted run), and its result is written into the red run's own run.json
    as well as its own folder.

.PARAMETER RerunFailed
    The folder of a red run (verdict FAILED). Re-runs exactly its failed tests, by fully qualified method
    name, on the same commit and configuration. Exit 10 when every one passes (FLAKY), 1 when any fails
    again. See THE FLAKY TEST RULE above.

.PARAMETER ClearFlaky
    A candidate commit whose release gate was found FLAKY. Run on a clean checkout of main after the fix
    merged; records the clearance assert-gated.ps1 needs. See THE FLAKY TEST RULE above.

.PARAMETER Gateway
    Run ONLY the parked Gateway suite (host-bound). Takes the release-gate lock; refused with exit 6 if
    another run holding the Gateway suite is in progress.

.PARAMETER Parked
    Also run the three parked suites - Gateway.Tests, Core.Tests and Gateway.UnitTests. This is the
    RELEASE gate. Takes the release-gate lock before building; a second one on this machine is refused
    with exit 6, not queued. The Gateway suite takes about 10 minutes of it (four processes side by side).

.PARAMETER Fast
    Retained for callers that pass it. The default IS fast now, so this is a no-op.

.PARAMETER KeepRun
    Keep the run folder after a GREEN run. By default a green run's folder is deleted - a green needs no
    reading, and every run's record (commit, tree state, verdict, each suite's counts) is appended to the
    runs log beside the gate records, so nothing a later reader needs goes with the folder - and a red
    run's folder is always kept. 523 folders and 2.6 GB had piled up in TEMP by 2026-10-10. Pass
    this when the result files themselves are the point: per-class timings, a count to quote.

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
    [switch]$KeepRun,
    [switch]$Quarantine,
    [string]$Filter = "",
    [int]$ExpectTests = 0,
    [string]$Configuration = "Debug",
    [string]$RerunFailed = "",
    [string]$ClearFlaky = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$sln = Join-Path $repoRoot "cc-director.sln"
. (Join-Path $PSScriptRoot "gate-lock.ps1")
. (Join-Path $PSScriptRoot "gateway-test-shards.ps1")

if ($Gateway -and $Parked) {
    Write-Error "-Gateway already runs a parked suite on its own; pass one or the other."
    exit 2
}
if ($Quarantine -and ($Parked -or $ExpectTests -gt 0)) {
    Write-Error "-Quarantine runs the quarantined tests of every suite except Gateway.Tests (add -Gateway for that suite alone) and never gates; it cannot be combined with -Parked or -ExpectTests."
    exit 2
}

# THE QUARANTINE TRAIT (see the header). Every suite run gets "Gate!=Quarantine" ANDed onto its filter, so
# a quarantined test never runs in a gate; -Quarantine flips it to run only them.
$QuarantineExclude = "Gate!=Quarantine"
$QuarantineOnly = "Gate=Quarantine"
# The caller's filter, ANDed with the quarantine trait: what each suite is actually given. $Filter itself stays
# the caller's words, because the per-term check below reads its "FullyQualifiedName~" terms.
$traitFilter = $(if ($Quarantine) { $QuarantineOnly } else { $QuarantineExclude })
$suiteFilter = $(if ($Filter -ne "") { "($Filter)&$traitFilter" } else { $traitFilter })

# THE TWO-MINUTE BUDGET IS THE RULE THIS LIST ENCODES. A suite is in the default run if it finishes
# inside it, and parked if it does not. DURATIONS ONLY - the test counts live once in the header, with
# the date they were measured, because keeping them in two places is what let one drift by a factor of
# thirty. Measured 2026-10-10 from a default run on a quiet machine, as each suite's own log reports it:
#   Avalonia 117s   Core.UnitTests 63s   Engine 41s   HostedAgent 23s   Terminal 21s   Launcher 14s
#   Reclaim 10s   installer: setup-engine.Tests 11s, setup.Tests 3s, setup-cli.Tests 1s
# They start together, so the default costs about the slowest one - now Avalonia, AT the ceiling: on a
# loaded machine it goes over and is stopped. The two Avalonia fixes in the Release Process Fix mission
# (GitChangesViewHiddenTabTests, RemovedDirectorTunnelTests) take about 36 seconds off it. The 2026-09-13
# figures this comment carried (Avalonia 32s, HostedAgent 40s) were off by a factor of three within a
# month, which is the reason the date is on the line.
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
    "src\CcDirector.Reclaim.Tests\CcDirector.Reclaim.Tests.csproj",
    # The delivery rig's rate arithmetic and scenario pass rules (Prompt Delivery mission, October 2026). Pure
    # functions, no agent is started; it builds the rig, which is the cost.
    "src\CcDirector.DeliveryQualification.Tests\CcDirector.DeliveryQualification.Tests.csproj"
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
# Gateway.Tests runs as several processes (see gateway-test-shards.ps1); Core.Tests costs 11 to 33 minutes of its
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
#
# ONE DEADLINE, SET WHEN THE SUITES ARE LAUNCHED. Each wait below gets only the time left on it. The
# first version waited for each suite in turn with the full budget, which gave the last suite in the
# list its two minutes on top of every wait before it - a ceiling that held for the first suite only.
$BudgetSeconds = 120

# A SINGLE TEST THAT RUNS THIS LONG HAS HUNG, in the -Parked and -Gateway runs that have no ceiling.
# Measured from 523 saved runs on 2026-10-10: the slowest legitimate test on main takes about six and a
# half minutes (RetiredMessagingWordsTests, being cut down), the next about three. Ten minutes is above
# every real test and far below the release gate's own length, so a hang is named within the run instead
# of holding it until a person notices. This is not a raised timeout: it is the opposite of waiting.
$HangTimeoutMinutes = 10

$toRun = @()
if ($Gateway) {
    # -Gateway is the "only that one suite" switch, so it stays exactly that and pulls in nothing else.
    $toRun = @($gatewayProject)
} else {
    $toRun = $defaultProjects + $installerProjects
    if ($Parked) { $toRun += $parkedProjects }
    # Every parked suite but the Gateway one, which takes the machine's one release-gate lock; -Quarantine -Gateway
    # runs it on its own (see the header).
    if ($Quarantine) { $toRun += @($parkedProjects | Where-Object { $_ -ne $gatewayProject }) }
}


# ---------------------------------------------------------------------------------------------------
# THE RUN'S IDENTITY AND ITS FOLDER, settled before anything is built, because the lock below names both.
#
# WHICH COMMIT THIS RUN CERTIFIES, AND WHETHER THE TREE WAS CLEAN. A run folder used to hold result files
# and logs only: across 523 saved folders nobody could say which commit any of them had run on, which is
# how a green quietly goes stale when main moves. Both facts are written into run.json in the run folder
# at launch, printed in the verdict, appended with the outcome to the runs log beside the gate records
# when the run ends, and - for a green, unfiltered -Parked run - recorded where scripts\assert-gated.ps1
# can find them when the tag step asks whether a candidate was gated. A dirty tree is recorded as dirty,
# never hidden: the run still happens, the record says what it tested.
$commit = (& git -C $repoRoot rev-parse HEAD).Trim()
$dirtyFiles = @(& git -C $repoRoot status --porcelain | Where-Object { $_ -ne "" })
$treeClean = ($dirtyFiles.Count -eq 0)

# THE TEST NAME A RE-RUN USES is the fully qualified METHOD name - TestMethod/@className plus "." plus
# TestMethod/@name from the result file's definitions - never the display name in UnitTestResult/@testName,
# which carries a theory's arguments and cannot be passed to a filter. Every row of a theory shares the one
# method name, so a theory with one failing row is re-run whole: stricter, never weaker.
function Read-TrxTestOutcomes([string] $TrxPath) {
    $raw = [string] (Get-Content $TrxPath -Raw)
    if ([string]::IsNullOrWhiteSpace($raw)) { return @() }
    [xml] $doc = $raw
    $methodById = @{}
    foreach ($def in @($doc.TestRun.TestDefinitions.UnitTest)) {
        if ($null -eq $def) { continue }
        $methodById[[string] $def.id] = ([string] $def.TestMethod.className) + "." + ([string] $def.TestMethod.name)
    }
    $rows = @()
    foreach ($u in @($doc.TestRun.Results.UnitTestResult)) {
        if ($null -eq $u) { continue }
        # A result with no definition keeps Test = $null rather than being dropped, so a caller can refuse
        # a failure it cannot name instead of losing it.
        $rows += [pscustomobject]@{ Test = $methodById[[string] $u.testId]; Outcome = [string] $u.outcome; Display = [string] $u.testName }
    }
    return $rows
}

function Get-TrxFailedTests([string] $TrxPath) {
    $failed = @(Read-TrxTestOutcomes $TrxPath | Where-Object { $_.Outcome -eq "Failed" })
    $unnamed = @($failed | Where-Object { $null -eq $_.Test })
    if ($unnamed.Count -gt 0) {
        Stop-Refused ("{0} failed result(s) in {1} have no test definition, so they cannot be re-run by name (first: {2}). Read the result file; a re-run that dropped them would hide a failure." -f $unnamed.Count, $TrxPath, $unnamed[0].Display)
    }
    return @($failed | ForEach-Object { $_.Test } | Sort-Object -Unique)
}

# A filter that names exactly these methods. The filter language gives \ ( ) & | = ! ~ a meaning, so each
# is escaped with a backslash; a name is matched by equality, never by "contains", so a re-run of one
# test cannot quietly pick up its neighbours.
function New-ExactTestFilter([string[]] $Tests) {
    $terms = foreach ($t in $Tests) {
        $escaped = ($t -replace '([\\()&|=!~])', '\$1')
        "FullyQualifiedName=$escaped"
    }
    return ($terms -join "|")
}

# ---------------------------------------------------------------------------------------------------
# A TARGETED RUN: -RerunFailed or -ClearFlaky. Each runs a short, named list of tests instead of whole
# suites, and the rule they carry out is in the header (THE FLAKY TEST RULE). Everything here is decided
# BEFORE the run folder exists, so a refusal leaves nothing behind.
$targetMode = ""
$targets = @()          # one entry per test: Suite, Project, Test (the fully qualified method name)
$targetRepeat = 1
$sourceRun = $null      # -RerunFailed: the red run being re-run
$sourceRunFolder = ""
$flakyRecord = $null    # -ClearFlaky: the FLAKY gate record being cleared
$flakyRecordPath = ""
$candidate = ""
# A re-run of more than this many distinct tests is not a re-run: that many failures at once is the
# machine (a full disk, a dead service) or a real defect, and the answer is to fix that and run the gate.
$MaxRerunTests = 50
# How many times -ClearFlaky runs each test being cleared. A flaky test passes most of the time by
# definition, so one pass proves little; five in a row, with its code changed, is the bar.
$ClearFlakyRepeat = 5

function Stop-Refused([string] $Message) {
    Write-Host ""
    Write-Host "RESULT: REFUSED - $Message"
    Write-Host ""
    exit 2
}

# Every suite this script knows, by the name used for its result file.
function Get-ProjectForSuite([string] $Suite) {
    $Suite = Get-SuiteBaseName $Suite
    foreach ($proj in ($defaultProjects + $installerProjects + $parkedProjects)) {
        if ((Split-Path -Leaf ([System.IO.Path]::GetDirectoryName((Join-Path $repoRoot $proj)))) -eq $Suite) { return $proj }
    }
    return $null
}

if ($RerunFailed -ne "" -or $ClearFlaky -ne "") {
    if ($RerunFailed -ne "" -and $ClearFlaky -ne "") { Stop-Refused "pass -RerunFailed or -ClearFlaky, not both." }
    if ($Gateway -or $Parked -or $Quarantine -or $Filter -ne "" -or $ExpectTests -gt 0) {
        Stop-Refused "-RerunFailed and -ClearFlaky choose their own tests; they cannot be combined with -Gateway, -Parked, -Quarantine, -Filter or -ExpectTests."
    }
    if (-not $treeClean) {
        Stop-Refused ("the tree is dirty ({0} changed or untracked files). A targeted run proves something about a COMMIT, so it runs only on a clean one." -f $dirtyFiles.Count)
    }
}

if ($RerunFailed -ne "") {
    $targetMode = "RERUN"
    $resolved = Resolve-Path -LiteralPath $RerunFailed -ErrorAction SilentlyContinue
    if ($null -eq $resolved) { Stop-Refused "the run folder '$RerunFailed' does not exist. A red run's folder is kept; its path is printed at the end of that run." }
    $sourceRunFolder = [string] $resolved
    $sourceJson = Join-Path $sourceRunFolder "run.json"
    if (-not (Test-Path $sourceJson)) { Stop-Refused "'$sourceRunFolder' holds no run.json, so it is not a run folder of this script." }
    $sourceRun = Get-Content $sourceJson -Raw | ConvertFrom-Json
    if ($sourceRun.verdict -ne "FAILED" -or $sourceRun.exitCode -ne 1) {
        Stop-Refused ("that run's verdict was '{0}' (exit {1}). Only a run that FINISHED with failing tests can be re-run test by test. A run that never ran, did not finish, or did not build has no list of failures to trust - fix its cause and run it again." -f $sourceRun.verdict, $sourceRun.exitCode)
    }
    if ($null -ne $sourceRun.PSObject.Properties["rerun"] -and $null -ne $sourceRun.rerun) {
        Stop-Refused "that run has already been re-run (verdict $($sourceRun.rerun.verdict), in $($sourceRun.rerun.runFolder)). A failure gets ONE re-run: re-running until it passes is exactly how a flaky test gets quietly accepted."
    }
    if ($sourceRun.commit -ne $commit) {
        Stop-Refused "that run was on commit $($sourceRun.commit) and this checkout is at $commit. A re-run must be on the SAME commit, or it proves nothing about the run it is re-running. Check out $($sourceRun.commit) (detached, in its own worktree) and run this there."
    }
    if ($PSBoundParameters.ContainsKey("Configuration") -and $Configuration -ne $sourceRun.configuration) {
        Stop-Refused "that run was $($sourceRun.configuration) and this asks for $Configuration. A re-run uses the configuration of the run it re-runs; leave -Configuration off."
    }
    $Configuration = [string] $sourceRun.configuration
    foreach ($suite in @($sourceRun.suites)) {
        # A suite that exited nonzero WITHOUT a failed test - a crash after its results were written, a
        # fixture that failed outside any one test - failed for a reason no re-run of named tests can
        # reach, so it is refused here instead of being left out of the re-run and so out of the verdict.
        if ($suite.outcome -ne "Failed" -and $null -ne $suite.exitCode -and [int] $suite.exitCode -ne 0) {
            Stop-Refused "suite $($suite.name) exited $($suite.exitCode) with outcome=$($suite.outcome) - it failed outside any single test, so re-running failed tests cannot judge it. Read $(Join-Path $sourceRunFolder "$($suite.name).log"), fix the cause, and run the gate again."
        }
        if ($suite.outcome -ne "Failed") { continue }
        $trxPath = Join-Path $sourceRunFolder "$($suite.name).trx"
        if (-not (Test-Path $trxPath)) { Stop-Refused "suite $($suite.name) failed in that run but its result file $trxPath is missing." }
        $proj = Get-ProjectForSuite $suite.name
        if ($null -eq $proj) { Stop-Refused "suite $($suite.name) is not a suite this script knows." }
        $failedHere = @(Get-TrxFailedTests $trxPath)
        if ($failedHere.Count -eq 0) { Stop-Refused "suite $($suite.name) reported outcome=Failed but its result file names no failed test, so there is no list to re-run. Read $trxPath." }
        foreach ($test in $failedHere) {
            # A Gateway.Tests shard's failures re-run as Gateway.Tests tests: the re-run runs each project once, by
            # name, and keys what it saw by the suite's base name.
            $targets += [pscustomobject]@{ Suite = (Get-SuiteBaseName ([string] $suite.name)); Project = $proj; Test = $test }
        }
    }
    if ($targets.Count -eq 0) { Stop-Refused "no suite in that run reported failed tests, so there is nothing to re-run." }
    if ($targets.Count -gt $MaxRerunTests) {
        Stop-Refused ("{0} distinct tests failed in that run; a re-run takes at most {1}. That many failures at once is the machine (a full disk, a service that is down) or a real defect, not a flaky test. Read the logs in {2}, fix the cause, and run the gate again." -f $targets.Count, $MaxRerunTests, $sourceRunFolder)
    }
}

if ($ClearFlaky -ne "") {
    $targetMode = "CLEAR"
    $targetRepeat = $ClearFlakyRepeat
    $candidate = (& git -C $repoRoot rev-parse --verify --quiet "$ClearFlaky^{commit}" 2>$null)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($candidate)) { Stop-Refused "'$ClearFlaky' is not a commit in this repository." }
    $candidate = $candidate.Trim()
    $recordDir = Get-GateRecordDirectory
    $flakyFiles = @()
    if (Test-Path $recordDir) { $flakyFiles = @(Get-ChildItem -Path $recordDir -Filter "$candidate-*.json" | Sort-Object Name) }
    foreach ($f in $flakyFiles) {
        $rec = Get-Content $f.FullName -Raw | ConvertFrom-Json
        if ($rec.verdict -eq "FLAKY") { $flakyRecord = $rec; $flakyRecordPath = $f.FullName }
    }
    if ($null -eq $flakyRecord) {
        Stop-Refused "no FLAKY gate record exists for $candidate in $recordDir. -ClearFlaky clears the flaky tests that a -RerunFailed of that candidate's red release gate named."
    }
    & git -C $repoRoot merge-base --is-ancestor $candidate $commit
    if ($LASTEXITCODE -ne 0) {
        Stop-Refused "this checkout ($commit) does not contain the candidate $candidate. Clear the flaky tests on main AFTER the fix merged: check out main in its own worktree and run this there."
    }
    if ($PSBoundParameters.ContainsKey("Configuration") -and $Configuration -ne $flakyRecord.configuration) {
        Stop-Refused "the candidate was gated in $($flakyRecord.configuration) and this asks for $Configuration; leave -Configuration off."
    }
    $Configuration = [string] $flakyRecord.configuration
    foreach ($t in @($flakyRecord.rerun.flaky)) {
        $proj = Get-ProjectForSuite $t.suite
        if ($null -eq $proj) {
            Stop-Refused "the flaky test $($t.test) was in suite $($t.suite), which this script no longer runs. Removing a whole suite is not clearing a test."
        }
        $targets += [pscustomobject]@{ Suite = [string] $t.suite; Project = $proj; Test = [string] $t.test }
    }
    if ($targets.Count -eq 0) { Stop-Refused "the FLAKY record $flakyRecordPath names no flaky test." }
}

if ($targetMode -ne "") {
    $toRun = @($targets | ForEach-Object { $_.Project } | Select-Object -Unique)
}
$logDir = Join-Path ([System.IO.Path]::GetTempPath()) ("cc-test-local-" + [Guid]::NewGuid().ToString("N").Substring(0,8))
New-Item -ItemType Directory -Path $logDir -Force | Out-Null

# EVERY TEST PROCESS GETS ITS OWN TEMPORARY FOLDER INSIDE THE RUN FOLDER, AND IT IS DELETED WHEN THE RUN
# ENDS - green or red. The result files and logs sit beside it and are kept as before.
#
# WHY. Thousands of tests create a folder under Path.GetTempPath() and never remove it. Measured on
# 2026-10-11: one run of Gateway.UnitTests alone left 263 folders and 83 MB behind, the user's TEMP held
# 35,849 folders, and 20,505 of them - 44 GB - had been written in the previous 24 hours. During the
# v2.18.0 release gate drive C: filled: all 1,675 Gateway.UnitTests failures and all 45 Core.Tests
# failures in that run were "not enough space on the disk", and the "SQLite Error 10: disk I/O error"
# failures (issue #3880) are very likely the same thing. A gate that fills the disk fails for a reason
# that has nothing to do with the code, which is exactly the kind of red this gate must never produce.
#
# TEMP and TMP are set for each child, so Path.GetTempPath() inside the test host lands here. The lock
# does not move with it: gate-lock.ps1 derives its path from the local application data folder precisely
# so that a changed TEMP cannot split it. The folder names are kept
# SHORT (t\0, t\1, ...) because tests build deep paths under it and Windows still has a 260-character
# limit in places; run.json maps each number to its suite.
$tempRoot = Join-Path $logDir "t"

$runRecord = [ordered]@{
    commit        = $commit
    treeClean     = $treeClean
    dirtyFiles    = @($dirtyFiles | Select-Object -First 50)
    parked        = [bool] $Parked
    gateway       = [bool] $Gateway
    quarantine    = [bool] $Quarantine
    configuration = $Configuration
    filter        = $Filter
    suiteFilter   = $suiteFilter
    expectTests   = $ExpectTests
    startedUtc    = (Get-Date).ToUniversalTime().ToString("o")
    machine       = [Environment]::MachineName
    user          = [Environment]::UserName
    session       = (Get-GateLockSessionName)
    directory     = $repoRoot
    runFolder     = $logDir
    finishedUtc   = $null
    exitCode      = $null
    verdict       = "RUNNING"
    mode          = $(if ($targetMode -eq "") { "SUITES" } else { $targetMode })
    tempLeftBehind = $null
    suites        = @()
}
# What each suite left in its temporary folder, measured just before the folder is deleted, keyed by
# suite name. Recorded per suite in run.json and the runs log, so the free-space floor below can be set
# from real runs instead of from one measurement.
$tempUsage = @{}
$running = $null
$targetedRuns = @()

# Deletes a folder tree and returns how many items are left in it (0: it is gone). rd with the \\?\ prefix,
# because Remove-Item in Windows PowerShell 5.1 fails on paths over 260 characters and tests do build
# paths that deep; rd also removes a junction without following it into its target.
function Remove-FolderTree([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return 0 }
    if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
        & $env:ComSpec /d /c "rd /s /q `"\\?\$Path`" >nul 2>&1"
    } else {
        Remove-Item -Recurse -Force -LiteralPath $Path -ErrorAction SilentlyContinue
    }
    if (-not (Test-Path -LiteralPath $Path)) { return 0 }
    return [Math]::Max(1, @(Get-ChildItem -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue).Count)
}

# Measures what a folder holds. This is a MEASUREMENT for run.json, never part of the verdict, so it
# must not throw: under "Stop" a throw here would skip Stop-Gate's records and exit code entirely. Windows
# PowerShell 5.1 refuses the \\?\ long-path prefix in System.IO ("Illegal characters in path"), so a
# folder too deep to read - or a junction a test left broken, or one deleted mid-walk - is COUNTED as
# unreadable, never silently treated as empty. Junctions and symbolic links are not followed.
function Measure-FolderTree([string] $Path) {
    $files = 0; $bytes = [long] 0; $unreadable = 0
    $stack = New-Object System.Collections.Stack
    $stack.Push($Path)
    while ($stack.Count -gt 0) {
        $dirPath = [string] $stack.Pop()
        try {
            $d = New-Object System.IO.DirectoryInfo $dirPath
            foreach ($f in $d.GetFiles()) { $files++; $bytes += $f.Length }
            foreach ($sub in $d.GetDirectories()) {
                if (($sub.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) { $stack.Push($sub.FullName) }
            }
        } catch {
            $unreadable++
        }
    }
    return [ordered]@{ files = $files; bytes = $bytes; megabytes = [Math]::Round($bytes / 1MB, 1); unreadableFolders = $unreadable }
}

# Ends every test process this run started that is still alive, with its whole tree. Only reached when
# the run is stopped before its verdict (Ctrl+C, an exception): deleting the temporary folders under a
# live test host would pull files out from under it. Only processes THIS run started are touched.
function Stop-RunProcesses {
    foreach ($r in @(@($running) + @($targetedRuns))) {
        if ($null -eq $r -or $null -eq $r.Process -or $r.Process.HasExited) { continue }
        if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
            & $env:ComSpec /d /c "taskkill /T /F /PID $($r.Process.Id) >nul 2>&1"
        } else {
            $r.Process.Kill()
        }
        $null = $r.Process.WaitForExit(10000)
    }
}

# Measures and deletes the run's temporary folders. Called on every path that ends a run. A folder that
# survives is NAMED, never ignored: it means a process the run started is still alive and holding a
# file, which the person running the gate needs to know.
function Remove-RunTemp {
    if (-not (Test-Path -LiteralPath $tempRoot)) { return }
    foreach ($dir in @(Get-ChildItem -LiteralPath $tempRoot -Directory)) {
        $m = Measure-FolderTree $dir.FullName
        $suite = $dir.Name
        $match = @(@($running) + @($targetedRuns) | Where-Object { $null -ne $_ -and $_.TempName -eq $dir.Name })
        if ($match.Count -eq 1) { $suite = $match[0].Name }
        # A targeted run starts one suite several times; its passes add up under the one suite name.
        if ($tempUsage.Contains($suite)) {
            $m.files += $tempUsage[$suite].files
            $m.bytes += [long] ($tempUsage[$suite].bytes)
            $m.megabytes = [Math]::Round($m.bytes / 1MB, 1)
            $m.unreadableFolders += $tempUsage[$suite].unreadableFolders
        }
        $tempUsage[$suite] = $m
    }
    $left = Remove-FolderTree $tempRoot
    $runRecord.tempLeftBehind = $left
    if ($left -gt 0) {
        Write-Host ("ERROR: the run's temporary folder could not be fully deleted - {0} item(s) remain in {1}." -f $left, $tempRoot)
        Write-Host "  A process this run started is still alive and holding a file. Find it (a testhost or dotnet process"
        Write-Host "  whose working files are under that folder), end it, and delete the folder by hand."
    } else {
        $total = 0.0; $unreadable = 0
        foreach ($v in $tempUsage.Values) { $total += $v.megabytes; $unreadable += $v.unreadableFolders }
        if ($unreadable -gt 0) {
            Write-Host ("Temporary folders deleted: the test processes had left at least {0} MB in them; {1} folder(s) were too deep or broken to measure." -f [Math]::Round($total, 1), $unreadable)
        } else {
            Write-Host ("Temporary folders deleted: the test processes had left {0} MB in them." -f [Math]::Round($total, 1))
        }
    }
}

function Write-RunRecord {
    if (Test-Path $logDir) {
        $runRecord | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $logDir "run.json") -Encoding Ascii
    }
}
# EVERY run, green or red, default or parked, appends its finished record as one line to a runs log beside
# the gate records. The run folder of a green run is deleted (see Stop-Gate), so without this a green
# default run would leave no trace of the commit it ran on - the very gap finding C5 is about.
function Write-RunsLog {
    $logPath = Join-Path (Split-Path -Parent (Get-GateRecordDirectory)) "runs.jsonl"
    New-Item -ItemType Directory -Force (Split-Path -Parent $logPath) | Out-Null
    Add-Content -Path $logPath -Value ($runRecord | ConvertTo-Json -Depth 5 -Compress) -Encoding Ascii
}
Write-RunRecord

# EVERY VERDICT LEAVES THROUGH HERE. The exit code and the verdict word go into run.json beside the commit
# and the suite results, so the folder says what the run concluded, not only what it saw. A green,
# unfiltered -Parked run is additionally recorded under the gate record directory, named by its commit:
# that file is what scripts\assert-gated.ps1 reads, and it outlives the run folder. "exit" here flows
# through the finally block below, so the release-gate lock is released as on every other path.
function Stop-Gate([int] $Code, [string] $Verdict) {
    # A verdict can be reached while other suites of the same pass are still running (a targeted run stops
    # at the first suite that did not finish). End them before their folders are deleted under them.
    Stop-RunProcesses
    Remove-RunTemp
    $runRecord.finishedUtc = (Get-Date).ToUniversalTime().ToString("o")
    $runRecord.exitCode = $Code
    $runRecord.verdict = $Verdict
    if ($null -ne $running) {
        $runRecord.suites = @($running | ForEach-Object {
            [ordered]@{
                name     = $_.Name
                outcome  = $(if ($null -ne $_.PSObject.Properties["Outcome"]) { $_.Outcome } else { "NOT-WAITED-FOR" })
                total    = $(if ($null -ne $_.PSObject.Properties["Total"]) { [int] $_.Total } else { 0 })
                executed = $(if ($null -ne $_.PSObject.Properties["Executed"]) { [int] $_.Executed } else { 0 })
                exitCode = $_.Process.ExitCode
                temp     = $tempUsage[$_.Name]
            }
        })
    }
    Write-RunRecord
    Write-RunsLog
    if ($Code -eq 0 -and $Parked -and $Filter -eq "" -and -not $Quarantine) {
        $recordDir = Get-GateRecordDirectory
        New-Item -ItemType Directory -Force $recordDir | Out-Null
        $stamp = ([DateTime]::Parse($runRecord.startedUtc)).ToUniversalTime().ToString("yyyyMMddTHHmmssZ")
        $recordPath = Join-Path $recordDir ("$commit-$stamp.json")
        $runRecord | ConvertTo-Json -Depth 5 | Set-Content -Path $recordPath -Encoding Ascii
        Write-Host "Gate record written: $recordPath"
        if (-not $treeClean) {
            Write-Host "NOTE: the tree was DIRTY, and the record says so. scripts\assert-gated.ps1 will refuse it."
        }
    }
    # A green run's folder is deleted, a red run's is kept, and -KeepRun keeps a green one. The folder is
    # only ever removed AFTER the records above are written - run.json's content is in the runs log by
    # then - so nothing a later reader needs goes with it.
    if ($Code -eq 0 -and -not $KeepRun -and $runRecord.tempLeftBehind -gt 0) {
        Write-Host "Run folder kept, although the run is green, because its temporary folder could not be deleted: $logDir"
    } elseif ($Code -eq 0 -and -not $KeepRun) {
        $leftInRun = Remove-FolderTree $logDir
        if ($leftInRun -gt 0) {
            Write-Host "ERROR: the run folder could not be fully deleted - $leftInRun item(s) remain in $logDir. Delete it by hand."
        } else {
            Write-Host "Run folder deleted (green run; pass -KeepRun to keep the result files)."
        }
    } elseif ($Code -ne 0) {
        Write-Host "Run folder kept: $logDir"
    }
    exit $Code
}

# A RELEASE GATE DOES NOT START ON A DISK THAT IS ABOUT TO FILL. The v2.18.0 gate ran drive C: out of
# space part way through and every failure after that was the disk, not the code - hours of run that
# certified nothing. So a -Parked or -Gateway run checks first and refuses with exit 7, before taking a
# lock or building anything.
#
# THE NUMBERS, AND WHERE THEY COME FROM. The temporary folders and the run folder live on the TEMP drive;
# the build output lives on the repository's drive. Measured 2026-10-11: a Release build of the solution
# is 3.8 GB; one Gateway.UnitTests run left 83 MB in its temporary folder, and the Gateway.Tests suite,
# which writes a SQLite database per test, leaves several times that; a crash or hang dump of a test host
# is hundreds of megabytes. 15 GB on the TEMP drive and 10 GB on the repository drive leaves a wide
# margin over all of that. Every run now records what each suite actually left (run.json, "temp"), so
# tune these from those records, not by guessing.
$MinFreeTempGB = 15
$MinFreeRepoGB = 10
# A FILTERED run writes a fraction of that, so the floor applies to the unfiltered runs only.
if (($Parked -or $Gateway -or $Quarantine) -and $Filter -eq "") {
    $short = @()
    foreach ($check in @(@{ Path = $logDir; Min = $MinFreeTempGB; What = "the TEMP drive (run folder and test temporary folders)" },
                         @{ Path = $repoRoot; Min = $MinFreeRepoGB; What = "the repository drive (build output)" })) {
        $root = [System.IO.Path]::GetPathRoot($check.Path)
        if ($root.StartsWith("\\")) {
            $short += ("  {0} {1}: a network path, whose free space this script cannot read. Run the release gate from a local drive." -f $root, $check.What)
            continue
        }
        $drive = New-Object System.IO.DriveInfo $root
        $freeGB = [Math]::Round($drive.AvailableFreeSpace / 1GB, 1)
        if ($freeGB -lt $check.Min) { $short += ("  {0} {1}: {2} GB free, at least {3} GB needed" -f $drive.Name, $check.What, $freeGB, $check.Min) }
    }
    if ($short.Count -gt 0) {
        Write-Host ""
        Write-Host "RESULT: REFUSED - NOT ENOUGH FREE DISK SPACE for a release gate. Nothing was built and nothing was started."
        foreach ($s in $short) { Write-Host $s }
        Write-Host ""
        Write-Host "A gate that fills the disk fails for a reason that has nothing to do with the code. Free space first -"
        Write-Host "old cc-test-local-* and other test folders in TEMP are the usual culprit (the reclaim-disk skill) -"
        Write-Host "and run this again."
        Remove-Item $logDir -Recurse -Force
        exit 7
    }
}

# ONE RELEASE GATE AT A TIME. A SECOND ONE IS REFUSED HERE, BEFORE THE BUILD, NEVER QUEUED. See the
# header and scripts\gate-lock.ps1 for why: a queue hid nineteen conflicting release gates for
# forty-five minutes each, and every one of them then executed nothing.
#
# The release-gate lock is TAKEN and held until this script ends, in the finally block below - held in a
# variable, not in a scope, so that a script run from an interactive shell (whose process survives "exit")
# releases it too. Gateway.Tests no longer has a lock of its own (scripts\gate-lock.ps1 says why).
$gateLock = $null
if ($toRun -contains $gatewayProject) {
    $command = "test-local.ps1"
    if ($Parked) { $command += " -Parked" }
    if ($Gateway) { $command += " -Gateway" }
    if ($Quarantine) { $command += " -Quarantine" }
    if ($RerunFailed -ne "") { $command += " -RerunFailed `"$RerunFailed`"" }
    if ($ClearFlaky -ne "") { $command += " -ClearFlaky $ClearFlaky" }
    $command += " -Configuration $Configuration"
    if ($Filter -ne "") { $command += " -Filter `"$Filter`"" }
    $gateLockPath = Get-ReleaseGateLockPath
    try {
        $gateLock = Enter-GateLock $gateLockPath ([ordered]@{
            session = (Get-GateLockSessionName); commit = $commit; command = $command
            directory = $repoRoot; runFolder = $logDir })
    } catch {
        # Only a sharing conflict means "held", and the helper answers that without throwing. Anything that
        # reaches here is a fault in the lock's home - permissions, a directory at the path, a read-only
        # file, a full disk - which no amount of waiting clears. Named here instead of dying with a raw
        # constructor error and an empty run folder left behind.
        $fault = Get-GateLockInnerException $_.Exception
        Write-Host ""
        Write-Host "RESULT: CANNOT SET UP THE GATE LOCK - nothing was built and nothing was started."
        Write-Host ("  " + $fault.GetType().Name + ": " + $fault.Message)
        Write-Host "  Lock file: $gateLockPath"
        Write-Host ""
        Write-Host "This is NOT another run holding the lock. It is a fault in the lock's location that will not"
        Write-Host "clear by waiting: fix the path above (permissions, a directory or read-only file sitting at it,"
        Write-Host "a full disk) and run this again."
        Remove-Item $logDir -Recurse -Force
        exit 2
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
        Remove-Item $logDir -Recurse -Force
        exit 6
    }
}

# STARTS ONE TEST PROJECT and returns what the verdict needs to read back. $Label is appended to the
# result and log file names, so a targeted run that starts one suite several times keeps every pass.
# The child inherits this script's environment at Start-Process, so TEMP and TMP are pointed at the
# suite's own folder just for the launch and put back straight after (see $tempRoot above).
$script:suiteIndex = 0
function Start-TestSuite([string] $Proj, [string] $Label, [string[]] $ExtraArgs) {
    $name = Split-Path -Leaf ([System.IO.Path]::GetDirectoryName((Join-Path $repoRoot $Proj)))
    $out = Join-Path $logDir "$name$Label.log"
    $trxName = "$name$Label.trx"
    $tempName = [string] $script:suiteIndex
    $script:suiteIndex++
    $suiteTemp = Join-Path $tempRoot $tempName
    New-Item -ItemType Directory -Force $suiteTemp | Out-Null
    $testArgs = @("test", (Join-Path $repoRoot $Proj), "--no-build", "-c", $Configuration, "--nologo", "-v", "q",
                  "--logger", "trx;LogFileName=$trxName", "--results-directory", $logDir) + $ExtraArgs
    $savedTemp = $env:TEMP
    $savedTmp = $env:TMP
    try {
        $env:TEMP = $suiteTemp
        $env:TMP = $suiteTemp
        $p = Start-Process -FilePath "dotnet" -ArgumentList $testArgs -NoNewWindow -PassThru `
                           -RedirectStandardOutput $out -RedirectStandardError "$out.err"
    } finally {
        $env:TEMP = $savedTemp
        $env:TMP = $savedTmp
    }

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

    return [pscustomobject]@{ Name = $name; Process = $p; Log = $out; Trx = (Join-Path $logDir $trxName); TempName = $tempName }
}

$originalRoot = $repoRoot
$clearTree = $null
try {
    # -ClearFlaky TESTS THE CANDIDATE'S PRODUCT CODE, NOT MAIN'S. The candidate is what gets tagged, so a
    # flaky test is cleared against it: a throwaway worktree at the candidate, with every test project
    # (and the shared test infrastructure) replaced by main's. If main's tests only pass because of a
    # product change made after the candidate, they fail here or do not build - and then the "flaky test"
    # was a product defect, the candidate is not cleared, and the answer is a new candidate.
    if ($targetMode -eq "CLEAR") {
        $testDirs = @(@($defaultProjects + $installerProjects + $parkedProjects | ForEach-Object { (Split-Path -Parent $_) -replace '\\', '/' }) +
                      @("src/TestInfrastructure") | Select-Object -Unique)
        $clearTree = Join-Path (Split-Path -Parent $originalRoot) ((Split-Path -Leaf $originalRoot) + ".clear-" + (Split-Path -Leaf $logDir))
        Write-Host "Building the candidate's product code with main's tests in $clearTree ..."
        & git -C $originalRoot worktree add -q --detach $clearTree $candidate
        if ($LASTEXITCODE -ne 0) { $clearTree = $null; Write-Host "RESULT: could not create the clearance worktree."; Stop-Gate 1 "CLEARANCE TREE FAILED" }
        & git -C $clearTree rm -r -q --ignore-unmatch -- @($testDirs)
        if ($LASTEXITCODE -ne 0) { Write-Host "RESULT: could not remove the candidate's test projects from $clearTree."; Stop-Gate 1 "CLEARANCE TREE FAILED" }
        # ls-tree answers an absent path with no output and no error; a native error under "Stop" would throw.
        $onMain = @($testDirs | Where-Object { @(& git -C $originalRoot ls-tree --name-only $commit -- $_).Count -gt 0 })
        & git -C $clearTree checkout $commit -- @($onMain)
        if ($LASTEXITCODE -ne 0) { Write-Host "RESULT: could not bring main's test projects into $clearTree."; Stop-Gate 1 "CLEARANCE TREE FAILED" }
        $repoRoot = $clearTree
        $sln = Join-Path $repoRoot "cc-director.sln"
    }

    Write-Host "Building once, then running $($toRun.Count) test project(s)..."
    $buildServerArgs = @()
    # A build server would keep files in the throwaway clearance worktree open, and it could not be removed.
    if ($null -ne $clearTree) { $buildServerArgs = @("--disable-build-servers") }
    & dotnet build $sln -c $Configuration -v q --nologo @buildServerArgs
    if ($LASTEXITCODE -ne 0 -and $null -ne $clearTree) {
        Write-Host ""
        Write-Host "RESULT: NOT CLEARED - main's test projects do not build against the candidate's product code: some test"
        Write-Host "on main (not necessarily the flaky one) uses a product change made after the candidate, so this candidate"
        Write-Host "cannot be cleared. The build output above names it. Cut a new candidate - and clear on the same day,"
        Write-Host "while main is still close to the candidate."
        Stop-Gate 1 "NOT CLEARED"
    }
    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "RESULT: BUILD FAILED - no tests were run."
        Stop-Gate 1 "BUILD FAILED"
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
            Stop-Gate 1 "BUILD FAILED"
        }
    }

    # =================================================================================================
    # THE TARGETED RUN (-RerunFailed, -ClearFlaky). It never falls through to the whole-suite run below:
    # every path out of this block is a Stop-Gate. See THE FLAKY TEST RULE in the header.
    if ($targetMode -ne "") {
        # Hang and crash detection always: a targeted run has no ceiling, and a re-run that hangs must name
        # the test, not hold the release.
        $targetBlame = @("--blame-hang", "--blame-hang-timeout", "${HangTimeoutMinutes}m", "--blame-hang-dump-type", "mini",
                         "--blame-crash", "--blame-crash-dump-type", "mini")
        $outcomes = @{}
        foreach ($t in $targets) { $outcomes["$($t.Suite)|$($t.Test)"] = New-Object System.Collections.Generic.List[string] }
        $presentIn = @{}
        foreach ($t in $targets) { $presentIn["$($t.Suite)|$($t.Test)"] = 0 }

        Write-Host ("{0}: {1} test(s) in {2} suite(s), {3} pass(es)." -f $(if ($targetMode -eq "RERUN") { "RE-RUNNING THE FAILED TESTS" } else { "CLEARING THE FLAKY TESTS" }),
            $targets.Count, $toRun.Count, $targetRepeat)
        foreach ($t in $targets) { Write-Host "  $($t.Suite)  $($t.Test)" }
        Write-Host ""

        for ($pass = 1; $pass -le $targetRepeat; $pass++) {
            $label = if ($targetMode -eq "RERUN") { ".rerun" } else { ".pass$pass" }
            $started = @()
            foreach ($proj in $toRun) {
                $tests = @($targets | Where-Object { $_.Project -eq $proj } | ForEach-Object { $_.Test })
                # A quarantined test is excluded here as everywhere: quarantining a flaky test takes it out of the
                # gate, but it is not a fix, so it must not clear one (it then reads as NOT DELETED).
                $r = Start-TestSuite $proj $label (@("--filter", ("(" + (New-ExactTestFilter $tests) + ")&$QuarantineExclude")) + $targetBlame)
                $started += $r
                $script:targetedRuns += $r
                Write-Host "  pass $pass started $($r.Name)"
            }
            foreach ($r in $started) {
                $r.Process.WaitForExit()
                # The same instrument check as the whole-suite run: a result file that is missing, empty,
                # unfinished or aborted is a run that did not happen, and its silence is not a pass.
                $raw = ""
                if (Test-Path $r.Trx) { $raw = [string] (Get-Content $r.Trx -Raw) }
                $outcome = "NO-TRX"; $total = 0; $executed = 0
                if (-not [string]::IsNullOrWhiteSpace($raw)) {
                    [xml] $doc = $raw
                    $outcome = [string] $doc.TestRun.ResultSummary.outcome
                    $total = [int] $doc.TestRun.ResultSummary.Counters.total
                    $executed = [int] $doc.TestRun.ResultSummary.Counters.executed
                } elseif (Test-Path $r.Trx) {
                    $outcome = "EMPTY-TRX"
                }
                $r | Add-Member -NotePropertyName Outcome -NotePropertyValue $outcome
                $r | Add-Member -NotePropertyName Total -NotePropertyValue $total
                $r | Add-Member -NotePropertyName Executed -NotePropertyValue $executed
                Write-Host ("  pass {0} {1,-40} outcome={2,-12} total={3,-4} executed={4}" -f $pass, $r.Name, $outcome, $total, $executed)
                $running = $script:targetedRuns
                if (@("Completed", "Failed") -notcontains $outcome -or $raw.Contains("The active test run was aborted")) {
                    Write-Host ""
                    Write-Host "RESULT: NEVER RAN - $($r.Name) did not finish in pass $pass (outcome=$outcome). A targeted run"
                    Write-Host "that did not finish proves nothing either way. Read $($r.Log), fix that, and run this again."
                    foreach ($l in (Get-Content $r.Log -Tail 12 -ErrorAction SilentlyContinue)) { Write-Host "      $l" }
                    Stop-Gate 9 "NEVER RAN"
                }
                $suiteTargets = @($targets | Where-Object { $_.Suite -eq $r.Name })
                $before = @{}
                foreach ($t in $suiteTargets) { $before["$($t.Suite)|$($t.Test)"] = $outcomes["$($t.Suite)|$($t.Test)"].Count }
                foreach ($row in (Read-TrxTestOutcomes $r.Trx)) {
                    $key = "$($r.Name)|$($row.Test)"
                    if ($outcomes.ContainsKey($key)) { $outcomes[$key].Add($row.Outcome) }
                }
                # presentIn counts the PASSES that saw the test at all; a theory's rows count once per pass.
                foreach ($t in $suiteTargets) {
                    $key = "$($t.Suite)|$($t.Test)"
                    if ($outcomes[$key].Count -gt $before[$key]) { $presentIn[$key] = $presentIn[$key] + 1 }
                }
            }
        }
        $running = $script:targetedRuns

        if ($targetMode -eq "RERUN") {
            $flaky = @(); $stillFailing = @(); $notRun = @()
            foreach ($t in $targets) {
                $seen = @($outcomes["$($t.Suite)|$($t.Test)"])
                $entry = [ordered]@{ suite = $t.Suite; test = $t.Test }
                if (@($seen | Where-Object { $_ -eq "Passed" -or $_ -eq "Failed" }).Count -eq 0) { $notRun += $entry }
                elseif ($seen -contains "Failed") { $stillFailing += $entry }
                else { $flaky += $entry }
            }
            $rerunResult = [ordered]@{
                runFolder    = $logDir
                startedUtc   = $runRecord.startedUtc
                verdict      = $(if ($notRun.Count -gt 0) { "NEVER RAN" } elseif ($stillFailing.Count -gt 0) { "FAILED" } else { "FLAKY" })
                flaky        = $flaky
                stillFailing = $stillFailing
                notRun       = $notRun
            }
            $runRecord.rerunOf = $sourceRunFolder
            $runRecord.rerun = $rerunResult
            # The re-run is written INTO the red run's own record, so that run cannot be re-run a second
            # time and anyone reading it sees what the re-run found.
            $sourceRun | Add-Member -NotePropertyName rerun -NotePropertyValue $rerunResult -Force
            $sourceRun | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $sourceRunFolder "run.json") -Encoding Ascii

            Write-Host ""
            if ($notRun.Count -gt 0) {
                Write-Host "RESULT: NEVER RAN - these failed tests were not found on the re-run of the same commit:"
                foreach ($e in $notRun) { Write-Host "  $($e.suite)  $($e.test)" }
                Write-Host "The re-run cannot judge a test it did not run. Check the name against the result file in"
                Write-Host "$sourceRunFolder and report it as a defect in this script."
                Stop-Gate 9 "NEVER RAN"
            }
            if ($stillFailing.Count -gt 0) {
                Write-Host "RESULT: FAILED AGAIN - these tests failed on the release gate AND on the re-run of the same commit."
                Write-Host "Treat them as code defects: fix on main and cut a new candidate."
                foreach ($e in $stillFailing) { Write-Host "  $($e.suite)  $($e.test)" }
                if ($flaky.Count -gt 0) {
                    Write-Host ""
                    Write-Host "And these failed once and passed on the re-run - FLAKY, to be deleted today as well, unless one shows a product defect:"
                    foreach ($e in $flaky) { Write-Host "  $($e.suite)  $($e.test)" }
                }
                Stop-Gate 1 "FAILED"
            }
            Write-Host "RESULT: FLAKY - every failed test passed on the re-run of the same commit. The code is not what failed;"
            Write-Host "these TESTS are defective:"
            foreach ($e in $flaky) { Write-Host "  $($e.suite)  $($e.test)" }
            Write-Host ""
            Write-Host "This is still a failure. Delete each test above TODAY, in a test-only pull request on main - unless one shows a product defect, and then fix the product and cut a new candidate."
            $isReleaseGate = ([bool] $sourceRun.parked) -and [string]::IsNullOrEmpty([string] $sourceRun.filter)
            if ($isReleaseGate) {
                $gateRecord = [ordered]@{}
                foreach ($prop in $sourceRun.PSObject.Properties) { $gateRecord[$prop.Name] = $prop.Value }
                $gateRecord.verdict = "FLAKY"
                $gateRecord.exitCode = 10
                $gateRecord.rerun = $rerunResult
                $recordDir = Get-GateRecordDirectory
                New-Item -ItemType Directory -Force $recordDir | Out-Null
                $stamp = ([DateTime]::Parse([string] $sourceRun.startedUtc)).ToUniversalTime().ToString("yyyyMMddTHHmmssZ")
                $recordPath = Join-Path $recordDir ("$commit-$stamp.json")
                $gateRecord | ConvertTo-Json -Depth 6 | Set-Content -Path $recordPath -Encoding Ascii
                Write-Host "Then, once that fix is merged, clear them from a clean checkout of main (not of this candidate):"
                Write-Host "  .\scripts\test-local.ps1 -ClearFlaky $commit"
                Write-Host "scripts\assert-gated.ps1 accepts this candidate only after that clearance is recorded."
                Write-Host "Gate record written: $recordPath"
            } else {
                Write-Host "(The run re-run here was not an unfiltered -Parked run, so no gate record was written.)"
            }
            Stop-Gate 10 "FLAKY"
        }

        # CLEAR: every flaky test must now be DELETED (no longer collected) or FIXED (its source changed
        # since the candidate AND it passed every one of the passes).
        $changed = @(& git -C $originalRoot diff --name-only $candidate $commit)
        $cleared = @(); $notCleared = @()
        foreach ($t in $targets) {
            $key = "$($t.Suite)|$($t.Test)"
            $seen = @($outcomes[$key])
            $entry = [ordered]@{ suite = $t.Suite; test = $t.Test; resolution = ""; passes = $presentIn[$key] }
            if ($seen.Count -eq 0) {
                # Not collected is not yet DELETED: a rename, or a move to another class or project, also
                # makes the exact name vanish while the flaky code lives on. So a method of that name must
                # be gone from every test project on main as well. A different test that happens to share the
                # name is refused too - stricter, never weaker; rename one of them.
                $method = $t.Test.Substring($t.Test.LastIndexOf(".") + 1)
                $still = @(& git -C $originalRoot grep -l -E "\b$method\s*\(" $commit -- @($testDirs) |
                           ForEach-Object { $_.Substring($_.IndexOf(":") + 1) })
                # A RENAME: the file that declared the class lost this method and gained another test method.
                $className = $t.Test.Substring(0, $t.Test.LastIndexOf("."))
                $simple = ($className.Substring($className.LastIndexOf(".") + 1) -split '\+')[0]
                $projDir = (Split-Path -Parent $t.Project) -replace '\\', '/'
                $renamedIn = @()
                foreach ($f in @(& git -C $originalRoot grep -l -E "\b(class|record|struct)\s+$simple\b" $candidate -- $projDir |
                                 ForEach-Object { $_.Substring($_.IndexOf(":") + 1) })) {
                    $diff = @(& git -C $originalRoot diff $candidate $commit -- $f)
                    $removed = @($diff | Where-Object { $_ -match "^-.*\b$method\s*\(" }).Count -gt 0
                    # Any added test attribute, or any added method declaration whatever it returns (ValueTask, async ...).
                    $added = @($diff | Where-Object { $_ -match '^\+\s*\[(Fact|Theory)\b' -or $_ -match '^\+\s*(public|internal|private|protected)\b[^=;]*\b\w+\s*\(' }).Count -gt 0
                    if ($removed -and $added) { $renamedIn += $f }
                }
                if ($still.Count -gt 0) {
                    $entry.resolution = "NOT DELETED: no longer collected under that name, but a method named $method is still in " + ($still -join ", ")
                    $notCleared += $entry
                } elseif ($renamedIn.Count -gt 0) {
                    $entry.resolution = "NOT DELETED: the change that removed $method also added a test method in " + ($renamedIn -join ", ") + " - a rename is not a delete"
                    $notCleared += $entry
                } else {
                    $entry.resolution = "DELETED"
                    $cleared += $entry
                }
                continue
            }
            if ($presentIn[$key] -ne $targetRepeat -or @($seen | Where-Object { $_ -ne "Passed" }).Count -gt 0) {
                $entry.resolution = ("FAILS: outcomes {0}" -f (($seen | Group-Object | ForEach-Object { "$($_.Name) x$($_.Count)" }) -join ", "))
                $notCleared += $entry
                continue
            }
            $className = $t.Test.Substring(0, $t.Test.LastIndexOf("."))
            $simple = ($className.Substring($className.LastIndexOf(".") + 1) -split '\+')[0]
            $projDir = (Split-Path -Parent $t.Project) -replace '\\', '/'
            $files = @(& git -C $originalRoot grep -l -E "\b(class|record|struct)\s+$simple\b" $commit -- $projDir |
                       ForEach-Object { $_.Substring($_.IndexOf(":") + 1) })
            $touched = @($files | Where-Object { $changed -contains $_ })
            if ($touched.Count -gt 0) {
                $entry.resolution = "FIXED in " + ($touched -join ", ")
                $cleared += $entry
            } else {
                $entry.resolution = "UNCHANGED: passed $targetRepeat times, but no file declaring $simple changed since $candidate"
                $notCleared += $entry
            }
        }
        $runRecord.clears = $candidate
        $runRecord.clearance = @($cleared + $notCleared)
        Write-Host ""
        foreach ($e in ($cleared + $notCleared)) { Write-Host ("  {0}  {1}  -> {2}" -f $e.suite, $e.test, $e.resolution) }
        Write-Host ""
        if ($notCleared.Count -gt 0) {
            Write-Host "RESULT: NOT CLEARED - a flaky test is cleared only by deleting it or by changing its code so that it"
            Write-Host "passes $targetRepeat times in a row. A flaky test that happened to pass is still flaky."
            Stop-Gate 1 "NOT CLEARED"
        }
        $clearance = [ordered]@{
            candidate     = $candidate
            clearedAt     = $commit
            configuration = $Configuration
            verdict       = "CLEARED"
            startedUtc    = $runRecord.startedUtc
            machine       = [Environment]::MachineName
            session       = (Get-GateLockSessionName)
            directory     = $originalRoot
            testedTree    = "the candidate's product code with the test projects of $commit"
            flakyRecord   = $flakyRecordPath
            tests         = $cleared
        }
        $clearDir = Get-GateClearanceDirectory
        New-Item -ItemType Directory -Force $clearDir | Out-Null
        $stamp = ([DateTime]::Parse($runRecord.startedUtc)).ToUniversalTime().ToString("yyyyMMddTHHmmssZ")
        $clearPath = Join-Path $clearDir ("$candidate-$stamp.json")
        $clearance | ConvertTo-Json -Depth 6 | Set-Content -Path $clearPath -Encoding Ascii
        Write-Host "RESULT: CLEARED - every flaky test of $candidate is deleted or fixed at $commit."
        Write-Host "Clearance written: $clearPath"
        Write-Host "scripts\assert-gated.ps1 $candidate accepts it once $commit is on origin/main."
        Stop-Gate 0 "CLEARED"
    }

    $filterArgs = @("--filter", $suiteFilter)
    # Each Gateway.Tests shard runs this same filter, ANDed with the shard's own part of the suite.
    $shardScopeFilter = $suiteFilter

    # Hang and crash detection for the runs that have no ceiling (see $HangTimeoutMinutes). A hung test is
    # dumped and named in Sequence_<guid>.xml inside the run folder, and its host is killed; the suite then
    # reports an aborted run, which the verdict below refuses as NEVER RAN rather than passing what finished.
    # Mini dumps: the name of the hung test is the finding, and a full dump of a Gateway host is hundreds of
    # megabytes nobody opens.
    $blameArgs = @()
    if ($Parked -or $Gateway -or $Quarantine) {
        $blameArgs = @("--blame-hang", "--blame-hang-timeout", "${HangTimeoutMinutes}m", "--blame-hang-dump-type", "mini",
                       "--blame-crash", "--blame-crash-dump-type", "mini")
    }

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
    # the console line.
    #
    # GATEWAY.TESTS RUNS AS SEVERAL PROCESSES, each with a disjoint part of the suite (scripts\gateway-test-shards.ps1
    # says how it is split and why that is safe). Each shard is its own entry below, with its own log, result file
    # and verdict line, so a failure names the shard it was in.
    # THE PARKED SUITES' AFFECTED TESTS RUN IN THE DEFAULT GATE. Until 17 October 2026 a change that reached a
    # parked suite was only WARNED about, and on 16 October one broke two Gateway unit test classes, merged on a
    # green default run, and held main red for a day - which blocked every release behind it. Now the default
    # run asks scripts\select-parked-tests.ps1 which classes of Gateway.UnitTests and Core.Tests the change can
    # reach, and runs exactly those, AFTER the default suites have finished: running them alongside would take
    # the processor from suites already near the two-minute ceiling and stop them for a reason that is not theirs.
    # They have no ceiling of their own - their cost is what the change reaches - and hang detection instead.
    #
    # A selector that cannot run fails this gate. It used to be caught and turned into a note, which made the
    # one check standing between a parked suite and main something that could quietly decide nothing.
    $parkedPending = @()
    if (-not $Parked -and -not $Gateway -and -not $Quarantine -and $Filter -eq "") {
        $parkedPending = @(& (Join-Path $PSScriptRoot "select-parked-tests.ps1"))
        foreach ($s in $parkedPending) {
            $what = $(if ($s.Whole) { "the WHOLE suite - $($s.Reasons -join '; ')" } else { "$($s.Classes.Count) class(es) this change can reach" })
            Write-Host "  parked $($s.Suite): $what - runs after the default suites"
        }
    }

    $running = @()
    $deadline = (Get-Date).AddSeconds($BudgetSeconds)
    foreach ($proj in $toRun) {
        if ($proj -eq $gatewayProject) {
            $shardFilters = Get-GatewayTestShardFilters
            for ($i = 0; $i -lt $shardFilters.Count; $i++) {
                $label = ".shard{0}of{1}" -f ($i + 1), $shardFilters.Count
                $r = Start-TestSuite $proj $label (@("--filter", (Join-GatewayTestShardFilter $shardScopeFilter $shardFilters[$i])) + $blameArgs)
                # The shard's name is the suite's name with its label, matching its log and result file, so the
                # verdict line, run.json and a later -RerunFailed all name the shard (Get-SuiteBaseName undoes it).
                $r.Name = $r.Name + $label
                $running += $r
                Write-Host "  started $($r.Name)"
            }
            continue
        }
        $r = Start-TestSuite $proj "" ($filterArgs + $blameArgs)
        $running += $r
        Write-Host "  started $($r.Name)"
    }

    Write-Host ""
    if ($toRun -contains $gatewayProject) {
        Write-Host "Waiting. This run holds the release-gate lock ($gateLockPath); another run that includes"
        Write-Host "the Gateway suite is refused at once rather than queued behind this one."
    } else {
        Write-Host "Waiting. The Gateway suite is not in this run, so it takes no lock and nothing queues behind it."
    }
    Write-Host ""

    $failed = @()
    $overBudget = @()
    $parkedRan = @()
    $ri = 0
    while ($true) {
        if ($ri -ge $running.Count) {
            # Every default suite is done: start the parked suites' affected tests, and wait for them the same way.
            if ($parkedPending.Count -eq 0) { break }
            $parkedBlame = @("--blame-hang", "--blame-hang-timeout", "${HangTimeoutMinutes}m", "--blame-hang-dump-type", "mini",
                             "--blame-crash", "--blame-crash-dump-type", "mini")
            foreach ($s in $parkedPending) {
                $selFilter = $(if ($s.Whole) { $suiteFilter } else { "(" + (($s.Classes | ForEach-Object { "FullyQualifiedName~.$_." }) -join "|") + ")&$suiteFilter" })
                $r = Start-TestSuite $s.Project "" (@("--filter", $selFilter) + $parkedBlame)
                $r | Add-Member -NotePropertyName NoCeiling -NotePropertyValue $true
                $running += $r
                Write-Host "  started $($r.Name) ($(if ($s.Whole) { 'whole suite' } else { "$($s.Classes.Count) affected class(es)" }))"
            }
            $parkedRan = @($parkedPending | ForEach-Object { $_.Suite })
            $parkedPending = @()
            continue
        }
        $r = $running[$ri]
        $ri++
        # Wait only until the deadline set at launch - the time LEFT, not a fresh budget per suite. A suite
        # that has not finished by then is over the ceiling: kill it, so a single slow project cannot hold
        # the whole gate, and record it separately from a real failure. -Parked and -Gateway deliberately
        # suspend the ceiling: that run is the release gate and is EXPECTED to be slow, and a hung test in it
        # is caught by the hang timeout instead. So do the parked suites' affected tests (see above).
        if ($Parked -or $Gateway -or $Quarantine -or $r.PSObject.Properties['NoCeiling']) {
            $r.Process.WaitForExit()
        } else {
            $remainingMs = [int] [Math]::Max(0, ($deadline - (Get-Date)).TotalMilliseconds)
            if (-not $r.Process.WaitForExit($remainingMs)) {
                $overBudget += $r.Name
                # THE WHOLE TREE, WITH A TOOL THAT EXISTS HERE. "dotnet test" is a parent whose test host is a
                # child; killing the parent alone leaves the host running. This used to call
                # Process.Kill($true) - the "entire process tree" overload - inside an empty catch. That
                # overload is .NET Core only: under Windows PowerShell 5.1, which is what runs this gate, it
                # does not exist, the call threw, the catch hid it, the suite ran on after the gate had
                # printed "were STOPPED", and nobody saw. Verified on 2026-10-10: Kill($true) threw and the
                # parent stayed alive; taskkill /T /F ended the parent and its test host.
                $killed = $false
                if ($r.Process.HasExited) {
                    # It finished in the gap between the deadline and this line; there is nothing to kill, and
                    # taskkill would answer 128 (no such process) and be reported as a failure to stop it.
                    $killed = $true
                } elseif ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
                    & $env:ComSpec /d /c "taskkill /T /F /PID $($r.Process.Id) >nul 2>&1"
                    $killed = ($LASTEXITCODE -eq 0)
                } else {
                    $r.Process.Kill()
                    $killed = $true
                }
                if (-not $killed -or -not $r.Process.WaitForExit(10000)) {
                    Write-Host ("  ERROR: could not stop {0} (process {1}) after the deadline - its test host may still be running. Stop it by hand before the next run." -f $r.Name, $r.Process.Id)
                }
            }
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
        $aborted = $false
        # Read as a string first: Get-Content -Raw on a ZERO-BYTE file returns null in PowerShell 5.1, and a
        # host killed before it wrote anything leaves exactly that file. An empty result file is its own
        # outcome, EMPTY-TRX, which the never-ran check below refuses as an unfinished run.
        $raw = ""
        if (Test-Path $r.Trx) { $raw = [string] (Get-Content $r.Trx -Raw) }
        if ([string]::IsNullOrWhiteSpace($raw) -and (Test-Path $r.Trx)) {
            $outcome = "EMPTY-TRX"
        } elseif (Test-Path $r.Trx) {
            [xml] $doc = $raw
            $outcome = [string] $doc.TestRun.ResultSummary.outcome
            $total = [int] $doc.TestRun.ResultSummary.Counters.total
            # EXECUTED is not the same number as TOTAL, and the difference is load-bearing (issue #2834).
            # A test carrying a static Skip is COLLECTED and counted in total while executing nothing, so a
            # run can report total=1, executed=0 and "Test Run Successful". Anything that asks "did this run
            # actually enforce something" has to read executed.
            $executed = [int] $doc.TestRun.ResultSummary.Counters.executed
            # A host that was killed or crashed, or that a suite lock once stopped, writes outcome="Failed" with
            # total=0 - the same outcome as an ordinary red - and says what happened only in a RunInfo text:
            # "The active test run was aborted". Observed three times in October 2026, each read as a test
            # failure by the check below instead of as a suite that never ran.
            $aborted = $raw.Contains("The active test run was aborted")
        }
        $r | Add-Member -NotePropertyName Outcome -NotePropertyValue $outcome
        $r | Add-Member -NotePropertyName Total -NotePropertyValue $total
        $r | Add-Member -NotePropertyName Executed -NotePropertyValue $executed
        $r | Add-Member -NotePropertyName Aborted -NotePropertyValue $aborted

        if ($r.Process.ExitCode -eq 0) {
            Write-Host ("  PASS  {0}  {1}" -f $r.Name, $summary.Trim())
        } else {
            Write-Host ("  FAIL  {0}  {1}" -f $r.Name, $summary.Trim())
            $failed += $r
        }
    }

    if ($Quarantine) {
        # NEVER A VERDICT. What ran, what failed, and exit 0 whatever happened - including a suite that did
        # not finish, which is printed as such rather than hidden.
        Write-Host ""
        Write-Host "QUARANTINE - these results decide nothing. Quarantined tests are on their way to being deleted:"
        Write-Host "  suite filter=$suiteFilter"
        $qFailed = @()
        foreach ($r in $running) {
            Write-Host ("  {0,-40} outcome={1,-12} total={2,-6} executed={3}" -f $r.Name, $r.Outcome, $r.Total, $r.Executed)
            if (-not (Test-Path $r.Trx)) { continue }
            $raw = [string] (Get-Content $r.Trx -Raw)
            if ([string]::IsNullOrWhiteSpace($raw)) { continue }
            [xml] $doc = $raw
            foreach ($u in @($doc.TestRun.Results.UnitTestResult)) {
                if ($null -ne $u -and [string] $u.outcome -eq "Failed") { $qFailed += "  $($r.Name)  $($u.testName)" }
            }
        }
        $qTotal = 0
        foreach ($r in $running) { $qTotal += [int] $r.Total }
        Write-Host ""
        Write-Host ("RESULT: QUARANTINE RAN {0} test(s); {1} failed. This never fails a run." -f $qTotal, $qFailed.Count)
        foreach ($l in $qFailed) { Write-Host $l }
        # The folder is the point of a quarantine run - the failures are read from it - so it is always kept.
        $KeepRun = $true
        Write-Host "Run folder kept: $logDir"
        Stop-Gate 0 "QUARANTINE"
    }

    Write-Host ""
    Write-Host "TRX verdict - THIS is the gate. Every suite must report outcome=Completed:"
    Write-Host ("  commit {0}  tree {1}  parked={2} configuration={3} suite filter={4}" -f $commit,
        $(if ($treeClean) { "clean" } else { "DIRTY ($($dirtyFiles.Count) changed or untracked)" }),
        [bool] $Parked, $Configuration, $suiteFilter)
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
    #
    # AND THE SUITE THAT NEVER RAN AT ALL, WHICH THE OUTCOME CHECK ABOVE CANNOT SEE. A Gateway host that
    # gave up on the suite lock, a host killed by the hang timeout, a host that crashed before its first
    # test - every one of these writes outcome="Failed" with total=0 executed=0, exactly the outcome the
    # block above was told to treat as finished, so for a week nineteen release gates that executed nothing
    # ended as "FAILED in 1 project(s)" and read as a red test. Two presences name it: the result file's
    # own words, "The active test run was aborted", and - with no filter, where every suite has tests to
    # run - an executed count of zero. A suite that wrote no result file and exited nonzero is the same
    # thing one step earlier. An over-budget suite is excluded here because it is reported on its own
    # below: it was killed by this script, and the reason is the ceiling, not the suite.
    $finishedOutcomes = @("Completed", "Failed")
    $neverRan = @()
    foreach ($r in $running) {
        if ($overBudget -contains $r.Name) { continue }
        $why = $null
        if ($r.Outcome -eq "NO-TRX") {
            if ($r.Process.ExitCode -ne 0 -and $Filter -eq "") { $why = "wrote no result file and exited $($r.Process.ExitCode)" }
        } elseif ($finishedOutcomes -notcontains $r.Outcome) {
            $why = "reported outcome=$($r.Outcome), which is not a finished run"
        } elseif ($r.Aborted) {
            $why = "says in its result file that the active test run was aborted (total=$($r.Total), executed=$($r.Executed))"
        } elseif ($Filter -eq "" -and $r.Executed -eq 0) {
            $why = "executed zero tests with no filter (outcome=$($r.Outcome), total=$($r.Total)) - or every test in it is quarantined, which is not allowed"
        }
        if ($null -ne $why) { $neverRan += [pscustomobject]@{ Name = $r.Name; Why = $why; Log = $r.Log } }
    }
    if ($neverRan.Count -gt 0) {
        Write-Host ""
        Write-Host "RESULT: NEVER RAN - a suite did not run, or did not finish. This run is not a verdict on anything."
        foreach ($n in $neverRan) {
            Write-Host ("  {0} {1} -> {2}" -f $n.Name, $n.Why, $n.Log)
            $tail = Get-Content $n.Log -Tail 12 -ErrorAction SilentlyContinue
            foreach ($l in $tail) { Write-Host "      $l" }
        }
        Write-Host ""
        Write-Host "This is NOT an assertion failure - a run that finished with failures reports 'Failed' with a"
        Write-Host "nonzero executed count and is reported in full as exit 1. This is a suite whose tests were"
        Write-Host "never reached: it was refused, killed, or stopped part way, and whatever passed before that"
        Write-Host "point certifies nothing. Read the lines above for why, fix that, and run again."
        Stop-Gate 9 "NEVER RAN"
    }
    Write-Host ""
    Write-Host "Run folder: $logDir (kept after a red run; deleted after a green one unless -KeepRun)"
    Write-Host ""

    # COVERAGE WARNING. The default run is fast because three suites are parked - but "parked" must never
    # quietly mean "this change was never tested". select-tests.ps1 works out, from the reference graph,
    # which suites this change could actually affect; if a PARKED one is in that set, say so loudly.
    #
    # Since 17 October 2026 this is a warning about Gateway.Tests ALONE in a default run: Gateway.UnitTests and
    # Core.Tests had their affected classes run above (or had none the change can reach). Running whole
    # suites was rejected because a parked suite is implicated in seven changes in ten - CcDirector.Core and
    # Gateway.Contracts are referenced by nearly everything - and that is the long gate this one replaced.
    # Gateway.Tests is host-bound and holds the machine's one release-gate lock, so it is not selected.
    # A -Filter or -Quarantine run selects nothing, so there every reachable parked suite is still a gap.
    $parkedNames = @($parkedProjects | ForEach-Object { Split-Path -Leaf ([System.IO.Path]::GetDirectoryName((Join-Path $repoRoot $_))) })
    $selectedInDefault = @("CcDirector.Gateway.UnitTests", "CcDirector.Core.Tests")
    $coverageGap = @()
    if (-not $Parked -and -not $Gateway) {
        $sel = & (Join-Path $PSScriptRoot "select-tests.ps1")
        $coverageGap = @($sel.Suites | Where-Object { $parkedNames -contains $_ } |
                         Where-Object { -not ($Filter -eq "" -and -not $Quarantine -and $selectedInDefault -contains $_) })
    }
    foreach ($n in $parkedRan) { Write-Host "Parked suite $n ran the tests this change can reach (see its line in the TRX verdict)." }

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
        Stop-Gate 1 "OVER BUDGET"
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
        Stop-Gate 4 "NO RESULT FILE"
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
            Write-Host "drifted, a test that has been removed, a typo - or a QUARANTINED test (trait Gate=Quarantine),"
            Write-Host "which no gate run includes; run it with -Quarantine. In every case the claim this run was"
            Write-Host "gathered to support is unproven."
            Stop-Gate 5 "FILTER MATCHED NOTHING"
        }

        if ($ExpectTests -gt 0 -and $collected -ne $ExpectTests) {
            Write-Host ""
            Write-Host "RESULT: EXPECTED $ExpectTests TEST(S), COLLECTED $collected."
            Write-Host "The caller declared the inventory this evidence needs and the run did not match it."
            Stop-Gate 5 "EXPECTED COUNT NOT MET"
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
        Stop-Gate 8 "EVERYTHING SKIPPED"
    }

    if ($collected -eq 0) {
        Write-Host ""
        Write-Host "RESULT: ZERO TESTS COLLECTED - nothing ran, so this is not a pass."
        if ($Filter -ne "") {
            Write-Host "The filter was: $Filter"
            Write-Host "No test in any project matched it. Check the test name, and check that the test file is"
            Write-Host "actually in THIS checkout - a filter naming a class that does not exist here exits zero"
            Write-Host "with 'No test matches', which is the shape of a green and the substance of nothing. A test tagged"
            Write-Host "Gate=Quarantine is excluded from every gate run too; run it with -Quarantine."
        } else {
            Write-Host "No filter was passed, so a total of zero means the run did not execute at all."
        }
        Write-Host ""
        Write-Host "A run that collected zero tests is a broken instrument. It is never evidence, and a red-first"
        Write-Host "claim must never be quoted from one."
        Stop-Gate 3 "ZERO COLLECTED"
    }

    if ($failed.Count -eq 0) {
        Write-Host ("RESULT: GREEN at commit {0}, tree {1}. Check the TRX outcome and totals above before calling it green." -f $commit, $(if ($treeClean) { "clean" } else { "DIRTY" }))
        Write-Host "This is the gate - you do not need to wait for GitHub CI to merge."
        Stop-Gate 0 "GREEN"
    }

    Write-Host "RESULT: FAILED in $($failed.Count) project(s):"
    foreach ($f in $failed) {
        Write-Host "  $($f.Name) -> $($f.Log)"
        $lines = Get-Content $f.Log -Tail 25 -ErrorAction SilentlyContinue
        foreach ($l in $lines) { Write-Host "      $l" }
    }
    Write-Host ""
    Write-Host "Logs kept in $logDir"
    Stop-Gate 1 "FAILED"
} finally {
    try {
        # A run that ends by exception or Ctrl+C never reached Stop-Gate, and its run.json would say RUNNING
        # forever - exactly the shape a reader mistakes for a live run. Stamp it as stopped without a verdict,
        # after ending the test processes it started, so nothing is deleted from under a live test host.
        if ($null -eq $runRecord.exitCode) {
            Stop-RunProcesses
            Remove-RunTemp
            $runRecord.verdict = "STOPPED BEFORE A VERDICT"
            $runRecord.finishedUtc = (Get-Date).ToUniversalTime().ToString("o")
            Write-RunRecord
            Write-RunsLog
        }
        # The throwaway -ClearFlaky worktree goes on every path; one that cannot be removed is named.
        if ($null -ne $clearTree -and (Test-Path -LiteralPath $clearTree)) {
            & git -C $originalRoot worktree remove --force $clearTree
            if (Test-Path -LiteralPath $clearTree) {
                $null = Remove-FolderTree $clearTree
                & git -C $originalRoot worktree prune
            }
            if (Test-Path -LiteralPath $clearTree) {
                Write-Host "ERROR: the clearance worktree $clearTree could not be removed. Delete it by hand, then run 'git worktree prune'."
            }
        }
    } finally {
        # Released here on EVERY path - a normal end, every exit code above, an exception, Ctrl+C - and
        # LAST, after this run's processes are ended and its folders deleted, so a second gate cannot start
        # while this one's test hosts are still being stopped. The next run on this machine is refused only
        # while a live process holds the handle, so the handle must not outlive the run.
        if ($null -ne $gateLock) { Exit-GateLock $gateLock }
    }
}
