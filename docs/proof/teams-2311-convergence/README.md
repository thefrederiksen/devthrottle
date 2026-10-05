# Proof: the seat convergence stops for a team whose subscription has ended

thefrederiksen/devthrottle_internal#2311, Phase 1 review finding 3. Run on 4 October 2026, on the branch rebased
onto origin/main 0f8ae2706.

## What the website answers, exactly

From `syncTeamSeats` in `website/api/_lib/team-billing.js` on the branch `teams/2299-web-billing`
(devthrottle_internal#2315, not merged yet). It refuses with **HTTP 409 and the code `no_active_team_subscription`**
in two places:

- its own row for the team is not billed, or has no subscription reference ("This team has no running bill");
- the payment provider says the subscription is no longer active or past due ("This team's subscription is
  canceled in Stripe").

Both mean the same thing: no sync can succeed until the team's bill row changes. The same route answers other 409
codes (`no_paid_seats`, `subscription_team_mismatch`, `unexpected_subscription_shape`). Those are NOT treated as
ended, and neither is the ended code on any status other than 409. `TeamSeatSyncResult.SubscriptionEnded` is true
only for the exact pair.

## What changed

- `TeamSeatSyncResult.SubscriptionEnded` and `TeamSeatSyncClient.NoActiveSubscriptionCode` (Core).
- `EntitlementRegistry.TeamBillFingerprint`: a one-way hash of the bill row's status, seats, period end,
  subscription reference and when it was written. `ReadTeamBilledSeats` now carries it on `TeamBilledSeats`.
- `TeamSeatSync.ConvergeAsync`: on the ended refusal the team is marked with its fingerprint (in memory) and
  logged once, with its hashed id. While the row's fingerprint is unchanged, later checks answer the new verdict
  `SubscriptionEnded` and call nothing. When the row changes, sync is called once more. Any other result clears the
  mark. A team that comes into step, or loses its bill, is cleared too.
- `TeamSeatConvergence`: the pass summary line counts the teams it did not call for this reason.

No schema change. Inviting, accepting and paid features are untouched.

## Tests

`src/CcDirector.Gateway.UnitTests/Teams/TeamSeatConvergenceEndedSubscriptionTests.cs`, 25 cases, over a real
migrated database with a scripted website:

| The mandate's test | Test |
|---|---|
| Ended refusal: marked, logged once, next passes do not call | `RunOnceAsync_WebsiteSaysSubscriptionEnded_MarksTheTeamLogsOnceAndStopsCalling` |
| Bill row changes: called again exactly once | `RunOnceAsync_EndedTeamsBillRowChanges_CallsSyncExactlyOnceMore` (one case per field) |
| Transient failure retried as today, never marks | `RunOnceAsync_OtherFailure_IsRetriedEveryPassAndNeverMarksTheTeam` (503, 500, 504, another 409, the code on a 400), `RunOnceAsync_WebsiteUnreachable_IsRetriedEveryPassAndNeverMarksTheTeam` |
| Two teams, one ended, one healthy | `RunOnceAsync_OneTeamEndedOneHealthy_TheHealthyTeamConvergesAsBefore` |
| New public methods | `SubscriptionEnded_EachResult_...`, `TeamBillFingerprint_EachFieldThatDescribesTheBill_...`, `ReadTeamBilledSeats_TeamWithABill_...`, `ConvergeAsync_MarkedTeamWithAnUnchangedBill_...`, `ConvergeAsync_MarkedTeamComesIntoStep_...` |

The "logged once" test reads the log through `FileLog.RedirectForTests` and also checks that no raw team id, no
address, no subject and no subscription reference reaches it.

## Revert checks - `revert-checks.txt`

The change was committed first. Each mutation was written into the source, the project rebuilt (no `--no-build`),
the new test class run, and the source restored in a `finally`. All eight went red:

| Mutation | Went red |
|---|---|
| R1 the marked team is no longer skipped | 8 cases: the stop, the bill-change, the two-teams and the verdict tests |
| R2 the ended refusal is never recognised | 9 cases |
| R3 any 409 counts as ended | 5 cases: the "another 409" retry case and the result-property theory |
| R4 every failed call marks the team | 6 cases: every transient-failure case |
| R5 `updated_at` left out of the fingerprint | the `updated_at` bill-change case and the fingerprint test |
| R6 the mark not cleared when the team comes into step | `ConvergeAsync_MarkedTeamComesIntoStep_...` |
| R7 a skipped pass logs the stop again | the logged-once test |
| R8 the reader carries no fingerprint | 10 cases |

## Gates

- `dotnet test src/CcDirector.Gateway.UnitTests` in full, with `MSBUILDDISABLENODEREUSE=1`: **8,575 passed, 0 failed,
  9 skipped** (`gateway-unittests-full.txt`). The known `Wingman.OwnedSessionsAreNotReadTests` defect did not
  appear in the full run.
- `.\scripts\test-local.ps1` (default): **all ten suites outcome=Completed, 3,476 tests, exit 0**
  (`test-local-default.txt`). The first run had one red,
  `RetiredMessagingWordsTests.Nothing_in_the_repository_...`, an `IOException` because that test reads every file
  in the repository and I was writing this gate's own log into `docs/proof` while it ran. Rerun with the log
  written outside the tree: green.
- What did NOT run here: `Gateway.Tests` and `Core.Tests` (parked; the mandate says not to run the long Gateway
  suite on this machine). Neither suite's source names any type this change touches. The Tech Lead starts CI for
  the full run.

## Review round (review-2311-convergence.md, F1 and F2)

Text and the summary count only. F1: the pass summary counts a call refused as ended apart from the retried
failures. F2: the error code's comment says it decides the stop. Revert check: putting the ended refusal back under
the retried failures turns `RunOnceAsync_WebsiteSaysSubscriptionEnded_MarksTheTeamLogsOnceAndStopsCalling` red
(1 of 25). Reruns: the convergence, seat sync and invitation tests 93 passed, 0 failed; `.\scripts\test-local.ps1`
default, all ten suites outcome=Completed, 3,476 tests, exit 0.
