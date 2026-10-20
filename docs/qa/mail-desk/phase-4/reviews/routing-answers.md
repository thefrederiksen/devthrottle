# Mail Desk phase 4, task B (live routing to seats): answers to the review

Developer: session "Mail Desk - Developer - live routing to seats", 19 October 2026. Answers
`routing-review.md` (pull request 4275). Both findings are accepted and fixed.

## Finding 1. A session the Gateway cannot see is taken for an ended one

**Accepted, fixed as the Tech Lead ruled** (contract section 3.2 step 3 and the busy row in section 1, corrected on
`origin/mail-desk-phase-4` in `2d0862e58`).

- `MailDeskRouter.IsAlive` (used by the orphan step and by the busy rule) counts a session as ended ONLY on evidence:
  a session row the Gateway holds with `FleetManagerSessions.IsGone` true. A session with no row counts as running
  until `MailDeskRouter.UnseenSessionEndsAfter` (24 hours) after its start; only then is it ended by absence, so an
  email whose Director died with its session is still never lost. Reaching that point is logged.
- Until then no re-route is made and the seat stays busy, so the same emails are never given to a second session
  while the first may still be working after a Gateway restart or a backup takeover.
- The start grace still governs what 3.4 says it governs: a start whose outcome is unknown (settled by name, or
  counted failed once the grace is over).

**Tests**, in `MailDeskRouterTests`:

- `RouteAsync_SessionUnseenAfterARestart_IsNeitherReroutedNorFreedBefore24Hours` at 6 minutes and at 23 hours: after
  a simulated restart (`MailDeskRoutingRig.RestartGateway`: a new router over the same database and an empty session
  list), the held email keeps its session with no re-route, and a new email for the seat waits with no second start.
- `RouteAsync_SessionUnseenFor24Hours_CountsAsEnded_IsReroutedAndTheSeatIsFree`: at 24 hours the email is re-routed
  once, and one new session takes it together with the waiting one.
- The old test `RouteAsync_SessionNotYetReportedInsideTheGrace_KeepsTheSeatBusy`, which proved the rule now reversed,
  is replaced by these.

**Proved to fail without the fix:** with `IsAlive` put back to the start grace, both cases of the first test fail
(6 minutes and 1,380 minutes); every other router test passes.

## Finding 2. No test proves the seat is busy for new emails while an unknown-outcome start is inside the grace

**Accepted, fixed.** New test `RouteAsync_NewEmailWhileAStartOfItsSeatHasAnUnknownOutcome_WaitsUntilTheGraceIsOver`:
the start of `qa-books/clerk` for one email has an unknown outcome; a second email for the same seat arrives; one
second before the grace ends, nothing is started and the new email waits; at the end of the grace the unknown start
counts as failed and one session takes both emails.

**Proved to fail without the clause:** with the first clause of `IsSeatBusy` removed, this test fails and the other
30 router tests pass. The clause is restored (checked byte for byte against the kept copy).

## Noted items

- **A start counted failed after the grace whose session appears later:** unchanged; the contract keeps the trigger's
  rule there (3.2 step 2).
- **A seat session waiting on a person holds its seat's new emails:** unchanged, as the busy rule says; a product
  question for the Tech Lead if the owner should be told.
- **`BeginStart` naming fewer rows than asked:** unchanged. As the review found, no writer can change a queued row
  between the read and that write today.
- **The rig's lookup by name ignores the origin surface:** unchanged; the router sets `trigger`, which the production
  lookup requires, and a test pins it.

## Runs

```
dotnet test src/CcDirector.Gateway.UnitTests -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~MailDesk|FullyQualifiedName~Trigger|FullyQualifiedName~Origin|FullyQualifiedName~Migration"
Passed!  - Failed:     0, Passed:  1027, Skipped:     0, Total:  1027
```

No schema change in this round, so the database run of the first round stands (`MailDeskRoutedPostgresTests`
passed).
