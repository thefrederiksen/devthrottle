using CcDirector.Core.Machine;
using CcDirector.Core.Machine.ResourceSummaries;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Core.UnitTests.Machine;

/// <summary>
/// The sampler's reads reaching the five-minute summary: the shape conversion keeps every unknown unknown, and
/// the feed closes a window on time through CloseIfDue.
/// </summary>
[Trait("Category", "Resources")]
public sealed class ResourceSummaryFeedTests
{
    private const ulong Gb = ResourceSampleBuilder.Gigabyte;
    private static readonly DateTime At = ResourceSampleBuilder.DefaultTakenAtUtc;

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
        var sample = new ResourceSampleBuilder().At(At).Cpu(88).Memory(16 * Gb, 6 * Gb).Build();

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
        var sample = new ResourceSampleBuilder().CpuUnreadable("the counters are switched off").Build();

        var summary = ResourceSummarySampleFold.From(new ResourceSamplerState(sample, Verdict(ResourceLevel.Unknown, ResourceLevel.Normal)), [])!;

        Assert.Null(summary.ProcessorPercent);
        Assert.Equal("the counters are switched off", summary.ProcessorUnreadableReason);
        Assert.NotNull(summary.MemoryUsedBytes);
    }

    [Fact]
    public void From_AnImpossibleProcessorFigure_IsNullAndSaysItCannotBeTrue()
    {
        var sample = new ResourceSampleBuilder().Build() with { Cpu = CpuReading.Read(400, null, 8) };

        var summary = ResourceSummarySampleFold.From(new ResourceSamplerState(sample, Verdict(ResourceLevel.Critical, ResourceLevel.Normal)), [])!;

        Assert.Null(summary.ProcessorPercent);
        Assert.Contains("cannot be true", summary.ProcessorUnreadableReason);
    }

    [Fact]
    public void From_ASessionWhoseEveryProcessRefusedAFigure_IsNull_NotTheZeroItsSumWouldGive()
    {
        var refused = new ResourceSessionInput(new SessionUsage("s-1", 10, 0, 0, [10, 11], 2, 2), "1", "Marketing");

        var session = Assert.Single(ResourceSummarySampleFold.From(
            new ResourceSamplerState(new ResourceSampleBuilder().Build(), Verdict(ResourceLevel.Normal, ResourceLevel.Normal)), [refused])!.Sessions);

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
            new ResourceSamplerState(new ResourceSampleBuilder().Build(), Verdict(ResourceLevel.Normal, ResourceLevel.Normal)), [partial])!.Sessions);

        Assert.Equal(12, session.ProcessorPercent);
        Assert.Equal((long)(2 * Gb), session.MemoryBytes);
        Assert.Equal(1, session.ProcessesWithUnknownFigures);
        Assert.Null(session.UnreadableReason);
    }

    [Fact]
    public void Feed_AWindowClosesThroughCloseIfDue_EvenWhenTheLastSampleIsLate()
    {
        var time = new FakeTimeProvider(At);
        var probe = new FakeResourceProbe();
        using var sampler = new ResourceSampler(probe, _ => Verdict(ResourceLevel.Normal, ResourceLevel.Normal), time);
        var outbox = new ResourceSummaryOutbox(time);
        using var feed = new ResourceSummaryFeed(sampler, new ResourceSummaryRecorder(outbox, "soren-north"), time, () => []);

        // Five minutes of reads, stamped 12:00:00 to 12:04:55.
        for (var i = 0; i < 60; i++)
        {
            probe.Set(new ResourceSampleBuilder().At(At.AddSeconds(i * 5)).Build());
            sampler.Tick();
            time.Advance(ResourceSampling.Interval);
        }
        Assert.Equal(0, outbox.Count);

        // The clock is now past 12:05:00 but the probe's next sample still carries the old stamp, so only
        // CloseIfDue can close the window.
        time.Advance(TimeSpan.FromSeconds(1));
        sampler.Tick();

        Assert.Equal(1, outbox.Count);
        Assert.Equal(At, Assert.Single(outbox.Peek(10)).WindowStartUtc);
    }

    [Fact]
    public void Feed_Dispose_StopsFeedingTheRecorder()
    {
        var time = new FakeTimeProvider(At);
        var probe = new FakeResourceProbe();
        using var sampler = new ResourceSampler(probe, _ => Verdict(ResourceLevel.Normal, ResourceLevel.Normal), time);
        var outbox = new ResourceSummaryOutbox(time);
        var feed = new ResourceSummaryFeed(sampler, new ResourceSummaryRecorder(outbox, "m"), time, () => []);
        sampler.Tick();
        feed.Dispose();

        time.Advance(TimeSpan.FromMinutes(6));
        sampler.Tick();

        Assert.Equal(0, outbox.Count);
    }
}
