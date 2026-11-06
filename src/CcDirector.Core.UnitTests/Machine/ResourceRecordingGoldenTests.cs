using CcDirector.Core.Machine;
using CcDirector.TestInfrastructure;
using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// ONE COMMITTED RECORDING HOLDS THE TWO ENDS OF THE CONTRACT TOGETHER.
///
/// The recorder writes the 24-hour recording in C#; <c>cc-devthrottle machine diagnose</c> reads it in
/// Python. Two hand-written descriptions of one file format drift apart, so there is one file instead:
/// this test proves the recorder writes it byte for byte, and the Python tests read the very same file
/// (<c>tools/cc-devthrottle/tests/test_resource_diagnose.py</c>). A change to the format fails here first,
/// and regenerating the file makes the Python tests read the new shape.
///
/// To regenerate after a deliberate format change, run this test once with
/// <c>CC_WRITE_RESOURCE_RECORDING_FIXTURE=1</c>, read the difference, and commit it.
///
/// THE SCENARIO is devlinux's slowdown of 2026-10-15 replayed on 2026-11-06 from 08:21 to 08:27 UTC: the
/// processor and memory healthy, and from 08:23 a test run in session 140 stalls programs on the spinning
/// disk sdc a third of the time. At 08:25 session 98 ends and leaves its compiler server running. The first
/// sample has no processor figure, as every first sample must not.
///
/// THE VERDICTS AND DIAGNOSES IN IT ARE WRITTEN BY HAND, standing in for <c>ResourcePressure</c> and
/// <c>SlownessDiagnoser</c>, which phase 1 is building in parallel. They use the mission's approved
/// sentence. The recorder stores whatever those functions say; this file says nothing about whether they
/// will say it.
/// </summary>
[Trait("Category", "Resources")]
public sealed class ResourceRecordingGoldenTests : IDisposable
{
    internal static readonly Guid RunId = new("6f1c2a9e-4b7d-4e35-9c1a-2d8e5f0b7a31");
    internal static readonly DateTimeOffset Start = new(2026, 11, 6, 8, 21, 0, TimeSpan.Zero);
    internal const int SampleCount = 72; // six minutes

    private static readonly string FixturePath = Path.Combine(RepositorySourceIndex.Root,
        "tools", "cc-devthrottle", "tests", "fixtures", "resource-recording", ResourceRecorder.FileName(0));

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "resource-recording-golden", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Recorder_TheSlowMachineScenario_WritesTheCommittedRecordingByteForByte()
    {
        // Arrange
        var clock = new FixedStepClock(Start);

        // Act
        using (var recorder = new ResourceRecorder(_directory, clock, RunId))
        {
            for (var i = 0; i < SampleCount; i++)
            {
                recorder.Record(SlowMachine.Moment(i, clock.GetUtcNow().UtcDateTime));
                clock.Pass(ResourceRecorder.SampleInterval);
            }
        }

        // Assert
        var written = File.ReadAllText(Path.Combine(_directory, ResourceRecorder.FileName(0)));
        if (Environment.GetEnvironmentVariable("CC_WRITE_RESOURCE_RECORDING_FIXTURE") == "1")
        {
            Directory.CreateDirectory(Path.Combine(RepositorySourceIndex.Root, "tools", "cc-devthrottle", "tests", "fixtures", "resource-recording"));
            File.WriteAllText(FixturePath, written);
        }

        Assert.True(File.Exists(FixturePath), $"the committed recording is missing: {FixturePath}");
        Assert.Equal(File.ReadAllText(FixturePath), written);
        Assert.Equal(SampleCount, ResourceRecordingReader.ReadRetained(_directory).Lines.Count);
    }

    private sealed class FixedStepClock : TimeProvider
    {
        private DateTimeOffset _wall;
        private long _timestamp;
        public FixedStepClock(DateTimeOffset wall) => _wall = wall;
        public override DateTimeOffset GetUtcNow() => _wall;
        public override long GetTimestamp() => _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Pass(TimeSpan by)
        {
            _wall = _wall.Add(by);
            _timestamp += by.Ticks;
        }
    }

    /// <summary>The scenario, one sample at a time. Deterministic: sample i is always the same.</summary>
    private static class SlowMachine
    {
        private const ulong GiB = 1UL << 30;
        private const ulong MiB = 1UL << 20;
        private static readonly DateTime Boot = new(2026, 11, 4, 9, 12, 0, DateTimeKind.Utc);
        private static readonly DateTime SessionStart140 = new(2026, 11, 6, 7, 40, 0, DateTimeKind.Utc);
        private static readonly DateTime SessionStart98 = new(2026, 11, 6, 6, 5, 0, DateTimeKind.Utc);
        private static readonly DateTime TestRunStart = new(2026, 11, 6, 8, 22, 58, DateTimeKind.Utc);

        private const string Session140 = "2ed1adf4-a275-4478-9618-5443ac09427d";
        private const string Session140Name = "Resource Monitor - Developer - 24-hour local recording and the diagnose command";
        private const string Session98 = "9b80c3d1-5e2f-4a77-8c10-7f3a6e1d2b94";
        private const string Session98Name = "Factory Inbox - Developer - phase 3";

        public static ResourceMoment Moment(int i, DateTime takenAtUtc)
        {
            var first = i == 0;
            var diskLoad = i >= 24;       // from 08:23:00
            var session98Ended = i >= 48; // from 08:25:00
            var wobble = (i % 6) * 0.7;   // so a minute's average and peak differ

            var cpu = first
                ? CpuReading.Unreadable(ResourceUnreadableReasons.FirstSample, 16)
                : CpuReading.Read(Math.Round((diskLoad ? 21.5 : 9.5) + wobble, 1), diskLoad ? 64 : 41, 16);
            var available = (diskLoad ? 26.0 : 27.4) * GiB - (ulong)(i % 4) * 96 * MiB;
            var memory = MachineMemoryReading.Read(39 * GiB, (ulong)available, takenAtUtc);
            var swap = SwapReading.Read(8 * GiB, (ulong)(0.4 * GiB));
            var stall = StallReading.Read(StallSource.LinuxPressureStall,
                cpuStalledPercent: 0.06, memoryStalledPercent: 0.17,
                ioStalledPercent: diskLoad ? Math.Round(31 + wobble, 1) : 1.8,
                // devlinux showed a load of 19 to 23 while 80 per cent idle: tasks waiting on the disk count in it.
                loadAverageOneMinute: diskLoad ? 21.4 : 2.1);

            var disks = DiskEvidenceSet.Read(new[]
            {
                DiskEvidence.Read("sda", DiskKind.SolidState, true, diskLoad ? 6 : 2, 0.21, 0.38, 0.1),
                DiskEvidence.Read("sdc", DiskKind.Spinning, true, diskLoad ? 75 : 4, diskLoad ? 9.4 : 4.1,
                    diskLoad ? 26 : 5.2, diskLoad ? 3.1 : 0.1),
            });

            double? Rate(double value) => first ? null : value;

            var processes = new List<ProcessReading>
            {
                new(1, null, "systemd", Boot, Rate(0.0), 14 * MiB, null, null),
                new(2104, 1, "gnome-shell", Boot, Rate(1.2), 612 * MiB, null, null),
                new(38810, 1, "agent", SessionStart140, Rate(0.9), 488 * MiB, null, null),
                new(39001, session98Ended ? 1 : 38722, "VBCSCompiler", SessionStart98.AddMinutes(3), Rate(session98Ended ? 6.1 : 0.4),
                    1_380 * MiB, null, null),
                new(39522, 1, "cc-director", Boot.AddHours(1), Rate(0.6), 905 * MiB, null, null),
                // A process the platform refuses: listed, named, figures unknown, never zero.
                new(812, 1, "fwupd", null, null, null, null, null),
            };
            if (!session98Ended) processes.Add(new(38722, 1, "agent", SessionStart98, Rate(0.3), 455 * MiB, null, null));
            if (diskLoad)
            {
                processes.Add(new(41872, 38810, "dotnet", TestRunStart, i == 24 ? null : 12.4 + wobble, 1_240 * MiB,
                    1.5 * MiB, 41 * MiB));
            }

            var tree140 = diskLoad ? new[] { 38810, 41872 } : new[] { 38810 };
            var sessions = new List<RecordedSession>
            {
                new(new SessionUsage(Session140, 38810,
                    first ? 0 : 0.9 + (diskLoad && i != 24 ? 12.4 + wobble : 0),
                    488 * MiB + (diskLoad ? 1_240 * MiB : 0), tree140,
                    ProcessesWithUnknownProcessor: first ? 1 : (i == 24 ? 1 : 0), ProcessesWithUnknownMemory: 0),
                    Session140Name),
            };
            if (!session98Ended)
            {
                sessions.Add(new(new SessionUsage(Session98, 38722, first ? 0 : 0.7, 1_835 * MiB, new[] { 38722, 39001 },
                    ProcessesWithUnknownProcessor: first ? 2 : 0, ProcessesWithUnknownMemory: 0), Session98Name));
            }

            var cpuVerdict = first
                ? new ResourceVerdict(ResourceKind.Cpu, ResourceLevel.Unknown, "cannot read: first sample, no interval yet")
                : new ResourceVerdict(ResourceKind.Cpu, ResourceLevel.Normal, $"{cpu.TotalBusyPercent:F0}% busy");
            var verdict = new ResourcePressureVerdict(new[]
            {
                cpuVerdict,
                new ResourceVerdict(ResourceKind.Memory, ResourceLevel.Normal, $"{available / GiB:F1} GB available of 39.0 GB"),
                new ResourceVerdict(ResourceKind.Swap, ResourceLevel.Normal, "0.4 GB used of 8.0 GB"),
            });

            var diagnosis = diskLoad
                ? new SlownessDiagnosis(true, true, SlownessCause.Disk,
                    new[]
                    {
                        $"programs were stalled waiting on the disk {stall.IoStalledPercent:F0}% of the time",
                        "the hard drive sdc is 75% busy at 26 milliseconds per write",
                        "sdc is a spinning hard drive inside the volume the machine runs from",
                        $"the processor is {cpu.TotalBusyPercent:F0}% busy and {available / GiB:F1} GB of memory is available, so neither is the cause",
                    },
                    new SlownessCulprit("dotnet", 41872, Session140, Session140Name, "sdc", DiskKind.Spinning),
                    "Stop or move the test run in session 140 off sdc; nothing on this machine should build on the spinning disk.",
                    $"Slow because of the disk: the hard drive sdc is 75% busy at 26 milliseconds per write, and programs are stalled waiting on it {stall.IoStalledPercent:F0}% of the time")
                : new SlownessDiagnosis(true, false, SlownessCause.None,
                    new[] { "programs were stalled waiting on the disk 2% of the time, on memory 0% and on the processor 0%" },
                    null, "Nothing to do.", "Not slow: no resource is holding programs up");

            return new ResourceMoment(
                new ResourceSample(takenAtUtc, cpu, memory, swap, stall, disks, ProcessSetReading.Read(processes)),
                sessions, verdict, diagnosis);
        }
    }
}
