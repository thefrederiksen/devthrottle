# Answers to the pull request 3923 review

The review is `3923-minute-check-review.md`. The Tech Lead ruled on it: fix 1 to 6, and from the notes fix 8 (a
growing wait) and 9 (log only when a check does something). Every finding is answered here, the notes too.

Each fix names the test that holds it down and the deliberate break that turns that test red. The breaks were made
in the worktree one at a time and restored; the counts are of the 201 license unit tests.

**1. Must fix - a check that fails after planning a send strands that switch. Accepted and fixed.**
The in-flight mark is no longer set when a send is planned. A check (and Switch now) makes its whole plan first, and
only then, still under the tenant's gate, marks the sends it is about to start (`MarkInFlight`). A plan that fails
part-way marks nothing. The marks are cleared on every path out - finished, failed or cancelled - by the `finally` of
`TickAsync` and of `SwitchNowAsync` (`ClearInFlight`), as well as by each send as soon as it has recorded its result.
Test: `ACheckThatFailsAfterPlanningASend_DoesNotStrandThatSwitch` is the reviewer's reproduction - a send that times
out, a check that fails on a row it cannot place after it has planned to ask again, the bad row removed, and a
healthy check that must ask about the first switch again and settle it.
Break: marking the switch when its send is planned, as before - 1 red, that test.

**2. Should fix - a resend cut off part-way leaves the row saying "never sent". Accepted and fixed, the way the
review suggests.** Before a row that is waiting for an offline Director is sent again it is written back to `pending`
with no reason (`PlanSendOfWaiting`), so if the Gateway stops or the result cannot be recorded while that send is in
flight, the row reads as "outcome not known" and holds its machine. `director-offline` is put back when the send
returns nothing, as before.
Test: `AResendCutOffPartWay_ReadsAsOutcomeNotKnown_AndTheMachineIsGivenNoSecondIdentifier` - the row is read as the
command goes out and again after the check is cancelled mid-send; a new service over the same store, with a different
best target by then, sends only the first identifier.
Break: the row is not written back - 1 red, that test.

**3. Should fix - a replayed answer overwrites a newer report when the Director's clock is ahead. Accepted and fixed
with the Gateway-clock rule.** `LicenseStore.ApplySwitchReadBack` no longer trusts the Director's clock for an answer
with `Replayed = true`: its read-back is never taken once the machine has reported after the switch row was written.
Both times are the Gateway's. The condition is part of the one conditional update, so a report landing at the same
moment still cannot be overwritten. A replayed answer for a switch this account has no row for is refused.
Tests: `ApplySwitchReadBack_ReplayedAnswerFromADirectorWhoseClockIsAhead_IsNotTakenOnceTheMachineHasReportedSinceTheRowWasWritten`
(the reviewer's numbers: 90 seconds ahead, the switch 30 seconds before the report, stamped 60 seconds after it) and
`ApplySwitchReadBack_ReplayedAnswerWhenTheMachineHasNotReportedSinceTheRowWasWritten_IsTaken`.
Break: the new condition removed - 1 red, that test.

**4. Should fix - one Director that does not answer holds up every tenant after it. Fixed now, by the Tech Lead's
ruling.** `TickEveryTenantAsync` starts each tenant's check inside that tenant's scope and awaits them all together
after the pass. What a check decides and writes happens before its first wait, so tenants are still decided one after
another and only their sends and emails overlap. The scope is an `AsyncLocal` value, so everything a check does after
a wait runs in the scope it was started in; the hosted test on a real Gateway with two accounts and a real tunnel
passes on this, which is the proof that matters for it. A minute in which the host skips the check because the last
one has not finished now writes a line (`GatewayHost.CheckLicenseSwitches`).
Test: `TickEveryTenant_ADirectorThatDoesNotAnswer_HoldsUpNoOtherTenant_AndEachSendStartsInItsOwnTenantsScope` - the
first tenant's Director answers only once the second tenant's command has arrived, and each send is seen to start in
its own tenant's scope.
Break: each tenant's whole check awaited before the next starts - 1 red, that test.
Not done: the order of tenants is still the order the tenant list gives. With the sends no longer waiting on each
other the order costs a tenant milliseconds, not minutes.

**5. Should fix - three behaviours no test held down. Accepted; three tests added, and a fourth.**
- Which Director a resend goes to: `AResend_GoesToTheDirectorOfTheMachinesLastReport_NotTheOneTheRowWasWrittenFor`.
  Break: the resend goes to the Director the row was written for - 1 red, that test.
- The in-flight mark: `TheMinuteCheck_WhileSwitchNowsSendIsInFlight_DoesNotSendThatSwitchAgain`. This is the case
  the mark exists for: a report arriving while Switch now's command is on its way would otherwise make the check send
  the same switch a second time beside the first. Break: nothing is ever marked - 1 red, that test.
  The direction the review names is held too, `SwitchNow_WhileTheMinuteChecksSendIsInFlight_LeavesThatMachineAlone`,
  but after fix 2 that one holds by the row's state alone, so the mark is not what it proves.
- Settling from a report while the machine says the switch is still running:
  `Restart_AfterNotAfter_WhileTheMachineSaysThatSwitchIsStillRunning_IsNotSettledFromItsReport`.
  Break: the condition made always true - 1 red, that test.

**6. Should fix - none of the new store statements had run on PostgreSQL. Accepted and fixed.**
`src/CcDirector.Gateway.DatabaseTests/Data/LicenseStorePostgresTests.cs`, two tests through the real `LicenseStore`
on the throwaway PostgreSQL: a switch from its pending row through not-connected, sent, unknown, settled, the
read-back taken and a replayed one refused, and a bar; and a no-room event opened, opened again as the same row,
moved, its email recorded as failed then sent then refused a change, ended, and still in the log.
`.\scripts\test-database.ps1`: GREEN, 70 executed, none failed, none skipped.
Not done: pinning the ordering of `license_switch_log.FromAccountEmail`. The review calls it consistency only, it
would be a migration on both providers, and the ruling did not ask for it. Recorded here so it is not lost.

**7. Note - a row can be settled `failed` from a report written before the switch ran. Not changed.**
The ruling did not include it. It needs the tunnel to drop and the machine to stay away past the switch's expiry
after a report that crossed the command on the wire, and the next report corrects the machine's account; only the log
row stays wrong. Comparing against the report's own `SentAtUtc` would bring the Director's clock into a rule that
fix 3 has just made free of it, so I would rather it were decided on its own than slipped in here.

**8. Note - the owner email. The growing wait is added, by the ruling.**
After a failed try the next one waits one minute, then two, four, eight, up to an hour
(`LicenseSwitchService.LongestOwnEmailWait`), or longer when the mail service asks for longer. The count starts again
when the email goes, when there is no account to email, and when the event ends. It is kept in memory, so a Gateway
restart starts it again at one minute.
Test: `NoTarget_AnEmailThatKeepsBeingRefused_IsTriedFurtherAndFurtherApart` - six tries in forty checks, at minutes
0, 1, 3, 7, 15 and 31. The reviewer counted 30 in 30.
Break: the wait fixed at one minute - 1 red, that test.
Not changed: a send that times out is still counted as failed and tried again, so a slow delivery can still mean two
emails. Closing that needs the account service to honour an identifier for the event, which is not this repository's
to add; it is the same gap as the stop between confirmation and mark, and both are named in the pull request.
The third point - no retry while no Director of the tenant is connected - is how the tenant pass lists tenants, and
is unchanged.

**9. Note - about 14 log lines a minute for good. Fixed in part, and the rest is not mine to change here.**
`TickAsync` no longer writes an entry line, and writes its result line only when the check sent, emailed or wrote
something. `LicenseStore.ListOpenNoRoom` is quiet like `HasMachines`. A check that fails still logs.
What is left on a check that finds nothing to do: the lines of `LicenseSnapshotSource.ReadPicture`, the read model,
`ListOpenSwitches`, `ListActiveBars` and `LicenseDecisionEngine.Decide`. The snapshot source is the shared commit
both pull requests carry and must stay identical in both, and the others are read by the Licenses page as well, where
their lines are wanted. Making them quiet for the check only needs a decision on that shared code. It is for the Tech
Lead to say whether that is done in the pull request that merges second or as its own change.

**10. Note - rules.**
- `ReadAnswer`'s try-catch: left, as the review suggests. It is the edge where a Director's text comes in.
- The store's write methods log their result and the check logs the failure once: left as it is.
- `SwitchNowAsync` takes the tenant from its caller while the send takes it from the scope: agreed, and it belongs
  in the route. It is noted in `switch-now-method.md` for the Licenses API pull request to assert.

## What else changed in this round

- A test of mine failed by hanging instead of by going red: with nothing marked in flight,
  `TheMinuteCheck_WhileSwitchNowsSendIsInFlight_DoesNotSendThatSwitchAgain` awaited a check that waited on an answer
  that never came. It now asserts that the check has finished, so the same break is a red test in seconds.
- origin/main was merged in again before the gate.
