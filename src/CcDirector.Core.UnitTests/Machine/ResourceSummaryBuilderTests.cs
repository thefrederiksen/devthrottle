using CcDirector.Core.Machine;
using CcDirector.Core.Machine.ResourceSummaries;
using CcDirector.Gateway.Contracts;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// The five-minute resource summary on the Director (Mission: Resource Monitor, section 6): the pure builder, the window
/// recorder and the 24-hour outbox. Every sample is hand-made and every clock is a fake, so nothing here reads the
/// machine it runs on or waits on real time.
/// </summary>
[Trait("Category", "Resources")]
public class ResourceSummaryBuilderTests
{
    private static readonly DateTime W0 = new(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc);
    private const long Gb = 1_000_000_000;

    private static ResourceSummarySample Quiet(DateTime at, double cpu = 20, long used = 8 * Gb, params ResourceSummarySessionSample[] sessions) =>
        new(at, cpu, null, ResourceLevel.Normal, used, 16 * Gb, null, ResourceLevel.Normal, sessions);

    /// <summary>Sixty five-second samples: one whole window.</summary>
    private static List<ResourceSummarySample> QuietWindow(DateTime start) =>
        Enumerable.Range(0, 60).Select(i => Quiet(start.AddSeconds(5 * i))).ToList();

    [Fact]
    public void Build_NinetySecondRedSpike_SurvivesAsPeakAndCrossing()
    {
        // Arrange: a quiet window at 20% with 90 seconds (18 samples) pinned at 99% and judged red, starting at 02:00.
        var samples = QuietWindow(W0);
        for (var i = 24; i < 42; i++)
            samples[i] = samples[i] with { ProcessorPercent = 99, ProcessorLevel = ResourceLevel.Critical };

        // Act
        var window = ResourceSummaryBuilder.Build(W0, "devlinux", samples);

        // Assert: the average alone would hide it (about 44%), the peak and the crossing do not.
        Assert.InRange(window.Machine.ProcessorAveragePercent!.Value, 43, 45);
        Assert.Equal(99, window.Machine.ProcessorPeakPercent);
        var crossing = Assert.Single(window.Machine.Crossings);
        Assert.Equal(ResourceSummaryContract.Processor, crossing.Resource);
        Assert.Equal(ResourceSummaryContract.Red, crossing.Level);
        Assert.Equal(W0.AddMinutes(2), crossing.StartedUtc);
        Assert.Equal(90, crossing.Seconds);
        Assert.Equal(99, crossing.PeakPercent);
    }

    [Fact]
    public void Build_AmberThenRedInOneRun_RecordsOneCrossingAtTheWorstLevel()
    {
        var samples = QuietWindow(W0);
        for (var i = 10; i < 20; i++)
            samples[i] = samples[i] with { MemoryUsedBytes = 15 * Gb, MemoryLevel = i < 15 ? ResourceLevel.Tight : ResourceLevel.Critical };

        var window = ResourceSummaryBuilder.Build(W0, "devlinux", samples);

        var crossing = Assert.Single(window.Machine.Crossings);
        Assert.Equal(ResourceSummaryContract.Memory, crossing.Resource);
        Assert.Equal(ResourceSummaryContract.Red, crossing.Level);
        Assert.Equal(50, crossing.Seconds);
        Assert.Equal(93.75, crossing.PeakPercent);
        Assert.Equal(15 * Gb, window.Machine.MemoryPeakUsedBytes);
        Assert.Equal(16 * Gb, window.Machine.MemoryTotalBytes);
    }

    [Fact]
    public void Build_ProcessorNeverRead_IsNullWithTheReason_NeverZero()
    {
        var samples = QuietWindow(W0)
            .Select(s => s with { ProcessorPercent = null, ProcessorUnreadableReason = "/proc/stat could not be opened", ProcessorLevel = ResourceLevel.Unknown })
            .ToList();

        var window = ResourceSummaryBuilder.Build(W0, "devlinux", samples);

        Assert.False(window.Machine.ProcessorCouldRead);
        Assert.Null(window.Machine.ProcessorAveragePercent);
        Assert.Null(window.Machine.ProcessorPeakPercent);
        Assert.Equal(0, window.Machine.ProcessorSamples);
        Assert.Equal("/proc/stat could not be opened", window.Machine.ProcessorUnreadableReason);
        // Memory failed independently of the processor: it still has its real figure.
        Assert.True(window.Machine.MemoryCouldRead);
        Assert.Equal(8 * Gb, window.Machine.MemoryAverageUsedBytes);
    }

    [Fact]
    public void Build_PartlyUnreadable_AveragesOnlyTheSamplesThatRead()
    {
        var samples = QuietWindow(W0);
        for (var i = 0; i < 30; i++) samples[i] = samples[i] with { ProcessorPercent = null, ProcessorUnreadableReason = "busy" };
        for (var i = 30; i < 60; i++) samples[i] = samples[i] with { ProcessorPercent = 40 };

        var window = ResourceSummaryBuilder.Build(W0, "devlinux", samples);

        Assert.True(window.Machine.ProcessorCouldRead);
        Assert.Equal(30, window.Machine.ProcessorSamples);
        Assert.Equal(40, window.Machine.ProcessorAveragePercent);
        Assert.Null(window.Machine.ProcessorUnreadableReason);
    }

    [Fact]
    public void Build_Sessions_CarryTheirOwnFiguresAndTheCrossingTheyCaused()
    {
        var builder = new ResourceSummarySessionSample("s-builder", "Marketing", 60, 2 * Gb, null);
        var writer = new ResourceSummarySessionSample("s-writer", null, 5, 1 * Gb, null);
        var samples = Enumerable.Range(0, 60).Select(i => Quiet(W0.AddSeconds(5 * i), 70, 8 * Gb, builder, writer)).ToList();
        for (var i = 0; i < 20; i++)
            samples[i] = samples[i] with { ProcessorPercent = 97, ProcessorLevel = ResourceLevel.Critical };

        var window = ResourceSummaryBuilder.Build(W0, "devlinux", samples);

        Assert.Equal(2, window.Sessions.Count);
        var hot = window.Sessions.Single(s => s.SessionId == "s-builder");
        Assert.Equal("Marketing", hot.Factory);
        Assert.Equal(60, hot.SampleCount);
        Assert.Equal(60, hot.Figures.ProcessorAveragePercent);
        Assert.Equal(2 * Gb, hot.Figures.MemoryPeakUsedBytes);
        Assert.Null(hot.Figures.MemoryTotalBytes);
        Assert.Equal(ResourceSummaryContract.Red, Assert.Single(hot.Figures.Crossings).Level);
        var quiet = window.Sessions.Single(s => s.SessionId == "s-writer");
        Assert.Null(quiet.Factory);
        Assert.Empty(quiet.Figures.Crossings);
    }

    [Fact]
    public void Build_ProcessesWithUnknownFigures_AreCarriedSoTheFiguresReadAsLowerBounds()
    {
        var samples = Enumerable.Range(0, 60).Select(i => Quiet(W0.AddSeconds(5 * i), 30, 8 * Gb,
            new ResourceSummarySessionSample("s-1", null, 12, 2 * Gb, null, ProcessesWithUnknownFigures: i == 30 ? 3 : 0))).ToList();

        var window = ResourceSummaryBuilder.Build(W0, "devlinux", samples);

        Assert.Equal(3, Assert.Single(window.Sessions).ProcessesWithUnknownFigures);
    }

    [Fact]
    public void Build_IgnoresSamplesOutsideTheWindow()
    {
        var samples = QuietWindow(W0);
        samples.Add(Quiet(W0.AddMinutes(5), cpu: 100));
        samples.Add(Quiet(W0.AddSeconds(-5), cpu: 100));

        var window = ResourceSummaryBuilder.Build(W0, "devlinux", samples);

        Assert.Equal(60, window.SampleCount);
        Assert.Equal(20, window.Machine.ProcessorPeakPercent);
    }

    [Fact]
    public void Build_StartNotOnAFiveMinuteBoundary_Throws()
    {
        Assert.Throws<ArgumentException>(() => ResourceSummaryBuilder.Build(W0.AddMinutes(1), "devlinux", QuietWindow(W0)));
    }

    [Fact]
    public void Recorder_SampleInTheNextWindow_ClosesTheFirstIntoTheOutbox()
    {
        var outbox = new ResourceSummaryOutbox(new FakeTimeProvider(W0));
        var recorder = new ResourceSummaryRecorder(outbox, "devlinux");

        foreach (var sample in QuietWindow(W0)) recorder.Add(sample);
        Assert.Equal(0, outbox.Count);
        recorder.Add(Quiet(W0.AddMinutes(5)));

        var closed = Assert.Single(outbox.Peek(10));
        Assert.Equal(W0, closed.WindowStartUtc);
        Assert.Equal(60, closed.SampleCount);
        Assert.Equal("devlinux", closed.MachineName);
    }

    [Fact]
    public void Recorder_ClockMovesBack_ClosesTheOpenWindowAndStartsTheEarlierOne()
    {
        var outbox = new ResourceSummaryOutbox(new FakeTimeProvider(W0.AddMinutes(10)));
        var recorder = new ResourceSummaryRecorder(outbox, "devlinux");

        recorder.Add(Quiet(W0.AddMinutes(5)));
        recorder.Add(Quiet(W0.AddMinutes(1)));
        recorder.CloseIfDue(W0.AddMinutes(10));

        Assert.Equal(new[] { W0, W0.AddMinutes(5) }, outbox.Peek(10).Select(w => w.WindowStartUtc));
    }

    [Fact]
    public void Recorder_CloseIfDue_OnlyOnceTheFiveMinutesAreOver()
    {
        var outbox = new ResourceSummaryOutbox(new FakeTimeProvider(W0));
        var recorder = new ResourceSummaryRecorder(outbox, "devlinux");
        recorder.Add(Quiet(W0));

        Assert.False(recorder.CloseIfDue(W0.AddMinutes(4).AddSeconds(59)));
        Assert.True(recorder.CloseIfDue(W0.AddMinutes(5)));
        Assert.Equal(1, outbox.Count);
    }

    [Fact]
    public void Outbox_HoldsUntilAcknowledged_OldestFirst()
    {
        var outbox = new ResourceSummaryOutbox(new FakeTimeProvider(W0.AddHours(1)));
        outbox.Add(ResourceSummaryBuilder.Build(W0.AddMinutes(10), "m", QuietWindow(W0.AddMinutes(10))));
        outbox.Add(ResourceSummaryBuilder.Build(W0, "m", QuietWindow(W0)));

        Assert.Equal(new[] { W0, W0.AddMinutes(10) }, outbox.Peek(5).Select(w => w.WindowStartUtc));
        Assert.Equal(1, outbox.Acknowledge(new[] { W0 }));
        Assert.Equal(W0.AddMinutes(10), Assert.Single(outbox.Peek(5)).WindowStartUtc);
    }

    [Fact]
    public void Outbox_WindowOlderThan24Hours_IsDropped()
    {
        var clock = new FakeTimeProvider(W0.AddMinutes(5));
        var outbox = new ResourceSummaryOutbox(clock);
        outbox.Add(ResourceSummaryBuilder.Build(W0, "m", QuietWindow(W0)));

        clock.Advance(TimeSpan.FromHours(24));
        Assert.Single(outbox.Peek(5));
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Empty(outbox.Peek(5));
        Assert.Equal(1, outbox.Dropped);
    }

    [Fact]
    public void Outbox_CapsAtTwentyFourHoursOfWindows_DroppingTheOldest()
    {
        var outbox = new ResourceSummaryOutbox(new FakeTimeProvider(W0.AddHours(30)));
        var start = W0.AddHours(6);
        for (var i = 0; i < ResourceSummaryContract.MaxBufferedWindows + 3; i++)
            outbox.Add(new ResourceSummaryWindow { WindowStartUtc = start.AddMinutes(5 * i), MachineName = "m" });

        Assert.Equal(288, ResourceSummaryContract.MaxBufferedWindows);
        Assert.Equal(288, outbox.Count);
        Assert.Equal(3, outbox.Dropped);
        Assert.Equal(start.AddMinutes(15), outbox.Peek(1)[0].WindowStartUtc);
    }
}
