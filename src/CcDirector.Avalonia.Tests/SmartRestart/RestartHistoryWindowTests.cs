using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CcDirector.Avalonia.SmartRestart;
using CcDirector.ControlApi.SmartRestart;
using Xunit;

namespace CcDirector.Avalonia.Tests.SmartRestart;

/// <summary>
/// FILE, RESTART HISTORY, OPENED (mission 5.3 item 11).
///
/// Every test opens the real window and reads what the real controls drew, for the reason set out on
/// <see cref="WayUpOfferWindowTests"/>: the window this pattern replaces threw the moment anybody
/// opened it, and nothing caught it because no test ever opened it.
///
/// WHAT THESE DO NOT COVER. The records are the test's own, not the engine's, and the File menu item
/// that opens this window has no test of its own - it is one line in MainWindow.axaml.cs.
/// </summary>
public class RestartHistoryWindowTests
{
    private static WayUpRecord StillOwed() => WayUp.Record(
        "ws-1",
        "A restart is available",
        "Shut down on 19 September 2026 at 17:50.",
        "Reason: updating the Director.",
        2,
        "2 sessions are waiting to be brought back.",
        WayUp.BringBackRow(
            "s-billing-lead", "Billing: Billing - Delivery Lead - invoices",
            "Brings back 2 sessions, leads first, each reading its own handover.",
            true,
            WayUp.Seat("s-billing-lead", "Billing - Delivery Lead - invoices", "Billing - Delivery Lead - invoices comes back reading its handover."),
            WayUp.Seat("s-billing-export", "Billing - Developer - the export", "Billing - Developer - the export comes back reading its handover.", "s-billing-lead")));

    private static WayUpHistory ThreeRecords() => new(
        Refused: false,
        Message: "This Director has 3 restart records, newest first.",
        Entries:
        [
            WayUp.HistoryEntry(
                "ws-1",
                "Shut down on 19 September 2026 at 17:50.",
                "Smart shutdown - every session was asked to hand over.",
                "Reason: updating the Director.",
                "2 sessions are waiting to be brought back. None has come back yet.",
                StillOwed(),
                WayUp.HistorySeat("s-billing-lead", "Billing - Delivery Lead - invoices", "Handed over and is waiting to be brought back."),
                WayUp.HistorySeat("s-billing-export", "Billing - Developer - the export", "Handed over and is waiting to be brought back.")),
            WayUp.HistoryEntry(
                "ws-2",
                "Shut down on 18 September 2026 at 09:12.",
                "Smart shutdown - every session was asked to hand over.",
                "No reason was given.",
                "Cancelled on 18 September 2026 at 09:20 - the sessions kept working, so nothing from it is offered back.",
                null,
                WayUp.HistorySeat("s-docs", "Docs - Developer - the install page", "Nothing was decided about bringing it back.")),
            WayUp.HistoryEntry(
                "ws-3",
                "Shut down on 17 September 2026 at 22:04.",
                "Shut down ignoring all sessions - nothing from it is offered back.",
                "Reason: the machine had to be rebooted.",
                "No session was ever marked to come back.",
                null,
                WayUp.HistorySeat("s-voice", "Voice - Developer - the wake word", "Never answered, so nothing was written for it. Its saved conversation can be reopened.")),
        ]);

    internal static RestartHistoryWindow Open(FakeWayUp engine)
    {
        var window = new RestartHistoryWindow(new RestartHistoryViewModel(engine))
        {
            RequestedThemeVariant = ThemeVariant.Dark,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    internal static async Task<RestartHistoryWindow> OpenAndLoad(FakeWayUp engine)
    {
        var window = Open(engine);
        await window.ViewModel.LoadAsync();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    /// <summary>The buttons drawn INSIDE a record - the bring back offers. A CheckBox is a ToggleButton
    /// and so is a Button, so toggles are left out by type, and the window's own Close by its name.</summary>
    private static List<Button> RecordButtons(Window window) =>
        window.GetVisualDescendants().OfType<Button>()
            .Where(b => b is not ToggleButton && b.IsEffectivelyVisible && b.Name is null)
            .ToList();

    // ===== The window opens =====

    /// <summary>
    /// THE TEST THE OLD WINDOW NEVER HAD. The window is opened and every named control is connected.
    /// </summary>
    [AvaloniaFact]
    public void Show_GeneratedInitializeComponent_EveryNamedControlIsConnected()
    {
        var window = Open(new FakeWayUp());

        Assert.True(window.IsVisible);
        Assert.NotNull(window.TxtStatus);
        Assert.NotNull(window.EntryList);
        Assert.NotNull(window.BtnClose);
        Assert.Equal("Restart history", window.Title);
    }

    /// <summary>
    /// CLAUDE.md rule 1. The window is on screen and says it is reading BEFORE the engine has answered,
    /// so a Gateway that takes seconds to read never leaves a blank or frozen window.
    /// </summary>
    [AvaloniaFact]
    public void Show_BeforeTheEngineHasAnswered_TheWindowIsUpAndSaysItIsReading()
    {
        var window = Open(new FakeWayUp());

        Assert.True(window.IsVisible);
        Assert.True(window.ViewModel.IsReading);
        Assert.Equal("Reading...", window.TxtStatus.Text);
        Assert.Empty(window.ViewModel.Entries);
    }

    // ===== What it shows =====

    /// <summary>The engine is asked OFF the interface thread - it reads every record from the Gateway
    /// one at a time.</summary>
    [AvaloniaFact]
    public async Task Load_ThreeRecords_AsksTheEngineOffTheInterfaceThread()
    {
        var engine = new FakeWayUp { History = ThreeRecords() };

        await OpenAndLoad(engine);

        Assert.Equal(1, engine.HistoryReads);
        Assert.False(engine.WasEverCalledOnTheInterfaceThread);
    }

    /// <summary>Every record, newest first, each with the engine's own when, kind, reason and outcome -
    /// a smart shutdown that still owes seats, a cancelled one and an ignore-all one.</summary>
    [AvaloniaFact]
    public async Task Load_ThreeRecords_ShowsEachOneInTheEnginesWordsNewestFirst()
    {
        var window = await OpenAndLoad(new FakeWayUp { History = ThreeRecords() });

        Assert.Equal("This Director has 3 restart records, newest first.", window.TxtStatus.Text);
        Assert.False(window.ViewModel.IsReading);
        Assert.Equal(3, window.ViewModel.Entries.Count);
        Assert.Equal(new[] { "ws-1", "ws-2", "ws-3" }, window.ViewModel.Entries.Select(e => e.Entry.WorkspaceId));

        var drawn = WayUpOfferWindowTests.DrawnTexts(window);
        var first = drawn.IndexOf("Shut down on 19 September 2026 at 17:50.");
        var cancelled = drawn.IndexOf("Cancelled on 18 September 2026 at 09:20 - the sessions kept working, so nothing from it is offered back.");
        var ignoreAll = drawn.IndexOf("Shut down ignoring all sessions - nothing from it is offered back.");
        Assert.True(first >= 0, "the newest record's when is not drawn");
        Assert.True(cancelled > first, "the cancelled record is not drawn after the newest");
        Assert.True(ignoreAll > cancelled, "the ignore-all record is not drawn after the cancelled one");

        Assert.Contains("Smart shutdown - every session was asked to hand over.", drawn);
        Assert.Contains("2 sessions are waiting to be brought back. None has come back yet.", drawn);
        Assert.Contains("Reason: the machine had to be rebooted.", drawn);
    }

    /// <summary>Each seat says what became of it, in the engine's words, beside its name.</summary>
    [AvaloniaFact]
    public async Task Load_ThreeRecords_EverySeatSaysWhatBecameOfItInTheEnginesWords()
    {
        var window = await OpenAndLoad(new FakeWayUp { History = ThreeRecords() });
        var drawn = WayUpOfferWindowTests.DrawnTexts(window);

        Assert.Contains("Billing - Delivery Lead - invoices", drawn);
        Assert.Contains("Handed over and is waiting to be brought back.", drawn);
        Assert.Contains("Never answered, so nothing was written for it. Its saved conversation can be reopened.", drawn);
    }

    /// <summary>
    /// An EMPTY history and an UNREADABLE one are different facts and the engine keeps them apart. The
    /// window shows whichever sentence it was handed and never turns one into the other - an empty list
    /// shown for an unreadable Gateway would read as "you have no records", which is a lie.
    /// </summary>
    [AvaloniaFact]
    public async Task Load_AnEmptyHistory_SaysSoInTheEnginesWordsAndIsNotARefusal()
    {
        var empty = "This Director has no restart history yet. A record is written every time it shuts its own sessions down.";
        var window = await OpenAndLoad(new FakeWayUp
        {
            History = new WayUpHistory(false, empty, Array.Empty<WayUpHistoryEntry>()),
        });

        Assert.Equal(empty, window.TxtStatus.Text);
        Assert.Empty(window.ViewModel.Entries);
    }

    [AvaloniaFact]
    public async Task Load_AGatewayThatDidNotAnswer_ShowsTheEnginesRefusalAndNotAnEmptyHistory()
    {
        var refusal = "The records of what this Director shut down could not be read (the Gateway did not answer).";
        var window = await OpenAndLoad(new FakeWayUp
        {
            History = new WayUpHistory(true, refusal, Array.Empty<WayUpHistoryEntry>()),
        });

        Assert.Equal(refusal, window.TxtStatus.Text);
        Assert.DoesNotContain("no restart history yet", window.TxtStatus.Text);
        Assert.Empty(window.ViewModel.Entries);
    }

    // ===== The offer, from the history =====

    /// <summary>
    /// THE WHOLE REASON THE HISTORY EXISTS, in the owner's words: "it could be that I accidentally don't
    /// restart it right away and I want to restart it later". A record that still owes seats carries the
    /// offer; the two that do not, do not.
    /// </summary>
    [AvaloniaFact]
    public async Task Load_ARecordThatStillOwesSeats_CarriesTheOfferAndTheOthersDoNot()
    {
        var window = await OpenAndLoad(new FakeWayUp { History = ThreeRecords() });

        Assert.True(window.ViewModel.Entries[0].HasOffer);
        Assert.False(window.ViewModel.Entries[1].HasOffer);
        Assert.False(window.ViewModel.Entries[2].HasOffer);

        var button = Assert.Single(RecordButtons(window));
        Assert.Equal("Bring back...", button.Content);
    }

    /// <summary>
    /// IT IS THE SAME OFFER, not a second copy of it: the view model the history builds is the one the
    /// start-up window is built from, carrying the engine's own rows, words and ticks. One rule, and no
    /// second wording that could drift away from the first.
    /// </summary>
    [AvaloniaFact]
    public async Task ARecordsOffer_IsTheSameOfferTheStartUpWindowMakes()
    {
        var window = await OpenAndLoad(new FakeWayUp { History = ThreeRecords() });

        var offer = window.ViewModel.Entries[0].BuildOffer();

        Assert.IsType<WayUpOfferViewModel>(offer);
        Assert.Equal("ws-1", offer.Record.WorkspaceId);
        Assert.Equal("A restart is available", offer.Headline);
        Assert.Equal("2 sessions are waiting to be brought back.", offer.SeatsOwedLabel);
        var row = Assert.Single(offer.Rows);
        Assert.Equal("s-billing-lead", row.RowId);
        Assert.True(row.Ticked);
        Assert.Equal(2, row.Seats.Count);
    }

    /// <summary>A record that owes nothing has no offer to build, and says so rather than handing back
    /// an empty one that would look like an offer with nothing in it.</summary>
    [AvaloniaFact]
    public async Task ARecordThatOwesNothing_RefusesToBuildAnOffer()
    {
        var window = await OpenAndLoad(new FakeWayUp { History = ThreeRecords() });

        var ex = Assert.Throws<InvalidOperationException>(() => window.ViewModel.Entries[1].BuildOffer());

        Assert.Contains("owes no seats", ex.Message);
    }

    // ===== Closing =====

    [AvaloniaFact]
    public async Task BtnClose_Clicked_ClosesAndChangesNothing()
    {
        var engine = new FakeWayUp { History = ThreeRecords() };
        var window = await OpenAndLoad(engine);

        window.BtnClose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsVisible);
        Assert.Empty(engine.BringBackRequests);
        Assert.Empty(engine.ReopenRequests);
    }

    // ===== A Director whose own host has not started =====

    /// <summary>
    /// A Director whose host has not started yet has no engine to ask. The history then says so in the
    /// ENGINE's own refusal sentence - the same function the engine itself calls - rather than showing
    /// an empty list, and nothing it could not do claims to have been done.
    /// </summary>
    [AvaloniaFact]
    public async Task Load_WithNoHost_ShowsTheEnginesOwnRefusalWordingAndStartsNothing()
    {
        var engine = new NoHostWayUp();
        var window = new RestartHistoryWindow(new RestartHistoryViewModel(engine))
        {
            RequestedThemeVariant = ThemeVariant.Dark,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        await window.ViewModel.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        var expected = WayUpWords.GatewayRefusal(RestartHistoryWindow.NoHostReason);
        Assert.Equal(expected, window.TxtStatus.Text);
        Assert.Empty(window.ViewModel.Entries);
        Assert.Contains("could not be read", expected);
    }

    /// <summary>
    /// And the same stand-in refuses everything it cannot do, in the same sentence, rather than claiming
    /// a session was started or handing back a silent nothing.
    /// </summary>
    [Fact]
    public async Task WithNoHost_EveryOtherCall_RefusesInTheSameSentenceAndStartsNothing()
    {
        var engine = new NoHostWayUp();
        var expected = WayUpWords.GatewayRefusal(RestartHistoryWindow.NoHostReason);

        var offer = await engine.FindOfferAsync(CancellationToken.None);
        var bringBack = await engine.BringBackAsync(new WayUpBringBackRequest("ws", ["r"]), CancellationToken.None);
        var reopen = await engine.ReopenAsync(new WayUpReopenRequest("ws", "seat"), CancellationToken.None);

        Assert.Equal(WayUpOfferState.Refused, offer.State);
        Assert.Equal(expected, offer.Message);
        Assert.Null(offer.Record);

        Assert.False(bringBack.Started);
        Assert.Equal(expected, bringBack.Message);
        Assert.Empty(bringBack.Seats);

        Assert.False(reopen.Started);
        Assert.Null(reopen.NewSessionId);
        Assert.Equal(expected, reopen.Message);
    }
}
