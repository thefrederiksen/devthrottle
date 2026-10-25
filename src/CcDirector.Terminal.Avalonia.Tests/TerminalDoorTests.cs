using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CcDirector.Core.Activity;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Terminal.Avalonia.Tests;

/// <summary>
/// THE DESKTOP TERMINAL DOOR (owner's ruling, 2026-09-05: source logging). Keystrokes typed straight into the
/// real <see cref="TerminalControl"/> reach the real Session through the control's own text-input path, and the
/// real activity producer writes the turn-submitted row: the door says it is the desktop terminal and the person
/// at the keyboard, the text is never in hand so there is no digest, and the length is the printable keystrokes.
/// </summary>
public sealed class TerminalDoorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-director-tests", Guid.NewGuid().ToString("N"));
    private readonly ActivityEventOutbox _outbox;
    private readonly ActivityEventProducer _producer;

    public TerminalDoorTests()
    {
        Directory.CreateDirectory(_dir);
        _outbox = new ActivityEventOutbox(Path.Combine(_dir, "outbox.jsonl"));
        _producer = new ActivityEventProducer(new SessionManager(new AgentOptions()) { DirectorId = "dir-test" }, _outbox);
    }

    public void Dispose()
    {
        _producer.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* scratch dir; best effort */ }
    }

    private sealed class RecordingBackend : ISessionBackend
    {
        public List<byte[]> Writes { get; } = new();
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
        public void Write(byte[] data) => Writes.Add(data);
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }

    [AvaloniaFact]
    public void KeystrokesTypedIntoTheRealTerminalControl_LandOnTheLedgerRow_AsTheDesktopTerminalDoor()
    {
        var backend = new RecordingBackend();
        var session = new Session(Guid.NewGuid(), @"C:\test\repo", @"C:\test\repo", null, backend, "claude-test",
            ActivityState.Idle, DateTimeOffset.UtcNow, null, null);
        session.MarkRunning();
        _producer.Wire(session);
        var terminal = new TerminalControl();
        var window = new Window { Width = 800, Height = 600, Content = terminal };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        terminal.Attach(session);

        // The user types a line, one keystroke at a time, then Enter - through the control's real text-input
        // and key-down handlers, exactly as the toolkit delivers them.
        foreach (var ch in "git status")
            terminal.HarnessTextInput(ch.ToString());
        terminal.HarnessKeyDown(global::Avalonia.Input.Key.Enter);

        Assert.Equal("git status".Length + 1, backend.Writes.Count);
        var row = Assert.Single(_outbox.PendingBatch(100), e => e.EventType == ActivityEventTypes.TurnSubmitted);
        Assert.Equal(SubmissionRoutes.DesktopTerminal, row.Route);
        Assert.Equal(SubmissionIdentityKinds.LocalUser, row.IdentityKind);
        Assert.Equal("typed/desktop", row.InputOrigin);
        Assert.Null(row.TranscriptId);
        Assert.Null(row.SpokenSpans);
        Assert.Null(row.ContentSha256);
        Assert.Equal("git status".Length, row.ContentLength);
        window.Close();
    }

    /// <summary>
    /// Issue #4380: a PASTE into the desktop terminal is the person at the terminal, as their typing is. It went with no
    /// origin, so while Claude Code's usage-limit dialog showed it was not counted as a person and the input guard dropped
    /// it, silently. Pasted through the control's real paste code (the clipboard read is the only part skipped).
    /// </summary>
    [AvaloniaFact]
    public async Task APasteWhileTheUsageLimitDialogShows_ReachesTheTerminal_AsThePersonAtIt()
    {
        var backend = new RecordingBackend();
        var session = new Session(Guid.NewGuid(), @"C:\test\repo", @"C:\test\repo", null, backend, SessionBackendType.ConPty);
        session.MarkRunning();
        backend.Buffer!.Write(System.Text.Encoding.UTF8.GetBytes("\u001b[2J\u001b[H" + string.Join("\r\n",
            "  You've hit your session limit - resets 10:20am (UTC)",
            "  What do you want to do?",
            "  > 1. Stop and wait for limit to reset",
            "    2. Add funds to continue with usage credits",
            "    3. Switch to Team plan",
            "  Enter to confirm - Esc to cancel")));
        Assert.True(session.UsageLimitDialogMayShow());
        var terminal = new TerminalControl();
        var window = new Window { Width = 800, Height = 600, Content = terminal };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        terminal.Attach(session);
        backend.Writes.Clear();

        await terminal.HarnessPasteAsync("pasted words");

        Assert.Equal("pasted words", string.Concat(backend.Writes.Select(w => System.Text.Encoding.UTF8.GetString(w))));
        window.Close();
    }
}
