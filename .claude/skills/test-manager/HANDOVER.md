# Test Manager - handover

Written 2026-08-03. Read `skill.md` first; this file is only the state of play and the queue.

## Where things stand

The gate went from **34 minutes on a quiet machine and 79 on a busy one** to about **80 seconds**.
That is the whole point of the work; do not trade it away.

| suite | state | tests | time |
|-------|-------|-------|------|
| `CcDirector.Core.UnitTests` | default gate | 278 | seconds |
| `Avalonia` / `Engine` / `HostedAgent` / `Launcher` / `Terminal.Avalonia` | default gate | 790 | seconds |
| installer: `setup.Tests` / `setup-engine.Tests` | default gate | 566 | seconds |
| `CcDirector.Gateway.UnitTests` | **PARKED** 2026-09-13 | 4,267 | ~180 s against a 120 s ceiling |
| `CcDirector.Gateway.Tests` | **PARKED** | ~1450 | machine-wide lock queue |
| `CcDirector.Core.Tests` | **PARKED** | ~3400 | 11 min quiet, 33 busy, sequential |

Everything still parked is parked ON PURPOSE. It stays parked until it is fast and good. Do not
un-park anything to raise a coverage number.

## The rules you inherit - these are not preferences

1. **There is no such thing as a bad-luck test.** A test works or it is a bad test. Rewrite it
   deterministically, or delete it. Never retry, never quarantine, never "it passes in isolation".
2. **Maximum 100 tests per batch, each checked individually.** Not 100 `[Fact]` attributes - 100
   ACTUAL tests. A `[Theory]` expands; counting attributes overshot by 18 on the first attempt here.
3. **Parallel is not optional.** Nothing in the default gate may carry
   `[assembly: CollectionBehavior(DisableTestParallelization = true)]` or take the machine-wide lock.
   Cap parallelism, never disable it. Prove it: wall clock must be far below summed durations.
4. **Speed beats coverage.** A fast gate runs on every change; a slow comprehensive one gets skipped,
   and a skipped gate has a true coverage of zero. A batch that would blow the 120-second ceiling does
   not go in, however good the tests are.
5. **Local is the gate, and nothing ever waits on continuous integration.** Not for a release
   either - a release runs the local `-Parked -Configuration Release` gate instead, plus the two
   installer test projects the script cannot reach, on merged main at the commit being tagged.
   Continuous integration is a post-merge backstop that is never WAITED on and
   never left red: a red is driven to green at once. Reading a result after the merge is chasing,
   not waiting, and it is required for the web and Python jobs - they are the only place those
   tests run at all.

## The queue, in the order I would do it

### 1. The bad test list - fix or delete these first

They are named in `skill.md`. Until they are gone the gate is red intermittently, which erodes trust
in everything else. Do this before adding a single new test.

- **`Gateway.UnitTests`, one or two database tests per run.** They fight over
  `SqliteConnection.ClearAllPools()`, which is process-global and is called from ~55 test sites
  deliberately. Two fixes were tried and BOTH REVERTED - read the skill before attempting a third.
  Reproduce it first by running the suite while other suites run; it does not appear on a quiet machine.
- **`SessionNumberAllocatorTests`** - fails intermittently on a fresh allocator with no shared state of
  its own. Cause NOT established. Do not guess; instrument it.
- **`ActivityEventProducerTests`**, **`JwtAccessTokenValidatorEs256Tests`** - both read the real clock.
  Inject one. These two are straightforward.

### 2. Core batch 2 onward - the mechanics are proven, just repeat them

`CcDirector.Core.UnitTests` exists, runs parallel, and has 82 tests in it. Adding to it is now
mechanical:

- Candidates: files under `src/CcDirector.Core.Tests/` with **no** `Task.Delay`, `Thread.Sleep`,
  `Stopwatch`, `Process`, `FileSystemWatcher`, `DateTime.Now/UtcNow`, `Environment.`, `CcStorage`,
  file or directory access, and not in a slow class.
- About **60 files / 496 tests** met that bar when I measured, so there are roughly five more batches
  of easy wins before it gets hard.
- `git mv` the file, build, run the batch ten times, confirm green and fast, commit.

**Two traps I hit, so you do not have to:**
- **13 classes fail in the new project for a mechanical reason, not a test defect:** they resolve
  `TestData` by walking up to the PROJECT directory, which the new project does not have. They are
  correct tests in the wrong home. Fix the path resolution and they can move.
- A helper defined INSIDE a test file pins every user of it to that assembly. Extract it to its own
  file (brace-match the class properly - I truncated a file getting this wrong).

### 3. Only then, the big remaining prize

`Core.Tests` still holds ~3400 tests. Sixteen classes hold 666 of its 727 seconds, and they are real
git work - creating repositories, branches and worktrees on disk. A shared prepared fixture instead of
building each from scratch is the same shape as the migration-template fix that took the Gateway suite
from 2 minutes to under one. That is what would let the suite un-park wholesale.

## What NOT to repeat

I got three diagnoses wrong today by reasoning instead of measuring. They are written up in `skill.md`
under "false trails" with the evidence that killed each. The short version:

- **Low CPU on the test host does not mean idle.** Child processes and database waits do not appear in
  that counter. I called `Core.Tests` sleep-bound on that basis; it was spawning git the whole time.
- **The suites are not full of sleeps.** 19 sleep calls totalling one second in one, 68 totalling 24 in
  the other. Count them before blaming them.
- **More threads is not the fix.** 24 threads ran a suite SLOWER than 4 and surfaced more bad tests.

The one that actually worked: per-class timings out of the TRX, then one comparison between a pure
class and a database-backed one. It found an 800x difference in a single step after hours of guessing
had found nothing.

## Two things outstanding that are not test work

- **The fleet fix (`#2412`) is merged but NOT deployed.** The hosted Gateway deploy belongs to another
  session. Until it deploys, the Fleet Map still shows the false "1 machine unreachable" warning.
- **`review-codex.md`** was a working note and was deliberately not committed. If a review artefact
  turns up in a worktree, it is scratch - do not commit it.
