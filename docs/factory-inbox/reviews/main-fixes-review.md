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
2. Consider removing the probe from the race entirely for a factory home seat: its working folder is the kit's
   work folder, which the Director itself deletes at the end, so the question "did that worktree have
   uncommitted changes" is meaningless there. Skipping the probe when `WorkingDirectory` is the kit's work
   folder removes the race at its source and keeps the stop fast. This is a design choice for the owner.
3. The test depends on a real `git` on PATH, through the stop verb's probe, and on this machine that git takes
   4 to 13 seconds. By the owner's test standard a gate test must not let a real process decide whether it
   passes. `KillAsync` already has a `worktreeProbe` injection seam; the drain control's `EndAsync` path should
   be given a fake probe in this test (or the probe skipped per item 2). Until then the test is red on this
   machine on main and on the branch alike.
4. Carry the delete failure on the stop's result (see the second question above).

---

## Re-review of branch 3: fix/factory-kit-restore-test at 9d7e90ecf (5 November 2026)

**Verdict: approve. Both findings are fixed with product code, the test that was red is green with the real git
and with a sleeping git, and the revert proof shows the new test watches the new code.**

### Scope of the re-review

- Read `git diff origin/main...origin/fix/factory-kit-restore-test` at 9d7e90ecf and the author's answers in
  `docs/reviews/factory-kit-restore-review.md` on the branch.
- Main has moved three commits past the branch's base (licenses, wingman); none touches a file the branch changes.
- Built Gateway.UnitTests on the branch tip in the detached worktree, ran the whole `FactoryKitSurvivesARestoreTests`
  class, reran the out-of-tree race reproduction against the branch's own ProcessRunner, and ran one revert proof
  (the probe skip disabled, the kernel wait kept). The mutation was restored with `git checkout --` in the same
  command and the test project rebuilt from the restored source; the tree is clean.
- Not done: no macOS or Linux run; no full parked suite; the branch was not edited.

### What the second commit does

1. `ProcessRunner.WaitForKilledExitAsync` now runs `Task.Run(() => proc.WaitForExit(5000))`, the kernel-object wait
   measured in the first review, and logs when the child outlives the limit.
2. `SessionCommandExecutor.KillAsync` runs no worktree probe when the session's working folder is its own kit work
   folder (`FactoryRunKit.WorkDirectoryOf(session.FactoryKitDirectory)`); the stop result then carries no worktree
   path and no uncommitted-changes answer, as for a session with no working folder. Any other folder is probed.
3. Two tests drive `KillAsync` with a counting fake probe: a home seat is never probed and its folder is gone; a
   session in an ordinary folder is probed.
4. The review file with the author's answers is committed under `docs/reviews/`. It names the reviewer as a
   separate review session, carries no attribution, and is ASCII only.

### Finding 1 (kernel wait): fixed, measured

| ProcessRunner used | child | rounds | folder delete failed | longest cancelled call |
|---|---|---|---|---|
| branch 9d7e90ecf | python sleeping 10 s | 20 | 0 | 9.3 s |
| branch 9d7e90ecf | ping, 11 pings | 40 | 1 | 7.7 s |
| branch 9d7e90ecf, rerun | ping | 40 | 1 | 6.7 s |
| branch 9d7e90ecf, rerun with the failed round's duration printed | ping | 40 | 0 | 6.1 s |

Against 32 of 40, 11 of 20 and 19 of 20 on c788f983e. The two failed ping rounds in 120 came in the runs that did
not print the failed round's own duration; in each of those runs the longest cancelled call exceeded the 5 second
limit, which is the only path on which the branch's code returns while the child still holds the folder. That is the
likely cause, not an observed one. The third run, which prints the duration on a failure, had no failure to print.

### Finding 2 (the named test): fixed, measured

| condition | result |
|---|---|
| the real git, run 1 | 11 passed, 23 s |
| the real git, run 2 | 11 passed, 26 s |
| the real git, run 3 | 11 passed, 11 s |
| a git that sleeps 10 s first on PATH | 11 passed, 56 s |

Against 4 of 4 red on c788f983e. The named test itself now takes 15 to 16 ms, under either git, because the stop of
a home seat starts no git.

### Revert proof: the probe skip disabled, the kernel wait kept

| condition | result |
|---|---|
| the real git, run 1 | 1 failed, 10 passed |
| the real git, run 2 | 1 failed, 10 passed |
| a git that sleeps 10 s first on PATH | 1 failed, 10 passed |

The one red in every run is `TheStopOfASeatInItsKitWorkFolder_RunsNoWorktreeProbe_AndDeletesTheKitFolder`, with
`Assert.Empty() Failure: Collection was not empty` (the fake probe was asked). So the new test watches the skip.

The same proof shows something the author did not claim: with the kernel wait alone, the originally red test
`AnEndThatIsNotForARestore_DeletesTheKitFolder` passes with the real git and with the sleeping git. The kernel wait
is the fix of the race; the probe skip removes git from the stop of a home seat, keeps that stop fast, and removes
the sentence "the worktree <kit>/work was left untouched - whether it has uncommitted changes could not be
determined" that a timed-out probe would have put on the stop result about a folder the stop then deletes
(`SessionStopFold.AddDetails` adds no worktree sentence when the path is null).

### Checks on the skip

- Only a home seat runs in its kit work folder (`SessionManager.cs` line 891: `home ? WorkDirectoryOf(kit) :
  pooled?.Path ?? repoPath`), so a factory seat in a pooled worktree keeps its probe. The second new test covers the
  ordinary-folder side.
- The path comparison (ordinal, case-insensitive on Windows, trailing separators trimmed, full paths) matches the
  SessionManager's own `PathComparison` for kit folders.
- A session with a kit directory but no working folder, or no kit directory, is unchanged.

### The two questions, re-asked against the new code

- **Beyond 5 seconds:** the behaviour is as described in the first review (log line, probe answers unknown, stop goes
  on, delete fails and is logged, the folder stays until the start-up sweep). It is now reachable only for a child the
  operating system does not end within 5 seconds of `TerminateProcess`, and only on a stop whose probe ran, which no
  longer includes a home seat. The cost in that case stays at up to 8 seconds before the stop begins.
- **Loudness of the delete failure:** item 4 (carry the delete failure on the stop's result) is declined for this
  branch and proposed as its own change. I agree: it changes the `RemoveSession` return and the stop contract both
  clients carry, and it reports the race rather than fixing it.

### Observations, not findings (no wrong verdict proven)

1. The author's answer says "no test in the class starts a real process any more". Four tests in the class still
   start git, through code the branch does not touch: under the sleeping git they take 10 to 13 seconds each
   (`ASessionThatIsNotAHomeSeat_UNDEFINED_FACTORY_CarriesNoHome`,
   `ARestoreFromASeatWhoseFolderIsGone_GivesItAFreshKitAndAnEmptyWorkFolder`,
   `AHomeSeat_IsCarriedByThePushTheCaptureAndBothRestoreBuilders`,
   `ARestoredHomeSeat_EndedByTheSmartShutdownAndRestoredFromItsCapture_ResumesInItsFolderWithItsWork`) against 0.4 to
   6 seconds with the real git. They pass under either git, so the git does not decide the verdict; the claim in the
   answer is wrong as a statement and the class costs about a minute under a slow git. I did not trace which call in
   the create, capture or restore path starts git. Not this branch's defect; worth a note in the task that carries
   item 4.
2. `Task.Run` holds a thread-pool thread for up to 5 seconds per cancelled call. Acceptable for a stop; a caller that
   cancels many children at once would hold that many threads.

---

## Branch 4: fix/postgres-proof-tests (5669125d0) (5 November 2026)

**Verdict: approve. Both tests still prove what their names claim; only an input each test hands the product was
brought up to a product rule that changed after the test was written. No assertion was weakened, removed or moved.**

### Scope

- Read `git diff origin/main...origin/fix/postgres-proof-tests` (two test files, 10 lines added, 2 removed, no
  product code) and both test files in full on the branch tip.
- Ran the two tests on the branch and on origin/main (33a7c1ede) through `scripts\test-database.ps1`, which builds
  a throwaway PostgreSQL in Docker for the run and destroys it after; NuGetAudit was turned off for the run through
  the environment, not in any file.
- Read the product rules each fix follows on origin/main: `LicenseAlarmService.RetiredKinds` and `RaiseAsync`, and
  `FactoryRegistryStore.Register` with its `NoBossSeat` refusal.
- Not done: no run of the rest of the database project.

### Evidence

| run | result |
|---|---|
| branch 5669125d0, both tests | 2 passed, 0 failed, 0 skipped (RESULT: GREEN) |
| origin/main 33a7c1ede, both tests | 2 failed: `Assert.Single() Failure: The collection was empty`; `FactoryViewValidationException: Every factory has a Boss, and the manifest names no boss seat` |

The diff touches nothing but the two inputs below, so the main run is also the revert proof.

### Test 1: AddLicenseAlarmsPostgresTests

What changed: the alarm kind the test raises was `new LicenseAlarmKind("not-ready-at-80", ...)`, a kind the test
made up; it is now `LicenseCannotSwitchAlarm.Kind` (`cannot-switch-at-80`), a kind the service raises today.

Why it failed on main: `RaiseAsync` empties the wanted list for any key in `RetiredKinds` and reconciles against
nothing, so the first `Assert.Single(alarms.ListOpen(...))` saw an empty list. `not-ready-at-80` was added to
`RetiredKinds` on 19 October 2026 by pull request 4252, one day after the test's last edit (18 October, pull request
4228). The comment on the branch credits the retirement to pull request 4528; that pull request (27 October)
retired `not-ready-five-hour` and left `not-ready-at-80` where 4252 had put it. The comment's cause is right, its
pull request number is wrong; worth a one-word fix when the branch is next touched, not a reason to hold it.

Still proven, unchanged: the migration order and its application on PostgreSQL; the service opening a row, a log
row, one email with its time stored in a `timestamp with time zone` column, one push; the re-raise matched by the
byte-collated identity with the account in another case; the close with its log rows; the hold-down found from the
last close stored in PostgreSQL; the re-open after the hold-down; the Down removing the table with the log intact.
The kind's key is the only thing the service reads from the kind on this path, and every assertion is as before.

### Test 2: ArchiveFactoriesPostgresTests

What changed: the manifest registered mid-test (`warm-forward`) now names `BossSeat = "boss"` and carries a seat
with id `boss`, name `Boss` and role `Boss`, beside the CEO seat it had.

Why it failed on main: `FactoryRegistryStore.Register` refuses a manifest with no boss seat unless the stored row
is archived (`entity.BossSeat is null && existing?.ArchivedAtUtc is null`), landed on 21 October 2026 by pull
request 4347 for issue 4343; the test was written on 10 October. The added seat follows the store's own rule that
the boss seat's name and role are the word Boss, so the fix is the registration the product asks for, not a way
around the check. The row inserted before the migration (`machine-care`, no boss, by plain SQL) is left as it was,
which keeps the proof honest about a factory registered before the rule existed.

Still proven, unchanged: the migration order; the three nullable columns added without touching the earlier row;
list, register, archive (time, who, the schedule names), find and restore through the real store on the real
PostgreSQL path; the Down removing the columns with both rows intact.

### Observation for the Delivery Lead, not a finding

The owner's ruling of 13 October 2026 says a test that fails while the code is correct is deleted, not repaired.
These two failed on correct code, but not at random: each stopped matching a product rule that changed after it was
written, and each fix is the input the product now demands. That is a stale test brought current, which I read as
ordinary maintenance rather than the random sensor the ruling targets, and both proofs cover PostgreSQL migrations
that once failed in production. If the ruling is meant literally for this case too, that is the owner's call; the
branch itself is sound.
