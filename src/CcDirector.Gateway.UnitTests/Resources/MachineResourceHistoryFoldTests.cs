using CcDirector.Core.Machine.ResourceSummaries;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Stats.Data.Entities;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static CcDirector.Gateway.UnitTests.Resources.ResourceWindows;

namespace CcDirector.Gateway.UnitTests.Resources;

/// <summary>
/// The expanded machine's history as the Gateway folds it (Mission: Resource Monitor, section 5k; CLAUDE.md rule 7): the
/// span the chart lays out, the gaps where no summary arrived, the note on every missing figure, and the one event list -
/// every word and tone the Cockpit draws as sent. Rows go through the real store, so the fold is tested on what the
/// database returns.
/// </summary>
[Trait("Category", "Resources")]
public sealed class MachineResourceHistoryFoldTests : IDisposable
{
    private static readonly DateTime W0 = new(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId Tenant = new("tenant-a");

    private readonly ResourceSummaryTestDb _db = new();
    private readonly ResourceSummaryStore _store;

    public MachineResourceHistoryFoldTests()
    {
        _store = new ResourceSummaryStore(() => _db.Factory, new FakeTimeProvider(new DateTimeOffset(W0.AddHours(1))));
    }

    public void Dispose() => _db.Dispose();

    /// <summary>Store the windows, then fold the span [from, to] exactly as the endpoint does.</summary>
    private MachineResourceHistoryDto History(DateTime from, DateTime to, params ResourceSummaryWindow[] windows)
    {
        _store.Store(Tenant, "director-1", Batch(windows));
        return MachineResourceHistoryFold.Fold("devlinux", 24, from, to,
            _store.MachineWindows(Tenant, MachineResourceHistoryFold.ReadFromUtc(from), to.AddMinutes(1)), _store.SessionWindows(Tenant, "devlinux", from, to.AddMinutes(1)));
    }

    [Fact]
    public void Fold_SendsTheSpanItRead_SoTheChartsRightEdgeIsNow()
    {
        var view = History(W0, W0.AddMinutes(12), Steady(W0, 30, 4 * Gb), Steady(W0.AddMinutes(5), 30, 4 * Gb));

        Assert.Equal(W0, view.FromUtc);
        Assert.Equal(W0.AddMinutes(12), view.ToUtc);
        // 10:10 to 10:12 is the window still being collected - not missing yet.
        Assert.Empty(view.Gaps);
    }

    [Fact]
    public void Fold_AMachineSilentForHours_ShowsTheSilenceAsAGapUpToNow_NotAChartThatLooksCurrent()
    {
        var view = History(W0, W0.AddHours(10), Steady(W0, 30, 4 * Gb));


        var gap = Assert.Single(view.Gaps);
        Assert.Equal(W0.AddMinutes(5), gap.StartUtc);
        Assert.Equal(W0.AddHours(10), gap.EndUtc);
        Assert.Equal("No resource summary has arrived for 9 hours 55 minutes", gap.Label);
        Assert.Equal(FleetTone.Idle, gap.Tone);
        Assert.Contains(view.Events, e => e.Label == gap.Label && e.AtUtc == gap.StartUtc);
    }

    // Second review, finding 1: the tail is a gap exactly when the capacity row calls the machine silent, never sooner,
    // so the row cannot show live figures above a chart saying nothing has arrived.
    [Fact]
    public void Fold_ATailNoLongerThanTheRowsSilence_IsNotAGap()
    {
        // Newest ends 10:05; at 10:20 it ended exactly SilentAfter ago, which the row still counts as reporting.
        Assert.Empty(History(W0, W0.AddMinutes(5) + ResourceCapacityFold.SilentAfter, Steady(W0, 30, 4 * Gb)).Gaps);
    }

    [Fact]
    public void Fold_ATailLongerThanTheRowsSilence_IsAGap_AndTheRowAgrees()
    {
        var to = W0.AddMinutes(5) + ResourceCapacityFold.SilentAfter + TimeSpan.FromSeconds(1);

        var view = History(W0, to, Steady(W0, 30, 4 * Gb));
        var row = ResourceCapacityFold.Fold(directorRunning: true,
            _store.MachineWindows(Tenant, to.AddHours(-1), to.AddMinutes(1)), to);

        Assert.Equal(W0.AddMinutes(5), Assert.Single(view.Gaps).StartUtc);
        Assert.False(row.Reporting);
    }

    // Second review, finding 2: the window straddling the span's start closes the stretch it covers.
    [Fact]
    public void Fold_AMissingWindowJustAfterTheSpansStart_IsAGapFromTheStraddlersEnd_NotFromTheSpansStart()
    {
        // The span opens at 10:01:30. 10:00 straddles it, 10:05 is missing, 10:10 is present.
        var from = W0.AddSeconds(90);
        var view = History(from, W0.AddMinutes(16), Steady(W0, 30, 4 * Gb), Steady(W0.AddMinutes(10), 30, 4 * Gb));

        var gap = Assert.Single(view.Gaps);
        Assert.Equal(W0.AddMinutes(5), gap.StartUtc);
        Assert.Equal("No resource summary arrived for 5 minutes", gap.Label);
        // The straddler itself is not drawn: it starts before the chart's left edge.
        Assert.Equal(W0.AddMinutes(10), Assert.Single(view.Windows).WindowStartUtc);
    }

    [Fact]
    public void Fold_OneMissingWindowBetweenTwo_IsAGap_AndAbuttingWindowsAreNot()
    {
        var view = History(W0, W0.AddMinutes(16),
            Steady(W0, 30, 4 * Gb), Steady(W0.AddMinutes(10), 30, 4 * Gb));


        var gap = Assert.Single(view.Gaps);
        Assert.Equal(W0.AddMinutes(5), gap.StartUtc);
        Assert.Equal(W0.AddMinutes(10), gap.EndUtc);
        Assert.Equal("No resource summary arrived for 5 minutes", gap.Label);
    }

    [Fact]
    public void Fold_AMachineThatJoinedPartWayThrough_HasAGapFromTheSpansStart()
    {
        // The span opens at 08:00; the first summary is the 10:00 window.
        var view = History(W0.AddHours(-2), W0.AddMinutes(6), Steady(W0, 30, 4 * Gb));

        var gap = Assert.Single(view.Gaps);
        Assert.Equal(W0.AddHours(-2), gap.StartUtc);
        Assert.Equal(W0, gap.EndUtc);
        Assert.Equal("No resource summary arrived for 2 hours", gap.Label);
    }

    [Fact]
    public void Fold_ASpanOpeningPartWayThroughAWindow_IsNotAGap()
    {
        // The span opens at 09:57, the first window at 10:00: three minutes cannot have held a summary.
        Assert.Empty(History(W0.AddMinutes(-3), W0.AddMinutes(6), Steady(W0, 30, 4 * Gb)).Gaps);
    }

    [Fact]
    public void Fold_AWindowWhoseProcessorWasNotRead_CarriesTheStoredReason_AndMemoryHasNoNote()
    {
        var unread = Steady(W0, 0, 4 * Gb);
        unread.Machine.ProcessorCouldRead = false;
        unread.Machine.ProcessorUnreadableReason = "the processor counters did not answer";

        var window = Assert.Single(History(W0, W0.AddMinutes(6), unread).Windows);

        Assert.NotNull(window.ProcessorNote);
        Assert.Equal("Processor not read: the processor counters did not answer", window.ProcessorNote.Label);
        Assert.Equal(FleetTone.Idle, window.ProcessorNote.Tone);
        Assert.Null(window.MemoryNote);
    }

    [Fact]
    public void Fold_AWindowWhoseMemoryWasNotRead_CarriesTheStoredReason()
    {
        var unread = Steady(W0, 30, 4 * Gb);
        unread.Machine.MemoryCouldRead = false;
        unread.Machine.MemoryUnreadableReason = "/proc/meminfo could not be opened";

        var window = Assert.Single(History(W0, W0.AddMinutes(6), unread).Windows);

        Assert.Null(window.ProcessorNote);
        Assert.Equal("Memory not read: /proc/meminfo could not be opened", window.MemoryNote!.Label);
    }

    [Fact]
    public void Fold_ARefusedWindow_NotesBothResourcesInTheFaultTone_AndListsItOnce()
    {
        var refused = Steady(W0.AddMinutes(5), 30, 4 * Gb);
        refused.Machine.ProcessorPeakPercent = 400;

        var view = History(W0, W0.AddMinutes(11), Steady(W0, 30, 4 * Gb), refused);

        var window = view.Windows[1];
        Assert.Equal(FleetTone.Bad, window.ProcessorNote!.Tone);
        Assert.Equal(FleetTone.Bad, window.MemoryNote!.Tone);
        Assert.StartsWith("Figures refused: ", window.ProcessorNote.Label);
        Assert.Contains("outside 0 to 100", window.ProcessorNote.Label);
        var listed = Assert.Single(view.Events);
        Assert.Equal(window.ProcessorNote.Label, listed.Label);
        Assert.Equal(FleetTone.Bad, listed.Tone);
        Assert.Equal(W0.AddMinutes(5), listed.AtUtc);
    }

    [Fact]
    public void Fold_AReadWindow_HasNoNotes()
    {
        var window = Assert.Single(History(W0, W0.AddMinutes(6), Steady(W0, 30, 4 * Gb)).Windows);

        Assert.Null(window.ProcessorNote);
        Assert.Null(window.MemoryNote);
    }

    [Fact]
    public void Fold_Events_AreCrossingsRefusalsAndGaps_OldestFirst()
    {
        var refused = Steady(W0.AddMinutes(20), 30, 4 * Gb);
        refused.Machine.ProcessorPeakPercent = 400;

        var view = History(W0, W0.AddMinutes(26), WithRedSpike(W0), refused);

        Assert.Collection(view.Events,
            e => Assert.Equal("CPU red for 90 seconds, peak 99%", e.Label),
            e => Assert.Equal("No resource summary arrived for 15 minutes", e.Label),
            e => Assert.StartsWith("Figures refused: ", e.Label));
        Assert.Equal(view.Events.OrderBy(e => e.AtUtc).Select(e => e.AtUtc), view.Events.Select(e => e.AtUtc));
    }

    [Fact]
    public void Fold_RowsButNoSessions_SaysSoInTheGatewaysWords()
    {
        var view = History(W0, W0.AddMinutes(6), Steady(W0, 30, 4 * Gb));

        Assert.Empty(view.Factories);
        Assert.Equal(MachineResourceHistoryFold.NoFactoriesMessage, view.NoFactoriesMessage);
    }

    [Fact]
    public void Fold_WithSessions_SendsNoNoFactoriesMessage()
    {
        var view = History(W0, W0.AddMinutes(6),
            Steady(W0, 30, 4 * Gb, "devlinux", new ResourceSummarySessionSample("s-bio", "Biographies", 31, 2 * Gb, null)));

        Assert.Single(view.Factories);
        Assert.Null(view.NoFactoriesMessage);
    }

    [Fact]
    public void Fold_NoRows_StillSendsTheSpan()
    {
        var view = MachineResourceHistoryFold.Fold("devlinux", 24, W0, W0.AddHours(24),
            Array.Empty<ResourceWindowEntity>(), Array.Empty<ResourceSessionWindowEntity>());

        Assert.Equal(W0, view.FromUtc);
        Assert.Equal(W0.AddHours(24), view.ToUtc);
        Assert.NotNull(view.EmptyMessage);
        Assert.Empty(view.Gaps);
    }

    [Theory]
    [InlineData(0.5, "under a minute")]
    [InlineData(5, "5 minutes")]
    [InlineData(61, "1 hour 1 minute")]
    [InlineData(120, "2 hours")]
    [InlineData(1440 + 185, "1 day 3 hours")]
    public void Duration_IsWholeMinutesInWords(double minutes, string expected)
    {
        Assert.Equal(expected, MachineResourceHistoryFold.Duration(TimeSpan.FromMinutes(minutes)));
    }
}
