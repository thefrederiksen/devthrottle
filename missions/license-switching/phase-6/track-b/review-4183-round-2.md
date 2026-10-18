# Review of pull request 4183, round two - Forget this machine (Track B), the fixes

Reviewed at head 457a3adfc (branch license-forget-machine, rebased onto 8994f5e1a). Compared by content with
a2eb54c6f, the head of round one. Reviewer: a separate review session.

**Verdict: ready to merge once it is rebased again.** All four round-one findings are fixed or corrected as the
Developer's answers say, and I could break none of the three fixes. Two small things should be fixed, neither of them
blocking (findings 1 and 2 below). Main has moved again since the rebase: 4115 merged, and the branch now conflicts with
origin/main (30556737f) in two files - a rebase is needed before the merge, and both conflicts are trivial.

## Scope

- Read: the diff from the merge base (17 files), with `LicenseStore.ForgetMachine`, `LicenseApproverStore.RemoveForMachine`
  and `AccountsApprovedBy`, `LicenseDisplayFold.ForgetVerdict` and the status order, the route, and the Developer's
  answers. Around the fixes: `LicenseSwitchService` (Classify, PlanTick, RecordResult, HoldIfItKeepsFailing),
  `LicenseStore` (ApplyReport, ListOpenSwitches, RecordSend, TryMark, TrySettle, ApplySwitchReadBack,
  HoldAfterRepeatedFailure), the fold's `SwitchInProgress` and `LogEntry`, `LicenseDecisionEngine` line 544,
  `LicenseApproverAlarm`'s conditions, `LicenseUsageIngest`'s order of refusal and apply, the Cockpit's rendering of
  `reasonText`, the API document, the proof document and the pull request body. On origin/main: what 4115 added
  (`cannot-switch` and `can-switch-again` rows).
- Ran, out of tree: a `git archive` of 457a3adfc in my scratchpad, `dotnet test` of `Gateway.UnitTests` filtered to
  `LicenseForgetMachineTests` and my own `ReviewRound2ForgetTests`. 19 tests: the Developer's 16 passed, and my 3
  passed (two of them confirm findings 1 and 2; the third clears the "comes back with an old switch id" question).
  I did not run the gate, did not build in or edit either worktree, and touched no real store or Gateway.
- Could not reach: a live Gateway; a settings store that fails on write (so the "save succeeded, removal failed" case is
  reasoned, not reproduced).

## Fix 1 - an open switch no longer stops the forget, and is ended as failed with `machine-forgotten`

**Never on a machine that is still reporting: true through the route, with one window in the store.** The fold's
status order is refusal, then stale, then switching (`LicenseDisplayFold.Machine`, lines 441 to 448), so a reporting
machine mid-switch is `switching` and `ForgetVerdict` refuses it with `forget_not_offered`; a stale machine mid-switch
is `not-reporting` and is offered. The route asks `ForgetVerdict` of the same folded overview before it calls the
store. The store itself does not check staleness again - see finding 1.

**The decision engine, the minute check, the Track A retry and the history stay consistent.**
- The minute check: `ListOpenSwitches` is by state (`pending` or `unknown`); the forget ends every row of the machine
  that is `FinishedAtUtc == null`, kind `switch` and `IsOpen(state)`, which is the same set. So no open row names the
  deleted machine row, and PlanTick's throw (line 439) cannot fire. The Developer's test runs the real check for three
  days before and once after; it passes.
- A send in flight at the moment of the forget (possible for a refused machine whose Director is connected, or a
  machine that reported once after a dropped send: case 1 of PlanTick plans at most one more send): `RecordSend`,
  `TryMark` and `TrySettle` all go through `OpenSwitch(ctx, id)`, so on the ended row each changes nothing and
  returns false; `ApplySwitchReadBack` updates a machine row by the OLD id, which is gone, so nothing is taken;
  `HoldIfItKeepsFailing` runs only when `TrySettle` returned true. The one residue is a `_sentAfterExpiry` entry that
  can linger for an ended row when the in-flight answer was a timeout; harmless.
- The Track A left-behind retry is the decision engine deciding afresh each tick for machines in the overview; a
  forgotten machine is not in it, and the engine never sends to a stale or refused machine. Nothing to retry.
- `HoldAfterRepeatedFailure` counts earlier rows by `MachineId`, and a machine that comes back is a new row with a
  new id (`ApplyReport` creates a fresh entity), so the `machine-forgotten` failure never starts a hold on it.
- A machine that reports again with the old `SwitchInProgressId` (my third test): a new machine row, status
  `switching` with the text "The machine reports a switch in progress.", `canForget` false, the old row stays
  `failed`, the minute check runs twice without a word, and the decision engine skips the machine while it reports a
  switch (line 544) - until the Director's next report clears the field, exactly as for any reported switch. Nothing
  looks the id up in the log and throws.
- 4115 (now on main, not in this branch): its `cannot-switch` and `can-switch-again` rows are `recorded` with
  `FinishedAtUtc` set when written, so a forget leaves none of them open; its `CannotSwitchReason` lives on the machine
  row, which goes with it.

**The refusal `switch_in_progress` is gone** from the code, the fold and the API document; the Cockpit tests still pin
`forget_not_offered` and `sign_in_in_progress`.

## Fix 2 - the approvers on that machine and user are removed after the save

**Removes nothing elsewhere.** `RemoveForMachine` reads `Read(tenant)` under the store's lock - the per-tenant
settings row, which no other tenant can reach - and removes only records where `NamesMachine(machineName, osUser)` is
true (name and user, case-insensitive, whatever the profile). An approver on another machine, or another user of the
same machine, stays; the Developer's test shows the one on machine-b surviving. A machine name and user are unique per
tenant (the `(MachineKey, OsUserKey)` lookup in `ApplyReport`), so there is no second machine row this could hit. For a
stand-in machine (never accepted), the name and user come from the refusal row, which is how the approver would have
named it.

**The alarm closes, and no new one opens.** `LicenseApproverAlarm` raises only for an approver record that cannot
approve; "An account with no approver at all is NOT an alarm here" (its summary, line 28). So removing the record
closes the alarm on the next raise, as the answers say, and the sentence "end its open problems and their alarms" is
now true for an approver machine.

**Save succeeded, removal fails.** The route removes after the store's save, outside the save; a throw from the
settings write propagates (the route's catch rethrows), so the owner gets a failed request while the machine, its
holdings and its health rows are already gone, and a retry answers 404 `machine_not_found`. The approver stays, and
the owner can remove it by hand - the Approvers table still shows it as "Not reporting yet", so it is visible. The one
wrong thing left behind is the `forgotten` row's sentence, "that approver is removed too", which is then false. The
Developer says this in the answers and the proof. It is bounded and disclosed; I do not ask for a change. Making the
row's sentence wait for the removal would need a second save, and is not worth it.

A smaller gap of the same shape: `AccountsApprovedBy` is read before the save and `RemoveForMachine` runs after it,
so an approver registered in between is removed but not named in the sentence. Milliseconds, on a machine that is
not reporting; not worth a change.

## Fix 3 - the transaction sentence

Corrected everywhere I looked: the store's own comment ("in one save, not in a transaction: a sign-in started between
the two is left open on a machine that is gone, and the sign-in check never settles it"), the API document's `409`
row, the proof document (lines 147 and 158 of the diff) and the pull request body (line 16). Each names what can slip
in. True to the code: `ForgetMachine` is one read of the open rows and one `SaveChanges`, no `BeginTransaction`.

## Finding 1 (should fix, not blocking) - the store will end a switch on a machine that reported this instant

The only guard against forgetting a reporting machine is the route's fold read. The store has the machine row and
`now` in hand and does not look at `LastReportAtUtc`. My test `TheStoreAlone_EndsASwitchOnAMachineThatReportedThisInstant`
calls `ForgetMachine` on a machine whose report was accepted at `now` with an `unknown` switch open: the switch ends
`failed` / `machine-forgotten`, the row and holdings go, the page is empty.

The window is the time between the route's fold read and the store's save, for a machine that has been silent for
over ten minutes and reports again in exactly that gap. When it is hit: a switch the Director may be carrying out is
recorded as failed, a live machine is off the page until its next report, and - the part that does not heal - its
approver records are removed with no removal record, so an OWNER-set approver is lost until the owner sets it again.
Why it should still change: the store's summary says the forget is for a machine that "has stopped reporting", and
the store is the one place that can make that true at the moment of the save, in two lines: refuse with
`forget_not_offered` when the machine row exists, there is no open refusal of it, and
`LicenseStaleness.IsMachineStale(machine.LastReportAtUtc, now)` is false. Refused reports never reach `ApplyReport`
(the ingest returns before line 218), so `LastReportAtUtc` is untouched by a refusal and the check cannot wrongly
refuse a refused machine. The route already maps a store refusal of that code to 409 with the fold's sentence.

## Finding 2 (should fix, not blocking) - the log shows the raw word `machine-forgotten`

`LicenseDisplayFold.ReasonText` has no sentence for `LicenseGatewayReasonCodes.MachineForgotten`, so it answers the
code word itself (the `_ => reasonCode` arm; my test `ReasonText_ForMachineForgotten_IsTheRawCodeWord`). The Cockpit
renders `entry.reasonText` under every log row (`LicensesView.tsx` line 635), so the ended switch reads: Failed;
"machine-a was not switched from A to B."; "Ended without an answer: machine-a was forgotten before this switch was
over."; and then the bare "machine-forgotten". The fold's own rule is that the code word appears only for a reason
"this build does not know"; this build knows it. One line in `ReasonText`, for example "The machine was forgotten
before the switch was over." - or null, since the message already says it.

## Verified (nothing to change)

- The two dead-machine tests are the two shapes of round-one finding 1, and `AMachineThatDiedWithASwitchUnanswered`
  uses the real minute check for three days, not a hand-built row state.
- The round-one request for an alarm assertion is met: both `OnTheNextRaise` tests run the real
  `LicenseMachineHealthAlarm` after the forget and find no open alarm.
- Round-one finding 2's conflict with 4172 is resolved: `LicenseStore` has `EndSilenceForRefusal` and `ForgetMachine`
  side by side, nothing of main lost. The NEW conflict is with 4115: `LicenseDisplayFold.cs` lines 974 to 979 (both
  sides add `kindText` lines - keep both) and `missions/license-switching/cockpit-api.md`. Rebase, keep both, rerun
  the gate.
- Revert proofs in the pull request body are specific and name the failing tests; I did not rerun them.
- No real email, machine name, address or account name in the diff; no attribution strings in the body.
- The sign-in refusal is unchanged at both the fold and the store, as agreed in round one.

---

Recap: the three fixes hold. A gone machine mid-switch can be forgotten, its switch ends as failed, nothing in the
minute check, the engine, the retry or the history is left inconsistent, and a machine that returns with the old switch
id is handled. Approver removal is scoped to that machine, user and tenant, and its alarm closes. The transaction
sentence is corrected everywhere. Two small should-fixes: let the store refuse a machine that reported within the stale
window (closes the only real window), and give `machine-forgotten` a sentence in `ReasonText`. The branch must be
rebased once more (4115) before the merge; both conflicts are trivial. Nothing in either worktree was edited; the
reproduction lives only in my scratchpad.
