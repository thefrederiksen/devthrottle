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
[Collection(DirectorGatesCollection.Name)]
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

        // THE HISTORY IS A READ OF THE RECORDS AND NOTHING ELSE. The seam CAN ask what is running, because
        // the reopen needs it, so only a count keeps every other path out of it. Only ReopenAsync may ask.
        Assert.Equal(0, rig.Gateway.RosterAsked);
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

    /// <summary>
    /// A SEAT THAT MAY STILL BE RUNNING IS NEVER REOPENED (review finding 1). The drain writes "ended at the
    /// limit" onto the record and SAVES IT BEFORE it ends the sessions, so an end that fails leaves the seat
    /// alive under a record that already says it is gone. Starting it again would put two live agents in one
    /// saved conversation, each acting on the other's half-written work.
    ///
    /// The rule is the restore's own - <c>DirectorRestore.StillRunning</c> - asked through the roster, not a
    /// second rule written in the way up.
    /// </summary>
    [Fact]
    public async Task Reopening_a_seat_that_is_still_running_is_refused_and_starts_nothing()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(WayUpTestRig.Record("restart-1", Shutdown, new[]
        {
            WayUpTestRig.Ended("ended", "A busy worker"),
        }));
        rig.Gateway.Running("ended", "some-other-director");

        var result = await rig.WayUp().ReopenAsync(
            new WayUpReopenRequest("restart-1", "ended"), CancellationToken.None);

        Assert.False(result.Started);
        Assert.Null(result.NewSessionId);
        Assert.Empty(rig.Gateway.Started);
        Assert.Contains("is still running on Director 'some-other-director'", result.Message);
        Assert.Contains("interleave", result.Message);
    }

    /// <summary>
    /// A seat still LISTED under a Director the Gateway cannot reach, which the drain never recorded closed,
    /// is refused too - the second half of the same rule. The Gateway keeps serving an unreachable Director's
    /// last-known sessions, so "on the list" and "running" are different facts and both are guarded.
    /// </summary>
    [Fact]
    public async Task Reopening_a_seat_listed_under_a_director_nobody_can_reach_is_refused()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(WayUpTestRig.Record("restart-1", Shutdown, new[]
        {
            WayUpTestRig.Ended("ended", "A busy worker"),
        }));
        rig.Gateway.RosterSessions.Add(new SessionDto { SessionId = "ended", DirectorId = "a-director-nobody-can-reach", Name = "A busy worker" });

        var result = await rig.WayUp().ReopenAsync(
            new WayUpReopenRequest("restart-1", "ended"), CancellationToken.None);

        Assert.False(result.Started);
        Assert.Empty(rig.Gateway.Started);
        Assert.Contains("still listed under Director 'a-director-nobody-can-reach'", result.Message);
    }

    /// <summary>
    /// ONE REOPEN PER SEAT WHILE THIS DIRECTOR IS UP (review finding 1, part two). A double click, or the
    /// history open on two screens, must not start two agents in one saved conversation. The second attempt
    /// is refused in words of the ENGINE, so no window has to invent the sentence.
    ///
    /// IT USES TWO SEPARATE ENGINES ON PURPOSE, because that is what the real windows do: the factory
    /// builds a new engine on every call, and the start-up window and the history window each hold one of
    /// their own. An earlier version of this test held ONE engine in a local, so it would have passed just
    /// as happily with the claim on the instance - where it guarded nothing the moment a caller built a
    /// second engine. Two engines is the test that proves the claim belongs to the process.
    ///
    /// WHAT THIS DOES NOT COVER: across a Director restart the same seat CAN still be reopened twice,
    /// because nothing is written onto the record. That is stated in the answer file and on the code.
    /// </summary>
    [Fact]
    public async Task Reopening_the_same_seat_twice_starts_only_one_session()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(WayUpTestRig.Record("restart-1", Shutdown, new[]
        {
            WayUpTestRig.Ended("ended", "A busy worker"),
        }));

        var first = await rig.WayUp().ReopenAsync(new WayUpReopenRequest("restart-1", "ended"), CancellationToken.None);
        var second = await rig.WayUp().ReopenAsync(new WayUpReopenRequest("restart-1", "ended"), CancellationToken.None);

        Assert.True(first.Started);
        Assert.False(second.Started);
        Assert.Null(second.NewSessionId);
        Assert.Single(rig.Gateway.Started);
        Assert.Contains("has already been reopened from this record", second.Message);
    }

    /// <summary>
    /// A RECORD OF ANOTHER DIRECTOR ON THIS MACHINE IS NOT REOPENED FROM (review finding 4). The read paths
    /// narrow to this machine and this Director's name; the reopen took whatever id it was handed, and the
    /// Gateway's own restore route refuses another MACHINE's record but not another Director's.
    /// </summary>
    [Fact]
    public async Task Reopening_from_another_directors_record_is_refused_by_name()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(WayUpTestRig.Record("restart-theirs", Shutdown,
            new[] { WayUpTestRig.Ended("ended", "Their busy worker") },
            directorName: WayUpTestRig.OtherDirector));

        var result = await rig.WayUp().ReopenAsync(
            new WayUpReopenRequest("restart-theirs", "ended"), CancellationToken.None);

        Assert.False(result.Started);
        Assert.Empty(rig.Gateway.Started);
        Assert.Contains($"belongs to Director '{WayUpTestRig.OtherDirector}'", result.Message);
    }

    /// <summary>
    /// A RECORD WHOSE EVERY SEAT ENDED AT THE LIMIT IS READABLE IN THE HISTORY WITH A REAL REOPEN OFFER PER
    /// SEAT, AND IS NOW OFFERED AT START-UP TOO.
    ///
    /// The second half was the other way round until the mission's ruling
    /// <c>ruling-way-up-presence-check.md</c>: the presence check counted only seats decided "restore", so
    /// the record the operating system shutdown writes - every seat ended at the limit, nothing decided -
    /// could never be offered, and ruling 10.5 could not be satisfied by any record at all. The check now
    /// also counts a seat that ended without a handover whose saved conversation can be reopened.
    ///
    /// What the history shows per seat is unchanged (review finding 3): the offer beside each seat is what
    /// makes the sentence "its saved conversation can be reopened" true, rather than leaving a window to
    /// invent what reopening would do.
    /// </summary>
    [Fact]
    public async Task A_record_whose_every_seat_ended_at_the_limit_offers_its_conversations_at_start_up_and_in_the_history()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(WayUpTestRig.Record("restart-all-ended", Shutdown, new[]
        {
            WayUpTestRig.Ended("ended-1", "A busy worker", "ClaudeCode", "conversation-one"),
            WayUpTestRig.Ended("ended-2", "A busy lead", "Codex", "conversation-two"),
        }));

        var offer = await rig.WayUp().FindOfferAsync(CancellationToken.None);
        var history = await rig.WayUp().ReadHistoryAsync(CancellationToken.None);

        // OFFERED AT START-UP, for those seats alone: nothing is waiting to come back, both seats are
        // listed, and both are unticked.
        Assert.Equal(WayUpOfferState.Offered, offer.State);
        var record = Assert.IsType<WayUpRecord>(offer.Record);
        Assert.Equal(0, record.SeatsOwed);
        Assert.Equal(2, record.SeatsEndedWithoutHandover);
        Assert.All(record.Rows, r => Assert.Equal(WayUpRowKind.EndedWithoutHandover, r.Kind));
        Assert.All(record.Rows, r => Assert.False(r.Ticked));

        // And the history carries the SAME offer, because one rule decides both.
        var entry = Assert.Single(history.Entries);
        var fromHistory = Assert.IsType<WayUpRecord>(entry.Offer);
        Assert.Equal(record.SeatsLabel, fromHistory.SeatsLabel);
        Assert.Equal(
            record.Rows.Select(r => r.RowId).ToArray(),
            fromHistory.Rows.Select(r => r.RowId).ToArray());
        Assert.Equal(2, entry.Seats.Count);

        var worker = Assert.Single(entry.Seats, s => s.SessionId == "ended-1");
        Assert.Contains("Its saved conversation can be reopened", worker.Outcome);
        var workerReopen = Assert.IsType<WayUpReopenOffer>(worker.Reopen);
        Assert.True(workerReopen.CanReopen);
        Assert.Equal("Reopen its saved conversation", workerReopen.Offer);
        Assert.Contains("Claude Code is started again on this session's saved conversation", workerReopen.What);

        var lead = Assert.Single(entry.Seats, s => s.SessionId == "ended-2");
        var leadReopen = Assert.IsType<WayUpReopenOffer>(lead.Reopen);
        Assert.True(leadReopen.CanReopen);
        Assert.Contains("Codex cannot be started on a saved conversation", leadReopen.What);

        // And the buttons really work: reopening one of those seats starts a session.
        var reopened = await rig.WayUp().ReopenAsync(
            new WayUpReopenRequest("restart-all-ended", "ended-1"), CancellationToken.None);
        Assert.True(reopened.Started);
    }

    /// <summary>
    /// ONLY a seat that ended without a handover carries a reopen offer in the history. A seat that handed
    /// over and is waiting, or that has already come back, carries none - an offer beside it would be a
    /// button that starts a second copy of a session the bring back is going to restore.
    /// </summary>
    [Fact]
    public async Task A_seat_that_handed_over_or_came_back_carries_no_reopen_offer_in_the_history()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(WayUpTestRig.Record("restart-1", Shutdown, new[]
        {
            WayUpTestRig.Owed("owed", "A waiting worker"),
            WayUpTestRig.AlreadyBack("back", "A returned worker"),
            WayUpTestRig.Ended("ended", "A busy worker"),
        }));

        var history = await rig.WayUp().ReadHistoryAsync(CancellationToken.None);

        var entry = Assert.Single(history.Entries);
        Assert.Null(Assert.Single(entry.Seats, s => s.SessionId == "owed").Reopen);
        Assert.Null(Assert.Single(entry.Seats, s => s.SessionId == "back").Reopen);
        Assert.NotNull(Assert.Single(entry.Seats, s => s.SessionId == "ended").Reopen);
    }
}
