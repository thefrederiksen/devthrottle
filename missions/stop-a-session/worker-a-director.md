# Worker A - the Director's honest answer

You are a Worker on the "Stop a session" mission, Phase A. Your Manager is session `ee59e5d0`.

**Read first, in full:** `missions/stop-a-session/handoff-phase-a.md` (the phase and the outcome
contract), then `missions/stop-a-session.html` sections 4 and 5 (the six rulings - they are settled,
you do not reopen one), then `missions/stop-a-session/architect-state.md`.

Work in this worktree, on branch `mission/stop-a-session`. Do NOT merge anything. Do not push -
tell your Manager when you are done and it commits. Do not touch the Gateway, the Python command
line, the Cockpit, the Director window or the mobile app: other seats own those and you will
collide with them.

## Your one job

Item 1 of the seven. Make the Director's `kill` verb answer honestly.

`SessionCommandExecutor.KillAsync` (`src/CcDirector.ControlApi/SessionCommandExecutor.cs`) today
returns `{ killed = true, removed = true }` and nothing else. It cannot tell "there was a live
process and I ended it" from "there was nothing running", because the kill is best-effort and
swallows the difference. Fix exactly that.

**The answer shape is already written**, in `src/CcDirector.Gateway.Contracts/SessionStopDtos.cs`
(`DirectorStopResult`). Do not invent fields and do not rename any. Read that file's comments -
they carry the reasoning, and the `WorktreeHadUncommittedChanges` comment is load-bearing.

## What it must do, in order

1. **Invalid session id** -> `BadRequest`, unchanged.

2. **Capture the facts BEFORE the stop**, from the session if there is a row for it:
   - `ProcessId` = the agent process id, or null when the session held none (`ProcessId <= 0`).
   - whether a LIVE process was there. `Session` has no `HasExited` today - add one that reads
     `_backend.HasExited` (the backend interface already declares it). A live process means: a row
     exists, `ProcessId > 0`, the backend has not exited, and `Status` is not `Exited` or `Failed`.
   - `WorktreePath` = the worktree or repository the session held (`Session.WorkingDirectory`, and
     say in a comment why you chose it over `RepoPath` if they differ).
   - `WorktreeHadUncommittedChanges`, via `GitStatusProvider.GetCountAsync` in
     `src/CcDirector.Core/Git/` - the service that already knows how to answer this. Use it; do not
     shell out to git yourself. `Success == false` means UNKNOWN, so the field is **null**, never
     false. Give the probe a short timeout (three seconds is right) and treat a timeout as unknown:
     a git probe must never be able to hold up or fail a stop. Inject the probe as a delegate so it
     is a test seam, the way `SessionGitStatusMonitor` takes one.

3. **NO ROW ON THIS DIRECTOR** -> this is `alreadyStopped`, NOT `NotFound`. Ruling 3 says the
   owning Director is asked to stop the session whether or not it still has a row for it, and a
   stop must never fail because there is nothing left to stop. Answer `Ok` with
   `ProcessId = null, ProcessEnded = false, RowRemoved = false, Verdict = "alreadyStopped"`.
   **This changes an existing test** - `DispatchAsync_Kill_MissingSession_ReturnsNotFound` in
   `src/CcDirector.Gateway.UnitTests/SessionCommandExecutorTests.cs`. Change it deliberately, rename
   it to say what it now asserts, and leave a comment naming Ruling 3 as the reason.

4. **Run the kill exactly as it runs today** - same call, same `FleetKillGraceMs` window, same
   best-effort catch. Do not change the escalation. Four existing tests pin that window; they must
   all still pass untouched.

5. **After the kill, check again.** `ProcessEnded` = a live process was found AND the backend now
   reports it exited. If a live process was found and it is STILL not exited, that is Ruling 3's
   third failure - "the process would not die" - so return `DirectorCommandStatus.Error` with a
   message that says exactly that and names the process id. Do not report it as a success.

6. **Remove the row** as today. `RowRemoved` = a row WAS present and this removed it.

7. `Killed` and `Removed` keep their existing values on every success path, so nothing reading the
   old answer changes meaning. Say in the comment that they are compatibility fields that do not
   distinguish the states, and that the honest fields beside them are why.

8. `Verdict` = `"stopped"` when a live process was found and ended, otherwise `"alreadyStopped"`.
   The Director NEVER returns `notOnFleet` - it cannot see the whole account.

## Logging

`FileLog.Write` on entry and on the answer, in the house format, carrying the verdict, the process
id, and whether the worktree probe succeeded. A stop is the sharpest thing the product does; the log
must be able to answer "what did it find" afterwards.

## Tests - and watch every one fail on purpose

`src/CcDirector.Gateway.UnitTests/SessionCommandExecutorTests.cs` is where the kill tests live.
Cover, one test each:

- a live process is found and ended -> `stopped`, `ProcessEnded` true, `RowRemoved` true, process id
  reported
- a row with no live process -> `alreadyStopped`, `ProcessEnded` false, `RowRemoved` **true**
- no row at all -> `alreadyStopped`, `RowRemoved` false, and NOT `NotFound`
- a second stop straight after the first -> still `alreadyStopped`, still exit-worthy as a success
- the worktree is dirty -> `WorktreeHadUncommittedChanges` true and the path reported
- the worktree is clean -> false
- the probe fails -> **null**, and specifically assert it is null rather than false. This is the one
  the whole field exists for.
- a live process that will not die -> `Error`, and the message names the process id
- invalid session id -> `BadRequest`, unchanged

**Before you believe any of them:** revert your change, run the test, watch it go RED with the
symptom it claims to catch, then restore. A test you have not watched fail is decoration. Tell your
Manager which ones you watched fail and what the red said.

Run `.\scripts\test-local.ps1` and make sure it is green before you report.

## Say what you did NOT prove

Write the gaps into the code as gaps. In particular: the git probe has a ten-second cache shared
across instances, so a stop can report a worktree state up to ten seconds old - decide whether that
matters, say so in a comment either way, and tell your Manager.

## When you are finished

Tell your Manager (session `ee59e5d0`) in ONE line - fleet messages truncate at the first newline.
Put the detail in `missions/stop-a-session/worker-a-notes.md` and point at it. Do not narrate
progress while you work; a message interrupts the session that receives it.
