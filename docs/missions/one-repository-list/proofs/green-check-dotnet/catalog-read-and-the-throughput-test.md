# Two defects the green-check work named but did not fix

**What this is.** The seat that made the .NET half of this mission's check green on macOS found more than it
was sent for. Two of those findings were handed on rather than fixed: one product defect the Delivery Lead
picked out of a list of thirteen, and one defect in a test that only came to light when the Delivery Lead
ran the same commit as the Developer and got a different answer. This is the record of both, written on
20 September 2026 on branch `mission/one-repo-list-green-dotnet`, rebased onto `origin/main` at
`640a00189`.

It is a companion to [`README.md`](README.md) in this folder, which records the fourteen failures that made
the check green in the first place, and to
[`../../reviews/green-check-dotnet-review.md`](../../reviews/green-check-dotnet-review.md), which verified
that work by experiment. Nothing in either is re-opened here.

---

## The counts, on this branch, on macOS, after the rebase

The mission check's three .NET commands, run in full from the worktree root with `~/.dotnet` on the path:

| Suite | Result |
|---|---|
| `CcDirector.Gateway.UnitTests` | **Failed: 0**, Passed: 6371, Skipped: 8, Total: 6379 |
| `CcDirector.Core.Tests` | **Failed: 0**, Passed: 4461, Skipped: 18, Total: 4479 |
| `CcDirector.Avalonia.Tests` | **Failed: 0**, Passed: 550, Skipped: 0, Total: 550 |

The Gateway suite gained sixteen tests against the previous record's 6363. Three of them are the ones added
here; the other thirteen came in with the rebase, from the seven commits `origin/main` moved by while this
branch was open. The Core and Avalonia totals are unchanged.

The eight skips in the Gateway suite are the PostgreSQL-backed proofs held back by `PostgresRigGate` with no
database rig present, and the eighteen in Core are the Windows-only cases carrying their own reasons. Both
sets are exactly as the previous record describes them; neither was touched.

---

## 1. `CatalogReadExecutor` named a repository from the machine reading the path

### What was wrong

Two lines, both in `src/CcDirector.ControlApi/CatalogReadExecutor.cs` - the `repos-list` verb that serves
the recent-repository picker, and the `repos-overview` verb behind the Repositories page:

```csharp
Name = string.IsNullOrEmpty(r.Name) ? Path.GetFileName(r.Path.TrimEnd('\\', '/')) : r.Name,
```

A registered repository whose stored display name is empty falls back to its folder name.
`Path.GetFileName` honours only the separator of the host it is running on. Handed
`D:\ReposFred\devthrottle_internal` on macOS or Linux it finds no separator at all and answers with the
whole path, so the list shows a path where a person is scanning for a name. The `TrimEnd` beside it does
not help: it removes a trailing separator, it does not teach `GetFileName` to find the segment.

### What was changed

Both lines now read the name from the path itself, through the shared
`CcDirector.Core.Utilities.RepositoryPaths.FolderName` the previous seat added for exactly this:

```csharp
Name = string.IsNullOrEmpty(r.Name) ? RepositoryPaths.FolderName(r.Path) : r.Name,
```

Each method's documentation says why, so the next reader does not put `Path.GetFileName` back.

### Why this one and not the other twelve

The Delivery Lead ruled it, and the reason is not tidiness. Phase 3 of this mission has the Gateway serve
the union of repositories **already ordered**, built directly on top of this list. A name defect underneath
phase 3 would let phase 3's own proof PASS while showing a correctly ordered list of incorrectly named
repositories - a proof that certifies the wrong thing. The rest of the sweep is named below and left for a
change of its own.

### The tests, and watching them fail

`src/CcDirector.Gateway.UnitTests/CatalogReadRepositoryNameTests.cs`, three cases:

- `ReposList_WindowsPathWithNoStoredName_ReadsTheFolderNameNotTheWholePath`
- `ReposOverview_WindowsPathWithNoStoredName_ReadsTheFolderNameNotTheWholePath`
- `ReposList_KeepsAStoredNameRatherThanReadingTheFolder` - the fallback stays a fallback; a repository the
  owner renamed keeps the name he gave it.

They live in `Gateway.UnitTests` and not in `Gateway.Tests`, where the rest of the `CatalogReadExecutor`
tests sit, **because `Gateway.Tests` is a parked suite and is not one of the three this mission's check
runs**. A regression test that the mission's own check never executes would guard nothing here. These need
no host: they call the two cores directly, and they take 27 milliseconds.

**Watched fail.** With the product change reverted and the tests kept, both path cases went red with the
symptom the defect produces, and the stored-name case stayed green:

```
Expected: "devthrottle_internal"
Actual:   "D:\\ReposFred\\devthrottle_internal"
```

### What this proof does NOT cover

- **Windows.** These tests can only fail on macOS and Linux. On Windows `Path.GetFileName` already reads
  both separators and understands a drive letter, so the old code was right there and **no test can make
  this defect appear on that platform**. The change is proven on macOS; on Windows it is proven by reading.
  This is the same shape of gap the previous record carries, and it is not softened here: neither that seat,
  nor its reviewer, nor this one has watched any of this work go red on a Windows machine.
- **The narrowness of the live bite.** `CatalogReadExecutor` runs inside the Director, on the machine whose
  disk holds those repositories, so for a path that machine wrote itself the old code answered correctly.
  The defect bites when the registry holds a path written somewhere else - a configuration or workspace
  carried between machines, which is exactly the case that produced the failing test the previous seat
  fixed in `LegacyWorkspaceImport` - and it bites systematically the moment this list is composed anywhere
  but the machine that owns the paths, which is what phase 3 does. The fix is right either way; the claim
  that users are hitting it today is not made.
- **A POSIX folder name containing a backslash.** `RepositoryPaths.FolderName` treats both characters as
  separators, so `/Users/dev/my\repo` answers `repo` where `Path.GetFileName` on Linux answers `my\repo`.
  A backslash is a legal character in a POSIX file name, so the helper is wrong for that one shape - unlike
  the shape-based comparison in `RuleCandidateFilter`, which deliberately leaves POSIX backslashes alone.
  It is recorded, not changed: the helper and its tests were reviewed and approved as they stand, a
  repository folder named with a backslash has never been seen, and widening the question means re-opening
  the previous change rather than finishing this one.

### Two adjacent defects of the same family, found here and NOT fixed

Neither is in the deferred list below and neither is in scope; both are named so the sweep, when it happens,
is not surprised by them.

- `src/CcDirector.Core/Configuration/RepositoryRegistry.cs:57-69` - `TryAdd` derives the stored name with
  `Path.GetFullPath(folderPath)` and then `Path.GetFileName`. Given a foreign path on a machine that cannot
  parse it, `GetFullPath` prefixes it with the current directory and the stored name becomes the mangled
  whole path. That name is then non-empty, so the fallback fixed above never runs and cannot rescue it.
- `src/CcDirector.ControlApi/ControlEndpoints.cs:53-56` - `NormalizeRepoPath`, the key every per-repository
  aggregation in `repos-overview` groups by, is `Path.GetFullPath(path).TrimEnd('\\','/').ToLowerInvariant()`.
  It decides both canonical form and case-insensitivity from the host, which is the same wrong question
  `RuleCandidateFilter` was fixed for, and it lower-cases POSIX paths that are genuinely case-sensitive.

---

## THE TWELVE SITES THE DELIVERY LEAD DEFERRED - OUTSTANDING WORK, DELIBERATELY NOT DONE

**Read this heading as a list of work that is still open.** The previous seat found the same wrong call -
deciding how to read a path from the machine RUNNING the code rather than from the path's own shape - in
thirteen places beyond the one it fixed, and reported them with file and line instead of changing them.
**The Delivery Lead then ruled, deliberately and in writing, that only `CatalogReadExecutor` would be fixed
on this branch and the rest would be left.** They are not forgotten, they are not fixed, and they are not
covered by any test. They go to the owner as named outstanding work.

**The defect, in plain words, is the same at every one of them:** each turns a repository path into a name a
person reads, using `Path.GetFileName`, which understands only the separator of the machine it is running
on. A path written on a Windows machine and read on macOS or Linux answers with the whole path instead of
the folder name. Each is a one-line change to `CcDirector.Core.Utilities.RepositoryPaths.FolderName`.

Verified line by line on this branch after the rebase onto `origin/main` at `640a00189`:

| # | Site | What it names |
|---|---|---|
| 1 | `src/CcDirector.ControlApi/ControlEndpoints.cs:42` | `ProjectNameOf` - the display fallback shared by several reads |
| 2 | `src/CcDirector.ControlApi/SessionWriteExecutor.cs:833` | the repository name returned when one is registered |
| 3 | `src/CcDirector.ControlApi/Chat/ChatService.cs:418` | the repository a chat reply names |
| 4 | `src/CcDirector.Avalonia/NewSessionDialog.axaml.cs:64` | a resumable session's project name |
| 5 | `src/CcDirector.Avalonia/NewSessionDialog.axaml.cs:136` | a history entry's repository name |
| 6 | `src/CcDirector.Avalonia/NewSessionDialog.axaml.cs:217` | a repository row's display name |
| 7 | `src/CcDirector.Avalonia/NewSessionDialog.axaml.cs:429` | a named session's repository name |
| 8 | `src/CcDirector.Avalonia/LoadWorkspaceDialog.axaml.cs:194` | a saved seat's repository name |
| 9 | `src/CcDirector.Avalonia/SessionViewModel.cs:567` | the session tile's title when the session has no custom name |
| 10 | `src/CcDirector.Avalonia/RestoreSessionsDialog.axaml.cs:53` | the repository each restored session belongs to |

**Four of those ten are in `NewSessionDialog`, which is the screen this mission is about.** Phases 4 and 6
rewrite parts of it, which is part of why the sweep was deferred rather than done twice.

### The list is longer than thirteen

Counted line by line, the previous record's list is twelve lines, of which two were in
`CatalogReadExecutor` - so ten remain, not twelve, and the prose figure of "thirteen other places" does not
match the list printed beneath it. More importantly, **the list is not complete.** Reading the whole
repository for the same shape while doing this work turned up seven further lines that also turn a
repository path into a name a person reads, and that nobody has recorded:

| Site | What it names |
|---|---|
| `src/CcDirector.Core/Sessions/SessionName.cs:36` | `SessionName.FolderName` - a second helper answering the same question the same wrong way, and callers of it inherit the defect |
| `src/CcDirector.Core/Voice/VoiceService.cs:290` | the repository name **spoken aloud** in a session report |
| `src/CcDirector.Core/Voice/VoiceService.cs:402` | the spoken fallback name for a session with no custom name |
| `src/CcDirector.Core/Git/RepositoryStatusService.cs:94` | the `Name` on a repository's status record (the failed-probe path) |
| `src/CcDirector.Core/Git/RepositoryStatusService.cs:119` | the `Name` on a repository's status record (the normal path) |
| `src/CcDirector.Avalonia/NewSessionDialog.axaml.cs:631` | the name of a repository FOUND under a watched root folder - phase 2's own subject matter |
| `src/CcDirector.Core/Configuration/RepositoryRegistry.cs:68` | the name STORED when a repository is registered, described above |

So the sweep the Delivery Lead deferred is at least seventeen lines across eleven files, not twelve lines.
That is a bigger decision than the one that was taken, which is the reason for writing it down rather than
quietly extending the branch. Nothing in this section was changed.

---

## 2. `Parse10MbInChunks_TypicalChunkStaysWithinUiBudget` measured the machine, not the parser

### What was wrong

The Delivery Lead ran `dotnet test src/CcDirector.Core.Tests` on the same commit the previous Developer had
reported green, and got one failure - this test. Both runs were honest. It was then settled by measurement
rather than argument: the same binary FAILED at a load average of 11.93 and PASSED at 2.99, minutes apart.
The measurement is recorded in
`docs/missions/one-repository-list/proofs/throughput-test-measures-the-machine.md`, which is on the
`mission/one-repository-list` branch rather than this one, so the link is deliberately written out rather
than made relative - it does not resolve from here until the mission record lands.

The test timed each 64 KB chunk with a stopwatch and asserted three things: the median chunk under 100
milliseconds, no more than 5 percent of chunks over it, and no single chunk over 1000. Elapsed wall-clock is
the parser's own work PLUS however long the operating system left the thread off a processor, so on a shared
machine all three assertions are verdicts on the machine.

**This matters more than one red line.** This Mac routinely runs a dozen agent sessions at once, so the test
goes red at random, is dismissed as noise, and is waved through on the day the parser genuinely does slow
down. A performance check that cries wolf is worse than none, because it is mistaken for cover.

### What it asserts now

**The budget was not widened.** The 100 millisecond figure is untouched. What changed is what is measured.

1. **The work per byte** - bytes allocated on this thread per byte parsed, read with
   `GC.GetAllocatedBytesForCurrentThread()` around the parse loop only. This is deterministic: the same
   input over the same code allocates the same amount however contended the box is. It is read per THREAD,
   so nothing else in the test run is counted. It is where a managed parser's regressions actually appear -
   a string built per cell, a query in the hot loop, a boxed struct - and it is the direct cause of the
   pauses the test exists to prevent, because allocation is garbage collector pressure and the collector is
   what stops the user interface thread. Measured at **34.47 bytes per byte parsed**; the budget is 40.
2. **The cost of a chunk**, taken from the **fastest** whole chunk of the run rather than the median or the
   worst. Scheduler contention is one-sided - being descheduled can only ADD elapsed time, never remove it -
   so the fastest of 160 samples is the closest this suite can get to the parser's own cost, and it can
   never read below that cost. The guarantee that buys runs in one direction only: **a pass is never false;
   a red means the parser is over budget, or was within this machine's interference of it.** A fastest
   sample under the budget proves the parser's own cost is under the budget too. A fastest sample over it
   does not prove the converse with the same force - a parser whose own cost sat close to 100 milliseconds
   could be pushed past it by the smallest interference present in every one of the 160 chunks. That this
   does not happen today is an empirical fact about today's margin and not a property of the measurement:
   the parser costs about 10 milliseconds a chunk, so a false red would need 90 milliseconds of
   interference on all 160 samples, and the fastest chunk measured 11.07 milliseconds at a load average
   above 40 (the third row of the table below). If a later legitimate change puts the parser at 70 to 90
   milliseconds a chunk - still inside budget - a loaded machine could turn this red, which is why the
   failure message says to check the margin rather than claiming machine load is excluded. Asserted against
   the same 100 millisecond user interface budget.

   What a fully contended machine produces is still a green, and it is a true one **about the parser**: the
   latency a user would have seen on such a machine can be far over budget while the parser itself is not.
   The test declines to judge the machine, deliberately - that is the defect it was rewritten to stop
   committing - which is why the strong assertion is the first one.

The median, the worst chunk and the share of chunks over budget are still **printed** on every run, because
a person reading a run wants to see them. None of them is a verdict any more.

The stream is now cut into exactly 160 whole chunks of 64 KB rather than 160 whole chunks and a runt of a
few bytes, because the runt parsed in microseconds and would have been the "fastest chunk" every time.

### Watched, under real load, on this machine

Three experiments, all on this Mac (ten cores), all with the old assertions compiled in beside the new ones
so the two were measured in the same process in the same seconds.

| Machine load | Old test | New test |
|---|---|---|
| about 2 (quiet) | passes - median 13.8 ms, slowest 20.2 ms | passes - fastest chunk 8.70 ms, 34.472 bytes per byte |
| about 17 to 22 | passes, barely - median **44.8 ms**, slowest **97.96 ms**, against a 100 ms bar | passes - fastest chunk **10.34 ms**, **34.475** bytes per byte |
| about 40 to 52 | **FAILS**: "median chunk 108.2 ms exceeded the 100 ms UI budget - parser regression", 94 of 161 chunks over budget | passes - fastest chunk **11.07 ms**, **34.474** bytes per byte |

The load was produced deliberately with processor-burning processes, all of which were stopped afterwards
and verified gone. Read the last row as the whole argument: the old test accused the parser of a regression
in the same seconds that the new test's two numbers barely moved. Across a 20-fold change in machine load
the asserted allocation figure moved in the fifth significant figure, and the asserted timing figure moved
by 27 percent against a bar nine times above it.

### Watched failing for a real regression - and the old test let the same regression through

The new assertions are not merely quiet; they are sharper. In a disposable edit, `AnsiParser.PutChar` was
made to allocate one string per character - a textbook managed-code regression - and both tests were run:

- **New test: FAILED.** `the parser allocated 56.076 bytes per byte parsed (587998224 bytes for 10485760),
  over the 40.0 budget`.
- **Old test: PASSED.** Median 24.57 ms, one chunk of 161 over budget, worst 171.81 ms - inside every bar it
  had.

So the old test was simultaneously failing for reasons that were not the parser's and passing for a real
regression that was. The parser edit was reverted; `git diff` on `AnsiParser.cs` is empty.

### What this does NOT cover, and what a stronger claim would need

- **The timing half is still a wall-clock measurement.** Taking the fastest sample makes a false GREEN
  impossible, because contention only ever inflates a sample: a fastest chunk under the bar proves the
  parser's own cost is under the bar. It does NOT make a false RED impossible, and the sentence has to be
  read that way round. Contention inflates the fastest sample too, so once the parser's own cost approaches
  100 milliseconds a loaded machine can push even the least-interfered sample past the bar and fail a parser
  that was inside budget. Today that needs 90 milliseconds of interference on all 160 chunks and does not
  happen, but that is the margin, not the measurement. The other thing it does not cover is the machine: on
  a contended box the latency a user actually saw can be far over budget while this assertion stays green,
  because the test judges the parser and refuses to judge the machine. That is why the load-independent
  allocation assertion is the primary one and this is the catastrophe guard it was really always serving as.
- **A genuinely load-independent TIMING claim needs processor time for this thread, and .NET exposes no
  portable way to read it.** `Process.TotalProcessorTime` is process-wide and this suite runs its tests in
  parallel, so it counts other tests. Per-thread processor time means a platform call each
  (`clock_gettime` with `CLOCK_THREAD_CPUTIME_ID`, `GetThreadTimes`) whose constants nobody here can check
  on Windows - a wrong constant would return a wrong number silently, which is worse than the defect being
  fixed. The alternative is a dedicated benchmark run on a machine reserved for it. Neither belongs in this
  suite, and neither was done.
- **The allocation figure is a macOS measurement.** It is stable to five significant figures across runs
  here and `TerminalCell` is a struct of value types whose size does not vary between 64-bit targets, so
  Windows is expected to measure the same. **Expected, not observed** - nobody has run this on Windows. The
  budget carries 16 percent of head-room above the measured figure for exactly that reason, which is why it
  is 40 and not 36. The deliberate regression above landed 63 percent over.
- **The other three tests in the class still read a stopwatch.** `Parse10MbStream_CompletesWithinTenSeconds`
  (measured at 2.216 seconds against a 10 second bar) and `ParseThroughput_IsNotSuperlinear` (a ratio, at
  9.62 against a bar of 15) have not been observed going red under load, and they were left alone - the
  ruling named one test. A note in the class says that if either ever does go red on a busy machine it is
  the same defect and wants the same repair, not a wider bar.

---

## What was changed

Product:

- `src/CcDirector.ControlApi/CatalogReadExecutor.cs` - the two empty-name fallbacks read the folder name
  from the path through `RepositoryPaths.FolderName`, with the reason written on both methods.

Tests:

- `src/CcDirector.Gateway.UnitTests/CatalogReadRepositoryNameTests.cs` - new; three cases, in the suite the
  mission's check actually runs.
- `src/CcDirector.Core.Tests/TerminalThroughputTests.cs` - the chunked test asserts the work per byte and
  the fastest chunk instead of three wall-clock statistics, and carries the argument for that on itself.

Nothing was skipped, no assertion was loosened, no test was deleted, and no budget was widened.
