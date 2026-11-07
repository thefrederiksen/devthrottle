namespace CcDirector.Core.Machine;

/// <summary>
/// One sample and where it sits on a MONOTONIC timeline - the one shape every fold that needs a window takes
/// (the Implementation Lead's ruling of 2026-11-06 on pull request 4660).
///
/// WHY THIS EXISTS. A window - "held for 60 seconds", "grew in five minutes" - is an interval, and an interval
/// is never measured by subtracting two <see cref="ResourceSample.TakenAtUtc"/> stamps: a routine time
/// correction or a machine waking from sleep steps the wall clock, and a verdict would then be computed from
/// the wrong samples with no change in load at all. So the thing that owns the clock hands the folds the
/// position of each sample on a timeline that only the monotonic clock advances.
///
/// ONE ANSWER FOR THE LIVE PATH AND THE REPLAY PATH:
/// <list type="bullet">
/// <item>Live, <see cref="ResourceSampler"/> sets <see cref="Timeline"/> to the time since it started, off its
/// own <see cref="TimeProvider"/>.</item>
/// <item>Replayed from disk, it is the recording's own <c>recordingSeconds</c> (<see cref="ResourceRecorder"/>),
/// which inside one run advances only with the monotonic clock.</item>
/// </list>
/// Only differences between two <see cref="Timeline"/> values mean anything; its zero is wherever the timeline
/// began.
/// </summary>
/// <param name="Sample">The sample. Its <see cref="ResourceSample.TakenAtUtc"/> is for printing only.</param>
/// <param name="Timeline">Where the sample sits on the monotonic timeline.</param>
public readonly record struct TimelineSample(ResourceSample Sample, TimeSpan Timeline);
