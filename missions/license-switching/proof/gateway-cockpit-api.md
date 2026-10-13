# Proof - the Gateway routes the Licenses page reads

**This file has two parts.** Everything down to "After the review" is the proof as the pull request was first opened,
at `af3ce994e`, and is left as it was written. "After the review", at the end, is what changed in answer to
`reviews/3925-cockpit-api-review.md`: the new counts, sixteen more mutations, and the gate on the final code. Where
the two differ, the second part is the present state.

Task: phase 2, the HTTP routes of the Gateway for the Licenses page of the Cockpit (MISSION.md goals 1, 2, 3 and the
drawing of 5). The response shapes, with a real example of each, are in `missions/license-switching/cockpit-api.md`.

## What is in it

- `src/CcDirector.Gateway.Contracts/LicenseCockpitDtos.cs` - the response types and the fixed words (account
  statuses, tones, window levels, machine statuses). Every member name is held to "never a sign-in key" by
  `LicenseMessagesCarryNoKeyTests`, which scans every `License*` type in that namespace.
- `src/CcDirector.Gateway/Api/LicenseEndpoints.cs` - three reads, each for the caller's own account:
  `GET /gateway/licenses`, `GET /gateway/licenses/forecast`, `GET /gateway/licenses/log`.
- `src/CcDirector.Gateway/Licenses/LicenseDisplayFold.cs` - the one place every verdict the page shows is decided.
  What is at its limit, what has room, what is stale and what Switch now would accept are the decision engine's own
  answers on the same picture the minute check judges; the fold adds the order of the statuses and the words.
- `src/CcDirector.Gateway/Licenses/LicenseSnapshotSource.cs` - the one place the engine's picture is built from the
  store (shared with the minute check's pull request, which cherry-picked it).
- `src/CcDirector.Gateway/Licenses/LicenseUsageHistory.cs` - two hours of readings per account in memory, fed by
  every usage report the ingest accepts, for the forecast's rate of use. No table and no migration.
- `LicenseStore.ListLogPage` - one page of the log, newest first, held to the moment the walk began.
- `SessionKeyGuard` - the three reads are in the allow list. Switch now is not.
- `GatewayHost.LicenseClock` - the routes take the Gateway's clock from the host, so their host tests run on a
  fixed one.

No migration was added, so `.\scripts\test-database.ps1` has nothing new to cover and was not run.

## Not in it: `POST /gateway/licenses/switch-now`

The brief: add it in this pull request if the method it calls had merged to main, otherwise it becomes a small
follow-up. `LicenseSwitchService.SwitchNowAsync` is on `origin/license-switching-gw-tick` and was not on main when
this pull request was opened, so the route is the follow-up. Everything the page needs to DRAW the control is in
the overview answer already (`canSwitchNow`, `switchNowText`, `switchNowTargetAccountEmail`, `switchTargets`), and
section 6 of `cockpit-api.md` says what the follow-up adds and the one case it must close (a machine waiting for
an offline Director).

## Tests, and what each holds down

All in `src/CcDirector.Gateway.UnitTests/Licenses/` unless said otherwise. Every fold and route test puts its facts
in the way production does - usage reports applied to the real store on a throwaway SQLite file and read back
through the snapshot source - rather than a picture built by hand.

| File | Tests | What it holds down |
|---|---|---|
| `LicenseDisplayFoldTests.cs` | 86 | Every verdict at its boundary: at limit from exactly 97.0 and not at 96.99, on each window; stale one second past 6 minutes (active) and 30 minutes (spare); a sign-in expired at exactly now, expiring soon up to exactly 3 days, fine one second later; the order of the statuses; Switch now offered while a target is under 97.0 and not at it; a machine not reporting one second past 6 minutes; the forecast's sentences; every log state, reason and kind, and a kind, state and reason this build does not know. |
| `LicenseEndpointsTests.cs` | 25 | The exact property names of each answer (a rename fails here); one account never sees another's accounts, machines, forecast, rate or log; no account bound is a 403; an account that never reported gets empty answers with a sentence; the log pages without overlap or loss while rows are written and when rows share a time; a page size or cursor that cannot be read is a 400; a log row of an unknown kind does not break the log. |
| `LicenseGuardTests.cs` | 18 | A session key may call exactly the three reads, and Switch now is refused even to a raised session. |
| `LicenseSnapshotSourceTests.cs` | 9 | The picture comes from the latest reports, the open switch rows and the bars in force; the open no-room events are passed in; one account never sees another's. |
| `LicenseUsageHistoryTests.cs` | 8 | One point per window at the judged read time; the same reading sent again adds nothing; kept two hours and no longer; only a report the ingest accepted is recorded, and the forecaster gets its rate from it. |
| `src/CcDirector.Gateway.Tests/LicenseHostTests.cs` | 8 | Over real HTTP through the real `AuthMiddleware` and `SessionKeyGuard` on a hosted host: a session key, the owner's browser and a machine's own credential read all three routes; no credential is a 401; Switch now is a 403 for a session key; one account never sees another's; the reports go in through the host's own ingest and the forecast has their rate. |

## Commands and results (2026-10-12, the Windows machine, other sessions running)

At commit `af3ce994e` (this branch with `origin/main` at `faffafcdc` merged in):

- `dotnet test src/CcDirector.Gateway.UnitTests/CcDirector.Gateway.UnitTests.csproj --filter "FullyQualifiedName~Licenses"`:
  passed 252, failed 0 (106 were there before this work).
- `dotnet test src/CcDirector.Gateway.Tests/CcDirector.Gateway.Tests.csproj --filter "FullyQualifiedName~LicenseHostTests"`:
  passed 8, failed 0.
- `dotnet test src/CcDirector.Core.UnitTests/CcDirector.Core.UnitTests.csproj --filter "FullyQualifiedName~LicenseMessagesCarryNoKey"`:
  passed, with the new types in its scan.

## The gate

`.\scripts\test-local.ps1 -Parked` at commit `af3ce994e` (the code of this pull request; `origin/main` at
`faffafcdc` merged in), 2026-10-13 01:38 to 01:54 UTC, with other sessions' test runs on the machine. `-Parked`
runs every suite of the default run and the three parked ones, so this one run is both gates.

| Suite | Outcome | Tests |
|---|---|---|
| CcDirector.Core.UnitTests | Failed | 1,624 of 1,625 passed |
| CcDirector.Avalonia.Tests | Completed | 1,043 |
| CcDirector.Engine.Tests | Completed | 86 |
| CcDirector.HostedAgent.Tests | Completed | 89 |
| CcDirector.Launcher.Tests | Completed | 218 |
| CcDirector.Terminal.Avalonia.Tests | Completed | 82 |
| CcDirector.Reclaim.Tests | Completed | 305 |
| cc-director-setup.Tests | Completed | 39 |
| cc-director-setup-engine.Tests | Completed | 870 |
| cc-director-setup-cli.Tests | Completed | 74 |
| CcDirector.Gateway.Tests, four shards | Completed | 829 + 755 + 667 + 810 = 3,061 |
| CcDirector.Core.Tests | Completed | 4,585 |
| CcDirector.Gateway.UnitTests | Failed | 10,788 of 10,790 passed |

Three tests were red. None is in a file this pull request touches. Then, as the gate's own rule says, only those
three were run again, once, on the same commit: `.\scripts\test-local.ps1 -RerunFailed <the run folder>`.

- `GateTestsDoNotDependOnTheMachineTests.No_gate_test_adds_a_sleep_a_real_clock_a_real_process_docker_or_a_fixed_port`
  failed AGAIN. Its message names one file: `src/CcDirector.Gateway.UnitTests/CronRunResultTests.cs`, four new
  reads of the real clock. That file came to main with pull request 3799 and is red on `origin/main` at `faffafcdc`
  without this branch: `git grep -n "DateTime.UtcNow" origin/main -- src/CcDirector.Gateway.UnitTests/CronRunResultTests.cs`
  lists the four reads (lines 512, 524, 1032, 1048). This pull request does not touch that file and does not fix
  it; the Tech Lead was told. The same test caught a real-clock read in this pull request's own host tests on the
  first default run, which is why the routes now take the Gateway's clock from the host (`GatewayHost.LicenseClock`).
- `HeldDeliveryReachabilityTests.APermanentFailureOnADriverAttempt_IsHandedBack_TheOutcomeReadAnswers422_AndARetryReEnters`
  and `PromptIncidentErrorReportTests.ASendStillDelivering_TheLateWatchsWaitLinesNeverCarryThePromptsWords_AndCarryTheCommandId`
  failed once and PASSED on the re-run: flaky under this machine's load, in the held delivery and prompt incident
  tests, which nothing here calls.

So the gate is green for everything this pull request changes and red on one test that is red on main.

One defect of this pull request was found by a suite outside the license filter, before the gate, by running all
of `CcDirector.Gateway.UnitTests` by hand: `GatewayHttpFailureLineShapeTests` refused the three failure lines of
the new routes, which named their route through a constant the check does not read as a route. They now write
the route itself (`GET /gateway/licenses FAILED`), commit `af3ce994e`. That hand run (10,793 of 10,796) had two
other reds, `StatsStoreReopensAfterAnUnreachableStoreTests.AnObstructedStore_IsUnavailableToBeginWith` and
`HeldDeliveryReachabilityTests.QACase8_...`; both passed when run again alone and neither failed in the gate run.

## Mutation proof

Each line is one change to the product code, made alone, with every license unit test then run
(`--filter "FullyQualifiedName~Licenses"`, 252 tests) and the file restored from its original bytes in a `finally`
block. After the last one `git status` was clean and the rebuilt tree passed 252 of 252. The script is not committed;
its mutations are exactly the lines below.

| | The behaviour broken | Failed | Went red |
|---|---|---|---|
| 1 | A window is at its limit from exactly 97% (engine `IsTired`: `>=` to `>`) | 28 | In the fold: `Account_AtLimit_StartsAtExactlyTheThreshold_OnEitherWindow` (both 97.0 rows), `Window_Level_IsAtLimitFromExactlyTheThreshold(97)`, `SwitchNow_IsOfferedOnlyWhileATargetHasRoom...(97)`, `Account_AtLimit_OutranksSignInExpiresSoon`; and 23 engine and forecaster tests |
| 2 | Stale no longer outranks at limit | 1 | `Account_Stale_OutranksAtLimit_BecauseOldNumbersCannotSayItIsAtItsLimit` |
| 3 | Sign-in needed no longer outranks stale and at limit | 1 | `Account_SignInNeeded_OutranksStaleAndAtLimit_BecauseOnlyTheOwnerCanFixIt` |
| 4 | A percentage is rounded to nearest, not down (96.99 reads "97%") | 1 | `Window_Level_IsAtLimitFromExactlyTheThreshold(96.99)` |
| 5 | Switch now offered only when a target is chosen automatically | 1 | `SwitchNow_ATargetBarredFromTheAutomaticChoice_CanStillBeChosenByHand` |
| 6 | A log row of a kind this build does not know throws | 2 | `LogEntry_AKindThisBuildDoesNotKnow_IsRenderedAsItsMessage_AndNeverFails`, `Log_ListsSpareRowsAndARowOfAKindThisBuildDoesNotKnow_AndNeverFailsOnIt` |
| 7 | The overview route answers for a fixed account, not the caller's | 1 | `EveryRoute_AnswersOnlyTheCallersOwnAccount` |
| 8 | The session key guard forgets the log route | 2 | `TheGuard_AllowsASessionKey_ExactlyTheLicenseReads(GET /gateway/licenses/log)`, `TheGuard_KnowsEveryRouteTheGatewayMaps` (since renamed `TheGuard_AllowsTheThreeReads_ByTheSameConstantsTheEndpointsMapThemWith`) |
| 9 | A log page is no longer held to the moment its walk began | 1 | `Log_IsNewestFirst_AndPagesWithoutOverlapOrLoss_EvenWhileRowsAreWritten` |
| 10 | The ingest no longer records an accepted report in the history | 1 | `Ingest_OnlyAnAcceptedReport_IsRecorded_AndTheForecastGetsItsRate` |
| 11 | The snapshot never says a machine has an open switch | 4 | `Read_AMachineWithAnOpenSwitchRow_HasAnOpenSwitch_UntilTheRowIsSettled`, `SwitchNow_SaysWhichMachineIsLeftOut_WhenOneIsMidSwitch`, and two forecast tests |
| 12 | An account with a machine but no sign-in is called empty | 1 | `Overview_OnceAMachineHasReported_IsNotEmpty_EvenWhenItHoldsNoSignIn` |
| 13 | The same reading sent again is added to the history twice | 1 | `Record_TheSameReadingSentAgain_AddsNothing` |
| 14 | A sign-in that expires soon is no longer a status | 3 | `Account_SignInExpiry_...(1 second)`, `...(exactly 3 days)`, `Account_SignInExpiresSoon_SaysWhereAndInHowLong` |
| 15 | Exactly one hour is said in minutes | 1 | `Span_SaysALengthOfTimeInWords_RoundedDown(3600)` |
| 16 | A log page may be any size | 1 | `Log_APageSizeOrCursorItCannotRead_IsRefused_NotGuessed(?limit=201)` |
| 17 | A window that resets exactly now is read as before its reset | 1 | `Account_AReadingTakenBeforeItsWindowReset_IsNotAtLimit_AndItsWindowSaysItHasReset` |
| 18 | A request with no account bound is not refused by the log route | 1 | `EveryRoute_WithNoAccountBoundToTheRequest_IsRefused` |

All eighteen were caught. The boundaries of staleness and of the sign-in expiry verdict are decided in
`LicenseReadModel` (already on main, with its own tests); the fold tests above pin them again through the answers.

## What these tests do not show

- The routes have not been called by a real Cockpit page or against the deployed Gateway; that is phase 3 and
  phase 5.
- The host tests reach the routes with keys minted in the test host. They do not sign in through the account
  sign-in flow.
- The forecast's rate after a Gateway restart is empty until two reports have arrived; that is by design and is
  tested only as "no readings means no rate", not across a real restart.
- Two things the examples in `cockpit-api.md` could not show because they are not built yet: a failed renewal on
  a holding (contract section 8.1, agreed and not built) and the Switch now route.

## After the review

The review (`missions/license-switching/reviews/3925-cockpit-api-review.md`) asked for changes; every finding is
answered in `reviews/3925-answers.md`. What it changed in the product: the usage history is bounded per DevThrottle
account and recognises a repeated reading by the machine's own read time; a stale account's windows are `stale`; a
machine is named with its user where a name repeats and carries its identifier in every list; the soonest sign-in
expiry is on the account row; the forecast's windows carry the overview's words; the forecaster no longer asks for a
date past the end of the calendar; a switch row in an unknown state no longer says it is switching; a log row says in
words whether its switch was confirmed; and the fold asks the engine through a preview that writes no log line.

### Counts at `c3252762b` (the review fixes, with `origin/main` at `982091146` merged in)

- `dotnet test src/CcDirector.Gateway.UnitTests/CcDirector.Gateway.UnitTests.csproj --filter "FullyQualifiedName~Licenses"`:
  passed 288, failed 0 (252 before the review).
- `dotnet test src/CcDirector.Gateway.Tests/CcDirector.Gateway.Tests.csproj --filter "FullyQualifiedName~LicenseHostTests"`:
  passed 8, failed 0.
- `LicenseMessagesCarryNoKeyTests` passes with the two new types (`LicenseMachineRefDto`, `LicenseAccountSignInExpiryDto`)
  in its scan; it ran inside the gate below.

### Sixteen more mutations

Run the same way as the first eighteen: one change to the product code, alone, every license unit test run (288),
the file restored from its original bytes in a `finally` block. After the last one `git status` was clean and the
rebuilt tree passed 288 of 288.

| | The behaviour broken | Failed | Went red |
|---|---|---|---|
| R01 | The history keeps any number of accounts for one DevThrottle account | 1 | `Record_MoreAccountsThanTheMostKept_DropsTheOnesHeardFromLongestAgo` |
| R02 | The cap on accounts drops one too many | 2 | `Record_ExactlyTheMostAccountsKept_DropsNone`, and the test of R01 |
| R03 | A reading stamped in the future keeps its own time | 2 | `Record_AReadingStampedInTheFuture_IsKeptAtNow_AndAgesOutLikeAnyOther`, and the test of R01 |
| R04 | A repeated reading is recognised by the corrected time (the code before the review) | 1 | `Record_TheSameReadingSentAgain_AddsNothing_EvenWhenTheClockCorrectionDiffersEachTime` |
| R05 | The history is read without the lock | 1 | `RecordAndRead_FromSeveralThreadsAtOnce_NeverSeeAHalfWrittenSeries` - see below |
| R06 | A stale account's window keeps the level its old number gives | 3 | `Account_Stale_ItsWindowsAreStaleToo_...`, `Window_OfAStaleReading_IsStale_...`, `Forecast_AWindowAtItsLimit_OrOfAStaleReading_SaysSoInItsLevel` |
| R07 | The account's sign-in expiry is the latest across its machines, not the soonest | 1 | `Account_SignInExpiry_IsTheSoonestAcrossItsMachines_WithTheMachineTheVerdictAndTheSentence` |
| R08 | The forecaster works out a date however slow the rate (the code before the review) | 4 | Both rows of `Forecast_ARateSoSlowTheWindowWouldNeedLongerThanAWeek_...`, `Forecast_AWindowThatNeedsExactlyAWeek_...`, and through the fold `Forecast_AWindowRisingSoSlowly...AndNothingThrows` |
| R09 | Two users of one machine are called by the machine name alone | 2 | Both `TwoUsersOfOneMachine_...` tests |
| R10 | A switch row in a state this build does not know says it is switching | 1 | `LogEntry_ASwitchRowInAStateThisBuildDoesNotKnow_NeverSaysItIsSwitching` |
| R11 | A refused or failed switch row is given words for its usage read | 2 | `LogEntry_SaysWhetherTheSwitchWasConfirmed_OnlyOnARowWhoseSwitchWasMade` (the `failed` and the `unknown` rows) |
| R12 | The fold asks the engine through the logging `DecideManual` | 1 | `TheDisplayFold_AsksThroughThePreview_SoAReadOfThePageWritesNoDecisionLines` |
| R13 | The forecast's window carries no level | 3 | `Forecast_AWindowAtItsLimit_...`, `Forecast_AnAccountInUseThatIsRising_...`, `Forecast_AnswersTheExactShapeTheLicensesPageIsBuiltFrom` |
| R14 | The forecast's machine rows carry no identifier | 2 | `TwoUsersOfOneMachine_AreToldApart...`, `Forecast_AnswersTheExactShapeTheLicensesPageIsBuiltFrom` |
| R15 | The preview writes a log line of its own | 1 | `TheDisplayFold_AsksThroughThePreview_SoAReadOfThePageWritesNoDecisionLines` |
| R16 | The account's sign-in expiry names a shared machine by its name alone | 1 | `TwoUsersOfOneMachine_TheOneLeftOutOfSwitchNow_AndTheOneWhoseSignInExpires_AreNamedWithTheirUser` |

**R05, said plainly.** On the first pass R05 SURVIVED: the several-threads test, as first written, did not notice the
lock missing from `Read`. The test was made to read for as long as the writers write, and to write more. With 12,000
writes the mutation was then caught in two runs out of three; at the committed size (100,000 writes) it was caught in
five runs out of five. A missing lock is a race, and no test of a race is certain: this one is a strong sensor, never
a proof, and it never fails on correct code.

One mutation was considered and not run because it changes nothing: "a reading at exactly now is kept at now" - the
clamp at `>` and at `>=` give the same time.

### The gate on the final code

`.\scripts\test-local.ps1 -Parked` at `c3252762b`, tree clean, 2026-10-13, finished 04:52 UTC. `-Parked` runs every
suite of the default run and the three parked ones.

| Suite | Outcome | Tests |
|---|---|---|
| CcDirector.Core.UnitTests | Failed | 1,718 of 1,720 passed |
| CcDirector.Avalonia.Tests | Completed | 1,079 |
| CcDirector.Engine.Tests | Completed | 86 |
| CcDirector.HostedAgent.Tests | Completed | 89 |
| CcDirector.Launcher.Tests | Completed | 218 |
| CcDirector.Terminal.Avalonia.Tests | Completed | 82 |
| CcDirector.Reclaim.Tests | Completed | 305 |
| cc-director-setup.Tests | Completed | 39 |
| cc-director-setup-engine.Tests | Completed | 870 |
| cc-director-setup-cli.Tests | Completed | 74 |
| CcDirector.Gateway.Tests, four shards | Completed | 829 + 757 + 667 + 811 = 3,064 |
| CcDirector.Core.Tests | Completed | 4,585 |
| CcDirector.Gateway.UnitTests | Completed | 10,932 |

Every Gateway test passed, all 13,996 of them. The two reds are both in `CcDirector.Core.UnitTests`, both name only
files this branch does not touch (`git diff --name-only origin/main...HEAD` lists none of them), and both came in
with the merge of main:

- `BackgroundWorkRegisterTests.EveryTimerWatcherAndWaitingLoop_HasARowInTheRegister_AndEveryRowStillPointsAtOne`:
  "`src/CcDirector.Core/Backends/GitHubActionsBackend.cs` has 1 site(s) in code, the register says 2".
- `GateTestsDoNotDependOnTheMachineTests`: this time it is not a new machine-dependent use but a baseline that was
  not lowered - "Fewer machine-dependent uses than the baseline allows", naming `GitHubActionsBackendTests.cs`,
  `SessionKeysAreNeverHeldTests.cs` and `DevReportShapeCheckTests.cs`.

Both are the bookkeeping of a change to main that removed timers and sleeps from those files and did not update the
register and the baseline beside them. They are not fixed here: the one-line edits belong to the change that made
them necessary, and making them in this branch would conflict with it. The Tech Lead was told. The red of the first
gate run (`CronRunResultTests.cs`) is gone: main fixed it.

Not checked: I did not run the gate on `origin/main` alone to watch these two fail there. That they are main's rests
on what each message names and on this branch touching none of it.

### After the gate: five field names, fixed values

The Tech Lead then gave the final names of five fields phase 3 builds to: `renewsOn` and `renewsText` on an account,
`renewalFailedAtUtc`, `renewalFailedText` and `renewalFailedTone` on a holding. They are in the response types and the
shapes document, with the only values they can have until the follow-up: null, and "Renewal date not set". After that
change the 288 license unit tests, the 8 host tests and the no-key scan were run again and passed. The whole gate was
not run again: its result above is for `c3252762b`.

## After the re-review

The re-review (`reviews/3925-re-review.md`) closed the must fix and left two should fix; both are answered in
`reviews/3925-answers.md`. In the product: the ingest refuses a usage number or a read time that cannot be a reading,
and a log row carries its machine's identifier and label and uses the label in its sentence.

### Counts at `521ef729f`

- `dotnet test src/CcDirector.Gateway.UnitTests/CcDirector.Gateway.UnitTests.csproj --filter "FullyQualifiedName~Licenses"`:
  passed 314, failed 0 (288 before this round).
- The 8 host tests and the no-key scan ran inside the gate below and passed.

### Thirteen more mutations

Run the same way: one change, alone, the 314 license unit tests, the file restored in a `finally` block; `git status`
clean and 314 of 314 afterwards.

| | The behaviour broken | Failed | Went red |
|---|---|---|---|
| S01 | The ingest accepts a usage number that is not finite | 1 | `Ingest_AUsageNumberOrAReadTimeThatCannotBeAReading_...(not a number)` |
| S02 | The ingest accepts a usage number below nought | 3 | The `just below nought` and `-1e308` rows, and `Forecast_AfterAReportNoRealDirectorSends_...(infinite-rate)` |
| S03 | The ingest accepts a usage number of any size | 2 | The `just above the most` and `1e308` rows |
| S04 | The most allowed is itself refused | 1 | `Ingest_AUsageNumberAtEitherEdgeOfWhatIsAllowed_IsAccepted(1000)` |
| S05 | The ingest accepts a read time later than the sent time | 4 | `Forecast_AfterAReportNoRealDirectorSends_...(year-9999)` and `(edge-of-calendar)`, `Ingest_AReadTimeAtTheEdgeOfTheCalendar_...(False)`, the `read one second after it was sent` row |
| S06 | A reading read at the moment it was sent is refused | 15 | `Ingest_AReadTimeIsJudgedAgainstTheReportsOwnSentTime_AtEachEdge` and fourteen ordinary ingest and hub tests |
| S07 | The ingest accepts a read time any length before the sent time | 2 | `Ingest_AReadTimeAtTheStartOfTheCalendar_IsRefused_AndNeverThrows`, `Ingest_AReadTimeIsJudgedAgainstTheReportsOwnSentTime_AtEachEdge` |
| S08 | The seven-day number is not checked | 1 | `Ingest_TheSevenDayNumberIsHeldToTheSameRule` |
| S09 | The extra usage number is not checked | 1 | The `an extra usage number that is infinite` row |
| S10 | A log row calls a shared machine by its name alone | 3 | Both `LogEntries_...` tests, `Log_NamesTwoUsersOfOneMachineApart_...` |
| S11 | A log row carries no machine identifier | 2 | `LogEntries_CallAMachineWhatTheOverviewCallsIt_...`, `Log_NamesTwoUsersOfOneMachineApart_...` |
| S12 | The log's labels look only at the rows of the page | 1 | `LogEntries_CallAMachineWhatTheOverviewCallsIt_...` |
| S13 | The log's labels look only at the account's machines | 1 | `LogEntries_TwoUsersOfAMachineNoLongerListed_AreStillToldApart_FromTheRowsThemselves` |

S01 is caught by one row only, and that is as it should be: an infinite number is also above the most allowed, so
only "not a number" needs the finite check.

### The gate

`.\scripts\test-local.ps1 -Parked` at `521ef729f`, tree clean, finished 2026-10-13 05:57 UTC - the default gate and
the Gateway suites in one run.

| Suite | Outcome | Tests |
|---|---|---|
| CcDirector.Core.UnitTests | Failed | 1,718 of 1,720 passed |
| CcDirector.Avalonia.Tests | Completed | 1,079 |
| CcDirector.Engine.Tests | Completed | 86 |
| CcDirector.HostedAgent.Tests | Completed | 89 |
| CcDirector.Launcher.Tests | Completed | 218 |
| CcDirector.Terminal.Avalonia.Tests | Completed | 82 |
| CcDirector.Reclaim.Tests | Completed | 305 |
| cc-director-setup.Tests | Completed | 39 |
| cc-director-setup-engine.Tests | Completed | 870 |
| cc-director-setup-cli.Tests | Completed | 74 |
| CcDirector.Gateway.Tests, four shards | Completed | 829 + 757 + 667 + 811 = 3,064 |
| CcDirector.Core.Tests | Completed | 4,585 |
| CcDirector.Gateway.UnitTests | Completed | 10,958 |

All 14,022 Gateway tests passed. The two reds are the same two tests as in the run before
(`BackgroundWorkRegisterTests.EveryTimerWatcherAndWaitingLoop_...` and `GateTestsDoNotDependOnTheMachineTests`), in
`CcDirector.Core.UnitTests`; `origin/main` had not moved since that run, and this round touched none of the files
they name. As before, I did not run the gate on `origin/main` alone to watch them fail there.

The commit after `521ef729f` adds this section and the answers to the re-review: two markdown files, no code.

