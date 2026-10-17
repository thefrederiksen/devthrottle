# Track C, Developer C2 - the Director's daily approver check

Pull request 2 of 3. Built on the approver contract (pull request 4109) and on `license check-approver` (pull
request 4116, which must merge first: this Director runs that command).

## What changed

- **The offer setting.** `licenseSwitching.offerApprovers` in config.json, beside `licenseSwitching.enabled` and
  read the same way (`LicenseApproverOfferSetting`, the same shape as `LicenseSwitchingSetting`): off unless it is
  exactly `true`, read at start, and logged at start on the same line as the switching setting
  (`[LicenseSwitching] CreateForThisMachine: enabled=..., offersApprovers=...`).
- **`LicenseApproverCheck`** (`src/CcDirector.Core/Licenses`), on the Director's scheduler the way
  `LicenseRenewer` is: it looks every 10 minutes, runs once at start and then once every 24 hours, and does nothing
  while license switching is off or while this Director is not the key holder. A run:
  1. asks the Gateway `GetLicenseApproverAssignments` for the records that name this machine;
  2. runs `license check-approver --browser <identifier> --email <account>` for each;
  3. when the offer setting is on, also runs `license check-approver --browser <identifier>` with no account for
     each of this Director's own browser profiles (from the machine's browser registry), so the tool says who each
     is signed in as;
  4. sends everything in ONE `ReportLicenseApproverChecks`, even when every row failed, and an empty report when
     there was nothing to check.
  A check the tool could not run is a `check-failed` row, never left out. Each row's detail is one of the
  Director's own fixed sentences (`LicenseApproverCheckSentences`), never the tool's text, which can quote what a
  browser step printed. Rows name the profile by its identifier; the display name goes in `browserProfileName` when
  it has the shape the Gateway takes, and is left out otherwise, so an odd name cannot get the whole row refused.
  A report the Gateway did not take is sent again at the next look without running every check again, unless it
  is a day old (the Gateway refuses rows older than its not-checked window), when the checks run again. At most 32
  rows, the contract's limit; checks beyond that are not run and the log says how many.
- **The tool call.** `ILicenseCommandLine.CheckApproverAsync` runs `license check-approver --json` with an eight
  minute limit (the tool's own waits add to about six and a half). Only the contract's state words are taken: a
  state outside `LicenseApproverStates.Reportable`, a "can approve" naming no account or another one, or an answer
  for another account than asked is read as no answer.
- **The hub calls.** `GatewayStreamClient` gains `CanCheckLicenseApprovers` (connected, and the Gateway offered both
  methods in Hello), `GetLicenseApproverAssignmentsAsync` and `ReportLicenseApproverChecksAsync`;
  `GatewayLicenseApproverChannel` hands them to the check, attached and detached with the tunnel beside the
  approval relay.

## One check at a time, never during a sign-in or an approval - which rule, and why

Neither of the two offered. A new `LicenseBrowserLane`, shared by the sign-in coordinator, the approver and the
check (`LicenseSwitching` makes one and hands it to all three):

- The **key store lock** is about the key store, which a check never touches; what collides is two browser-harness
  runs on this machine's profiles.
- The **coordinator's one-at-a-time lock** is held for the whole of a sign-in, including the wait for another
  machine's approval, and that other machine can be this one: a Director that approves its own renewal. An
  approval made to take that lock would wait for the sign-in that waits for it - a deadlock until the sign-in's
  deadline. Pull request 3 proves the self-renewal path end to end; this lane is built so it cannot deadlock there.

So in the lane, sign-ins and approvals never wait for each other. A check starts only when none is running, one
check at a time; a sign-in or approval that starts while a check runs waits for that one check to end. A check
that is merely WAITING holds nothing back.

## The tests, in plain words

`src/CcDirector.Core.UnitTests/Licenses/LicenseApproverCheckTests.cs` - a fake license tool, a fake tunnel and a
fake profile list:

- `TickAsync_AssignedRecords_AreCheckedWithTheirAccount_AndGoUpInOneReport` - each record that names this machine
  is checked with its own account; one report carries both rows, with the identifier, the display name, the
  account, the state and the time of the check.
- `TickAsync_OfferOff_OwnProfilesAreNotChecked` - with the setting off, no profile is checked without an account.
- `TickAsync_OfferOn_EveryOwnProfileIsCheckedWithNoAccount_AndTheReportSaysItOffers` - with it on, every own
  profile is checked with no account as well, and the report says the machine offers.
- `TickAsync_LicenseSwitchingOff_ChecksNothing_AndAsksTheGatewayNothing` and
  `TickAsync_NotTheKeyHolder_ChecksNothing_AndAsksTheGatewayNothing` - nothing at all runs, and the Gateway is not
  even asked.
- `TickAsync_ToolGaveNoAnswer_IsReportedCheckFailed_NeverLeftOut` - a check that failed is a `check-failed` row
  with its sentence.
- `TickAsync_EveryRowFailed_TheReportIsStillSent` and `TickAsync_NothingAssignedAndNothingOffered_SendsAnEmptyReport`.
- `TickAsync_RunsOnceADay` - after a run, nothing for 23 hours, then a run at 24.
- `TickAsync_GatewayKeepsNoApproverRecords_NoCheckRuns_AndOneRunsOnceItDoes` and
  `TickAsync_AssignmentsCannotBeAsked_NoCheckRuns_AndItIsTriedAgain` - no check runs with nowhere to report it.
- `TickAsync_ReportNotTaken_IsSentAgainAtTheNextLook_WithoutCheckingAgain` and
  `TickAsync_ReportUnsentForADay_IsCheckedAgain_NotSentStale`.
- `TickAsync_DetailIsTheDirectorsOwnWords_NeverTheTools` - the fixed sentences, each within the contract's length,
  and the fake tool's planted text appears nowhere in the report.
- `TickAsync_ProfileNameOfAShapeTheGatewayRefuses_IsNotSent_TheRowStillIs` - also a record naming a profile this
  machine does not have: the row goes, with no name.
- `TickAsync_MoreThanAReportTakes_StopsAtThirtyTwoRows`.
- `TickAsync_ASignInIsRunning_TheCheckWaitsForIt_AndRunsAfter` and
  `TickAsync_AnApprovalArrivesDuringACheck_WaitsForTheCheckToEnd` - the lane, seen from the check.

`LicenseBrowserLaneTests.cs`: a sign-in and its own approval never wait for each other; a check waits for a running
sign-in; an approval waits for a running check; a WAITING check holds no approval back (the self-renewal case);
one check at a time; leaving twice leaves once; a cancelled wait takes nothing.

`LicenseSignInCommandLineTests.ReadApproverCheck_OnlyTheContractsWordsForTheAccountAsked_AreAnAnswer` - the four
answers that are taken, and the six that are not.

`LicenseApproverOfferSettingTests.cs` - off unless exactly true; it sits beside the switching setting without
changing it; its patch writes only its own key.

Not tested here: the three-line hub calls and the channel class, which only pass their arguments to
`HubConnection.InvokeAsync`; the hosted proof of the whole path is pull request 3's.

## Gate output

- `.\scripts\test-local.ps1` at commit 77a6bb521, tree clean: RESULT GREEN, every suite Completed with all tests
  executed (`CcDirector.Core.UnitTests` 2,169, `CcDirector.Avalonia.Tests` 1,063, `CcDirector.Reclaim.Tests` 305
  among them).
- `.\scripts\test-local.ps1 -Parked` at the same commit, with TEMP and TMP on D: as the Delivery Lead asked (C: is
  short of room): every suite Completed with all tests executed, the parked ones green - `CcDirector.Gateway.UnitTests`
  13,935, `CcDirector.Gateway.Tests` 1,764 in four shards, `CcDirector.Core.Tests` 4,538. One test failed, the same
  one as in pull request 4116's parked run and for the same reason:
  `ReclaimRefusalTests.Reclaim_APathThatIsNotCanonicalAfterResolution_IsRefused` needs Windows short (8.3) names,
  which drive D: does not make. It passed in this commit's default run above, where TEMP is on C:. It is not this
  change's and is left for its own test-only pull request.

## After the review of this pull request

`review-4123.md`, answered in `review-4123-answers.md`. What changed, in plain words:

- A sign-in or approval that meets a running check waits only as long as it could still start in time: 30 seconds
  after it arrived for an approval (the Gateway's three-minute wait less the tool's 150 seconds) and for a sign-in the
  Gateway asked for (its five minutes less the three-minute approval wait and the 90-second finish), and until its own
  deadline for the daily renewal. Once in the lane it looks at the time again. Too late is `expired`, and no tool
  runs: nothing is pressed for a sign-in that was already given up.
- A sign-in or approval that is waiting stops the next check of a run from starting, so it goes in after the one check
  running now.
- The next run is a day after the last run's checks started, not after the Gateway took its report.
- The display-name shape is one regular expression in the Contracts project, read by both sides.

New tests: lane `EnterWork_NoCheckRunning_EntersAtOnce_AndStartsNoTimer`,
`EnterWork_WhileACheckRuns_GivesUpAfterItsLongestWait_AndTakesNothing`,
`EnterWork_WhileACheckRuns_WithNoTimeLeft_GivesUpAtOnce`, `EnterCheck_WhileWorkIsWaiting_LetsThatWorkGoFirst`,
`EnterWork_CancelledWhileWaiting_NoLongerHoldsTheNextCheckBack`; check
`TickAsync_AnApprovalArrivesDuringARunOfTwoChecks_GoesInBetweenThem_NotAfterBoth`,
`Approve_DuringACheck_ThatOutlastsTheTimeToApproveInTime_IsRefusedExpired_AndPressesNothing`,
`Approve_DuringACheck_ThatEndsOnlyAfterTheTimeRanOut_IsRefusedExpired_AndPressesNothing`,
`Approve_DuringACheck_ThatEndsInTime_Approves`,
`SignIn_TheGatewayAskedFor_DuringACheck_ThatOutlastsItsTime_FailsExpired_AndStartsNoLogin`,
`TickAsync_AReportTakenLate_TheNextChecksRunADayAfterTheyStarted_NotADayAfterTheReport`; and the scheduler
assertions in the two `Start_LicenseSwitchingTurned...` tests. Each new test of a fix was shown to fail with that fix
put back (see the answers).

Gate at e38673081, tree clean: `.\scripts\test-local.ps1` RESULT GREEN; `.\scripts\test-local.ps1 -Parked` with
TEMP and TMP on D: RESULT GREEN, every suite Completed - `CcDirector.Gateway.UnitTests` 13,986,
`CcDirector.Gateway.Tests` 1,775 executed of 1,778 (three reported skipped in shard 3), `CcDirector.Core.Tests` 4,538,
`CcDirector.Core.UnitTests` 2,213, `CcDirector.Reclaim.Tests` 305 with no failure.
