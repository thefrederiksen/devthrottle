# Review of pull request 4660 - phase 2a: the sampler, the two-bar meter and the warning banner

Reviewed: branch `resmon/phase2-sampler-meter` at 565599fe7, diffed three-dot against origin/main
(merge base ff55d00e4). Read against `missions/resource-monitor.html` sections 5 to 8 and the house
rules in the project instructions file. Nothing in the worktree was edited, built or run; the one out-of-tree check was a
`git merge-tree --write-tree origin/main HEAD`, which writes only to the object store.

Hunted for: a plausible figure standing in for an unknown one; one banner per resource per crossing and
the warning clearing; the 60-second rule on the monotonic timeline; thread safety between the sampler's
timer thread, the user-interface thread and shutdown; the narrow-toolbar fit; the Windows memory-only
probe; and any view deciding what a state means (project rule 7, the dumb client).

## Verdict in one line

The sampler, the folds and the banner lifetime are sound and well tested, and the one defect class the
mission has been bitten by is absent from the diff itself - but the branch is nine commits behind
origin/main, and the rebase that must come before the merge is where that defect class re-enters: main's
copy of the detail fold carries fixes this branch's copy lacks, this branch carries a fix main's copy
lacks, and main now has the full Windows probe this branch says does not exist. Two further real
defects: a warning already in force when the Director starts never gets its banner, and memory has no
hold, so a machine on the limit blinks a banner every five seconds.

## Findings, most severe first

### 1. HIGH - Rebase hazard: six add/add conflicts, and the wrong side wins the plausible-number fight

Where: `src/CcDirector.Core/Machine/ResourceDetailView.cs` lines 207 to 262 (branch) against the same
file on origin/main lines 207 to 276; `src/CcDirector.Core/Machine/ResourceMeterView.cs` line 64
(branch) against origin/main line 52.

What happened: pull request 4668, merged to main after this branch was cut, added its own copies of
`IResourceSampler.cs`, `ResourceCrossings.cs`, `ResourceMeterView.cs`, `ResourceDetailView.cs` and
three unit-test files. A dry-run merge reports add/add conflicts in `IResourceSampler.cs`,
`ResourceDetailView.cs`, `ResourceMeterView.cs`, `FakeResourceProbeTests.cs`,
`ResourceCrossingDetectorTests.cs` and `ResourceViewFoldTests.cs`. The two copies have diverged in
BOTH directions on exactly the defect class under review:

- Main's detail fold shows the dash when "Everything else" does not add up: `if (other >= 0)` for the
  processor and `if (ours <= used)` for memory, with a comment saying "never a made-up zero". This
  branch's copy clamps instead: `PercentOf100(Math.Max(other, 0))` and `used > ours ? used - ours : 0`
  - a tidy "0%" and "0 MB" for a figure that cannot be true. Main's copy also lists a session member
  whose parent has exited under the root (the "orphan" block, lines 264 to 276 on main); the branch's
  copy drops it silently. Main's `ResourceViewText.Bytes` prints kilobytes below a megabyte so a small
  helper never reads "0 MB"; the branch's copy does not.
- The other way round: main's `ResourceViewText.CpuFraction` is `Math.Clamp(cpu.TotalBusyPercent / 100.0, 0, 1)`
  - a clamp that turns an impossible processor figure into a full bar. This branch's copy refuses it
  (null, with `CpuOutOfRangeReason`) and pins that with `ResourceMeterHonestyTests`. That is the right
  answer and main currently has the wrong one.

Failure scenario: the author resolves the conflicts by taking "ours" for every file. Main loses the
three detail-fold fixes and gains two clamps; the "Everything else on the machine" row then shows "0%"
on every quiet machine where the per-process reads lag the machine total. Or the author takes "theirs"
for every file and main keeps its clamp in `CpuFraction`, and the meter draws a full green-less bar for
a probe that reported 140 per cent. Either blanket choice reintroduces the defect.

What to do: rebase before merging; in `ResourceDetailView.cs` keep main's copy (its only non-doc
difference from the branch is the three fixes above), in `ResourceMeterView.cs` keep the branch's
`CpuFraction` and `CpuOutOfRangeReason` AND main's kilobyte branch in `Bytes`, and re-run
`ResourceViewFoldTests` from both sides. The merged tree was not built here; `ResourcePressureTests.cs`
auto-merges but was changed on both sides, so it needs a build, not a glance.

### 2. HIGH - On Windows the Director will say it has no Windows probe, and that will be false

Where: `src/CcDirector.Core/Machine/ResourceProbes.cs` lines 16 to 20 and 41 to 50
(`NoWindowsProbeYet`, `MemoryOnlyResourceProbe`).

Origin/main has carried `WindowsResourceProbe` since 977dee904 (pull request 4669), with the same
`(TimeProvider? time = null, IMachineMemoryProbe? memory = null)` constructor shape as the macOS probe,
and two later fixes to it (dc255ed71, 8f58ec482). `ForThisMachine()` on this branch still returns the
memory-only probe on Windows, so after the rebase the owner's main machine would show a dashed CPU bar
whose tooltip reads "cannot read: this Director has no Windows resource probe yet - only memory is read
on Windows", and the 24-hour recording would carry that sentence in every sample. The sentence is not
true on main. That is a stated reason that is wrong, which is the same family as a figure that is wrong.

What to do: return `new WindowsResourceProbe()` on Windows. The memory-only probe can stay as a named
stand-in for a platform with no probe (and for the "cannot read" photograph), but it must not be the
Windows answer. `MemoryOnlyResourceProbeTests` is fine as a test of that class; it is the factory that
must change.

### 3. MEDIUM-HIGH - A warning already in force when the Director starts never gets a banner

Where: `src/CcDirector.Avalonia/App.axaml.cs` line 516 (`StartResourceMonitor` inside
`InitializeServices`, which runs on a background thread from the splash at line 117, before the main
window is created at line 172); `src/CcDirector.Core/Machine/ResourceSampler.cs` line 268 (the first
read is at `TimeSpan.Zero`); `src/CcDirector.Avalonia/Controls/ResourceMonitorPresenter.cs` line 148
(`Crossed` is subscribed only when the window attaches, in `MainWindow_Loaded`).

Memory and swap are judged on the newest sample with no 60-second hold (`ResourcePressure.Memory`,
`ResourcePressure.Swap`), so the very first read on a machine that is already short of memory produces
a `Raised` crossing. That crossing is raised on the sampler's thread before the main window exists, so
nobody is listening. `ResourceCrossingDetector._lastKnown` is now Tight, so no further crossing fires
until the level changes. Result: the MEM bar is amber (the meter renders `Current` at attach, and the
verdict colours it), the owner hovers, the tooltip says the figure, and there is no banner - and there
will be none until memory recovers and drops again. The mission's "a banner within 70 seconds of a
resource maxing out" does not hold for the Director's own start. The rig in `ResourceMeterWindowTests`
cannot see this because it attaches before the first read.

Failure scenario, by reading: a machine with under 2 GB available (the `MemoryPressure.TightAbsoluteBytes`
line) when the Director starts. The CPU is not affected because its 60-second rule means the first
possible crossing is a minute in, by which time the window is attached.

What to do: when the presenter attaches, seed the board from the current verdict - treat each resource
that is Tight or Critical at attach as a crossing into that level, using `ResourceBannerFold.Build`
with the current sample and `host.Latest?.Sessions`. Or have the host keep the last crossing per
resource and replay it to a late subscriber. Add a window test that reads once before attaching.

### 4. MEDIUM - Memory and swap have no hold and no hysteresis, so a machine on the limit blinks a banner every five seconds

Where: `src/CcDirector.Core/Machine/ResourcePressure.cs` lines 30 to 34 and 162 to 172 (memory on the
newest sample, deliberately not held); `MemoryPressure.Level` on main lines 61 to 62 (two absolute
limits, no hysteresis); `src/CcDirector.Core/Machine/ResourceCrossings.cs` line 100 to 114 (any level
change is a crossing); `src/CcDirector.Core/Machine/ResourceBanner.cs` lines 261 to 276 (every Raised
puts the banner up, every Cleared takes it down).

Available memory on a busy machine moves tens of megabytes per sample. At 2.0 GB available the level
flips Tight, Normal, Tight, Normal every five seconds; each flip is a crossing, each Raised re-raises
the banner (and un-dismisses it), each Cleared removes it, and `[ResourceCrossingDetector]` and
`[ResourceBannerBoard]` log two lines per flip for as long as it lasts. The mission's limits table says
"A resource must stay over its limit for 60 seconds to count" above the whole table; this branch
applies it to the processor only, and the reason given (memory does not swing the way a processor
does) is not true at the limit. The memory judgement was in the merge base, but the banner that makes
it visible is this pull request's, so this is where it is reported.

What to do: hold memory and swap for the sustained window the same way the processor is held (every
readable sample in the window over the limit), or give the crossing detector a short hold before it
reports a Cleared. Pin it with a test that alternates 1.9 GB and 2.1 GB available and expects one
banner.

### 5. MEDIUM - The dispose wait is unbounded, first in line at shutdown, and not proved by the test that claims it

Where: `src/CcDirector.Core/Machine/ResourceSampler.cs` lines 340 to 359 (`Dispose` takes `_tickLock`
with no timeout); `src/CcDirector.Avalonia/App.axaml.cs` lines 1035 to 1037 (`StopResourceMonitor` is
the first step of `OnShutdown`, ahead of the Gateway farewell and the session kill, both of which are
time-boxed); `src/CcDirector.Core/Machine/ResourceMonitorHost.cs` line 353 (the recording's once-a-minute
disk append runs inside the tick, so the tick can be inside a synchronous write on a disk that is the
reason the machine is slow).

Failure scenario: the owner closes a Director on a thrashing machine (the devlinux case) at the moment
the recorder's minute write is in flight, or a probe call stalls. `Dispose` blocks the user-interface
thread for as long as that takes. Everything after it in `OnShutdown` - the farewell that lets the
Gateway rule the sessions "Director stopped", the session kill, the crash-journal delete - waits
behind it with no cap. A stuck probe means a Director that never exits.

The justification for the wait ("the recording is not safe to write and dispose on two threads") is
weaker than written: `ResourceRecorder.Record` and `ResourceRecorder.Dispose` both take the recorder's
own `_gate`, so they already serialise. A bounded wait (`Monitor.TryEnter(_tickLock, timeout)`, logged
when it expires) loses nothing the recorder's lock does not already protect.

Test gap: `ResourceSamplerTests.Dispose_StopsTheTimer` proves the timer stops;
`ResourceMonitorHostTests.Host_Dispose_StopsTheSamplerBeforeTheRecording_AndNoReadFollows` proves no
read follows. Neither proves the wait for a read in progress nor the order sampler-then-recording. A
probe that blocks on a `ManualResetEvent`, a `Dispose` on another thread, and an assertion that it
returned only after the probe was released would prove the claim in the summary comment.

### 6. MEDIUM - "(not one of this Director's sessions)" is a claim the evidence does not support

Where: `src/CcDirector.Core/Machine/ResourceBanner.cs` lines 216 to 225; the wording is pinned by
`ResourceBannerTests.Build_MemoryRed_NamesTheBiggestByMemory_AndSaysWhenItIsNotOurs` and
`Build_WhenTheDirectorRunsNoSessions_SaysTheBusiestIsNotOurs`.

`sessions` is non-null whenever the process list could be read (`ResourceMonitorHost.SessionsAt`), and
then any process not in a session's `Pids` is written up as not ours. But `Pids` is what the parent walk
and the remembered membership managed to ATTRIBUTE, not what belongs. Three ways a process of ours is
not in it: a child that detaches from its parent before its first five-second sighting (the Windows
case in issue 4624, which `SessionUsageRollup` itself calls out); a session whose root process id is 0
at that read (`ApiModeSessionBackend.ProcessId` is 0 until its transport exists, `GitHubActionsBackend`
always, so `DirectorSessions` leaves it out); and the one-read lag between a session starting and its
tree being seen. In each the banner says "Busiest: dotnet test (not one of this Director's sessions)"
about a process that is one of this Director's sessions - the "claim made without the evidence"
pattern the mission has hit before.

What to do: say what is known - "not attributed to one of this Director's sessions" - or make the
stronger claim only when it is earned (every started session has a root in the process list and no
session was left out). Adjust the two tests' expected strings.

### 7. MEDIUM - "Sessions not known" collapses into "no sessions" on the way to the five-minute summary

Where: `src/CcDirector.Core/Machine/ResourceMonitorHost.cs` line 345 (`sessions ?? Array.Empty<ResourceSessionInput>()`);
`src/CcDirector.Core/Machine/ResourceSummaries/ResourceSummarySampleFold.cs` line 42;
`ResourceSummarySample` on main (lines 24 to 33) has no "sessions unreadable" reason - only a list.

The host's own comment, eight lines above, says null (not known) is never the same as empty (no
sessions). Then it hands the summary an empty list for the not-known case. A five-minute window in
which every read's process list was unreadable - or every read on Windows under the memory-only probe
of finding 2 - reaches the Gateway with zero session rows, identical to a Director running nothing.
The machine figures carry a reason for an unreadable processor or memory; the sessions carry none.

What to do: either skip `_summary.Add` for a read whose sessions are not known and let the window's
sample count show the gap, or give the summary sample a sessions-unreadable reason the builder can
carry into the row. The second is the honest one; the first is the smaller change.

### 8. LOW-MEDIUM - A fold in the view layer, and a user-interface collection read off its thread

Where: `src/CcDirector.Avalonia/Controls/ResourceMonitorPresenter.cs` lines 162 to 173 (`Combined`
sums every session's processor share, memory and unknown counts) and line 158 (`_runningSessions()`,
which is `() => _sessions.Count` on `MainWindow._sessions`, an `ObservableCollection`, called from the
sampler's thread inside `OnUpdated`).

`Combined` decides what "this Director's sessions cost together" is - a sum with two lower-bound
counts - inside the Avalonia project. Project rule 7 (the dumb client) wants that in Core: `ResourceMeterFold.Build`
could take the sessions list and do the sum, so a Core test pins it (today no test does - `Combined`
is private to the presenter). The off-thread `Count` read is benign for a `List<T>` but it is a
user-interface collection read from a pool thread; the number should be supplied by the host alongside
the sessions it already lists.

### 9. LOW - A monitor that failed to start shows "has not been read yet" for the Director's whole life

Where: `src/CcDirector.Avalonia/App.ResourceMonitor.cs` lines 46 to 50; the tooltip text comes from
`ResourceSamplerState.NotYetSampledReason`.

`ResourceRecorder`'s constructor creates the recording folder and reads the previous run's end; if
that throws (a read-only or missing storage root), the whole monitor is null and the meter shows two
dashes with "cannot read: the machine has not been read yet" forever - a "not yet" that is really
"never", with the reason only in the log. The recording failing also takes the meter down with it,
although the meter needs no recording. Give the meter a "the resource monitor did not start - see
the log" state, and consider whether the recorder failing should stop the sampler at all.

### 10. LOW - `History` drops the timeline the mission just made load-bearing

Where: `src/CcDirector.Core/Machine/IResourceSampler.cs` line 91 and `ResourceSampler.cs` lines 226
to 238 (`History` returns `ResourceSample`, not `TimelineSample`).

Phase 2b's detail screen draws an hour of history and takes `IReadOnlyList<ResourceSample>`; its points
are `ResourceHistoryPoint(DateTime AtUtc, ...)`. With no timeline on `History`, the chart's x-axis has
to be `TakenAtUtc`, so a clock step mid-hour folds or stretches the line - the same wall-clock
subtraction this pull request removed from the verdict. Expose the timeline on `History` now, before
2b builds on the shape.

### 11. LOW - Production code depends on a test builder, and the rebase leaves two builders with one name

Where: `src/CcDirector.Core/Machine/ResourceMeterView.cs` lines 110 and 112
(`ResourceSampleBuilder.Gigabyte` and `.Megabyte`); `src/CcDirector.Core/Machine/FakeResourceProbe.cs`.

`ResourceViewText.Bytes` - a production fold - takes its units from `ResourceSampleBuilder`, a test
builder this branch ships inside `CcDirector.Core`. Main already has a different `ResourceSampleBuilder`
(init-only properties, `Every5Seconds`) in `src/CcDirector.Core.UnitTests/Machine/` under the namespace
`CcDirector.Core.Tests.Machine`, and a `FakeResourceProbe` plus `FakeSampleBuilder` in
`CcDirector.Core.Tests`. After the rebase the solution holds two builders of the same name with
different shapes, resolved by whichever namespace a test file sits in. Put the unit constants on
`ResourceViewText` (or a `Units` type) and settle on one builder.

### 12. LOW - Stopping the Director discards the open five-minute window

Where: `src/CcDirector.Avalonia/App.ResourceMonitor.cs` lines 77 to 90 and
`src/CcDirector.Core/Machine/ResourceMonitorHost.cs` lines 374 to 383.

`Dispose` stops the sampler and closes the recording; nothing closes the summary's open window into
the outbox, and the farewell that follows does not drain the outbox. Every Smart Restart loses up to
five minutes of Gateway rows for that machine. This may belong to phase 3's drain, but the mission's
"a 10-minute disconnect leaves no gap" will be measured against a Director that restarts, so it is
noted here.

## Checked and found sound

- The 60-second rule is measured on `TimelineSample.Timeline`, set from `TimeProvider.GetElapsedTime`
  off the sampler's own origin; `ResourcePressure` never subtracts two `TakenAtUtc` stamps, and
  `ResourceSamplerTests.Evaluate_IsHandedTheMonotonicTimeline_NotTheSamplesWallClockStamps` pins it. The
  ring buffer is bounded by count, pinned by `History_IsBoundedByCount_WhenTheWallClockStampsJumpBackwards`.
- The meter never draws a zero for an unknown: `CpuFraction` refuses out-of-range, `MemoryUsedFraction`
  refuses a reading that does not add up, an unreadable bar loses its track and shows a dashed outline,
  the frame colour is the worse of the two bars with Unknown below Normal, and the first-read state is
  two dashes.
- One banner per resource per crossing: the detector compares against the last KNOWN level, so an
  unreadable read mid-warning neither clears nor re-raises; amber-to-red is one banner that turns red;
  Dismiss stays dismissed through an ease and comes back on the next crossing; a Cleared crossing takes
  the banner down whether or not it was dismissed. Pinned by `ResourceBannerTests` and the sampler's
  crossing tests, and shown end to end in `ResourceMeterWindowTests` with photographs.
- Thread safety inside the sampler: `_gate` guards state, `_tickLock` serialises reads, a slow probe
  skips the next tick rather than overlapping, handler exceptions are caught per handler, and both
  presenter handlers hand the user-interface thread finished views through `Dispatcher.UIThread.Post`
  (never a blocking Invoke, so the shutdown wait cannot deadlock on the user-interface thread).
  `SessionManager.ListSessions` copies out of a `ConcurrentDictionary`, so the host's per-read session
  list is safe off-thread; every backend's `ProcessId` returns 0 rather than throwing.
- Rule 7 in the controls: `ResourceMeter` and `ResourceBannerStrip` map a level to a colour and lay out
  what they are handed; no control decides what a state means. The one fold in the view layer is
  finding 8.
- The narrow-toolbar fit measures natural widths of both groups rather than squeezed bounds, is stable
  (the decision always asks whether the FULL meter would fit, so compact cannot oscillate), and is
  proved in a headless window at 800 pixels with a long Director name, with Cockpit and Settings
  asserted inside the window and the bars coming back at 1600.
- `MemoryOnlyResourceProbe` itself keeps the probe contract: memory is the real reading, every other
  part is unreadable with one plain reason, and the core count is a fact rather than a figure.
