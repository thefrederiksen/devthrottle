# Answers: review of the Mail Desk phase 7 Gateway fold

Developer seat, Mail Desk mission (issue #4213), phase 7. Answers to `gateway-fold-review.md`. 19 October 2026.
Both findings are fixed in the commit that adds this file, on branch `mail-desk-p7-fold`, rebased onto
`origin/mail-desk-phase-7` at `882e4dc5d` (the corrected contract).

## F1. `machinesLabel` promises a failover order that nothing follows - ACCEPTED

The review is right. A channel's machine list says only which machines may read the mailbox. Which one runs the
check, and the order of failover, belong to the Mail Desk start (mission 5.9), so the fold must not claim an order.
The Tech Lead corrected the contract in `882e4dc5d`, and the fold now follows it:

- one machine: its name, `devlinux`;
- two: `devlinux or north-box, whichever runs the Mail Desk check`;
- three or more: `devlinux, north-box or south-box, whichever runs the Mail Desk check`.

Tests: `MachinesLabelOf_TwoMachines_NamesBothWithNoOrder` and `MachinesLabelOf_ThreeMachines_NamesAllWithNoOrder`
replace the two tests that pinned the false sentence. `MachinesLabelOf_SeveralMachines_ClaimsNoFailoverOrder` checks
that no sentence of two or three machines says "then" or "not answering". The route test
`ListChannels_ObserveRowNeverChecked_CarriesEveryNewWordAndTone` checks the new sentence as it reaches a client.

## F2. Any outcome other than `failed` is green - ACCEPTED, with one refinement

The review is right: the green fallback was the kind of fallback repository rule 3 forbids, and it disagreed with
every other tone in the fold, which all refuse a value they do not know. `LastCheckToneOf` now names every outcome
and refuses any other:

| Outcome | Tone | Why |
|---|---|---|
| no check yet | neutral | nothing is known |
| `nothing-to-do`, `started`, `screened` | good | the check read the mailbox and did its work |
| `paused`, `skipped-running` | neutral | nothing failed, but nothing was screened |
| `failed` | bad | the check failed |
| anything else | refused (throws) | an outcome nobody gave a tone must not show green |

The refinement: the review asks for `neutral` on the screening-still-running case of `skipped-running`. The tone is
read off the last check's own record, which carries the outcome but not the reason, so splitting `skipped-running` by
its reason would mean adding the reason to the row. Instead every `skipped-running` is neutral. That is also right for
the one meaning it has on this branch ("new mail, and the seat it started last is still running"): nothing failed,
but nothing new was started.

`screened` is phase 3's new outcome. It is not yet in `TriggerRunOutcome` on this branch, so the fold names it as
`MailDeskChannelFold.ScreenedOutcome = "screened"`; when phase 3 lands it can be replaced by
`TriggerRunOutcome.Screened`. A trial of this commit on `origin/mail-desk-phase-3` is reported in the commit's
hand-back message.

Tests: `LastCheckTone_CheckDidItsWork_IsGood` (`nothing-to-do`, `started`, `screened`),
`LastCheckTone_NothingFailedButNothingScreened_IsNeutral` (`paused`, `skipped-running`),
`LastCheckToneOf_AnOutcomeWithNoTone_Throws` (replaces the case that locked in `"an-outcome-added-later"` as good),
and `LastCheckToneOf_EveryTriggerOutcome_HasATone`, which walks `TriggerRunOutcome.All`. That last test fails the day
someone adds an outcome without a tone, and on phase 3 it covers `screened` as well.

**For the Tech Lead:** the contract, section 2, still says "any other outcome -> good" for `lastCheck`. It needs the
table above.
