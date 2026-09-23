using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Input;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// The gate in front of a new session's first prompt (issue #3290), against the real captured screens in
/// TestData/doorbell. The failure it exists for, measured in the Director logs of 23 September 2026: Claude Code draws
/// its composer before it reads keystrokes, so a prompt typed at that moment was held, stripped of its Enter, and left
/// in the composer - or typed twice when the echo check pressed Escape and retyped. The gate proves the agent reads a
/// keystroke before a single character of the prompt is typed, and says so when it cannot.
/// </summary>
public sealed class FirstPromptGateTests
{
    private const char Nbsp = '\u00A0';

    /// <summary>
    /// A scripted agent. It shows <paramref name="loading"/> until <c>DrawAfterReads</c> screen reads have passed, then
    /// its composer. It does not READ input until <c>ReadAfterReads</c> reads have passed; keystrokes written before that
    /// are held, exactly as Claude Code holds them, and applied when it starts reading.
    /// </summary>
    private sealed class ScriptedAgent
    {
        private readonly AgentKind _agent;
        private readonly ScreenFrame _loading;
        private readonly ScreenFrame _composer;
        private readonly List<byte> _held = new();
        private string _typed = "";
        private int _reads;

        public ScriptedAgent(AgentKind agent, ScreenFrame loading, ScreenFrame composer)
        {
            _agent = agent;
            _loading = loading;
            _composer = composer;
        }

        public int DrawAfterReads { get; init; }
        public int ReadAfterReads { get; init; }
        public int ExitAfterReads { get; init; } = int.MaxValue;
        public List<byte[]> Writes { get; } = new();
        public List<int> WriteAtRead { get; } = new();
        public bool Exited => _reads >= ExitAfterReads;

        public void Write(byte[] data)
        {
            Writes.Add(data);
            WriteAtRead.Add(_reads);
            _held.AddRange(data);
        }

        public ScreenFrame TakeFrame()
        {
            _reads++;
            if (_reads < DrawAfterReads) return _loading;
            if (_reads >= ReadAfterReads)
            {
                foreach (var b in _held)
                    _typed = b == 0x7F ? (_typed.Length > 0 ? _typed[..^1] : "") : _typed + (char)b;
                _held.Clear();
            }
            return _typed.Length == 0 ? _composer : WithComposerText(_composer, _typed);
        }

        private ScreenFrame WithComposerText(ScreenFrame frame, string text)
        {
            if (_agent == AgentKind.ClaudeCode)
            {
                var prompt = DoorbellCaptures.LastRowStartingWith(frame, "\u276F");
                return DoorbellCaptures.WithRow(frame, prompt, "\u276F" + Nbsp + text) with
                {
                    CursorRow = prompt,
                    CursorCol = DoorbellSafety.EmptyComposerCursorColumn + text.Length,
                    CursorVisible = true,
                };
            }
            var row = DoorbellCaptures.LastRowStartingWith(frame, "\u203A");
            return DoorbellCaptures.WithRow(frame, row, "\u203A " + text) with
            {
                CursorRow = row,
                CursorCol = DoorbellSafety.EmptyComposerCursorColumn + text.Length,
                CursorVisible = true,
            };
        }
    }

    /// <summary>A screen with nothing drawn yet but the banner - what the terminal shows while the agent loads.</summary>
    private static ScreenFrame Loading()
    {
        var rows = Enumerable.Repeat("", 40).ToArray();
        rows[1] = " Claude Code v2.1.280";
        return new ScreenFrame(rows, 0, 0, false);
    }

    /// <summary>The fresh composer with the grey suggestion Claude Code 2.1.280 draws in it, as the failing logs show.</summary>
    private static ScreenFrame ClaudeComposerWithSuggestion()
    {
        var frame = DoorbellCaptures.Load("claude-idle-empty-fresh");
        var prompt = DoorbellCaptures.LastRowStartingWith(frame, "\u276F");
        return DoorbellCaptures.WithRow(frame, prompt, "\u276F" + Nbsp + "Try \"fix typecheck errors\"");
    }

    private static Task<FirstPromptGateResult> Run(ScriptedAgent agent, AgentKind kind, int limitSeconds = 60,
        Func<Task<IDisposable>>? beginInput = null)
    {
        var now = new DateTime(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc);
        return FirstPromptGate.WaitUntilAcceptingInputAsync(
            kind,
            agent.TakeFrame,
            agent.Write,
            () => agent.Exited,
            TimeSpan.FromSeconds(limitSeconds),
            beginInput,
            poll: TimeSpan.FromMilliseconds(250),
            pause: d => { now += d; return Task.CompletedTask; },
            utcNow: () => now);
    }

    // ---------- the failure from the logs ----------

    [Fact]
    public async Task A_composer_that_is_drawn_before_the_agent_reads_is_not_typed_into_until_the_probe_comes_back()
    {
        var agent = new ScriptedAgent(AgentKind.ClaudeCode, Loading(), ClaudeComposerWithSuggestion())
        {
            DrawAfterReads = 4,
            ReadAfterReads = 60, // about fifteen seconds of a drawn composer that reads nothing
        };

        var result = await Run(agent, AgentKind.ClaudeCode);

        Assert.Equal(FirstPromptGateOutcome.Ready, result.Outcome);
        // Exactly one probe and one Backspace - never retyped while the agent was deaf, no Escape, no Enter.
        Assert.Equal(2, agent.Writes.Count);
        Assert.Equal(new byte[] { (byte)'x' }, agent.Writes[0]);
        Assert.Equal(new byte[] { 0x7F }, agent.Writes[1]);
        // The Backspace waited for the agent to show the probe.
        Assert.True(agent.WriteAtRead[1] >= 60, $"Backspace was written at read {agent.WriteAtRead[1]}, before the agent read input");
        Assert.False(result.ProbeMayRemain);
    }

    [Fact]
    public async Task Nothing_is_typed_before_the_composer_is_drawn()
    {
        var agent = new ScriptedAgent(AgentKind.ClaudeCode, Loading(), DoorbellCaptures.Load("claude-idle-empty-fresh"))
        {
            DrawAfterReads = 20,
            ReadAfterReads = 0,
        };

        var result = await Run(agent, AgentKind.ClaudeCode);

        Assert.Equal(FirstPromptGateOutcome.Ready, result.Outcome);
        Assert.True(agent.WriteAtRead[0] >= 21, $"the probe was typed at read {agent.WriteAtRead[0]}, before two drawn frames");
    }

    // ---------- it fails loudly, and types nothing it cannot take back ----------

    [Fact]
    public async Task An_agent_that_never_reads_times_out_with_the_probe_typed_once_and_flagged()
    {
        var agent = new ScriptedAgent(AgentKind.ClaudeCode, Loading(), ClaudeComposerWithSuggestion())
        {
            DrawAfterReads = 0,
            ReadAfterReads = int.MaxValue,
        };

        var result = await Run(agent, AgentKind.ClaudeCode, limitSeconds: 30);

        Assert.Equal(FirstPromptGateOutcome.TimedOut, result.Outcome);
        Assert.Single(agent.Writes);
        Assert.Equal(new byte[] { (byte)'x' }, agent.Writes[0]);
        Assert.True(result.ProbeMayRemain);
        Assert.Contains("did not read a keystroke", result.Detail);
    }

    [Fact]
    public async Task A_composer_that_never_appears_times_out_with_nothing_typed()
    {
        var agent = new ScriptedAgent(AgentKind.ClaudeCode, Loading(), ClaudeComposerWithSuggestion())
        {
            DrawAfterReads = int.MaxValue,
        };

        var result = await Run(agent, AgentKind.ClaudeCode, limitSeconds: 30);

        Assert.Equal(FirstPromptGateOutcome.TimedOut, result.Outcome);
        Assert.Empty(agent.Writes);
        Assert.False(result.ProbeMayRemain);
    }

    [Theory]
    [InlineData("claude-menu-trust-folder", AgentKind.ClaudeCode)]
    [InlineData("claude-menu-model-picker", AgentKind.ClaudeCode)]
    [InlineData("codex-menu-trust-folder", AgentKind.Codex)]
    public async Task A_dialog_is_never_typed_into(string capture, AgentKind kind)
    {
        var dialog = DoorbellCaptures.Load(capture);
        var agent = new ScriptedAgent(kind, dialog, dialog) { DrawAfterReads = int.MaxValue };

        var result = await Run(agent, kind, limitSeconds: 30);

        Assert.Equal(FirstPromptGateOutcome.TimedOut, result.Outcome);
        Assert.Empty(agent.Writes);
    }

    [Fact]
    public async Task A_session_that_exits_while_starting_ends_the_gate_with_nothing_typed()
    {
        var agent = new ScriptedAgent(AgentKind.ClaudeCode, Loading(), ClaudeComposerWithSuggestion())
        {
            DrawAfterReads = int.MaxValue,
            ExitAfterReads = 5,
        };

        var result = await Run(agent, AgentKind.ClaudeCode);

        Assert.Equal(FirstPromptGateOutcome.Exited, result.Outcome);
        Assert.Empty(agent.Writes);
    }

    // ---------- the other agent it covers, and the input hold ----------

    [Fact]
    public async Task Codex_with_its_placeholder_is_gated_the_same_way()
    {
        var agent = new ScriptedAgent(AgentKind.Codex, Loading(), DoorbellCaptures.Load("codex-idle-empty-placeholder"))
        {
            DrawAfterReads = 3,
            ReadAfterReads = 30,
        };

        var result = await Run(agent, AgentKind.Codex);

        Assert.Equal(FirstPromptGateOutcome.Ready, result.Outcome);
        Assert.Equal(2, agent.Writes.Count);
    }

    [Fact]
    public async Task Other_input_is_held_from_just_before_the_probe_until_the_gate_ends()
    {
        var agent = new ScriptedAgent(AgentKind.ClaudeCode, Loading(), ClaudeComposerWithSuggestion())
        {
            DrawAfterReads = 2,
            ReadAfterReads = 10,
        };
        var hold = new Hold();
        var writesWhenHeld = -1;

        await Run(agent, AgentKind.ClaudeCode, beginInput: () =>
        {
            writesWhenHeld = agent.Writes.Count;
            return Task.FromResult<IDisposable>(hold);
        });

        Assert.Equal(0, writesWhenHeld);
        Assert.True(hold.Disposed);
    }

    [Fact]
    public async Task An_agent_without_a_composer_reader_is_refused_rather_than_guessed()
    {
        Assert.False(FirstPromptGate.CanProve(AgentKind.Gemini));
        await Assert.ThrowsAsync<NotSupportedException>(() => FirstPromptGate.WaitUntilAcceptingInputAsync(
            AgentKind.Gemini, Loading, _ => { }, () => false, TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData("claude-idle-empty-fresh", true)]
    [InlineData("claude-idle-empty-after-turn", true)]
    [InlineData("claude-working", false)]
    [InlineData("claude-menu-trust-folder", false)]
    public void ComposerDrawn_reads_the_real_captures(string capture, bool drawn)
    {
        Assert.Equal(drawn, FirstPromptGate.ComposerDrawn(AgentKind.ClaudeCode, DoorbellCaptures.Load(capture)));
    }

    private sealed class Hold : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    // ---------- the session: a gate that does not open is a failed delivery on the session's row ----------

    private sealed class RecordingBackend : ISessionBackend
    {
        public List<byte[]> Writes { get; } = new();
        public int ProcessId => 1234;
        public string Status => "Recording";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer => null;
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

    [Fact]
    public async Task DeliverFirstPromptAsync_ScreenNeverDrawn_TypesNothingAndMarksThePromptNotDelivered()
    {
        var backend = new RecordingBackend();
        using var session = new Session(Guid.NewGuid(), @"C:\test\repo", @"C:\test\repo", null, backend, SessionBackendType.ConPty);
        session.MarkRunning();
        Assert.True(session.CanGateFirstPrompt);

        var ex = await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(() =>
            session.DeliverFirstPromptAsync("Read the brief and follow it", SubmissionProvenance.FrameworkText(), TimeSpan.FromSeconds(1)));

        Assert.Empty(backend.Writes);
        var tally = PromptDeliveryFailures.Tally(session.Id);
        Assert.True(tally.Unresolved);
        Assert.Equal(1, tally.FailedDeliveries);
        Assert.Contains("first prompt was not typed", tally.LastFailureReason);
        Assert.Contains("not drawn", ex.Message);
    }
}
