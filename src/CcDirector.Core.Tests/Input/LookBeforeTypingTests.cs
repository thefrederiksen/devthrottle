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
/// LOOK BEFORE TYPING (the Prompt Delivery mission, 16 October 2026; the owner's ruling: never type unless the composer is
/// drawn AND empty, and refuse with a plain reason otherwise).
///
/// Measured in the field and reproduced in the delivery rig on a real Claude Code: a send to a session showing its
/// question menu was typed into the menu and lost, and every later send was refused until a person closed it; a send to a
/// composer holding the owner's unsent draft - or a doorbell line left unsubmitted - was typed onto its end and the agent
/// received both as one prompt, reported delivered. These tests drive the real <see cref="TerminalSubmit.SharedSubmitAsync"/>
/// with the arguments the Session passes for Claude Code, against a terminal drawn through the Director's own parser and
/// read through <see cref="DoorbellSafety"/>, exactly as a live session is read.
/// </summary>
[Collection("PromptDeliveryFailures")]
public sealed class LookBeforeTypingTests : IDisposable
{
    private readonly PinnedMachineMemory _machine = PinnedMachineMemory.Healthy();

    public LookBeforeTypingTests() => PromptDeliveryFailures.ResetForTests();

    public void Dispose() => _machine.Dispose();

    /// <summary>The rows of Claude Code's question menu, as the delivery rig captured them from Claude Code 2.1.301.</summary>
    private static readonly string[] QuestionMenu =
    [
        " ☐ Colour",
        "",
        "Which colour?",
        "",
        "❯ 1. Red",
        "     Choose red",
        "  2. Blue",
        "     Choose blue",
        "  3. Type something.",
        new string('─', 80),
        "  4. Chat about this",
        "",
        "Enter to select · ↑/↓ to navigate · Esc to cancel",
    ];

    /// <summary>A full-screen output view: a box of text and no composer anywhere, as captured on 14 October.</summary>
    private static readonly string[] OutputView =
    [
        "  Output:",
        "  ╭" + new string('─', 60) + "╮",
        "  │ count: 1 unread (now marked read) message 1 of 1" + new string(' ', 10) + "│",
        "  │" + new string(' ', 60) + "│",
        "  ╰" + new string('─', 60) + "╯",
    ];

    /// <summary>
    /// A Claude Code screen on the Director's own parser. Printable keys go into the composer, Backspace removes the last
    /// character, Ctrl+E does nothing visible, Enter submits. <see cref="Overlay"/>, when set, is drawn instead of the
    /// composer - a menu or a full-screen view - and keys typed while it is up are recorded but change nothing, as a real
    /// menu would not put them in the composer.
    /// </summary>
    private sealed class ClaudeScreen : ISessionBackend
    {
        private const short Cols = 100;
        private const short Rows = 20;
        private readonly object _lock = new();
        private readonly AnsiParser _parser = new(new TerminalCell[Cols, Rows], Cols, Rows, new List<TerminalCell[]>(), 100);
        private readonly StringBuilder _composer = new();
        private string[]? _overlay;

        public List<string> Submitted { get; } = [];
        public List<byte[]> Written { get; } = [];

        /// <summary>After this many frames are read, the overlay closes by itself (a menu the owner answered).</summary>
        public int? OverlayClosesAfterFrames { get; init; }
        private int _framesRead;

        public ClaudeScreen(string composer = "", string[]? overlay = null)
        {
            _composer.Append(composer);
            _overlay = overlay;
            Redraw();
        }

        public string Composer { get { lock (_lock) return _composer.ToString(); } }

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
                if (_overlay is not null) return;
                foreach (var b in data)
                {
                    switch (b)
                    {
                        case 0x05: break;
                        case 0x7F: if (_composer.Length > 0) _composer.Length--; break;
                        case 0x0D: Submitted.Add(_composer.ToString()); _composer.Clear(); break;
                        default: if (b >= 0x20 && b < 0x7F) _composer.Append((char)b); break;
                    }
                }
                Redraw();
            }
        }

        public ScreenFrame Frame()
        {
            lock (_lock)
            {
                _framesRead++;
                if (_overlay is not null && OverlayClosesAfterFrames is { } n && _framesRead > n)
                {
                    _overlay = null;
                    Redraw();
                }
                var (rows, row, col) = _parser.SnapshotActiveRows();
                var (withoutFaint, _, _) = _parser.SnapshotActiveRows(faintAsBlank: true);
                return new ScreenFrame(rows, row, col, _parser.IsCursorVisible, withoutFaint);
            }
        }

        private void Redraw()
        {
            var rule = new string('─', Cols);
            var body = _overlay is { } overlay
                ? string.Join("\r\n", overlay)
                : "● Shall I go ahead?\r\n\r\n" + rule + "\r\n❯ " + _composer + "\r\n" + rule +
                  "\r\n  ⏵⏵ bypass permissions on (shift+tab to cycle)" +
                  // Claude Code leaves the cursor in the composer, straight after what it holds.
                  $"\x1b[4;{3 + _composer.Length}H";
            var bytes = Encoding.UTF8.GetBytes("\x1b[2J\x1b[H" + body);
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
    private static Task<string> Send(ClaudeScreen screen, string text) =>
        TerminalSubmit.SharedSubmitAsync(
            screen,
            text,
            AgentKind.ClaudeCode.ToString(),
            screenSnapshot: () => screen.Frame().Rows.ToArray(),
            submitVerifyBeat: TimeSpan.FromMilliseconds(20),
            clearKeysFor: retained => ComposerClearKeys.For(AgentKind.ClaudeCode, retained),
            composerHoldsNothing: () =>
            {
                var (reading, composerText) = DoorbellSafety.ReadComposerText(AgentKind.ClaudeCode, screen.Frame());
                return PromptArrival.ComposerHoldsNothing(reading, composerText);
            },
            recordsProofFollows: true,
            composerSeen: () => DoorbellSafety.ReadComposerText(AgentKind.ClaudeCode, screen.Frame()).ToString(),
            composerRegion: () => DoorbellSafety.ReadComposerText(AgentKind.ClaudeCode, screen.Frame()));

    private const string Prompt = "please run the release checklist now";

    [Fact]
    public void Screen_QuestionMenu_ReadsAsMenuOpen()
    {
        using var screen = new ClaudeScreen(overlay: QuestionMenu);

        Assert.Equal(ComposerReading.MenuOpen, DoorbellSafety.ReadComposer(AgentKind.ClaudeCode, screen.Frame()));
    }

    [Fact]
    public async Task SharedSubmit_QuestionMenuOpen_RefusesAndTypesNothing()
    {
        using var screen = new ClaudeScreen(overlay: QuestionMenu);

        var ex = await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(() => Send(screen, Prompt));

        Assert.Contains("nothing was typed", ex.Message);
        Assert.Contains("menu or a dialog", ex.Message);
        Assert.False(ex.TerminalReacted);
        Assert.Empty(screen.Written);
    }

    [Fact]
    public async Task SharedSubmit_FullScreenViewWithNoComposer_RefusesAndTypesNothing()
    {
        using var screen = new ClaudeScreen(overlay: OutputView);

        var ex = await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(() => Send(screen, Prompt));

        Assert.Contains("composer is not on the screen", ex.Message);
        Assert.Empty(screen.Written);
    }

    [Fact]
    public async Task SharedSubmit_OwnerDraftInComposer_RefusesAndLeavesTheDraft()
    {
        using var screen = new ClaudeScreen(composer: "my unfinished draft");

        var ex = await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(() => Send(screen, Prompt));

        Assert.Contains("never sent", ex.Message);
        Assert.Empty(screen.Written);
        Assert.Empty(screen.Submitted);
        Assert.Equal("my unfinished draft", screen.Composer);
    }

    [Fact]
    public async Task SharedSubmit_EmptyComposer_SubmitsThePromptAlone()
    {
        using var screen = new ClaudeScreen();

        await Send(screen, Prompt);

        Assert.Equal([Prompt], screen.Submitted);
    }

    [Fact]
    public async Task SharedSubmit_ProductsOwnLeftoverLine_IsClearedThenThePromptGoesAlone()
    {
        const string leftover = "[DevThrottle doorbell] 1 fleet message is waiting for you. To read, run: cc-devthrottle message inbox";
        using var screen = new ClaudeScreen(composer: leftover);
        ComposerRetention.MarkMayHoldText(screen, AgentKind.ClaudeCode.ToString(), leftover);

        await Send(screen, Prompt);

        Assert.Equal([Prompt], screen.Submitted);
    }

    [Fact]
    public async Task SharedSubmit_MenuOpenWhileAMarkStands_RefusesAndKeepsTheMarkForTheNextSend()
    {
        using var screen = new ClaudeScreen(overlay: QuestionMenu);
        ComposerRetention.MarkMayHoldText(screen, AgentKind.ClaudeCode.ToString(), "an earlier unsent prompt");

        await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(() => Send(screen, Prompt));

        Assert.Empty(screen.Written);
        Assert.True(ComposerRetention.MayHoldText(screen));
    }

    [Fact]
    public async Task SharedSubmit_MenuClosesWhileTheSendLooks_SubmitsThePrompt()
    {
        using var screen = new ClaudeScreen(overlay: QuestionMenu) { OverlayClosesAfterFrames = 2 };

        await Send(screen, Prompt);

        Assert.Equal([Prompt], screen.Submitted);
    }
}
