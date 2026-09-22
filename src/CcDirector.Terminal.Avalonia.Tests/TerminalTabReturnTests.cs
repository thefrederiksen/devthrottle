using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Terminal.Avalonia.Tests;

/// <summary>
/// Returning to the Terminal tab replays the whole terminal buffer only when the grid size changed while the tab
/// was hidden (relief plan step 8a). The replay is up to two megabytes of parsing on the screen thread, so an
/// unchanged return must keep the parser it has; a changed size must still get the full replay it always had.
/// </summary>
public sealed class TerminalTabReturnTests
{
    private sealed class BufferBackend : ISessionBackend
    {
        public int ProcessId => 1;
        public string Status => "Test";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer { get; } = new(64 * 1024);
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

    private static (TerminalControl Terminal, Window Window) AttachedTerminal()
    {
        var backend = new BufferBackend();
        backend.Buffer!.Write(Encoding.UTF8.GetBytes("line one\r\nline two\r\n"));
        var session = new Session(Guid.NewGuid(), @"C:\test\repo", @"C:\test\repo", null, backend, "claude-test",
            ActivityState.Idle, DateTimeOffset.UtcNow, null, null);
        session.MarkRunning();

        var terminal = new TerminalControl();
        var window = new Window { Width = 800, Height = 600, Content = terminal };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        terminal.Attach(session);
        Dispatcher.UIThread.RunJobs();
        return (terminal, window);
    }

    [AvaloniaFact]
    public void RefreshIfGridChangedSince_SameSize_KeepsTheParserAndDoesNotReplay()
    {
        var (terminal, _) = AttachedTerminal();
        var hidden = terminal.GridSize;
        var parserBefore = terminal.HarnessParser;

        bool replayed = terminal.RefreshIfGridChangedSince(hidden.Cols, hidden.Rows);

        Assert.False(replayed);
        Assert.Same(parserBefore, terminal.HarnessParser);
    }

    [AvaloniaFact]
    public void RefreshIfGridChangedSince_WindowResizedWhileHidden_ReplaysTheBuffer()
    {
        var (terminal, window) = AttachedTerminal();
        var hidden = terminal.GridSize;
        var parserBefore = terminal.HarnessParser;

        window.Width = 500;
        window.Height = 300;
        Dispatcher.UIThread.RunJobs();
        Assert.NotEqual(hidden, terminal.GridSize);

        bool replayed = terminal.RefreshIfGridChangedSince(hidden.Cols, hidden.Rows);

        Assert.True(replayed);
        Assert.NotSame(parserBefore, terminal.HarnessParser);
    }
}
