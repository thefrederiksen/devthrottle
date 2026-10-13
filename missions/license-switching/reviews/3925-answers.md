# Answers to the pull request 3925 review

The review is `3925-cockpit-api-review.md`. Every finding is answered below, in its order. The code is commit
`bf185c8db` and the two that follow it; the shapes document `cockpit-api.md` was regenerated from the real route
handlers afterwards, so its examples show every new field. The mutation numbers (R01 to R16) are the table in
`missions/license-switching/proof/gateway-cockpit-api.md`.

**1. Must fix - the usage history is not bounded. Accepted and fixed.**
Both holes are closed in `LicenseUsageHistory`:
- At most `MaxAccountsPerTenant` (256) accounts are kept for one DevThrottle account. Past that the account heard from
  longest ago goes. 256 is four full reports of 64 holdings - many times any real fleet, and a fixed ceiling.
- No point is kept later than the Gateway's own clock. A reading stamped ahead is kept AT now, so it ages out in two
  hours like any other. It is kept rather than dropped because a machine whose clock is a minute fast is a real
  machine, and dropping its readings would leave its account with no rate at all.
The worst one DevThrottle account can now hold is 256 accounts times two windows times 600 points, whatever its
Directors send.
Tests: `Record_MoreAccountsThanTheMostKept_DropsTheOnesHeardFromLongestAgo` is the reviewer's own case (50 reports of
64 new accounts stamped ten years ahead): 256 are kept, the newest stay, the oldest went, and three days later nothing
is left. `Record_ExactlyTheMostAccountsKept_DropsNone` is the boundary. `Record_AReadingStampedInTheFuture_IsKeptAtNow_AndAgesOutLikeAnyOther`
and `Record_AReadingAtExactlyNow_KeepsItsOwnTime` hold the second hole. Proven by mutation R01, R02 and R03.
The smaller related point (an account that stops reporting keeps its last two hours until it is read or the Gateway
restarts) is left as it is and is now written in the class comment: it is at most the bound above, once.

**2. Should fix - a stale account's window still says "at-limit". Accepted and fixed.**
A window has a new level, `stale`. `Window` takes whether the reading is stale, and for a stale account both windows
are `stale` - never `at-limit`, never `normal`. The old number is still given. The level alone is now enough to colour
a bar. Tests: `Account_Stale_ItsWindowsAreStaleToo_NeverAtLimitOrNormal_SoTheLevelAloneIsEnough` (the reviewer's 99%
reading, at the six-minute boundary) and `Window_OfAStaleReading_IsStale_WhateverItsNumber_AndAMissingOneIsStillNotKnown`.
Mutation R06.

**3. Should fix - the forecast's windows carry numbers with no words. Accepted and fixed.**
`LicenseWindowForecastDto` now carries `utilizationText`, `level` and `resetsText`, and they are not a second
implementation: the forecast window calls the same `Window` fold the overview uses, on the same reading. The schedule
compares nothing and needs no join. Tests: `Forecast_AnAccountInUseThatIsRising...` now asserts the forecast window's
words equal the overview's for the same window; `Forecast_AWindowAtItsLimit_OrOfAStaleReading_SaysSoInItsLevel`; and
the shape test names the fields. Mutation R13.

**4. Should fix - the "renewal date" of goal 1. Answered as the Tech Lead and the Delivery Lead ruled.**
Two different dates, kept apart:
- The SIGN-IN expiry is now on the account row: `signInExpiry`, the soonest across every machine holding the account,
  with its date, the machine it is on, the verdict, the sentence and the tone - the same verdict words a holding
  carries. A holding that gave no expiry cannot be the soonest; when none gave one it is "not known", never "fine".
  It is named sign-in expiry everywhere and never renewal.
- The RENEWAL date is the subscription's billing date. No report can carry it. `cockpit-api.md` now says so in its
  rules and in section 6: it arrives in the follow-up as an optional date per account that a person sets, shown as
  "Renews <date>" or "Not set", never a guess. Nothing in this pull request pretends to be it.
Tests: `Account_SignInExpiry_IsTheSoonestAcrossItsMachines_WithTheMachineTheVerdictAndTheSentence`,
`Account_SignInExpiry_CarriesTheSameVerdictItsHoldingDoes_AtEachBoundary` (expired at exactly now, soon up to exactly
three days, fine one second later) and `Account_SignInExpiry_WhenNoMachineGaveOne_IsNotKnown_AndWhenNoMachineHoldsIt_SaysSo`.
Mutation R07.

**5. Should fix - the forecast route answers 500 on a window rising very slowly. Accepted and fixed.**
Fixed where it is, in `LicenseForecaster`: the hours needed to reach the threshold are checked BEFORE any date is
worked out. A window that needs longer than a week - longer than any window lasts - is not expected to reach it.
That is the reviewer's own statement of the right answer, and it also holds when the provider gave no reset time.
Tests: `Forecast_ARateSoSlowTheWindowWouldNeedLongerThanAWeek_IsNotExpected_EvenWithNoResetTime` (the reviewer's
rate, which threw before, and a merely slow one), `Forecast_AWindowThatNeedsExactlyAWeek_IsStillExpected_AndOneHourMoreIsNot`
for the boundary, and through the fold `Forecast_AWindowRisingSoSlowly...AndNothingThrows`. Mutation R08.

**6. Should fix - a machine with two operating system users is named twice. Accepted and fixed.**
A machine is now named one way everywhere. `activeOnMachines`, `spareOnMachines`, `nextSwitch.machines` and
`signInExpiry.machine` are each the same four fields: `machineId`, `machineName`, `osUser`, `label`. The overview's
machine rows and the forecast's machine rows carry `label`, and the forecast's rows now carry `machineId`, so every
list joins to the overview's rows by identifier. `label` is the name, with the user in brackets only when another
machine has the same name. Every sentence the fold writes uses the label.
Tests: `TwoUsersOfOneMachine_AreToldApartInEveryListAndEverySentence_AndAMachineWithItsOwnNameIsNot` and
`TwoUsersOfOneMachine_TheOneLeftOutOfSwitchNow_AndTheOneWhoseSignInExpires_AreNamedWithTheirUser`. Mutations R09, R14, R16.
NOT changed, and said plainly: two sentences the fold passes on are written by the decision engine, not the fold -
a refusal ("m1 holds no usable spare for ...") and the reason a machine is left out. Those name a machine by its name
alone. The engine is on main and shared with the minute check's pull request; changing its sentences belongs there or
in the follow-up. The lists beside those sentences do carry the identifier.

**7. Should fix - a switch row in an unknown state says "is switching". Accepted and fixed.**
"is switching" is now said only for `pending` and `unknown`. A switch row in any other state this build does not know
is given its message, or, with no message, a sentence that names the state and claims nothing.
Test: `LogEntry_ASwitchRowInAStateThisBuildDoesNotKnow_NeverSaysItIsSwitching` (the reviewer's `cancelled`). Mutation R10.

**8. Should fix - a repeated reading is added again under clock skew. Accepted and fixed.**
A reading is now recognised by the machine's OWN read time, as it was sent, before any correction. The class comment
says why the corrected time cannot be used.
Test: `Record_TheSameReadingSentAgain_AddsNothing_EvenWhenTheClockCorrectionDiffersEachTime` - the reviewer's case, one
reading sent five times with a correction that differs by milliseconds: one point, and no rate ("No rate yet", not
"Not rising"). Mutation R04, which is the old behaviour exactly.

**9. Should fix - one page poll writes dozens of log lines. Accepted and fixed.**
The engine has `PreviewManual`: the same code as `DecideManual`, and so the same answer, with no log line.
`DecideManual` is now that preview plus its two lines, so anything that SENDS a switch still logs as before. The fold
asks only through the preview, so no decision line is written by a read of the page. (This sentence first said a read
"writes the fold's two lines and the route's two". That undercounted: see the answer to note 7 of the re-review below.)
Tests: `PreviewManual_GivesExactlyTheAnswerDecideManualGives` (four cases) and
`TheDisplayFold_AsksThroughThePreview_SoAReadOfThePageWritesNoDecisionLines`, which reads the source: the fold never
calls `DecideManual`, and the preview's body holds no log call. It reads the source rather than the log because the
log is one process-wide writer and a test that captured it would catch other tests' lines. Mutations R12 and R15.

**10. Note - nothing tests the history from two threads. Accepted and fixed.**
`RecordAndRead_FromSeveralThreadsAtOnce_NeverSeeAHalfWrittenSeries`: four writers on one account and four readers that
read for as long as the writers write, with no clock in it. Mutation R05 (the read without the lock): caught in five
runs out of five at the committed size. Said plainly: with a quarter of the writes it was caught two times in three,
so this is a strong sensor and not a proof - a missing lock is a race, and no test of a race is certain. It never
fails on correct code.

**11. Note - two sentences written for cases that cannot happen. Accepted and fixed.**
Both now throw instead of guessing a reason (CLAUDE.md rule 3). I checked whether finding 6's repeated names could
reach the second one: they cannot - the engine names a machine by name AND user and the fold matches on both - and the
two-user tests above pass through that code.

**12. Note - the fold's small public methods do not log. Accepted and fixed.**
The fold's header now states the exemption and its reason, naming each method, as the engine's header does for its own.

**13. Note - a log walk can, rarely, show one row twice. Wording corrected, as ruled.**
`cockpit-api.md` no longer says a row "never moves from one page to another". It says what the code gives: the walk
is held to rows dated at or before its start, nothing is ever lost, and in the one rare case the reviewer describes a
page can repeat the last row of the page before - with the advice to key the list on `entryId`.

**14. Note - `TheGuard_KnowsEveryRouteTheGatewayMaps` overstated. Wording corrected, as ruled.**
The test is renamed `TheGuard_AllowsTheThreeReads_ByTheSameConstantsTheEndpointsMapThemWith` and its comment says
what it does and does not catch: a fourth route under a new constant fails nothing there, and a new route needs its
own line there and in the host test.

**15. Note - `verifiedByUsageRead` has no words. Accepted and fixed.**
`verifiedText` is beside it: a sentence on a row whose switch was made, null on every other row - so the refused
switch of the document's example, which carries `false`, shows nothing. Test:
`LogEntry_SaysWhetherTheSwitchWasConfirmed_OnlyOnARowWhoseSwitchWasMade` (nine rows). Mutation R11.

**16. Note - the rows of one switch share nothing but their time. Ruled: one row per machine, no grouping.**
Written in `cockpit-api.md` as a decision, with the reason: each machine of one switch can end differently.

**17. Note - any session key can read every account email, machine and user. Ruled: accepted.**
Recorded in `cockpit-api.md` as a decision, in those words, beside the fact that no session key can switch anything.

**18. Note - what "never a sign-in key" is and is not proven by. Wording corrected, as ruled.**
Rule 7 of `cockpit-api.md` now says the test holds member NAMES only, names the two free-text values that pass
through from a Director unchanged, and says nothing on the Gateway's side reads their content.

## What changed for phase 3 because of this review

Names a page built from the first version of `cockpit-api.md` would have used, and what they are now:
- `activeOnMachines`, `spareOnMachines`, `nextSwitch.machines`: were lists of names, are lists of the four machine fields.
- New: `signInExpiry` on an account; `label` on a machine and on a forecast machine; `machineId` on a forecast machine;
  `utilizationText`, `level`, `resetsText` on a forecast window; `verifiedText` on a log row; the window level `stale`.
- Nothing was removed.

## The final field names (the Tech Lead's message after the answers above)

Added to this pull request, with the names phase 3 builds to:
- On an account: `renewsOn` (a date, `yyyy-MM-dd`, or null) and `renewsText`. No date can be set yet, so `renewsOn`
  is null and `renewsText` is "Renewal date not set" for every account.
- On a holding: `renewalFailedAtUtc`, `renewalFailedText`, `renewalFailedTone` - null when the last renewal worked or
  is not known. The Gateway does not store a report's renewal result yet, so all three are null on every holding.
`cockpit-api.md` documents all five, and its section 6 is now final: the follow-up pull request "Licenses API: Switch
now, renewal date, failed renewal" - `POST /gateway/licenses/switch-now`, `PUT /gateway/licenses/accounts/renewal`
with its stored column, and storing the renewal result from the report, in one migration for both providers.
The shape test names the five fields and asserts today's values. They are fixed values with no rule behind them yet,
so there was nothing to prove by mutation; the rules arrive with the follow-up and are tested there.
Checked after this change: the 288 license unit tests, the 8 host tests and the no-key scan. The whole gate was NOT
run again for it - the gate result in the proof file is for `c3252762b`, the commit before these five fields.

# Answers to the re-review (`3925-re-review.md`)

**1. Should fix - the forecast route still answers 500 on input a Director can send. Accepted and fixed, at the ingest.**
`LicenseUsageIngest.Malformed` now refuses a holding whose numbers or read time cannot be a reading. The change to
that file is one line in `Malformed` and one helper below it (`UnreadableReading`), kept small because the minute
check's pull request edits the same file. Refused:
- a usage number - 5-hour, weekly or extra usage - that is not a number, is below 0, or is above 1,000. The provider
  reports 0 to 100 and can pass 100 a little; 1,000 is far outside it;
- a read time LATER than the report's own sent time. One clock wrote both, so that is a defect and not a wrong clock:
  a machine whose whole clock is wrong moves both together, is still accepted, and is corrected as before;
- a read time more than ten years BEFORE the sent time. The re-review did not ask for this one. It is there because
  with it the sum of a read time and a clock correction can never leave the calendar in either direction: the read
  time is within ten years of the sent time, and the correction is the receive time less the sent time.
Both times are judged against the report's sent time, never against a clock.
This closes the two ways to a 500 the reviewer ran, the "Infinity%" and the three-hundred-digit number on the page,
and notes 3 and 4 with them: a reading can no longer be stamped later than its report, so it can no longer be fresh
for ever, and the sum at the edge of the calendar can no longer throw.
Tests, one for each case the reviewer ran, through the real ingest, the real store and the real route handlers:
`Forecast_AfterAReportNoRealDirectorSends_StillAnswers_BecauseTheReportWasRefusedAtTheIngest` with three rows -
the infinite rate (-1e308 then 96), the year 9999, and the edge-of-calendar sum. Each asserts a report was refused,
both routes answer 200, and neither answer contains "Infinity" or "9999". At the ingest itself:
`Ingest_AUsageNumberOrAReadTimeThatCannotBeAReading_IsRefused_AndStoresNothing` (nine rows),
`Ingest_TheSevenDayNumberIsHeldToTheSameRule`, `Ingest_AUsageNumberAtEitherEdgeOfWhatIsAllowed_IsAccepted` (0, 100,
103.5 and exactly 1,000), `Ingest_AReadTimeIsJudgedAgainstTheReportsOwnSentTime_AtEachEdge`,
`Ingest_AMachineWhoseWholeClockIsWrong_IsStillAccepted_...` (and it does go stale),
`Ingest_AReadTimeAtTheEdgeOfTheCalendar_IsRefusedOrCorrected_AndNeverThrows` and
`Ingest_AReadTimeAtTheStartOfTheCalendar_IsRefused_AndNeverThrows`. Mutations S01 to S09.
Said plainly: the forecaster itself was not changed in this round. It is safe because what reaches it is now always a
real percentage and a read time no later than its report - not because it defends itself a second time.

**2. Should fix - the log and the engine's sentences name a machine by its name alone. The log is fixed; the engine's
sentences are written into rule 9 as an exception, as ruled.**
- The log row now carries `machineId` and `label` beside `machineName` and `osUser` - the same four fields as every
  other list - and its `summary` uses the label. The label counts every machine of the account AND the machines of
  the rows on the page, so two users of a machine that is no longer listed are still told apart. A row about no
  machine has a null `machineId` and an empty `label`. Tests: `Log_NamesTwoUsersOfOneMachineApart_WithTheIdentifierOfEachRowInTheOverview`
  (the reviewer's case, with the two spellings of the name, and it asserts the log's identifiers and labels are the
  overview's), `LogEntries_CallAMachineWhatTheOverviewCallsIt_AndARowAboutNoMachineHasNoIdentifierAndNoLabel` and
  `LogEntries_TwoUsersOfAMachineNoLongerListed_AreStillToldApart_FromTheRowsThemselves`. Mutations S10 to S13.
- Rule 9 of `cockpit-api.md` no longer says every sentence uses the label. It says every sentence the FOLD writes
  does, names the three sentences the decision engine writes that use the machine name alone, says what the page
  does with them, and names the pull request that removes the exception: "Licenses API: Switch now, renewal date,
  failed renewal".

**3. Note - a reading stamped later than now is never stale.** Closed by the check of finding 1: such a report is
refused. `Ingest_AMachineWhoseWholeClockIsWrong_IsStillAccepted_...` shows the honest case (the whole clock 400 days
fast) is accepted and goes stale after seven minutes like any other.

**4. Note - a read time at the edge of the calendar makes the ingest throw.** Closed by the check of finding 1, at
both ends of the calendar; the two tests named for the edge and the start of the calendar hold it.
`LicenseUsageHistory.Record` still does the same sum when called directly with a time no ingest would pass; nothing
but the ingest calls it.

**5. Note - a flood of new accounts pushes the real ones out of the history.** Not changed. It is the price of the
bound, it stays inside one DevThrottle account, and it needs more than 256 accounts. It is a known price now.

**6. Note - a repeat is recognised by read time alone, across machines.** Not changed: it needs two machines to read
one account at the same tick, and the cost is one number in a rate.

**7. Note - the answer to finding 9 undercounted the lines of a read. Corrected.** The reviewer measured it: one read
of the overview writes 12 lines, the forecast 15 and the log 4, because the store, the read model and the snapshot
source each log their own. (The log is now one read more: it also reads the machines' names for its labels.) What
the fix of finding 9 achieved, and all it achieved, is that the count does not grow with the number of accounts and
that no decision line is among them. The sentence in the first answers is corrected in place. The reviewer's other
point stands as well: the test reads source text, so a log call added somewhere other than the two places it reads
would pass it.

**8. Note - "renewal" now means two things. Field names kept, as ruled; the document now keeps them apart.**
`cockpit-api.md` has a decision in section 1 saying that `renewsOn` and `renewsText` are the SUBSCRIPTION's billing
renewal and the three `renewalFailed...` fields are a machine renewing its SIGN-IN, that a failed sign-in renewal says
nothing about the subscription, and where the page shows each. The paragraph on a holding's failed renewal says it
again.

**9. Note - the three failed renewal fields were not in the fold.** Fixed: `Holding()` now sets the three, to null,
with a comment saying why and that the pull request storing the renewal result fills them in there.

**10. Note - a switch row with an empty state word.** Fixed: its summary says "with no state recorded" rather than
calling "Recorded" an unknown state. Test: `LogEntry_ASwitchRowWithNoStateWord_SaysNoStateWasRecorded_...`.

**11. Note - the several-threads test takes seven to ten seconds.** Not changed. It is that size because at a quarter
of it the missing lock was caught only two times in three. It is in a parked suite. The reviewer's warning is fair
and is repeated here: it is the first test to look at if this suite shows a red that passes alone.

## After the re-review: what was run

At `521ef729f`, tree clean: 314 license unit tests (288 before this round) and 8 host tests pass. Thirteen more
mutations, all caught. `.\scripts\test-local.ps1 -Parked` - the default gate and the Gateway suites in one run -
completed every suite except `CcDirector.Core.UnitTests`, 1,718 of 1,720, with the same two tests red as in the run
before, each naming only files this branch does not touch. All 14,022 Gateway tests passed. The table is in the proof
file. The commit that adds this text and the proof's last section changes two markdown files and nothing else.

## One change before the merge (the Tech Lead's message after the re-review answers)

**A read time a little ahead of the sent time is accepted. Accepted and fixed.**
The check of finding 1 refused the WHOLE report when any holding's read time was later than the sent time by any
amount. That was too strict, and the reason is real: a spare is read every ten minutes and its read time is kept
between reads. When time synchronisation steps the machine's clock back, that kept read time is ahead of every report
for up to ten minutes; every report was refused, the machine went stale and could not be switched.
The ingest now refuses only a read time more than `LicenseContract.ClockSkewLoggedAbove` (two minutes) later than the
sent time - the same amount of skew it already ignores between a machine's clock and the Gateway's. The sentences
above that say "a read time LATER than the report's own sent time" describe the first version; this is the rule now.
Tests: `Ingest_AReadTimeALittleAheadOfTheSentTime_IsAccepted_BecauseAClockSteppedBackIsNotADefect` (1 second, 90
seconds and exactly 120 seconds ahead - accepted, the machine is not stale and its spare is listed); the rows "read
three minutes after it was sent" and "read one second more than two minutes after it was sent" of
`Ingest_AUsageNumberOrAReadTimeThatCannotBeAReading_...` are refused; and the year 9999 is still refused - both
year-9999 tests and the edge-of-calendar tests pass unchanged.
The history: an accepted reading that is ahead of the Gateway's clock is still kept AT now and no later, and ages out
like any other - `Ingest_AReadTimeALittleAheadOfTheSentTime_IsKeptInTheHistoryAtNow_NeverLaterThanTheGatewaysClock`,
through the real ingest. (It is kept at now, not dropped: that is what the first review's fix does, and why.)
What the tolerance costs, said plainly: a reading up to two minutes ahead is judged stale up to two minutes later
than it truly is, and the sum of a read time and a clock correction can now pass the Gateway's receive time by up to
two minutes - nowhere near the end of the calendar.
Mutations T01 to T04 in the proof file, all caught. Run after the change: 319 license unit tests and 8 host tests.
The whole gate was not run again for this one condition.
