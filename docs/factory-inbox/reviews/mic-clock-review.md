# Review: pull request 4600 - microphone quality test placed back from a fixed time

Reviewer: a separate review session (worktree D:\ReposFred\devthrottle-mic-clock, branch fix/microphone-quality-fold-clock, head 2f60548a2, base origin/main ac758857b). Written 2026-11-05.

## Scope

What I was asked to check:
1. The fold truly reads no clock, so the fix belongs in the test and hides no product defect.
2. The named test still proves what its name says.
3. No other test in the class reads the real clock.

What I read: the diff (one file, +6/-1), the whole test class at the pull request head, `MicrophoneQualityFold.cs` on origin/main, the architecture guard `GateTestsDoNotDependOnTheMachineTests.cs` and its baseline on origin/main, and the suite list in `scripts/test-local.ps1`.

What I ran (the worktree was not edited; the scratch project lives in the session scratchpad):
- The 22 tests of `MicrophoneQualityFoldTests` in the worktree: 22 passed.
- A scratch console project holding a copy of the fold and the record type from origin/main, running the named test's exact sample layout against three clocks.
- The architecture guard `GateTestsDoNotDependOnTheMachineTests` in the worktree.

## Findings

### 1. The fold reads no clock - confirmed

`MicrophoneQualityFold.cs` on origin/main contains no `DateTime.UtcNow`, `DateTime.Now`, `Today`, `TimeProvider`, `Stopwatch` or `TickCount`. The only time it ever uses is each record's own `TimestampUtc`: the trend groups on `r.TimestampUtc.Date` (documented on `MicrophoneTrendPoint.Date` as the UTC calendar day), the summary takes `LastSeenUtc` as the maximum record time. The fold is a pure function of its input. So the defect was in the test's input, and the fix is in the right place. No product bug is being hidden.

### 2. The cause and the fix, reproduced out of tree

The scratch run of the named test's layout (three samples 1500 to 1502 minutes back, two samples 0 to 1 minutes back):

| "now" | trend days | days seen |
|---|---|---|
| 2026-11-04 00:00:30 UTC (the gate's shape) | 3 | 11-02: 3, 11-03: 1, 11-04: 1 |
| 2026-11-03 23:59:30 UTC | 2 | 11-02: 3, 11-03: 2 |
| 2026-11-04 12:00:00 UTC (the fix) | 2 | 11-03: 3, 11-04: 2 |

Just after midnight the "1 minute ago" sample falls on the previous day and the three "yesterday" samples (25 hours back) fall on the day before that: three days, matching the gate's "Expected: 2, Actual: 3". That is exactly the pull request's stated cause.

The widest offsets in the class are 1502 minutes (this test) and 224 minutes (`TheMeasurementListIsCappedLoudly`). From 12:00 UTC, 1502 minutes back is 2026-11-03 10:58 UTC. No offset in the class comes within an hour of a day boundary, so the fixed time is safe for every test, not just the one that failed.

### 3. The named test still proves its name - yes

- Every measurement: `MeasurementsTotal == 5` and `Measurements.Count == 5`.
- Newest first: the first measurement's time is at or after the last one's. (This is a weak form - it checks the two ends, not every adjacent pair - but it is unchanged by this pull request and the fold sorts with `OrderByDescending`, so I do not ask for it here.)
- One point per day, oldest first: `Trend.Count == 2`, the first date compares ordinally below the second, the older day carries 3 samples, and the two days are told apart by their medians (30 and 40). The days are now 2026-11-03 and 2026-11-04 regardless of when the test runs.

### 4. No other test in the class reads the real clock - confirmed

A pattern search of the file at the pull request head for `UtcNow`, `Now`, `Today`, `TimeProvider`, `Stopwatch` and `TickCount` finds only the new fixed `Now` field, which is a constant `new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc)`. The architecture guard's own regex scan agrees: it now counts this file at zero real-clock uses.

### 5. REQUIRED CHANGE - the baseline must shrink in the same pull request

The architecture guard `GateTestsDoNotDependOnTheMachineTests.No_gate_test_adds_a_sleep_a_real_clock_a_real_process_docker_or_a_fixed_port` (in `CcDirector.Core.UnitTests`, which IS in the default gate) fails when a file's count has FALLEN below its baseline, by design ("Bank it ... in the same change"). Run in this worktree it fails with exactly one line:

```
Fewer machine-dependent uses than the baseline allows - well done. Bank it: edit GateTestsDoNotDependOnTheMachine.baseline.txt in the same change:
  remove: real-clock | src/CcDirector.Gateway.Tests/MicrophoneQualityFoldTests.cs | 1
```

So merging the pull request as it stands turns the default gate red on main. The fix is one deletion: remove line 196 of `src/CcDirector.Core.UnitTests/Architecture/GateTestsDoNotDependOnTheMachine.baseline.txt`:

```
real-clock | src/CcDirector.Gateway.Tests/MicrophoneQualityFoldTests.cs | 1
```

The pull request body says the full local gate was not run because the change is test-only in a parked suite. This is the case that reasoning missed: a test-only change can still move a guard that runs by default.

### Out of scope, noted only

`MicrophoneRankingTests` in `Gateway.UnitTests` still builds `LastSeenUtc` from `DateTime.UtcNow` (on the baseline at line 292). `RankBest` and `ComparableScore` never read `LastSeenUtc`, so that test cannot fail on the clock. Not required for this pull request.

## Verdict

NOT MERGEABLE AS IS. One required change: delete the `MicrophoneQualityFoldTests.cs` real-clock line from `GateTestsDoNotDependOnTheMachine.baseline.txt` in this pull request, then rerun `GateTestsDoNotDependOnTheMachineTests` (about 40 seconds) to see it green. With that line gone the change is correct, in the right place, proves what its name says, and leaves no real-clock read in the class. Mergeable after that one edit.
