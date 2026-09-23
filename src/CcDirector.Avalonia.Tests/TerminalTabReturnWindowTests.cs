using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Leaving the Terminal tab and coming back, through the REAL <see cref="MainWindow"/> and its real tab buttons,
/// which really hide and show TerminalPanel (relief plan step 8a, and the review of pull request 3336).
///
/// Coming back at the same size must keep the parser (no two megabyte replay), still do the cheap half of the old
/// refresh - catch up with the buffer, the same-size resize, a repaint - and show what the session wrote while
/// hidden, including a synchronized-output frame. Coming back at a different size must replay. A session switched
/// to while the tab was hidden must be the one shown on return.
/// </summary>
public sealed class TerminalTabReturnWindowTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "ccd-tab-return-" + Guid.NewGuid().ToString("N")[..8]);

    public TerminalTabReturnWindowTests()
    {
        // A .git folder makes the window show the Source Control tab button, the real way to leave the terminal.
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch { /* scratch dir; best effort */ }
    }

    private sealed class RecordingBackend : ISessionBackend
    {
        public List<(short Cols, short Rows)> Resizes { get; } = new();
        public int ProcessId => 1234;
        public string Status => "Test";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer { get; } = new(256 * 1024);
#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067
        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) => Resizes.Add((cols, rows));
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }

    private SessionViewModel MakeSession(RecordingBackend backend, string firstLine)
    {
        var session = new Session(Guid.NewGuid(), _repo, _repo, null, backend, SessionBackendType.ConPty);
        session.IsBrandNew = false;
        session.CustomName = firstLine;
        backend.Buffer!.Write(Encoding.UTF8.GetBytes(firstLine + "\r\n"));
        return new SessionViewModel(session);
    }

    private static void Write(RecordingBackend backend, string text) =>
        backend.Buffer!.Write(Encoding.UTF8.GetBytes(text));

    private static MainWindow ShownWindow()
    {
        var window = new MainWindow { SkipApplicationWiringOnLoaded = true, Width = 1200, Height = 800 };
        window.RememberExpandedCrews = _ => { };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        // The return check is posted at Loaded priority, after layout: run it.
        Dispatcher.UIThread.RunJobs();
    }

    private static string ScreenText(MainWindow window) => window.TerminalHost.GetAllTerminalText();

    /// <summary>Pump the screen thread until the terminal's pool-thread replay has handed over; fail after 60 seconds.</summary>
    private static void WaitForReplayHandover(MainWindow window)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (window.TerminalHost.HarnessReplayPending)
        {
            Dispatcher.UIThread.RunJobs();
            if (waited.Elapsed > TimeSpan.FromSeconds(60))
                throw new TimeoutException("the terminal replay never handed over");
            Thread.Sleep(2);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static int Occurrences(string text, string needle)
    {
        int count = 0;
        for (int at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + 1, StringComparison.Ordinal))
            count++;
        return count;
    }

    [AvaloniaFact]
    public void SameSizeReturn_KeepsTheParser_SendsTheSameSizeResize_AndShowsWhatArrivedWhileHidden()
    {
        var window = ShownWindow();
        var backend = new RecordingBackend();
        var vm = MakeSession(backend, "FIRST-LINE");
        window._sessions.Add(vm);
        window.SelectSession(vm);
        Dispatcher.UIThread.RunJobs();
        // The attach replays on a pool thread; the parser to compare against exists only once it hands over.
        WaitForReplayHandover(window);
        Assert.True(window.SourceControlTabButton.IsVisible, "the repository has a .git folder, so its tab shows");

        var parserBefore = window.TerminalHost.HarnessParser;
        var size = window.TerminalHost.GridSize;
        int resizesBefore = backend.Resizes.Count;
        Assert.NotNull(parserBefore);

        Click(window.SourceControlTabButton);
        Assert.False(window.TerminalPanel.IsVisible, "the Source Control tab hides the terminal panel");

        // Output while hidden: a plain line, a complete synchronized-output frame, and a frame left open.
        Write(backend, "WHILE-HIDDEN\r\n");
        Write(backend, "\x1b[?2026hSYNC-FRAME\r\n\x1b[?2026l");
        Write(backend, "\x1b[?2026hOPEN-FRAME\r\n");

        int rendersBefore = window.TerminalHost.HarnessRenderCount;
        int resizeRequestsBefore = window.TerminalHost.HarnessResizeRequests;
        Click(window.TerminalTabButton);
        Assert.True(window.TerminalPanel.IsVisible);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.CaptureRenderedFrame();

        Assert.Equal(size, window.TerminalHost.GridSize);
        Assert.Same(parserBefore, window.TerminalHost.HarnessParser);
        Assert.True(window.TerminalHost.HarnessRenderCount > rendersBefore, "the return must repaint the terminal");

        string text = ScreenText(window);
        Assert.Equal(1, Occurrences(text, "FIRST-LINE"));
        Assert.Equal(1, Occurrences(text, "WHILE-HIDDEN"));
        Assert.Equal(1, Occurrences(text, "SYNC-FRAME"));
        Assert.Equal(1, Occurrences(text, "OPEN-FRAME"));
        Assert.True(parserBefore!.InSynchronizedUpdate, "the open frame is still open until the program closes it");

        // The program closes the frame after the return; the next poll picks it up, once.
        Write(backend, "\x1b[?2026l");
        window.TerminalHost.HarnessPollTick();
        Assert.False(parserBefore.InSynchronizedUpdate);
        Assert.Equal(1, Occurrences(ScreenText(window), "OPEN-FRAME"));

        // The same-size resize is requested: the control asked its session for exactly one resize on the return,
        // at the unchanged size. Counted where the call leaves the control, because Session.Resize drops a resize
        // to the size the session already has (a guard since 2c12a04b6, May 2026), so nothing reaches the program
        // and the backend alone could not tell the request from its absence. Both ends are asserted so a change to
        // either one shows.
        Assert.Equal(resizeRequestsBefore + 1, window.TerminalHost.HarnessResizeRequests);
        Assert.Equal(((short)size.Cols, (short)size.Rows), window.TerminalHost.HarnessLastResizeRequest);
        Assert.Equal(resizesBefore, backend.Resizes.Count);
    }

    [AvaloniaFact]
    public void ChangedSizeReturn_ReplaysTheBuffer_AndResizesTheProgram()
    {
        var window = ShownWindow();
        var backend = new RecordingBackend();
        var vm = MakeSession(backend, "FIRST-LINE");
        window._sessions.Add(vm);
        window.SelectSession(vm);
        Dispatcher.UIThread.RunJobs();
        // The attach replays on a pool thread; the parser to compare against exists only once it hands over.
        WaitForReplayHandover(window);

        var parserBefore = window.TerminalHost.HarnessParser;
        Assert.NotNull(parserBefore);
        var sizeBefore = window.TerminalHost.GridSize;

        Click(window.SourceControlTabButton);
        window.Width = 900;
        window.Height = 600;
        Dispatcher.UIThread.RunJobs();
        Write(backend, "WHILE-HIDDEN\r\n");

        // The replay runs on a pool thread (pull request 3341). Hold it there, so what the terminal shows while a
        // replay is in flight can be checked without racing a small buffer's replay to its handover.
        using var releaseReplay = new ManualResetEventSlim(false);
        window.TerminalHost.HarnessBeforeReplay = _ =>
        {
            if (!releaseReplay.Wait(TimeSpan.FromSeconds(60)))
                throw new TimeoutException("the test never released the replay");
        };
        try
        {
            Click(window.TerminalTabButton);

            // Until the replay hands over, the terminal keeps showing the session it already had, not a blank grid.
            Assert.True(window.TerminalHost.HarnessReplayPending, "a return at a changed size must start a replay");
            Assert.Same(parserBefore, window.TerminalHost.HarnessParser);
            Assert.Equal(1, Occurrences(ScreenText(window), "FIRST-LINE"));
        }
        finally
        {
            releaseReplay.Set();
        }

        // Wait positively for the handover; this throws if the replay never lands.
        WaitForReplayHandover(window);

        Assert.NotEqual(sizeBefore, window.TerminalHost.GridSize);
        Assert.NotNull(window.TerminalHost.HarnessParser);
        Assert.NotSame(parserBefore, window.TerminalHost.HarnessParser);
        var now = window.TerminalHost.GridSize;
        Assert.Contains(((short)now.Cols, (short)now.Rows), backend.Resizes);

        string text = ScreenText(window);
        Assert.Equal(1, Occurrences(text, "FIRST-LINE"));
        Assert.Equal(1, Occurrences(text, "WHILE-HIDDEN"));
    }

    [AvaloniaFact]
    public void SessionSwitchedWhileHidden_IsTheSessionShownOnReturn()
    {
        var window = ShownWindow();
        var firstBackend = new RecordingBackend();
        var first = MakeSession(firstBackend, "SESSION-ONE");
        var secondBackend = new RecordingBackend();
        var second = MakeSession(secondBackend, "SESSION-TWO");
        // The second session was last on its Source Control tab, so selecting it keeps the terminal hidden.
        second.Session.SelectedTabName = "SourceControl";
        window._sessions.Add(first);
        window._sessions.Add(second);
        window.SelectSession(first);
        Dispatcher.UIThread.RunJobs();
        WaitForReplayHandover(window);
        Assert.NotNull(window.TerminalHost.HarnessParser);

        Click(window.SourceControlTabButton);
        window.SelectSession(second);
        Dispatcher.UIThread.RunJobs();
        // The switch attaches the second session, and its replay also runs on a pool thread.
        WaitForReplayHandover(window);
        Assert.False(window.TerminalPanel.IsVisible, "still on the Source Control tab after the switch");

        Write(secondBackend, "TWO-WHILE-HIDDEN\r\n");
        Write(firstBackend, "ONE-WHILE-HIDDEN\r\n");

        Click(window.TerminalTabButton);
        // If the switch's attach waited for layout while the panel was hidden, its replay starts only now.
        WaitForReplayHandover(window);
        Assert.NotNull(window.TerminalHost.HarnessParser);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.CaptureRenderedFrame();

        string text = ScreenText(window);
        Assert.Equal(1, Occurrences(text, "SESSION-TWO"));
        Assert.Equal(1, Occurrences(text, "TWO-WHILE-HIDDEN"));
        Assert.DoesNotContain("SESSION-ONE", text);
        Assert.DoesNotContain("ONE-WHILE-HIDDEN", text);
    }
}
