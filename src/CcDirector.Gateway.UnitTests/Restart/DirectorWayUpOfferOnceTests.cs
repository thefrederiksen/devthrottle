using CcDirector.ControlApi.SmartRestart;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Restart;

/// <summary>
/// THE OFFER APPEARS ONCE, AND IT CAN BE CLEARED (the owner's ruling of 20 September 2026).
///
/// His words: "as soon as we have used a restart it should no longer be offered on startup ... So this means
/// that automatically we should only see this restart message once if we use it. The user should also be able
/// to clear it if they don't want it ... they don't want to see it on every upstart."
///
/// TWO RULES, AND BOTH ARE ABOUT INTERRUPTING HIM AT START-UP AND NOTHING ELSE:
///
///  - a record is not offered again once ANYTHING has been brought back or reopened from it, even when six of
///    its seven seats were never touched. THIS IS THE DELIBERATE CONSEQUENCE: those six leave the start-up
///    offer with the record. They are not lost - they stay in the restart history with their buttons, which
///    is the "menu in the file system" he asked for - and the tests below assert both halves together, so the
///    first can never be shipped without the second;
///  - a record he has CLEARED is not offered again either, and clearing brings nothing back and deletes
///    nothing at all.
///
/// Nothing here counts running sessions, for the reason <see cref="DirectorWayUpOfferTests"/> gives: every
/// rule is read off a stored record.
/// </summary>
[Collection(DirectorGatesCollection.Name)]
public class DirectorWayUpOfferOnceTests : IDisposable
{
    private static readonly DateTime Shutdown = new(2026, 9, 19, 21, 50, 0, DateTimeKind.Utc);

    /// <summary>The drain folder a bring back writes its seed file into, beside the handover it points at.
    /// A real folder, because the bring back really writes the file.</summary>
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "offer-once-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public DirectorWayUpOfferOnceTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // The folder is a test's scratch space; a file still held open by the operating system is not a
            // failure of anything this suite proves.
        }
    }

    /// <summary>The owner's own shape: one seat that has already come back, and six that never were touched.</summary>
    private static WorkspaceDocument OneUsedAndSixUntouched(string id = "restart-used-once") =>
        WayUpTestRig.Record(id, Shutdown, new[]
        {
            WayUpTestRig.AlreadyBack("seat-lead", "Billing - Delivery Lead - invoices"),
            WayUpTestRig.Ended("seat-1", "Voice - Developer - the wake word"),
            WayUpTestRig.Ended("seat-2", "Fleet - Tech Lead - the restart"),
            WayUpTestRig.Ended("seat-3", "Docs - Developer - the install page"),
            WayUpTestRig.Ended("seat-4", "Billing - Developer - the totals"),
            WayUpTestRig.Ended("seat-5", "Billing - Developer - the export"),
            WayUpTestRig.Ended("seat-6", "Docs - Reviewer - the install page"),
        });

    /// <summary>A record nothing has been done with yet: one seat waiting, three that ended at the limit.</summary>
    private static WorkspaceDocument Untouched(string id = "restart-untouched", string? handoverFolder = null) =>
        WayUpTestRig.Record(id, Shutdown, new[]
        {
            WayUpTestRig.Owed(
                "seat-lead", "Billing - Delivery Lead - invoices",
                handover: handoverFolder is null
                    ? @"C:\handovers\seat.md"
                    : Path.Combine(handoverFolder, "seat-lead - handover.md")),
            WayUpTestRig.Ended("seat-1", "Voice - Developer - the wake word"),
            WayUpTestRig.Ended("seat-2", "Fleet - Tech Lead - the restart"),
            WayUpTestRig.Ended("seat-3", "Docs - Developer - the install page"),
        });

    // ===================================================================================================
    // USED ONCE MEANS NEVER OFFERED AGAIN
    // ===================================================================================================

    /// <summary>
    /// ONE SEAT BROUGHT BACK OUT OF SEVEN ENDS THE START-UP OFFER FOR THE WHOLE RECORD. Six seats in this
    /// record were never touched and each one could still be reopened - under the older rule ("a record stops
    /// being offered once EVERY seat in it is dealt with") that was six reasons to put the window in front of
    /// him again at every start, which is what he asked us to stop.
    /// </summary>
    [Fact]
    public async Task A_record_with_one_seat_brought_back_and_six_untouched_is_not_offered_again()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(OneUsedAndSixUntouched());

        var offer = await rig.WayUp().FindOfferAsync(CancellationToken.None);

        Assert.Equal(WayUpOfferState.NothingWaiting, offer.State);
        Assert.Null(offer.Record);

        // And it is not that the record holds nothing: it holds six seats that can still be acted on. The
        // rule is about interrupting him, not about what is left.
        var doc = OneUsedAndSixUntouched();
        Assert.True(DirectorWayUp.HasSomethingToActOn(doc));
        Assert.Equal(6, DirectorWayUp.ReopenableSeats(doc).Count);
    }

    /// <summary>
    /// THE OTHER HALF OF THE SAME RULE, AND IT IS ASSERTED IN THE SAME BREATH: the six that left the start-up
    /// offer are all still in the restart history, each still carrying the button that reopens it. If this
    /// ever goes red, the change above has stopped being "offered once" and become "lost".
    /// </summary>
    [Fact]
    public async Task The_six_untouched_seats_are_still_reachable_in_the_restart_history()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(OneUsedAndSixUntouched());

        var history = await rig.WayUp().ReadHistoryAsync(CancellationToken.None);

        var entry = Assert.Single(history.Entries);
        Assert.NotNull(entry.Offer);
        var ended = entry.Offer!.Rows.Where(r => r.Kind == WayUpRowKind.EndedWithoutHandover).ToList();
        Assert.Equal(6, ended.Count);
        Assert.All(ended, row => Assert.True(row.Reopen!.CanReopen));
        Assert.All(ended, row => Assert.Equal("Reopen its saved conversation", row.Reopen!.Offer));

        // And the history says WHY it stopped appearing by itself, rather than letting it vanish in silence.
        Assert.Equal(WayUpWords.AlreadyUsedLabel, entry.NotOfferedAtStartUpLabel);
    }

    /// <summary>
    /// A REOPEN COUNTS AS USING IT, exactly as a bring back does. Both are things he did with this record, and
    /// the ruling is about the restart having been used at all - not about which of the two ways he used it.
    /// </summary>
    [Fact]
    public async Task A_record_with_one_conversation_reopened_and_the_rest_untouched_is_not_offered_again()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(WayUpTestRig.Record("restart-reopened-once", Shutdown, new[]
        {
            WayUpTestRig.Owed("seat-lead", "Billing - Delivery Lead - invoices"),
            WayUpTestRig.AlreadyReopened("seat-1", "Voice - Developer - the wake word"),
            WayUpTestRig.Ended("seat-2", "Fleet - Tech Lead - the restart"),
        }));

        var offer = await rig.WayUp().FindOfferAsync(CancellationToken.None);
        Assert.Equal(WayUpOfferState.NothingWaiting, offer.State);

        // The seat still waiting to be brought back has NOT been thrown away: it is in the history, in the
        // main list of the same offer, ready to come back from there.
        var history = await rig.WayUp().ReadHistoryAsync(CancellationToken.None);
        var entry = Assert.Single(history.Entries);
        Assert.NotNull(entry.Offer);
        Assert.True(entry.Offer!.CanBringBackAnything);
        Assert.Equal(1, entry.Offer.SeatsOwed);
        Assert.Equal(WayUpWords.AlreadyUsedLabel, entry.NotOfferedAtStartUpLabel);
    }

    /// <summary>
    /// THE ENGINE'S OWN REOPEN TAKES THE RECORD OFF THE OFFER - proved by asking the engine to reopen a seat
    /// and then asking it for the offer again, rather than by handing it a record already marked. A test that
    /// only hand-built the marked record would stay green if the reopen stopped writing them.
    /// </summary>
    [Fact]
    public async Task Reopening_one_seat_takes_the_whole_record_off_the_start_up_offer()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(Untouched());
        var wayUp = rig.WayUp();

        var before = await wayUp.FindOfferAsync(CancellationToken.None);
        Assert.Equal(WayUpOfferState.Offered, before.State);

        var reopened = await wayUp.ReopenAsync(
            new WayUpReopenRequest("restart-untouched", "seat-1"), CancellationToken.None);
        Assert.True(reopened.Started);

        var after = await wayUp.FindOfferAsync(CancellationToken.None);
        Assert.Equal(WayUpOfferState.NothingWaiting, after.State);
    }

    /// <summary>
    /// THE ENGINE'S OWN BRING BACK TAKES IT OFF TOO. The restore writes the restored session id onto the seat
    /// through the Gateway, so this test arrives at the used state the way the product does - one seat back,
    /// the two that ended at the limit never touched - and then asks for the offer again.
    /// </summary>
    [Fact]
    public async Task Bringing_one_row_back_takes_the_whole_record_off_the_start_up_offer()
    {
        var rig = new WayUpTestRig();
        var doc = Untouched("restart-brought-back", _folder);
        rig.Gateway.With(doc);
        var wayUp = rig.WayUp();

        Assert.Equal(WayUpOfferState.Offered, (await wayUp.FindOfferAsync(CancellationToken.None)).State);

        var back = await wayUp.BringBackAsync(
            new WayUpBringBackRequest("restart-brought-back", new[] { "seat-lead" }), CancellationToken.None);
        Assert.True(back.Started);

        // The restore is what writes this onto the record; the rig's restore seam does not, so it is written
        // here to stand for what the Gateway stores. The ORDER the restore was handed is asserted above.
        doc.Seats.Single(s => s.SessionId == "seat-lead").RestoredSessionId = "back-seat-lead";

        var after = await wayUp.FindOfferAsync(CancellationToken.None);
        Assert.Equal(WayUpOfferState.NothingWaiting, after.State);
    }

    // ===================================================================================================
    // HE CAN CLEAR IT WITHOUT USING IT
    // ===================================================================================================

    /// <summary>
    /// CLEARING STOPS THE ASKING AND DOES NOTHING ELSE. Nothing is started, no restore is ordered, no seat is
    /// changed, and the record still holds every one of its seats - which is the whole difference between
    /// "stop asking me" and "delete this".
    /// </summary>
    [Fact]
    public async Task A_cleared_record_is_not_offered_and_brings_nothing_back()
    {
        var rig = new WayUpTestRig();
        var doc = Untouched("restart-to-clear");
        rig.Gateway.With(doc);
        var wayUp = rig.WayUp();

        Assert.Equal(WayUpOfferState.Offered, (await wayUp.FindOfferAsync(CancellationToken.None)).State);

        var cleared = await wayUp.ClearFromStartUpOfferAsync(
            new WayUpClearRequest("restart-to-clear"), CancellationToken.None);

        Assert.True(cleared.Cleared);
        Assert.Equal(WayUpWords.ClearedMessage, cleared.Message);
        Assert.Equal(WayUpOfferState.NothingWaiting, (await wayUp.FindOfferAsync(CancellationToken.None)).State);

        // NOTHING WAS STARTED AND NOTHING WAS TAKEN AWAY.
        Assert.Empty(rig.Gateway.Started);
        Assert.Empty(rig.Restore.Orders);
        Assert.Equal(4, doc.Seats.Count);
        Assert.All(doc.Seats, seat => Assert.True(string.IsNullOrWhiteSpace(seat.RestoredSessionId)));
        Assert.All(doc.Seats, seat => Assert.Null(seat.Restore?.ReopenedAtUtc));
    }

    /// <summary>
    /// A CLEARED RECORD STILL READS CORRECTLY IN THE HISTORY: it says he cleared it and when, and it still
    /// carries its whole offer with working buttons. A record that simply stopped appearing with no sentence
    /// anywhere would be the same confusion in the other direction.
    /// </summary>
    [Fact]
    public async Task A_cleared_record_says_so_in_the_history_and_still_offers_everything_it_held()
    {
        var rig = new WayUpTestRig();
        var clearedAt = WayUpTestRig.Now.AddHours(-3);
        rig.Gateway.With(WayUpTestRig.Cleared(Untouched("restart-cleared"), clearedAt));

        var history = await rig.WayUp().ReadHistoryAsync(CancellationToken.None);

        var entry = Assert.Single(history.Entries);
        Assert.Equal(
            WayUpWords.ClearedLabel(DateTime.SpecifyKind(clearedAt, DateTimeKind.Utc).ToLocalTime()),
            entry.NotOfferedAtStartUpLabel);
        Assert.NotNull(entry.Offer);
        Assert.True(entry.Offer!.CanBringBackAnything);
        Assert.Equal(3, entry.Offer.Rows.Count(r => r.Kind == WayUpRowKind.EndedWithoutHandover));
    }

    /// <summary>
    /// "NOT NOW" KEEPS ITS MEANING: ask me again next time. It writes nothing at all - no clearing, no reopen
    /// claim, no restore - so the very next start-up finds the same record and offers it again. That is what
    /// the new answer is different FROM, and the difference is worth a test of its own.
    /// </summary>
    [Fact]
    public async Task Not_now_writes_nothing_and_the_record_is_offered_again_next_time()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(Untouched("restart-not-now"));
        var wayUp = rig.WayUp();

        // "Not now" IS the absence of a call: the window closes and asks the engine nothing. So the second
        // start-up is simply another FindOffer, with nothing in between.
        var first = await wayUp.FindOfferAsync(CancellationToken.None);
        var second = await wayUp.FindOfferAsync(CancellationToken.None);

        Assert.Equal(WayUpOfferState.Offered, first.State);
        Assert.Equal(WayUpOfferState.Offered, second.State);
        Assert.Equal("restart-not-now", second.Record!.WorkspaceId);
        Assert.Empty(rig.Gateway.ClearMarks);
        Assert.Empty(rig.Gateway.ReopenMarks);
        Assert.Empty(rig.Restore.Orders);
    }

    /// <summary>
    /// A GATEWAY THAT CANNOT RECORD THE CLEARING CHANGES NOTHING AND SAYS SO - loud and safe, with no
    /// fallback that pretends. It must say he WILL be asked again, because the one outcome this must never
    /// have is telling him it is dealt with while the record carries nothing of the kind: he would meet the
    /// same window at the next start with no idea why.
    /// </summary>
    [Fact]
    public async Task A_clearing_the_record_cannot_be_marked_with_changes_nothing_and_says_why()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(Untouched("restart-old-gateway"));
        rig.Gateway.RefuseClearMarks = "kind must be one of: started, restored, failed, finished.";
        var wayUp = rig.WayUp();

        var cleared = await wayUp.ClearFromStartUpOfferAsync(
            new WayUpClearRequest("restart-old-gateway"), CancellationToken.None);

        Assert.False(cleared.Cleared);
        Assert.Contains("could not be marked as cleared", cleared.Message);
        Assert.Contains("kind must be one of: started, restored, failed, finished.", cleared.Message);
        Assert.Contains("you WILL be asked about this again", cleared.Message);

        // And the record is exactly as it was: still offered at the next start.
        Assert.Equal(WayUpOfferState.Offered, (await wayUp.FindOfferAsync(CancellationToken.None)).State);
    }

    /// <summary>
    /// A GATEWAY THAT WILL NOT ANSWER AT ALL is the same shape of answer: nothing recorded, and the reason in
    /// plain words. It is a separate case from the refusal above because they fail differently - one answers
    /// and refuses, the other never answers - and both used to be able to read as success.
    /// </summary>
    [Fact]
    public async Task A_clearing_a_gateway_never_answered_changes_nothing_and_says_why()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(Untouched("restart-unreachable"));
        rig.Gateway.Unreachable = "the Gateway is not reachable from this machine";

        var cleared = await rig.WayUp().ClearFromStartUpOfferAsync(
            new WayUpClearRequest("restart-unreachable"), CancellationToken.None);

        Assert.False(cleared.Cleared);
        Assert.Contains("the Gateway is not reachable from this machine", cleared.Message);
        Assert.Contains("you WILL be asked about this again", cleared.Message);
    }

    /// <summary>
    /// ONE DIRECTOR DOES NOT CLEAR ANOTHER DIRECTOR'S RECORD, by the same rule the bring back and the reopen
    /// keep. Nothing is written at all - the refusal comes before the mark.
    /// </summary>
    [Fact]
    public async Task Clearing_a_record_that_belongs_to_another_director_changes_nothing()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(WayUpTestRig.Record(
            "restart-somebody-elses", Shutdown,
            new[] { WayUpTestRig.Owed("seat-lead", "A lead") },
            directorName: WayUpTestRig.OtherDirector));

        var cleared = await rig.WayUp().ClearFromStartUpOfferAsync(
            new WayUpClearRequest("restart-somebody-elses"), CancellationToken.None);

        Assert.False(cleared.Cleared);
        Assert.Contains(WayUpTestRig.OtherDirector, cleared.Message);
        Assert.Empty(rig.Gateway.ClearMarks);
    }

    /// <summary>
    /// A RECORD THE GATEWAY NO LONGER HOLDS is said as it is, and nothing is marked. It is the same answer the
    /// bring back and the reopen give for the same case.
    /// </summary>
    [Fact]
    public async Task Clearing_a_record_that_is_no_longer_there_changes_nothing()
    {
        var rig = new WayUpTestRig();

        var cleared = await rig.WayUp().ClearFromStartUpOfferAsync(
            new WayUpClearRequest("restart-that-went-away"), CancellationToken.None);

        Assert.False(cleared.Cleared);
        Assert.Contains("restart-that-went-away", cleared.Message);
        Assert.Empty(rig.Gateway.ClearMarks);
    }

    /// <summary>
    /// THE CLEARING ANSWER IS ONLY OFFERED WHERE PRESSING IT WOULD CHANGE SOMETHING. A record already cleared,
    /// and one already used, have both stopped interrupting him for good - and a button whose press changes
    /// nothing reads as broken. The engine says so; no window works it out.
    /// </summary>
    [Fact]
    public void The_clearing_answer_is_offered_only_where_pressing_it_would_change_something()
    {
        var untouched = DirectorWayUp.BuildRecord(Untouched());
        Assert.True(untouched.CanClearFromStartUpOffer);
        Assert.Equal(WayUpWords.ClearDetail, untouched.ClearDetail);

        var used = DirectorWayUp.BuildRecord(OneUsedAndSixUntouched());
        Assert.False(used.CanClearFromStartUpOffer);
        Assert.Null(used.ClearDetail);

        var cleared = DirectorWayUp.BuildRecord(WayUpTestRig.Cleared(Untouched("restart-already-cleared")));
        Assert.False(cleared.CanClearFromStartUpOffer);
        Assert.Null(cleared.ClearDetail);
    }

    /// <summary>
    /// THE SENTENCE BESIDE THE CLEARING ANSWER NAMES THE OTHER ANSWER. The owner's ruling asks that the
    /// difference between "Not now" and this one be obvious from the words on the window rather than from a
    /// manual, and it says what is NOT done - nothing brought back, nothing deleted - because "clear" is the
    /// word most likely to be read as "delete".
    /// </summary>
    [Fact]
    public void The_clearing_answer_says_on_the_window_how_it_differs_from_not_now()
    {
        Assert.Contains("Not now", WayUpWords.ClearDetail);
        Assert.Contains("stops it asking at all", WayUpWords.ClearDetail);
        Assert.Contains("nothing is deleted", WayUpWords.ClearDetail);
        Assert.Contains("Restart history", WayUpWords.ClearDetail);

        // And what is said afterwards says the same three things, because that is the moment he is most
        // likely to fear he has thrown something away.
        Assert.Contains("not be asked about this again", WayUpWords.ClearedMessage);
        Assert.Contains("nothing was deleted", WayUpWords.ClearedMessage);
        Assert.Contains("Restart history", WayUpWords.ClearedMessage);
    }

    /// <summary>
    /// THE THREE REASONS A RECORD STOPS APPEARING ARE RANKED, because a record can carry more than one and the
    /// reader gets ONE sentence: used, then cleared, then too old. Asserted through a record that carries all
    /// three at once, which is the only way to prove which one wins.
    /// </summary>
    [Fact]
    public void A_record_that_was_used_cleared_and_aged_out_says_it_was_used()
    {
        var doc = WayUpTestRig.Cleared(OneUsedAndSixUntouched("restart-all-three"));
        doc.CompletedAtUtc = WayUpTestRig.Now.AddDays(-30);

        Assert.Equal(WayUpWords.AlreadyUsedLabel, DirectorWayUp.NotOfferedAtStartUpLabel(doc, WayUpTestRig.Now));

        // Take the use away and the clearing is what is left to say; take that away too and it is the age.
        var clearedAndOld = WayUpTestRig.Cleared(Untouched("restart-cleared-and-old"));
        clearedAndOld.CompletedAtUtc = WayUpTestRig.Now.AddDays(-30);
        Assert.StartsWith("You asked on ", DirectorWayUp.NotOfferedAtStartUpLabel(clearedAndOld, WayUpTestRig.Now));

        var old = Untouched("restart-just-old");
        old.CompletedAtUtc = WayUpTestRig.Now.AddDays(-30);
        Assert.Equal(
            WayUpWords.TooOldToOfferLabel(DirectorWayUp.OfferedForDays),
            DirectorWayUp.NotOfferedAtStartUpLabel(old, WayUpTestRig.Now));
    }
}
