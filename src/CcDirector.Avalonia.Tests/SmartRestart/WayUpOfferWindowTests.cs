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

    /// <summary>
    /// THE RECORD THE OPERATING SYSTEM SHUTDOWN WRITES, as the engine now words it (ruling 10.5 and the
    /// mission's <c>ruling-way-up-presence-check.md</c>): nothing handed over, so no bring back row at all -
    /// only seats that ended without a handover, each with its own button.
    /// </summary>
    private static WayUpRecord AllEndedWithoutAHandover() => WayUp.Record(
        "ws-3",
        "A restart is available",
        "Shut down on 19 September 2026 at 17:50.",
        "Reason: the machine was shutting down.",
        0,
        "No session handed over, so there is nothing to bring back.",
        WayUp.EndedRow(
            "s-voice", "Voice - Developer - the wake word - ended without a handover",
            "It was still running when time ran out and was shut down for it. It is not brought back with the rest, because there is no handover for it to read.",
            canReopen: true,
            offer: "Reopen its saved conversation",
            what: "Claude Code is started again on this session's saved conversation, in the same repository, and told that it was stopped and must check the state of its work before acting."),
        WayUp.EndedRow(
            "s-fleet", "Fleet - Tech Lead - the restart - ended without a handover",
            "It never answered, so nothing was written for it. It is not brought back with the rest, because there is no handover for it to read.",
            canReopen: true,
            offer: "Reopen its saved conversation",
            what: "Claude Code is started again on this session's saved conversation, in the same repository, and told that it was stopped and must check the state of its work before acting."));

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

    /// <summary>
    /// OPEN THE SECTION THE SESSIONS THAT ENDED WITHOUT A HANDOVER SIT BEHIND, through the real disclosure
    /// control and its real two-way binding - not by setting the view model. A test that set the view model
    /// would pass with the control wired to nothing.
    /// </summary>
    internal static void OpenTheEndedSection(WayUpOfferWindow window)
    {
        window.EndedToggle.IsChecked = true;
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
        Assert.NotNull(window.TxtSeats);
        Assert.NotNull(window.RowList);
        Assert.NotNull(window.EndedSection);
        Assert.NotNull(window.EndedToggle);
        Assert.NotNull(window.TxtEndedSection);
        Assert.NotNull(window.TxtEndedCaret);
        Assert.NotNull(window.TxtEndedDetail);
        Assert.NotNull(window.EndedRowList);
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
        Assert.Equal("One session is waiting to be brought back.", window.TxtSeats.Text);
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
        Assert.Equal("6 sessions are waiting to be brought back.", window.TxtSeats.Text);
    }

    /// <summary>
    /// A RECORD WITH NOTHING TO BRING BACK STILL READS AS AN OFFER. The machine shut the Director down, so
    /// every session ended without a handover; the window shows the engine's line naming both counts, one
    /// row per seat, EVERY ONE UNTICKED, and a reopen button beside each. This is ruling 10.3 on the record
    /// ruling 10.5 describes.
    /// </summary>
    [AvaloniaFact]
    public void Show_EverySeatEndedWithoutAHandover_RowsAreUntickedAndEachCarriesItsButton()
    {
        var window = Open(AllEndedWithoutAHandover(), new FakeWayUp());

        // NOT OPENED BY THIS TEST. The engine said the section starts open, because it is the whole window.
        Assert.Equal("No session handed over, so there is nothing to bring back.", window.TxtSeats.Text);
        Assert.Equal("2 sessions ended without a handover.", window.TxtEndedSection.Text);
        Assert.True(window.EndedRowList.IsEffectivelyVisible);

        var tickBoxes = TickBoxes(window);
        Assert.Equal(2, tickBoxes.Count);
        Assert.All(tickBoxes, box => Assert.False(box.IsChecked));
        Assert.Equal(
            new[]
            {
                "Voice - Developer - the wake word - ended without a handover",
                "Fleet - Tech Lead - the restart - ended without a handover",
            },
            tickBoxes.Select(b => (string?)b.Content));

        var buttons = RowButtons(window);
        Assert.Equal(2, buttons.Count);
        Assert.All(buttons, b => Assert.Equal("Reopen its saved conversation", b.Content));
    }

    /// <summary>
    /// NOTHING COMES BACK BY ITSELF FROM SUCH A RECORD. The window is shown, and until somebody presses
    /// something the engine is asked for nothing at all - no bring back, no reopen.
    /// </summary>
    [AvaloniaFact]
    public void Show_EverySeatEndedWithoutAHandover_NothingIsAskedOfTheEngine()
    {
        var engine = new FakeWayUp();

        Open(AllEndedWithoutAHandover(), engine);

        Assert.Empty(engine.BringBackRequests);
        Assert.Empty(engine.ReopenRequests);
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
        OpenTheEndedSection(window);

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
        OpenTheEndedSection(window);
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
        OpenTheEndedSection(window);

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
        OpenTheEndedSection(window);

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

    /// <summary>
    /// The real button is wired to the real call: pressing it reaches the engine with this record's own
    /// workspace, and the window remembers that a bring back was asked for.
    ///
    /// IT WAITS FOR THE WORK THE PRESS STARTED, and that is the whole difference between this test and a
    /// flaky one. A press hands its work to a thread pool thread; draining the interface thread's queue
    /// runs what is already queued there and never waits for the pool.
    /// </summary>
    [AvaloniaFact]
    public async Task BtnBringBack_Clicked_ReachesTheEngineWithThisRecord()
    {
        var engine = new FakeWayUp();
        var window = Open(ThreeMissions(), engine);

        Click(window.BtnBringBack);
        await window.WorkTheLastPressStarted;

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

    /// <summary>
    /// The real button is wired to the real call, and it carries its own row with it - nothing is looked
    /// up by index, so the second ended row could never be reopened by pressing the first's button.
    ///
    /// IT WAITS FOR THE WORK THE PRESS STARTED. This test used to assert straight after the press, which
    /// raced the thread pool the press had just handed its work to: it won by a few microseconds on an
    /// idle machine and lost about one whole-project run in ten. Under a starved thread pool the engine
    /// had been asked ZERO times at the instant the press returned, which is that race stopped still.
    /// </summary>
    [AvaloniaFact]
    public async Task BtnReopen_Clicked_ReachesTheEngineWithItsOwnRowsSeat()
    {
        var engine = new FakeWayUp();
        var window = Open(WithAnEndedSeat(), engine);
        OpenTheEndedSection(window);

        Click(Assert.Single(RowButtons(window)));
        await window.WorkTheLastPressStarted;

        var request = Assert.Single(engine.ReopenRequests);
        Assert.Equal("s-voice", request.SeatSessionId);
    }

    // ===== The screen reads correctly: what the owner could not read on 20 September 2026 =====

    /// <summary>
    /// THE MAIN LIST HOLDS ONLY WHAT CAN BE BROUGHT BACK, AND THE LINE ABOVE IT COUNTS THOSE ROWS.
    ///
    /// This is the owner's own complaint, turned into a test. He saw "One session is waiting to be brought
    /// back" above SEVEN rows, six of them greyed, and could not read it: "why are there multiple sessions
    /// ... It's really confusing with all of those on the screen." Here the record holds one bring back row
    /// and two that ended without a handover, and what is on screen under the count is the one row.
    /// </summary>
    [AvaloniaFact]
    public void Show_ARecordHoldingBoth_TheMainListHoldsOnlyTheRowsThatComeBack()
    {
        var window = Open(WithAnEndedSeat(), new FakeWayUp());

        var mainList = Assert.IsType<ItemsControl>(window.RowList);
        Assert.True(mainList.IsEffectivelyVisible);
        Assert.Equal(
            new[] { "s-billing-lead" },
            window.ViewModel.BringBackRows.Select(r => r.RowId).ToArray());

        // The rows that ended without a handover are NOT in it, and are not drawn at all until asked for.
        Assert.False(window.EndedRowList.IsEffectivelyVisible);
        Assert.Empty(RowButtons(window));
        Assert.DoesNotContain(
            "Voice - Developer - the wake word - ended without a handover",
            DrawnTexts(window));

        // And they are behind one line that says how many there are, with the shut caret beside it.
        Assert.True(window.EndedSection.IsEffectivelyVisible);
        Assert.Equal("2 sessions ended without a handover.", window.TxtEndedSection.Text);
        Assert.Equal(WayUpOfferViewModel.ShutCaret, window.TxtEndedCaret.Text);
    }

    /// <summary>
    /// OPENING THAT LINE BRINGS THEM BACK ON SCREEN, WHOLE. Ruling 10.3 is not weakened by moving them: each
    /// is still listed, still unticked, and still carries its own reopen button. The caret turns over too, so
    /// the header says which way it is.
    /// </summary>
    [AvaloniaFact]
    public void Show_OpeningTheEndedSection_RevealsEveryRowUntickedWithItsOwnButton()
    {
        var window = Open(WithAnEndedSeat(), new FakeWayUp());

        OpenTheEndedSection(window);

        Assert.True(window.EndedRowList.IsEffectivelyVisible);
        Assert.Equal(WayUpOfferViewModel.OpenCaret, window.TxtEndedCaret.Text);
        Assert.Contains(
            "Voice - Developer - the wake word - ended without a handover",
            TickBoxes(window).Select(b => (string?)b.Content));
        Assert.All(
            TickBoxes(window).Where(b => b.Content is string c && c.EndsWith("ended without a handover")),
            box =>
            {
                Assert.False(box.IsChecked);
                Assert.False(box.IsEnabled);
            });
        Assert.Single(RowButtons(window));

        // And shutting it again hides them, without changing a single word.
        window.EndedToggle.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.EndedRowList.IsEffectivelyVisible);
        Assert.Equal("2 sessions ended without a handover.", window.TxtEndedSection.Text);
    }

    /// <summary>
    /// WHEN THE SHUTDOWN WAS, WHERE THE EYE LANDS. The date and time were on the window before and the owner
    /// read it without seeing them - "we're totally missing a date and time when that was saved" - because
    /// they were secondary grey on the third line. They are now primary text directly under the headline, and
    /// this pins that: a change back to a dimmer colour or a smaller size turns it red.
    /// </summary>
    [AvaloniaFact]
    public void Show_TheDateAndTime_AreDrawnInPrimaryTextRightUnderTheHeadline()
    {
        var window = Open(ThreeMissions(), new FakeWayUp());

        Assert.Equal("Shut down on 19 September 2026 at 17:50.", window.TxtWhen.Text);
        Assert.Equal(Color.Parse("#CCCCCC"), Assert.IsType<ISolidColorBrush>(window.TxtWhen.Foreground, exactMatch: false).Color);
        Assert.True(window.TxtWhen.FontSize >= 14);
        Assert.Equal(FontWeight.SemiBold, window.TxtWhen.FontWeight);

        // Directly under the headline: nothing the window draws comes between them.
        var drawn = DrawnTexts(window);
        Assert.Equal(drawn.IndexOf("A restart is available") + 1, drawn.IndexOf("Shut down on 19 September 2026 at 17:50."));
    }

    /// <summary>
    /// A SHUTDOWN WITH NO REASON DRAWS NO REASON LINE. The engine answers null, and the window shows nothing
    /// at all rather than "No reason was given." - a line that tells the reader what he already knows, on the
    /// window he called confusing.
    /// </summary>
    [AvaloniaFact]
    public void Show_ARecordWithNoReason_DrawsNoReasonLineAtAll()
    {
        var window = Open(
            WayUp.Record(
                "ws-no-reason",
                "A restart is available",
                "Shut down on 19 September 2026 at 17:50.",
                null,
                1,
                "One session is waiting to be brought back.",
                WayUp.BringBackRow("s-solo", "Docs: the install page", "Brings back one session.")),
            new FakeWayUp());

        Assert.False(window.TxtReason.IsEffectivelyVisible);
        Assert.DoesNotContain("No reason was given.", DrawnTexts(window));

        // The reason line was the only thing between the date and the count; the count follows it directly.
        var drawn = DrawnTexts(window);
        Assert.Equal(
            drawn.IndexOf("Shut down on 19 September 2026 at 17:50.") + 1,
            drawn.IndexOf("One session is waiting to be brought back."));
    }

    /// <summary>
    /// A RECORD WITH NOTHING TO BRING BACK DRAWS NO BRING BACK ANSWER, and the keyboard goes to the answer
    /// there is. The only thing that button could ever do on such a record is collect the engine's refusal
    /// "no row was ticked", and a button whose every press is a refusal reads as broken.
    /// </summary>
    [AvaloniaFact]
    public void Show_ARecordWithNothingToBringBack_DrawsNoBringBackAnswer()
    {
        var window = Open(AllEndedWithoutAHandover(), new FakeWayUp());

        Assert.False(window.BtnBringBack.IsEffectivelyVisible);
        Assert.True(window.BtnNotNow.IsEffectivelyVisible);
        Assert.False(window.ViewModel.ShowBringBack);
        Assert.True(window.ViewModel.ShowAnswers);

        // And the record that HAS something to bring back still draws it.
        var withSomething = Open(ThreeMissions(), new FakeWayUp());
        Assert.True(withSomething.BtnBringBack.IsEffectivelyVisible);
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
