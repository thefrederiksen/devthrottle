# Pull request 3948 re-review - the owner email for a sign-in that cannot be renewed

Reviewed at `04281f832` (main merged in). The answers are `3948-answers.md`. 13 October 2026.

## Verdict: changes needed

Every round 1 finding is settled as ruled, and my three red reproductions are green. One new must fix, made by the
fix for finding 1: a sign-in whose end moves LATER BY SECONDS is taken for a newer sign-in, the streak is ended and
the owner is emailed again. The license tool moves the end by seconds on every key refresh. Reproduced out of tree.

3 new findings: 1 must fix, 2 notes.

## The round 1 findings

| # | Round 1 | Now |
|---|---|---|
| 1 | A Director restart ended the streak (must fix) | **Settled**, but see new finding 1. A report that only lacks the failure time ends nothing (`LicenseRenewalEmailService.cs:146-157`, `:276-278`). Both of my reproductions are green. |
| 2 | The email did not say how old the facts are | **Settled.** The email says when the last try failed and when the machine last reported (`:303-310`); the row says the same as moments (`:338-344`). My week-old reproduction is green. |
| 3 | The machine's sentence went in as sent | **Settled.** The Gateway's sentence for a known reason; the machine's only when none is known, on one line, cut to 200 characters (`:314-320`, `:365-380`). The machine's name is one line in the subject (`:384-387`). A link in the sentence is still a link, as the answers say. |
| 4 | No test for two checks at once | **Settled.** With the guard made to never refuse, `ASecondCheckOfOneAccount_WhileTheFirstIsWaitingForTheMailService_SendsNothing` goes red. |
| 5 | The table's description did not know the kind | **Settled.** `LicenseLogKinds.RenewalFailed`, the entity summary and the comments. |
| 6 | The no-machines guard cannot be seen by a test | **Settled**: said as held by reading only. |
| 7 | A row that stays unmarked, for both emails | **Settled.** See "Breaks" below: all four breaks go red. |
| 8 | "Does not email again" without its exception | **Settled** in the class comment (`:39-41`), the contract and the proof. |
| 9 | A sign-in that leaves and comes back | As built, as ruled. Still two emails (reproduced again). |
| 10 | The renewal check waited for the switch pass | **Settled.** The two run side by side (`GatewayHost.cs:6654-6693`). The account scope is ambient to each flow, so two passes at once do not share it. |
| 11 | Many emails from one cause | For the Architect, no change. |
| 12 | Small things | **Settled**, except the collation, left as ruled. |

## New findings

### 1. MUST FIX - an end that moves by seconds ends the streak, and the owner is emailed again

`src/CcDirector.Gateway/Licenses/LicenseRenewalEmailService.cs:276-278` (`IsNewerSignIn`: `ends > endedBefore`).

What is wrong. Any later end, by any amount, counts as a newer sign-in. One sign-in's reported end is not one fixed
moment. The license tool says so itself:

- `tools/cc-devthrottle/src/license_usage.py:624-627` - on every key refresh the end is written again as
  "the moment of the answer + the seconds the provider says are left".
- `tools/cc-devthrottle/src/license_store.py:73-75` - "each refresh recomputes it from now + seconds left, so two
  readings of one sign-in differ by seconds", and the tool allows ONE HOUR when it asks whether two ends are the
  same sign-in (`SIGN_IN_MATCH_TOLERANCE_SECONDS`).

A key is refreshed many times a day: spares by the tool, the active one by Claude Code. Each refresh that lands a
second later than the end the row holds closes the row with "now reports a newer sign-in" (false), and because the
renewal is still failing inside the warning time the same check opens a new row and sends a new email. The new row
holds the new end, so the next refresh can do it again.

Reproduced (scratch project, the real store, this head's code):

    report: A failing, signed in S, ends E        -> check: 1 email, row open
    report: A failing, signed in S, ends E + 2s   -> check: row ENDED "the-mac now reports a newer sign-in.",
                                                      a second row, a second email
    Assert.Single(sent) -> FAILED: The collection contained 2 items

Why it matters. It is the round 1 must fix again by another road, and a more travelled one: no restart is needed,
only the ordinary refresh of a key, for each failing sign-in, for the three days the warning lasts.

A direction, not a fix: "later" means later by more than the same sign-in can differ by. The tool's own measure is
one hour; a real renewal moves the end by weeks. The sign-in time has no such drift (it is read from the tool's
record), so that half of the rule can stay as it is. No test of the pull request moves an end by a small amount.

### 2. NOTE - what a report without a failure time now leaves standing

`LicenseRenewalEmailService.cs:146-157`, `:173`. Said in the answers; recorded here as seen.

- A send that had failed is not tried again while the machine reports no failure time, because only failing
  holdings are walked. If that machine never reports the failure again, the owner is never emailed for that row.
- The row stays open and "Failing" while the Licenses page shows the same sign-in with no failed renewal.
- A renewal that worked, with the same end, on a machine that never said when it signed in, cannot be told from a
  restart: it ends nothing, and a failure after it is not a new streak (reproduced: one email, one open row). A
  real renewal moves the end, so this is the rule's edge, not a path I could reach with the tool as it is.

### 3. NOTE - the try and catch inside the two services

`LicenseRenewalEmailService.cs:210-219`, `LicenseSwitchService.cs:717-726`. Around the one call to the sender, and
it lets a cancellation through. This is against rule 4 of CLAUDE.md by ruling, as the answers say. Nothing else is
caught there.

## What I tried and could not break

- **Two emails for one streak.** A Director restart, and a restart followed by the same failure: one email. The key
  holder moving is the same report shape: one email. Only new finding 1 got a second email.
- **A real new streak is not swallowed.** Renewed (made later, ends later) and then failing again: the first row
  ends, a second row opens, two emails. The same when the renewal and the new failure both happen between two
  checks, so the Gateway never reads the renewed-and-fine report: still a new streak.
- **The memory of a confirmed email.** It is keyed by account and row. With updates refused for two accounts at
  once, each account's email went out once, the second account was not held back by the first's memory, both rows
  were marked when the database took writes again, and the memory was then empty. It holds at most one entry for
  each open row; a row that ends drops its entry (`:154`). For "no account has room" it is keyed by account and
  email and dropped when the event closes (`LicenseSwitchService.cs:513`) or is marked.
- **Two passes of one account.** The renewal check and the switch pass now run together, but each service still
  refuses a second check of its own for an account, and the two write rows of different kinds.

## Breaks, made by me out of tree

The Developer has run no revert proof of this round. I compiled changed copies of the two services in place of the
real ones and ran the pull request's own `LicenseRenewalEmailServiceTests` and `LicenseSwitchServiceTests` (105
tests; all green on the unchanged copy).

| The break | Red |
|---|---|
| A streak ends whenever the holding is not failing (round 1 behaviour) | 3, among them `ADirectorThatRestarts_AndReportsTheSameSignInWithNoFailureTime_EndsNothing_AndTheOwnerIsEmailedOnce` |
| A confirmed renewal email whose row is not marked is forgotten | 1: `AnEmailTheMailServiceConfirmed_IsNeverSentAgainByThisGateway_WhileTheDatabaseRefusesToMarkItsRow` |
| A renewal sender that throws is thrown on | 1: `AMailSenderThatThrows_IsAFailedSend_TriedAgainLaterEachTime_AndItsMessageIsNotWrittenDown` |
| The one-check-at-a-time guard never refuses | 1: `ASecondCheckOfOneAccount_WhileTheFirstIsWaitingForTheMailService_SendsNothing` |
| A confirmed "no account has room" email is not remembered | 1: `NoTarget_AnEmailTheMailServiceConfirmed_IsNeverSentAgainByThisGateway_WhileTheDatabaseRefusesToMarkItsRow` |
| A "no account has room" sender that throws is thrown on | 1: `NoTarget_AMailSenderThatThrows_IsAFailedSend_TriedAgainLaterEachTime_NotEveryMinute` |

## The runs

| Run | Result |
|---|---|
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Licenses"` | 763 passed, 0 failed |
| `dotnet test src/CcDirector.Gateway.Tests --filter "FullyQualifiedName~License"` | 42 passed, 0 failed |

Not run by me: `.\scripts\test-database.ps1`, `.\scripts\test-local.ps1` with or without `-Parked`, and the pending
model change check (the entity file changed in comments only). The worktree was not edited apart from this file.
