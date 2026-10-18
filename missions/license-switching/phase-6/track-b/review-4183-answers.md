# Answers to the review of pull request 4183

The review is `review-4183.md`, beside this file. It was made at `a2eb54c6f`.

## Finding 1 - a machine that went silent while a switch was recorded on it could never be forgotten: ACCEPTED, fixed

The reviewer is right, and this was the very case the action exists for. A machine that is gone never answers its
switch. The minute check settles an unanswered switch only from a later report, and a switch the machine itself
reported is cleared only by a later accepted report. So the refusal "it can be forgotten once the switch is over"
waited for a day that never comes.

**The fix is the reviewer's.**
- **The fold** no longer refuses because of a switch in progress. Forget is offered for a machine that is
  `report-refused` or `not-reporting`, whatever switch is recorded on it. A machine that is reporting and switching
  is still not offered: its status is `switching`, not one of the two.
- **The store** ends the machine's open switch rows in the same save, as `failed` with the new reason
  `machine-forgotten`, and the message "Ended without an answer: machine-a was forgotten before this switch was
  over." So no open row names the machine row that is gone, and the minute check's throw on such a row cannot happen.
  A late answer changes nothing, because an ended row is not settled again. A switch the machine itself reported goes
  with its machine row.
- **A sign-in in progress still refuses**, at the fold and at the store, as the reviewer agreed: it is only a delay.
- The refusal code `switch_in_progress` is gone from the code and the API document. Only `forget_not_offered` and
  `sign_in_in_progress` remain.

**The tests:**
- `AMachineThatDiedWithASwitchUnanswered_IsOffered_AndForgettingEndsTheSwitchAsFailed` is the reviewer's first
  case. A switch is marked unanswered, and three days of the real minute check leave it open. Then the machine is
  offered and forgotten, the switch is `failed` with `machine-forgotten`, and the minute check runs on afterwards.
- `AMachineThatDiedReportingASwitchInProgress_IsOffered_AndForgotten` is the second case.

With the fold's switch refusal put back, both tests fail and the other fourteen pass.

## Finding 2 - the branch was behind origin/main and conflicted: settled before this review was answered

It was rebased onto `8994f5e1a`, which has 4172, and the one conflict in `LicenseStore.cs` was resolved by keeping
both new methods, with nothing of main removed. The reviewer's request is also a test now: after a forget, the next
raise of the real `LicenseMachineHealthAlarm` leaves no alarm open.
- `ForgettingASilentMachine_ClosesItsAlarm_OnTheNextRaise`
- `ForgettingARefusedMachineThatNeverReportedWell_ClosesItsAlarm_OnTheNextRaise`

## Finding 3 - the approver record outlived the machine, and its alarm did not close: ACCEPTED, fixed

This takes the Tech Lead's preference: the fix, for an approver on THAT machine.
- **What is removed.** Every approver whose machine and user are the forgotten machine's is removed, by the new
  `LicenseApproverStore.RemoveForMachine`. An approver on another machine stays.
- **The sentence.** The `forgotten` row and the answer say which accounts it approved: "It approved the sign-ins of
  alice@example.com; that approver is removed too."
- **The alarm.** With the record gone, its account's approver alarm closes on the next check.
- **No removal record is left**, unlike the owner's own removal. The owner did not reject that approver; the machine
  left. So a machine that comes back registers its approver again on its next check.

The approvers live in the account's settings, apart from the store's tables, so the two writes cannot be one save.
The route reads the accounts first, the store writes its row naming them, and the approvers are removed after the
store's save. When the store refuses, they stay. When the settings write fails after the store's save, the request
fails and the approver stays; the owner can remove it by hand.

**The test** is `AMachineThatApprovesSignIns_TakesItsApproversWithIt_AndSaysSo`. It checks the answer's sentence,
that only the approver on that machine goes, and that no removal record is left. Skipping the removal fails it, and
the other fifteen pass.

## Finding 4 - there is no transaction around the store's second check: ACCEPTED, the sentence is corrected

The reviewer is right: it is a read and then one `SaveChanges`. The proof, the pull request body and the store's own
comment now say "in one save", and they say what can slip in between: a sign-in started between the read and the
save stays open in the log on a machine that is gone, and the sign-in check never settles it. The API document says
the same.

I did not close the window, because the harm is bounded as the reviewer describes it. If the Tech Lead wants it
closed, the route can take the switch service's tenant gate.
