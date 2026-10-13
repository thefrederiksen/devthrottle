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
asks only through the preview. A read of the overview now writes the fold's two lines and the route's two.
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
