using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Terminal.Core;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Terminal.Avalonia.Tests;

/// <summary>
/// A session switch replays the terminal buffer OFF the screen thread (relief plan step 8b). Attach returns at
/// once with an empty grid, the replay runs on a pool thread, and the screen thread swaps the result in and parses
/// exactly the bytes written while it ran. Every test here drives the real <see cref="TerminalControl.Attach"/>
/// against a real <see cref="Session"/> and its real ring buffer.
/// </summary>
public sealed class TerminalAttachReplayTests
{
    private readonly ITestOutputHelper _output;

    public TerminalAttachReplayTests(ITestOutputHelper output) => _output = output;

    private sealed class BufferBackend : ISessionBackend
    {
        public int ProcessId => 1;
        public string Status => "Test";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer { get; } = new();
#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067
        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }

    private static Session NewSession(BufferBackend backend)
    {
        var session = new Session(Guid.NewGuid(), @"C:\test\repo", @"C:\test\repo", null, backend, "claude-test",
            ActivityState.Idle, DateTimeOffset.UtcNow, null, null);
        session.MarkRunning();
        return session;
    }

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>
    /// A session that has run all day: well over the two megabyte ring, so the ring has wrapped, with coloured
    /// output and two recorded resizes (issue #1304) inside what the ring still holds.
    /// </summary>
    private static Session WrappedSession(string tag)
    {
        var backend = new BufferBackend();
        var buffer = backend.Buffer!;
        buffer.RecordResize(100, 30);
        int line = 0;
        void WriteLines(int count)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < count; i++, line++)
                sb.Append($"{tag} {line:D7} \x1b[32mgreen\x1b[0m \x1b[1mbold\x1b[0m the quick brown fox jumps over the lazy dog\r\n");
            buffer.Write(Bytes(sb.ToString()));
        }
        WriteLines(20_000);
        buffer.RecordResize(90, 25);
        WriteLines(10_000);
        buffer.RecordResize(110, 35);
        WriteLines(10_000);
        Assert.True(buffer.TotalBytesWritten > 2 * 1024 * 1024, $"the ring must have wrapped, wrote {buffer.TotalBytesWritten}");
        return NewSession(backend);
    }

    private static (TerminalControl Terminal, Window Window) ShownTerminal()
    {
        var terminal = new TerminalControl();
        var window = new Window { Width = 800, Height = 600, Content = terminal };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (terminal, window);
    }

    /// <summary>Pump the screen thread until the replay has handed over.</summary>
    private static void WaitForHandover(TerminalControl terminal)
    {
        var deadline = Stopwatch.StartNew();
        while (terminal.HarnessReplayPending)
        {
            Dispatcher.UIThread.RunJobs();
            if (deadline.Elapsed > TimeSpan.FromSeconds(60))
                throw new TimeoutException("the replay never handed over");
            Thread.Sleep(2);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Run the real poll tick a few times so any double parse would show. The headless platform does not fire the
    /// control's poll timer on its own, so the tick handler is called directly.
    /// </summary>
    private static void PumpPolls(TerminalControl terminal)
    {
        for (int i = 0; i < 3; i++)
        {
            terminal.HarnessPollOnce();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// What the synchronous replay produced before this change, computed the way RebuildFromBuffer did it, in the
    /// same text form <see cref="TerminalControl.GetAllTerminalText"/> uses.
    /// </summary>
    private static string SynchronousReplayText(CircularTerminalBuffer buffer, int cols, int rows)
    {
        var (data, newPos) = buffer.GetWrittenSince(0);
        long dataStart = newPos - data.Length;
        var (startCols, startRows, marks) = buffer.GetResizeMarksSince(dataStart);
        int firstCols = startCols > 0 ? startCols : cols;
        int firstRows = startRows > 0 ? startRows : rows;
        var resizes = marks.Select(m => new ReplayResize((int)(m.Position - dataStart), m.Cols, m.Rows)).ToList();
        var scrollback = new List<TerminalCell[]>();
        var (_, parser) = SegmentedReplay.Replay(data, firstCols, firstRows, resizes, cols, rows, scrollback, 1000);

        var sb = new StringBuilder();
        foreach (var line in scrollback)
            sb.AppendLine(new string(line.Select(c => c.Character == '\0' ? ' ' : c.Character).ToArray()).TrimEnd());
        var cells = parser.ActiveCells;
        for (int row = 0; row < rows; row++)
        {
            var lineBuilder = new StringBuilder();
            for (int col = 0; col < cols; col++)
            {
                char ch = cells[col, row].Character;
                lineBuilder.Append(ch == '\0' ? ' ' : ch);
            }
            sb.AppendLine(lineBuilder.ToString().TrimEnd());
        }
        return sb.ToString();
    }

    private static int Occurrences(string text, string needle)
    {
        int count = 0;
        for (int at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + 1, StringComparison.Ordinal))
            count++;
        return count;
    }

    [AvaloniaFact]
    public void Attach_WrappedTwoMegabyteBuffer_ReturnsInUnder50Ms_AndTheHandoverGridEqualsASynchronousReplay()
    {
        var (terminal, _) = ShownTerminal();

        // Warm the path once so the timing below measures the attach, not first-call compilation.
        terminal.Attach(NewSession(new BufferBackend()));
        WaitForHandover(terminal);

        var session = WrappedSession("A");
        var stopwatch = Stopwatch.StartNew();
        terminal.Attach(session);
        stopwatch.Stop();
        _output.WriteLine($"Attach returned after {stopwatch.Elapsed.TotalMilliseconds:F1} ms on a {session.Buffer!.TotalBytesWritten} byte history");

        Assert.True(stopwatch.ElapsedMilliseconds < 50,
            $"Attach held the screen thread for {stopwatch.Elapsed.TotalMilliseconds:F0} ms on a wrapped two megabyte buffer");
        Assert.True(terminal.HarnessReplayPending, "the replay must still be running when Attach returns");
        Assert.Null(terminal.HarnessParser);

        var waited = Stopwatch.StartNew();
        WaitForHandover(terminal);
        _output.WriteLine($"Handover after a further {waited.Elapsed.TotalMilliseconds:F0} ms");

        Assert.NotNull(terminal.HarnessParser);
        string expected = SynchronousReplayText(session.Buffer!, terminal.HarnessCols, terminal.HarnessRows);
        Assert.Equal(expected, terminal.GetAllTerminalText());
    }

    [AvaloniaFact]
    public void Attach_BytesWrittenDuringTheReplay_AppearExactlyOnce()
    {
        var (terminal, _) = ShownTerminal();
        var session = WrappedSession("B");

        terminal.Attach(session);
        Assert.True(terminal.HarnessReplayPending);
        Assert.False(terminal.HarnessPollTimerRunning, "no poll may run while the replay is in flight");

        // Output that arrives after Attach copied the ring and before the handover. The handover runs on this
        // (the screen) thread, so it cannot happen until the pump below: these bytes are strictly after the
        // replay's end position.
        for (int i = 1; i <= 5; i++)
            session.Buffer!.Write(Bytes($"DURING-REPLAY-{i}\r\n"));

        WaitForHandover(terminal);
        Assert.True(terminal.HarnessPollTimerRunning, "the poll must start at the handover");
        PumpPolls(terminal);

        // And output after the handover, which the poll must pick up.
        session.Buffer!.Write(Bytes("AFTER-HANDOVER\r\n"));
        PumpPolls(terminal);

        string text = terminal.GetAllTerminalText();
        for (int i = 1; i <= 5; i++)
            Assert.Equal(1, Occurrences(text, $"DURING-REPLAY-{i}"));
        Assert.Equal(1, Occurrences(text, "AFTER-HANDOVER"));
    }

    [AvaloniaFact]
    public void Attach_SwitchedAwayDuringTheReplay_TheOldReplayIsDiscarded()
    {
        var (terminal, _) = ShownTerminal();
        var first = WrappedSession("FIRST");

        terminal.Attach(first);
        Assert.True(terminal.HarnessReplayPending);

        var secondBackend = new BufferBackend();
        secondBackend.Buffer!.Write(Bytes("the second session\r\n"));
        terminal.Attach(NewSession(secondBackend));

        WaitForHandover(terminal);
        // Let the first replay finish too, so a stale handover would have had its chance to paint.
        Thread.Sleep(3000);
        Dispatcher.UIThread.RunJobs();
        PumpPolls(terminal);

        string text = terminal.GetAllTerminalText();
        Assert.Contains("the second session", text);
        Assert.DoesNotContain("FIRST", text);
    }
}
