using CcDirector.ControlApi.Drain;
using CcDirector.ControlApi.SmartRestart;
using CcDirector.Gateway.Contracts;
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

        Assert.Contains(
            "The lead of this mission is not coming back, so the sessions that reported to it come back owned by you.",
            row.Detail);

        // THE SENTENCE THE RULING RETIRED. It promised an owner off the record for seats the restore refused
        // outright, and it must not come back.
        Assert.DoesNotContain("whoever the record says owns them", row.Detail);

        // NOR THE SENTENCE THAT REPLACED IT AND PROMISED ONE OWNER FOR THE WHOLE ROW. Both seats here report to
        // the head, so the promise happens to hold - but the words must be the ones that stay true when a row
        // runs deeper, which the nested test below is about.
        Assert.DoesNotContain("these sessions come back owned by you", row.Detail);

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

        // "AS LONG AS IT IS STILL RUNNING" IS NOT A HEDGE, IT IS THE ENGINE'S RULE: ResolveOwner puts a seat
        // under a restored owner only while that owner answers, and refuses the seat otherwise. A restored id on
        // the record is not on its own proof the session is alive.
        Assert.Contains(
            "The lead of this mission is already back, so the sessions that reported to it come back under it, " +
            "as long as it is still running.",
            row.Detail);
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

    /// <summary>
    /// A ROW IS GROUPED BY MISSION HEAD, SO IT CAN BE A CHAIN TWO DEEP - and then "owned by you" is true of the
    /// head's own reports and false of everything below them (both reviewers of pull request 3397). The engine
    /// gives the grandchild to its RESTORED PARENT, because that parent IS coming back; only the direct reports
    /// of a head that is not coming back are top level.
    ///
    /// The window's words and the engine's answer are asserted side by side in one test on purpose: the whole
    /// defect this pull request exists to remove is the two of them disagreeing, and a test that reads only one
    /// of them cannot see it.
    /// </summary>
    [Fact]
    public async Task A_row_two_deep_under_a_lead_that_is_not_coming_back_gives_only_its_own_reports_to_you()
    {
        var rig = new WayUpTestRig();
        var head = WayUpTestRig.NotComingBack("head", "A mission lead");
        var lead = WayUpTestRig.Owed("lead", "A sub lead", reportsTo: "head", sortOrder: 1);
        var worker = WayUpTestRig.Owed("worker", "A worker", reportsTo: "lead", sortOrder: 2);
        rig.Gateway.With(WayUpTestRig.Record("restart-1", Shutdown, new[] { head, lead, worker }));

        var offer = await rig.WayUp().FindOfferAsync(CancellationToken.None);
        var record = Assert.IsType<WayUpRecord>(offer.Record);
        var row = Assert.Single(record.Rows, r => r.Kind == WayUpRowKind.BringBack);
        Assert.Equal(new[] { "lead", "worker" }, row.Seats.Select(s => s.SessionId).ToArray());

        // What the row says: the head's own reports are yours, and what reports to THEM is theirs.
        Assert.Contains("the sessions that reported to it come back owned by you", row.Detail);
        Assert.Contains("Anything reporting to one of these sessions comes back under it.", row.Detail);

        // THE SENTENCE THAT WAS FALSE FOR THIS ROW, and it must not come back in any form.
        Assert.DoesNotContain("these sessions come back owned by you", row.Detail);

        // And what the engine does with the same two seats. The sub lead is the head's own report, so it is the
        // user's; the worker reports to the sub lead, which has just come back, so it is the sub lead's.
        var byId = new[] { head, lead, worker }.ToDictionary(s => s.SessionId!, StringComparer.OrdinalIgnoreCase);
        var nobodyRunning = new RestoreRoster(Array.Empty<SessionDto>(), Array.Empty<DirectorReachabilityDto>());

        var forTheLead = DirectorRestore.ResolveOwner(
            lead, byId, new Dictionary<string, string>(), new HashSet<string>(), nobodyRunning);
        Assert.Null(forTheLead.Owner);
        Assert.Null(forTheLead.Failure);

        lead.RestoredSessionId = "back-as-this";
        var forTheWorker = DirectorRestore.ResolveOwner(
            worker, byId, new Dictionary<string, string>(), new HashSet<string> { "lead" }, nobodyRunning);
        Assert.Equal("back-as-this", forTheWorker.Owner);
        Assert.Null(forTheWorker.Failure);
    }

    /// <summary>
    /// A LEAD THE RECORD NEVER CLOSED IS ITS OWN CASE, AND THE ROW PROMISES NOTHING (both reviewers of pull
    /// request 3397). Such a lead - one that blocked the drain, or that a cancelled shutdown left running, or
    /// whose end at the limit failed - may still be running under the id it had. The engine asks the FLEET; the
    /// window reads the record and never the fleet, so it cannot know, and it says that instead of guessing.
    ///
    /// The old row said "these sessions come back owned by you" here, which was false in exactly the case where
    /// the lead was alive: the engine puts them back under it.
    /// </summary>
    [Theory]
    [InlineData(WorkspaceDrainStates.EndedAtLimit)]
    [InlineData(WorkspaceDrainStates.Blocked)]
    public async Task A_row_whose_lead_the_record_never_closed_promises_no_owner_and_the_engine_asks_the_fleet(
        string drainState)
    {
        var rig = new WayUpTestRig();
        var head = WayUpTestRig.NotComingBackAndNeverClosed("head", "A mission lead", drainState);
        var worker = WayUpTestRig.Owed("worker", "A worker", reportsTo: "head", sortOrder: 1);
        rig.Gateway.With(WayUpTestRig.Record("restart-1", Shutdown, new[] { head, worker }));

        var offer = await rig.WayUp().FindOfferAsync(CancellationToken.None);
        var record = Assert.IsType<WayUpRecord>(offer.Record);
        var row = Assert.Single(record.Rows, r => r.Kind == WayUpRowKind.BringBack);

        // NO PROMISE OF AN OWNER, in either direction.
        Assert.DoesNotContain("so these sessions come back owned by you", row.Detail);
        Assert.DoesNotContain("so the sessions that reported to it come back owned by you", row.Detail);
        Assert.Contains("the record does not say it closed", row.Detail);
        Assert.Contains("depends on whether it is still running", row.Detail);

        // And the engine, for the same seat, against a fleet that DOES still list the lead on a Director it can
        // reach: the worker comes back under the lead it always had. This is the answer the old row denied.
        var byId = new[] { head, worker }.ToDictionary(s => s.SessionId!, StringComparer.OrdinalIgnoreCase);
        var leadIsRunning = new RestoreRoster(
            new[] { new SessionDto { SessionId = "head", DirectorId = "some-director" } },
            new[] { new DirectorReachabilityDto { DirectorId = "some-director", State = DirectorReachabilityDto.StateOnline } });

        var resolved = DirectorRestore.ResolveOwner(
            worker, byId, new Dictionary<string, string>(), new HashSet<string>(), leadIsRunning,
            shutdownWasCancelled: drainState != WorkspaceDrainStates.Blocked);

        Assert.Equal("head", resolved.Owner);
        Assert.Null(resolved.Failure);
    }
}
