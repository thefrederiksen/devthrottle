using CcDirector.Core.Machine;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// Builds <see cref="ResourceSample"/> values for the verdict tests, so each test states only the
/// figures it is about and everything else is a quiet, readable machine.
///
/// Time comes only from <see cref="Start"/> plus an offset the test chooses - never the clock - which is
/// what keeps these tests in the half of the suite that forbids wall-clock dependence.
/// </summary>
internal sealed record ResourceSampleBuilder
{
    public static readonly DateTime Start = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    public const ulong Gigabyte = 1024UL * 1024 * 1024;

    public TimeSpan At { get; init; }
    public CpuReading Cpu { get; init; } = CpuReading.Read(totalBusyPercent: 10, busiestCorePercent: 30, logicalCoreCount: 32);
    public MachineMemoryReading? Memory { get; init; }
    public SwapReading Swap { get; init; } = SwapReading.Read(8 * Gigabyte, 1 * Gigabyte);
    public DiskSetReading Disks { get; init; } = DiskSetReading.Read(new[]
    {
        PhysicalDiskReading.Read("sda", DiskKind.SolidState, 3, 0, 1_000_000, 0.2, 0.3, 0.01),
    });
    public IReadOnlyList<VolumeReading> Volumes { get; init; } = new[]
    {
        VolumeReading.Read("/", 900 * Gigabyte, 400 * Gigabyte, new[] { "sda" }),
    };
    public StallReading Stall { get; init; } = StallReading.Read(StallSource.LinuxPressureStall,
        cpuStalledPercent: 0.5, memoryStalledPercent: 0.1, ioStalledPercent: 1.0, runQueueLength: 2);
    public ProcessSetReading Processes { get; init; } = ProcessSetReading.Read(new[]
    {
        new ProcessReading(1, null, "systemd", Start.AddDays(-1), 0.1, 10_000_000, 0, 0),
    });

    public ResourceSample Build()
    {
        var takenAt = Start + At;
        return new ResourceSample(
            takenAt,
            Cpu,
            Memory ?? MachineMemoryReading.Read(39 * Gigabyte, 26 * Gigabyte, takenAt),
            Swap,
            Disks,
            Volumes,
            NetworkReading.Unreadable("not used by these tests"),
            Stall,
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
    /// THE DEVLINUX READINGS OF 2026-10-15 (mission section 2), the fixture the diagnosis must get right.
    ///
    /// Programs stalled on the disk about 32% of the time against 0.06% for the processor and 0.17% for
    /// memory; 26 GiB of 39 GiB memory available; the processor 80% idle with a load of 19 to 23 on 32
    /// cores. The spinning hard drive sdc, inside the root volume beside two solid-state drives, was 75%
    /// busy at 26 milliseconds per write while the solid-state drives sat near idle. The busiest disk
    /// writer was a test run inside session 140.
    /// </summary>
    public static ResourceSampleBuilder Devlinux(TimeSpan offset)
    {
        var wobble = (offset.TotalSeconds % 15) / 15.0; // the figures moved a little, as real ones do
        return new ResourceSampleBuilder
        {
            Cpu = CpuReading.Read(totalBusyPercent: 19 + wobble, busiestCorePercent: 100, logicalCoreCount: 32),
            Swap = SwapReading.Read(8 * Gigabyte, (ulong)(0.4 * Gigabyte)),
            Disks = DiskSetReading.Read(new[]
            {
                PhysicalDiskReading.Read("sda", DiskKind.SolidState, 2 + wobble, 100_000, 400_000, 0.2, 0.4, 0.01),
                PhysicalDiskReading.Read("sdb", DiskKind.SolidState, 1 + wobble, 50_000, 200_000, 0.2, 0.3, 0.01),
                PhysicalDiskReading.Read("sdc", DiskKind.Spinning, 74 + 2 * wobble, 300_000, 9_000_000, 14, 25.5 + wobble, 3.1),
            }),
            Volumes = new[]
            {
                VolumeReading.Read("/", 3_200 * Gigabyte, 2_900 * Gigabyte, new[] { "sda", "sdb", "sdc" }),
                VolumeReading.Read("/boot/efi", 1 * Gigabyte, (ulong)(0.99 * Gigabyte), new[] { "sda" }),
            },
            Stall = StallReading.Read(StallSource.LinuxPressureStall,
                cpuStalledPercent: 0.06, memoryStalledPercent: 0.17, ioStalledPercent: 31 + 2 * wobble,
                runQueueLength: 19 + 4 * wobble, processesWaitingOnDisk: 14),
            Processes = ProcessSetReading.Read(new[]
            {
                new ProcessReading(1, null, "systemd", Start.AddDays(-3), 0.0, 12_000_000, 0, 0),
                new ProcessReading(4000, 1, "agent", Start.AddHours(-2), 1.5, 600_000_000, 1_000, 20_000),
                new ProcessReading(4100, 4000, "dotnet test", Start.AddMinutes(-5), 6.0, 2_000_000_000, 100_000, 6_500_000),
                new ProcessReading(5000, 1, "code", Start.AddHours(-1), 3.0, 900_000_000, 0, 50_000),
                new ProcessReading(6000, 1, "grep", Start.AddMinutes(-1), 2.0, 5_000_000, 2_000_000, 0),
            }),
        };
    }

    /// <summary>The session map the devlinux fixture is diagnosed with: the agent at process 4000 is session 140.</summary>
    public static readonly IReadOnlyDictionary<int, string> DevlinuxSessions = new Dictionary<int, string> { [4000] = "140" };
}
