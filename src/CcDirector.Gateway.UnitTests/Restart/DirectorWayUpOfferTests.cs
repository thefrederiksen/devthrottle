using CcDirector.ControlApi.SmartRestart;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Restart;

/// <summary>
/// THE WAY UP'S PRESENCE CHECK AND ITS ROWS (mission "Smart Director Restart", section 5.3 item 10 and
/// rulings 10.2 and 10.3).
///
/// What is pinned here is the rule the mission argued hardest for: what may be offered when the Director
/// comes back is decided by READING THE RECORD, never by counting sessions. A Director with sessions
/// running may still hold a record worth offering, and a Director with none may hold nothing - so these
/// tests never set up a session, because the engine has no way to ask about one.
/// </summary>
[Collection(DirectorGatesCollection.Name)]
public class DirectorWayUpOfferTests
{
    private static readonly DateTime Shutdown = new(2026, 9, 19, 21, 50, 0, DateTimeKind.Utc);

    /// <summary>
    /// THE NAME IS THE KEY. A restarted Director gets a new identifier, so the record is found by the
    /// Director's display name and the machine. A record another Director on this same machine wrote is
    /// therefore somebody else's, and offering it would bring that Director's sessions up here.
    /// </summary>
    [Fact]
    public async Task A_record_for_another_director_on_this_machine_is_not_offered()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(WayUpTestRig.Record(
            "restart-other", Shutdown, new[] { WayUpTestRig.Owed("seat-1", "A lead") },
            directorName: WayUpTestRig.OtherDirector));

        var offer = await rig.WayUp().FindOfferAsync(CancellationToken.None);

        Assert.Equal(WayUpOfferState.NothingWaiting, offer.State);
        Assert.Null(offer.Record);
        // It was never even read as a document: the summary said whose it was.
        Assert.Empty(rig.Gateway.Read);
    }

    /// <summary>A record whose every owed seat has already come back owes nothing, so there is nothing to
    /// offer - and it stays readable in the history.</summary>
    [Fact]
    public async Task A_record_already_brought_back_is_not_offered()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(WayUpTestRig.Record("restart-done", Shutdown, new[]
        {
            WayUpTestRig.AlreadyBack("seat-1", "A lead"),
            WayUpTestRig.AlreadyBack("seat-2", "A worker"),
        }));

        var offer = await rig.WayUp().FindOfferAsync(CancellationToken.None);

        Assert.Equal(WayUpOfferState.NothingWaiting, offer.State);
        Assert.Null(offer.Record);

        // NOTHING WAITING IS ALSO A PROMISE ABOUT THE RECORD. The offered path asserts this too; a path
        // that answers "nothing" after asking what is running would be the same race by the other door.
        Assert.Equal(0, rig.Gateway.RosterAsked);
    }

    /// <summary>The owner cancelled and kept working, so the sessions never stopped. Offering it back would
    /// start a second copy of every one of them.</summary>
    [Fact]
    public async Task A_cancelled_record_is_not_offered()
    {
        var rig = new WayUpTestRig();
        var doc = WayUpTestRig.Record("restart-cancelled", Shutdown, new[] { WayUpTestRig.Owed("seat-1", "A lead") });
        doc.CancelledAtUtc = Shutdown.AddMinutes(3);
        rig.Gateway.With(doc);

        var offer = await rig.WayUp().FindOfferAsync(CancellationToken.None);

        Assert.Equal(WayUpOfferState.NothingWaiting, offer.State);
        Assert.Null(offer.Record);
    }

    /// <summary>"Shut down and ignore all sessions" is the owner saying the sessions do not matter. The
    /// record says what was closed; nothing in it is offered back.</summary>
    [Fact]
    public async Task An_ignore_all_record_is_not_offered()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(WayUpTestRig.Record(
            "restart-ignored", Shutdown, new[] { WayUpTestRig.Owed("seat-1", "A lead") },
            shutdownKind: WorkspaceShutdownKinds.IgnoreAll));

        var offer = await rig.WayUp().FindOfferAsync(CancellationToken.None);

        Assert.Equal(WayUpOfferState.NothingWaiting, offer.State);
        Assert.Null(offer.Record);
    }

    /// <summary>
    /// ONE OWED SEAT IS ENOUGH, and nothing is asked about what is running.
    ///
    /// READ THIS BEFORE YOU BELIEVE THE SEAM CANNOT ASK: it can. The engine's Gateway seam has a fourth
    /// method, <c>GetRosterAsync</c>, because the REOPEN needs it to refuse a seat that may still be
    /// alive. So what keeps the start-up check a promise about the RECORD rather than a race with whatever
    /// happens to be running is no longer the seam's shape - it is the count asserted at the end of this
    /// test, and the same count asserted on the nothing-waiting path here, on the history and on the bring
    /// back. Only <c>ReopenAsync</c> may ask. If you are adding a roster call to another path, those four
    /// assertions are what you are about to break, and breaking them is the point of them.
    /// </summary>
    [Fact]
    public async Task A_record_with_one_owed_seat_is_offered_and_nothing_is_asked_about_running_sessions()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(WayUpTestRig.Record("restart-owed", Shutdown, new[]
        {
            WayUpTestRig.AlreadyBack("seat-1", "A lead"),
            WayUpTestRig.Owed("seat-2", "A worker"),
        }));

        var offer = await rig.WayUp().FindOfferAsync(CancellationToken.None);

        Assert.Equal(WayUpOfferState.Offered, offer.State);
        Assert.Equal("A restart is available", offer.Message);
        var record = Assert.IsType<WayUpRecord>(offer.Record);
        Assert.Equal("restart-owed", record.WorkspaceId);
        Assert.Equal(1, record.SeatsOwed);
        Assert.Equal("One session is waiting to be brought back.", record.SeatsOwedLabel);
        Assert.Equal("Reason: update to 2.9.0", record.ReasonLabel);
        Assert.Equal(Shutdown, record.ShutdownAtUtc);
        Assert.Empty(rig.Gateway.Started);

        // The seam CAN ask what is running - the reopen needs it, to refuse a seat that may still be alive -
        // so the rule that the start-up check never asks is now held by this count rather than by the seam
        // having no such method at all.
        Assert.Equal(0, rig.Gateway.RosterAsked);
    }

    /// <summary>Newest first: the record offered is the most recent one that may be offered, not the first
    /// the Gateway happens to list.</summary>
    [Fact]
    public async Task The_newest_record_that_may_be_offered_is_the_one_offered()
    {
        var rig = new WayUpTestRig();
        rig.Gateway
            .With(WayUpTestRig.Record("restart-older", Shutdown.AddDays(-2), new[] { WayUpTestRig.Owed("seat-1", "Older") }))
            .With(WayUpTestRig.Record("restart-newer", Shutdown, new[] { WayUpTestRig.Owed("seat-2", "Newer") }));

        var offer = await rig.WayUp().FindOfferAsync(CancellationToken.None);

        Assert.Equal(WayUpOfferState.Offered, offer.State);
        Assert.Equal("restart-newer", offer.Record?.WorkspaceId);
        // It stopped at the first one it could offer rather than reading the rest.
        Assert.Equal(new[] { "restart-newer" }, rig.Gateway.Read);
    }

    /// <summary>A Director with years of records must not make hundreds of calls at start-up, so only the
    /// newest twenty-five are read as documents.</summary>
    [Fact]
    public async Task Only_the_newest_twenty_five_records_are_read()
    {
        var rig = new WayUpTestRig();
        for (var i = 0; i < 40; i++)
        {
            rig.Gateway.With(WayUpTestRig.Record(
                $"restart-{i:00}", Shutdown.AddHours(-i), new[] { WayUpTestRig.Owed($"seat-{i}", "A seat") },
                shutdownKind: WorkspaceShutdownKinds.IgnoreAll));
        }

        var offer = await rig.WayUp().FindOfferAsync(CancellationToken.None);

        Assert.Equal(WayUpOfferState.NothingWaiting, offer.State);
        Assert.Equal(25, rig.Gateway.Read.Count);
        Assert.Equal("restart-00", rig.Gateway.Read[0]);
        Assert.Equal("restart-24", rig.Gateway.Read[24]);
    }

    /// <summary>A record listed and then deleted is not an error and not a silence: the next record is read.</summary>
    [Fact]
    public async Task A_record_listed_and_then_deleted_is_passed_over()
    {
        var rig = new WayUpTestRig();
        rig.Gateway
            .With(WayUpTestRig.Record("restart-gone", Shutdown, new[] { WayUpTestRig.Owed("seat-1", "Gone") }))
            .With(WayUpTestRig.Record("restart-here", Shutdown.AddHours(-1), new[] { WayUpTestRig.Owed("seat-2", "Here") }));
        rig.Gateway.ListedButGone.Add("restart-gone");

        var offer = await rig.WayUp().FindOfferAsync(CancellationToken.None);

        Assert.Equal(WayUpOfferState.Offered, offer.State);
        Assert.Equal("restart-here", offer.Record?.WorkspaceId);
    }

    /// <summary>
    /// ONE ROW PER MISSION HEAD, LEADS FIRST, ALL TICKED (ruling 10.2: the owner is asked once for the lot,
    /// not once per session). Each row carries the seats under it, however deep, with the lead first.
    /// </summary>
    [Fact]
    public async Task Rows_are_one_per_mission_head_with_its_own_seats_all_ticked()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(WayUpTestRig.Record("restart-two-missions", Shutdown, new[]
        {
            WayUpTestRig.Owed("lead-a", "Delivery Lead A", mission: "Mission A", role: "Manager", sortOrder: 0),
            WayUpTestRig.Owed("worker-a2", "Worker A2", reportsTo: "tech-a", mission: "Mission A", sortOrder: 2),
            WayUpTestRig.Owed("tech-a", "Tech Lead A", reportsTo: "lead-a", mission: "Mission A", sortOrder: 1),
            WayUpTestRig.Owed("lead-b", "Delivery Lead B", mission: "Mission B", sortOrder: 3),
        }));

        var offer = await rig.WayUp().FindOfferAsync(CancellationToken.None);
        var rows = offer.Record?.Rows ?? Array.Empty<WayUpRow>();

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True(r.Ticked));
        Assert.All(rows, r => Assert.Equal(WayUpRowKind.BringBack, r.Kind));

        Assert.Equal("lead-a", rows[0].RowId);
        Assert.Equal("Mission A: Delivery Lead A", rows[0].Title);
        Assert.Equal(new[] { "lead-a", "tech-a", "worker-a2" }, rows[0].Seats.Select(s => s.SessionId).ToArray());

        Assert.Equal("lead-b", rows[1].RowId);
        Assert.Equal("Mission B: Delivery Lead B", rows[1].Title);
        Assert.Equal(new[] { "lead-b" }, rows[1].Seats.Select(s => s.SessionId).ToArray());
        Assert.Equal(4, offer.Record?.SeatsOwed);
    }

    /// <summary>A seat whose reporting line names a seat this record does not hold is its own mission head:
    /// the chain stops where the record stops.</summary>
    [Fact]
    public async Task A_seat_reporting_outside_the_record_is_its_own_mission_head()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(WayUpTestRig.Record("restart-outside", Shutdown, new[]
        {
            WayUpTestRig.Owed("worker", "A worker", reportsTo: "a-session-on-another-director"),
        }));

        var offer = await rig.WayUp().FindOfferAsync(CancellationToken.None);
        var row = Assert.Single(offer.Record?.Rows ?? Array.Empty<WayUpRow>());

        Assert.Equal("worker", row.RowId);
        Assert.Equal(WayUpRowKind.BringBack, row.Kind);
    }

    /// <summary>
    /// A SEAT THAT ENDED WITHOUT A HANDOVER IS ITS OWN ROW, UNTICKED (ruling 10.3). It is not brought back
    /// with the rest, because there is no handover for it to read; its one offer is its saved conversation.
    ///
    /// The drain already writes such a seat "undecided", so the restore refuses it too. This row makes that
    /// visible rather than adding a second guard on top of it.
    /// </summary>
    [Fact]
    public async Task A_seat_that_ended_at_the_limit_is_its_own_unticked_row()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(WayUpTestRig.Record("restart-ended", Shutdown, new[]
        {
            WayUpTestRig.Owed("lead", "A lead", sortOrder: 0),
            WayUpTestRig.Ended("stuck", "A busy worker"),
        }));

        var offer = await rig.WayUp().FindOfferAsync(CancellationToken.None);
        var rows = offer.Record?.Rows ?? Array.Empty<WayUpRow>();

        Assert.Equal(2, rows.Count);
        Assert.Equal(WayUpRowKind.BringBack, rows[0].Kind);
        Assert.True(rows[0].Ticked);

        var ended = rows[1];
        Assert.Equal(WayUpRowKind.EndedWithoutHandover, ended.Kind);
        Assert.False(ended.Ticked);
        Assert.Equal("stuck", ended.RowId);
        Assert.Equal("A busy worker - ended without a handover", ended.Title);
        Assert.Empty(ended.Seats);
        Assert.Contains("still running when time ran out", ended.Detail);

        // It is not counted among the seats waiting to come back: only the lead is owed.
        Assert.Equal(1, offer.Record?.SeatsOwed);
    }

    /// <summary>A seat that never answered ended without a handover just as surely, and is offered the same way.</summary>
    [Fact]
    public async Task A_seat_that_never_answered_is_also_its_own_unticked_row()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(WayUpTestRig.Record("restart-silent", Shutdown, new[]
        {
            WayUpTestRig.Owed("lead", "A lead"),
            WayUpTestRig.Ended("silent", "A silent worker", drainState: WorkspaceDrainStates.Unreachable),
        }));

        var offer = await rig.WayUp().FindOfferAsync(CancellationToken.None);
        var ended = Assert.Single(
            offer.Record?.Rows ?? Array.Empty<WayUpRow>(), r => r.Kind == WayUpRowKind.EndedWithoutHandover);

        Assert.False(ended.Ticked);
        Assert.Contains("never answered", ended.Detail);
    }

    /// <summary>
    /// CLAUDE CODE can be started on a saved conversation - its driver passes <c>--resume</c> - so the offer
    /// says the conversation comes back.
    /// </summary>
    [Fact]
    public async Task A_claude_code_seat_is_offered_its_saved_conversation()
    {
        var reopen = await ReopenOfferFor("ClaudeCode");

        Assert.True(reopen.CanReopen);
        Assert.Equal("Reopen its saved conversation", reopen.Offer);
        Assert.Contains("Claude Code is started again on this session's saved conversation", reopen.What);
    }

    /// <summary>PI takes a session id on its command line, so it is offered the same way as Claude Code.</summary>
    [Fact]
    public async Task A_pi_seat_is_offered_its_saved_conversation()
    {
        var reopen = await ReopenOfferFor("Pi");

        Assert.True(reopen.CanReopen);
        Assert.Equal("Reopen its saved conversation", reopen.Offer);
        Assert.Contains("Pi is started again on this session's saved conversation", reopen.What);
    }

    /// <summary>
    /// CODEX IGNORES THE CONVERSATION ID - its driver logs "ignoring resume" and starts fresh - so the offer
    /// says so in plain words rather than promising a conversation that will not arrive. There is no second
    /// way round it and there must not be one.
    /// </summary>
    [Fact]
    public async Task A_codex_seat_is_offered_a_fresh_session_and_told_so()
    {
        var reopen = await ReopenOfferFor("Codex");

        Assert.True(reopen.CanReopen);
        Assert.Equal("Open a fresh session in its repository", reopen.Offer);
        Assert.Contains("Codex cannot be started on a saved conversation", reopen.What);
        Assert.Contains("NEW, blank session", reopen.What);
    }

    /// <summary>
    /// COPILOT REALLY DOES RESUME: <c>CopilotAgent.BuildLaunchSpec</c> appends "--resume &lt;id&gt;", so the
    /// offer must not tell the owner his conversation is lost. This was the defect in review finding 2 - a
    /// hand-written list of agent names in the wording knew only Claude Code and Pi - and the fix was to
    /// read the fact from the agent's own plugin instead.
    /// </summary>
    [Fact]
    public async Task A_copilot_seat_is_offered_its_saved_conversation()
    {
        var reopen = await ReopenOfferFor("Copilot");

        Assert.True(reopen.CanReopen);
        Assert.Equal("Reopen its saved conversation", reopen.Offer);
        Assert.Contains("GitHub Copilot is started again on this session's saved conversation", reopen.What);
        Assert.DoesNotContain("NEW, blank session", reopen.What);
    }

    /// <summary>CURSOR REALLY DOES RESUME TOO: its driver appends "--resume=&quot;&lt;id&gt;&quot;".</summary>
    [Fact]
    public async Task A_cursor_seat_is_offered_its_saved_conversation()
    {
        var reopen = await ReopenOfferFor("Cursor");

        Assert.True(reopen.CanReopen);
        Assert.Equal("Reopen its saved conversation", reopen.Offer);
        Assert.Contains("Cursor is started again on this session's saved conversation", reopen.What);
        Assert.DoesNotContain("NEW, blank session", reopen.What);
    }

    /// <summary>
    /// GEMINI, GROK AND OPENCODE EACH LOG THAT THEY ARE IGNORING THE ID, so each is told plainly that the
    /// session will be blank. Named here beside Copilot and Cursor so that this file holds both sides: the
    /// wording is read from each agent's own plugin, and reading it must not turn every agent into a
    /// resuming one.
    /// </summary>
    /// <param name="agent">The agent the seat was running, as a record spells it.</param>
    /// <param name="shownAs">The agent's name as a person reads it.</param>
    [Theory]
    [InlineData("Gemini", "Gemini")]
    [InlineData("Grok", "Grok")]
    [InlineData("OpenCode", "OpenCode")]
    public async Task An_agent_whose_driver_ignores_the_id_is_offered_a_fresh_session(string agent, string shownAs)
    {
        var reopen = await ReopenOfferFor(agent);

        Assert.True(reopen.CanReopen);
        Assert.Equal("Open a fresh session in its repository", reopen.Offer);
        Assert.Contains($"{shownAs} cannot be started on a saved conversation", reopen.What);
        Assert.Contains("NEW, blank session", reopen.What);
    }

    /// <summary>
    /// AN AGENT THIS BUILD HAS NEVER HEARD OF IS WORDED LIKE CODEX, NOT LIKE CLAUDE CODE. This is the safe
    /// way round: a promise of a conversation that does not arrive is worse than a plain "this will be a
    /// blank session", and every agent added after this was written falls in here by itself.
    /// </summary>
    [Fact]
    public async Task An_agent_nothing_knows_is_offered_a_fresh_session_like_codex()
    {
        var reopen = await ReopenOfferFor("Thelonious");

        Assert.True(reopen.CanReopen);
        Assert.Equal("Open a fresh session in its repository", reopen.Offer);
        Assert.Contains("Thelonious cannot be started on a saved conversation", reopen.What);
        Assert.Contains("NEW, blank session", reopen.What);
    }

    /// <summary>A seat with no conversation recorded cannot be reopened at all. It says so, and offers
    /// nothing - rather than showing a button that could never work.</summary>
    [Fact]
    public async Task A_seat_with_no_conversation_says_so_and_offers_nothing()
    {
        var reopen = await ReopenOfferFor("ClaudeCode", conversationId: null);

        Assert.False(reopen.CanReopen);
        Assert.Null(reopen.Offer);
        Assert.Contains("No conversation was recorded for this session", reopen.What);
    }

    /// <summary>
    /// THE GATEWAY DID NOT ANSWER IS A REFUSAL, NEVER AN EMPTY LIST. An empty list reads as "you have no
    /// records", which is a lie about a Gateway that simply could not be reached, and the sessions it
    /// describes would be lost with nobody told.
    /// </summary>
    [Fact]
    public async Task The_gateway_unreachable_is_a_refusal_carrying_the_reason()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(WayUpTestRig.Record("restart-owed", Shutdown, new[] { WayUpTestRig.Owed("seat-1", "A lead") }));
        rig.Gateway.Unreachable = "No connection could be made to the Gateway";

        var offer = await rig.WayUp().FindOfferAsync(CancellationToken.None);

        Assert.Equal(WayUpOfferState.Refused, offer.State);
        Assert.Null(offer.Record);
        Assert.Contains("No connection could be made to the Gateway", offer.Message);
        Assert.Contains("could not be read", offer.Message);
        Assert.NotEqual(WayUpOfferState.NothingWaiting, offer.State);
    }

    /// <summary>A Director with no display name cannot tell its own records from another Director's on the
    /// same machine, so it refuses with that reason rather than matching on a blank.</summary>
    [Fact]
    public async Task A_director_with_no_name_refuses_rather_than_matching_on_a_blank()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(WayUpTestRig.Record("restart-owed", Shutdown, new[] { WayUpTestRig.Owed("seat-1", "A lead") }));

        var offer = await rig.WayUp(directorName: null).FindOfferAsync(CancellationToken.None);

        Assert.Equal(WayUpOfferState.Refused, offer.State);
        Assert.Contains("no display name", offer.Message);
    }

    /// <summary>The reopen offer on the row for one ended seat, through the whole engine.</summary>
    /// <param name="agent">The agent the seat was running.</param>
    /// <param name="conversationId">Its saved conversation, or null when none was recorded.</param>
    private static async Task<WayUpReopenOffer> ReopenOfferFor(string agent, string? conversationId = "the-saved-conversation")
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(WayUpTestRig.Record("restart-agents", Shutdown, new[]
        {
            WayUpTestRig.Owed("lead", "A lead"),
            WayUpTestRig.Ended("ended", "An ended seat", agent, conversationId),
        }));

        var offer = await rig.WayUp().FindOfferAsync(CancellationToken.None);
        var row = Assert.Single(
            offer.Record?.Rows ?? Array.Empty<WayUpRow>(), r => r.Kind == WayUpRowKind.EndedWithoutHandover);
        return Assert.IsType<WayUpReopenOffer>(row.Reopen);
    }
}
