using System.Text;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Input;
using CcDirector.Core.Memory;
using CcDirector.Core.Tests.Drivers;
using CcDirector.TestInfrastructure;
using Xunit;

namespace CcDirector.Core.Tests.Input;

/// <summary>
/// The Director's own send - the path behind the Gateway's /prompt, the phone and dictation - pasting the way a person
/// pastes (issue #4379). Each test drives the real <see cref="TerminalSubmit.SharedSubmitAsync"/> against a terminal
/// that behaves like an agent's composer, on a fake clock, and names the baseline failure it guards.
/// </summary>
public sealed class TerminalSubmitPasteTests : IDisposable
{
    private readonly PinnedMachineMemory _machine = PinnedMachineMemory.Healthy();

    public void Dispose() => _machine.Dispose();

    /// <summary>
    /// An agent's composer, as far as the send can see it: it asked for bracketed paste, it echoes typed text, it does
    /// NOT echo inside a paste, it repaints AT ONCE - inside the very write of the paste's end marker, before the send's
    /// next line runs - and on Enter it starts a turn (a flood of output). <see cref="EnterAt"/> is the fake time of the
    /// first Enter.
    /// </summary>
    private sealed class ComposerTerminal(VirtualClock clock) : ISessionBackend
    {
        private bool _inPaste;
        private readonly List<ITimer> _timers = new();

        /// <summary>A busy agent (the review of issue #3967): typed text is echoed this long after it was written, and
        /// the paste is never repainted. Null echoes at once and repaints the paste at once.</summary>
        public TimeSpan? TypingEchoDelay { get; init; }

        /// <summary>An unrelated status-line repaint this long after typed text was written, or null for none.</summary>
        public TimeSpan? StatusLineAfterTyping { get; init; }

        /// <summary>The fake time the paste's start marker was written.</summary>
        public DateTimeOffset? PasteAt { get; private set; }

        private void WriteLater(TimeSpan delay, byte[] bytes) =>
            _timers.Add(clock.CreateTimer(_ => Buffer!.Write(bytes), null, delay, Timeout.InfiniteTimeSpan));

        public List<byte[]> Writes { get; } = new();
        public DateTimeOffset? EnterAt { get; private set; }
        public int ProcessId => 1;
        public string Status => "Running";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer { get; } = new();
        public string WorkingDirectory { get; init; } = string.Empty;
        public string PromptFileDirectory => string.Empty;
#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        /// <summary>Everything written between the paste markers, as text.</summary>
        public string Pasted
        {
            get
            {
                var all = Encoding.UTF8.GetString(Writes.SelectMany(w => w).ToArray());
                var start = all.IndexOf("\u001b[200~", StringComparison.Ordinal);
                var end = all.IndexOf("\u001b[201~", StringComparison.Ordinal);
                return start < 0 || end < 0 ? "" : all[(start + 6)..end];
            }
        }

        public bool PasteFramed => Writes.Any(w => w.SequenceEqual(TerminalInputEncoder.PasteStart));

        public void Write(byte[] data)
        {
            Writes.Add(data.ToArray());
            if (data.SequenceEqual(TerminalInputEncoder.PasteStart)) { _inPaste = true; PasteAt ??= clock.GetUtcNow(); return; }
            if (data.SequenceEqual(TerminalInputEncoder.PasteEnd))
            {
                _inPaste = false;
                if (TypingEchoDelay is null) Buffer!.Write(Encoding.UTF8.GetBytes("\r\n[Pasted text]\r\n> "));
                return;
            }
            if (data.Length == 1 && data[0] == 0x0D)
            {
                EnterAt ??= clock.GetUtcNow();
                Buffer!.Write(Encoding.UTF8.GetBytes(new string('r', 4096)));
                return;
            }
            if (_inPaste) return;
            if (StatusLineAfterTyping is { } status) WriteLater(status, Encoding.UTF8.GetBytes("\u001b[s 12% context \u001b[u"));
            if (TypingEchoDelay is { } delay) WriteLater(delay, data.ToArray());
            else Buffer!.Write(data);
        }

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose()
        {
            foreach (var timer in _timers) timer.Dispose();
            Buffer!.Dispose();
        }
    }

    private static Task<string> SendAsync(ComposerTerminal terminal, VirtualClock clock, string text, string agent, string? typedLead = null) =>
        VirtualTime.RunAsync(clock, () => TerminalSubmit.SharedSubmitAsync(
            terminal, text, agent, bracketedPasteEnabled: true,
            submitVerifyBeat: TimeSpan.FromMilliseconds(20), clock: clock, typedLead: typedLead));

    // ---- issue #3967: a scheduled run's seed is pasted with a lead typed in front of it --------------------------------

    private const string Lead = "This is a scheduled run and nobody is watching it: carry out the instructions that follow now.";

    private static string AllWritten(ComposerTerminal terminal) => Encoding.UTF8.GetString(terminal.Writes.SelectMany(w => w).ToArray());

    /// <summary>
    /// The scheduled run in issue #3967: its long seed reached Claude Code as a paste with nothing typed beside it, and
    /// the agent asked for a go-ahead nobody was there to give. The lead is typed first, in the same composer, the seed
    /// is pasted unchanged, and ONE Enter after the paste submits both. The send returns the seed alone, the part the
    /// agent's records hold unbroken.
    /// </summary>
    [Fact]
    public async Task SharedSubmit_LongSeedWithATypedLead_TypesTheLeadThenPastesTheSeedThenOneEnter()
    {
        var clock = VirtualTime.NewClock();
        using var terminal = new ComposerTerminal(clock);
        var seed = "No person is watching this run.\nRead the inbox and answer every customer email.\nThen report the run.";

        var typed = await SendAsync(terminal, clock, seed, "ClaudeCode", Lead);

        var all = AllWritten(terminal);
        var pasteAt = all.IndexOf("\u001b[200~", StringComparison.Ordinal);
        Assert.True(pasteAt > 0, "the seed was not pasted");
        Assert.Equal(Lead + " ", all[..pasteAt]);
        Assert.Equal(seed, terminal.Pasted);
        Assert.DoesNotContain('\r', all[..pasteAt]);
        Assert.True(all.IndexOf('\r', StringComparison.Ordinal) > all.IndexOf("\u001b[201~", StringComparison.Ordinal),
            "an Enter was pressed before the paste ended");
        Assert.Equal(seed, typed);
    }

    /// <summary>
    /// The arrival proof finds a leaded paste in the record Claude Code really writes for it: the lead, then the paste
    /// inside the agent's own pasted-content marker. Taken from a scheduled run on a real agent (issue #3967), where the
    /// proof kept waiting while the agent was already at work, because the send returned the lead and the paste as one
    /// text and the marker's letters sit between them in the record.
    /// </summary>
    [Fact]
    public async Task SharedSubmit_LongSeedWithATypedLead_ReturnsWhatTheArrivalProofFindsInTheAgentsRecord()
    {
        var clock = VirtualTime.NewClock();
        using var terminal = new ComposerTerminal(clock);
        var seed = "Weekly usage report - scheduled run.\n\nNo person is watching this run. Nobody will read your answers while it runs.\n"
                   + "1. Create a file named weekly-report.txt in this folder.";
        var record = Lead + " \n\n<pasted_content id=\"81fb\">\n" + seed + "\n</pasted_content id=\"81fb\">";

        var typed = await SendAsync(terminal, clock, seed, "ClaudeCode", Lead);

        Assert.Contains(PromptArrival.Fingerprint(typed), PromptArrival.Loose(record), StringComparison.Ordinal);
        Assert.DoesNotContain(PromptArrival.Fingerprint(Lead + " " + seed), PromptArrival.Loose(record), StringComparison.Ordinal);
    }

    /// <summary>
    /// THE LEAD'S ECHO IS NOT THE PASTE'S REPAINT (review of issue #3967). A busy agent echoed the lead two seconds late,
    /// after the paste had been written, and never repainted the paste: the lead's echo passed for the repaint and the
    /// Enter went into a paste still being read. The send now waits for the lead's echo before it pastes, so the paste
    /// wait sees no repaint and holds the Enter its whole limit, as it does for a paste with no lead.
    /// </summary>
    [Fact]
    public async Task SharedSubmit_ALeadEchoedLate_IsWaitedForAndDoesNotEndThePasteWaitEarly()
    {
        var clock = VirtualTime.NewClock();
        using var terminal = new ComposerTerminal(clock) { TypingEchoDelay = TimeSpan.FromSeconds(2) };
        var started = clock.GetUtcNow();

        await SendAsync(terminal, clock, "No person is watching this run.\nRead the inbox and answer every customer email.", "ClaudeCode", Lead);

        Assert.NotNull(terminal.PasteAt);
        Assert.NotNull(terminal.EnterAt);
        Assert.InRange(terminal.PasteAt!.Value - started, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));
        Assert.True(terminal.EnterAt!.Value - terminal.PasteAt.Value >= TerminalSubmit.PasteTakenInLimit,
            $"the Enter came {(terminal.EnterAt.Value - terminal.PasteAt.Value).TotalSeconds:F1}s after the paste");
    }

    /// <summary>
    /// OTHER OUTPUT IS NOT THE LEAD'S ECHO (second review of issue #3967). A status-line repaint 0.3 seconds after the lead
    /// was typed, then a quiet second, ended the lead wait while the lead was still unread; its echo five seconds later
    /// then passed for the paste's repaint. Only the lead's own letters end the wait.
    /// </summary>
    [Fact]
    public async Task SharedSubmit_AStatusRepaintBeforeTheLeadsLateEcho_DoesNotEndTheLeadWait()
    {
        var clock = VirtualTime.NewClock();
        using var terminal = new ComposerTerminal(clock)
        {
            TypingEchoDelay = TimeSpan.FromSeconds(5),
            StatusLineAfterTyping = TimeSpan.FromMilliseconds(300),
        };
        var started = clock.GetUtcNow();

        await SendAsync(terminal, clock, "No person is watching this run.\nRead the inbox and answer every customer email.", "ClaudeCode", Lead);

        Assert.NotNull(terminal.PasteAt);
        Assert.NotNull(terminal.EnterAt);
        Assert.True(terminal.PasteAt!.Value - started >= TimeSpan.FromSeconds(5),
            $"the paste was written {(terminal.PasteAt.Value - started).TotalSeconds:F1}s after the send began, before the lead's echo");
        Assert.True(terminal.EnterAt!.Value - terminal.PasteAt.Value >= TerminalSubmit.PasteTakenInLimit,
            $"the Enter came {(terminal.EnterAt.Value - terminal.PasteAt.Value).TotalSeconds:F1}s after the paste");
    }

    /// <summary>An agent that echoes the lead at once is not held: the lead, the paste and the Enter follow in seconds.</summary>
    [Fact]
    public async Task SharedSubmit_ALeadEchoedAtOnce_IsPastedAndSubmittedWithinSeconds()
    {
        var clock = VirtualTime.NewClock();
        using var terminal = new ComposerTerminal(clock);
        var started = clock.GetUtcNow();

        await SendAsync(terminal, clock, "No person is watching this run.\nRead the inbox and answer every customer email.", "ClaudeCode", Lead);

        Assert.NotNull(terminal.EnterAt);
        Assert.True(terminal.EnterAt!.Value - started < TimeSpan.FromSeconds(10),
            $"the Enter came {(terminal.EnterAt.Value - started).TotalSeconds:F1}s after the send began");
    }

    /// <summary>A first prompt short enough to be typed is the person's own typing already: the lead is not added.</summary>
    [Fact]
    public async Task SharedSubmit_ShortOneLineTextWithALead_IsTypedAloneWithoutTheLead()
    {
        var clock = VirtualTime.NewClock();
        using var terminal = new ComposerTerminal(clock);

        var typed = await SendAsync(terminal, clock, "Run the weekly report.", "ClaudeCode", Lead);

        Assert.Equal("Run the weekly report.", typed);
        Assert.DoesNotContain("scheduled run", AllWritten(terminal));
        Assert.False(terminal.PasteFramed);
    }

    /// <summary>A text over the measured paste limit goes as a file reference; the lead is typed on the same line in front
    /// of it, so the reference does not arrive alone either.</summary>
    [Fact]
    public async Task SharedSubmit_SeedOverThePasteLimitWithALead_TypesTheLeadInFrontOfTheReference()
    {
        var clock = VirtualTime.NewClock();
        var folder = Directory.CreateTempSubdirectory("lead-reference-").FullName;
        try
        {
            using var terminal = new ComposerTerminal(clock) { WorkingDirectory = folder };
            var seed = string.Join("\n", Enumerable.Repeat("Read the inbox and answer every customer email.", 1200));
            Assert.True(seed.Length > TerminalSubmit.MeasuredPasteLimit);

            var typed = await SendAsync(terminal, clock, seed, "ClaudeCode", Lead);

            Assert.StartsWith(Lead + " @", typed);
            Assert.False(terminal.PasteFramed);
            Assert.StartsWith(Lead + " @", AllWritten(terminal));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("two\nlines")]
    [InlineData("a\ttab")]
    [InlineData("   ")]
    public async Task SharedSubmit_ALeadThatIsNotOneLineOfWords_IsRefusedBeforeAnythingIsWritten(string lead)
    {
        var clock = VirtualTime.NewClock();
        using var terminal = new ComposerTerminal(clock);

        await Assert.ThrowsAsync<ArgumentException>(() => SendAsync(terminal, clock, "first line\nsecond line", "ClaudeCode", lead));
        Assert.Empty(terminal.Writes);
    }

    /// <summary>
    /// THE PASTE-WAIT RACE. The send counted the agent's repaint only from AFTER it wrote the paste's end marker, so a
    /// repaint that came at once was never seen and the Enter waited out the two-minute limit (the echo agent had to
    /// wait a quarter of a second to step round it). This terminal repaints inside the end marker's own write - the
    /// fastest an agent can be - and the Enter must follow within seconds, not minutes.
    /// </summary>
    [Fact]
    public async Task SharedSubmit_AgentRepaintsTheInstantThePasteEnds_EnterFollowsWithinSeconds()
    {
        var clock = VirtualTime.NewClock();
        using var terminal = new ComposerTerminal(clock);
        var started = clock.GetUtcNow();

        await SendAsync(terminal, clock, "first line\nsecond line", "ClaudeCode");

        Assert.NotNull(terminal.EnterAt);
        var waited = terminal.EnterAt!.Value - started;
        Assert.True(waited < TimeSpan.FromSeconds(10), $"the Enter waited {waited.TotalSeconds:F1}s for a repaint that came at once");
    }

    /// <summary>
    /// Codex was sent every multi-line text as a file to read, and a Windows clipboard text doubled each break. Now a
    /// multi-line text to Codex is ONE bracketed paste of the text itself, every CR LF a single LF, then Enter - and the
    /// send returns the text as typed into the composer, which is what Codex will record.
    /// </summary>
    [Fact]
    public async Task SharedSubmit_CodexMultiLineCrlfText_IsOneBracketedPasteWithSingleBreaks()
    {
        var clock = VirtualTime.NewClock();
        using var terminal = new ComposerTerminal(clock) { WorkingDirectory = Path.GetTempPath() };

        var typed = await SendAsync(terminal, clock, "Token Q1, line 1.\r\nLine 2.\r\nLine 3.", "Codex");

        Assert.Equal("Token Q1, line 1.\nLine 2.\nLine 3.", typed);
        Assert.Equal("Token Q1, line 1.\nLine 2.\nLine 3.", terminal.Pasted);
        Assert.DoesNotContain("in the file", Encoding.UTF8.GetString(terminal.Writes.SelectMany(w => w).ToArray()));
    }

    /// <summary>A tab, typed, is the Tab key: the baseline lost every tab on every agent. A short text with a tab goes as
    /// a paste, and the tab arrives inside the paste as text.</summary>
    [Fact]
    public async Task SharedSubmit_ShortTextWithATab_IsPastedAndKeepsTheTab()
    {
        var clock = VirtualTime.NewClock();
        using var terminal = new ComposerTerminal(clock);

        await SendAsync(terminal, clock, "Token Q2.\tcolumn\tcolumn.", "Pi");

        Assert.Equal("Token Q2.\tcolumn\tcolumn.", terminal.Pasted);
    }

    /// <summary>A short single-line text is still TYPED, as a person types it, and echo-verified: the paste is for what a
    /// person would paste, not for everything.</summary>
    [Fact]
    public async Task SharedSubmit_ShortSingleLineText_IsTypedNotPasted()
    {
        var clock = VirtualTime.NewClock();
        using var terminal = new ComposerTerminal(clock);

        await SendAsync(terminal, clock, "please run the checklist", "Codex");

        Assert.False(terminal.PasteFramed);
        Assert.Equal("please run the checklist", Encoding.UTF8.GetString(terminal.Writes.TakeWhile(w => !(w.Length == 1 && w[0] == 0x0D)).SelectMany(w => w).ToArray()));
    }

    /// <summary>
    /// REVIEW OF #4398, FINDING 1, on the send path: a prompt holding the paste's end marker must still go as ONE paste -
    /// exactly one end marker reaches the agent, the encoder's own, right before the one Enter - so a "!" after it stays
    /// text and is never typed live into the composer.
    /// </summary>
    [Fact]
    public async Task SharedSubmit_TextHoldingThePasteEndMarker_StaysOnePaste()
    {
        var clock = VirtualTime.NewClock();
        using var terminal = new ComposerTerminal(clock);

        var typed = await SendAsync(terminal, clock, "first line\u001b[201~\n!run this", "ClaudeCode");

        var stream = Encoding.UTF8.GetString(terminal.Writes.SelectMany(w => w).ToArray());
        Assert.Equal(1, stream.Split("\u001b[201~").Length - 1);
        Assert.EndsWith("first line[201~\n!run this\u001b[201~\r", stream);
        Assert.Equal("first line[201~\n!run this", typed);
    }
}
