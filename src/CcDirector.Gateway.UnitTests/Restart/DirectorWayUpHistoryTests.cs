using CcDirector.ControlApi.SmartRestart;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Restart;

/// <summary>
/// THE RESTART HISTORY, AND REOPENING A SESSION THAT ENDED WITHOUT A HANDOVER (mission "Smart Director
/// Restart", section 5.3 item 11 and ruling 10.3).
///
/// The history exists because of what the owner said: he may not restart right away and may want to
/// restart later. So nothing is deleted, every record stays readable whatever became of it, and a record
/// that still owes seats carries the SAME offer the start-up check makes - one rule, not two that can
/// drift apart.
/// </summary>
public class DirectorWayUpHistoryTests
{
    private static readonly DateTime Shutdown = new(2026, 9, 19, 21, 50, 0, DateTimeKind.Utc);

    /// <summary>Every record for this Director, newest first, whatever kind of shutdown wrote it and
    /// whether or not it was cancelled.</summary>
    [Fact]
    public async Task The_history_holds_every_record_of_this_director_newest_first()
    {
        var rig = new WayUpTestRig();
        var cancelled = WayUpTestRig.Record("restart-middle", Shutdown.AddDays(-1),
            new[] { WayUpTestRig.Owed("seat-2", "Cancelled seat") });
        cancelled.CancelledAtUtc = Shutdown.AddDays(-1).AddMinutes(2);
        rig.Gateway
            .With(WayUpTestRig.Record("restart-oldest", Shutdown.AddDays(-2),
                new[] { WayUpTestRig.AlreadyBack("seat-3", "An old seat") },
                shutdownKind: WorkspaceShutdownKinds.IgnoreAll))
            .With(cancelled)
            .With(WayUpTestRig.Record("restart-newest", Shutdown, new[] { WayUpTestRig.Owed("seat-1", "A lead") }));

        var history = await rig.WayUp().ReadHistoryAsync(CancellationToken.None);

        Assert.False(history.Refused);
        Assert.Equal("This Director has 3 restart records, newest first.", history.Message);
        Assert.Equal(new[] { "restart-newest", "restart-middle", "restart-oldest" },
            history.Entries.Select(e => e.WorkspaceId).ToArray());
        Assert.Equal("Smart shutdown - every session was asked to hand over.", history.Entries[0].KindLabel);
        Assert.Equal("Shut down ignoring all sessions - nothing from it is offered back.", history.Entries[2].KindLabel);
        Assert.Contains("Cancelled on", history.Entries[1].OutcomeLabel);
    }

    /// <summary>Another Director's records on this machine are not this Director's history.</summary>
    [Fact]
    public async Task The_history_leaves_out_another_directors_records()
    {
        var rig = new WayUpTestRig();
        rig.Gateway
            .With(WayUpTestRig.Record("restart-mine", Shutdown, new[] { WayUpTestRig.Owed("seat-1", "Mine") }))
            .With(WayUpTestRig.Record("restart-theirs", Shutdown, new[] { WayUpTestRig.Owed("seat-2", "Theirs") },
                directorName: WayUpTestRig.OtherDirector));

        var history = await rig.WayUp().ReadHistoryAsync(CancellationToken.None);

        Assert.Equal(new[] { "restart-mine" }, history.Entries.Select(e => e.WorkspaceId).ToArray());
    }

    /// <summary>A record that still owes seats can be brought back from the history later, so it carries
    /// the same rows the start-up check would have shown.</summary>
    [Fact]
    public async Task A_record_that_still_owes_seats_carries_the_offer_in_the_history()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(WayUpTestRig.Record("restart-owed", Shutdown, new[]
        {
            WayUpTestRig.Owed("lead", "A lead"),
            WayUpTestRig.Owed("worker", "A worker", reportsTo: "lead"),
        }));

        var history = await rig.WayUp().ReadHistoryAsync(CancellationToken.None);
        var entry = Assert.Single(history.Entries);
        var offer = Assert.IsType<WayUpRecord>(entry.Offer);

        Assert.Equal(2, offer.SeatsOwed);
        var row = Assert.Single(offer.Rows);
        Assert.Equal("lead", row.RowId);
        Assert.True(row.Ticked);
    }

    /// <summary>A cancelled record stays in the history and offers nothing: those sessions never stopped.</summary>
    [Fact]
    public async Task A_cancelled_record_stays_in_the_history_and_offers_nothing()
    {
        var rig = new WayUpTestRig();
        var doc = WayUpTestRig.Record("restart-cancelled", Shutdown, new[] { WayUpTestRig.Owed("lead", "A lead") });
        doc.CancelledAtUtc = Shutdown.AddMinutes(2);
        rig.Gateway.With(doc);

        var history = await rig.WayUp().ReadHistoryAsync(CancellationToken.None);
        var entry = Assert.Single(history.Entries);

        Assert.Null(entry.Offer);
        Assert.Contains("the sessions kept working", entry.OutcomeLabel);
    }

    /// <summary>What became of each seat, in plain words computed here rather than left to a window.</summary>
    [Fact]
    public async Task Each_seat_says_what_became_of_it()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(WayUpTestRig.Record("restart-mixed", Shutdown, new[]
        {
            WayUpTestRig.AlreadyBack("came-back", "Came back", "aabbccdd-1111"),
            WayUpTestRig.Owed("waiting", "Waiting"),
            WayUpTestRig.Ended("ended", "Ended"),
        }));

        var history = await rig.WayUp().ReadHistoryAsync(CancellationToken.None);
        var seats = Assert.Single(history.Entries).Seats;

        Assert.Equal("Came back as aabbccdd.", seats.Single(s => s.SessionId == "came-back").Outcome);
        Assert.Equal("Handed over and is waiting to be brought back.", seats.Single(s => s.SessionId == "waiting").Outcome);
        Assert.Contains("Ended when time was up", seats.Single(s => s.SessionId == "ended").Outcome);
    }

    /// <summary>An empty history says it is empty. That is not a refusal, and the two never read alike.</summary>
    [Fact]
    public async Task An_empty_history_says_so_and_is_not_a_refusal()
    {
        var history = await new WayUpTestRig().WayUp().ReadHistoryAsync(CancellationToken.None);

        Assert.False(history.Refused);
        Assert.Empty(history.Entries);
        Assert.Contains("no restart history yet", history.Message);
    }

    /// <summary>A Gateway that cannot be reached gives a REFUSED history carrying the reason, never an
    /// empty one - which would read as "this Director has never restarted anything".</summary>
    [Fact]
    public async Task The_gateway_unreachable_is_a_refused_history_and_never_an_empty_one()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(WayUpTestRig.Record("restart-1", Shutdown, new[] { WayUpTestRig.Owed("lead", "A lead") }));
        rig.Gateway.Unreachable = "the Gateway did not answer in time";

        var history = await rig.WayUp().ReadHistoryAsync(CancellationToken.None);

        Assert.True(history.Refused);
        Assert.Empty(history.Entries);
        Assert.Contains("the Gateway did not answer in time", history.Message);
        Assert.DoesNotContain("no restart history yet", history.Message);
    }

    /// <summary>
    /// REOPENING A CLAUDE CODE SEAT hands over the saved conversation and one line telling it that it was
    /// stopped and must check the state of its work before acting - in its own repository, under its own
    /// agent.
    /// </summary>
    [Fact]
    public async Task Reopening_a_seat_starts_it_on_its_saved_conversation_with_one_line()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(WayUpTestRig.Record("restart-1", Shutdown, new[]
        {
            WayUpTestRig.Ended("ended", "A busy worker", "ClaudeCode", "the-saved-conversation"),
        }));

        var result = await rig.WayUp().ReopenAsync(
            new WayUpReopenRequest("restart-1", "ended"), CancellationToken.None);

        var started = Assert.Single(rig.Gateway.Started);
        Assert.Equal("the-saved-conversation", started.ResumeSessionId);
        Assert.Equal(@"D:\ReposFred\devthrottle", started.RepoPath);
        Assert.Equal("ClaudeCode", started.Agent);
        Assert.Equal("A busy worker", started.Name);
        Assert.Contains("You were stopped when the Director shut down", started.PrePrompt);
        Assert.Contains("check the state of your work before you act", started.PrePrompt);
        Assert.True(result.Started);
        Assert.Equal("99990000-aaaa", result.NewSessionId);
    }

    /// <summary>
    /// A CODEX SEAT IS REOPENED THE SAME WAY - one call, the conversation id handed over, and Codex's own
    /// driver ignores it. There is no second way round it; what changes is what the person was told would
    /// happen.
    /// </summary>
    [Fact]
    public async Task Reopening_a_codex_seat_makes_the_same_call_and_says_it_will_be_blank()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(WayUpTestRig.Record("restart-1", Shutdown, new[]
        {
            WayUpTestRig.Ended("ended", "A busy worker", "Codex", "the-saved-conversation"),
        }));

        var result = await rig.WayUp().ReopenAsync(
            new WayUpReopenRequest("restart-1", "ended"), CancellationToken.None);

        var started = Assert.Single(rig.Gateway.Started);
        Assert.Equal("the-saved-conversation", started.ResumeSessionId);
        Assert.True(result.Started);
        Assert.Contains("Codex cannot be started on a saved conversation", result.Message);
    }

    /// <summary>A seat with no conversation recorded starts nothing at all, and says why.</summary>
    [Fact]
    public async Task Reopening_a_seat_with_no_conversation_starts_nothing()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(WayUpTestRig.Record("restart-1", Shutdown, new[]
        {
            WayUpTestRig.Ended("ended", "A busy worker", "ClaudeCode", conversationId: null),
        }));

        var result = await rig.WayUp().ReopenAsync(
            new WayUpReopenRequest("restart-1", "ended"), CancellationToken.None);

        Assert.False(result.Started);
        Assert.Null(result.NewSessionId);
        Assert.Empty(rig.Gateway.Started);
        Assert.Contains("No conversation was recorded", result.Message);
    }

    /// <summary>A seat the record does not hold is said plainly, and nothing is started.</summary>
    [Fact]
    public async Task Reopening_a_seat_that_is_not_in_the_record_starts_nothing()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(WayUpTestRig.Record("restart-1", Shutdown, new[] { WayUpTestRig.Owed("lead", "A lead") }));

        var result = await rig.WayUp().ReopenAsync(
            new WayUpReopenRequest("restart-1", "not-a-seat"), CancellationToken.None);

        Assert.False(result.Started);
        Assert.Empty(rig.Gateway.Started);
        Assert.Contains("not-a-seat", result.Message);
    }

    /// <summary>A Gateway that cannot be reached says so, and nothing is claimed to have been reopened.</summary>
    [Fact]
    public async Task Reopening_with_the_gateway_unreachable_says_so()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(WayUpTestRig.Record("restart-1", Shutdown, new[]
        {
            WayUpTestRig.Ended("ended", "A busy worker"),
        }));
        rig.Gateway.Unreachable = "No connection could be made to the Gateway";

        var result = await rig.WayUp().ReopenAsync(
            new WayUpReopenRequest("restart-1", "ended"), CancellationToken.None);

        Assert.False(result.Started);
        Assert.Contains("No connection could be made to the Gateway", result.Message);
    }
}
