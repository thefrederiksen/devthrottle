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
/// The Gateway's display verdict for a machine's resources (Mission: Resource Monitor, sections 5j and 5k; CLAUDE.md
/// rule 7): every label, tone and the Spare figure the Cockpit will render verbatim. The rows are stored and read back
/// through the real store, so the fold is tested on what the database actually returns.
/// </summary>
[Trait("Category", "Resources")]
public sealed class ResourceCapacityFoldTests : IDisposable
{
    private static readonly DateTime W0 = new(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = W0.AddMinutes(32);
    private static readonly TenantId Tenant = new("tenant-a");

    private readonly ResourceSummaryTestDb _db = new();
    private readonly ResourceSummaryStore _store;

    public ResourceCapacityFoldTests()
    {
        _store = new ResourceSummaryStore(() => _db.Factory, new FakeTimeProvider(new DateTimeOffset(Now)));
    }

    public void Dispose() => _db.Dispose();

    private IReadOnlyList<ResourceWindowEntity> Stored(params ResourceSummaryWindow[] windows)
    {
        _store.Store(Tenant, "director-1", Batch(windows));
        return _store.MachineWindows(Tenant, Now.AddHours(-1), Now.AddMinutes(1))!;
    }

    [Fact]
    public void Fold_RunningMachine_ShowsNowPeakAndSpareAtTheHoursPeak()
    {
        // Twenty minutes ago memory peaked at 14 GB of 16; now it sits at 8 GB.
        var windows = Stored(Steady(W0.AddMinutes(10), 60, 14 * Gb), Steady(W0.AddMinutes(25), 28, 8 * Gb));

        var capacity = ResourceCapacityFold.Fold(directorRunning: true, windows, Now);

        Assert.True(capacity.Reporting);
        Assert.Equal("28%", capacity.ProcessorLabel);
        Assert.Equal("peak 60%", capacity.ProcessorDetail);
        Assert.Equal(FleetTone.Ok, capacity.ProcessorTone);
        Assert.Equal("50%", capacity.MemoryLabel);
        Assert.Equal("8.0 / 16 GB", capacity.MemoryDetail);
        // Spare is free memory at the hour's PEAK (16 - 14), not right now (16 - 8): a machine that spikes is not roomy.
        Assert.Equal(2 * Gb, capacity.SpareBytes);
        Assert.Equal("2.0 GB", capacity.SpareLabel);
        Assert.Null(capacity.StatusLabel);
        Assert.Null(capacity.Why);
        Assert.Equal(W0.AddMinutes(25), capacity.ReadingWindowStartUtc);
    }

    [Fact]
    public void Fold_RedSpikeInTheNewestWindow_IsBadWithAStatus()
    {
        var windows = Stored(WithRedSpike(W0.AddMinutes(25)));

        var capacity = ResourceCapacityFold.Fold(directorRunning: true, windows, Now);

        Assert.Equal(FleetTone.Bad, capacity.ProcessorTone);
        Assert.Equal("peak 99%", capacity.ProcessorDetail);
        Assert.Equal("CPU maxed out", capacity.StatusLabel);
        Assert.Equal(FleetTone.Bad, capacity.StatusTone);
        Assert.Equal(FleetTone.Ok, capacity.MemoryTone);
    }

    [Fact]
    public void Fold_NoRunningDirector_ShowsDashesNeverZeros()
    {
        var windows = Stored(Steady(W0.AddMinutes(25), 0, 0));

        var capacity = ResourceCapacityFold.Fold(directorRunning: false, windows, Now);

        Assert.False(capacity.Reporting);
        Assert.Equal(FleetMachineCapacityDto.Dash, capacity.ProcessorLabel);
        Assert.Equal(FleetMachineCapacityDto.Dash, capacity.MemoryLabel);
        Assert.Equal(FleetMachineCapacityDto.Dash, capacity.SpareLabel);
        Assert.Null(capacity.SpareBytes);
        Assert.Equal("Offline", capacity.StatusLabel);
        Assert.Contains("Offline is not idle", capacity.Why);
    }

    [Fact]
    public void Fold_NoWindowInFifteenMinutes_IsNotReporting()
    {
        // The 10:00 window arrived on time at 10:05; nothing since, and the Gateway's now is 10:32.
        var early = new ResourceSummaryStore(() => _db.Factory, new FakeTimeProvider(new DateTimeOffset(W0.AddMinutes(5))));
        early.Store(Tenant, "director-1", Batch(Steady(W0, 30, 4 * Gb)));
        var windows = _store.MachineWindows(Tenant, Now.AddHours(-1), Now.AddMinutes(1))!;

        var capacity = ResourceCapacityFold.Fold(directorRunning: true, windows, Now);

        Assert.False(capacity.Reporting);
        Assert.Equal(FleetMachineCapacityDto.Dash, capacity.ProcessorLabel);
        Assert.Contains("no resource summary in the last 15 minutes", capacity.Why);
    }

    [Fact]
    public void Fold_NewestWindowRefused_SaysTheFiguresWereRefusedAndWhy_NotSilentAndNotIdle()
    {
        var refused = Steady(W0.AddMinutes(25), 30, 8 * Gb);
        refused.Machine.ProcessorPeakPercent = 400;

        var capacity = ResourceCapacityFold.Fold(directorRunning: true, Stored(Steady(W0.AddMinutes(20), 30, 8 * Gb), refused), Now);

        Assert.True(capacity.Reporting);
        Assert.Equal("cannot read", capacity.ProcessorLabel);
        Assert.Equal("cannot read", capacity.MemoryLabel);
        Assert.Equal("cannot read", capacity.SpareLabel);
        Assert.Null(capacity.SpareBytes);
        Assert.Equal("Figures refused", capacity.StatusLabel);
        Assert.Equal(FleetTone.Bad, capacity.StatusTone);
        Assert.Contains("outside 0 to 100", capacity.Why);
    }

    // ---- Issue #4670: the hour's peak is known only when every window in the hour read THAT resource. ----
    // Each case has an earlier 10:15 window that is unread or refused for the resource under test, and a normal 10:25
    // window at 28% and 8 GB of 16. Computed from the readable windows alone, the peak would be the newest window's.

    [Fact]
    public void Fold_AWindowUnreadForTheProcessorOnly_MakesOnlyTheProcessorPeakUnknown()
    {
        var unread = Steady(W0.AddMinutes(15), 97, 12 * Gb);
        unread.Machine.ProcessorCouldRead = false;
        unread.Machine.ProcessorUnreadableReason = "the processor counters did not answer";

        var capacity = ResourceCapacityFold.Fold(directorRunning: true, Stored(unread, Steady(W0.AddMinutes(25), 28, 8 * Gb)), Now);

        Assert.Equal("28%", capacity.ProcessorLabel);
        Assert.Equal("peak not known", capacity.ProcessorDetail);
        // Memory was read in every window, so its peak - 12 GB at 10:15 - and Spare stay known.
        Assert.Equal(4 * Gb, capacity.SpareBytes);
        Assert.Equal("4.0 GB", capacity.SpareLabel);
        Assert.Equal("In the last hour one window could not be read (at 10:15 UTC: the processor counters did not answer), "
                     + "so the hour's peak processor use is not known.", capacity.Why);
    }

    [Fact]
    public void Fold_AWindowUnreadForMemoryOnly_MakesSpareUnknown_NeverRoomier()
    {
        var unread = Steady(W0.AddMinutes(15), 60, 15 * Gb);
        unread.Machine.MemoryCouldRead = false;
        unread.Machine.MemoryUnreadableReason = "/proc/meminfo could not be opened";

        var capacity = ResourceCapacityFold.Fold(directorRunning: true, Stored(unread, Steady(W0.AddMinutes(25), 28, 8 * Gb)), Now);

        // The processor was read in every window, so its peak stays known.
        Assert.Equal("peak 60%", capacity.ProcessorDetail);
        Assert.Equal("50%", capacity.MemoryLabel);
        Assert.Null(capacity.SpareBytes);
        Assert.Equal("cannot read", capacity.SpareLabel);
        Assert.Equal("In the last hour one window could not be read (at 10:15 UTC: /proc/meminfo could not be opened), "
                     + "so the hour's peak memory use - and with it the spare capacity - is not known.", capacity.Why);
    }

    [Fact]
    public void Fold_ARefusedWindowEarlierInTheHour_MakesBothPeaksUnknown_AndSaysItWasRefused()
    {
        // 10:15 peaked at 15 of 16 GB, but its 400% processor reading is one no machine can give, so the store refuses
        // the whole window and keeps none of its figures. Spare from the readable 10:25 alone would advertise 8 GB of room
        // on a machine that had 1.
        var refused = Steady(W0.AddMinutes(15), 30, 15 * Gb);
        refused.Machine.ProcessorPeakPercent = 400;

        var capacity = ResourceCapacityFold.Fold(directorRunning: true, Stored(refused, Steady(W0.AddMinutes(25), 28, 8 * Gb)), Now);

        Assert.Equal("28%", capacity.ProcessorLabel);
        Assert.Equal("peak not known", capacity.ProcessorDetail);
        Assert.Equal("50%", capacity.MemoryLabel);
        Assert.Null(capacity.SpareBytes);
        Assert.Equal("cannot read", capacity.SpareLabel);
        // One cause sank both peaks, so it is said once, not once per resource.
        Assert.StartsWith("In the last hour one window had figures the Gateway refused (at 10:15 UTC: ", capacity.Why);
        Assert.Contains("outside 0 to 100", capacity.Why);
        Assert.EndsWith("), so the hour's peak processor and memory use - and with it the spare capacity - are not known.", capacity.Why);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(capacity.Why!, "In the last hour"));
        Assert.DoesNotContain("could not be read", capacity.Why);
    }

    [Fact]
    public void Fold_AWindowOneDirectorHadRefusedButAnotherRead_KeepsThePeakKnown()
    {
        var refused = Steady(W0.AddMinutes(15), 30, 15 * Gb);
        refused.Machine.ProcessorPeakPercent = 400;
        _store.Store(Tenant, "director-2", Batch(refused));

        var windows = Stored(Steady(W0.AddMinutes(15), 97, 12 * Gb), Steady(W0.AddMinutes(25), 28, 8 * Gb));
        var capacity = ResourceCapacityFold.Fold(directorRunning: true, windows, Now);

        Assert.Equal("peak 97%", capacity.ProcessorDetail);
        Assert.Equal(4 * Gb, capacity.SpareBytes);
        Assert.Null(capacity.Why);
    }

    [Fact]
    public void Fold_AWindowOneDirectorRefusedAndAnotherCouldNotRead_IsAGap_AndNamesBothCauses()
    {
        var refused = Steady(W0.AddMinutes(15), 30, 15 * Gb);
        refused.Machine.ProcessorPeakPercent = 400;
        _store.Store(Tenant, "director-2", Batch(refused));
        var unread = Steady(W0.AddMinutes(15), 0, 12 * Gb);
        unread.Machine.ProcessorCouldRead = false;
        unread.Machine.ProcessorUnreadableReason = "the processor counters did not answer";

        var capacity = ResourceCapacityFold.Fold(directorRunning: true, Stored(unread, Steady(W0.AddMinutes(25), 28, 8 * Gb)), Now);

        Assert.Equal("peak not known", capacity.ProcessorDetail);
        Assert.Contains("had figures the Gateway refused (at 10:15 UTC:", capacity.Why);
        Assert.Contains("could not be read (at 10:15 UTC: the processor counters did not answer)", capacity.Why);
        // director-1 read memory at 10:15 (12 GB), so memory's peak and Spare stay known.
        Assert.Equal(4 * Gb, capacity.SpareBytes);
    }

    [Fact]
    public void Fold_AWindowOneDirectorCouldNotReadButAnotherDid_KeepsThePeakKnown()
    {
        // Two Directors on one machine both measure the whole machine: director-2's unread 10:15 is covered by director-1's.
        var unread = Steady(W0.AddMinutes(15), 0, 0);
        unread.Machine.ProcessorCouldRead = false;
        unread.Machine.ProcessorUnreadableReason = "the processor counters did not answer";
        unread.Machine.MemoryCouldRead = false;
        unread.Machine.MemoryUnreadableReason = "/proc/meminfo could not be opened";
        _store.Store(Tenant, "director-2", Batch(unread));

        var windows = Stored(Steady(W0.AddMinutes(15), 97, 12 * Gb), Steady(W0.AddMinutes(25), 28, 8 * Gb));
        var capacity = ResourceCapacityFold.Fold(directorRunning: true, windows, Now);

        Assert.Equal("peak 97%", capacity.ProcessorDetail);
        Assert.Equal(4 * Gb, capacity.SpareBytes);
        Assert.Null(capacity.Why);
    }

    [Fact]
    public void History_ARefusedWindow_MakesTheFactoryCostAtLeast()
    {
        var share = new ResourceSummarySessionSample("s-bio", "Biographies", 31, 2 * Gb, null);
        var refused = Steady(W0.AddMinutes(5), 30, 8 * Gb, "devlinux", share);
        refused.Machine.ProcessorPeakPercent = 400;
        _store.Store(Tenant, "director-1", Batch(Steady(W0, 30, 8 * Gb, "devlinux", share), refused));
        var from = Now.AddHours(-24);

        var view = MachineResourceHistoryFold.Fold("devlinux", 24,
            _store.MachineWindows(Tenant, from, Now), _store.SessionWindows(Tenant, "devlinux", from, Now));

        Assert.Equal("at least 31% / 31%", Assert.Single(view.Factories).ProcessorLabel);
    }

    [Fact]
    public void History_ARefusedWindow_IsMarkedWithItsReason()
    {
        var refused = Steady(W0.AddMinutes(5), 30, 8 * Gb);
        refused.Machine.ProcessorPeakPercent = 400;
        _store.Store(Tenant, "director-1", Batch(Steady(W0, 30, 8 * Gb), refused));
        var from = Now.AddHours(-24);

        var view = MachineResourceHistoryFold.Fold("devlinux", 24,
            _store.MachineWindows(Tenant, from, Now), _store.SessionWindows(Tenant, "devlinux", from, Now));

        Assert.Equal(2, view.Windows.Count);
        Assert.Null(view.Windows[0].RefusedReason);
        Assert.Contains("outside 0 to 100", view.Windows[1].RefusedReason);
        Assert.Null(view.Windows[1].ProcessorAveragePercent);
    }

    [Fact]
    public void Fold_ADirectorWhoseClockIsBehind_IsNamedAsAClockProblem_NotAConnectionProblem()
    {
        // Received a minute ago, stamped 10:00 - twenty-seven minutes before the Gateway's now of 10:32.
        var windows = Stored(Steady(W0, 30, 8 * Gb));

        var capacity = ResourceCapacityFold.Fold(directorRunning: true, windows, Now);

        Assert.False(capacity.Reporting);
        Assert.Contains("clock is probably behind", capacity.Why);
    }

    [Fact]
    public void Fold_StatisticsStoreNotOpen_SaysSoInsteadOfGuessing()
    {
        var capacity = ResourceCapacityFold.Fold(directorRunning: true, windows: null, Now);

        Assert.False(capacity.Reporting);
        Assert.Contains("statistics store is not open", capacity.Why);
    }

    [Fact]
    public void Fold_ProcessorUnreadable_SaysCannotReadAndWhy_MemoryStillShown()
    {
        var window = Steady(W0.AddMinutes(25), 30, 4 * Gb);
        window.Machine.ProcessorCouldRead = false;
        window.Machine.ProcessorUnreadableReason = "/proc/stat could not be opened";

        var capacity = ResourceCapacityFold.Fold(directorRunning: true, Stored(window), Now);

        Assert.Equal("cannot read", capacity.ProcessorLabel);
        Assert.Equal(FleetTone.Idle, capacity.ProcessorTone);
        Assert.Contains("/proc/stat could not be opened", capacity.Why);
        Assert.Equal("25%", capacity.MemoryLabel);
    }

    [Fact]
    public void Stamp_EveryMachineGetsItsOwnRows_MatchedIgnoringCase()
    {
        var windows = Stored(Steady(W0.AddMinutes(25), 28, 8 * Gb, "DevLinux"), Steady(W0.AddMinutes(25), 70, 12 * Gb, "mac-mini"));
        var view = new FleetMachinesDto
        {
            Machines =
            {
                new FleetMachineDto { Machine = "devlinux", Directors = { new FleetDirectorDto { StateLabel = "Running" } } },
                new FleetMachineDto { Machine = "demo-win11", Directors = { new FleetDirectorDto { StateLabel = "Stopped" } } },
            },
        };

        ResourceCapacityFold.Stamp(view, windows, Now);

        Assert.Equal("28%", view.Machines[0].Capacity.ProcessorLabel);
        Assert.Equal(FleetMachineCapacityDto.Dash, view.Machines[1].Capacity.ProcessorLabel);
    }

    [Fact]
    public void History_GroupsCostByFactoryNotMission_AndMarksCrossings()
    {
        var bio = new ResourceSummarySessionSample("s-bio", "Biographies", 31, 2 * Gb, null);
        var mkt1 = new ResourceSummarySessionSample("s-mkt-1", "Marketing", 10, 1 * Gb, null);
        var mkt2 = new ResourceSummarySessionSample("s-mkt-2", "Marketing", 12, 1 * Gb, null);
        var loose = new ResourceSummarySessionSample("s-loose", null, 4, 1 * Gb, null);
        _store.Store(Tenant, "director-1", Batch(
            Steady(W0, 60, 8 * Gb, "devlinux", bio, mkt1, mkt2, loose),
            WithRedSpike(W0.AddMinutes(5))));
        var from = Now.AddHours(-24);

        var view = MachineResourceHistoryFold.Fold("devlinux", 24,
            _store.MachineWindows(Tenant, from, Now), _store.SessionWindows(Tenant, "devlinux", from, Now));

        Assert.Null(view.EmptyMessage);
        Assert.Equal(2, view.Windows.Count);
        var mark = Assert.Single(view.Windows[1].Crossings);
        Assert.Equal("CPU red for 90 seconds, peak 99%", mark.Label);
        Assert.Equal(FleetTone.Bad, mark.Tone);
        Assert.Equal(new[] { "Biographies", "Marketing", MachineResourceHistoryFold.NoFactory }, view.Factories.Select(f => f.Factory));
        var marketing = view.Factories.Single(f => f.Factory == "Marketing");
        Assert.Equal(2, marketing.Sessions);
        // Two Marketing sessions side by side: their figures add.
        Assert.Equal("22% / 22%", marketing.ProcessorLabel);
        Assert.Equal("2.0 / 2.0 GB", marketing.MemoryLabel);
    }

    [Fact]
    public void Fold_PeakInUseAboveTheMachinesTotal_IsSurfacedNotClampedToZeroSpare()
    {
        // An impossible reading: 18 GB in use on a 16 GB machine. A clamp would show a tidy "0.0 GB" spare.
        var window = Steady(W0.AddMinutes(25), 30, 8 * Gb);
        window.Machine.MemoryPeakUsedBytes = 18 * Gb;
        var rows = new[] { new ResourceWindowEntity
        {
            Tenant = Tenant.Value, DirectorId = "director-1", WindowStartUtc = W0.AddMinutes(25), HourStartUtc = W0,
            MachineName = "devlinux", WindowSeconds = 300, SampleCount = 60, ProcessorSamples = 60,
            ProcessorAveragePercent = 30, ProcessorPeakPercent = 30, MemorySamples = 60,
            MemoryAverageUsedBytes = 8 * Gb, MemoryPeakUsedBytes = 18 * Gb, MemoryTotalBytes = 16 * Gb,
        } };

        var capacity = ResourceCapacityFold.Fold(directorRunning: true, rows, Now);

        Assert.Null(capacity.SpareBytes);
        Assert.Equal("cannot read", capacity.SpareLabel);
        Assert.Contains("more memory in use than it has", capacity.Why);
    }

    [Fact]
    public void History_ASessionWhoseFigureIsUnknown_MakesTheFactoryCostAtLeast_NeverAZero()
    {
        // Two Marketing sessions in one window: one read at 10%, one whose processor could not be read. Summing the
        // unknown as zero would report the factory at a plausible, too-cheap 10%.
        var known = new ResourceSummarySessionSample("s-mkt-1", "Marketing", 10, 1 * Gb, null);
        var unknown = new ResourceSummarySessionSample("s-mkt-2", "Marketing", null, null, "the process tree could not be read");
        _store.Store(Tenant, "director-1", Batch(Steady(W0, 60, 8 * Gb, "devlinux", known, unknown)));
        var from = Now.AddHours(-24);

        var view = MachineResourceHistoryFold.Fold("devlinux", 24,
            _store.MachineWindows(Tenant, from, Now), _store.SessionWindows(Tenant, "devlinux", from, Now));

        var marketing = Assert.Single(view.Factories);
        Assert.Equal("at least 10% / 10%", marketing.ProcessorLabel);
        Assert.Equal("at least 1.0 / 1.0 GB", marketing.MemoryLabel);
    }

    [Fact]
    public void History_ProcessesWithUnknownFigures_MakeTheSessionsCostAtLeast()
    {
        var partial = new ResourceSummarySessionSample("s-bio", "Biographies", 31, 2 * Gb, null, ProcessesWithUnknownFigures: 2);
        _store.Store(Tenant, "director-1", Batch(Steady(W0, 60, 8 * Gb, "devlinux", partial)));
        var from = Now.AddHours(-24);

        var view = MachineResourceHistoryFold.Fold("devlinux", 24,
            _store.MachineWindows(Tenant, from, Now), _store.SessionWindows(Tenant, "devlinux", from, Now));

        var row = Assert.Single(_store.SessionWindows(Tenant, "devlinux", from, Now)!);
        Assert.Equal(2, row.ProcessesWithUnknownFigures);
        Assert.Equal("at least 31% / 31%", Assert.Single(view.Factories).ProcessorLabel);
    }

    [Fact]
    public void History_NoRows_SaysSo()
    {
        var view = MachineResourceHistoryFold.Fold("devlinux", 24, Array.Empty<ResourceWindowEntity>(), Array.Empty<ResourceSessionWindowEntity>());

        Assert.Empty(view.Windows);
        Assert.Equal("No resource summaries from devlinux in the last 24 hours.", view.EmptyMessage);
    }
}
