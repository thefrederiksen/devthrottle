using Avalonia.Threading;
using CcDirector.ControlApi.SmartRestart;

namespace CcDirector.Avalonia.Tests.SmartRestart;

/// <summary>
/// The stand-ins these window tests drive: a fake way up engine, and builders for the records it
/// answers with.
///
/// THE WORDS IN THESE RECORDS ARE THE TEST'S OWN, not the engine's. That is the point: the windows are
/// proved to show WHATEVER they are handed, so a test writes a sentence no engine would ever produce
/// and asserts the window drew it unchanged. The real engine's wording is proved by the engine's own
/// tests (see proof-phase-3-engine.md), not here.
/// </summary>
internal static class WayUp
{
    internal static WayUpRowSeat Seat(
        string sessionId, string name, string detail, string? reportsTo = null,
        string? mission = null, string? role = null) =>
        new(sessionId, name, mission, role, reportsTo, detail);

    internal static WayUpRow BringBackRow(
        string rowId, string title, string detail, bool ticked = true, params WayUpRowSeat[] seats) =>
        new(WayUpRowKind.BringBack, rowId, title, detail, ticked, seats, null);

    internal static WayUpRow EndedRow(
        string rowId, string title, string detail, bool canReopen, string? offer, string what) =>
        new(WayUpRowKind.EndedWithoutHandover, rowId, title, detail, false,
            Array.Empty<WayUpRowSeat>(), new WayUpReopenOffer(canReopen, offer, what));

    internal static WayUpRecord Record(
        string workspaceId,
        string headline,
        string whenLabel,
        string reasonLabel,
        int seatsOwed,
        string seatsOwedLabel,
        params WayUpRow[] rows) =>
        new(workspaceId, new DateTime(2026, 9, 19, 21, 50, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 19, 17, 50, 0, DateTimeKind.Local),
            headline, whenLabel, null, reasonLabel, seatsOwed, seatsOwedLabel, rows);

    internal static WayUpHistorySeat HistorySeat(string sessionId, string name, string outcome) =>
        new(sessionId, name, null, null, outcome);

    internal static WayUpHistoryEntry HistoryEntry(
        string workspaceId,
        string whenLabel,
        string kindLabel,
        string reasonLabel,
        string outcomeLabel,
        WayUpRecord? offer = null,
        params WayUpHistorySeat[] seats) =>
        new(workspaceId, new DateTime(2026, 9, 19, 21, 50, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 19, 17, 50, 0, DateTimeKind.Local),
            whenLabel, kindLabel, null, reasonLabel, outcomeLabel, seats, offer);
}

/// <summary>
/// A way up engine that answers what the test tells it to, and remembers what it was asked.
///
/// It also remembers WHETHER IT WAS CALLED ON THE INTERFACE THREAD. Every call it makes can read the
/// Gateway record by record, so a window that asked it on the interface thread would freeze the
/// Director for as long as that took (CLAUDE.md rule 1), and a test that only checked the answer would
/// never notice.
/// </summary>
internal sealed class FakeWayUp : IDirectorWayUp
{
    internal WayUpOffer Offer { get; set; } =
        new(WayUpOfferState.NothingWaiting, "nothing waiting", null);

    internal WayUpHistory History { get; set; } =
        new(false, "an empty history", Array.Empty<WayUpHistoryEntry>());

    internal WayUpBringBackResult BringBack { get; set; } =
        new(true, null, "brought back", Array.Empty<WayUpSeatResult>());

    internal WayUpReopenResult Reopen { get; set; } = new(true, "new-1", "reopened");

    /// <summary>Set to throw from every call, to prove a caller survives a seam that fails.</summary>
    internal Exception? Throws { get; set; }

    internal int OfferAsks { get; private set; }

    internal int HistoryReads { get; private set; }

    internal List<WayUpBringBackRequest> BringBackRequests { get; } = [];

    internal List<WayUpReopenRequest> ReopenRequests { get; } = [];

    /// <summary>True when ANY call arrived on the interface thread. It must stay false.</summary>
    internal bool WasEverCalledOnTheInterfaceThread { get; private set; }

    public Task<WayUpOffer> FindOfferAsync(CancellationToken ct)
    {
        Note();
        OfferAsks++;
        return Throws is null ? Task.FromResult(Offer) : Task.FromException<WayUpOffer>(Throws);
    }

    public Task<WayUpHistory> ReadHistoryAsync(CancellationToken ct)
    {
        Note();
        HistoryReads++;
        return Throws is null ? Task.FromResult(History) : Task.FromException<WayUpHistory>(Throws);
    }

    public Task<WayUpBringBackResult> BringBackAsync(WayUpBringBackRequest request, CancellationToken ct)
    {
        Note();
        BringBackRequests.Add(request);
        return Throws is null
            ? Task.FromResult(BringBack)
            : Task.FromException<WayUpBringBackResult>(Throws);
    }

    public Task<WayUpReopenResult> ReopenAsync(WayUpReopenRequest request, CancellationToken ct)
    {
        Note();
        ReopenRequests.Add(request);
        return Throws is null
            ? Task.FromResult(Reopen)
            : Task.FromException<WayUpReopenResult>(Throws);
    }

    private void Note()
    {
        if (Dispatcher.UIThread.CheckAccess())
            WasEverCalledOnTheInterfaceThread = true;
    }
}
