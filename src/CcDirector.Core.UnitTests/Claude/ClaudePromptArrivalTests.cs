using System.Text;
using System.Text.Json;
using CcDirector.Core.Backends;
using CcDirector.Core.Claude;
using CcDirector.Core.Drivers;
using CcDirector.Core.Input;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Claude;

/// <summary>
/// The proof that a prompt reached Claude Code (issue #3290): its conversation file, never the terminal's output.
/// The case it exists for: on 24 September 2026 the 07:45 LinkedIn schedule's 3,606 character paste was dropped by a
/// Claude Code still starting, Claude Code printed "Removed 4 invisible characters - nothing left to send", and the
/// submit verifier counted that message as a submitted turn.
/// </summary>
public sealed class ClaudePromptArrivalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-arrival-" + Guid.NewGuid().ToString("N"));
    private readonly string _file;

    public ClaudePromptArrivalTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, Guid.NewGuid() + ".jsonl");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private const string LinkedInPrompt =
        "FIRST LINE OF YOUR REPORT: say which model you are running as.\r\n\r\nSkill /linkedin - answer twenty replies in the inbox.";

    private static string UserLine(string content, bool meta = false, bool sidechain = false) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "user",
            ["isMeta"] = meta,
            ["isSidechain"] = sidechain,
            ["message"] = new Dictionary<string, object?> { ["role"] = "user", ["content"] = content },
        });

    private static string UserArrayLine(string text) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "user",
            ["message"] = new Dictionary<string, object?>
            {
                ["role"] = "user",
                ["content"] = new object[] { new Dictionary<string, object?> { ["type"] = "text", ["text"] = text } },
            },
        });

    private static string EnqueueLine(string content) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "queue-operation", ["operation"] = "enqueue", ["content"] = content,
        });

    private void Append(params string[] lines) => File.AppendAllText(_file, string.Join("\n", lines) + "\n", Encoding.UTF8);

    // ---------- reading the conversation file ----------

    [Fact]
    public void A_paste_wrapped_by_Claude_Code_with_other_line_endings_is_found()
    {
        var offset = ClaudePromptArrival.EndOf(_file);
        Append(UserLine("  <pasted_content id=\"9d0f\">\n" + LinkedInPrompt.Replace("\r\n", "\n") + "\n</pasted_content>"));
        Assert.True(ClaudePromptArrival.Arrived(_file, offset, LinkedInPrompt));
    }

    [Fact]
    public void A_plain_prompt_and_a_prompt_as_text_parts_are_found()
    {
        Append(UserLine("Read the brief and follow it"));
        Assert.True(ClaudePromptArrival.Arrived(_file, 0, "Read the brief and follow it"));
        var offset = ClaudePromptArrival.EndOf(_file);
        Append(UserArrayLine("Check the inbox now"));
        Assert.True(ClaudePromptArrival.Arrived(_file, offset, "Check the inbox now"));
    }

    [Fact]
    public void A_prompt_queued_while_Claude_Code_is_busy_is_found()
    {
        Append(EnqueueLine("Check the inbox now"));
        Assert.True(ClaudePromptArrival.Arrived(_file, 0, "Check the inbox now"));
    }

    [Fact]
    public void The_same_prompt_written_before_the_send_is_not_proof()
    {
        Append(UserLine("Check the inbox now"));
        var offset = ClaudePromptArrival.EndOf(_file);
        Append(UserLine("something else entirely"));
        Assert.False(ClaudePromptArrival.Arrived(_file, offset, "Check the inbox now"));
    }

    [Fact]
    public void Meta_and_sidechain_lines_are_not_proof()
    {
        Append(UserLine("Check the inbox now", meta: true), UserLine("Check the inbox now", sidechain: true));
        Assert.False(ClaudePromptArrival.Arrived(_file, 0, "Check the inbox now"));
    }

    [Fact]
    public void A_missing_file_and_a_half_written_last_line_are_not_proof_and_do_not_throw()
    {
        Assert.False(ClaudePromptArrival.Arrived(_file, 0, "Check the inbox now"));
        Assert.Equal(0, ClaudePromptArrival.EndOf(_file));
        File.WriteAllText(_file, "{\"type\":\"user\",\"message\":{\"content\":\"Check the inb");
        Assert.False(ClaudePromptArrival.Arrived(_file, 0, "Check the inbox now"));
    }

    [Fact]
    public void A_file_shorter_than_the_mark_was_replaced_and_is_read_from_its_start()
    {
        Append(UserLine("Check the inbox now"));
        Assert.True(ClaudePromptArrival.Arrived(_file, 1_000_000, "Check the inbox now"));
    }

    [Theory]
    [InlineData("/clear", false)]
    [InlineData("  /compact now", false)]
    [InlineData("!git status", false)]
    [InlineData("# remember this", false)]
    [InlineData("   ", false)]
    [InlineData("skill /jobs --morning-scan", true)]
    [InlineData("Read the brief", true)]
    public void Commands_are_not_proven_through_the_file(string text, bool provable) =>
        Assert.Equal(provable, ClaudePromptArrival.CanProve(text));

    [Theory]
    [InlineData(ComposerReading.Empty, "", true)]
    [InlineData(ComposerReading.HoldsText, "Try \"how do I log an error?\"", true)]
    [InlineData(ComposerReading.HoldsText, "[Pasted text #1 +12 lines]", false)]
    [InlineData(ComposerReading.HoldsText, "FIRST LINE OF YOUR REPORT", false)]
    [InlineData(ComposerReading.MenuOpen, "", false)]
    [InlineData(ComposerReading.NotFound, "", false)]
    public void Only_an_empty_composer_or_the_grey_suggestion_holds_nothing(ComposerReading reading, string text, bool nothing) =>
        Assert.Equal(nothing, ClaudePromptArrival.ComposerHoldsNothing(reading, text));

    // ---------- the decision ----------

    private static Task NoPause(TimeSpan _) => Task.CompletedTask;

    private sealed class Clock
    {
        public DateTime Now = new(2026, 9, 24, 11, 45, 0, DateTimeKind.Utc);
        public Task Pause(TimeSpan step) { Now += step; return Task.CompletedTask; }
    }

    [Fact]
    public async Task A_prompt_already_in_the_file_is_arrived_and_never_resent()
    {
        var resends = 0;
        var outcome = await ClaudePromptArrival.ConfirmAsync(
            () => true, () => true, () => { resends++; return Task.CompletedTask; }, true,
            TimeSpan.FromSeconds(20), "t", pause: NoPause);
        Assert.Equal(PromptArrivalOutcome.Arrived, outcome);
        Assert.Equal(0, resends);
    }

    [Fact]
    public async Task A_prompt_lost_with_an_empty_composer_is_resent_exactly_once()
    {
        var clock = new Clock();
        var resends = 0;
        var outcome = await ClaudePromptArrival.ConfirmAsync(
            () => resends > 0, () => true, () => { resends++; return Task.CompletedTask; }, true,
            TimeSpan.FromSeconds(20), "t", pause: clock.Pause, utcNow: () => clock.Now);
        Assert.Equal(PromptArrivalOutcome.ArrivedAfterResend, outcome);
        Assert.Equal(1, resends);
    }

    [Fact]
    public async Task A_prompt_that_never_arrives_is_resent_once_then_reported_not_arrived()
    {
        var clock = new Clock();
        var resends = 0;
        var outcome = await ClaudePromptArrival.ConfirmAsync(
            () => false, () => true, () => { resends++; return Task.CompletedTask; }, true,
            TimeSpan.FromSeconds(20), "t", pause: clock.Pause, utcNow: () => clock.Now);
        Assert.Equal(PromptArrivalOutcome.NotArrived, outcome);
        Assert.Equal(1, resends);
    }

    [Fact]
    public async Task A_composer_holding_text_is_never_resent_into()
    {
        var clock = new Clock();
        var resends = 0;
        var outcome = await ClaudePromptArrival.ConfirmAsync(
            () => false, () => false, () => { resends++; return Task.CompletedTask; }, true,
            TimeSpan.FromSeconds(20), "t", pause: clock.Pause, utcNow: () => clock.Now);
        Assert.Equal(PromptArrivalOutcome.NotArrived, outcome);
        Assert.Equal(0, resends);
    }

    [Fact]
    public async Task One_empty_frame_followed_by_text_does_not_license_a_resend()
    {
        var clock = new Clock();
        var frames = new Queue<bool>([true, false]);
        var resends = 0;
        var outcome = await ClaudePromptArrival.ConfirmAsync(
            () => false, () => frames.Count > 0 ? frames.Dequeue() : false, () => { resends++; return Task.CompletedTask; }, true,
            TimeSpan.FromSeconds(20), "t", pause: clock.Pause, utcNow: () => clock.Now);
        Assert.Equal(PromptArrivalOutcome.NotArrived, outcome);
        Assert.Equal(0, resends);
    }

    [Fact]
    public async Task A_send_that_may_not_resend_is_reported_not_arrived()
    {
        var clock = new Clock();
        var resends = 0;
        var outcome = await ClaudePromptArrival.ConfirmAsync(
            () => false, () => true, () => { resends++; return Task.CompletedTask; }, false,
            TimeSpan.FromSeconds(20), "t", pause: clock.Pause, utcNow: () => clock.Now);
        Assert.Equal(PromptArrivalOutcome.NotArrived, outcome);
        Assert.Equal(0, resends);
    }

    // ---------- the session: output is never proof, the conversation file is ----------

    /// <summary>A terminal with no screen. When <see cref="OnEnter"/> is set it runs on every Enter, standing in for
    /// Claude Code writing the turn to its conversation file.</summary>
    private sealed class ScriptedTerminal : ISessionBackend
    {
        public List<byte[]> Writes { get; } = new();
        public Action? OnEnter { get; set; }
        public int ProcessId => 4321;
        public string Status => "Scripted";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer => null;
#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067
        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data)
        {
            Writes.Add(data);
            if (data.Length == 1 && data[0] == 0x0D) OnEnter?.Invoke();
        }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }

    private Session NewClaudeSession(ScriptedTerminal terminal)
    {
        var session = new Session(Guid.NewGuid(), _dir, _dir, null, terminal, SessionBackendType.ConPty);
        session.MarkRunning();
        session.UpdateClaudeSessionPointer(Path.GetFileNameWithoutExtension(_file), _file, "startup");
        session.ArrivalWindow = TimeSpan.FromMilliseconds(600);
        return session;
    }

    [Fact]
    public async Task A_send_Claude_Code_never_records_is_NOT_delivered_whatever_the_terminal_did()
    {
        var terminal = new ScriptedTerminal();
        using var session = NewClaudeSession(terminal);

        await Assert.ThrowsAsync<PromptNotSubmittedException>(() =>
            session.SendTextAsync(LinkedInPrompt, SubmissionProvenance.FrameworkText(), SendSource.Framework));

        var tally = PromptDeliveryFailures.Tally(session.Id);
        Assert.True(tally.Unresolved);
        Assert.Contains("never reached Claude Code", tally.LastFailureReason);
    }

    [Fact]
    public async Task A_send_Claude_Code_records_is_delivered()
    {
        var terminal = new ScriptedTerminal();
        using var session = NewClaudeSession(terminal);
        terminal.OnEnter = () => Append(UserLine(LinkedInPrompt));

        await session.SendTextAsync(LinkedInPrompt, SubmissionProvenance.FrameworkText(), SendSource.Framework);

        Assert.False(PromptDeliveryFailures.Tally(session.Id).Unresolved);
    }
}
