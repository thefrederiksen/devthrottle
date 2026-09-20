using Avalonia.Headless;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CcDirector.Avalonia.SmartRestart;
using CcDirector.ControlApi.SmartRestart;
using Xunit;

namespace CcDirector.Avalonia.Tests.SmartRestart;

/// <summary>
/// THE "A RESTART IS AVAILABLE" WINDOW, OPENED (mission 5.3 item 10, ruling 10.2).
///
/// Every test here opens the real window and reads what the real controls drew. The window this pattern
/// replaces (DrainDirectorDialog) defined its own InitializeComponent, which skipped the generated code
/// that connects named controls, and it threw the moment anybody opened it in every shipped build -
/// nothing caught it because no test ever opened the window.
///
/// WHAT THESE DO NOT COVER. They do not prove the real engine words anything the way these records do:
/// every record here is built by the test, and that is deliberate (see <see cref="WayUp"/>). They do
/// not prove the File menu opens the history window or that start-up opens this one - the start-up ask
/// has its own tests and the menu item is one line in MainWindow with no test of its own. The window's
/// own X cannot be pressed without a real window frame; Close() is what that X does.
/// </summary>
public class WayUpOfferWindowTests
{
    private static WayUpRecord ThreeMissions() => WayUp.Record(
        "ws-1",
        "A restart is available",
        "Shut down on 19 September 2026 at 17:50.",
        "Reason: updating the Director.",
        6,
        "6 sessions are waiting to be brought back.",
        WayUp.BringBackRow(
            "s-billing-lead", "Billing: Billing - Delivery Lead - invoices",
            "Brings back 3 sessions, leads first, each reading its own handover.",
            true,
            WayUp.Seat("s-billing-lead", "Billing - Delivery Lead - invoices", "Billing - Delivery Lead - invoices (Delivery Lead) comes back reading its handover."),
            WayUp.Seat("s-billing-export", "Billing - Developer - the export", "Billing - Developer - the export (Developer) comes back reading its handover.", "s-billing-lead"),
            WayUp.Seat("s-billing-totals", "Billing - Developer - the totals", "Billing - Developer - the totals (Developer) comes back reading its handover.", "s-billing-lead")),
        WayUp.BringBackRow(
            "s-fleet-lead", "Fleet: Fleet - Tech Lead - the restart",
            "Brings back 2 sessions, leads first, each reading its own handover.",
            true,
            WayUp.Seat("s-fleet-lead", "Fleet - Tech Lead - the restart", "Fleet - Tech Lead - the restart (Tech Lead) comes back reading its handover."),
            WayUp.Seat("s-fleet-history", "Fleet - Developer - the history list", "Fleet - Developer - the history list (Developer) comes back reading its handover.", "s-fleet-lead")),
        WayUp.BringBackRow(
            "s-docs", "Docs site: Docs - Developer - the install page",
            "Brings back one session, reading its own handover.",
            true,
            WayUp.Seat("s-docs", "Docs - Developer - the install page", "Docs - Developer - the install page (Developer) comes back reading its handover.")));

    private static WayUpRecord WithAnEndedSeat() => WayUp.Record(
        "ws-2",
        "A restart is available",
        "Shut down on 19 September 2026 at 17:50.",
        "No reason was given.",
        2,
        "2 sessions are waiting to be brought back.",
        WayUp.BringBackRow(
            "s-billing-lead", "Billing: Billing - Delivery Lead - invoices",
            "Brings back 2 sessions, leads first, each reading its own handover.",
            true,
            WayUp.Seat("s-billing-lead", "Billing - Delivery Lead - invoices", "Billing - Delivery Lead - invoices (Delivery Lead) comes back reading its handover."),
            WayUp.Seat("s-billing-export", "Billing - Developer - the export", "Billing - Developer - the export (Developer) comes back reading its handover.", "s-billing-lead")),
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
            what: "No conversation was recorded for this session, so there is nothing to reopen. Start it again yourself when you are ready."));

    internal static WayUpOfferWindow Open(WayUpRecord record, FakeWayUp engine)
    {
        var window = new WayUpOfferWindow(new WayUpOfferViewModel(record, engine))
        {
            // The application runs the dark theme (App.axaml); the bare test application does not say.
            RequestedThemeVariant = ThemeVariant.Dark,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Every piece of text the opened window drew, in the order it drew it.</summary>
    internal static List<string> DrawnTexts(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text))
            .Select(t => t.Text!)
            .ToList();

    private static List<CheckBox> TickBoxes(Window window) =>
        window.GetVisualDescendants().OfType<CheckBox>().ToList();

    /// <summary>
    /// The buttons drawn INSIDE a row - the reopen buttons. A CheckBox is a ToggleButton and so is a
    /// Button, so the tick boxes are left out by type, and the window's own answers by their names.
    /// </summary>
    private static List<Button> RowButtons(Window window) =>
        window.GetVisualDescendants().OfType<Button>()
            .Where(b => b is not ToggleButton && b.IsEffectivelyVisible && b.Name is null)
            .ToList();

    // ===== The window opens =====

    /// <summary>
    /// THE TEST THE OLD WINDOW NEVER HAD. The window is opened and every named control is connected. A
    /// window that defines its own InitializeComponent skips the generated code that connects them: the
    /// constructor then meets a null BtnBringBack and this goes red.
    /// </summary>
    [AvaloniaFact]
    public void Show_GeneratedInitializeComponent_EveryNamedControlIsConnected()
    {
        var window = Open(ThreeMissions(), new FakeWayUp());

        Assert.True(window.IsVisible);
        Assert.NotNull(window.TxtHeadline);
        Assert.NotNull(window.TxtWhen);
        Assert.NotNull(window.TxtReason);
        Assert.NotNull(window.TxtSeatsOwed);
        Assert.NotNull(window.RowList);
        Assert.NotNull(window.ResultPanel);
        Assert.NotNull(window.TxtResult);
        Assert.NotNull(window.SeatResultList);
        Assert.NotNull(window.BtnBringBack);
        Assert.NotNull(window.BtnNotNow);
        Assert.NotNull(window.BtnClose);
    }

    // ===== The client is dumb =====

    /// <summary>
    /// THE RULE THIS WHOLE WINDOW EXISTS UNDER (critical rule 7 in CLAUDE.md). The engine is handed an
    /// answer whose every label DISAGREES with its own numbers - it says one session is waiting while
    /// carrying three rows and five seats, and it calls a bring back row a reopen - and the window still
    /// shows exactly the labels it was given. A window that counted anything for itself, or that chose a
    /// sentence from a state, would show something else here and this would go red.
    /// </summary>
    [AvaloniaFact]
    public void Show_LabelsThatDisagreeWithTheirOwnNumbers_TheWindowShowsTheLabelItWasGiven()
    {
        var record = WayUp.Record(
            "ws-liar",
            "Nothing at all is available",
            "Shut down at no time whatever.",
            "Reason: none of these sentences are true.",
            1,
            "One session is waiting to be brought back.",
            WayUp.BringBackRow(
                "r1", "A row that says it reopens a conversation",
                "This row brings back nothing at all.",
                true,
                WayUp.Seat("a", "a", "seat one says something about seat two"),
                WayUp.Seat("b", "b", "seat two says something about seat three"),
                WayUp.Seat("c", "c", "seat three says something about seat one")),
            WayUp.BringBackRow("r2", "A second row", "It says two sessions and names none.", true),
            WayUp.BringBackRow("r3", "A third row", "It says nothing at all.", true));

        var window = Open(record, new FakeWayUp());
        var drawn = DrawnTexts(window);

        // The count is the engine's sentence, though three rows and three seats are on screen.
        Assert.Equal("One session is waiting to be brought back.", window.TxtSeatsOwed.Text);
        Assert.Equal("Nothing at all is available", window.TxtHeadline.Text);
        Assert.Equal("Nothing at all is available", window.Title);
        Assert.Equal("Shut down at no time whatever.", window.TxtWhen.Text);
        Assert.Equal("Reason: none of these sentences are true.", window.TxtReason.Text);
        Assert.Contains("This row brings back nothing at all.", drawn);
        Assert.Contains("seat one says something about seat two", drawn);
        Assert.Contains("It says two sessions and names none.", drawn);

        // And the row that calls itself a reopen is still a bring back row, because the ENGINE said its
        // kind was BringBack. The title is a label, not a verdict.
        var tickBoxes = TickBoxes(window);
        Assert.Equal(3, tickBoxes.Count);
        Assert.All(tickBoxes, box => Assert.True(box.IsEnabled));
        Assert.Equal("A row that says it reopens a conversation", tickBoxes[0].Content);
    }

    // ===== What it shows =====

    [AvaloniaFact]
    public void Show_ThreeMissionHeads_ShowsTheEnginesHeadlineWhenReasonAndCount()
    {
        var window = Open(ThreeMissions(), new FakeWayUp());

        Assert.Equal("A restart is available", window.TxtHeadline.Text);
        Assert.Equal("Shut down on 19 September 2026 at 17:50.", window.TxtWhen.Text);
        Assert.Equal("Reason: updating the Director.", window.TxtReason.Text);
        Assert.Equal("6 sessions are waiting to be brought back.", window.TxtSeatsOwed.Text);
    }

    [AvaloniaFact]
    public void Show_ThreeMissionHeads_OneRowEachLeadsFirstAllTicked()
    {
        var window = Open(ThreeMissions(), new FakeWayUp());

        var tickBoxes = TickBoxes(window);
        Assert.Equal(3, tickBoxes.Count);
        Assert.All(tickBoxes, box => Assert.True(box.IsChecked));
        Assert.Equal(
            new[]
            {
                "Billing: Billing - Delivery Lead - invoices",
                "Fleet: Fleet - Tech Lead - the restart",
                "Docs site: Docs - Developer - the install page",
            },
            tickBoxes.Select(b => (string?)b.Content));
    }

    /// <summary>Each seat is drawn under its own row, in the order the engine put it, and the seat's
    /// own sentence is what is drawn - the engine's sentence already names the seat, so the name is not
    /// drawn twice.</summary>
    [AvaloniaFact]
    public void Show_SeatsUnderARow_AreDrawnInTheEnginesOrderWithTheEnginesSentence()
    {
        var window = Open(ThreeMissions(), new FakeWayUp());
        var drawn = DrawnTexts(window);

        var lead = drawn.IndexOf("Billing - Delivery Lead - invoices (Delivery Lead) comes back reading its handover.");
        Assert.True(lead >= 0, "the lead's own sentence is not drawn");
        Assert.Equal("Billing - Developer - the export (Developer) comes back reading its handover.", drawn[lead + 1]);
        Assert.Equal("Billing - Developer - the totals (Developer) comes back reading its handover.", drawn[lead + 2]);
        Assert.True(drawn.IndexOf("Brings back 3 sessions, leads first, each reading its own handover.") < lead);
    }

    // ===== The seat that ended without a handover =====

    /// <summary>
    /// Ruling 10.3. The seat that ended without a handover is its OWN row, beside the others, UNTICKED,
    /// and it carries its own button. The tick box is dead because the engine has already said in words
    /// that the row is not brought back with the rest - drawing that is rendering the sentence, not
    /// deciding it.
    /// </summary>
    [AvaloniaFact]
    public void Show_ASeatThatEndedWithoutAHandover_IsItsOwnUntickedRowBesideTheOthers()
    {
        var window = Open(WithAnEndedSeat(), new FakeWayUp());

        var tickBoxes = TickBoxes(window);
        Assert.Equal(3, tickBoxes.Count);

        Assert.True(tickBoxes[0].IsChecked);
        Assert.True(tickBoxes[0].IsEnabled);

        Assert.False(tickBoxes[1].IsChecked);
        Assert.False(tickBoxes[1].IsEnabled);
        Assert.Equal("Voice - Developer - the wake word - ended without a handover", tickBoxes[1].Content);

        Assert.False(tickBoxes[2].IsChecked);
        Assert.False(tickBoxes[2].IsEnabled);
    }

    /// <summary>The row that ended without a handover is coloured differently from one that brings
    /// sessions back. COLOUR, never a word (critical rule 7).</summary>
    [AvaloniaFact]
    public void Show_ASeatThatEndedWithoutAHandover_IsColouredApartFromTheRowsThatComeBack()
    {
        var window = Open(WithAnEndedSeat(), new FakeWayUp());
        var tickBoxes = TickBoxes(window);

        Assert.Equal(WayUpRowViewModel.BringBackBrush, tickBoxes[0].Foreground);
        Assert.Equal(WayUpRowViewModel.EndedWithoutHandoverBrush, tickBoxes[1].Foreground);
        Assert.NotEqual(tickBoxes[0].Foreground, tickBoxes[1].Foreground);
    }

    /// <summary>It carries its own button, worded by the engine, and the engine's sentence saying what
    /// pressing it would really do.</summary>
    [AvaloniaFact]
    public void Show_ASeatWithASavedConversation_HasItsOwnButtonInTheEnginesWords()
    {
        var window = Open(WithAnEndedSeat(), new FakeWayUp());

        var buttons = RowButtons(window);
        var reopen = Assert.Single(buttons);
        Assert.Equal("Reopen its saved conversation", reopen.Content);
        Assert.Contains(
            "Claude Code is started again on this session's saved conversation, in the same repository, and " +
            "told that it was stopped and must check the state of its work before acting.",
            DrawnTexts(window));
    }

    /// <summary>
    /// And a seat the engine says has NO conversation gets the engine's sentence saying so AND NO
    /// BUTTON. A button that could never work is the defect this rule exists to stop.
    /// </summary>
    [AvaloniaFact]
    public void Show_ASeatWithNoConversation_ShowsTheEnginesSentenceAndNoButton()
    {
        var window = Open(WithAnEndedSeat(), new FakeWayUp());

        Assert.Contains(
            "No conversation was recorded for this session, so there is nothing to reopen. Start it again " +
            "yourself when you are ready.",
            DrawnTexts(window));

        // One reopen button on screen, and it is the one belonging to the seat that CAN be reopened.
        var reopen = Assert.Single(RowButtons(window));
        Assert.Equal("Reopen its saved conversation", reopen.Content);
    }

    // ===== The two answers =====

    [AvaloniaFact]
    public void Show_TheTwoAnswers_AreBringBackAndNotNowWithBringBackTheDefault()
    {
        var window = Open(ThreeMissions(), new FakeWayUp());

        Assert.Equal("Bring back", window.BtnBringBack.Content);
        Assert.Equal("Not now", window.BtnNotNow.Content);
        Assert.True(window.BtnBringBack.IsVisible);
        Assert.True(window.BtnNotNow.IsVisible);
        Assert.False(window.BtnClose.IsVisible);
        Assert.True(window.BtnBringBack.IsDefault);
        Assert.True(window.BtnNotNow.IsCancel);
        Assert.True(window.BtnBringBack.IsFocused);
    }

    /// <summary>
    /// NOT NOW WRITES NOTHING. The window closes, the engine is never asked to bring anything back or to
    /// reopen anything, and the record is left exactly as it was so it is offered again next time.
    /// </summary>
    [AvaloniaFact]
    public void BtnNotNow_Clicked_ClosesAndAsksTheEngineForNothing()
    {
        var engine = new FakeWayUp();
        var window = Open(ThreeMissions(), engine);

        Click(window.BtnNotNow);

        Assert.False(window.IsVisible);
        Assert.False(window.BringBackAsked);
        Assert.Empty(engine.BringBackRequests);
        Assert.Empty(engine.ReopenRequests);
    }

    /// <summary>Escape is the same answer as Not now, and so is the window's own X - Close() is what
    /// that X does under the headless platform.</summary>
    [AvaloniaFact]
    public void EscapeKey_AndTheWindowsOwnClose_AreBothNotNow()
    {
        var engine = new FakeWayUp();
        var byEscape = Open(ThreeMissions(), engine);
        byEscape.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        var byX = Open(ThreeMissions(), engine);
        byX.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.False(byEscape.IsVisible);
        Assert.False(byEscape.BringBackAsked);
        Assert.False(byX.IsVisible);
        Assert.False(byX.BringBackAsked);
        Assert.Empty(engine.BringBackRequests);
    }

    // ===== Bring back =====

    /// <summary>
    /// The ticked rows, and only the ticked rows, are named to the engine - by the engine's own row ids,
    /// in the engine's order. The engine is asked OFF the interface thread, because it runs the whole
    /// restore before it answers.
    /// </summary>
    [AvaloniaFact]
    public async Task BringBack_TwoOfThreeTicked_NamesThoseTwoRowsOffTheInterfaceThread()
    {
        var engine = new FakeWayUp
        {
            BringBack = new WayUpBringBackResult(true, null, "5 sessions came back.",
            [
                new WayUpSeatResult("s-billing-lead", "Billing - Delivery Lead - invoices", "new-1", "Came back as new-1."),
                new WayUpSeatResult("s-docs", "Docs - Developer - the install page", null, "Did not come back: its repository is gone."),
            ]),
        };
        var window = Open(ThreeMissions(), engine);
        window.ViewModel.Rows[1].Ticked = false;

        await window.ViewModel.BringBackAsync();
        Dispatcher.UIThread.RunJobs();

        var request = Assert.Single(engine.BringBackRequests);
        Assert.Equal("ws-1", request.WorkspaceId);
        Assert.Equal(new[] { "s-billing-lead", "s-docs" }, request.TickedRowIds);
        Assert.False(engine.WasEverCalledOnTheInterfaceThread);
    }

    /// <summary>What came of it is the engine's own words, whole, with one line per seat - also the
    /// engine's - and the two answers are replaced by the one that closes the window.</summary>
    [AvaloniaFact]
    public async Task BringBack_Answered_ShowsTheEnginesMessageAndEverySeatsOwnLine()
    {
        var engine = new FakeWayUp
        {
            BringBack = new WayUpBringBackResult(true, null, "5 came back; 1 could not. Each one says why beside it.",
            [
                new WayUpSeatResult("s-billing-lead", "Billing - Delivery Lead - invoices", "new-1", "Came back as new-1."),
                new WayUpSeatResult("s-docs", "Docs - Developer - the install page", null, "Did not come back: its repository is gone."),
            ]),
        };
        var window = Open(ThreeMissions(), engine);

        await window.ViewModel.BringBackAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.ResultPanel.IsVisible);
        Assert.Equal("5 came back; 1 could not. Each one says why beside it.", window.TxtResult.Text);
        var drawn = DrawnTexts(window);
        Assert.Contains("Came back as new-1.", drawn);
        Assert.Contains("Did not come back: its repository is gone.", drawn);

        Assert.False(window.BtnBringBack.IsVisible);
        Assert.False(window.BtnNotNow.IsVisible);
        Assert.True(window.BtnClose.IsVisible);
        Assert.Equal("Close", window.BtnClose.Content);
    }

    /// <summary>
    /// NOTHING TICKED IS STILL SENT. The window does not pre-empt the engine's refusal with one of its
    /// own - a second way of saying the same thing is exactly what critical rule 7 forbids - so the
    /// engine is asked with an empty list and ITS refusal is what the owner reads.
    /// </summary>
    [AvaloniaFact]
    public async Task BringBack_NothingTicked_AsksTheEngineAnywayAndShowsTheEnginesRefusal()
    {
        var refusal = "Nothing was ticked, so nothing was brought back.";
        var engine = new FakeWayUp
        {
            BringBack = new WayUpBringBackResult(false, refusal, refusal, Array.Empty<WayUpSeatResult>()),
        };
        var window = Open(ThreeMissions(), engine);
        foreach (var row in window.ViewModel.Rows) row.Ticked = false;

        await window.ViewModel.BringBackAsync();
        Dispatcher.UIThread.RunJobs();

        var request = Assert.Single(engine.BringBackRequests);
        Assert.Empty(request.TickedRowIds);
        Assert.Equal(refusal, window.TxtResult.Text);
        Assert.False(window.SeatResultList.IsVisible);
    }

    /// <summary>A second press does nothing: the restore has already run and running it again would
    /// start a second copy of every session.</summary>
    [AvaloniaFact]
    public async Task BringBack_PressedAgainAfterAnAnswer_AsksTheEngineOnlyOnce()
    {
        var engine = new FakeWayUp();
        var window = Open(ThreeMissions(), engine);

        await window.ViewModel.BringBackAsync();
        await window.ViewModel.BringBackAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Single(engine.BringBackRequests);
    }

    /// <summary>The real button is wired to the real call: pressing it reaches the engine with this
    /// record's own workspace, and the window remembers that a bring back was asked for.</summary>
    [AvaloniaFact]
    public void BtnBringBack_Clicked_ReachesTheEngineWithThisRecord()
    {
        var engine = new FakeWayUp();
        var window = Open(ThreeMissions(), engine);

        Click(window.BtnBringBack);
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.BringBackAsked);
        var request = Assert.Single(engine.BringBackRequests);
        Assert.Equal("ws-1", request.WorkspaceId);
    }

    // ===== Reopen =====

    /// <summary>
    /// The reopen button reaches the engine with THAT SEAT's own id, off the interface thread, and what
    /// came of it is the engine's own sentence.
    /// </summary>
    [AvaloniaFact]
    public async Task Reopen_TheSeatThatEndedWithoutAHandover_NamesThatSeatAndShowsTheEnginesAnswer()
    {
        var engine = new FakeWayUp
        {
            Reopen = new WayUpReopenResult(true, "new-9",
                "Claude Code was started again on its saved conversation as new-9."),
        };
        var window = Open(WithAnEndedSeat(), engine);
        var endedRow = window.ViewModel.Rows.Single(r => r.RowId == "s-voice");

        await window.ViewModel.ReopenAsync(endedRow);
        Dispatcher.UIThread.RunJobs();

        var request = Assert.Single(engine.ReopenRequests);
        Assert.Equal("ws-2", request.WorkspaceId);
        Assert.Equal("s-voice", request.SeatSessionId);
        Assert.False(engine.WasEverCalledOnTheInterfaceThread);
        Assert.Equal("Claude Code was started again on its saved conversation as new-9.", window.TxtResult.Text);
        Assert.Empty(engine.BringBackRequests);
    }

    /// <summary>The real button is wired to the real call, and it carries its own row with it - nothing
    /// is looked up by index, so the second ended row could never be reopened by pressing the first's
    /// button.</summary>
    [AvaloniaFact]
    public void BtnReopen_Clicked_ReachesTheEngineWithItsOwnRowsSeat()
    {
        var engine = new FakeWayUp();
        var window = Open(WithAnEndedSeat(), engine);

        Click(Assert.Single(RowButtons(window)));
        Dispatcher.UIThread.RunJobs();

        var request = Assert.Single(engine.ReopenRequests);
        Assert.Equal("s-voice", request.SeatSessionId);
    }

    // ===== The view model refuses what the window must never show =====

    [Fact]
    public void Constructor_NoEngine_ThrowsRatherThanBuildAWindowThatCannotAnswer()
    {
        var record = WayUp.Record("ws", "h", "w", "r", 0, "s");

        Assert.Throws<ArgumentNullException>(() => new WayUpOfferViewModel(record, null!));
    }

    [Fact]
    public void Constructor_NoRecord_ThrowsRatherThanBuildAnEmptyOffer()
    {
        Assert.Throws<ArgumentNullException>(() => new WayUpOfferViewModel(null!, new FakeWayUp()));
    }
}
