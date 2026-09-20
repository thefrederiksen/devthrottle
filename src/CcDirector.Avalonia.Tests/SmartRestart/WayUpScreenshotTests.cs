using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Avalonia.Threading;
using CcDirector.Avalonia.SmartRestart;
using CcDirector.ControlApi.SmartRestart;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests.SmartRestart;

/// <summary>
/// ONE PICTURE PER MOMENT of the two way up windows, drawn by real Skia from the real windows.
///
/// Every run draws every picture and proves each one is a REAL DRAWING and not a blank frame: a blank
/// frame is one flat colour with a handful of distinct colours, and a drawn window carries the window's
/// own background, the panel background and hundreds of colours from the drawn text. That check exists
/// because nobody downstream may be able to open an image, so "the picture was written" has to mean
/// something without anyone looking.
///
/// The pictures are WRITTEN only when SMART_RESTART_SCREENSHOT_DIR names a folder, so an ordinary test
/// run leaves nothing on disk.
///
/// WHAT THIS DOES NOT PROVE: that a picture looks right. A person or an agent opens the files for that.
/// The words in them are the test's stand-ins for the engine's labels, written to read the way the
/// mission words them; the real engine's words may differ.
/// </summary>
public class WayUpScreenshotTests
{
    private const string FolderVariable = "SMART_RESTART_SCREENSHOT_DIR";

    /// <summary>The window background from docs/VisualStyle.md, which a blank frame cannot hold.</summary>
    private const uint WindowBackground = 0xFF252526;

    /// <summary>The panel background from docs/VisualStyle.md.</summary>
    private const uint PanelBackground = 0xFF1E1E1E;

    [AvaloniaFact]
    public async Task Capture_EveryMomentOfTheTwoWindows_DrawsARealPicture()
    {
        var folder = Environment.GetEnvironmentVariable(FolderVariable);
        if (!string.IsNullOrWhiteSpace(folder))
            Directory.CreateDirectory(folder);

        Capture(OfferWithThreeMissions(), folder, "way-up-1-offer-three-missions-all-ticked.png");
        Capture(OfferWithAnEndedSeat(), folder, "way-up-2-offer-with-a-seat-that-ended-without-a-handover.png");
        Capture(OfferWithOneMission(), folder, "way-up-3-offer-one-session.png");
        Capture(await AfterABringBackAsync(), folder, "way-up-4-offer-after-a-bring-back.png");
        Capture(await HistoryWithThreeRecordsAsync(), folder, "way-up-5-history-three-records.png");
        Capture(await HistoryWithNothingInItAsync(), folder, "way-up-6-history-empty.png");
        Capture(await HistoryRefusedAsync(), folder, "way-up-7-history-gateway-did-not-answer.png");
        var historyWithAReopenButton = await HistoryWithAReopenButtonAsync();
        AssertTheReopenButtonIsReallyDrawn(historyWithAReopenButton);
        Capture(historyWithAReopenButton, folder,
            "way-up-8-history-a-seat-that-ended-without-a-handover.png");

        // ===== The owner's own case, and the states he will ask about (product issue 3230) =====
        // EVERY WORD IN THESE FIVE IS THE REAL ENGINE'S, not this test's: each one builds a stored record
        // and asks DirectorWayUp to word it. The owner reads these pictures, so what is in them has to be
        // what the product would put in front of him.
        var ownersCase = OpenOffer(TheOwnersOwnCase());
        AssertTheClearingActionIsReallyDrawn(ownersCase);
        Capture(ownersCase, folder, "way-up-9-offer-one-to-bring-back-and-six-that-ended.png");

        var ownersCaseOpened = OpenOffer(TheOwnersOwnCase());
        WayUpOfferWindowTests.OpenTheEndedSection((WayUpOfferWindow)ownersCaseOpened);
        AssertTheSixEndedRowsAreReallyDrawn(ownersCaseOpened);
        Capture(ownersCaseOpened, folder, "way-up-10-offer-the-six-opened.png");

        Capture(OpenOffer(NothingToBringBack()), folder, "way-up-11-offer-nothing-to-bring-back.png");

        Capture(await HistoryARecordAlreadyDealtWithAsync(), folder,
            "way-up-12-history-a-record-already-dealt-with-is-not-offered.png");

        Capture(await HistoryARecordOlderThanSevenDaysAsync(), folder,
            "way-up-13-history-older-than-seven-days-is-not-offered-at-start-up.png");

        // ===== The owner's ruling of 20 September 2026: offered once, and clearable =====
        Capture(await HistoryARecordThatWasUsedAsync(), folder,
            "way-up-14-history-a-record-that-was-used-is-not-offered-again.png");

        Capture(await HistoryARecordThatWasClearedAsync(), folder,
            "way-up-15-history-a-record-the-owner-cleared.png");
    }

    /// <summary>
    /// THE NINTH PICTURE IS NOW THE OFFER WITH THE THIRD ACTION ON IT, and "more than a hundred colours"
    /// cannot say that the action is really there. This asserts the button, its words, and the engine's
    /// sentence beside it - the sentence that names "Not now", which is how the owner tells the two apart.
    /// </summary>
    private static void AssertTheClearingActionIsReallyDrawn(Window window)
    {
        var offer = (WayUpOfferWindow)window;
        Assert.True(offer.BtnClear.IsEffectivelyVisible);
        Assert.Equal(WayUpOfferViewModel.ClearButtonText, offer.BtnClear.Content);
        Assert.True(offer.TxtClearDetail.IsEffectivelyVisible);
        Assert.Equal(WayUpWords.ClearDetail, offer.TxtClearDetail.Text);
    }

    /// <summary>
    /// A RECORD THE OWNER HAS USED, WHICH IS NOT OFFERED AGAIN - his ruling of 20 September 2026: "as soon as
    /// we have used a restart it should no longer be offered on startup". One session came back; six were
    /// never touched. Those six are all HERE, each with its own button, and the entry says in the engine's own
    /// sentence why the Director has stopped putting it in front of him.
    /// </summary>
    private static Task<Window> HistoryARecordThatWasUsedAsync()
    {
        var doc = Stored(
            "restart-used-once",
            "updating the Director to 2.8.2",
            Owed("s-billing-lead", "Billing - Delivery Lead - invoices", "Billing", "Delivery Lead"),
            Ended("s-voice", "Voice - Developer - the wake word"),
            Ended("s-fleet", "Fleet - Tech Lead - the restart"),
            Ended("s-docs", "Docs - Developer - the install page"),
            Ended("s-totals", "Billing - Developer - the totals"),
            Ended("s-export", "Billing - Developer - the export"),
            Ended("s-review", "Docs - Reviewer - the install page"));
        doc.Seats[0].RestoredSessionId = "a1b2c3d4-e5f6";

        return OpenHistoryAsync(new WayUpHistory(
            false,
            WayUpWords.HistoryRead(1),
            [DirectorWayUp.BuildHistoryEntry(doc, Now)]));
    }

    /// <summary>
    /// A RECORD THE OWNER CLEARED WITHOUT USING IT - his own case: "it could be that they shut down but they
    /// don't want to use it and they don't want to see it on every upstart." Nothing was brought back and
    /// nothing was deleted: every seat is still here with everything it had, and the entry says he asked, and
    /// when.
    /// </summary>
    private static Task<Window> HistoryARecordThatWasClearedAsync()
    {
        var doc = Stored(
            "restart-cleared",
            "the machine had to be rebooted",
            Owed("s-billing-lead", "Billing - Delivery Lead - invoices", "Billing", "Delivery Lead"),
            Ended("s-voice", "Voice - Developer - the wake word"),
            Ended("s-fleet", "Fleet - Tech Lead - the restart"));
        doc.ClearedFromStartUpOfferAtUtc = Now.AddHours(-2);
        doc.ClearedFromStartUpOfferByDirectorId = "the-director-after-the-restart";

        return OpenHistoryAsync(new WayUpHistory(
            false,
            WayUpWords.HistoryRead(1),
            [DirectorWayUp.BuildHistoryEntry(doc, Now)]));
    }

    // ===== The owner's own case, worded by the real engine =====

    /// <summary>
    /// WHAT HE PHOTOGRAPHED, AS IT READS NOW. One session handed over and can be brought back; six ended
    /// without a handover. Before this change the headline said "One session is waiting to be brought back"
    /// over all seven rows at once, and he could not read it.
    ///
    /// The record is a STORED record and the words are <see cref="DirectorWayUp"/>'s own.
    /// </summary>
    private static WayUpRecord TheOwnersOwnCase() => DirectorWayUp.BuildRecord(Stored(
        "restart-20-september",
        "updating the Director to 2.8.2",
        Owed("s-billing-lead", "Billing - Delivery Lead - invoices", "Billing", "Delivery Lead"),
        Ended("s-voice", "Voice - Developer - the wake word"),
        Ended("s-fleet", "Fleet - Tech Lead - the restart"),
        Ended("s-docs", "Docs - Developer - the install page"),
        Ended("s-totals", "Billing - Developer - the totals"),
        Ended("s-export", "Billing - Developer - the export"),
        Ended("s-review", "Docs - Reviewer - the install page", conversationId: null)));

    /// <summary>
    /// THE RECORD THE OPERATING SYSTEM'S OWN SHUTDOWN WRITES (ruling 10.5): nothing handed over at all. The
    /// section is open from the start, because it is the whole window, and no bring back answer is drawn.
    /// </summary>
    private static WayUpRecord NothingToBringBack() => DirectorWayUp.BuildRecord(Stored(
        "restart-machine-rebooted",
        null,
        Ended("s-voice", "Voice - Developer - the wake word"),
        Ended("s-fleet", "Fleet - Tech Lead - the restart"),
        Ended("s-docs", "Docs - Developer - the install page")));

    /// <summary>
    /// A RECORD EVERY SEAT OF WHICH HAS BEEN DEALT WITH IS NOT OFFERED AND IS STILL READ HERE - the owner's
    /// "Once you restart a session, it shouldn't be there anymore." One seat came back through the restore and
    /// two had their saved conversations reopened, so the entry carries NO offer and no bring back button, and
    /// each seat says what became of it.
    /// </summary>
    private static Task<Window> HistoryARecordAlreadyDealtWithAsync()
    {
        var doc = Stored(
            "restart-already-answered",
            "updating the Director to 2.8.2",
            Owed("s-billing-lead", "Billing - Delivery Lead - invoices", "Billing", "Delivery Lead"),
            Ended("s-voice", "Voice - Developer - the wake word"),
            Ended("s-fleet", "Fleet - Tech Lead - the restart"));
        doc.Seats[0].RestoredSessionId = "a1b2c3d4-e5f6";
        Reopened(doc.Seats[1], "b2c3d4e5-f6a1");
        Reopened(doc.Seats[2], null);

        return OpenHistoryAsync(new WayUpHistory(
            false,
            WayUpWords.HistoryRead(1),
            [DirectorWayUp.BuildHistoryEntry(doc, Now)]));
    }

    /// <summary>
    /// A RECORD OLDER THAN THE DIRECTOR OFFERS ONE FOR - "or they should timeout", in the owner's words. It has
    /// stopped appearing when the Director starts, it says so here in the engine's own sentence, and it still
    /// carries its offer: his reason for having a history at all is restarting something later.
    /// </summary>
    private static Task<Window> HistoryARecordOlderThanSevenDaysAsync()
    {
        var doc = Stored(
            "restart-nine-days-ago",
            "trying the new launcher",
            Owed("s-billing-lead", "Billing - Delivery Lead - invoices", "Billing", "Delivery Lead"),
            Ended("s-voice", "Voice - Developer - the wake word"));
        doc.CompletedAtUtc = Now.AddDays(-9);
        doc.StartedAtUtc = Now.AddDays(-9).AddMinutes(-10);
        doc.CreatedUtc = doc.StartedAtUtc.Value;
        doc.UpdatedUtc = Now.AddDays(-9);

        return OpenHistoryAsync(new WayUpHistory(
            false,
            WayUpWords.HistoryRead(1),
            [DirectorWayUp.BuildHistoryEntry(doc, Now)]));
    }

    /// <summary>The moment these pictures are taken at, so a picture of an age rule is not a picture of
    /// whatever day the suite happens to run on.</summary>
    private static readonly DateTime Now = new(2026, 9, 20, 16, 36, 0, DateTimeKind.Utc);

    /// <summary>A stored record of a smart shutdown, as the Gateway holds one.</summary>
    private static WorkspaceDocument Stored(string id, string? reason, params WorkspaceSeat[] seats) => new()
    {
        Id = id,
        Name = id,
        Origin = WorkspaceOrigins.Captured,
        Machine = "SOREN_NORTH",
        DirectorId = "the-director-before-the-restart",
        DirectorName = "DevThrottle_1",
        ShutdownKind = WorkspaceShutdownKinds.SmartShutdown,
        Reason = reason,
        StartedAtUtc = new DateTime(2026, 9, 20, 16, 26, 0, DateTimeKind.Utc),
        CompletedAtUtc = new DateTime(2026, 9, 20, 16, 36, 0, DateTimeKind.Utc),
        CreatedUtc = new DateTime(2026, 9, 20, 16, 26, 0, DateTimeKind.Utc),
        UpdatedUtc = new DateTime(2026, 9, 20, 16, 36, 0, DateTimeKind.Utc),
        Seats = seats.ToList(),
    };

    private static WorkspaceSeat Owed(string id, string name, string? mission, string? role) => new()
    {
        SessionId = id,
        Name = name,
        Agent = "ClaudeCode",
        RepoPath = @"D:\ReposFred\devthrottle",
        Role = role,
        Mission = mission is null ? null : new WorkspaceMissionRef { Name = mission },
        HandoverPath = @"C:\handovers\lead.md",
        DrainState = WorkspaceDrainStates.Drained,
        ClaudeSessionId = $"conversation-of-{id}",
        Restore = new WorkspaceSeatRestore { Decision = WorkspaceRestoreDecisions.Restore, Why = "it was mid-task" },
    };

    private static WorkspaceSeat Ended(string id, string name, string? conversationId = "the-saved-conversation") => new()
    {
        SessionId = id,
        Name = name,
        Agent = "ClaudeCode",
        RepoPath = @"D:\ReposFred\devthrottle",
        DrainState = WorkspaceDrainStates.EndedAtLimit,
        ClaudeSessionId = conversationId,
        Restore = new WorkspaceSeatRestore { Decision = WorkspaceRestoreDecisions.Undecided },
    };

    private static void Reopened(WorkspaceSeat seat, string? reopenedAs)
    {
        seat.Restore!.ReopenedAtUtc = new DateTime(2026, 9, 20, 16, 50, 0, DateTimeKind.Utc);
        seat.Restore.ReopenedByDirectorId = "the-director-after-the-restart";
        seat.Restore.ReopenedSessionId = reopenedAs;
    }

    /// <summary>
    /// The tenth picture is only worth anything if the SIX rows are really in it, and "more than a hundred
    /// colours" cannot say that. Five of them carry a reopen button and the sixth, whose conversation was
    /// never recorded, carries the engine's sentence saying there is nothing to reopen and no button at all.
    /// </summary>
    private static void AssertTheSixEndedRowsAreReallyDrawn(Window window)
    {
        var rows = window.GetVisualDescendants().OfType<CheckBox>()
            .Where(b => b.IsEffectivelyVisible && b.Content is string c && c.EndsWith("ended without a handover"))
            .ToList();
        Assert.Equal(6, rows.Count);
        Assert.All(rows, box => Assert.False(box.IsChecked));

        var buttons = window.GetVisualDescendants().OfType<Button>()
            .Where(b => b is not ToggleButton && b.IsEffectivelyVisible && b.Name is null)
            .ToList();
        Assert.Equal(5, buttons.Count);
        Assert.All(buttons, b => Assert.Equal("Reopen its saved conversation", b.Content));
    }

    /// <summary>
    /// The eighth picture is only worth anything if the BUTTON is really in it, and "more than a hundred
    /// colours" cannot say that. A visible button whose data context is a history seat is exactly what
    /// the window draws beside a seat that ended without a handover, so this asserts the thing the
    /// picture is for - and that the second seat, the one with no conversation, has no button.
    /// </summary>
    private static void AssertTheReopenButtonIsReallyDrawn(Window window)
    {
        var buttons = window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.IsEffectivelyVisible && b.DataContext is RestartHistorySeatViewModel)
            .ToList();

        var button = Assert.Single(buttons);
        Assert.Equal("Reopen its saved conversation", button.Content);
        Assert.Equal("s-voice", ((RestartHistorySeatViewModel)button.DataContext!).Seat.SessionId);
    }

    // ===== The offer =====

    private static Window OfferWithThreeMissions() => OpenOffer(WayUp.Record(
        "ws-1",
        "A restart is available",
        "Shut down on 19 September 2026 at 17:50.",
        "Reason: updating the Director to 2.8.2.",
        6,
        "6 sessions are waiting to be brought back.",
        WayUp.BringBackRow(
            "s-billing-lead", "Billing: Billing - Delivery Lead - invoices",
            "Brings back 3 sessions, leads first, each reading its own handover.", true,
            WayUp.Seat("s-billing-lead", "lead", "Billing - Delivery Lead - invoices (Delivery Lead) comes back reading its handover."),
            WayUp.Seat("s-billing-export", "export", "Billing - Developer - the export (Developer) comes back reading its handover."),
            WayUp.Seat("s-billing-totals", "totals", "Billing - Developer - the totals (Developer) comes back reading its handover.")),
        WayUp.BringBackRow(
            "s-fleet-lead", "Fleet: Fleet - Tech Lead - the restart",
            "Brings back 2 sessions, leads first, each reading its own handover.", true,
            WayUp.Seat("s-fleet-lead", "lead", "Fleet - Tech Lead - the restart (Tech Lead) comes back reading its handover."),
            WayUp.Seat("s-fleet-history", "history", "Fleet - Developer - the history list (Developer) comes back reading its handover.")),
        WayUp.BringBackRow(
            "s-docs", "Docs site: Docs - Developer - the install page",
            "Brings back one session, reading its own handover.", true,
            WayUp.Seat("s-docs", "docs", "Docs - Developer - the install page (Developer) comes back reading its handover."))));

    private static Window OfferWithAnEndedSeat() => OpenOffer(WayUp.Record(
        "ws-2",
        "A restart is available",
        "Shut down on 19 September 2026 at 17:50.",
        "No reason was given.",
        3,
        "3 sessions are waiting to be brought back.",
        WayUp.BringBackRow(
            "s-billing-lead", "Billing: Billing - Delivery Lead - invoices",
            "Brings back 3 sessions, leads first, each reading its own handover.", true,
            WayUp.Seat("s-billing-lead", "lead", "Billing - Delivery Lead - invoices (Delivery Lead) comes back reading its handover."),
            WayUp.Seat("s-billing-export", "export", "Billing - Developer - the export (Developer) comes back reading its handover."),
            WayUp.Seat("s-billing-totals", "totals", "Billing - Developer - the totals (Developer) comes back with no handover of its own.")),
        WayUp.EndedRow(
            "s-voice", "Voice - Developer - the wake word - ended without a handover",
            "It was still running when time ran out and was shut down for it. It is not brought back with the rest, because there is no handover for it to read.",
            canReopen: true,
            offer: "Reopen its saved conversation",
            what: "Claude Code is started again on this session's saved conversation, in the same repository, and told that it was stopped and must check the state of its work before acting."),
        WayUp.EndedRow(
            "s-lost", "Docs - Reviewer - the install page - ended without a handover",
            "It never answered, so nothing was written for it. It is not brought back with the rest, because there is no handover for it to read.",
            canReopen: false,
            offer: null,
            what: "No conversation was recorded for this session, so there is nothing to reopen. Start it again yourself when you are ready.")));

    private static Window OfferWithOneMission() => OpenOffer(WayUp.Record(
        "ws-3",
        "A restart is available",
        "Shut down on 20 September 2026 at 08:05.",
        "Reason: the machine had to be rebooted.",
        1,
        "One session is waiting to be brought back.",
        WayUp.BringBackRow(
            "s-solo", "Docs site: Docs - Developer - the install page",
            "Brings back one session, reading its own handover.", true,
            WayUp.Seat("s-solo", "docs", "Docs - Developer - the install page (Developer) comes back reading its handover."))));

    private static async Task<Window> AfterABringBackAsync()
    {
        var engine = new FakeWayUp
        {
            BringBack = new WayUpBringBackResult(true, null, "5 came back; 1 could not. Each one says why beside it.",
            [
                new WayUpSeatResult("s-billing-lead", "lead", "a1b2c3", "Came back as a1b2c3."),
                new WayUpSeatResult("s-billing-export", "export", "d4e5f6", "Came back as d4e5f6."),
                new WayUpSeatResult("s-billing-totals", "totals", "g7h8i9", "Came back as g7h8i9."),
                new WayUpSeatResult("s-fleet-lead", "fleet lead", "j1k2l3", "Came back as j1k2l3."),
                new WayUpSeatResult("s-fleet-history", "history", "m4n5o6", "Came back as m4n5o6."),
                new WayUpSeatResult("s-docs", "docs", null, "Did not come back: its repository is no longer on this machine."),
            ]),
        };
        var window = WayUpOfferWindowTests.Open(
            ((WayUpOfferWindow)OfferWithThreeMissions()).ViewModel.Record, engine);
        await window.ViewModel.BringBackAsync();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static Window OpenOffer(WayUpRecord record) =>
        WayUpOfferWindowTests.Open(record, new FakeWayUp());

    // ===== The history =====

    private static Task<Window> HistoryWithThreeRecordsAsync() => OpenHistoryAsync(new WayUpHistory(
        false,
        "This Director has 3 restart records, newest first.",
        [
            WayUp.HistoryEntry(
                "ws-1",
                "Shut down on 19 September 2026 at 17:50.",
                "Smart shutdown - every session was asked to hand over.",
                "Reason: updating the Director to 2.8.2.",
                "2 sessions are waiting to be brought back. None has come back yet.",
                WayUp.Record(
                    "ws-1", "A restart is available", "Shut down on 19 September 2026 at 17:50.",
                    "Reason: updating the Director to 2.8.2.", 2, "2 sessions are waiting to be brought back.",
                    WayUp.BringBackRow("s-billing-lead", "Billing: Billing - Delivery Lead - invoices",
                        "Brings back 2 sessions, leads first, each reading its own handover.", true,
                        WayUp.Seat("s-billing-lead", "lead", "It comes back reading its handover."))),
                WayUp.HistorySeat("s-billing-lead", "Billing - Delivery Lead - invoices", "Handed over and is waiting to be brought back."),
                WayUp.HistorySeat("s-billing-export", "Billing - Developer - the export", "Handed over and is waiting to be brought back."),
                WayUp.HistorySeat("s-docs", "Docs - Developer - the install page", "Came back as a1b2c3.")),
            WayUp.HistoryEntry(
                "ws-2",
                "Shut down on 18 September 2026 at 09:12.",
                "Smart shutdown - every session was asked to hand over.",
                "No reason was given.",
                "Cancelled on 18 September 2026 at 09:20 - the sessions kept working, so nothing from it is offered back.",
                null,
                WayUp.HistorySeat("s-fleet-lead", "Fleet - Tech Lead - the restart", "Nothing was decided about bringing it back."),
                WayUp.HistorySeat("s-voice", "Voice - Developer - the wake word", "Nothing was decided about bringing it back.")),
            WayUp.HistoryEntry(
                "ws-3",
                "Shut down on 17 September 2026 at 22:04.",
                "Shut down ignoring all sessions - nothing from it is offered back.",
                "Reason: the machine had to be rebooted.",
                "No session was ever marked to come back.",
                null,
                WayUp.EndedHistorySeat("s-billing-totals", "Billing - Developer - the totals",
                    "Ended when time was up, without a handover.",
                    canReopen: true,
                    offer: "Reopen its saved conversation",
                    what: "Claude Code is started again on this session's saved conversation, in the same repository, and told that it was stopped and must check the state of its work before acting.")),
        ]));

    /// <summary>
    /// THE HISTORY WITH A REOPEN BUTTON. A record that owes nothing back - it was shut down ignoring
    /// every session - holding two seats that ended without a handover: one whose saved conversation can
    /// be reopened, drawn with the engine's own button, and one the engine says has no conversation,
    /// drawn with the engine's sentence saying so and NO button.
    ///
    /// This is the picture of finding 3 answered: before it, the history said such a conversation could
    /// be reopened and offered no way to do it.
    /// </summary>
    private static Task<Window> HistoryWithAReopenButtonAsync() => OpenHistoryAsync(new WayUpHistory(
        false,
        "This Director has 2 restart records, newest first.",
        [
            WayUp.HistoryEntry(
                "ws-1",
                "Shut down on 19 September 2026 at 17:50.",
                "Shut down ignoring all sessions - nothing from it is offered back.",
                "Reason: the machine had to be rebooted.",
                "No session was ever marked to come back.",
                null,
                WayUp.EndedHistorySeat("s-voice", "Voice - Developer - the wake word",
                    "Ended when time was up, without a handover.",
                    canReopen: true,
                    offer: "Reopen its saved conversation",
                    what: "Claude Code is started again on this session's saved conversation, in the same repository, and told that it was stopped and must check the state of its work before acting."),
                WayUp.EndedHistorySeat("s-lost", "Docs - Reviewer - the install page",
                    "Never answered, so nothing was written for it.",
                    canReopen: false,
                    offer: null,
                    what: "No conversation was recorded for this session, so there is nothing to reopen. Start it again yourself when you are ready.")),
            WayUp.HistoryEntry(
                "ws-2",
                "Shut down on 18 September 2026 at 09:12.",
                "Smart shutdown - every session was asked to hand over.",
                "No reason was given.",
                "Cancelled on 18 September 2026 at 09:20 - the sessions kept working, so nothing from it is offered back.",
                null,
                WayUp.HistorySeat("s-docs", "Docs - Developer - the install page", "Nothing was decided about bringing it back.")),
        ]));

    private static Task<Window> HistoryWithNothingInItAsync() => OpenHistoryAsync(new WayUpHistory(
        false,
        "This Director has no restart history yet. A record is written every time it shuts its own sessions down.",
        Array.Empty<WayUpHistoryEntry>()));

    private static Task<Window> HistoryRefusedAsync() => OpenHistoryAsync(new WayUpHistory(
        true,
        "The records of what this Director shut down could not be read (the Gateway did not answer). They " +
        "are kept on the Gateway, not on this machine, so nothing can be offered until the Gateway answers. " +
        "Nothing has been lost: try again when it does, or open the restart history later.",
        Array.Empty<WayUpHistoryEntry>()));

    private static async Task<Window> OpenHistoryAsync(WayUpHistory history)
    {
        var window = new RestartHistoryWindow(
            new RestartHistoryViewModel(new FakeWayUp { History = history }))
        {
            RequestedThemeVariant = ThemeVariant.Dark,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        await window.ViewModel.LoadAsync();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    // ===== Drawing, and proving it was drawn =====

    private static void Capture(Window window, string? folder, string fileName)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);

        var colours = DistinctColours(frame!);
        Assert.True(colours.Contains(WindowBackground),
            $"{fileName}: the window background #252526 is not in the picture, so this is not the window");
        Assert.True(colours.Contains(PanelBackground),
            $"{fileName}: the panel background #1E1E1E is not in the picture, so the panel was not drawn");
        Assert.True(colours.Count > 100,
            $"{fileName}: only {colours.Count} distinct colours, so it is blank or no text was drawn");

        if (!string.IsNullOrEmpty(folder))
            frame!.Save(Path.Combine(folder, fileName));

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static HashSet<uint> DistinctColours(WriteableBitmap bitmap)
    {
        using var fb = bitmap.Lock();
        var total = fb.RowBytes * fb.Size.Height;
        var buffer = new byte[total];
        Marshal.Copy(fb.Address, buffer, 0, total);

        // The frame is BGRA or RGBA depending on the platform; read either, refuse anything else.
        var blueFirst = fb.Format == global::Avalonia.Platform.PixelFormat.Bgra8888;
        Assert.True(blueFirst || fb.Format == global::Avalonia.Platform.PixelFormat.Rgba8888,
            $"unexpected pixel format {fb.Format}");

        var colours = new HashSet<uint>();
        for (var y = 0; y < fb.Size.Height; y++)
        {
            var rowStart = y * fb.RowBytes;
            for (var x = 0; x < fb.Size.Width; x++)
            {
                var p = rowStart + x * 4;
                var red = buffer[blueFirst ? p + 2 : p];
                var blue = buffer[blueFirst ? p : p + 2];
                colours.Add(0xFF000000u | ((uint)red << 16) | ((uint)buffer[p + 1] << 8) | blue);
            }
        }

        return colours;
    }
}
