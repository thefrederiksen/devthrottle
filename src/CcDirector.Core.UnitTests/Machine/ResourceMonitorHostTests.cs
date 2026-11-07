using CcDirector.Core.Machine;
using CcDirector.Core.Machine.ResourceSummaries;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// What the Director does with each read: the summary's shape keeps every unknown unknown, the five-minute
/// window closes on time through CloseIfDue, the read reaches the 24-hour recording, each session's figure
/// covers its whole process tree, and shutting down stops the sampler before the recording.
/// </summary>
[Trait("Category", "Resources")]
public sealed class ResourceMonitorHostTests
{
    private const ulong Gb = FakeSampleBuilder.Gigabyte;
    private static readonly DateTime At = FakeSampleBuilder.DefaultTakenAtUtc;

    private static ResourcePressureVerdict Verdict(ResourceLevel cpu, ResourceLevel memory) =>
        new([
            new ResourceVerdict(ResourceKind.Cpu, cpu, "cpu"),
            new ResourceVerdict(ResourceKind.Memory, memory, "memory"),
            new ResourceVerdict(ResourceKind.Swap, ResourceLevel.Normal, "swap"),
        ]);

    [Fact]
    public void From_BeforeTheFirstRead_IsNull()
    {
        Assert.Null(ResourceSummarySampleFold.From(ResourceSamplerState.NotYetSampled, []));
    }

    [Fact]
    public void From_AReadableSample_CarriesTheFiguresAndTheLevelsItWasGiven()
    {
        var sample = new FakeSampleBuilder().At(At).Cpu(88).Memory(16 * Gb, 6 * Gb).Build();

        var summary = ResourceSummarySampleFold.From(new ResourceSamplerState(sample, Verdict(ResourceLevel.Tight, ResourceLevel.Normal)), [])!;

        Assert.Equal(At, summary.AtUtc);
        Assert.Equal(88, summary.ProcessorPercent!.Value, 6);
        Assert.Null(summary.ProcessorUnreadableReason);
        Assert.Equal(ResourceLevel.Tight, summary.ProcessorLevel);
        Assert.Equal((long)(10 * Gb), summary.MemoryUsedBytes);
        Assert.Equal((long)(16 * Gb), summary.MemoryTotalBytes);
        Assert.Equal(ResourceLevel.Normal, summary.MemoryLevel);
    }

    [Fact]
    public void From_AnUnreadableProcessor_IsNullWithItsReason_NeverZero()
    {
        var sample = new FakeSampleBuilder().CpuUnreadable("the counters are switched off").Build();

        var summary = ResourceSummarySampleFold.From(new ResourceSamplerState(sample, Verdict(ResourceLevel.Unknown, ResourceLevel.Normal)), [])!;

        Assert.Null(summary.ProcessorPercent);
        Assert.Equal("the counters are switched off", summary.ProcessorUnreadableReason);
        Assert.NotNull(summary.MemoryUsedBytes);
    }

    [Fact]
    public void From_AnImpossibleProcessorFigure_IsNullAndSaysItCannotBeTrue()
    {
        var sample = new FakeSampleBuilder().Build() with { Cpu = CpuReading.Read(400, null, 8) };

        var summary = ResourceSummarySampleFold.From(new ResourceSamplerState(sample, Verdict(ResourceLevel.Critical, ResourceLevel.Normal)), [])!;

        Assert.Null(summary.ProcessorPercent);
        Assert.Contains("cannot be true", summary.ProcessorUnreadableReason);
    }

    [Fact]
    public void From_SessionsThatCouldNotBeMeasured_CarryNoSharesAndSayWhy_NeverAnEmptyListAlone()
    {
        var sample = new FakeSampleBuilder().ProcessesUnreadable("the process table was locked").Build();

        var summary = ResourceSummarySampleFold.From(new ResourceSamplerState(sample, Verdict(ResourceLevel.Normal, ResourceLevel.Normal)), null)!;

        Assert.Empty(summary.Sessions);
        Assert.Equal("the process list could not be read: the process table was locked", summary.SessionsUnreadableReason);
    }

    [Fact]
    public void From_SessionsMeasuredAndNoneRunning_IsAnEmptyListWithNoReason()
    {
        var summary = ResourceSummarySampleFold.From(
            new ResourceSamplerState(new FakeSampleBuilder().Build(), Verdict(ResourceLevel.Normal, ResourceLevel.Normal)), [])!;

        Assert.Empty(summary.Sessions);
        Assert.Null(summary.SessionsUnreadableReason);
    }

    [Fact]
    public void From_ASessionWhoseEveryProcessRefusedAFigure_IsNull_NotTheZeroItsSumWouldGive()
    {
        var refused = new ResourceSessionInput(new SessionUsage("s-1", 10, 0, 0, [10, 11], 2, 2), "1", "Marketing");

        var session = Assert.Single(ResourceSummarySampleFold.From(
            new ResourceSamplerState(new FakeSampleBuilder().Build(), Verdict(ResourceLevel.Normal, ResourceLevel.Normal)), [refused])!.Sessions);

        Assert.Null(session.ProcessorPercent);
        Assert.Null(session.MemoryBytes);
        Assert.Equal("no process in this session's tree had a readable figure", session.UnreadableReason);
        Assert.Equal("Marketing", session.Factory);
    }

    [Fact]
    public void From_ASessionWithSomeProcessesRefused_KeepsItsFiguresAsLowerBounds()
    {
        var partial = new ResourceSessionInput(new SessionUsage("s-1", 10, 12, 2 * Gb, [10, 11, 12], 1, 0), "1", null);

        var session = Assert.Single(ResourceSummarySampleFold.From(
            new ResourceSamplerState(new FakeSampleBuilder().Build(), Verdict(ResourceLevel.Normal, ResourceLevel.Normal)), [partial])!.Sessions);

        Assert.Equal(12, session.ProcessorPercent);
        Assert.Equal((long)(2 * Gb), session.MemoryBytes);
        Assert.Equal(1, session.ProcessesWithUnknownFigures);
        Assert.Null(session.UnreadableReason);
    }

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Directory.CreateDirectory(RecordingDirectory);
            Sampler = new ResourceSampler(Probe, _ => Verdict(ResourceLevel.Normal, ResourceLevel.Normal), Time);
            Host = new ResourceMonitorHost(
                Sampler, () => Sessions, new ResourceSummaryRecorder(Outbox, "soren-north"),
                new ResourceRecorder(RecordingDirectory, Time), Time);
        }

        public string RecordingDirectory { get; } =
            Path.Combine(Path.GetTempPath(), "resource-monitor-host-tests", Guid.NewGuid().ToString("N"));
        public FakeTimeProvider Time { get; } = new(At);
        public FakeResourceProbe Probe { get; } = new();
        public ResourceSampler Sampler { get; }
        public ResourceSummaryOutbox Outbox => _outbox ??= new ResourceSummaryOutbox(Time);
        private ResourceSummaryOutbox? _outbox;
        public ResourceMonitorHost Host { get; }
        public IReadOnlyList<DirectorSessionInfo> Sessions { get; set; } = [];

        public void Read(ResourceSample sample)
        {
            Probe.Set(sample);
            Sampler.Tick();
            Time.Advance(ResourceSampling.Interval);
        }

        public void Dispose()
        {
            Host.Dispose();
            if (Directory.Exists(RecordingDirectory)) Directory.Delete(RecordingDirectory, recursive: true);
        }
    }

    [Fact]
    public void Host_AWindowClosesThroughCloseIfDue_EvenWhenTheLastSampleIsLate()
    {
        using var rig = new Rig();

        // Five minutes of reads, stamped 12:00:00 to 12:04:55.
        for (var i = 0; i < 60; i++) rig.Read(new FakeSampleBuilder().At(At.AddSeconds(i * 5)).Build());
        Assert.Equal(0, rig.Outbox.Count);

        // The clock is now past 12:05:00 but the next sample still carries the old stamp, so only CloseIfDue
        // can close the window.
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.Sampler.Tick();

        Assert.Equal(1, rig.Outbox.Count);
        Assert.Equal(At, Assert.Single(rig.Outbox.Peek(10)).WindowStartUtc);
    }

    [Fact]
    public void Host_EveryReadReachesTheRecording_OnDisk()
    {
        using var rig = new Rig();

        // The recording writes once a minute, so a minute and a bit of reads must leave a file with content.
        for (var i = 0; i < 14; i++) rig.Read(new FakeSampleBuilder().At(At.AddSeconds(i * 5)).Build());
        rig.Host.Dispose();

        var files = Directory.GetFiles(rig.RecordingDirectory, "*", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        Assert.Contains(files, file => new FileInfo(file).Length > 0);
    }

    /// <summary>
    /// THE RECORDING CARRIES A DIAGNOSIS, which until issue #4719 it never did: the diagnoser was a tested pure
    /// function with no caller anywhere in the product, and every moment went to disk with Diagnosis: null. A
    /// recording with a hole where the verdict belongs cannot be replayed to show what the product would have
    /// said, so the platform thresholds - still proposals, measured on one Mac - could never be confirmed
    /// against a real machine.
    ///
    /// This reads the written file back rather than inspecting what was passed in, because the point of the
    /// change is that the diagnosis reaches DISK.
    /// </summary>
    [Fact]
    public void Host_TheRecordedMoment_CarriesADiagnosis_NotNull()
    {
        using var rig = new Rig();
        rig.Sessions = [new DirectorSessionInfo("s-140", 200, "140 (Resource Monitor)", null)];

        for (var i = 0; i < 14; i++)
        {
            rig.Read(new FakeSampleBuilder()
                .At(At.AddSeconds(i * 5))
                .Process(200, 1, "claude", 2, 400 * FakeSampleBuilder.Megabyte)
                .Process(201, 200, "dotnet test", 60, 2 * Gb)
                .Build());
        }
        rig.Host.Dispose();

        var read = ResourceRecordingReader.ReadRetained(rig.RecordingDirectory);
        Assert.NotEmpty(read.Lines);

        // Every written line carries a diagnosis object, and none is the null that used to be hard-coded. The
        // recorder writes without indentation, so this substring is exact rather than hopeful.
        Assert.All(read.Lines, line =>
            Assert.DoesNotContain("\"diagnosis\":null", line.Json, StringComparison.Ordinal));

        // NOT MERELY NON-NULL. Review of pull request 4739 pointed out that the assertions above would be
        // satisfied by SlownessDiagnosis.CannotDiagnose on every line - an object that says nothing, written
        // every five seconds, which is barely better than the null it replaced. So this demands a diagnosis
        // that actually reached a verdict and carries the sentences a person reads.
        Assert.All(read.Lines, line =>
        {
            Assert.Contains("\"couldDiagnose\":true", line.Json, StringComparison.Ordinal);
            Assert.Contains("\"summary\":", line.Json, StringComparison.Ordinal);
            Assert.Contains("\"nextStep\":", line.Json, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Host_ASessionsFigure_CoversItsChildProcesses()
    {
        using var rig = new Rig();
        rig.Sessions = [new DirectorSessionInfo("s-140", 200, "140 (Resource Monitor)", "Marketing")];

        rig.Read(new FakeSampleBuilder()
            .Process(200, 1, "claude", 2, 400 * FakeSampleBuilder.Megabyte)
            .Process(201, 200, "dotnet test", 60, 2 * Gb)
            .Process(300, 1, "chrome", 9, 3 * Gb)
            .Build());

        var session = Assert.Single(rig.Host.Latest!.Sessions!);
        Assert.Equal("s-140", session.Usage.SessionId);
        Assert.Equal(62, session.Usage.CpuPercent, 6);
        Assert.Equal(2 * Gb + 400 * FakeSampleBuilder.Megabyte, session.Usage.MemoryBytes);
        Assert.Equal("Marketing", session.Factory);
    }

    [Fact]
    public void Host_WhenTheProcessListCannotBeRead_TheSessionsAreNotKnown_NotZero()
    {
        using var rig = new Rig();
        rig.Sessions = [new DirectorSessionInfo("s-140", 200, "140", null)];

        rig.Read(new FakeSampleBuilder().ProcessesUnreadable("the platform refused the process list").Build());

        Assert.Null(rig.Host.Latest!.Sessions);
    }

    [Fact]
    public void Host_Dispose_StopsTheSamplerBeforeTheRecording_AndNoReadFollows()
    {
        var rig = new Rig();
        rig.Sampler.Start();
        rig.Time.Advance(ResourceSampling.Interval);
        var reads = rig.Probe.ReadCount;

        rig.Host.Dispose();
        rig.Time.Advance(ResourceSampling.Interval * 5);
        rig.Sampler.Tick();

        Assert.Equal(reads, rig.Probe.ReadCount);
        rig.Dispose();
    }
}
