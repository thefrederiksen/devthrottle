using CcDirector.Core.Machine;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// Builds <see cref="ResourceSample"/> values for the verdict tests, so each test states only the
/// figures it is about and everything else is a quiet, readable machine.
///
/// Time comes only from <see cref="Start"/> plus an offset the test chooses - never the clock - which is
/// what keeps these tests in the suite that forbids depending on the machine.
/// </summary>
internal sealed record ResourceSampleBuilder
{
    public static readonly DateTime Start = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    public const ulong Gigabyte = 1024UL * 1024 * 1024;

    public TimeSpan At { get; init; }
    public CpuReading Cpu { get; init; } = CpuReading.Read(totalBusyPercent: 10, busiestCorePercent: 30, logicalCoreCount: 32);
    public MachineMemoryReading? Memory { get; init; }
    public SwapReading Swap { get; init; } = SwapReading.Read(8 * Gigabyte, 1 * Gigabyte);
    public StallReading Stall { get; init; } = StallReading.Read(StallSource.LinuxPressureStall,
        cpuStalledPercent: 0.5, memoryStalledPercent: 0.1, ioStalledPercent: 1.0, runQueueLength: 3, loadAverageOneMinute: 2.5,
        processesWaitingOnDisk: 0);
    public DiskEvidenceSet DiskEvidence { get; init; } = DiskEvidenceSet.Read(
    [
        Core.Machine.DiskEvidence.Read("sda", DiskKind.SolidState, isInSystemVolume: true, busyPercent: 3, millisecondsPerRead: 0.2,
            millisecondsPerWrite: 0.3, averageQueueDepth: 0.01),
    ]);
    public ProcessSetReading Processes { get; init; } = ProcessSetReading.Read(
    [
        new ProcessReading(1, null, "systemd", Start.AddDays(-1), 0.1, 10_000_000, 0, 0),
    ]);

    public ResourceSample Build()
    {
        var takenAt = Start + At;
        return new ResourceSample(
            takenAt,
            Cpu,
            Memory ?? MachineMemoryReading.Read(39 * Gigabyte, 26 * Gigabyte, takenAt),
            Swap,
            Stall,
            DiskEvidence,
            Processes);
    }

    /// <summary>One sample every five seconds for the given span, inclusive at both ends - the sampler's real rhythm.</summary>
    public static List<ResourceSample> Every5Seconds(TimeSpan span, Func<TimeSpan, ResourceSampleBuilder> at)
    {
        var samples = new List<ResourceSample>();
        for (var offset = TimeSpan.Zero; offset <= span; offset += TimeSpan.FromSeconds(5))
        {
            samples.Add((at(offset) with { At = offset }).Build());
        }
        return samples;
    }

    /// <summary>
    /// Pairs each sample with its position on a monotonic timeline, taken from its own
    /// <see cref="ResourceSample.TakenAtUtc"/> relative to the earliest in the list.
    ///
    /// THIS DERIVATION IS HONEST HERE AND NOWHERE ELSE. Every stamp these tests produce is
    /// <see cref="Start"/> plus an offset the test chose, so the wall clock and the timeline cannot
    /// disagree and reading one off the other loses nothing. In production the caller owns a real
    /// monotonic clock and must never build a timeline out of wall-clock stamps - doing that is precisely
    /// the defect of issue #4686. A test that is ABOUT a stepping clock therefore does not use this: it
    /// states both the stamp and the timeline position itself, because that is the only way the two can
    /// be made to disagree.
    /// </summary>
    public static List<TimelineSample> OnTheTimeline(IReadOnlyList<ResourceSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0) return [];
        var earliest = samples.Min(sample => sample.TakenAtUtc);
        return samples.Select(sample => new TimelineSample(sample, sample.TakenAtUtc - earliest)).ToList();
    }
}
