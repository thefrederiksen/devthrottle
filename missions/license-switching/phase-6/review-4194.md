# Review of pull request 4194 - an approver the owner sets is checked within ten minutes, not a day

17 October 2026. Written by a separate review session opened by the License Switching Delivery Lead. The reviewer
read and ran tests in a scratch worktree of its own; it built nothing into the product, fixed nothing, committed
nothing and did not touch the Developer's worktree.

## Verdict

No finding. The pull request closes Finding 1 of the phase 6 review as described, the dangerous actions stay guarded,
readiness still refuses an unchecked record, and every new test fails without the change it names. Three observations
for the Delivery Lead, none of which blocks the merge.

## Scope

**What I read.** The pull request body and its 19 files (`gh pr view 4194`, the diff of
`origin/main...origin/license-approver-check-when-set`, one commit `ab7e70d44` on base `58e563ba0`); Finding 1 and its
two further shapes in `phase-review.md`. On the branch: `LicenseApproverCheck.cs` whole (the Director's look, the daily
run, the due run, the unsent report), `LicenseApproverMessages.cs` (`CheckDue`, `DueCheckWithin`),
`LicenseApproverRegistration.cs` (`Assignments`, `ReportingMachine`), `LicenseApproverReadiness.cs` (`ReachOf`,
`Verdict`, the three sentences), `LicenseApproverAlarm.cs` (`ConditionsNow`, `RaiseSoon`), `LicenseSignInFold.cs`,
`GatewayHost.cs` (the two constructor changes), `LicenseApproverStore.cs` (`StateOf`, `Set` keeping a check only for
the very same machine, user and profile), `LicenseApprovalRelay.cs` whole, the Director's `LicenseApprover.ApproveAsync`
in `LicenseSignInCoordinator.cs` (the refusal with switching off), `LicenseSwitching.cs` (what `Start` starts and under
what), `LicenseUsageReporter.cs` (reports only when the key holder lock is held), `LicenseAlarmService.Reconcile` (an
alarm still true has its words updated and nothing is sent), `DirectorHub.GetLicenseApproverAssignments`,
`GatewayLicenseApproverChannel`, `LicenseReadinessFold.WhatIsDone` (the approver gate), `LicenseContract.ReportInterval`
(2 minutes) and `LicenseStore.SilentAfter` (10 minutes). The contract and Cockpit API changes and the Cockpit fixtures.

**What I ran.** In a scratch worktree cut at `ab7e70d44` and deleted afterwards:

| Run | State of the tree | Result |
|---|---|---|
| `LicenseApproverCheckTests` (Core.UnitTests) | as on the branch | 31 passed |
| `LicenseApproverCheckTests` | `LicenseApproverCheck.cs` restored to origin/main | 4 failed, 27 passed: `..._IsCheckedAtTheNextLook_NotAtTheDailyRun`, `..._IsNotCheckedAgainUntilTheDailyRun`, `..._IsCheckedAgainWhenDue`, `..._IsSentAgainAtTheNextLook_WithoutCheckingAgain` |
| `Tick_TheOwnerSetsAnApproverAt80_...` (Gateway.UnitTests) | `LicenseApproverCheck.cs` restored to origin/main | failed (the look returns null, `report!.Rows` throws) |
| `LicenseApproverRegistrationTests` + `Tick_TheOwnerSetsAnApproverAt80_...` | `CheckDue = false` in `LicenseApproverRegistration.Assignments` | 4 failed: the three `Assignments_..._IsMarkedCheckDue...` tests and the end-to-end test |
| `LicenseApproverRegistrationTests`, `LicenseReadinessServiceTests`, `LicenseApproverReadinessTests`, `LicenseApproverAlarmTests`, `LicenseSignInEndpointsTests` | as on the branch | 153 passed |

The two tests that pass without the Director change (`..._ARecordNotMarkedDue_IsNotChecked...` and
`CheckInterval_IsTheCadence...`) pass on main too, as they should: main never checked between runs either, and the
interval was already ten minutes.

**What I could not reach.** No live Gateway or Director. The Developer's full `test-local.ps1` and `-Parked -Filter
License` runs are its own testimony; I did not repeat them. The Cockpit `vitest` run was not repeated (only fixture
text changed there).

## The brief's questions, answered

**Does a hand-set approver get checked within minutes?** Yes. `Set` stores the record with no check time, so
`LicenseApproverHealth.StateOf` reads `not-checked` and `Assignments` marks it `checkDue`. The approving Director's job
looks every `CheckInterval` (now `LicenseApproverContract.DueCheckWithin`, 10 minutes), asks for its assignments on
every look that has no unsent report, and runs `CheckDueAsync` on each due record it has not checked since its last
daily run. The end-to-end test wires the real `LicenseApproverCheck` to the real `LicenseApproverRegistration` and
proves the readiness tick after that check starts the sign-in.

**A record from before phase 6?** Yes, by the same path: null `State` and null `CheckedAtUtc` read `not-checked`
(`Assignments_ARecordStoredBeforeChecksExisted_IsMarkedCheckDue` writes the old JSON shape). It needs the approving
Director to be on a build that reads `checkDue`; see observation 1.

**A record on a machine with license switching off?** It is not checked, and the pull request is right that this is
consistent rather than a gap: the Director's `LicenseApprover.ApproveAsync` refuses with "License switching is turned
off on this Director, so nothing was approved." before anything else, and the relay only finds a machine that has
reported. The phase review's sentence that manual sign-ins through such a machine keep working was wrong on main; the
pull request says so and the contract now carries it (10.4). The sentence for that machine tells the owner to turn
switching on.

**Does the never-checked sentence tell the truth?** For the three states it distinguishes, yes. `ReachOf` names the
machine by `NamesMachine`, then the connection of the Director that last reported for it, then silence past
`SilentAfter`. The Director that reports is the key holder (`LicenseUsageReporter` reports only while
`_holder.IsHeld`), and the key holder is the one that checks (`TickAsync` returns at once otherwise), so "connected and
reporting" does name the Director that will run the check. Reporting is every 2 minutes, so a reporting Director is
never read as silent. Words change as reach changes, and `Reconcile` updates the words of an open alarm without
sending; no email per flip.

**Can anything now press Authorize, sign in, copy a key, or alarm every minute?** No. The due run calls
`CheckApproverAsync` only, which presses nothing; it runs inside the browser lane like the daily run. A due record is
checked once per daily cycle (`_checkedWhenDue`, cleared only by a daily run and trimmed to the current assignments),
so a row the Gateway refuses or keeps reading as not-checked cannot open the browser every ten minutes; the test
`..._IsNotCheckedAgainUntilTheDailyRun` pins it. A due report the Gateway could not take is resent as-is and does not
move `LastRunUtc`. The alarm is keyed on the account alone, as before.

**Does readiness still refuse an unchecked record?** Yes. `NotCheckedWhatToDo` always returns a sentence, so
`Verdict.WhatToDo` is non-null and `Sentence` is non-null for every reach; `Sentence_NeverChecked_IsNotReady_WhateverTheReach`
walks every enum value. The readiness fold's `ApproverNotReady` branch is unchanged.

**Do the tests fail without the change?** Yes, both halves, as the table above shows.

## Observations - not findings

1. **The connected sentence is true only of a Director on a build that reads `checkDue`.** Until an approving Director
   has updated past this pull request, "Nothing to do: the Director on machine-a is connected and checks it within
   10 minutes" is said of a Director that will not. The update itself restarts the Director, and a start runs the
   full check, so it heals on its own; the window is the Director update window, not a day. Worth a line in the
   release notes.
2. **The same shape remains for a record that WAS checked and found wanting.** A due check that answers `signed-out`,
   `needs-fresh-sign-in` or `wrong-account` stores that state, so the record is no longer due. The alarm then tells the
   owner to sign in to claude.ai in that browser, and when the owner does, nothing re-checks the record until the
   daily run, so the automatic sign-in waits up to a day again; and the owner cannot force a re-check from the page,
   because `Set` with the same machine, user and profile keeps the old check. This is outside Finding 1 and this pull
   request's brief. It is the next place the phase goal "does not need the user" leaks, and the pull request's
   mechanism (mark due) would carry it with a one-line rule change if the Delivery Lead wants it.
3. **The Developer's own observation stands.** Setting an approver while its Director is connected still opens the
   approver alarm, with one email and one push, for the up-to-ten minutes before the check lands. Its words are now
   true. Whether it should be held back is the owner's call, as the pull request says.
