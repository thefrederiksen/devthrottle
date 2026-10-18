# Answers to the round-two review of pull request 4183

The review is `review-4183-round-2.md`, beside this file, made at `457a3adfc`. It found that the three round-one fixes
hold, and asked for two small fixes and a rebase. The Tech Lead asked for both fixes.

## Finding 1 - the store would end a switch on a machine that had just reported: ACCEPTED, fixed

The reviewer is right. The route's fold read was the only guard, so a machine that reported again between that read
and the store's save would have had its switch recorded as failed, its row removed and its approvers taken. The last
of those does not heal.

**The fix** is in `LicenseStore.ForgetMachine`, before anything is written. A machine with a machine row, no open
refusal, and a last accepted report that is not stale is refused with `forget_not_offered`, and nothing is changed.
"Not stale" uses `LicenseStaleness.IsMachineStale`, the read model's own rule, so the page and the store cannot
disagree. A refused report never reaches `ApplyReport`, so it never moves the last accepted report, and a refused
machine is never refused for this. The route already turns that code into `409` with the fold's sentence.

**The test** is `TheStoreRefusesAMachineThatReportedAgain_AfterThePageWasRead_AndChangesNothing`. It calls the store
on a machine with an unanswered switch and an approver that reported again after going silent. The store refuses,
the switch is still open, and the machine and the approver stay. Without the check, it fails.

## Finding 2 - the log showed the raw word `machine-forgotten`: ACCEPTED, fixed

`LicenseDisplayFold.ReasonText` now has a sentence for it: "The machine was forgotten, so this switch can never be
answered." It is not a repeat of the row's message, which says when the switch ended. The unanswered-switch test now
checks that sentence on the ended row's log entry. Without the line, it fails. The API document gives the sentence.

## The rebase

The branch was rebased onto origin/main `58e563ba0`, which is past the `30556737f` the Tech Lead named. The two
conflicts were two lists that had each gained an item, and both sides are kept:
- `LicenseDisplayFold` kind texts: `cannot-switch`, `can-switch-again` and `forgotten`.
- The API document's row of log kinds: the same three.

## Not changed, as the review recommends

- **When the settings write fails after the store's save,** the `forgotten` row says an approver was removed while it
  was not.
- **An approver registered between the read of the accounts and the removal** is removed but not named.

Both are bounded and disclosed, and the reviewer asks for no change.
