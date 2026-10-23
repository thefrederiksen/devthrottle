using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Terminal.Avalonia.Tests;

/// <summary>
/// Text entry on the desktop terminal (issue #4379), through the REAL <see cref="TerminalControl"/> handlers on a real
/// <see cref="Session"/>: the bytes asserted are the bytes the agent's terminal receives. Each test names the failure from
/// the parity baseline (23 October 2026) it guards.
/// </summary>
public sealed class TerminalTextEntryTests
{
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
        public void Write(byte[] data) { lock (Writes) Writes.Add(data.ToArray()); }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }

        public byte[] All() { lock (Writes) return Writes.SelectMany(w => w).ToArray(); }
    }

    private static (TerminalControl Terminal, RecordingBackend Backend, Window Window) Open(AgentKind agent, bool agentAskedForBracketedPaste)
    {
        var backend = new RecordingBackend();
        var session = new Session(Guid.NewGuid(), "/test/repo", "/test/repo", null, backend, "agent-test",
            ActivityState.Idle, new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero), null, null) { AgentKind = agent };
        session.MarkRunning();
        // The agent's own request for bracketed paste, as Claude Code, Codex and Pi print it at start.
        if (agentAskedForBracketedPaste)
            backend.Buffer!.Write(Encoding.ASCII.GetBytes("\u001b[?2004h"));
        var terminal = new TerminalControl();
        var window = new Window { Width = 800, Height = 600, Content = terminal };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        terminal.Attach(session);
        return (terminal, backend, window);
    }

    /// <summary>Shift+Enter sent a plain Enter (CR), so a person's new line submitted the first line alone - every
    /// multi-line text on the desktop-newline route split, on all three agents. For each measured agent it must now send
    /// the key measured to make a new line (Ctrl+J, LF), and never CR.</summary>
    [AvaloniaTheory]
    [InlineData(AgentKind.ClaudeCode)]
    [InlineData(AgentKind.Codex)]
    [InlineData(AgentKind.Pi)]
    public void ShiftEnter_MeasuredAgent_SendsTheNewlineKeyNotEnter(AgentKind agent)
    {
        var (terminal, backend, window) = Open(agent, agentAskedForBracketedPaste: true);

        terminal.HarnessKeyDown(Key.Enter, KeyModifiers.Shift);

        Assert.Equal(new byte[] { 0x0A }, backend.All());
        window.Close();
    }

    /// <summary>Ctrl+J is the other chord a person uses for a new line; it sent nothing at all before.</summary>
    [AvaloniaFact]
    public void CtrlJ_MeasuredAgent_SendsTheNewlineKey()
    {
        var (terminal, backend, window) = Open(AgentKind.ClaudeCode, agentAskedForBracketedPaste: true);

        terminal.HarnessKeyDown(Key.J, KeyModifiers.Control);

        Assert.Equal(new byte[] { 0x0A }, backend.All());
        window.Close();
    }

    /// <summary>An agent nobody has measured keeps exactly what the terminal always sent for Shift+Enter, and a plain
    /// Enter is still Enter everywhere.</summary>
    [AvaloniaFact]
    public void ShiftEnter_UnmeasuredAgent_AndPlainEnter_StillSendEnter()
    {
        var (terminal, backend, window) = Open(AgentKind.Gemini, agentAskedForBracketedPaste: true);

        terminal.HarnessKeyDown(Key.Enter, KeyModifiers.Shift);
        terminal.HarnessKeyDown(Key.Enter);

        Assert.Equal(new byte[] { 0x0D, 0x0D }, backend.All());
        window.Close();
    }

    /// <summary>THE DESKTOP PASTE WAS NOT A PASTE: it went as plain keystrokes, so each line break was an Enter and each
    /// tab the Tab key. With the agent's bracketed paste on, Ctrl+V's code must send ONE bracketed paste, its CR LF breaks
    /// as single LFs and its tabs as tabs, with no Enter inside, and nothing after the end marker (the person presses
    /// Enter).</summary>
    [AvaloniaFact]
    public async Task Paste_AgentAskedForBracketedPaste_IsOneFramedPasteWithBreaksAndTabsAsText()
    {
        var (terminal, backend, window) = Open(AgentKind.Codex, agentAskedForBracketedPaste: true);

        await terminal.HarnessPasteAsync("line one\r\nline\ttwo\r\n");

        var sent = Encoding.UTF8.GetString(backend.All());
        Assert.Equal("\u001b[200~line one\nline\ttwo\u001b[201~", sent);
        window.Close();
    }

    /// <summary>A program that never asked for bracketed paste (a shell, a sign-in prompt) gets the text as a terminal
    /// sends it - no markers, at the keystroke pace that sign-in prompts need.</summary>
    [AvaloniaFact]
    public async Task Paste_ProgramNeverAskedForBracketedPaste_GoesUnframed()
    {
        var (terminal, backend, window) = Open(AgentKind.ClaudeCode, agentAskedForBracketedPaste: false);

        await terminal.HarnessPasteAsync("abc123");

        Assert.Equal("abc123", Encoding.UTF8.GetString(backend.All()));
        window.Close();
    }

    /// <summary>The paste was cut every eight UTF-16 units, so an emoji straddling a cut was written as two broken
    /// halves. A text with emoji in every position must arrive whole.</summary>
    [AvaloniaFact]
    public async Task Paste_EmojiAcrossEveryChunkBoundary_ArrivesWhole()
    {
        var (terminal, backend, window) = Open(AgentKind.Pi, agentAskedForBracketedPaste: false);
        var text = string.Concat(Enumerable.Repeat("ab\U0001F600", 10));

        await terminal.HarnessPasteAsync(text);

        Assert.Equal(text, Encoding.UTF8.GetString(backend.All()));
        window.Close();
    }
}
