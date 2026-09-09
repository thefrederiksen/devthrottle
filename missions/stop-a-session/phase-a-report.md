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
| 4 | `notOnFleet` is a 200 success on the stop (the legacy `DELETE` door keeps its 404 - see the regression below) | `StopSessionAsync` | `91b79b1e`, `5397e01b` |
| 5 | The allow list: `stop` in, cancel-deletion in, bare `DELETE` still refused | `SessionKeyGuard.cs` | `91b79b1e` |
| 6 | The audit record - a new `stopped` intervention type, actor required | `GovernanceAuditEventDtos.cs`, `GovernanceAuditLog.cs` | `91b79b1e` |
| 7 | The two commands | `session_ops.py`, `cli.py` | `5e16767a` |

The shared answer shapes were written FIRST (`SessionStopDtos.cs`, `071e549f`) so three seats built
against one interface instead of discovering three versions of it.

**Then the Architect's ruling on this report added four more, all done (`2388631c`, `5397e01b`):**

| Thing | What changed |
|---|---|
| The 502 is REVERSED | An answer this Gateway cannot describe folds to the fourth verdict `stoppedNotDescribed` - the stop is reported, the description is not invented. Both doors. |
| Stops are not counted as interventions | The audit row stays in the `intervention` category; it is excluded from `OutcomeLedgerReporter`'s derived `InterventionCount`. |
| `session stop --json` | The whole folded answer, printed plainly, for the agent callers Ruling 4 makes ordinary. |
| The parked run | Completed - and it caught a regression. See below; this is the most important part of this report. |

---

## What is proven, and how

**71 new tests from the three Workers, plus 10 more of mine for the Architect's ruling. Every one was
watched failing on purpose** - the change reverted, the test watched going red with the symptom it
claims to catch, then restored. Worker A did twelve reversions, Worker B three rounds covering ten,
six and four failures, Worker C sixteen. Each Worker recorded what the red actually SAID, not just
that something went red; those tables are in `worker-a-notes.md`, `worker-b-notes.md` and
`worker-c-notes.md`.

My own ten, and the reds they produced:

| Mutation | Went red |
|---|---|
| `CanDescribe` always accepts, so the fourth verdict can never be produced | the three `stoppedNotDescribed` fold tests |
| the undescribed branch fabricates a worktree sentence | `A_stop_that_could_not_be_described_names_no_process_and_no_worktree` |
| the `stopped` exclusion removed from the ledger count | `A_stop_is_recorded_in_the_trail_but_never_counted_as_an_intervention` - **Expected: 1, Actual: 3** |
| the JSON printed through Rich instead of plainly | three `--json` tests, all with `json.decoder.JSONDecodeError` |
| the `--json` flag renamed | `test_the_json_flag_is_declared_on_stop` - `assert '--json' in ['--reason', '-r', '--as-json']` |
| the legacy door pointed back at the stop's answer | **all three** door tests - `Expected: NotFound, Actual: OK` |

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
| `Gateway.UnitTests` alone, to completion | **4222 tests, 0 failures**, ~2 m 40 s |
| `.\scripts\test-local.ps1 -Parked`, on the FINAL code | **COMPLETED.** `Gateway.Tests` 2389 passed / **2 failed** / 47 skipped (1 h 07 m). `Core.Tests` 4372 passed / **0 failed** (11 m 38 s). The 2 are proven environmental - see below. |
| `python -m pytest tools/test_shipped_tools_contract.py` | 36 passed (this is the ASCII-only guard) |
| `cc-devthrottle` suite, `FORCE_COLOR=1` | **266 passed**, 2 failed - both pre-existing, proved below |
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
change touches. That is why `-Parked` was started.

### `-Parked` COMPLETED, and it caught a regression the default gate never could

The first draft of this report carried a literal `PARKED_RESULT` placeholder here while the prose
beside it said the run had happened. The Architect caught it. That is a check whose pass condition is
nobody looking, and it would have certified a run that never finished. It is recorded rather than
quietly filled in, because the thing it nearly hid turned out to be real.

**The completed run, on the final code:**

| Suite | Result |
|---|---|
| Core.UnitTests | 227 passed |
| Gateway.UnitTests | 4222 total, 0 failed (3 m 08 s) |
| Avalonia.Tests | 407 passed |
| Engine.Tests | 63 passed |
| HostedAgent.Tests | 88 passed |
| Launcher.Tests | 188 passed |
| Terminal.Avalonia.Tests | 25 passed |
| cc-director-setup.Tests | 25 passed |
| cc-director-setup-engine.Tests | 541 passed |
| **CcDirector.Gateway.Tests** | **2438 total, 2389 passed, 47 skipped, 2 FAILED** (1 h 07 m) |
| **CcDirector.Core.Tests** | **4380 total, 4372 passed, 0 failed** (11 m 38 s) |

### THE REGRESSION. This is the most important thing in this report.

**Making `DELETE /sessions/{sid}` a thin forward silently changed what it answers for an unknown
session - from the locator's 404 to the stop's 200 `notOnFleet` - and broke two EXISTING tests.**
Both live only in the parked suite, so nothing that ran before would ever have shown it:

    StreamCommandTests.StreamModeOff_KillEndpoint_StaysOnHttp
    HostedSessionCommandRouteTenancyTests.Another_tenant_cannot_reach_it(method: "DELETE")

Both failed with `Expected: NotFound / Actual: OK`. **The second is a CROSS-TENANT ISOLATION test**:
it pins that one account naming another account's session identifier gets exactly the locator's
not-found answer.

**Worker B reported running its own 16 route tests green, and that was true and beside the point.**
It never ran the whole parked suite, so it never saw what its change did to the tests already there.
That is precisely the shape of a proof that covers the wrong thing, and it is why the Architect was
right to refuse to land without these numbers.

**The fix (`5397e01b`):** Ruling 3's "nothing on this fleet is a success" is about THE STOP - the verb
this mission adds, which the command line calls and `POST /sessions/{sid}/stop` serves. `DELETE` is a
legacy door kept for exactly one reason, a shipped native phone client that does not deploy with the
Gateway, and keeping a door for compatibility means keeping what it answers. It now keeps its 404.

It is still one stop: nothing is stopped on that path at all - no Director is asked, no answer is
folded, no audit row is written - so the doors differ only in how each says "there is nothing of
yours here". **The available alternative was to edit a cross-tenant isolation test until it agreed
with the new code, and that is the move to distrust.** Leaving both security tests untouched is the
strongest evidence the fix is the right one.

A third test also had to change - `SessionStopEndpointTests`, which still pinned the 502 the Architect
reversed. It now asserts the fourth verdict, and gained the twin it was missing: the legacy door gets
`stoppedNotDescribed` too.

### The two remaining failures are NOT this branch, and it is proven rather than argued

    PathContainmentLinkEscapeTests.ResolveScreenshot_fileLinkPlantedInsideTheScreenshotsFolder_isRefused
    PathContainmentLinkEscapeTests.ResolveSessionFile_fileSymbolicLinkUnderTheRootEscapingIt_isRefused

Those tests create a FILE symbolic link and, by their own design, `Assert.Fail` loudly when the host
cannot - deliberately, rather than skipping into a false green. Probed directly on this machine:

    New-Item -ItemType SymbolicLink  ->  "Administrator privilege required for this operation."
    Developer Mode: (unset)          Elevated: False

That is the host, not the code. This branch touches nothing under path containment, and only the two
FILE-link tests fail while the directory-link test beside them passes - which is exactly the split
the privilege explains. **A machine with Developer Mode or elevation is needed to run those two at
all; on this one they cannot be proven either way.**

### The `Launcher.Tests` margin FAIL - answered, not waved away

The Architect asked me not to leave this as an oddity nobody owns. **The margin `PASS`/`FAIL` is the
`dotnet test` PROCESS EXIT CODE, not the test results** - `scripts	est-local.ps1:286` prints it from
`$r.Process.ExitCode`, while the summary beside it comes from the console line. So that row meant
"this process exited non-zero although all 188 of its tests passed".

It did not reproduce: `Launcher.Tests` printed `PASS` in the default gate and in both later parked
runs, and its TRX outcome is `Completed` with 188 every time. **I could not make it happen again, so
I cannot say what caused that one exit code** - the run it appeared in was one of three competing for
a loaded machine, which is the same condition that produced two other failures that also vanished
when the machine went quiet. It is recorded here as unexplained rather than explained.
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

### 2. An answer this Gateway cannot fold honestly. RULED ON AND NOW REVERSED - built, not just agreed.

The Gateway and the Directors do not deploy together. During a rollout, a Gateway carrying this
mission can be handed an answer from an older Director that reports only `killed`/`removed`. There is
no honest headline for that, so Worker B refuses it with a 502 naming the old Director.

**The cost is real and it is not small:** in that window a stop through EITHER door fails for a
session on a machine that has not updated - including `DELETE /sessions/{sid}`, which shipped clients
call and which succeeds against such a Director today. The session is still stopped; what is refused
is the REPORT.

**The Architect agreed and reversed it, and it is now built.** Ruling 3 gained a fourth verdict word,
`stoppedNotDescribed`: the stop is reported, and the description is not invented. The headline leads
with the success and then says this machine could not describe it; the process id, the worktree and
every description boolean are left empty, and the DTO says in terms that they mean "not established"
rather than "established to be false". Both doors get it, so `DELETE` does not start failing against
an older Director where it succeeds today. The audit row IS written, because a stop happened - the
502 had been silently losing that row too.

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

**RULED ON: stops must NOT count, and that is now built.** What was found, in code rather than by
assumption: **`OutcomeLedgerReporter` would have counted stops as interventions.** Verified in code, not assumed:
it counts EVERY row in the `intervention` category per session with no filter on event type
(`OutcomeLedgerReporter.cs:81`, fed to `InterventionCount` at line 190). A `stopped` row is in that
category. It is defensible - a session that had to be stopped did require an intervention - but it
moves a number in a shipped report, and somebody should decide that on purpose.

The alternative was to reuse `human-cancelled`, which would be a lie every time one agent stops
another - which Ruling 4 makes the ordinary case. So the validated list was extended deliberately,
as the handoff instructed.

The row stays in the `intervention` category, because the audit trail Ruling 4 rests on has to hold
it. Only the DERIVED count excludes it, with the reasoning in the code beside the filter: that number
means "how often did this session need a person", and a number whose meaning changes underneath its
readers is worse than a missing one.

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

- **The parked suites are unverified by me.** `-Parked` did not finish, and the two suites that did
  not report are the two parked ones. Worker B's 16 route tests in `Gateway.Tests` therefore rest on
  the Worker's own account of running them. This is the top outstanding item.
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

## What the Architect still has to decide, in one place

1. **The end-to-end run does not exist**, and it is the largest risk in the mission. The ruling
   already places it in Phase B, which stands the stack up locally and proves one real session can be
   stopped. Nothing in Phase A substitutes for it.
2. **Two path-containment tests cannot run on this machine at all** (file symbolic links need
   Administrator privilege or Developer Mode). They are unproven here in both directions, and no
   machine in this fleet has yet run them green as far as this report knows.
3. **The audit write is best-effort.** A database fault produces a stop with no audit row, logged
   loudly and nowhere else. Given the owner accepted Ruling 4 ON the ground that stops are audited,
   that may deserve a decision rather than being inherited from the first implementation.
4. **`notOnFleet` writes no audit row.** Deliberate - nothing was stopped and there is no session in
   the account to key the row to - but it means a caller repeatedly naming an identifier that does
   not exist leaves no trace.
