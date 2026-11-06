# Review: the wake costs less in production (commit 17f82aed9, pull request 4601)

Reviewed 2026-11-06 by a separate review session (Factory Inbox - Reviewer - the wake cost changes). Branch
factory-inbox/wake, worktree D:/ReposFred/devthrottle-fi-wake, head 17f82aed9. The worktree was not edited, built
or run. The rest of the branch was reviewed before (phase-2-wake-review.md) and is not re-reviewed here.

## Verdict: APPROVE - no finding

Each of the three changes keeps every wake guarantee. Moving the look off the request loses no exception, no log
line and no tenant scope. The new path cannot race the sweep into a double start, because nothing about how two
looks are kept apart changed. The tests watch the caller through the real stores and the wake's own entry points.
Five observations follow, none of which blocks the merge.

## Scope

What I read, all at 17f82aed9 (git show, never the working tree):

- The commit: src/CcDirector.Gateway/Factory/Inbox/FactoryInboxWake.cs, GatewayFactoryInboxWakeEnvironment.cs,
  GatewayHost.cs (the sweep, lines 1072 to 1100, and the wake's wiring, lines 4525 to 4557), and the four test
  files it touches.
- Around the commit: the whole wake class; FactoryAgentsSwitch.cs (Answer, AnswerQuietly, Decide, Decision);
  FactoryAgentsGate.cs and the mapping of the inbox routes under it; FactoryInboxEndpoints.cs (the send route and
  the bundle route); FactoryInboxWakeStore.cs (Facts and FactoriesWithWork); the inbox migration's indexes;
  AsyncLocalTenantContext.cs and HostedTenantBoundary.cs; MISSION.md section 5.
- A search of every store the look reaches for a read of the ambient tenant: none reads it (details below).

What I ran, out of tree (git archive of 17f82aed9 into the session scratchpad; nothing in the worktree):

| run | result |
|---|---|
| dotnet test, Gateway.UnitTests, filter CcDirector.Gateway.Tests.Factory.Inbox, on the commit | 277 passed, 0 failed, 24 seconds |
| the same four new or changed tests with BOTH product hunks of FactoryInboxWake.cs reverted in the scratch copy | 3 failed, 1 passed (see "The tests") |

What I could not reach: the hosted plan read inside AnswerQuietly (TenantAiGate.ForTenantOnATimer) was not run;
the production slowdown that prompted the commit was not measured; GatewayHost's wiring of the switch into the
environment has no test and was checked by reading only.

## The guarantees, change by change

### 1. The switch is asked quietly (AnswerQuietly through the environment)

AnswerQuietly and Answer both call Decide, which calls the one DecideUnlogged; the only difference is the
decision line. The answer is the same object, so an Unknown plan read still answers "not on" and the sweep still
wakes nobody for that account, exactly as before. The environment test proves it over the real switch and a
throwaway settings store, with the log seam live: no line from the quiet ask, a line from the logged ask of the
same question. A plan read that fails is still logged by the gate, by the switch's own documented design.

### 2. The switch is read only for an account with inbox work

Before: the host asked IsOn for every account on every pass, then called the wake. After: the wake reads
FactoriesWithWork first and returns on an empty list; with work it asks the switch once and returns on "not on"
before any look. The ordering change cannot wake anything the old code would not have woken: the only way to a
look is still work AND the area on. An account with the area off and work waiting is skipped as before, now
with one log line per pass (observation 1).

FactoriesWithWork is two queries, both by the tenant index (IX_factory_inbox_messages_tenant_id_BundleId covers
the BundleId-is-null filter; the bundles query uses the tenant prefix of the bundles index and scans the few
open bundles). "Two indexed reads" is a fair description.

The read of the switch is now inside the sweep's per-account try. A switch that throws (a decision row that does
not parse, which the switch deliberately treats as an error) is now logged and skipped for that account, and the
pass goes on to the next account. Before the commit, that throw happened inside the lambda the host hands to
ForEachTenantAsync, so it ended the whole pass for every later account. That is an improvement, not a loss.

### 3. A handed bundle's look runs on the thread pool

What changed: BundleHanded was LookLaterAsync with a due time of now, which runs LookSafeAsync synchronously on the
caller's thread until the first real await (the semaphore is free, the factory and the facts are read, the Boss
is planned, the claim is written, the start is sent). Now it is Task.Run of LookSafeAsync. The route already
discarded the task (the `_ =` on the wiring line in GatewayHost), so the route's contract is unchanged.

One Secretary per gather window, never a second, a live Boss never doubled: every look, from the heartbeat, a
scheduled look or a handed bundle, goes through LookAsync, which takes the per-factory semaphore
(keyed tenant/factory on the one wake instance) and then takes the start's claim as a conditional update on the
wake row's version. The thread the look starts on is not part of either mechanism. Where a pool look and a sweep
look meet, the second waits on the semaphore, re-reads the row and the roster, and finds either a live session
(rung or already told) or a claim inside the start grace (Starting). That is the same path the old request-thread
look and the sweep took when they met, so no new double-start race exists.

A ring never typed while the session works or holds the owner's draft: RingAsync and the Director's check are
untouched; the look's thread does not reach them.

A restart loses no message: the look runs on the Gateway's own lifetime token (the wake's constructor), never the
request's; a look cut by shutdown is caught in LookSafeAsync and the next Gateway's heartbeat reads the window
from the store. Unchanged.

The bundle is committed before the look is queued: the route calls HandBundle inside Refusals and only then
BundleHanded, so a pool look never reads a bundle the request has not yet written.

### Exceptions, log lines, tenant scope off the request

- Exceptions: LookSafeAsync's whole body is one try that catches Exception (including a cancellation of the
  semaphore wait) and logs it. Nothing runs inside Task.Run before that try, so no exception is lost and no task
  faults unobserved.
- Log lines: the BundleHanded line is written on the request thread before the Task.Run, and every line inside the
  look is written by the look itself. None is lost.
- Tenant scope: every store the look reaches takes the tenant explicitly and opens its context with
  GatewayDatabase.CreateContext(tenant), which the class documents as "never the ambient one". The two
  delegates that need a scope enter their own: the start delegate wraps the spawn in EnterScope(tenant), and the
  ring is the Fleet Manager's SendPromptAsync, built with enterTenantScope. No class in the look's chain reads
  ITenantContext.Current (the ambient readers in the Gateway are the nine sweeps and the host, none of them on
  this path). And even where it would matter, Task.Run captures the execution context at the call, so the
  request's AsyncLocal tenant flows into the look; the request's later dispose of its scope restores the value on
  the request's own context, not on the captured one.

## The tests

Each test writes through the real FactoryInboxStore (SendAsync, Receive, HandBundle) into a throwaway database and
calls the wake's own public entry points (SweepAsync, BundleHanded), with the fake only at the Gateway seam the
wake is designed around. No test hand-builds a wake row or a facts object. The route itself is not driven, and
does not need to be: the claim is about BundleHanded, and the route's one line was read.

The revert proof (both product hunks reverted in the scratch copy, tests unchanged):

| test | on the commit | reverted | why |
|---|---|---|---|
| BundleHanded_ReturnsBeforeTheLookReadsAnything_AndTheLookStartsTheBossOffTheCallersThread | pass | FAIL after 36 seconds, Assert.False(look.IsCompleted) | the look ran on the test's thread, hit the fake's 30-second bound and returned a completed task |
| TheHeartbeat_OverInboxWork_WithTheFactoryAgentsAreaOff_AsksOnce_AndWakesNobody | pass | FAIL, asked 0 not 1 | the old sweep never asked the switch |
| TheHeartbeat_LooksAtEveryFactoryWithSomethingWaiting (the added assertion) | pass | FAIL, asked 0 not 1 | same |
| TheHeartbeat_OverAnEmptyInbox_NeverAsksTheFactoryAgentsSwitch | pass | pass | its pass condition is an absence; its pair above proves the order |

The fourth row is not a defect: the empty-inbox test and the area-off test together prove "zero asks over an
empty inbox, one ask over work", which is the ordering claim. On its own the empty-inbox test cannot tell the new
order from a wake that never asks at all.

## Observations (none blocking)

1. A new repeated line. An account with inbox work and the area off now writes "has inbox work in N factories,
   but its Factory Agents area is off" every 15 seconds, for as long as the work waits; and it waits for ever,
   because nothing wakes while the area is off. The state is reachable only when the area goes off after messages
   were sent (the inbox routes sit under the factory gate, which answers 404 when it is off), so it is rare, but
   when it happens it is 5,760 lines a day for that account: the same class of cost this commit set out to remove.
   Writing it once per change of state would close it.
2. The sweep's catch wording. When the switch throws, the line reads "FAILED to read the work of", which names the
   wrong read. A cosmetic point.
3. The fake's 30-second bound is a real-clock wait, but only on the failing path: a passing run never waits, so the
   clock never decides a pass. The proof above is the worked example (36 seconds to fail).
4. The host wiring is untested: nothing proves GatewayHost hands the environment the Gateway's own switch rather
   than another one. The constructor refuses null, and the single FactoryAgentsSwitch property is what is passed;
   read, not run.
5. Thread-pool starvation (the very slowdown that prompted this) now delays the Boss's start after a handed bundle
   instead of delaying the Secretary's response. Nothing is lost: the heartbeat looks within 15 seconds either way.
   Worth knowing when reading a log where a Boss started some seconds after the bundle.

## Answers (the Developer, 6 November 2026)

No code changes in this commit, so the parked run on 17f82aed9 still holds.

1. Accepted as later work. The line should be written once when an account goes into "inbox work, area off", not on every pass. The fix is a small per-account memory in the wake, with a test that two passes in that state write one line. It is rare because the inbox routes answer 404 while the area is off, and it does not block the merge.
2. Accepted as later work. The catch around the inbox read and the switch read should say "FAILED to read the work or the Factory Agents switch of", or catch the switch read separately. Only the log text changes. It goes in with observation 1, since both touch the same lines of SweepAsync.
3. Declined. The 30-second bound only matters when the test fails: a run that passes never waits on the clock, and without the bound a broken change would hang the suite instead of failing it. The review's revert proof shows it failing in 36 seconds.
4. Accepted as later work. A host-level test could show that the environment is given the Gateway's own FactoryAgentsSwitch, but the wiring is done in GatewayHost.StartAsync, so testing it needs a started host, and that test belongs in the parked Gateway.Tests suite. Until then the constructor refuses null and only one switch exists to pass in.
5. Declined, no change needed. This is the intended trade: the hand request answers at once, and under thread-pool starvation the Boss starts later, but the heartbeat still looks within 15 seconds, so nothing is lost. It is worth knowing when a log shows the Boss starting some seconds after the bundle, and the BundleHanded line ("looking on the thread pool") already marks the moment it was queued.
