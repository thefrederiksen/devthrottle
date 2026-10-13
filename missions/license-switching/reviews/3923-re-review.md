# Pull request 3923 re-review - the Gateway's once-a-minute check, round 2

Reviewed at head `edc543db2`, against round 1 (`3923-minute-check-review.md`, head `f173d4ead`) and the answers
(`3923-answers.md`). The license files were judged as they now stand, after the merge of main. Nothing tracked in the
worktree was edited. Every reproduction and every deliberate break was made in a copy of the commit outside the
repository.

## Verdict

**Approve.** The must fix is fixed, and I could not strand a switch on any path I tried. Every should fix is fixed and
held by a test that goes red when the code is broken. Four new findings: one should fix that needs the Tech Lead's
ruling (the tenants no longer wait for one another inside a check, but one Director that does not answer still costs
every tenant its next minute), one small should fix (three behaviours the answers claim that no committed test
holds), and two notes.

## What I ran

| Run | Result |
|---|---|
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Licenses"` | 201 passed, 0 failed, 0 skipped |
| `dotnet test src/CcDirector.Gateway.Tests --filter "FullyQualifiedName~Licenses"` | 2 passed, 0 failed, 0 skipped |
| `.\scripts\test-database.ps1` | GREEN: 70 executed on the throwaway PostgreSQL, 70 passed, none skipped |
| `.\scripts\test-database.ps1 -Filter "FullyQualifiedName~LicenseStorePostgresTests"` | GREEN: 2 executed, 2 passed, none skipped |
| 19 reproductions of my own, in the copy | 19 passed on the code as it stands (one of them records the gap in new finding 3) |
| The eight breaks the proof names, one at a time, in the copy | each turned exactly one committed test red, the one the proof names |
| Seven further breaks of my own | see new finding 2 |

Not run by me: the whole gate (`test-local.ps1 -Parked`). That run belongs to one seat.

## The round 1 findings

### 1. A check that fails after planning a send strands that switch - FIXED

The mark is now set only after the whole plan stands, still under the tenant's gate
(`LicenseSwitchService.cs:205-208`, `:292`), and cleared by the send itself (`:587`) and again on the way out of the
check and of Switch now (`:230`, `:325`).

My round 1 reproduction is now the committed test `ACheckThatFailsAfterPlanningASend_DoesNotStrandThatSwitch`; it
passes, and it goes red when the mark is set at planning time again.

Other paths I tried, each on the SAME service afterwards, each reading the in-flight table directly and finding it
empty:

- a check cancelled while its send is in flight: the next check asks again with the same identifier and settles the row;
- a check whose send came back but whose result could not be recorded (a failure injected between the send and the
  write): the same;
- Switch now failing after its send, and Switch now cancelled during its send: nothing left marked, a second Switch now
  is refused because the first has no known outcome, and the next minute check finishes the row with the same
  identifier;
- a tenant pass that throws after it has started a check: the started check is still waited for, it settles its row,
  nothing is left marked, and the failure of the pass still reaches the caller.

The two clears overlap: with either one removed every test stays green, with both removed nothing would ever be
cleared. That is belt and braces, not a defect.

### 2. A resend cut off part-way leaves the row saying "never sent" - FIXED

`PlanSendOfWaiting` (`:522-527`) writes the row back to `pending` with no reason before the command goes out, and
`director-offline` is put back when the send returns nothing (`:597-599`; removing that turns seven committed tests
red). The committed test goes red when the write-back is removed.

Re-run with the other way of being cut off: the resend reaches the Director and its result cannot be recorded. The row
reads `pending` with no reason; a new service over the same store, with a different best target by then, refuses a
Switch now for that machine and the minute check sends only the first identifier. One identifier for the machine
throughout.

One consequence to know, not a defect: the write-back happens when the send is PLANNED. If the rest of that plan then
fails, the row reads "outcome not known" although nothing left the Gateway. It is then asked about with the same
identifier once the machine has reported, instead of being replaceable by a changed decision. That errs on the safe
side of contract section 5.4.

### 3. A replayed answer overwrites a newer report when the Director's clock is ahead - FIXED

`LicenseStore.cs:439-452`: for an answer with `Replayed = true` the read-back is taken only when the machine has not
reported since the switch row was written. Both times are the Gateway's. It is part of the one conditional update.

Re-run with the round 1 numbers through the whole check, not only at the store: Director's clock 90 seconds ahead, the
switch ran 30 seconds before the report the Gateway holds, the stored answer is stamped 60 seconds after that report.
The row settles `switched`, `Replayed` is recorded, and the machine still shows the account its report showed. With
the new condition removed that reproduction and the committed store test both go red.

A fresh answer that is NOT replayed is still taken when the Director's clock agrees with the Gateway's, and when it is
90 seconds ahead. It is not taken when the clock is 90 seconds BEHIND - that is new finding 3, and it is not caused by
this change.

### 4. One Director that does not answer holds up every tenant after it - FIXED INSIDE A CHECK, NOT ACROSS CHECKS

Inside one check it is fixed and it is sound:

- **The scope.** The tenant scope is an `AsyncLocal` holding the tenant itself, not a holder object that is emptied on
  dispose (`AsyncLocalTenantContext.cs:25`, `:74`), so what a check captured cannot be changed by the pass moving on. I
  tried to make one tenant's command go out under another's scope with the REAL `TenantPass` and
  `AsyncLocalTenantContext` in hosted mode and could not: (a) tenant one's Director answers only after the whole pass
  has left every scope and the caller is in none - the scope read at the send and again after the wait is tenant one's,
  and tenant two's is tenant two's; (b) tenant one's gate is held while the pass runs, so tenant one's plan and send
  start late, on another thread, after the pass has entered and left tenant two's scope - its send still starts in
  tenant one's scope, and tenant two was not held up by the gate.
  The committed unit test uses a pass whose scope is an ordinary property, so it shows the send starting inside the
  pass, not the scope following a wait; the hosted test covers the real scope on the path where nothing waits. The two
  cases above are the ones neither covers. They pass.
- **A tenant cannot overlap itself.** True in the code (`:192`). No committed test holds it - new finding 2.
- **Deadlock and growth.** A send ends after 60 seconds whatever the Director does (`DirectorCommandRouter.cs:92-115`
  turns every failure of the wait into a result), the owner email after 30, nothing blocks a thread while it waits,
  and the host starts one check at a time. The in-memory tables are keyed by tenant or by switch and stay that small.
- **A skipped minute logs a line.** `GatewayHost.cs:6549`. Read, not tested (it is a log line).

Across checks it is not fixed - new finding 1.

### 5. Three behaviours no test held down - FIXED

Each of the three breaks from round 1 now turns exactly one committed test red:
the resend going to the Director the row was written for (`AResend_GoesToTheDirectorOfTheMachinesLastReport_...`);
nothing ever marked in flight (`TheMinuteCheck_WhileSwitchNowsSendIsInFlight_DoesNotSendThatSwitchAgain`);
settling from a report while the machine says the switch is still running
(`Restart_AfterNotAfter_WhileTheMachineSaysThatSwitchIsStillRunning_IsNotSettledFromItsReport`).

### 6. None of the new store statements had run on PostgreSQL - FIXED

`LicenseStorePostgresTests.cs` goes through the real `LicenseStore` on the throwaway PostgreSQL: the report, the
pending row and the refusal of a second, every `TryMark` move, `RecordSend`, `TrySettle`, the read-back taken and a
replayed one refused, the bar, and the whole life of a no-room event including the conditional email update. I ran the
script: 70 executed, none skipped; and with a filter for this file alone: 2 executed, none skipped. The pinning of
`FromAccountEmail` is recorded as not done, which is what round 1 called it.

### 7. A row settled `failed` from a report written before the switch ran - NOT CHANGED, as ruled

Agreed with the reason given: it should be decided on its own.

### 8. The owner email - the growing wait is right

Ran it for 400 checks with every try refused: tries at minutes 0, 1, 3, 7, 15, 31, 63, 123, 183, 243, 303, 363 - the
wait doubles to 32 minutes and then stays at an hour. A shorter wait asked for by the mail service does not shorten
it. The email is never lost: after five refusals it went at the next due minute, once, and never again. A Gateway
restart while a refused email waits tries once at once and then waits again from one minute. A restart between the
event being opened and its email sends exactly one. An event that ends while its email waits and opens again emails
at once, with a one-minute wait after. Part of this is held only by my runs - new finding 2.

### 9. Logging - done as far as the answer says

`TickAsync` writes no entry line and a result line only when it did something (`:219-221`); `ListOpenNoRoom` is quiet.
The lines that remain on an idle check are the ones the answer lists, in code shared with the Licenses page. Whether
those are made quiet for the check is still the Tech Lead's to rule.

### 10. Rules - as answered

The note for the Licenses route is in `switch-now-method.md`.

## What else I checked

- **One email across a restart, and one tenant never overlapping itself:** above.
- **Tenants:** every store call still takes its tenant; every in-memory table is keyed by tenant; the two real-scope
  runs above.
- **The table in contract section 3.5:** unchanged and still as written - not connected stays `pending` /
  `director-offline` (also after a resend of a waiting row), no answer or a dropped tunnel is `unknown`, a refusal or
  error is `failed` / `director-error`, an answer is its own outcome. The committed `Result_*` tests cover each row.

## New findings

### 1. Should fix, for the Tech Lead to rule - one Director that does not answer still costs EVERY tenant its next minute

`src/CcDirector.Gateway/GatewayHost.cs:6546-6552` and `:6560`; `src/CcDirector.Gateway/Licenses/LicenseSwitchService.cs:167`.

`TickEveryTenantAsync` returns only when every tenant's check has finished, and the host starts no new check until it
has. A send to a Director that does not answer ends after 60 seconds, which is just after the next minute's timer has
fired. So that minute is skipped for every tenant, not only for the one with the silent Director. This is the first
bullet of round 1 finding 4. What changed is that the skipped minute now writes a line.

Why it matters: a tenant that reaches 97% in that minute waits up to a minute longer because of another tenant's
machine. A Director that reports but never answers the switch repeats this on each resend until the switch expires,
about ten minutes.

Reproduced at the service: with tenant one's Director unanswered, the pass is still open after tenant two has finished.
The same run shows the remedy is already in the service: when a second pass IS started meanwhile, tenant one is skipped
by its own guard (`:192`) and tenant two is checked and switched. So the host could start a pass every minute and
leave the overlap to the per-tenant guard.

With one owner in production today this does not bite. The proof's sentence "a Director that does not answer holds up
nobody else" is true inside a check and not across checks; if this is recorded as known rather than fixed, that
sentence should say so.

### 2. Should fix, small - three things the answers claim that no committed test holds

Each line is one break made in the copy; all 201 committed license tests stayed green.

- **A tenant cannot overlap itself** (`LicenseSwitchService.cs:192`, guard removed): 0 red. The guard is what keeps two
  checks of one tenant from both sending the same event's email. A test that holds it: the mail service has not
  answered, a second check of the same tenant runs, and still only one email has been tried. That test passes on the
  code and goes red with the guard removed.
- **The email wait is forgotten when the email goes and when the event ends** (`:690-694`, made to forget nothing):
  0 red. Without it a new event for the same account starts its retries at the old event's long wait.
- **The wait stops growing at an hour** (`:121`, changed to ten hours): 0 red. The committed test stops at minute 31,
  one step before the limit is reached.

### 3. Note, not caused by this change - a fresh answer from a Director whose clock is BEHIND is not taken

`src/CcDirector.Gateway/Licenses/LicenseStore.cs:437-438`.

This is the other half of the band round 1 finding 3 named. A fresh answer (not replayed) is still judged by the
Director's clock, and a skew under two minutes is not corrected. With the Director 90 seconds behind and a report
received 20 seconds before the check, the row settles `switched` but the machine goes on showing the old account.

Reproduced. If no report arrives before the next check, that check then sends the same machine a second switch with a
new identifier, because it still looks to be on the tired account with no open row. A real Director sends a report
straight after every switch, which closes the gap within seconds, and it would refuse the second switch because the
expected active account no longer matches. So the cost is a possible spurious `failed` row, not a wrong switch.

A rule that needs no second clock, in the spirit of the fix for replayed answers: for an answer to a command this
Gateway has just sent, compare the machine's last report with the Gateway's own send time.

### 4. Note, read and not run - the one send after expiry is counted when it is planned, not when it goes

`src/CcDirector.Gateway/Licenses/LicenseSwitchService.cs:405`.

This is the same shape as round 1 finding 1, with a milder end. `_sentAfterExpiry` is set while the plan is being
made. If the rest of the plan fails, or the Director turns out not to be connected, nothing was sent, and the next
check settles the row from the machine's report without the one ask that contract section 5.5 describes. The row is
finished, not stranded, and the report it is settled from was received after the last real send, so the outcome is
most likely right. An entry in that table is also left behind when the after-expiry send ends in a Director error; it
is one small entry per such switch.

## How the reproductions were run

`git archive` of `edc543db2` into this session's scratch folder, built there with the workspace typecheck switched off.
One extra test file was added to the copied license unit tests: the real store on SQLite, a fake Director and a fake
mail service, the real `TenantPass` and `AsyncLocalTenantContext` for the scope runs. Each break was one edit to the
copied source, run against the committed tests and mine together, then restored. The scratch folder is this session's
and will not last; the steps above are enough to write each test again.
