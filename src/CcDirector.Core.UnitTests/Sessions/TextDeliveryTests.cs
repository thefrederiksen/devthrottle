using System.Text;
using CcDirector.Core.Agents;
using CcDirector.Core.Drivers;
using CcDirector.Core.Grok;
using CcDirector.Core.Input;
using CcDirector.Core.Memory;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// The rules the text-delivery qualification rig (src/CcDirector.DeliveryQualification) measured against real agents on
/// 24 September 2026 (issue #3290), pinned so they cannot drift back. Each test names the failure it guards.
/// </summary>
public sealed class TextDeliveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-delivery-" + Guid.NewGuid().ToString("N"));

    public TextDeliveryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private const string Dictated = "Caf\u00e9 \u2013 an en dash, \u201ccurly quotes\u201d, \u00a3 and \u20ac";

    // ---------------------------------------------------------------------------------------------
    // Characters outside ASCII reach Codex and Grok as key presses
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Ascii_is_written_as_it_is_on_every_backend()
    {
        Assert.Equal(Encoding.UTF8.GetBytes("plain text 123"), TerminalSubmit.Encode("plain text 123", nonAsciiAsKeyEvents: true));
        Assert.Equal(Encoding.UTF8.GetBytes("plain text 123"), TerminalSubmit.Encode("plain text 123", nonAsciiAsKeyEvents: false));
    }

    [Fact]
    public void A_character_outside_ascii_becomes_one_key_press_down_and_up_on_a_pseudo_console()
    {
        var bytes = Encoding.ASCII.GetString(TerminalSubmit.Encode("a\u2013b", nonAsciiAsKeyEvents: true));

        Assert.Equal("a\u001b[0;0;8211;1;0;1_\u001b[0;0;8211;0;0;1_b", bytes);
    }

    [Fact]
    public void A_character_outside_the_basic_plane_is_sent_as_its_two_surrogate_units()
    {
        var bytes = Encoding.ASCII.GetString(TerminalSubmit.Encode("\U0001F600", nonAsciiAsKeyEvents: true));

        Assert.Contains(";55357;1;0;1_", bytes);
        Assert.Contains(";56832;1;0;1_", bytes);
    }

    [Fact]
    public void Without_key_events_the_text_is_plain_utf8()
    {
        Assert.Equal(Encoding.UTF8.GetBytes(Dictated), TerminalSubmit.Encode(Dictated, nonAsciiAsKeyEvents: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Chunks_never_split_a_character_and_reassemble_to_the_whole_text(bool asKeyEvents)
    {
        var text = string.Concat(Enumerable.Repeat(Dictated + " ", 12));

        var chunks = TerminalSubmit.Chunks(text, asKeyEvents).ToList();

        Assert.True(chunks.Count > 1);
        Assert.Equal(TerminalSubmit.Encode(text, asKeyEvents), chunks.SelectMany(c => c).ToArray());
        if (!asKeyEvents)
            foreach (var chunk in chunks)
                Assert.Equal(chunk, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(chunk)));
    }

    // ---------------------------------------------------------------------------------------------
    // The composer filling up is progress
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("", "TokenQ12brief", 0)]
    [InlineData("xxTokenQ1yy", "TokenQ12brief", 7)]
    [InlineData("TokenQ12brief", "TokenQ12brief", 13)]
    [InlineData("nothing", "TokenQ12brief", 0)]
    public void The_screen_prefix_is_the_longest_start_of_the_text_on_screen(string hay, string needle, int expected)
    {
        Assert.Equal(expected, TerminalSubmit.PrefixLengthIn(hay, needle));
    }

    // ---------------------------------------------------------------------------------------------
    // No blind Enter nudges when the records decide
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task When_the_records_decide_exactly_one_Enter_is_pressed_on_a_silent_terminal()
    {
        // The 8,238-character paste on 24 September: eight nudges became eight blank lines in the next prompt.
        var buffer = new CircularTerminalBuffer();
        var enters = 0;

        await SubmitVerifier.PressEnterAndVerifyAsync(
            buffer, b => { if (b.Length == 1 && b[0] == 0x0D) enters++; }, "t",
            attemptDelay: TimeSpan.FromMilliseconds(1), beatDelay: _ => Task.CompletedTask, throwWhenParked: false);

        Assert.Equal(1, enters);
    }

    // ---------------------------------------------------------------------------------------------
    // The arrival proof: Enter again only when the screen asks, never a resend after any new prompt
    // ---------------------------------------------------------------------------------------------

    private sealed class Clock
    {
        public DateTime Now = new(2026, 9, 24, 11, 45, 0, DateTimeKind.Utc);
        public Task Pause(TimeSpan step) { Now += step; return Task.CompletedTask; }
    }

    [Fact]
    public async Task No_resend_while_the_agent_has_printed_nothing_since_the_send()
    {
        // Measured 24 September 2026: a CPU-starved Claude Code had not drawn an 8 KB paste, its composer read as
        // empty, and the resend pasted the text a second time behind the first.
        var clock = new Clock();
        var resends = 0;
        var outcome = await PromptArrival.ConfirmAsync(
            () => false, () => true, () => { resends++; return Task.CompletedTask; }, true, TimeSpan.FromSeconds(20), "t",
            agentPrintedSinceSend: () => false, pause: clock.Pause, utcNow: () => clock.Now);

        Assert.Equal(PromptArrivalOutcome.NotArrived, outcome);
        Assert.Equal(0, resends);
    }

    [Fact]
    public async Task A_resend_is_still_made_when_the_agent_printed_and_the_composer_is_empty()
    {
        var clock = new Clock();
        var resends = 0;
        var outcome = await PromptArrival.ConfirmAsync(
            () => resends > 0, () => true, () => { resends++; return Task.CompletedTask; }, true, TimeSpan.FromSeconds(20), "t",
            agentPrintedSinceSend: () => true, pause: clock.Pause, utcNow: () => clock.Now);

        Assert.Equal(PromptArrivalOutcome.ArrivedAfterResend, outcome);
        Assert.Equal(1, resends);
    }

    [Fact]
    public async Task An_Enter_eaten_by_the_agent_is_pressed_again_when_the_composer_still_holds_the_text()
    {
        var clock = new Clock();
        var enters = 0;
        var outcome = await PromptArrival.ConfirmAsync(
            () => enters > 0, () => false, () => Task.CompletedTask, true, TimeSpan.FromSeconds(20), "t",
            textWaitsUnsubmitted: () => Task.FromResult(enters == 0), pressEnter: () => enters++,
            pause: clock.Pause, utcNow: () => clock.Now);

        Assert.Equal(PromptArrivalOutcome.Arrived, outcome);
        Assert.Equal(1, enters);
    }

    [Fact]
    public async Task No_Enter_is_pressed_again_when_the_composer_does_not_hold_text()
    {
        var clock = new Clock();
        var enters = 0;
        await PromptArrival.ConfirmAsync(
            () => false, () => false, () => Task.CompletedTask, false, TimeSpan.FromSeconds(20), "t",
            textWaitsUnsubmitted: () => Task.FromResult(false), pressEnter: () => enters++,
            pause: clock.Pause, utcNow: () => clock.Now);

        Assert.Equal(0, enters);
    }

    [Fact]
    public async Task Enter_is_pressed_again_at_most_twice()
    {
        var clock = new Clock();
        var enters = 0;
        var outcome = await PromptArrival.ConfirmAsync(
            () => false, () => false, () => Task.CompletedTask, false, TimeSpan.FromSeconds(20), "t",
            textWaitsUnsubmitted: () => Task.FromResult(true), pressEnter: () => enters++,
            pause: clock.Pause, utcNow: () => clock.Now);

        Assert.Equal(PromptArrivalOutcome.NotArrived, outcome);
        Assert.Equal(PromptArrival.MaxEnterAgain, enters);
    }

    [Fact]
    public async Task A_new_prompt_that_is_not_ours_word_for_word_is_arrived_altered_and_never_resent()
    {
        // Codex received a dictation with its dashes and curly quotes stripped and answered it; resending on the
        // mismatch sent it twice.
        var clock = new Clock();
        var resends = 0;
        var outcome = await PromptArrival.ConfirmAsync(
            () => false, () => true, () => { resends++; return Task.CompletedTask; }, true, TimeSpan.FromSeconds(20), "t",
            anyNewPrompt: () => true, pause: clock.Pause, utcNow: () => clock.Now);

        Assert.Equal(PromptArrivalOutcome.ArrivedAltered, outcome);
        Assert.Equal(0, resends);
    }

    [Fact]
    public void Any_prompt_after_the_offset_is_seen_and_an_older_one_is_not()
    {
        var file = Path.Combine(_dir, "c.jsonl");
        File.WriteAllText(file, "{\"type\":\"user\",\"message\":{\"content\":\"old\"}}\n");
        var offset = PromptArrival.EndOf(file);
        Assert.False(PromptArrival.AnyPromptAfter(file, offset));

        File.AppendAllText(file, "{\"type\":\"assistant\",\"message\":{\"content\":\"hi\"}}\n");
        Assert.False(PromptArrival.AnyPromptAfter(file, offset));

        File.AppendAllText(file, "{\"type\":\"user\",\"message\":{\"content\":\"new\"}}\n");
        Assert.True(PromptArrival.AnyPromptAfter(file, offset));
    }

    [Fact]
    public void A_Copilot_live_event_log_prompt_is_read()
    {
        Assert.Equal("hello there",
            PromptArrival.PromptTextOf("{\"type\":\"user.message\",\"data\":{\"content\":\"hello there\",\"transformedContent\":\"x\"}}"));
        Assert.Null(PromptArrival.PromptTextOf("{\"type\":\"assistant.message\",\"data\":{\"content\":\"ACK\"}}"));
    }

    // ---------------------------------------------------------------------------------------------
    // Review findings (Fable review, 24 September 2026)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task When_the_owner_typed_during_the_send_no_Enter_is_pressed_again_and_nothing_is_resent()
    {
        // Finding 1: the owner's keystrokes are not held during an ordinary send, so their draft would be submitted.
        var clock = new Clock();
        var enters = 0;
        var resends = 0;
        var outcome = await PromptArrival.ConfirmAsync(
            () => false, () => true, () => { resends++; return Task.CompletedTask; }, true, TimeSpan.FromSeconds(20), "t",
            textWaitsUnsubmitted: () => Task.FromResult(true), pressEnter: () => enters++, ownerTyped: () => true,
            pause: clock.Pause, utcNow: () => clock.Now);

        Assert.Equal(PromptArrivalOutcome.NotArrived, outcome);
        Assert.Equal(0, enters);
        Assert.Equal(0, resends);
    }

    [Fact]
    public void The_prompt_is_found_by_its_letters_and_digits_whatever_the_punctuation_and_invisible_characters()
    {
        // Finding 7: a benign difference must not read as a failed delivery.
        var file = Path.Combine(_dir, "p.jsonl");
        File.WriteAllText(file,
            "{\"type\":\"user\",\"message\":{\"content\":\"Token Q1 - check \\u2764 the  build, please\"}}\n");

        Assert.True(PromptArrival.Arrived(file, 0, "Token Q1: check ❤️ the build please!"));
        Assert.False(PromptArrival.Arrived(file, 0, "Token Q2 check the build please"));
    }

    [Fact]
    public void A_conversation_file_that_cannot_be_opened_is_not_yet_arrived_and_never_an_error()
    {
        // Finding 10: a scanner holding the file for a moment is "not yet", never a lost prompt.
        var file = Path.Combine(_dir, "locked.jsonl");
        File.WriteAllText(file, "{\"type\":\"user\",\"message\":{\"content\":\"hello\"}}\n");
        using var hold = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.False(PromptArrival.Arrived(file, 0, "hello"));
        Assert.False(PromptArrival.AnyPromptAfter(file, 0));
    }

    [Theory]
    [InlineData("abcabcabc", "abc", 3)]
    [InlineData("aaaa", "aa", 2)]
    [InlineData("xyz", "abc", 0)]
    [InlineData("abc", "", 0)]
    public void Copies_on_screen_are_counted_without_overlap(string hay, string needle, int expected)
    {
        // An old copy of the same text on screen must not pass for the new one's echo (measured on Codex under load).
        Assert.Equal(expected, TerminalSubmit.CountIn(hay, needle));
    }

    [Fact]
    public async Task A_quiet_beat_is_not_nudged_when_the_screen_does_not_show_the_text_waiting()
    {
        // Finding 5: guarded sends kept blind Enters; now an Enter again needs the screen to ask for it.
        var buffer = new CircularTerminalBuffer();
        var enters = 0;

        await Assert.ThrowsAsync<PromptNotSubmittedException>(() => SubmitVerifier.PressEnterAndVerifyAsync(
            buffer, b => { if (b.Length == 1 && b[0] == 0x0D) enters++; }, "t",
            attemptDelay: TimeSpan.FromMilliseconds(1), beatDelay: _ => Task.CompletedTask, nudgeOnlyWhen: () => false));

        Assert.Equal(1, enters);
    }

    // ---------------------------------------------------------------------------------------------
    // Grok: every conversation of the repository is watched, never "the newest"
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Every_Grok_transcript_of_the_repository_is_listed_and_no_other()
    {
        var repo = Path.Combine(_dir, "repo");
        var other = Path.Combine(_dir, "other");
        var sessions = Path.Combine(_dir, "sessions");
        foreach (var (cwd, id) in new[] { (repo, "a"), (repo, "b"), (other, "c") })
        {
            var d = Path.Combine(sessions, Uri.EscapeDataString(cwd), id);
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "chat_history.jsonl"), "");
        }

        var found = GrokSessionLocator.AllTranscripts(repo, sessions);

        Assert.Equal(2, found.Count);
        Assert.All(found, f => Assert.Contains(Uri.EscapeDataString(repo), f));
    }

    // ---------------------------------------------------------------------------------------------
    // The measured clear keys and the Codex loading gate
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Each_agent_gets_the_clear_key_measured_for_it_and_never_a_lone_Escape()
    {
        Assert.Equal(new byte[] { 0x15 }, ComposerClearKeys.For(AgentKind.Codex));
        Assert.Equal(new byte[] { 0x15 }, ComposerClearKeys.For(AgentKind.Pi));
        var claude = ComposerClearKeys.For(AgentKind.ClaudeCode, 100)!;
        Assert.Equal(0x05, claude[0]);
        Assert.Equal(116, claude.Count(b => b == 0x7F));
        Assert.Null(ComposerClearKeys.For(AgentKind.Gemini));
        foreach (var kind in Enum.GetValues<AgentKind>())
            Assert.NotEqual(new byte[] { 0x1B }, ComposerClearKeys.For(kind));
    }

    [Fact]
    public void Codex_is_not_ready_while_its_header_still_says_loading()
    {
        Assert.True(FirstPromptGate.IsCodexStillLoading("  model:       loading"));
        Assert.True(FirstPromptGate.IsCodexStillLoading("  directory:   D:\\repo  loading"));
        Assert.False(FirstPromptGate.IsCodexStillLoading("  model:       gpt-5.6-luna low   /model to change"));
    }
}
