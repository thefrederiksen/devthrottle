using System.Text;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Input;
using CcDirector.Core.Memory;
using CcDirector.Core.Tests.Drivers;
using CcDirector.Terminal.Core;
using Xunit;

namespace CcDirector.Core.Tests.Input;

/// <summary>
/// A SEND TO AN IDLE CLAUDE CODE SHOWING ITS GREY SUGGESTION GOES THROUGH (the Prompt Delivery mission, 7 October 2026).
///
/// The owner's "go" to session 110 was refused twice over by one misreading: Claude Code's faint guess at the next
/// prompt, "yes, go on 2.18.0 after the follow-up merges", was read as text in the composer. These tests drive the real
/// <see cref="TerminalSubmit.SharedSubmitAsync"/> - with the arguments the Session passes for Claude Code - against a
/// terminal that behaves the way Claude Code 2.1.293 was captured behaving: whenever its composer is empty it draws the
/// suggestion faint, with the cursor straight after the glyph, and typing replaces it. The screen is the Director's own
/// parser fed those bytes, and the composer is read through <see cref="DoorbellSafety"/>, exactly as on a live session.
/// </summary>
[Collection("PromptDeliveryFailures")]
public sealed class ClaudeSuggestionSubmitTests : IDisposable
{
    private const string Suggestion = "yes, go on 2.18.0 after the follow-up merges";

    private readonly PinnedMachineMemory _machine = PinnedMachineMemory.Healthy();

    public ClaudeSuggestionSubmitTests() => PromptDeliveryFailures.ResetForTests();

    public void Dispose() => _machine.Dispose();

    /// <summary>
    /// A Claude Code composer on the Director's own parser. Printable keys go into the composer; Backspace removes the last
    /// character; Ctrl+E moves to the end (nothing to draw); Enter submits. Every change is drawn with the bytes Claude Code
    /// was captured writing, and an empty composer always shows the faint suggestion.
    /// </summary>
    private sealed class SuggestingClaudeTerminal : ISessionBackend
    {
        private const short Cols = 124;
        private const short Rows = 12;
        private readonly object _lock = new();
        private readonly AnsiParser _parser = new(new TerminalCell[Cols, Rows], Cols, Rows, new List<TerminalCell[]>(), 100);
        private readonly StringBuilder _composer = new();

        public List<string> Submitted { get; } = [];
        public List<byte[]> Written { get; } = [];

        public SuggestingClaudeTerminal()
        {
            var rule = new string('─', Cols);
            Draw("\x1b[2J\x1b[H● Shall I go ahead?\r\n\r\n\x1b[38;2;136;136;136m\r\n" + rule +
                 "\x1b[m❯ \x1b[K\x1b[38;2;136;136;136m\r\n" + rule +
                 "\x1b[m  \x1b[38;2;255;107;128m⏵⏵ bypass permissions on \x1b[38;2;153;153;153m(shift+tab to cycle)\x1b[K\x1b[m\r\n");
            DrawComposer();
        }

        public CircularTerminalBuffer? Buffer { get; } = new();
        public int ProcessId => 1;
        public string Status => "Test";
        public bool IsRunning => true;
        public bool HasExited => false;
#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Write(byte[] data)
        {
            lock (_lock)
            {
                Written.Add(data);
                foreach (var b in data)
                {
                    switch (b)
                    {
                        case 0x05: break;
                        case 0x7F: if (_composer.Length > 0) _composer.Length--; break;
                        case 0x0D:
                            Submitted.Add(_composer.ToString());
                            _composer.Clear();
                            Draw("\x1b[1;1H● working\x1b[K");
                            break;
                        default: if (b >= 0x20 && b < 0x7F) _composer.Append((char)b); break;
                    }
                }
                DrawComposer();
            }
        }

        /// <summary>The frame the Director takes from a live session (<c>Session.SnapshotLiveFrame</c>).</summary>
        public ScreenFrame Frame()
        {
            lock (_lock)
            {
                var (rows, row, col) = _parser.SnapshotActiveRows();
                var (withoutFaint, _, _) = _parser.SnapshotActiveRows(faintAsBlank: true);
                return new ScreenFrame(rows, row, col, _parser.IsCursorVisible, withoutFaint);
            }
        }

        private void DrawComposer() => Draw(_composer.Length == 0
            ? "\x1b[?25l\x1b[2m\x1b[5;3H" + Suggestion + "\x1b[22m\x1b[K\x1b[5;3H\x1b[?25h\x1b[m"
            : "\x1b[?25l\x1b[5;3H" + _composer + $"\x1b[K\x1b[5;{3 + _composer.Length}H\x1b[?25h");

        private void Draw(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            _parser.Parse(bytes);
            Buffer!.Write(bytes);
        }

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public Task SendTextAsync(string text) { Write(Encoding.UTF8.GetBytes(text)); return Task.CompletedTask; }
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() => Buffer!.Dispose();
    }

    /// <summary>The submit the Session runs for Claude Code, with the composer read the way the Session reads it.</summary>
    private static Task<string> Send(SuggestingClaudeTerminal terminal, string text) =>
        TerminalSubmit.SharedSubmitAsync(
            terminal,
            text,
            AgentKind.ClaudeCode.ToString(),
            screenSnapshot: () => terminal.Frame().Rows.ToArray(),
            submitVerifyBeat: TimeSpan.FromMilliseconds(20),
            clearKeysFor: retained => ComposerClearKeys.For(AgentKind.ClaudeCode, retained),
            composerHoldsNothing: () =>
            {
                var (reading, composerText) = DoorbellSafety.ReadComposerText(AgentKind.ClaudeCode, terminal.Frame());
                return PromptArrival.ComposerHoldsNothing(reading, composerText);
            },
            recordsProofFollows: true,
            composerSeen: () => DoorbellSafety.ReadComposerText(AgentKind.ClaudeCode, terminal.Frame()).ToString(),
            composerRegion: () => DoorbellSafety.ReadComposerText(AgentKind.ClaudeCode, terminal.Frame()));

    [Fact]
    public void Terminal_IdleWithTheSuggestion_TheComposerReadsEmpty()
    {
        using var terminal = new SuggestingClaudeTerminal();

        var frame = terminal.Frame();

        Assert.Equal("❯ " + Suggestion, frame.Rows[4]);
        Assert.Equal(ComposerReading.Empty, DoorbellSafety.ReadComposerText(AgentKind.ClaudeCode, frame).Reading);
    }

    [Fact]
    public async Task SharedSubmitAsync_ShortPromptInsideTheSuggestion_IsTypedOnceAndSubmitted()
    {
        // Session 110's first failure: "go" is inside the suggestion, so with the suggestion read as text the composer
        // "already held" the text once, and its echo was never accepted.
        using var terminal = new SuggestingClaudeTerminal();

        var typed = await Send(terminal, "go");

        Assert.Equal("go", typed);
        Assert.Equal(["go"], terminal.Submitted);
        Assert.DoesNotContain(terminal.Written, w => w.Length > 1 && w[0] == 0x05);
    }

    [Fact]
    public async Task SharedSubmitAsync_RetainedMarkAndTheSuggestionReturnsAfterTheClear_ClearsAndSubmits()
    {
        // Session 110's second failure: an earlier send may have left 2 characters, so the composer is cleared first;
        // Claude Code then draws its suggestion in the empty composer, and that must read as the clear having worked.
        using var terminal = new SuggestingClaudeTerminal();
        terminal.Write("go"u8.ToArray());
        ComposerRetention.MarkMayHoldText(terminal, AgentKind.ClaudeCode.ToString(), "go");

        var typed = await Send(terminal, "go");

        Assert.Equal("go", typed);
        Assert.Equal(["go"], terminal.Submitted);
        Assert.Contains(terminal.Written, w => w.Length > 1 && w[0] == 0x05);
    }

    [Fact]
    public async Task SharedSubmitAsync_UnderATrail_KeepsEveryStepWhole()
    {
        using var terminal = new SuggestingClaudeTerminal();
        terminal.Write("go"u8.ToArray());
        ComposerRetention.MarkMayHoldText(terminal, AgentKind.ClaudeCode.ToString(), "go");
        var trail = SendTrail.Begin();

        await Send(terminal, "go");

        var steps = string.Join("\n", trail.Steps);
        Assert.Contains("pressing Ctrl+E, Backspace x64", steps);
        Assert.Contains("the composer reads EMPTY", steps);
        Assert.Contains("the composer echoed the text", steps);
        Assert.Contains("reading=HoldsText, text='go'", steps);
    }
}
