# Worker F - "could not be determined" is never "gone" (inspection 1, findings I2 and I3)

Phase C. Branch `mission/stop-a-session`. Findings **I2** and **I3** only; nothing else was touched.

---

## What the product now does

**Liveness has three answers, not two.** `SessionCommandExecutor.DefaultProcessIsAlive` returned a plain
`bool` and folded every exception into `false`, so a process this machine may not open - one whose
`HasExited` throws - was reported as a process that had gone. That single `false` did two separate pieces
of damage: before the stop it produced the verdict `alreadyStopped` and cleared the row, and after the stop
it certified `ProcessEnded = true`. An access failure establishes neither fact.

It is now `DefaultProcessLiveness`, returning `Alive`, `Gone` or `Unreadable` and carrying the machine's own
words about the failure (`src/CcDirector.ControlApi/ProcessLiveness.cs`). `ArgumentException` from
`Process.GetProcessById` is the one real `Gone` - the operating system enumerated its process identifiers
and that one is not among them. Everything else is `Unreadable`.

**What an operator sees now, in the three cases the mission cares about:**

| Case | Before | Now |
|---|---|---|
| A live process whose liveness cannot be read | `already stopped ... no process was running` | `stopped <id> - whether process 51884 was running could not be read before the stop - could not read process 51884 on this machine (Win32Exception: Access is denied)` |
| A session carrying no process identifier at all (the remote workflow backend while a run is going; the pipe and studio backends always) | `already stopped ... no process was running` | `stopped <id> - this session carried no process identifier, so no process could be checked - whether anything was running, and whether anything has ended, are not known` |
| A remote run that refused to cancel | `already stopped`, and the row removed | a FAILURE: `the stop was refused by the session's own backend: run 4711 on owner/repo would not cancel: 403 Forbidden. It may still be running on GitHub.` - and **the row is left in place** so the operator can see the session and try again |

No fifth verdict word was added. `stoppedNotDescribed` is reused, exactly as the Architect ruled.

---

## What changed, file by file

**`src/CcDirector.ControlApi/ProcessLiveness.cs`** (new). The `ProcessLiveness` enum and the
`ProcessLivenessReading` record struct that carries the machine's words alongside the answer.

**`src/CcDirector.ControlApi/SessionCommandExecutor.cs`**

- `DefaultProcessIsAlive` -> `DefaultProcessLiveness`, three-valued, carrying the exception's own words.
- The seam changed with it: `Func<int, bool>? processIsAlive` -> `Func<int, ProcessLivenessReading>? processLiveness`.
- `ProcessIsGoneAsync` -> `WaitForProcessToGoAsync`, which answers with the reading rather than a boolean.
  An unreadable answer returns at once instead of being polled on: the failure that produces it does not
  clear inside a one-second window, and waiting on it would only delay the stop.
- The no-process-identifier case is now handled explicitly rather than falling through the check. This is
  the sentence I deleted, because it was the false premise under I3: *"The process id is
  backend-independent, and it is the same fact the mission report has to photograph."* It is not
  backend-independent - three of the five backends report zero.
- A new branch reads `session.Backend.LastShutdownFailure` after the kill and before the row is removed. A
  backend that reported its own shutdown failure is Ruling 3's third failure reached by another road, and
  the row is left in place there for the same reason it is left on "the process would not die".
- Only a process ESTABLISHED alive can be established to have ended, so an unreadable reading before the
  stop skips the re-check; an unreadable reading after it is neither "ended" nor "would not die".

**`src/CcDirector.Core/Backends/ISessionBackend.cs`** - a default-implemented `string? LastShutdownFailure
=> null`. Default null, so no other backend changes.

**`src/CcDirector.Core/Backends/GitHubActionsBackend.cs`** - `GracefulShutdownAsync` still does not throw
(callers depend on that), but a refused `CancelRunAsync` now sets `LastShutdownFailure` instead of only
writing to the terminal buffer.

**`src/CcDirector.Gateway.Contracts/SessionStopDtos.cs`** - `DirectorStopResult.NotDescribedReason` added.
The `StoppedNotDescribed` doc comment used to say *"Every description field is meaningless under this
verdict"*; that is now true only of the older-Director cause, and the comment says so per cause. The two
"three verdicts" counts were corrected to four (they had been wrong since the fourth word was added).

**`src/CcDirector.Gateway/Api/SessionStopFold.cs`** - `CanDescribe` now knows `stoppedNotDescribed` as a
word a CURRENT Director sends. Its doc comment said the opposite (*"a Director never sends it"*) and is
corrected. `Fold` tells the two causes apart by the Director's own sentence and carries through the facts
the stop did establish - the process identifier, `RowRemoved`, and the worktree line Ruling 2 requires,
including the dirty-tree sentence. Only `ProcessEnded` stays empty.

**Tests** - `SessionCommandExecutorLivenessTests.cs` (new, 6 tests), `SessionStopFoldTests.cs` (+4),
`GitHubActionsBackendTests.cs` (+2), `StubGitHubClient.cs` (a `CancelRunFailure` option),
`SessionCommandExecutorTests.cs` (the existing kill tests moved to the new seam signature - behaviour
unchanged).

---

## The standard: tests that run the PRODUCTION method

The inspection's most valuable line was that replacing the production liveness check with the constant
`false` left all 68 tests in `SessionCommandExecutorTests` passing, because every one injects its own
substitute. So the new file passes **no liveness seam at all** and uses **real child processes**:

- **Alive** - a real running child; the test backend really ends it on shutdown; the check reads it running
  before and gone after, which is the only combination that may be called `stopped`.
- **Gone** - a real child that really exited, whose identifier the operating system no longer lists.
- **Unreadable** - a real, still-running child whose own access control list is replaced with one that
  denies everyone, using the full-access handle this process got when it created the child. `GetProcessById`
  still finds it; `HasExited` throws `Win32Exception: Access is denied`. That is the exact state the
  inspection reproduced. **A host that cannot produce it fails the test loudly with the reason** - it does
  not skip, and it is not replaced by an injected substitute. The test asserts at the end that the process
  really is still running, which is what made the old answer a false report.

Nothing here touches a process it did not start. Each child is a thirty-second `ping` that would exit on its
own even if a test were killed mid-run.

---

## Mutation table - every red message exactly as it printed

Run in an ISOLATED copy of the tree (`git archive HEAD` into a scratch directory), not in the shared
worktree - four other seats build and test in `C:\ReposFred\devthrottle-stop-a-session`, and mutating shared
source while another seat's run is compiling would corrupt that seat's evidence. One attempt was made in the
shared tree first; it failed on a locked output file held by another seat's live `testhost`, which is what
prompted the move. No process was killed.

| # | Mutation | Result | Red message, verbatim |
|---|---|---|---|
| 1 | `DefaultProcessLiveness` body -> `return ProcessLivenessReading.IsGone;` (the inspection's own mutation: every read collapses to "not alive") | **2 failed, 4 passed** | `Assert.Equal() Failure: Strings differ / Expected: "stopped" / Actual: "alreadyStopped"` and `Assert.NotEqual() Failure: Strings are equal / Expected: Not "alreadyStopped" / Actual: "alreadyStopped"` |
| 2 | `DefaultProcessLiveness` body -> `return ProcessLivenessReading.IsAlive;` | **3 failed, 3 passed** | `Assert.Equal() Failure: Values differ / Expected: Ok / Actual: Error` (three times - every stop becomes "the process would not die") |
| 3 | `DefaultProcessLiveness` body -> `return ProcessLivenessReading.CouldNotRead("mutation");` | **2 failed, 4 passed** | `Assert.Equal() Failure: Strings differ / Expected: "stopped" / Actual: "stoppedNotDescribed"` and `Assert.Equal() Failure: Strings differ / Expected: "alreadyStopped" / Actual: "stoppedNotDescribed"` |
| 4 | A session with no process identifier goes back to being an established absence (`before = IsGone`, no sentence) | **1 failed, 5 passed** | `Assert.NotEqual() Failure: Strings are equal / Expected: Not "alreadyStopped" / Actual: "alreadyStopped"` |
| 5 | The executor stops reading `session.Backend.LastShutdownFailure` | **1 failed, 5 passed** | `Assert.Equal() Failure: Values differ / Expected: Error / Actual: Ok` |
| 6 | `GitHubActionsBackend` goes back to swallowing a refused cancellation | **1 failed, 10 passed** | `Assert.NotNull() Failure: Value is null` |
| 7 | The fold stops telling the two causes apart and prints the older-Director sentence over both | **3 failed, 28 passed** | `Assert.Equal() Failure: Strings differ / Expected: "stopped 9c41e7a2 - whether process 51884 "··· / Actual: "stopped 9c41e7a2 - the Director on that m"···`; `Expected: ···"topped 9c41e7a2 - this session carried no"··· / Actual: ···"topped 9c41e7a2 - the Director on that ma"···`; and `Assert.Equal() Failure: Values differ / Expected: 51884 / Actual: null` |
| 8 | An unreadable check AFTER the stop certifies that the process ended | **1 failed, 5 passed** | `Assert.Equal() Failure: Strings differ / Expected: "stoppedNotDescribed" / Actual: "stopped"` |

Every mutation was restored and the isolated copy deleted afterwards. The shared worktree was verified clean
(`git status` empty) before and after.

---

## Suites actually run

Each row says which code it ran against, because two of them are older than the final code and saying so is
the point.

| Run | Against | What it printed |
|---|---|---|
| `dotnet test src/CcDirector.Gateway.UnitTests` (whole project) | the code before the fixture fix | `Failed: 0, Passed: 4233, Skipped: 8, Total: 4241, Duration: 3 m 48 s` |
| `dotnet test src/CcDirector.Core.Tests` (whole project, PARKED suite) | the code before the fixture fix | `Failed: 0, Passed: 4396, Skipped: 8, Total: 4404, Duration: 10 m 58 s` |
| The six new liveness tests, in an isolated copy | the FINAL code | `Failed: 0, Passed: 6, Skipped: 0, Total: 6` |

Those first two rows are honest but stale: the fixture fix came after them, and neither has been re-run on
the final code by me. The fix touches one test file and nothing in the product, so I do not expect it to
move either number - but expecting is not running, and the Manager's gate run is what settles it.

**I did not run `.\scripts\test-local.ps1 -Parked` to completion on the final code, and there is no number
from me for it.** The Manager is running it once, on the final code, and that run is the one to believe.

I did start a `-Parked` run earlier, on the code before the fixture fix described below. It got through ten
of the eleven suites and I stopped it when the Manager said he was running the gate himself, so it never
produced a gate verdict. What it did produce is worth having, because it is what caught my own defect:

| Suite in that partial, superseded run | What it printed |
|---|---|
| `CcDirector.Core.UnitTests` | `Failed: 0, Passed: 227, Skipped: 0, Total: 227` |
| `CcDirector.Gateway.UnitTests` | `Failed: 2, Passed: 4231, Skipped: 8, Total: 4241` - **both failures mine**, see below |
| `CcDirector.Avalonia.Tests` | `Failed: 0, Passed: 421, Skipped: 0, Total: 421` |
| `CcDirector.Engine.Tests` | `Failed: 0, Passed: 63, Skipped: 0, Total: 63` |
| `CcDirector.HostedAgent.Tests` | `Failed: 0, Passed: 88, Skipped: 0, Total: 88` |
| `CcDirector.Launcher.Tests` | `Failed: 0, Passed: 191, Skipped: 0, Total: 191` |
| `CcDirector.Terminal.Avalonia.Tests` | `Failed: 0, Passed: 25, Skipped: 0, Total: 25` |
| `cc-director-setup.Tests` | `Failed: 0, Passed: 25, Skipped: 0, Total: 25` |
| `cc-director-setup-engine.Tests` | `Failed: 0, Passed: 541, Skipped: 0, Total: 541` |
| `CcDirector.Core.Tests` | `Failed: 0, Passed: 4396, Skipped: 8, Total: 4404` |
| `CcDirector.Gateway.Tests` | never ran - still queued behind another seat's hold on the machine-wide lock when I stopped the run |

---

## The gate caught a defect in MY OWN test fixture, and it was this mission's own failure shape

The two failures above were `Kill_WithNoInjectedCheck_ReadsARealLiveProcessAndReportsItStopped`
(`Expected: "stopped" / Actual: "alreadyStopped"`) and
`Kill_WithNoInjectedCheck_WillNotCallAnUnreadableLiveProcessAlreadyStopped`
(`Expected: Not "alreadyStopped" / Actual: "alreadyStopped"`).

**The production code was not at fault. The fixture was**, in two ways:

1. **The child process expired underneath the test.** The sleeper ran about twenty-nine seconds. Alone that
   was ample - these tests passed every run, including ten-plus times across the mutation work. Inside the
   gate, with eleven test projects competing for the machine, they reached their assertion after the child
   had already exited, so the production check read a genuinely dead process and answered `alreadyStopped` -
   a true answer about the wrong thing. It is now ten minutes, still bounded so a killed test host cannot
   orphan anything, and each test asserts the child is still running at the moment the stop is asked for.
2. **The fixture's proof that it had built the unreadable state was an ABSENCE.** It asked only whether
   `OpenProcess` had failed - and `OpenProcess` fails on an exited process too, so a child that had quietly
   died read as "successfully closed off" and the test carried on against a corpse. It now demands the state
   be PRESENT: the open must fail specifically with access denied, and
   `Process.GetProcessById(...).HasExited` must actually throw `Win32Exception`. A dead child, or a host that
   cannot produce the state, now fails saying which.

That second one is the same defect this mission exists to remove - a check whose pass condition an absence
satisfies - reproduced inside my own test for it.

**Watched failing.** With the child replaced by one that exits immediately, the first test now stops with

    The child process this test started (23944) had already exited when the stop was about to be asked
    for, so the state this test is about was never set up. This is a fault in the test fixture, not a
    verdict on the production liveness check.

rather than with a misleading assertion about the verdict. With the real fixture the six tests pass
(`Failed: 0, Passed: 6, Skipped: 0, Total: 6`), verified in an isolated copy of the tree so as not to
disturb another seat's run.

**The mutation table above still stands.** Every mutation was run in the isolated copy against the same
production code, and none of them depended on the fixture's timing - the eight red results are unaffected by
this fix.

---

## What this does NOT cover - named honestly

- **The `Unreadable` answer is proven on Windows only.** It is produced with Windows process security, and
  on a non-Windows host the test FAILS with a message saying the answer is unproven there rather than
  skipping. The .NET suites only run on `windows-latest` in continuous integration, so nothing is currently
  red because of it - but the production `DefaultProcessLiveness` is not proven to reach `Unreadable` on
  Linux or macOS at all, and I did not investigate what makes `HasExited` throw there.
- **The unreadable state is produced by locking a process's own access control list**, not by the failure a
  real Director would hit (a protected system process, or a process owned by another user). No process on
  this machine makes `HasExited` throw naturally - I scanned every one of them - so the state had to be
  manufactured. The exception that reaches `DefaultProcessLiveness` is the same one either way, but the
  route to it is not the production route.
- **Pre-stop unreadable skips the post-stop re-check.** So the case "the check was unreadable before the
  stop and the process is definitely still alive after it" reports `stoppedNotDescribed` rather than "the
  process would not die", and the row IS removed. I took that deliberately: `stopped` and `alreadyStopped`
  both claim something nothing established, and in practice the failure that makes a process unreadable
  (access denied) is still there after the kill. It is a judgement, not a proven behaviour.
- **The row is removed on the unreadable path.** That follows the ruling ("what was established is still
  reported", and removal is established), but it means a live-but-unreadable process can lose its row. The
  operator is told so in words; nothing hunts for the process afterwards.
- **`CcDirector.Gateway.Tests` was never run by me at all.** It is the parked suite holding
  `SessionStopEndpointTests`, and every attempt queued behind another seat's hold on the machine-wide lock.
  Its two `stoppedNotDescribed` cases use a legacy `{killed, removed}` answer, so they exercise the
  older-Director branch, which this change leaves untouched - but that is reasoning, not a run.
- **No end-to-end proof through the Gateway route or a client.** These tests exercise the executor and the
  fold directly. `SessionStopEndpointTests` (parked) covers the route with controlled answers; nothing here
  proves a real Director sending `stoppedNotDescribed` over a real tunnel to a real command line.
- **The GitHub backend change is proven against the stub client only.** No real workflow run was cancelled
  or refused.
- **`ISessionBackend.LastShutdownFailure` is proven on `GitHubActionsBackend` and on a test backend.** The
  ConPty, Unix pty, pipe and studio backends inherit the default `null` and are unchanged - and untested for
  it, because there is nothing new to test.

---

## Three things for the Manager

1. **`GatewayEndpoints.cs:2053` logs the wrong cause now.** When `CanDescribe` is false it writes
   `the Director on <machine> could not describe the stop (older version?)`. With this change that line will
   also print for a CURRENT Director that could not read a liveness check, which is not an older version. It
   is a log line only, and I was told not to touch that file, so I have not. One sentence to fix.
2. **`tools/cc-devthrottle/src/session_ops.py:775` says "EXIT ZERO FOR ALL THREE VERDICTS ... stopped,
   alreadyStopped and notOnFleet".** The behaviour is already right - the command line exits zero on any
   200 and prints the Gateway's headline verbatim, so a `stoppedNotDescribed` from a Director works today -
   but the docstring now undercounts. Worker I's file; I did not touch it.
3. **`missions/stop-a-session.html`, Ruling 3, says "There are TWO causes" for `stopped, not
   described`.** With I3 there are arguably three - an older Director, a liveness check that threw, and a
   session with no process identifier to check. The third reads as a sub-case of "could not read whether a
   process was alive", so the sentence is not wrong; the code comment in `SessionStopDtos.cs` lists all
   three because the fields differ per cause. The Architect owns that document, so I have not touched it.
4. **Five seats in one worktree is costing real time.** My work was swept into commit `a6c4c28c`, whose
   message is about documentation, by another seat's `git add -A`; the branch tip then did not build for a
   while because a third seat's file went in mid-edit; and my first mutation run died on a build output file
   locked by a fourth seat's live test host. Nothing was lost and I killed nothing, but the mutation runs had
   to move to an isolated copy of the tree to be safe to do at all.
