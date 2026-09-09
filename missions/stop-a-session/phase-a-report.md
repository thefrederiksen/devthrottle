# Phase A report - Seats 1 and 2, the engine and the command line

Written for the Architect by the Phase A Manager (session `ee59e5d0`). Branch
`mission/stop-a-session`. **Nothing was merged to main** - that is the Architect's, and it is the
only "done".

---

## What now exists

`cc-devthrottle session stop <session> --reason "why"` ends a real session and prints an answer that
says what actually happened to it. `cc-devthrottle session done --undo` takes a deletion flag back
off. All seven things the handoff asked for are built.

| # | Thing | Where | Commit |
|---|---|---|---|
| 1 | The Director's honest answer | `SessionCommandExecutor.KillAsync` -> `DirectorStopResult` | `48f8e98e` |
| 2 | `POST /sessions/{sid}/stop`, and `DELETE` as a thin forward | `GatewayEndpoints.cs`, `SessionStopFold.cs` | `91b79b1e` |
| 3 | The refusal - 400, naming the reason, naming no flag | `SessionStopFold.ReasonMissing` | `91b79b1e` |
| 4 | `notOnFleet` is a 200 success | `StopSessionAsync` | `91b79b1e` |
| 5 | The allow list: `stop` in, cancel-deletion in, bare `DELETE` still refused | `SessionKeyGuard.cs` | `91b79b1e` |
| 6 | The audit record - a new `stopped` intervention type, actor required | `GovernanceAuditEventDtos.cs`, `GovernanceAuditLog.cs` | `91b79b1e` |
| 7 | The two commands | `session_ops.py`, `cli.py` | `5e16767a` |

The shared answer shapes were written FIRST (`SessionStopDtos.cs`, `071e549f`) so three seats built
against one interface instead of discovering three versions of it.

---

## What is proven, and how

**71 new tests. Every one was watched failing on purpose** - the change reverted, the test watched
going red with the symptom it claims to catch, then restored. Worker A did twelve reversions,
Worker B three rounds covering ten, six and four failures, Worker C sixteen. Each Worker recorded
what the red actually SAID, not just that something went red; those tables are in
`worker-a-notes.md`, `worker-b-notes.md` and `worker-c-notes.md`.

Two reds worth quoting, because they are the mission's own defects reproduced:

    A_typed_name_is_never_truncated
      expected: Stop a session - Worker      actual: Stop a s

    A_session_that_is_not_on_this_fleet_is_a_success_not_a_404
      expected: OK                           actual: NotFound

The second is precisely the failure Ruling 3 exists to prevent - a second stop returning an error an
operator reads as "it is still alive".

**Test runs, all by me, after the Workers had gone:**

| Run | Result |
|---|---|
| `.\scripts\test-local.ps1` | 8 of 9 suites green. One suite OVER BUDGET - see below. Not a test failure. |
| `Gateway.UnitTests` alone, to completion | **4221 tests, 0 failures**, 2 m 36 s |
| `.\scripts\test-local.ps1 -Parked` | PARKED_RESULT |
| `python -m pytest tools/test_shipped_tools_contract.py` | 36 passed (this is the ASCII-only guard) |
| `cc-devthrottle` suite, `FORCE_COLOR=1` | **261 passed**, 2 failed - both pre-existing, proved below |
| `cc_shared` suite | 126 passed, 1 failed - pre-existing, and this branch does not touch `cc_shared` |

**One check that was mine rather than any Worker's.** Worker C's Python tests stub every Gateway
answer from the written contract, and Worker B's Gateway tests stub the Director's answer. Two green
suites that have never met each other are exactly the shape of proof that covers the wrong thing. So
I diffed the fold's literal sentences against the Python fixtures, character for character:

| Sentence | Agrees |
|---|---|
| `stopped {id} - process {pid} ended, row removed` | yes |
| `already stopped {id} - no process was running; the row it left behind has been cleared` | yes |
| `not on this fleet - nothing in this account carries the id {id}, so no machine was asked and no machine's processes were searched` | yes |
| `the worktree {path} was left untouched - it has uncommitted changes in it` | yes |
| `reason: {reason}` | yes |

That closes drift on the strings. It closes nothing else.

---

## THE BIG GAP: the three layers have never met

**There is no end-to-end run. Nothing here has stopped a real session.**

- Worker A's tests inject process liveness as a delegate. **No real process was ever killed**, and
  the production `Process.GetProcessById` check has no test at all.
- Worker A's worktree probe is injected too. **No real git was ever run.**
- Worker B's route tests stub the Director's answer. They prove what the Gateway does WITH an answer
  and nothing about whether that answer is true.
- Worker C's command tests stub the Gateway's answer.

The string diff above is the only place two of the three layers were compared, and it compares only
words. Status codes, the tunnel, the audit write and the real kill are all unproven end to end.

I did not close this, deliberately, and the Architect should agree or disagree:

- this session's Gateway does not carry these changes, so `session stop` from here would just 404;
- deploying the hosted Gateway is the deploy skill's alone and is not a Manager's call;
- the run that proves it IS the mission's own QA report (phase 5), which the state note says is
  written by a seat that did not build the feature. A builder photographing his own work reaches for
  the path he already knows works.

**So: do not let "the suites are green" stand in for this.** Until a real session is stopped, the
live-process path is reasoned, not demonstrated.

---

## The local gate is RED, and it is not this mission. Measured, not argued.

`.\scripts\test-local.ps1` fails for exactly one reason: `CcDirector.Gateway.UnitTests` exceeds the
120-second ceiling, so the gate stops it and records no result. The script says in its own output
that this is NOT a test failure.

Both Workers claimed it was not theirs. I did not take that on trust; I measured it on a clean
extract of `origin/main` with none of this branch's changes:

| Tree | Gateway.UnitTests |
|---|---|
| clean `origin/main` (`6710a86c`) | 4182 tests, **1 failed**, **2 m 44 s** |
| this branch | 4221 tests, **0 failed**, **2 m 36 s** |

The breach predates the branch by 44 seconds. This branch adds 39 tests and the suite got no slower.
Worker B measured the same thing a second way - excluding every mission test, the suite still took
3 m 16 s - and its 26 fold tests account for 0.136 seconds of 704 seconds of summed test time.

The single failure in the clean baseline (`SessionHistoryStoreTests.Two_tenants_never_see_each_others_history`,
an EF/SQLite connection fault) did not reproduce on this branch. Two other failures the Workers saw
also did not reproduce once the machine was quiet: the `setup-engine` environment-variable race
(541 of 541 twice) and a `SessionCommandExecutorTests` case that was red only because another Worker
was editing the file while the gate read it. Three Workers building at once in one worktree is a real
source of noise, and it is worth knowing that the suites disagree with themselves under that load.

**The parked-suite coverage warning fired**, naming `Core.Tests` and `Gateway.Tests` as suites this
change touches. That is why `-Parked` was run.

---

## Decisions taken that the Architect should confirm or reverse

### 1. The required reason lives on the POST door, not inside the shared handler. (Mine.)

The handoff says `DELETE /sessions/{sid}` becomes "a thin forward into the same handler", and
separately that a stop with no reason is refused 400. Taken together literally, DELETE - which has no
body - would always be refused, breaking a shipped native phone client in the middle of the mission.

I resolved it on the state note's own emphasised sentence: *"an agent's key can only ever stop a
session with a reason attached."* That only makes sense if the DELETE door can stop without one. So
the reason check is on the POST route, the shared handler takes an optional reason, and the allow
list keeps session keys off DELETE entirely. The owner's invariant holds exactly. This is the one
place I read past the letter of the handoff.

### 2. An answer this Gateway cannot fold honestly is REFUSED (502). (Worker B's. I recommend revisiting.)

The Gateway and the Directors do not deploy together. During a rollout, a Gateway carrying this
mission can be handed an answer from an older Director that reports only `killed`/`removed`. There is
no honest headline for that, so Worker B refuses it with a 502 naming the old Director.

**The cost is real and it is not small:** in that window a stop through EITHER door fails for a
session on a machine that has not updated - including `DELETE /sessions/{sid}`, which shipped clients
call and which succeeds against such a Director today. The session is still stopped; what is refused
is the REPORT.

**My recommendation, and it is the Architect's to settle:** reporting a failure for an operation that
SUCCEEDED is itself a false report, and arguably the very thing this mission exists to remove - a
tool confident in one direction and vague in the other. A degraded but honest headline - the session
was stopped, and this machine's Director is too old to say what it found - would claim nothing false
while not calling a success a failure. Worker B says it is one method either way.

### 3. Three smaller ones, all sound in my reading

- **A session whose Director has merely gone quiet is not `notOnFleet`** - it is the existing
  retryable 503, Ruling 3's "the owning Director could not be reached". Otherwise the Gateway would
  say "nothing in this account carries that id" about a session that IS in the account. (Worker B.)
- **The stop route resolves the tenant itself** rather than through `LocateSessionForRequestAsync`,
  which collapses "no tenant bound" and "no such session" into the same nulls. (Worker B.)
- **A one-second settle window on the process re-check.** `Process.Kill(entireProcessTree: true)` is
  `TerminateProcess` and returns BEFORE the process is gone, so an immediate single re-check can
  report "the process would not die" about a perfectly successful kill. It polls every 50 ms for up
  to a second and returns the instant the process is gone. It is not a second grace period - the
  force-kill has already been issued. One constant if unwanted. (Worker A.)

### 4. A consequence to an existing report, said out loud rather than discovered

**`OutcomeLedgerReporter` will now count stops as interventions.** Verified in code, not assumed:
it counts EVERY row in the `intervention` category per session with no filter on event type
(`OutcomeLedgerReporter.cs:81`, fed to `InterventionCount` at line 190). A `stopped` row is in that
category. It is defensible - a session that had to be stopped did require an intervention - but it
moves a number in a shipped report, and somebody should decide that on purpose.

The alternative was to reuse `human-cancelled`, which would be a lie every time one agent stops
another - which Ruling 4 makes the ordinary case. So the validated list was extended deliberately,
as the handoff instructed.

### 5. Worker C's two, which I accepted

- It added `session-stop` and `session-done-undo` to the action catalogue. A verb missing from it
  does not exist to the fleet. (`session done` itself still has no entry - pre-existing, not closed.)
- An answer carrying no `headline` exits non-zero. Strictly a fourth non-zero case beyond Ruling 3's
  three. It is not a verdict: it is a broken instrument, and exiting 0 while printing nothing is the
  button that accepts a click and says nothing.

---

## Two defects I put in my own briefs and had to correct mid-flight

Recorded because the Inspector should know the briefs were wrong before the code was.

1. **`ShortId` would have truncated a typed NAME to eight characters.** The `notOnFleet` case is
   reached precisely when someone typed something unresolvable, which is often a name. Worker B's
   test caught the pre-correction behaviour printing `Stop a s`.
2. **I told Worker A to read process liveness from `_backend.HasExited`.** That is wrong on three of
   the five backends: `ISessionBackend.HasExited` is documented as "the process has exited", but
   `PipeBackend`, `StudioBackend` and `GitHubActionsBackend` all return `_disposed`. Corrected to an
   operating-system check on the captured process id, following `LauncherDiscovery.IsRunning`. This
   would have answered the headline fact of the whole feature wrongly.

---

## Everything else that is NOT proven

Named as gaps rather than left to be discovered.

- **Pid reuse is open**, and named in the code. Between capturing the process id and re-checking it,
  the operating system could hand that number to something else. Closing it needs a process handle
  held across the kill - a change to the backends, not to this verb.
- **The three-second worktree probe timeout is not tested as a timeout.** The exception path is
  tested by throwing directly; nothing proves the cancellation token reaches git.
- **The git probe reads a ten-second shared cache**, so a stop can report a worktree state up to ten
  seconds old. Accepted deliberately and reasoned in the code: the sentence is advisory, nothing is
  gated on it, and the service that later removes a worktree re-checks and fails closed.
- **No client was exercised.** That `DELETE /sessions/{sid}` may safely grow its response body rests
  on my reading of four call sites - `killSession` in client-core, the Cockpit menu, the mobile hook
  and the native phone client - all of which read the status code only. No test covers a client.
- **No hosted or multi-tenant path was exercised.** Every route test runs self-host where the tenant
  is always Local; the 403-when-no-tenant branch is written and reasoned but untested.
- **The audit actor is `unknown` in every route test**, because the harness authenticates nothing.
  The four actor shapes are covered in the fold's own tests. No route test drives a real session key
  through `AuthMiddleware`.
- **The audit write is best-effort.** If the append throws, the stop still answers success and the
  failure is logged loudly - the session is already ended, and answering an error would be a lie
  about the one fact this verb reports. A database fault therefore produces a stop with no audit row,
  and only the log records it. Given the owner accepted Ruling 4 ON the ground that stops are
  audited, the Architect may want that decided rather than inherited.
- **`WorkingDirectory` versus `RepoPath` is untested as a difference**, because every path that
  creates a session today passes the same string for both.
- **The help text is unverified.** `--help` cannot render in this environment at all - the installed
  typer and click raise `TyperArgument.make_metavar() takes 1 positional argument but 2 were given`
  for every command, including ones written long before this mission. That is also the cause of the
  two pre-existing Python failures. Flag declarations are asserted directly instead.
- **`session stop` has no `--json`.** An agent parsing it today reads Rich-rendered text that wraps
  to the console width.

---

## Housekeeping

- One comment fix that no Worker owned: `AutoDismissSweeper` said "a typed failure (e.g. NotFound =
  already gone)". The kill verb no longer returns NotFound, so that example was false. Rewritten to
  say what now arrives there - including the new process-would-not-die failure, which the sweeper
  should mark terminal rather than claim it closed a session whose agent is still running.
- The mission record is committed on the branch: the three Worker briefs, the three Worker notes,
  and this report.
- All three Workers were flagged for deletion after their work was committed and pushed.
