# Worker F - "could not be determined" is never "gone"

You are a Worker on the "Stop a session" mission, Phase C. Your manager is session `e7ea69df`.
Branch `mission/stop-a-session`, worktree `C:\ReposFred\devthrottle-stop-a-session`. Work there and
nowhere else. Do not merge to main. Do not open a pull request.

Read these first, in full:

1. `missions/stop-a-session/inspection-1.md` - findings **I2** and **I3** are yours.
2. `missions/stop-a-session/architect-ruling-on-inspection-1.md` - read the section headed
   **"The ruling the fixes cannot be made without"** twice. It is the whole shape of your work.
3. `missions/stop-a-session.html` - Ruling 3, whose fourth verdict word was just broadened to cover
   an unreadable liveness check.

## What is wrong

`SessionCommandExecutor.DefaultProcessIsAlive` (`src/CcDirector.ControlApi/SessionCommandExecutor.cs:546`)
returns `false` for **every** exception, including a `Win32Exception` from reading `HasExited` on a
process that is demonstrably alive. That `false` then does two different pieces of damage:

- before the stop, it suppresses the exit verification and the verdict becomes `alreadyStopped`;
- during the final check, it certifies `ProcessEnded = true`.

An access failure establishes neither fact. The inspection reproduced this: a live process whose
`HasExited` threw was reported `alreadyStopped`, `RowRemoved = true`, and the process was still there
afterwards.

Separately (I3), a backend that reports process identifier zero - `GitHubActionsBackend`, and the
`Pipe` and `Studio` backends - is never checked at all, and lands in the same `alreadyStopped`
branch. `GitHubActionsBackend.GracefulShutdownAsync` (`src/CcDirector.Core/Backends/GitHubActionsBackend.cs:158`)
catches a failed `CancelRunAsync`, writes the failure into its own buffer, and returns normally. So a
remote run that refused to cancel is reported as "no process was running" and its row is removed.

## The ruling you build to

**Liveness has THREE answers - alive, gone, and could not be read - and the third is not a synonym
for either.**

- `DefaultProcessIsAlive` stops collapsing every exception into `false`. Make the seam three-valued
  (an enum - `Alive`, `Gone`, `Unreadable` - is the obvious shape; the seam signature changes with
  it). `ArgumentException` from `Process.GetProcessById` is a real `Gone`. Anything else is
  `Unreadable`, and it carries the exception's own words forward.
- When liveness cannot be read, the stop **still attempts the shutdown** - it was always best-effort -
  and then reports **`stoppedNotDescribed`**, with a detail line naming what specifically could not
  be read. It must never report `alreadyStopped` and must never set `ProcessEnded = true`.
- **Reuse `stoppedNotDescribed`. Do not add a fifth verdict.**
- **A backend with no process identifier is the same case.** Where there is no identifier to check,
  the verdict cannot be `alreadyStopped`; it is `stoppedNotDescribed`, and the detail says that this
  session carried no process identifier so nothing could be checked.

## Three mechanics decisions your manager has already taken - build to them, do not re-open them

1. **`stoppedNotDescribed` now has three causes, and the fold must tell them apart in words.** The
   Director sends the verdict plus a short machine-written sentence saying what could not be
   established; the Gateway fold turns that into the operator's line. The existing cause - an older
   Director that says only `killed`/`removed` - keeps its existing headline exactly. Add a field to
   `DirectorStopResult` for the Director's sentence, and teach `SessionStopFold.CanDescribe` that
   `stoppedNotDescribed` from a Director is now a KNOWN word rather than the shape an old Director
   answers with. Its doc comment currently says the opposite; correct it.

2. **Facts that WERE established are still reported under `stoppedNotDescribed`.** The process
   identifier read off the row, whether the row was removed, and the worktree line Ruling 2 requires
   are all things this stop genuinely established, and blanking them would throw away information the
   operator needs - including the dirty-tree sentence, which Ruling 2 makes mandatory. Only the
   process facts nobody could read are left empty: `ProcessEnded` stays false and the headline says
   in words that whether it was running or has ended is not known. **`SessionStopDtos.cs` currently
   says every description field under this verdict is meaningless; that sentence is now true only of
   the older-Director cause, and you must correct it to say so per cause.** Do not leave a false
   comment behind.

3. **A backend whose own shutdown reported a failure is a FAILURE, and its row is NOT removed.** This
   is Ruling 3's third failure - "the process would not die" - reached by a different road, and the
   executor already has that branch and already leaves the row in place on purpose so the operator can
   try again. Surface the backend's own words in the failure sentence. The mechanism: `ISessionBackend`
   grows a default-implemented member reporting the last shutdown failure (default null, so no other
   backend changes), `GitHubActionsBackend` sets it where it currently only writes to its buffer, and
   the executor reads it through `Session.Backend` after the kill. Do not make
   `GracefulShutdownAsync` throw - other callers depend on it not throwing.

## THE STANDARD, and it is why this finding exists

The inspection replaced the production liveness check with the constant `false` and **all 68 tests in
`SessionCommandExecutorTests` still passed**, because every test injects its own substitute for it.

**Every test you write is watched failing against the PRODUCTION code path.** Concretely:

- There must be tests that call the executor with **no injected liveness seam at all**, so the real
  `DefaultProcessIsAlive` runs. Start a real short-lived child process, put its identifier on a test
  session, and assert the verdict. A definitely-dead identifier must produce `Gone`.
- There must be a test that exercises the real method's **`Unreadable`** answer - a process
  identifier the operating system will expose while `HasExited` throws. The inspection did exactly
  this with a `Win32Exception`; find the same shape. If this machine cannot produce that state, the
  test **fails loudly with a message saying why**, in the manner of
  `PathContainmentLinkEscapeTests` - it does not skip into a false green, and it does not get
  replaced by an injected substitute.
- **The proof that this is real coverage: replace the body of the production liveness method with a
  constant and watch tests go red.** Do that for each of the three answers and record the exact red
  message. If a mutation leaves the suite green, you have not finished.
- Every other fix likewise gets a mutation, run, with the red message recorded as it printed.

## What you must NOT do

- Do not add a fifth verdict word.
- Do not touch `GatewayEndpoints.cs` - another Worker is in that file this phase. If your change
  needs something from it, tell your manager.
- Do not touch anything under `apps/`, `packages/` or `tools/cc-devthrottle`.
- Do not run `taskkill` or `Stop-Process` against anything you did not start. The test child
  processes you start yourself are yours; nothing else on this machine is.
- No abbreviations in anything you write. No mention of any assistant, vendor or model in any commit
  message, comment or document.

## What to run before you report

- The projects your change touches, to completion, with the numbers.
- `.\scripts\test-local.ps1` in full - and tell your manager what it printed, not what you expect.
  **Never write a result row before the run that fills it.** Two phases of this mission have already
  been caught doing exactly that.

## What to hand back

Commit and push your work on the branch as you go. Write your notes to
`missions/stop-a-session/worker-f-notes.md`: what you changed, the mutation table with the red
messages as they actually printed, the suite numbers you actually ran, and - named honestly - what
your tests still do not cover. Then send your manager (`e7ea69df`) ONE single-line message saying
you are done and pointing at that file. Fleet messages truncate at the first newline.

## Pushing, when you are not the only seat on this branch

Four Workers and a QA seat are all committing to `mission/stop-a-session`. Before every push, run
`git fetch origin` and then `git rebase origin/mission/stop-a-session`. **Never force-push** - a
force-push on this branch deletes somebody else's commit, and it has already happened once this
phase. If a rebase conflicts in a file you were told not to touch, stop and tell your manager rather
than resolving it.
