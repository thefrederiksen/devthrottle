using CcDirector.ControlApi.SmartRestart;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Restart;

/// <summary>
/// WHAT THE WINDOW SAYS ABOUT A LEAD THAT IS NOT COMING BACK, AND WHAT THE RESTORE THEN DOES - the two halves
/// of the owner's ruling of 25 September 2026 on product issue 3395: "the top level session should be owned by
/// the user that started the director".
///
/// These rows used to promise something the engine refused. A row whose mission head was decided "close" said
/// the sessions under it "come back under whoever the record says owns them", while
/// <see cref="CcDirector.ControlApi.Drain.DirectorRestore.ResolveOwner"/> failed every one of them with "nobody
/// would own this seat" - so the row brought back nothing and said nothing about why. The ruling settles which
/// of the two was right: the lead is not coming back, so those seats are top level, and a top level session is
/// the user's. These tests hold the two sentences to the same rule, which is the only thing that stops them
/// drifting apart again.
///
/// The engine's own half is proved over the real workspace store in
/// <see cref="Drain.DirectorRestoreTests"/>; what is proved here is the words a person reads before pressing
/// the button, and the seats the row hands to the restore.
/// </summary>
[Collection(DirectorGatesCollection.Name)]
public class DirectorWayUpTopLevelOwnerTests
{
    private static readonly DateTime Shutdown = new(2026, 9, 25, 7, 32, 0, DateTimeKind.Utc);

    /// <summary>
    /// The start-up window: a lead decided "close" with two seats under it. The row offers both, and says they
    /// come back owned by the person reading it - which is what the restore now does with them.
    /// </summary>
    [Fact]
    public async Task A_row_under_a_lead_that_is_not_coming_back_says_the_sessions_come_back_owned_by_you()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(WayUpTestRig.Record("restart-1", Shutdown, new[]
        {
            WayUpTestRig.NotComingBack("lead", "A lead"),
            WayUpTestRig.Owed("worker-a", "A worker", reportsTo: "lead", sortOrder: 1),
            WayUpTestRig.Owed("worker-b", "Another worker", reportsTo: "lead", sortOrder: 2),
        }));

        var offer = await rig.WayUp().FindOfferAsync(CancellationToken.None);

        Assert.Equal(WayUpOfferState.Offered, offer.State);
        var record = Assert.IsType<WayUpRecord>(offer.Record);
        var row = Assert.Single(record.Rows, r => r.Kind == WayUpRowKind.BringBack);

        Assert.Contains("The lead of this mission is not coming back, so these sessions come back owned by you.", row.Detail);

        // THE SENTENCE THE RULING RETIRED. It promised an owner off the record for seats the restore refused
        // outright, and it must not come back.
        Assert.DoesNotContain("whoever the record says owns them", row.Detail);

        // Both seats are still offered, and the lead is not one of them.
        Assert.Equal(new[] { "worker-a", "worker-b" }, row.Seats.Select(s => s.SessionId).ToArray());
        Assert.Equal(2, record.SeatsOwed);
    }

    /// <summary>
    /// Pressing bring back on that row asks the restore for exactly those two seats. Who owns them is the
    /// restore's to decide and is never sent in the order - the window reports that rule, it does not carry it.
    /// </summary>
    [Fact]
    public async Task Bringing_that_row_back_asks_the_restore_for_the_seats_under_the_lead()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(WayUpTestRig.Record("restart-1", Shutdown, new[]
        {
            WayUpTestRig.NotComingBack("lead", "A lead"),
            WayUpTestRig.Owed("worker-a", "A worker", reportsTo: "lead", sortOrder: 1),
            WayUpTestRig.Owed("worker-b", "Another worker", reportsTo: "lead", sortOrder: 2),
        }));

        var result = await rig.WayUp().BringBackAsync(
            new WayUpBringBackRequest("restart-1", new[] { "lead" }), CancellationToken.None);

        Assert.True(result.Started, result.Refusal);
        var order = Assert.Single(rig.Restore.Orders);
        Assert.Equal(new[] { "worker-a", "worker-b" }, order.Seats!.ToArray());
    }

    /// <summary>
    /// THE OTHER WAY A HEAD IS MISSING FROM ITS OWN ROW, and it reads differently: this lead HAS come back, so
    /// the seats under it come back under the session it came back as - not owned by the user. A record with a
    /// seat already back is no longer offered when the Director starts, so this is read where the owner reads
    /// it afterwards: File, then Restart history.
    /// </summary>
    [Fact]
    public async Task A_row_under_a_lead_that_is_already_back_says_they_come_back_under_it()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(WayUpTestRig.Record("restart-1", Shutdown, new[]
        {
            WayUpTestRig.AlreadyBack("lead", "A lead", restoredAs: "eeee1111-2222"),
            WayUpTestRig.Owed("worker-a", "A worker", reportsTo: "lead", sortOrder: 1),
        }));

        var entry = Assert.Single((await rig.WayUp().ReadHistoryAsync(CancellationToken.None)).Entries);
        var record = Assert.IsType<WayUpRecord>(entry.Offer);
        var row = Assert.Single(record.Rows, r => r.Kind == WayUpRowKind.BringBack);

        Assert.Contains("The lead of this mission is already back, so these sessions come back under it.", row.Detail);
        Assert.DoesNotContain("owned by you", row.Detail);
    }

    /// <summary>A row whose lead IS coming back says nothing about ownership at all: it comes back first, and
    /// the seats under it come back under it. The ruling changes nothing here.</summary>
    [Fact]
    public async Task A_row_whose_lead_is_coming_back_still_says_nothing_about_who_owns_them()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(WayUpTestRig.Record("restart-1", Shutdown, new[]
        {
            WayUpTestRig.Owed("lead", "A lead"),
            WayUpTestRig.Owed("worker-a", "A worker", reportsTo: "lead", sortOrder: 1),
        }));

        var offer = await rig.WayUp().FindOfferAsync(CancellationToken.None);
        var record = Assert.IsType<WayUpRecord>(offer.Record);
        var row = Assert.Single(record.Rows, r => r.Kind == WayUpRowKind.BringBack);

        Assert.Equal("Brings back 2 sessions, leads first, each reading its own handover.", row.Detail);
    }
}
