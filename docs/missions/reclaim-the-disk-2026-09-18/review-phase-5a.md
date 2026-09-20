# Phase 5 part one: review

From the Reviewer, 20 September 2026. The change under review is the difference between
`2092941b6` (origin/main at the cut) and the phase tip `979f953cd`, on branch
`reclaim/phase5a-review`: the Launcher hosts the background disk scan, part one of
`mandate-phase-5.md`. The Developer's own record is `phase-5a-proof.md` and
`phase-5a-decisions.md`. I read the code, judged the six questions this phase turns on, and
re-ran three of the five revert proofs myself. No real disk was scanned, the Launcher was never
started, no process was stopped, nothing was removed, and nothing was committed except this file.

## The six questions

### 1. Can any path from the Launcher or the job it schedules remove, move or purge a file? No.

I searched every project the Launcher can reach for the operations that change a disk, by the
operations themselves: `File.Delete`, `Directory.Delete`, `MoveTo`, `File.Move`,
`Process.Start`. The findings:

- The only `File.Move` anywhere in `CcDirector.Reclaim`, `CcDirector.Reclaim.Windows`,
  `CcDirector.Launcher` and `tools/cc-cleanup-storage` is `WholeFileWriter`'s move of a
  `.partial` file onto the final name inside the store folder. That is a write into the job's own
  store, not a change to anything it scanned.
- There is no `File.Delete` or `Directory.Delete` in any of them. Phase 3, the removal phase, is
  not merged, so no removal code exists anywhere this branch can reach.
- The one `Process.Start` is `DismComponentStoreAnalysis` from phase 4, running
  `Dism.exe /Online /Cleanup-Image /AnalyzeComponentStore` - the ANALYSIS verb, which measures
  and changes nothing. It is phase 4's already-reviewed code, reached through the rules the job
  runs, and the rule still refuses when the Launcher is not running as an administrator.
- The Launcher's two new project references are the engine and the Windows rules, not the command
  line tool; the job calls the scanner (which reads), the report builder, the index and
  recommendation stores (which write inside the store folder), and the rules' examination methods
  (which read). Two tests hold a fixture tree byte for byte across a run, one in each project.

Nothing in this change can remove, move or purge a file outside its own store folder. This is
proved by the absence of the code, which today is a strong proof, and by the two unchanged-tree
tests. It will STOP being a proof the day phase 3 merges, because removal code will then be
reachable from the Launcher's references for the first time; the Developer says this themselves
in the proof. The phase that joins the two owes a test that fails when the Launcher names a
removal type. Recorded as an obligation for that phase, not a defect in this one.

### 2. Are never-run, running, failed and completed truly told apart? Yes, and a crashed or killed scan cannot read as no-report-yet or as a clean result.

The engine folds every record into one of four states - never-run, running, completed, failed -
and writes the finished sentences itself, so no screen re-derives anything (critical rule 7).
Each way a scan can die is covered:

- A scan that throws is caught once, inside the job, and recorded failed with the reason. If that
  catch did not exist, three tests go red (the Developer's proof C; I did not re-run that one).
- A scan whose process was killed is detected at read time: the record carries the process number
  AND when that process started, so a reused number cannot fool it, and a record saying running
  with no live holder reads as failed, "cut short". I deleted that branch and the test went red;
  restored, the whole project is green again. See the revert proofs below.
- A record that cannot be read, or names an unknown state word, reads as failed, never as
  never-run.
- Completed is written only after the index and the recommendations are both saved, so no
  interrupted scan can write it.

The one residual way a dead scan can read as "no report yet": a scan that dies BEFORE its running
record reaches the disk. That record is the first thing the job writes, so the window is the
length of one small write. The realistic cause is a full disk - and the store lives under the
user's local application data on the system disk, so on a machine whose system disk has zero
bytes free, which is this mission's founding scenario, no record of any kind can be written by
any design. Judged not blocking, for two reasons: nothing on a full disk can be recorded, so no
design choice here could have done better on that volume; and the design's answer to a full disk
is yesterday's saved report, which is at most a day old and still reads fine. The failure to
record is also logged, and the hourly look retries. One consequence is worth naming: while the
disk is full, a folder whose record cannot be written is retried every hour under the never-run
rule rather than the six-hour failure backoff, because there is no record to carry the backoff.
The cost is one failed small write an hour, not a walk.

### 3. Can an interrupted scan leave an index a later read believes? No.

Every file a reader opens - the index, the recommendations, the record - is written by
`WholeFileWriter`: the bytes go to a `.partial` name, and only a complete file is moved onto the
real name. I deleted the temporary name and the move, so the file was written in place, and both
interrupted-write tests went red; restored, green. The listing of saved scans matches `*.json`
only, so a leftover `.partial` is never listed and is overwritten by the next save of the same
folder; the no-earlier-scan test proves this on this machine. A walk asked to stop throws rather
than returning part of a disk, so a partial walk cannot be saved either. The one unproved
corner, which the Developer states plainly, is power loss on a file system whose rename is not a
single step; on the default Windows file system it is, and the mission builds for Windows first.

One deliberate seam, read and judged harmless: if a stop lands after the index is saved but
before the recommendations are, the index on disk is the NEW walk while the record still points
at the OLD pair. The index is whole and carries its own write time, and the saved recommendations
are internally consistent with their own write time, so no reader is handed a half file or a
mismatched pair; the record keeps naming the last pair that completed together.

### 4. Can two scans run at once? Within one process, no - and the two-process gap is not harmful here.

The guard is a file held open for the length of a scan and shared with nobody, in the
machine-wide store folder. I deleted the refusal and the guard tests went red in BOTH projects -
the engine's and the Launcher's - and the tree restored green. The guard is one for the whole
store, not one per folder, so two disks cannot be walked at once either.

Across processes the refusal rests on the operating system refusing a second opening of a file
shared with nobody, which is the operating system's own behaviour on Windows and is not tested
here, as the Developer says. My judgment that this gap is not harmful, given one Launcher per
machine:

- The guard is released by the operating system itself when the holder dies, so a crashed scan
  cannot lock the machine out - that is the right shape for a guard.
- Two Launchers on one Windows machine would still be refused, by the sharing rule, in either
  process.
- The command line tool's `scan` deliberately does not take the guard (the Developer's decision
  8). The worst case is a person's scan and the background scan saving the same folder at the
  same moment: the second writer of the `.partial` is refused by the sharing rule, the background
  job records a failure with the six-hour backoff, and no half file can result. A duplicated disk
  read and one recorded failure is the bounded cost of a decision that keeps a person's command
  from failing for a reason they cannot see. I accept the judgment, with the reason stated.
- On macOS and Linux, the .NET file sharing mode is not enforced between processes, so the guard
  would not refuse a second Launcher there. That needs two Launchers on one machine, which the
  product does not do, and even then the whole-file writer bounds the outcome to a duplicated
  read and one failed save. There are no rules off Windows yet, so the recommendations there say
  broken by design. Not blocking; worth remembering when another platform gains rules.

### 5. Can the scan slow, block or crash the Launcher itself? No.

- It is started beside the update loop, fire-and-forget, under the same `--managed` gate, in both
  the tray mode and the headless mode. A Launcher started from a repository build or a test rig
  never begins a walk; that also means no test of mine could ever scan this machine.
- The first thing it does is wait ten minutes; nothing at startup competes with the tray.
- The walk runs on its own long-running thread, not the thread pool and not the tray thread.
- Two levels of catch stand between it and the process: every look is caught inside the loop and
  logged, and the started task is wrapped once more so an unobserved exception cannot escape. I
  traced every path that could throw outside the job's own catch - the guard, the read, the
  running record - and each lands in the loop's catch and is retried an hour later.
- A quit is honoured between roots and inside the walk, once per folder, and a stopped scan
  records its own failure sentence.
- The cost worth naming, not as a defect: the walk holds the whole disk's entries in memory in
  the Launcher process for the duration, and a walk was measured at 1,406 seconds. That scanner
  is phase 1's, unchanged, speed and resources were ruled out of scope by the mission, and the
  phase 2 run proved the walk completes on this machine. An out-of-memory failure inside the walk
  is caught and recorded like any other failure; the Launcher carries on.

### 6. File.Exists and Directory.Exists, every use that decides an answer

- `BackgroundScanGuard.TryAcquire`: `File.Exists` appears in the catch filter that decides
  "refused" against "something else is wrong". If the existence check itself lied, the
  exception is thrown rather than swallowed - the scan does not run, nothing is recorded, the
  look is logged and retried. Fails loud. Safe.
- `BackgroundScanStatusStore.Read`: `!File.Exists(recordPath)` decides never-run. This is the one
  use whose swallowed error would produce a wrong verdict - a completed record that cannot be
  seen would read as "no scan was ever started". Judged safe on this evidence: every state in
  which the existence check lies is a state in which the file could not be read either (the
  folder denies access, the file is gone), so no reader would learn more from any design, and the
  read of the saved index under the same store would fail loudly at the same moment. The
  Developer's decision 3 names this as the one deliberate absence, and the unreadable-record
  test holds the other side of it.
- `SavedRecommendationStore.Load` and `ScanIndexStore.Load`: `File.Exists` decides between
  throwing "nothing is saved there" and reading - both throw, neither returns a quiet empty
  answer. The message asserts a cause it cannot prove ("the background scan has not finished
  one") when the truth may be that the file cannot be seen; wording, and no reader exists yet.
- `ScanIndexStore.List`: `Directory.Exists` deciding an empty listing is phase 1 code, unchanged
  in this diff, and out of this review's scope.

## The revert proofs I re-ran

Three of the five, run by me in this worktree. The mutation was never committed; each check was
DELETED, never switched off; the WHOLE test project was run with no filter every time; the
restore run was a full build and run, never `--no-build`; and the worktree is clean against the
phase tip now, with `git status` empty. `CC_DIRECTOR_ROOT` was unset in my shell before every
Launcher run, and the two known `LauncherDeclaredCapabilities` failures passed throughout
exactly as the Delivery Lead said they would.

| | What I deleted | Whole Reclaim project, 255 tests | Whole Launcher project, 208 tests |
|---|---|---|---|
| A | the guard refusal in `BackgroundScanJob.Run` | 254 passed, 1 failed: the guard test | 207 passed, 1 failed: the Launcher guard test, on both targets |
| B | the dead-holder branch in `BackgroundScanStatusStore.Read` | 254 passed, 1 failed: `Read_WhenTheRecordSaysRunningAndItsProcessIsGone` | not run |
| D | the temporary name and the move in `WholeFileWriter` | 253 passed, 2 failed: both `Save_CutShort` tests | not run |

Every number matches the Developer's proof. After each restore the projects were green again:
Reclaim 255 of 255, Launcher 208 of 208 on both targets.

## What I did not cover

- Proofs C and E were not re-run; their subject matter (the failure recording, the stop check) is
  covered by my reading of the code, the named tests, and the matching of the Developer's numbers
  on the three I did run.
- The parked suites were not run by me either. The Developer's open item stands: the Delivery Lead
  should run `.\scripts\test-local.ps1 -Parked` on this commit from a worktree of its own, outside
  a session of the live installation, before merging - the one parked suite with a real edge here
  is the Gateway unit tests, because they reference the Launcher project.
- The Launcher actually running (`RunLoopAsync`, the ten-minute settle, the hourly look, the two
  start lines) is exercised by no test, as the proof says plainly. I verified the `--managed` gate
  by reading both call sites, and verified no test of mine could start a real scan, but I did not
  start the Launcher.

## Findings

None blocking. Recorded for the phases that follow, each with its judgment:

1. **A failure record that cannot be written leaves "running" under a live Launcher.** If the
   system disk fills during a scan - the record store is on that disk - the scan's failure record
   fails to write, the record still says running, the Launcher process is alive, and a folder in
   that state is never due again until the Launcher restarts, because running is not due. It
   heals by itself at the Launcher's next restart, which its own updates cause regularly, and no
   reader is handed anything false in the meantime - only a stale "still running". When the phase
   6 fold is built, consider treating a running record older than a day, or carrying a heartbeat
   in the record, so this state cannot outlive a long-lived Launcher.
2. **On a full system disk, every read says never-run.** Judged inherent, not fixable by any
   design that records on the same volume; yesterday's saved report is the answer the design
   gives, and the hourly retry costs one small failed write, not a walk.
3. **The cross-process guard is untested and is not a guard at all on macOS and Linux**, where
   the .NET sharing mode is not enforced between processes. Bounded by one Launcher per machine
   and by whole-file writing; revisit when another platform gains rules.
4. **Nothing stops a later change from calling removal code from the Launcher once phase 3
   merges.** The phase that joins them owes a test that fails when the Launcher names a removal
   type.

## Verdict

Approved. The mandate's part one is built as asked, the proofs are honest about what they do not
cover, and the revert proofs reproduce. The parked-suite run remains the Delivery Lead's to do
before merging, per the proof document.
