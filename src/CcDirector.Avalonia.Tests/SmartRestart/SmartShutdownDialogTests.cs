using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CcDirector.Avalonia.SmartRestart;
using Xunit;

namespace CcDirector.Avalonia.Tests.SmartRestart;

/// <summary>
/// The Smart shutdown dialog, OPENED. The window it replaced (DrainDirectorDialog, since removed) threw on opening
/// in every shipped build because nothing ever opened it under test; every test here that touches the
/// window calls Show() first and reads what the real controls show, and every result comes from
/// driving the real buttons and keys, never from a result built by hand.
///
/// WHAT THESE DO NOT COVER. Nobody calls this window yet, so nothing here proves the File menu or the
/// close of the main window opens it. The window's own X cannot be pressed without a real window
/// frame; Close() is what that X does, and that is what is driven. The pictures are drawn by Skia
/// under the Fluent dark theme with the application's brushes, not by the running application.
/// </summary>
public class SmartShutdownDialogTests
{
    /// <summary>The folder the pictures are written to, when this variable names one.</summary>
    private const string ScreenshotFolderVariable = "SMART_RESTART_SCREENSHOT_DIR";

    private static List<SmartShutdownSession> Sessions(int working, int waiting, params string[] questionBoxNames)
    {
        var sessions = new List<SmartShutdownSession>();
        for (var i = 1; i <= working; i++)
            sessions.Add(new SmartShutdownSession($"working {i}", IsWorking: true, HasQuestionBoxOpen: false));
        for (var i = 1; i <= waiting - questionBoxNames.Length; i++)
            sessions.Add(new SmartShutdownSession($"waiting {i}", IsWorking: false, HasQuestionBoxOpen: false));
        foreach (var name in questionBoxNames)
            sessions.Add(new SmartShutdownSession(name, IsWorking: false, HasQuestionBoxOpen: true));
        return sessions;
    }

    private static SmartShutdownDialog Open(
        IReadOnlyList<SmartShutdownSession> sessions, SmartShutdownDoor door = SmartShutdownDoor.WindowClose)
    {
        var dialog = new SmartShutdownDialog(new SmartShutdownViewModel(sessions, door))
        {
            // The application runs the dark theme (App.axaml); the bare test application does not say.
            RequestedThemeVariant = ThemeVariant.Dark,
        };
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        return dialog;
    }

    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Every piece of text the opened window draws, in the order it draws it.</summary>
    private static List<string> DrawnTexts(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text))
            .Select(t => t.Text!)
            .ToList();

    // ===== The window opens =====

    /// <summary>
    /// THE TEST THE OLD WINDOW NEVER HAD. The window is opened, and every named control is connected.
    /// A window that defines its own InitializeComponent skips the generated code that connects them:
    /// the constructor then meets a null BtnSmart and this goes red (watched; see the report).
    /// </summary>
    [AvaloniaFact]
    public void Show_GeneratedInitializeComponent_EveryNamedControlIsConnected()
    {
        var dialog = Open(Sessions(working: 4, waiting: 5));

        Assert.True(dialog.IsVisible);
        Assert.NotNull(dialog.TxtSessionCount);
        Assert.NotNull(dialog.TxtWorkingWaiting);
        Assert.NotNull(dialog.QuestionBoxPanel);
        Assert.NotNull(dialog.TxtQuestionBoxHeading);
        Assert.NotNull(dialog.QuestionBoxList);
        Assert.NotNull(dialog.TxtExplanation);
        Assert.NotNull(dialog.TxtWhy);
        Assert.NotNull(dialog.CmbTimeAllowed);
        Assert.NotNull(dialog.TxtIgnoreExplanation);
        Assert.NotNull(dialog.BtnIgnore);
        Assert.NotNull(dialog.BtnSmart);
        Assert.NotNull(dialog.BtnCancel);
    }

    // ===== What it shows =====

    [AvaloniaFact]
    public void Show_NineSessionsNoQuestionBoxes_ShowsCountsAndNoQuestionSection()
    {
        var dialog = Open(Sessions(working: 4, waiting: 5));

        Assert.Equal("9 sessions are running", dialog.TxtSessionCount.Text);
        Assert.Equal("4 working, 5 waiting", dialog.TxtWorkingWaiting.Text);
        Assert.False(dialog.QuestionBoxPanel.IsVisible);
        Assert.DoesNotContain("Answer these first?", DrawnTexts(dialog));
    }

    [AvaloniaFact]
    public void Show_TwoQuestionBoxesOpen_ListsThemByNameUnderAnswerTheseFirst()
    {
        var dialog = Open(Sessions(working: 4, waiting: 5, "Docs site - Developer", "Billing - Tech Lead"));

        Assert.True(dialog.QuestionBoxPanel.IsVisible);
        var drawn = DrawnTexts(dialog);
        var heading = drawn.IndexOf("Answer these first?");
        Assert.True(heading >= 0, "the heading is not drawn");
        Assert.Equal("Docs site - Developer", drawn[heading + 1]);
        Assert.Equal("Billing - Tech Lead", drawn[heading + 2]);
        // The order the mandate sets: the count, then the review, then the explanation.
        Assert.True(drawn.IndexOf("9 sessions are running") < drawn.IndexOf("4 working, 5 waiting"));
        Assert.True(drawn.IndexOf("4 working, 5 waiting") < heading);
        Assert.True(heading < drawn.IndexOf(dialog.ViewModel.ExplanationText));
    }

    [AvaloniaFact]
    public void Show_OneSession_SaysOneSessionIsRunning()
    {
        var dialog = Open(Sessions(working: 0, waiting: 1));

        Assert.Equal("1 session is running", dialog.TxtSessionCount.Text);
        Assert.Equal("0 working, 1 waiting", dialog.TxtWorkingWaiting.Text);
    }

    [AvaloniaFact]
    public void Show_Explanation_SaysEverythingTheOwnerAskedItToSay()
    {
        var dialog = Open(Sessions(working: 4, waiting: 5));

        var explanation = dialog.TxtExplanation.Text!;
        Assert.Contains("shut down nicely", explanation);
        Assert.Contains("short handover of what it was doing and what is left", explanation);
        Assert.Contains("They get 10 minutes", explanation);
        Assert.Contains("whatever is still running after that is shut down for them", explanation);
        Assert.Contains("start the sessions again when the Director comes back", explanation);
        Assert.Contains("a handover compresses a session down to what is left to do", dialog.TxtWhy.Text);
        Assert.Equal("Shut down and ignore all sessions", dialog.BtnIgnore.Content);
        Assert.Equal("The sessions are ended at once and no handovers are written.", dialog.TxtIgnoreExplanation.Text);
    }

    // ===== The default and the time allowed =====

    [AvaloniaFact]
    public void Show_Defaults_SmartShutdownIsTheDefaultButtonWithTenMinutes()
    {
        var dialog = Open(Sessions(working: 4, waiting: 5));

        Assert.True(dialog.BtnSmart.IsDefault);
        Assert.False(dialog.BtnIgnore.IsDefault);
        Assert.False(dialog.BtnCancel.IsDefault);
        Assert.True(dialog.BtnSmart.IsFocused);
        var selected = Assert.IsType<SmartShutdownTimeOption>(dialog.CmbTimeAllowed.SelectedItem);
        Assert.Equal(10, selected.Minutes);
    }

    [AvaloniaFact]
    public void Show_TimeAllowedDropdown_HoldsExactlyFiveTenFifteenThirtySixty()
    {
        var dialog = Open(Sessions(working: 4, waiting: 5));

        var options = dialog.CmbTimeAllowed.Items.Cast<SmartShutdownTimeOption>().ToList();
        Assert.Equal(new[] { 5, 10, 15, 30, 60 }, options.Select(o => o.Minutes));
        Assert.Equal(
            new[] { "5 minutes", "10 minutes", "15 minutes", "30 minutes", "1 hour" },
            options.Select(o => o.Label));
    }

    [AvaloniaFact]
    public void TimeAllowed_ChangedInTheDropdown_TheExplanationFollows()
    {
        var dialog = Open(Sessions(working: 4, waiting: 5));

        dialog.CmbTimeAllowed.SelectedIndex = 3;
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("They get 30 minutes;", dialog.TxtExplanation.Text);
        Assert.DoesNotContain("10 minutes", dialog.TxtExplanation.Text);

        dialog.CmbTimeAllowed.SelectedIndex = 4;
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("They get 1 hour;", dialog.TxtExplanation.Text);
    }

    // ===== The three results =====

    [AvaloniaFact]
    public void BtnSmart_Clicked_GivesSmartShutdownWithTenMinutesAndCloses()
    {
        var dialog = Open(Sessions(working: 4, waiting: 5));

        Click(dialog.BtnSmart);

        Assert.Equal(SmartShutdownChoiceKind.SmartShutdown, dialog.Result.Choice);
        Assert.Equal(TimeSpan.FromMinutes(10), dialog.Result.TimeAllowed);
        Assert.False(dialog.IsVisible);
    }

    [AvaloniaFact]
    public void BtnSmart_ClickedAfterChoosingThirtyMinutes_GivesThirtyMinutes()
    {
        var dialog = Open(Sessions(working: 4, waiting: 5));
        dialog.CmbTimeAllowed.SelectedIndex = 3;
        Dispatcher.UIThread.RunJobs();

        Click(dialog.BtnSmart);

        Assert.Equal(SmartShutdownChoiceKind.SmartShutdown, dialog.Result.Choice);
        Assert.Equal(TimeSpan.FromMinutes(30), dialog.Result.TimeAllowed);
    }

    [AvaloniaFact]
    public void EnterKey_OnTheOpenedDialog_TakesSmartShutdown()
    {
        var dialog = Open(Sessions(working: 4, waiting: 5));

        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(SmartShutdownChoiceKind.SmartShutdown, dialog.Result.Choice);
        Assert.Equal(TimeSpan.FromMinutes(10), dialog.Result.TimeAllowed);
        Assert.False(dialog.IsVisible);
    }

    [AvaloniaFact]
    public void BtnIgnore_Clicked_GivesIgnoreAllSessionsWithNoTimeAndCloses()
    {
        var dialog = Open(Sessions(working: 4, waiting: 5));

        Click(dialog.BtnIgnore);

        Assert.Equal(SmartShutdownChoiceKind.IgnoreAllSessions, dialog.Result.Choice);
        Assert.Null(dialog.Result.TimeAllowed);
        Assert.False(dialog.IsVisible);
    }

    [AvaloniaFact]
    public void BtnCancel_Clicked_GivesCancelledAndCloses()
    {
        var dialog = Open(Sessions(working: 4, waiting: 5));

        Click(dialog.BtnCancel);

        Assert.Equal(SmartShutdownChoiceKind.Cancelled, dialog.Result.Choice);
        Assert.Null(dialog.Result.TimeAllowed);
        Assert.False(dialog.IsVisible);
    }

    [AvaloniaFact]
    public void EscapeKey_OnTheOpenedDialog_IsCancelled()
    {
        var dialog = Open(Sessions(working: 4, waiting: 5));

        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(SmartShutdownChoiceKind.Cancelled, dialog.Result.Choice);
        Assert.False(dialog.IsVisible);
    }

    /// <summary>
    /// The window's own X. There is no window frame to press under the headless platform; Close() is
    /// what the X does. The time allowed is changed first so that a result quietly built from the view
    /// model would show.
    /// </summary>
    [AvaloniaFact]
    public void WindowOwnClose_NoButtonPressed_IsCancelled()
    {
        var dialog = Open(Sessions(working: 4, waiting: 5));
        dialog.CmbTimeAllowed.SelectedIndex = 3;
        Dispatcher.UIThread.RunJobs();

        dialog.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(SmartShutdownChoiceKind.Cancelled, dialog.Result.Choice);
        Assert.Null(dialog.Result.TimeAllowed);
    }

    [AvaloniaFact]
    public async Task ShowForResultAsync_SmartShutdownClicked_ReturnsThatResultToTheCaller()
    {
        var owner = new Window();
        owner.Show();
        var dialog = new SmartShutdownDialog(
            new SmartShutdownViewModel(Sessions(working: 1, waiting: 1), SmartShutdownDoor.FileMenu));

        var pending = dialog.ShowForResultAsync(owner);
        Dispatcher.UIThread.RunJobs();
        Click(dialog.BtnSmart);
        var result = await pending;

        Assert.Equal(SmartShutdownChoiceKind.SmartShutdown, result.Choice);
        Assert.Equal(TimeSpan.FromMinutes(10), result.TimeAllowed);
    }

    // ===== The two doors =====

    [AvaloniaFact]
    public void Show_TheTwoDoors_DifferOnlyInTitleAndConfirmWords()
    {
        var sessions = Sessions(working: 4, waiting: 5, "Docs site - Developer");
        var fromClose = Open(sessions, SmartShutdownDoor.WindowClose);
        var fromMenu = Open(sessions, SmartShutdownDoor.FileMenu);

        Assert.Equal("Smart shutdown", fromClose.Title);
        Assert.Equal("Smart Restart", fromMenu.Title);
        Assert.Equal("Smart shutdown", fromClose.BtnSmart.Content);
        Assert.Equal("Smart Restart", fromMenu.BtnSmart.Content);

        var closeTexts = DrawnTexts(fromClose);
        var menuTexts = DrawnTexts(fromMenu);
        Assert.Equal(closeTexts.Count, menuTexts.Count);
        var differing = Enumerable.Range(0, closeTexts.Count).Where(i => closeTexts[i] != menuTexts[i]).ToList();
        var only = Assert.Single(differing);
        Assert.Equal("Smart shutdown", closeTexts[only]);
        Assert.Equal("Smart Restart", menuTexts[only]);
    }

    // ===== The view model refuses what the dialog must never show =====

    [Fact]
    public void Constructor_ZeroSessions_ThrowsRatherThanShowZeroSessions()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new SmartShutdownViewModel(new List<SmartShutdownSession>(), SmartShutdownDoor.WindowClose));

        Assert.Contains("never shown with no sessions running", ex.Message);
    }

    // ===== The pictures =====

    /// <summary>
    /// Draws the four pictures the mandate names and proves each one is a real drawing, not a blank
    /// frame: a blank frame is one flat colour, and a drawn dialog holds the dialog background, the
    /// panel background, the accent button and text. When SMART_RESTART_SCREENSHOT_DIR names a folder
    /// the pictures are also written there.
    /// </summary>
    [AvaloniaFact]
    public void CaptureRenderedFrame_TheFourPictures_AreDrawnNotBlank()
    {
        var folder = Environment.GetEnvironmentVariable(ScreenshotFolderVariable);

        var fromClose = Open(Sessions(working: 4, waiting: 5));
        Capture(fromClose, folder, "dialog-from-window-close-no-question-boxes.png");

        var fromMenu = Open(Sessions(working: 4, waiting: 5), SmartShutdownDoor.FileMenu);
        Capture(fromMenu, folder, "dialog-from-file-menu.png");

        var withQuestions = Open(Sessions(working: 4, waiting: 5, "Docs site - Developer", "Billing - Tech Lead"));
        Capture(withQuestions, folder, "dialog-two-question-boxes-open.png");

        var thirtyMinutes = Open(Sessions(working: 4, waiting: 5));
        thirtyMinutes.CmbTimeAllowed.SelectedIndex = 3;
        Capture(thirtyMinutes, folder, "dialog-time-allowed-30-minutes.png");
    }

    private static void Capture(Window window, string? folder, string fileName)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);

        var colours = DistinctColours(frame!);
        Assert.True(colours.Contains(0xFF252526), $"{fileName}: the dialog background #252526 is not in the picture");
        Assert.True(colours.Contains(0xFF1E1E1E), $"{fileName}: the panel background #1E1E1E is not in the picture");
        Assert.True(colours.Contains(0xFF007ACC), $"{fileName}: the accent button #007ACC is not in the picture");
        Assert.True(colours.Count > 50, $"{fileName}: only {colours.Count} colours, so no text was drawn");

        if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder);
        frame!.Save(Path.Combine(folder, fileName));
    }

    private static HashSet<uint> DistinctColours(WriteableBitmap bitmap)
    {
        using var fb = bitmap.Lock();
        var total = fb.RowBytes * fb.Size.Height;
        var buffer = new byte[total];
        Marshal.Copy(fb.Address, buffer, 0, total);

        // The frame is BGRA or RGBA depending on the platform; read either, refuse anything else.
        var blueFirst = fb.Format == global::Avalonia.Platform.PixelFormat.Bgra8888;
        Assert.True(blueFirst || fb.Format == global::Avalonia.Platform.PixelFormat.Rgba8888, $"unexpected pixel format {fb.Format}");

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
