# Worker A - the Director's honest answer

Item 1 of the seven, Phase A. What was built, what was proved and how, and what was NOT proved.

**Files touched, and only these:**

- `src/CcDirector.ControlApi/SessionCommandExecutor.cs` - `KillAsync` rewritten to answer
  `DirectorStopResult`, plus four small private helpers beside it.
- `src/CcDirector.Gateway.UnitTests/SessionCommandExecutorTests.cs` - eight new tests, one existing
  test deliberately changed, and a local test backend.

Nothing else. The Gateway, the Python command line, the Cockpit, the Director window and the mobile
app were not touched - other seats own those.

---

## What it now does

`SessionCommandExecutor.KillAsync` returns a serialized `DirectorStopResult` (the shape already
written in `SessionStopDtos.cs` - no field invented, none renamed) instead of
`{ killed = true, removed = true }`.

In order:

1. Invalid session id -> `BadRequest`, unchanged.
2. **No row on this Director -> `Ok`, verdict `alreadyStopped`**, `ProcessId` null, `ProcessEnded`
   false, `RowRemoved` false. This is the one behaviour change to an existing contract, and it is
   Ruling 3: a stop must never fail because there is nothing left to stop.
3. With a row, the facts are captured BEFORE the stop: the process id (null when the session held
   none), whether that process is alive, the worktree path, and whether that worktree had
   uncommitted changes.
4. The kill runs exactly as before - same call, same `FleetKillGraceMs` window, same best-effort
   catch. The escalation was not touched.
5. The liveness check is run again. `ProcessEnded` is "a live process was found AND it is now gone".
6. A live process that is still there -> `DirectorCommandStatus.Error`, message naming the process
   id. The row is deliberately left in place (below).
7. Otherwise the row is removed, `Killed`/`Removed` keep their original `true` values as
   compatibility fields, and `Verdict` is `stopped` when a live process was found and ended,
   `alreadyStopped` otherwise. The Director never returns `notOnFleet`.

`FileLog.Write` on entry and on every exit, carrying the verdict, the process id, whether a row was
removed, the worktree, and whether the worktree probe answered (`dirty` / `clean` / `unknown`).

---

## The Manager's correction, and what it changed

The brief originally said to read liveness from `_backend.HasExited` and to add a `Session.HasExited`
that wrapped it. The Manager corrected that mid-build: only `ConPtyBackend` and `UnixPtyBackend`
implement `HasExited` as "the process has exited" - `PipeBackend`, `StudioBackend` and
`GitHubActionsBackend` all return `_disposed`.

Built to the correction. Liveness is asked of the operating system by process id, following
`LauncherDiscovery.IsRunning` (`Process.GetProcessById`, `ArgumentException` means no such process,
anything unreadable is treated as not alive). **`Session.HasExited` was NOT added** - it would have
been a wrapper around the fact the correction rejected, so leaving it out is deliberate, not an
oversight. Step 5 of the brief still says "the backend now reports it exited"; that sentence is left
over from the pre-correction version, and it is the same operating-system check that runs, twice.

---

## Two things I decided that the brief did not settle

**1. A one-second settle window on the re-check.** Not in the brief. Without it the verb is wrong in
the real product: the force branch of the kill is `Process.Kill(entireProcessTree: true)`, which is
`TerminateProcess` and returns BEFORE the process is gone, so an immediate single re-check can read
"still alive" milliseconds after a perfectly successful kill and report "the process would not die"
when it did. So the re-check polls every 50ms for up to one second, returns the instant the process
is gone, and then fails loudly. It is not a second grace period - the force-kill has already been
issued by the time it is reached - and it does not touch the escalation. **If the Architect wants it
gone, it is one constant.**

**2. `Killed` and `Removed` are `true` on the no-row path.** The brief says they keep their existing
values on every success path; there was no existing success path for a missing row (it was a 404), so
this is a judgment call. They read true because the only thing the old two-field answer could ever
mean is "the session is not there", and it is not there. **Worker B should not read them** - the
Gateway fold has `ProcessEnded` and `RowRemoved`, which say what actually happened.

---

## What was proved, and how

Nine tests cover the kill verb's states, and
**every one of them was watched failing on purpose**: twelve targeted reversions of the shipped
behaviour, one at a time, each followed by a full run of the file and a restore. Every reversion
produced a named red test with the symptom that test claims to catch.

| What was reverted | What went red | What the red said |
|---|---|---|
| verdict always `alreadyStopped` | `Kill_LiveProcessFoundAndEnded...`, `Kill_SecondStop...` | Expected "stopped", Actual "alreadyStopped" |
| verdict always `stopped` | `Kill_RowWithNoLiveProcess...`, `Kill_WorktreeProbeThrows...` | Expected "alreadyStopped", Actual "stopped" |
| failed probe reports **false** instead of null | `Kill_WorktreeProbeFails_ReportsNullNotFalse` | Assert.Null failure - Expected null, Actual False |
| probe exception no longer caught | `Kill_WorktreeProbeThrows...` | `System.OperationCanceledException : the git probe timed out` |
| no row is `NotFound` again | `DispatchAsync_Kill_NoRowOnThisDirector...`, `Kill_SecondStop...` | Expected Ok, Actual NotFound / Expected Not NotFound, Actual NotFound |
| a stuck process reported as success | `Kill_ProcessWouldNotDie...` | Expected Error, Actual Ok |
| the re-check after the kill dropped | `Kill_ProcessWouldNotDie...` | Expected Error, Actual Ok |
| `RowRemoved` reports the process, not the row | `Kill_RowWithNoLiveProcess...`, `Kill_WorktreeProbeThrows...` | Assert.True failure - Expected True, Actual False |
| the worktree path not reported | `Kill_DirtyWorktree...`, `Kill_CleanWorktree...` | Expected the temp path, Actual null |
| the process id not reported | `Kill_LiveProcessFoundAndEnded...` | Expected 4242, Actual null |
| a dirty tree reported clean | `Kill_DirtyWorktree_ReportsTrueAndNamesThePath` | Assert.True failure |
| a clean tree reported dirty | `Kill_CleanWorktree_ReportsFalse` | Assert.False failure |

The four tests that pin the `FleetKillGraceMs` window were not touched and stayed green through all
twelve reversions, which is the evidence that the escalation was not changed.

One test was changed deliberately: `DispatchAsync_Kill_MissingSession_ReturnsNotFound` is now
`DispatchAsync_Kill_NoRowOnThisDirector_IsAlreadyStoppedNotNotFound`, with a comment naming Ruling 3.

A note on the instrument itself: the first pass of that twelve-reversion run reported **zero** red
tests for all ten reversions it tried, which looked like a clean sweep and was not - the parser was
reading the wrong lines and the run was quiet, not passing. The pass condition was an absence. It
now names the red test and shouts when a reversion reddens nothing, and two reversions that had
genuinely never run (a transient compile error in another seat's `GatewayEndpoints.cs` broke the
build under them) were found that way and re-run.

---

## The local gate is NOT green, and neither failure is this change

**`.\scripts\test-local.ps1` fails on this working tree. I could not make it pass and I am not
claiming it passed.** Both failures were run down, and neither is caused by anything in this item.
This is for the Manager to decide, not for me to work around.

**1. `CcDirector.Gateway.UnitTests` is OVER BUDGET and gets stopped at the 120-second ceiling.**
Run on its own it is **4,221 tests, all passing, in 3 minutes 11 seconds** - 71 seconds past the
ceiling, so the gate stops it and records no result at all.

That is not this change. The whole of `SessionCommandExecutorTests` - all 68 tests in the file,
including my nine - runs in **5 seconds**. Take every second of that away and the suite is still
66 seconds over. My nine tests add roughly one second between them, nearly all of it the deliberate
settle window in the would-not-die test.

Worth knowing: Worker B is adding to this same test project (`SessionStopFoldTests.cs`), so
whatever is decided here lands on both of us. The choice is the one the script itself names - park
the suite, or split it - and it is above a Worker.

**2. `cc-director-setup-engine.Tests` failed one test in the gate run and passes in isolation.**
`GatewayAccountEnrollRunnerTests.EverySignInCancelledMessage_StatesTheFact_AndNamesNoButton` failed
with `Assert.Contains() Failure ... String: "DEVTHROTTLE_HOSTED_GATEWAY_URL is set to "`. Re-run
alone: **541 of 541 pass**.

It is a pre-existing race on process-global state, not a flake to be shrugged at: a sibling test
file (`GatewayHostedEnrollRunnerTests`) sets `DEVTHROTTLE_HOSTED_GATEWAY_URL` for the life of a
scope and restores it afterwards, and `HostedGateway.ResolveUrl` honours that variable. While that
scope is open, any test in the assembly running in parallel sees it. Nothing in this item touches
the installer, that assembly, or that variable.

The suites that DID complete were green: Core.UnitTests 227, Avalonia.Tests 407, Engine.Tests 63,
HostedAgent.Tests 88, Launcher.Tests 39 seconds/188, Terminal.Avalonia.Tests 25,
cc-director-setup.Tests 25.

The gate also printed a COVERAGE GAP warning naming `CcDirector.Core.Tests` and
`CcDirector.Gateway.Tests` as parked suites this change touches. **I did not run `-Parked`** - the
phase handoff already puts that on the Manager, and it is one machine-wide lock shared with the
other seats.

---

## What was NOT proved

- **No real process was ever killed by these tests.** Liveness is an injected delegate, so every
  branch is driven without starting anything. The production check itself
  (`Process.GetProcessById`) has NO test, and neither has the settle window. The first real
  evidence that the Director ends a real agent process will be the mission's QA report, item 4 -
  "the machine itself showing that process id no longer exists". Until then, treat the
  live-process path as reasoned, not demonstrated.
- **No real git was ever run.** The worktree probe is an injected delegate too. What is proved is
  that `Success == false` and a thrown probe both become null, and that a count above zero becomes
  true. That `GitStatusProvider.GetCountAsync` reports this repository correctly is assumed, not
  shown here.
- **The three-second probe timeout is not tested as a timeout.** The exception path is tested by
  throwing `OperationCanceledException` directly; nothing proves the cancellation token actually
  reaches git and cuts a slow call short.
- **Pid reuse is open and named in the code.** Between the capture and the re-check the operating
  system could hand that number to something else, and the re-check would then report a process
  that would not die. Closing it needs a process handle held across the kill, which is a change to
  the backends, not to this verb.
- **`WorkingDirectory` versus `RepoPath` is untested as a difference**, because on every path that
  creates a session today `SessionManager` passes the same string for both. The choice only bites
  for a restored session that persisted a different working directory, and no test builds one.

## The ten-second git cache - decided, and it does not matter

`GitStatusProvider` caches for ten seconds, keyed by path and shared across instances, and this
Director's own `SessionGitStatusMonitor` is filling that same cache every fifteen seconds. So a stop
can report a worktree state up to ten seconds old.

**Accepted, not invalidated**, and the reasoning is in the code beside the probe. The worktree
sentence is ADVISORY: Ruling 2 says the stop never refuses, so nothing is gated on the answer, and
the service that later removes a worktree re-checks for itself and fails closed on anything modified
or untracked. The cost of being wrong is that the operator is pointed at the wrong tree for ten
seconds. The cost of invalidating would be throwing away, on every stop, a cache entry an unrelated
monitor owns. In practice the monitor's fifteen-second poll is longer than the ten-second time to
live, so the entry is usually already expired and the stop gets a fresh answer anyway.

## One thing for the Manager to know about the worktree

The twelve reversions were applied to `SessionCommandExecutor.cs` **in this shared worktree**, one at
a time, each restored immediately. Every mutation compiled, so nobody's build was broken by them -
but for roughly twenty minutes a Gateway or Director build in this tree could have picked up a
deliberately wrong `kill` verb. If another seat saw a strange kill result in that window, that is
where it came from. The file is verified byte-identical to the original now.
