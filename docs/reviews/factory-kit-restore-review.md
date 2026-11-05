# Review: three fixes to tests that fail on main (4 November 2026)

Reviewer: a separate review session (Factory Inbox - Reviewer - three main-branch fixes), in the detached
worktree D:\ReposFred\devthrottle-fi-mainfix-review. Nothing was committed or pushed; every mutation made for a
proof was restored with `git checkout --` in the same command and the tree was verified clean afterwards.

## Scope

- Read each branch as `git diff origin/main...origin/<branch>` and read the surrounding code on the branch tip.
- Built and ran the tests each branch names, on Windows, with `-p:NuGetAudit=false` on the command line only.
- Branch 1: ran the setup-engine Mac tests on the branch and on main.
- Branch 2: ran the test class on the branch; ran a revert proof (new product hunk removed, tests rerun, file
  restored); read the Session's retry guard.
- Branch 3: reproduced the race out of tree with a scratch program against the branch's own ProcessRunner, read
  the .NET 10 runtime source for the wait it uses, measured a kernel-object wait patched in locally (then restored),
  and ran the failing test on the branch with the real git and with a sleeping git first on PATH.
- Not done: no run on macOS or Linux; no run of the full parked suites; no review of code outside the diffs
  beyond what the diffs call.

Machine during the review: 63.8 GB total, 9.7 GB free, processor load about 31 percent, 804 processes. Not short
of memory by the product's own thresholds (Tight is under 2 GB free).

---

## Branch 1: fix/mac-app-name-tests (36a0703e0)

**Verdict: approve. No finding.**

What it does: a `MacPath` helper (DirectoryOf, FileNameOf, FileUrlOf) reads Mac paths by forward-slash rules, and
the four pure Mac decisions (relocation decision, Dock address and check, Setup's last-screen sentence) use it
instead of `System.IO.Path` and `new Uri(path)`. Code that touches the disk is unchanged. Nineteen MacPath tests
are added.

Evidence:

| run | result |
|---|---|
| branch, setup-engine Mac tests (MacPath, MacAppNameAndPlace, MacDock, MacDirectorRelocation, InstallCompletion) | 70 passed, 0 failed |
| main, the same filter without the new MacPath class | 41 passed, 10 failed |

So the ten reds on Windows are real and the branch removes them with product code, not with a bent test.

Checked for behaviour change on a Mac (where the old code was correct):

- `DirectoryOf` equals `Path.GetDirectoryName` on a Mac for every input the callers pass: a bundle path with
  its trailing slash trimmed, a bundle at the root (`/Director.app` gives `/`), a bare name (gives empty), and
  a path with a trailing slash (both give the path minus the slash). The one divergence is the input `/`
  itself (helper gives `/`, Path gives null); no caller can pass it, since every caller passes a bundle path.
- `FileNameOf` equals `Path.GetFileName` on a Mac, including a trailing slash (both give empty).
- `FileUrlOf` through `UriBuilder` escapes a space as `%20` and `#` as `%23`, which is what `new Uri(path)`
  gives on a Mac; the MacDock tests that carry the expected Dock strings pass unchanged.

Observation, not a finding: `MacPath` is `internal` and the test project reaches it, so the engine already
exposes internals to its tests. Fine.

---

## Branch 2: fix/busy-agent-send-test (c1179fb8a)

**Verdict: approve. No finding. The owner's draft is safer after this change than before it, and no path in
the new code can clear, duplicate or interleave it.**

What it does: in `ResolveRetainedComposerAsync`, when the observation of an old mark answers Unknown and the
composer region holds text of which the marked text contains no copy, the send does not clear and types after
that text. A per-flow memory pin (`PinMemoryForThisFlow`, an AsyncLocal like the send's clock) lets the tests
fix the memory reading for one send only; every send in the class is pinned healthy and a new test pins a
starved machine.

Evidence:

| run | result |
|---|---|
| branch, BusyAgentSendTests | 14 passed |
| branch with the new product hunk removed (18 lines), same tests | 13 passed, 1 failed: the new starved-machine test, typed 0 times (Expected 1, Actual 0), the recorded failure shape |

The revert proof also shows the pin reaches the send: had the AsyncLocal not flowed into `Session.SendTextAsync`,
the starved test would have read this healthy machine and passed with the hunk removed.

Checked hard, as asked, for a path that clears, duplicates or interleaves the owner's text:

- Clear: the new branch returns before any clear. It is the only change to the decision, and it can only turn a
  former clear into a no-clear. Nothing new clears.
- Where it fires: `ObserveComposerAsync` with a readable region answers Unknown only when (a) both samples
  were read, neither showed the marked text (by containment, interleaved, or visible tail) and the machine is
  under pressure, or (b) a sample could not be read. Then the third read must be non-empty and not a substring
  of the marked text. A prefix, a middle, or a tail of the marked text IS a substring, so a partially drawn
  mark still goes down the old path (clear, confirm empty). Only text that cannot be the mark drawing late is
  treated as the owner's.
- Same screen, same decision as a healthy machine: for the identical readings, a healthy machine answers
  Absent and the existing else branch types after the text. The branch does not invent a new outcome; it
  removes the pressure-only divergence.
- Both strings compared are in the same alphabet: `regionHolds` comes from `ReadRegionText`, which applies
  `NormalizeForEcho`, and `retainedNeedle` is `NormalizeForEcho(retained)`.
- Duplicate: after the branch returns, the send types a space and the text once; the echo check looks for the
  text inside the region. A failure is `ComposerNotAcceptingInputException` with `TypedAfterExistingText`,
  and `Session.cs` (line 4849 on the branch) guards its retry-with-clear with `!ex.TypedAfterExistingText`.
  The test asserts typed once and zero clear keys with the typed text never drawn.
- Interleave with the owner's keystrokes: unchanged. The clear keys still go through `ClearKeysWriter`, and
  this branch presses none.

Observations, not findings (no harm proven):

1. The third read (`regionHolds`) is checked only by containment, not by `RegionShowsText`, so a frame that
   shows the whole mark with a footer hint interleaved (the issue #1592 shape) would read as "someone else's
   text" and be typed after. The two sampled reads do check for interleaving, so this needs one unreadable
   sample plus an interleaved third frame. The existing Absent path has the same exposure already. A one-line
   tightening is available if wanted: also require `!RegionShowsText(regionHolds, retainedNeedle,
   VisibleTailNeedle(retainedNeedle))`.
2. The comment on the pin says the Director never sets it; the public `MemoryProbe` setter remains for the
   process-wide reading. Keep it that way.

---

## Branch 3: fix/factory-kit-restore-test (c788f983e)

**Verdict: do not merge as is. The wait it adds waits for nothing in the case it was written for, and the test
it names still fails on the branch on this machine, with the real git and with a sleeping git.**

What it does: in `ProcessRunner.RunAsync`, after `TryKillTree(proc)` on cancellation, it awaits
`proc.WaitForExitAsync` for up to 5 seconds (`KilledExitLimit`), logs if the child outlives that, and returns.
No test is added or changed.

### Finding 1 (proven): `Process.WaitForExitAsync` returns before a killed child has released its folder

The .NET 10 runtime (release/10.0, `Process.cs` and `ProcessManager.Windows.cs`) does this:

- `WaitForExitAsync` checks `HasExited` and, when true, skips the wait on the exit event entirely.
- On Windows `HasExited` is true as soon as `GetExitCodeProcess` reports a code other than STILL_ACTIVE. The
  runtime's own comment on `ProcessManager.HasExited` says this "is the wrong way to check whether the process
  has exited" and is kept for compatibility; the "more correct check" (the process handle being signalled)
  only runs when the exit code is still STILL_ACTIVE.
- `TerminateProcess` sets the exit code at once and tears the process down afterwards. Its working-directory
  handle is released in that teardown. So after `Kill`, `WaitForExitAsync` is a no-op in practice.

The synchronous `Process.WaitForExit(int)` goes straight to `ProcessWaitHandle.WaitOne(milliseconds)` on the
process handle, which is the kernel object. That is the wait this change needs.

Measured, out of tree (scratch program in the review's scratchpad; a child with the folder as its working
directory is started, cancelled at 300 ms, and the folder is deleted at once, as the stop verb does):

| ProcessRunner used | child | rounds | folder delete failed |
|---|---|---|---|
| main (687e3c3af) | ping, 11 pings | 20 | 17 |
| branch (c788f983e) | ping | 40 | 32 |
| branch (c788f983e) | ping, rerun | 20 | 11 |
| branch (c788f983e) | python sleeping 10 s (the author's shape) | 20 | 19 |
| branch with the wait replaced locally by `Task.Run(() => proc.WaitForExit(5000))`, then restored | python sleeping 10 s | 20 | 0 |
| same local patch | ping | 40 | 1 (the one round the child outlived 5 s) |

Raw semantics without product code, same shape, 20 rounds each: no wait 15 failed; `WaitForExitAsync` 7
failed, returning 30 to 222 ms after the kill; synchronous `WaitForExit()` 0 failed. Awaiting the pipes to EOF
also gives 0, but ProcessRunner passes the cancelled token to its `ReadToEndAsync` calls, so on cancellation
those reads abort instead of waiting for EOF; the pipe drain the catch block awaits therefore waits for nothing
either.

Every scratch folder the failed rounds left behind was removed after the runs.

> **Answer: ACCEPTED.** `ProcessRunner.WaitForKilledExitAsync` now runs the kernel wait,
> `Task.Run(() => proc.WaitForExit(5000))`, on the thread pool, and logs when the child outlives the limit - the
> patch measured above. The comment on the method records why the asynchronous wait is wrong after a kill.

### Finding 2 (proven): the named test still fails on the branch on this machine

Gateway.UnitTests built on c788f983e, the one test
`FactoryKitSurvivesARestoreTests.AnEndThatIsNotForARestore_DeletesTheKitFolder` run with `--no-build`:

| condition | result |
|---|---|
| a git that sleeps 10 s first on PATH (the author's reproduction) | FAILED, Assert.False, the folder still exists, 12 s |
| the real git (Git for Windows), run 1 | FAILED, 3 s |
| the real git, run 2 | FAILED, 7 s |
| the real git, run 3 | FAILED, 5 s |

Why the real git fails here: on this machine during the review, `git --version` took 5.5, 6.2 and 10.0
seconds, and `git status --porcelain=v1 -u` in an empty temp folder took 10.7, 4.4 and 3.8 seconds (user plus
system time under 0.1 s, so the time is spent waiting, not working). The probe's budget is 3 seconds
(`WorktreeProbeTimeout`), so the probe is cancelled on most stops here, and the race follows every time. The
machine was not short of memory (9.7 GB free), so the "starved machine" in the commit message is one cause of a
slow git, not the only one; a slow git start is enough.

So the claim in the commit message that "the same test then passes" did not hold here, in four of four runs.

> **Answer: ACCEPTED.** Fixed at the source (item 2 below): the stop of a seat in its kit work folder runs no
> worktree probe, so no git runs on that path at all. Evidence on the branch, the whole class
> (`FactoryKitSurvivesARestoreTests`, now 11 tests): with the real git, 11 passed; with a git that sleeps 10 s
> first on PATH, 11 passed; five more runs with the real git, 11 passed each time. The earlier claim was wrong:
> I ran the slow-git check once, it passed, and I took one run for proof.

### The two questions asked

**What happens if the child does not exit in 5 seconds.** `WaitForKilledExitAsync` logs one line and returns;
the `OperationCanceledException` is rethrown; the probe's catch in `SessionCommandExecutor` logs "did not
answer" and returns null (unknown); the stop goes on; `RemoveSession` calls `FactoryRunKit.Delete`, which fails
and logs "Delete FAILED" without throwing; the folder stays until the start-up sweep (`FactoryRunSweep`) deletes
it once nothing names it. That is exactly main's behaviour plus one log line. Also note the cost in the stuck
case: a stop whose probe timed out now takes up to 8 seconds (3 budget plus 5 wait) before the stop itself
begins, where the probe's own comment says "a git call must never be able to hold [a stop] up". With a correct
kernel-object wait the measured cancelled call took 0.75 to 10.8 seconds here (that includes the process-tree
enumeration `Kill(entireProcessTree: true)` does).

**Should the silent delete failure have been made loud.** It is logged, so it is not silent in the log, but it
is silent to the caller and to the person: the stop reports success and the folder stays. Under the no-fallback
rule the fact should travel with the result, the way the dirty-worktree sentence does: `RemoveSession` returns
true today whether or not the kit folder went. Throwing from `RemoveSession` would be wrong, because the row is
already out of the roster when the delete runs; carrying "kit folder kept: could not be removed: <reason>" on
the stop's result (and so into the trail the person reads) is the honest shape. That is a separate change from
this branch and does not fix the race; it reports it.

### What to do instead

1. Replace the wait with the kernel-object wait, bounded:
   `var gone = await Task.Run(() => proc.WaitForExit((int)KilledExitLimit.TotalMilliseconds)).ConfigureAwait(false);`
   and log when `gone` is false. This is the patch measured above (0 of 20 and 1 of 40).

   > **Answer: ACCEPTED** - done as written; see Finding 1. Kept even with item 2 in place, because every other
   > caller that cancels a child and then touches its folder has the same race.
2. Consider removing the probe from the race entirely for a factory home seat: its working folder is the kit's
   work folder, which the Director itself deletes at the end, so the question "did that worktree have
   uncommitted changes" is meaningless there. Skipping the probe when `WorkingDirectory` is the kit's work
   folder removes the race at its source and keeps the stop fast. This is a design choice for the owner.

   > **Answer: ACCEPTED**, and flagged to the Delivery Lead as the design choice it is. `SessionCommandExecutor.KillAsync`
   > now treats a working folder equal to the session's own `FactoryRunKit.WorkDirectoryOf(FactoryKitDirectory)` as no
   > worktree: no probe, and the stop result carries no worktree path and no uncommitted-changes answer, exactly as for a
   > session with no working folder. Any other folder is probed as before. Tests:
   > `TheStopOfASeatInItsKitWorkFolder_RunsNoWorktreeProbe_AndDeletesTheKitFolder` (a counting fake probe is never
   > asked, the row is gone, the folder is gone) and `TheStopOfASessionInAnOrdinaryFolder_StillProbesThatFolder`.
   > Revert proof: with the skip disabled and the rest kept, the first of those failed (1 failed, 10 passed); restored.
3. The test depends on a real `git` on PATH, through the stop verb's probe, and on this machine that git takes
   4 to 13 seconds. By the owner's test standard a gate test must not let a real process decide whether it
   passes. `KillAsync` already has a `worktreeProbe` injection seam; the drain control's `EndAsync` path should
   be given a fake probe in this test (or the probe skipped per item 2). Until then the test is red on this
   machine on main and on the branch alike.

   > **Answer: ACCEPTED.** With item 2 the existing test reaches no git: its seat runs in its kit work folder, so the
   > stop it drives through `EndAsync` never starts the probe. The two new tests drive `KillAsync` directly with a fake
   > counting probe through its `worktreeProbe` seam. No test in the class starts a real process any more.
4. Carry the delete failure on the stop's result (see the second question above).

   > **Answer: DECLINED for this branch, proposed as its own change.** I agree with it, and so does the review: "that
   > is a separate change from this branch and does not fix the race; it reports it." It changes what
   > `RemoveSession` returns and adds a field to the stop result that the Gateway and both clients carry, so it needs
   > its own review against the contracts. With items 1 and 2 in, the race that made the folder stay is gone; the
   > remaining way a delete fails (a file held by something outside the Director) is still logged and still swept
   > at the next start-up. Asked the Delivery Lead to open it as a separate task.

---

## Answers to the observations on branch 2 (not asked, recorded for completeness)

Observation 1 (the third read checked by containment only) and observation 2 (the pin's comment) need no change on
that branch; branch 2 was approved and is not touched here.
