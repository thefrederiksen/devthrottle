using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// THE BUSY-AGENT SEND (Voice Delivery mission, phase 3), driven through the Director's own <see cref="Session"/> against
/// a scripted agent terminal that draws its screen the way Claude Code and Codex do: a composer, and while it works a
/// spinner that repaints without a break, so the terminal is never quiet. That is the screen that held a spoken prompt
/// for 122 seconds on 25 September 2026 at 09:05.
/// </summary>
public sealed class BusyAgentSendTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-busy-send-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _cleanup = new();

    public BusyAgentSendTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var d in _cleanup) d.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private (Session Session, ScriptedAgentTerminal Terminal) NewWorkingSession(AgentKind agent, string composer = "")
    {
        var transcript = Path.Combine(_dir, Guid.NewGuid() + ".jsonl");
        File.WriteAllText(transcript, "");
        var terminal = new ScriptedAgentTerminal(agent, transcript, _dir) { Working = true };
        terminal.SetComposer(composer);
        _cleanup.Add(terminal);
        var session = new Session(Guid.NewGuid(), _dir, _dir, null, terminal, SessionBackendType.ConPty) { AgentKind = agent };
        _cleanup.Add(session);
        session.UpdateClaudeSessionPointer(Guid.NewGuid().ToString(), transcript, "test");
        terminal.StartDrawing();
        session.ApplyTerminalActivityState(ActivityState.Working);
        return (session, terminal);
    }

    private static string Paragraph(string token) =>
        $"Token {token}. " + string.Concat(Enumerable.Range(1, 8).Select(i =>
            $"Sentence {i} of a spoken prompt sent while the agent is busy, long enough to go as a paste. "));

    [Fact]
    public async Task SendTextAsync_ClaudeCodeWorkingAndNeverQuiet_PasteIsTakenInWithoutWaitingOutTheLimit()
    {
        // Arrange: a working Claude Code whose spinner never stops, as at 09:05.
        var (session, terminal) = NewWorkingSession(AgentKind.ClaudeCode);
        var text = Paragraph("PASTE1");
        Assert.True(text.Length > TerminalSubmit.ClaudeTypingLimit);
        var sw = Stopwatch.StartNew();

        // Act
        await session.SendTextAsync(text, SessionTestDoors.TestDoor);

        // Assert: taken in on the composer's evidence, in seconds - not the two-minute limit - and in the records once.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"the send took {sw.Elapsed.TotalSeconds:F1}s");
        Assert.Equal(1, terminal.EntersAccepted);
        Assert.Equal(new[] { text }, terminal.Recorded);
    }

    [Fact]
    public async Task SendTextAsync_ClaudeCodeWorkingEnterSwallowed_ReportsNotDeliveredAndLeavesTheTextInPlace()
    {
        // Arrange: a working Claude Code that swallows the Enter, so the text stays in its composer.
        var (session, terminal) = NewWorkingSession(AgentKind.ClaudeCode);
        terminal.SwallowEnter = true;
        session.ArrivalWindow = TimeSpan.FromSeconds(3);
        var text = "Token SHORT1. Reply with exactly: ACK";

        // Act
        var ex = await Assert.ThrowsAsync<PromptNotSubmittedException>(
            () => session.SendTextAsync(text, SessionTestDoors.TestDoor));

        // Assert: reported NOT delivered, the text is still in the composer exactly as typed, it was typed once, and
        // nothing cleared it or typed it again.
        Assert.Contains("NOT delivered", ex.Message);
        Assert.Equal(text, terminal.Composer);
        Assert.Empty(terminal.Recorded);
        Assert.Equal(1, CountOf(terminal.TypedText, text));
        Assert.Equal(0, terminal.ClearKeysPressed);
    }

    [Fact]
    public async Task SendTextAsync_CodexWorkingEnterSwallowed_ReportsNotDeliveredAndLeavesTheTextInPlace()
    {
        // Arrange: a working Codex - whose records cannot prove a prompt sent mid-turn - that swallows the Enter. Its
        // spinner prints plenty, so counting output after the Enter would call the send submitted.
        var (session, terminal) = NewWorkingSession(AgentKind.Codex);
        terminal.SwallowEnter = true;
        session.ComposerReleaseWindowForTests = TimeSpan.FromSeconds(1);
        var text = "Token CODEX1. Reply with exactly: ACK";

        // Act
        var ex = await Assert.ThrowsAsync<PromptNotSubmittedException>(
            () => session.SendTextAsync(text, SessionTestDoors.TestDoor));

        // Assert
        Assert.Contains("still in", ex.Message);
        Assert.Equal(text, terminal.Composer);
        Assert.Equal(1, CountOf(terminal.TypedText, text));
        Assert.Equal(0, terminal.ClearKeysPressed);
    }

    [Fact]
    public async Task SendTextAsync_CodexWorkingEnterTaken_IsDeliveredOnceTheComposerEmpties()
    {
        // Arrange
        var (session, terminal) = NewWorkingSession(AgentKind.Codex);
        var text = "Token CODEX2. Reply with exactly: ACK";

        // Act
        await session.SendTextAsync(text, SessionTestDoors.TestDoor);

        // Assert
        Assert.Equal(1, terminal.EntersAccepted);
        Assert.Equal("", terminal.Composer);
    }

    [Fact]
    public async Task SendTextAsync_ComposerHeldTextBeforeThePaste_EnterWaitsForThePasteToBeDrawn()
    {
        // Arrange: the composer already holds text, and the agent draws the paste only after two seconds. The text that
        // was there before is not the paste, so it must not release the Enter.
        var (session, terminal) = NewWorkingSession(AgentKind.ClaudeCode, composer: "an old draft");
        terminal.PasteDrawDelay = TimeSpan.FromSeconds(2);
        var text = Paragraph("PASTE2");

        // Act
        await session.SendTextAsync(text, SessionTestDoors.TestDoor);

        // Assert
        Assert.NotNull(terminal.PasteDrawnAt);
        Assert.NotNull(terminal.FirstEnterAt);
        Assert.True(terminal.FirstEnterAt >= terminal.PasteDrawnAt,
            $"Enter at {terminal.FirstEnterAt:HH:mm:ss.fff} came before the paste was drawn at {terminal.PasteDrawnAt:HH:mm:ss.fff}");
    }

    private static int CountOf(string hay, string needle)
    {
        var count = 0;
        for (var at = hay.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = hay.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}

/// <summary>
/// A scripted agent terminal. It keeps a composer the way Claude Code and Codex do - typed characters append, a
/// bracketed paste folds into "[Pasted text #1 +N lines]", Backspace removes one character (or the whole paste), Enter
/// submits and empties it - and draws the screen those agents draw, so the Director's own composer reader reads it.
/// While <see cref="Working"/> it redraws a spinner every 30 milliseconds and never goes quiet. An accepted Enter writes
/// the prompt to the conversation file the way Claude Code does: a queue "enqueue" line while working, a user line when
/// idle.
/// </summary>
internal sealed class ScriptedAgentTerminal : ISessionBackend
{
    private readonly object _lock = new();
    private readonly AgentKind _agent;
    private readonly string _transcript;
    private readonly StringBuilder _composer = new();
    private readonly StringBuilder _typed = new();
    private readonly StringBuilder _paste = new();
    private readonly CancellationTokenSource _stop = new();
    private string? _pasteHeld;
    private bool _inPaste;
    private int _frame;

    public ScriptedAgentTerminal(AgentKind agent, string transcript, string workingDirectory)
    {
        _agent = agent;
        _transcript = transcript;
        WorkingDirectory = workingDirectory;
    }

    public bool Working { get; set; }
    public bool SwallowEnter { get; set; }
    public TimeSpan PasteDrawDelay { get; set; } = TimeSpan.Zero;
    public int EntersAccepted { get; private set; }
    public int ClearKeysPressed { get; private set; }
    public DateTime? PasteDrawnAt { get; private set; }
    public DateTime? FirstEnterAt { get; private set; }
    public List<string> Recorded { get; } = new();

    public string Composer { get { lock (_lock) return ComposerShown(); } }
    public string TypedText { get { lock (_lock) return _typed.ToString(); } }

    public int ProcessId => 0;
    public string Status => "scripted";
    public bool IsRunning => true;
    public bool HasExited => false;
    public string WorkingDirectory { get; }
    public CircularTerminalBuffer? Buffer { get; } = new(1 << 20);
#pragma warning disable CS0067
    public event Action<string>? StatusChanged;
    public event Action<int>? ProcessExited;
#pragma warning restore CS0067

    public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }

    public void SetComposer(string text) { lock (_lock) { _composer.Clear(); _composer.Append(text); } }

    /// <summary>Turn on bracketed paste as the real agents do, draw the first frame, and start the spinner.</summary>
    public void StartDrawing()
    {
        Buffer!.Write(Encoding.UTF8.GetBytes("\x1b[?2004h"));
        Draw();
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(30);
                if (Working) Draw();
            }
        });
    }

    public void Write(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data);
        lock (_lock)
        {
            for (var i = 0; i < text.Length; i++)
            {
                if (text.AsSpan(i).StartsWith("\x1b[200~")) { _inPaste = true; _paste.Clear(); i += 5; continue; }
                if (text.AsSpan(i).StartsWith("\x1b[201~")) { _inPaste = false; i += 5; EndPaste(); continue; }
                var ch = text[i];
                if (_inPaste) { _paste.Append(ch); continue; }
                if (ch == '\r') { Enter(); continue; }
                if (ch is '\x7f' or '\b')
                {
                    ClearKeysPressed++;
                    if (_pasteHeld is not null) _pasteHeld = null;
                    else if (_composer.Length > 0) _composer.Length--;
                    continue;
                }
                if (ch < ' ') { if (ch is '\x05' or '\x15') ClearKeysPressed++; continue; }
                _composer.Append(ch);
                _typed.Append(ch);
            }
        }
        Draw();
    }

    private void EndPaste()
    {
        var pasted = _paste.ToString();
        _typed.Append(pasted);
        if (PasteDrawDelay <= TimeSpan.Zero)
        {
            _pasteHeld = pasted;
            PasteDrawnAt = DateTime.UtcNow;
            return;
        }
        _ = Task.Run(async () =>
        {
            await Task.Delay(PasteDrawDelay);
            lock (_lock) { _pasteHeld = pasted; PasteDrawnAt = DateTime.UtcNow; }
            Draw();
        });
    }

    private void Enter()
    {
        FirstEnterAt ??= DateTime.UtcNow;
        if (SwallowEnter) return;
        var submitted = _pasteHeld is not null ? _composer + _pasteHeld : _composer.ToString();
        if (submitted.Length == 0) return;
        EntersAccepted++;
        _composer.Clear();
        _pasteHeld = null;
        Recorded.Add(submitted);
        var line = Working
            ? JsonSerializer.Serialize(new { type = "queue-operation", operation = "enqueue", content = submitted })
            : JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = submitted } });
        File.AppendAllText(_transcript, line + "\n");
    }

    private string ComposerShown() =>
        _pasteHeld is null ? _composer.ToString() : _composer + $"[Pasted text #1 +{_pasteHeld.Count(c => c == '\n')} lines]";

    /// <summary>Draw the whole screen: a spinner row, then the agent's composer, then its footer.</summary>
    private void Draw()
    {
        string frame;
        lock (_lock)
        {
            _frame++;
            var shown = ComposerShown();
            var spinner = Working ? $"* Working... ({_frame})" : "Done.";
            var footer = Working ? "  esc to interrupt" : "  ? for shortcuts";
            var rule = new string('─', 80);
            var sb = new StringBuilder("\x1b[2J\x1b[H");
            if (_agent == AgentKind.Codex)
            {
                sb.Append(spinner).Append("\r\n\r\n").Append("› ").Append(shown).Append("\r\n\r\n").Append(footer);
                sb.Append($"\x1b[3;{3 + shown.Length}H");
            }
            else
            {
                sb.Append(spinner).Append("\r\n").Append(rule).Append("\r\n").Append("❯ ").Append(shown)
                    .Append("\r\n").Append(rule).Append("\r\n").Append(footer);
                sb.Append($"\x1b[3;{3 + shown.Length}H");
            }
            frame = sb.ToString();
        }
        Buffer!.Write(Encoding.UTF8.GetBytes(frame));
    }

    public Task SendTextAsync(string text) => Task.CompletedTask;
    public void Resize(short cols, short rows) { }
    public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;

    public void Dispose()
    {
        _stop.Cancel();
    }
}
