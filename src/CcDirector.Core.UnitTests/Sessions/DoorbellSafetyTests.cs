using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Core.Drivers;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// The doorbell's safety check (the Message Load mission, slice 2) against REAL captured screens - see
/// TestData/doorbell/README.md for where each one came from. The check decides whether one line may be typed
/// into a session; every "deferred" here is a case where typing would have landed on the owner's words, on a
/// running turn, or on a menu.
/// </summary>
public sealed class DoorbellSafetyTests
{
    private sealed class Capture
    {
        public string[] Rows { get; set; } = [];
        public int CursorRow { get; set; }
        public int CursorCol { get; set; }
        public bool CursorVisible { get; set; }
    }

    private static ScreenFrame Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "doorbell", name + ".json");
        var capture = JsonSerializer.Deserialize<Capture>(File.ReadAllText(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(capture.Rows.Length == 40, $"{name} should be a full 40-row capture");
        return new ScreenFrame(capture.Rows, capture.CursorRow, capture.CursorCol, capture.CursorVisible);
    }

    /// <summary>The facts a quiet, healthy session presents, with the same capture as both frames.</summary>
    private static DoorbellFacts Quiet(AgentKind agent, string capture) =>
        Quiet(agent, Load(capture), Load(capture));

    private static DoorbellFacts Quiet(AgentKind agent, ScreenFrame first, ScreenFrame second) => new(
        agent,
        Exited: false,
        DirectorSaysWorking: false,
        HasTerminalGrid: true,
        ProductMayHaveLeftText: false,
        Frames: [first, second]);

    // ---------- The four cases the brief names ----------

    [Theory]
    [InlineData("claude-idle-empty-after-turn")]
    [InlineData("claude-idle-empty-fresh")]
    public void Claude_idle_with_an_empty_composer_is_rung(string capture)
    {
        var verdict = DoorbellSafety.Check(Quiet(AgentKind.ClaudeCode, capture));

        Assert.True(verdict.Ring, verdict.Detail);
        Assert.Equal("", verdict.Reason);
    }

    [Theory]
    [InlineData("claude-owner-text")]
    [InlineData("claude-owner-text-two-lines")]
    [InlineData("claude-collapsed-paste")]
    [InlineData("claude-slash-command-typed")]
    public void Claude_with_anything_in_the_composer_is_deferred(string capture)
    {
        var verdict = DoorbellSafety.Check(Quiet(AgentKind.ClaudeCode, capture));

        Assert.False(verdict.Ring);
        Assert.Equal(FleetRingDeferReasons.ComposerHoldsText, verdict.Reason);
    }

    [Fact]
    public void Claude_mid_turn_is_deferred_even_though_its_composer_is_drawn_empty()
    {
        var frame = Load("claude-working");

        // The trap this rule exists for: mid-turn, the composer itself reads empty.
        Assert.Equal(ComposerReading.Empty, DoorbellSafety.ReadComposer(AgentKind.ClaudeCode, frame));

        var verdict = DoorbellSafety.Check(Quiet(AgentKind.ClaudeCode, frame, frame));

        Assert.False(verdict.Ring);
        Assert.Equal(FleetRingDeferReasons.Working, verdict.Reason);
    }

    [Theory]
    [InlineData("claude-menu-model-picker")]
    [InlineData("claude-menu-trust-folder")]
    public void Claude_with_a_menu_open_is_deferred(string capture)
    {
        var verdict = DoorbellSafety.Check(Quiet(AgentKind.ClaudeCode, capture));

        Assert.False(verdict.Ring);
        Assert.Equal(FleetRingDeferReasons.MenuOpen, verdict.Reason);
    }

    // ---------- Codex ----------

    [Fact]
    public void Codex_showing_only_its_placeholder_is_rung()
    {
        var verdict = DoorbellSafety.Check(Quiet(AgentKind.Codex, "codex-idle-empty-placeholder"));

        Assert.True(verdict.Ring, verdict.Detail);
    }

    [Fact]
    public void Codex_with_owner_text_is_deferred()
    {
        var verdict = DoorbellSafety.Check(Quiet(AgentKind.Codex, "codex-owner-text"));

        Assert.Equal(FleetRingDeferReasons.ComposerHoldsText, verdict.Reason);
    }

    [Theory]
    [InlineData("codex-menu-rate-limit")]
    [InlineData("codex-menu-trust-folder")]
    public void Codex_with_a_menu_open_is_deferred(string capture)
    {
        var verdict = DoorbellSafety.Check(Quiet(AgentKind.Codex, capture));

        Assert.Equal(FleetRingDeferReasons.MenuOpen, verdict.Reason);
    }

    [Fact]
    public void Codex_placeholder_text_with_the_cursor_after_it_is_owner_text()
    {
        // The owner typed the placeholder's exact words: the cursor is at the end of them, not at the start.
        var idle = Load("codex-idle-empty-placeholder");
        var typed = idle with { CursorCol = 2 + "Ask Codex to do anything".Length };

        Assert.Equal(ComposerReading.HoldsText, DoorbellSafety.ReadComposer(AgentKind.Codex, typed));
    }

    [Fact]
    public void Codex_mid_turn_is_deferred_on_its_footer_written_not_captured()
    {
        // No mid-turn Codex screen was captured (usage limit). This row is written from the Codex footer shape.
        var idle = Load("codex-idle-empty-placeholder");
        var rows = idle.Rows.ToArray();
        rows[idle.CursorRow - 1] = "• Working (4s • esc to interrupt)";
        var verdict = DoorbellSafety.Check(Quiet(AgentKind.Codex, idle with { Rows = rows }, idle with { Rows = rows }));

        Assert.Equal(FleetRingDeferReasons.Working, verdict.Reason);
    }

    // ---------- The rules before the screen ----------

    [Fact]
    public void An_exited_session_is_never_rung_whatever_the_screen_shows()
    {
        var facts = Quiet(AgentKind.ClaudeCode, "claude-idle-empty-after-turn") with { Exited = true };

        Assert.Equal(FleetRingDeferReasons.Exited, DoorbellSafety.Check(facts).Reason);
    }

    [Fact]
    public void The_directors_own_working_state_defers_even_on_an_idle_screen()
    {
        var facts = Quiet(AgentKind.ClaudeCode, "claude-idle-empty-after-turn") with { DirectorSaysWorking = true };

        Assert.Equal(FleetRingDeferReasons.Working, DoorbellSafety.Check(facts).Reason);
    }

    [Fact]
    public void Text_the_product_may_have_left_defers_even_on_an_empty_looking_screen()
    {
        var facts = Quiet(AgentKind.ClaudeCode, "claude-idle-empty-after-turn") with { ProductMayHaveLeftText = true };

        Assert.Equal(FleetRingDeferReasons.ComposerHoldsText, DoorbellSafety.Check(facts).Reason);
    }

    [Fact]
    public void A_session_without_a_rendered_terminal_is_unreadable()
    {
        var facts = Quiet(AgentKind.ClaudeCode, "claude-idle-empty-after-turn") with { HasTerminalGrid = false };

        Assert.Equal(FleetRingDeferReasons.ScreenUnreadable, DoorbellSafety.Check(facts).Reason);
    }

    [Fact]
    public void One_frame_is_not_enough()
    {
        var facts = Quiet(AgentKind.ClaudeCode, "claude-idle-empty-after-turn") with
        {
            Frames = [Load("claude-idle-empty-after-turn")],
        };

        Assert.Equal(FleetRingDeferReasons.ScreenUnreadable, DoorbellSafety.Check(facts).Reason);
    }

    [Fact]
    public void Both_frames_must_be_empty_an_empty_then_typed_pair_is_deferred()
    {
        var facts = Quiet(AgentKind.ClaudeCode, Load("claude-idle-empty-after-turn"), Load("claude-owner-text"));

        Assert.Equal(FleetRingDeferReasons.ComposerHoldsText, DoorbellSafety.Check(facts).Reason);
    }

    [Fact]
    public void A_blank_screen_is_unreadable_not_empty()
    {
        var blank = new ScreenFrame(Enumerable.Repeat("", 40).ToArray(), 0, 0, true);

        Assert.Equal(FleetRingDeferReasons.ScreenUnreadable,
            DoorbellSafety.Check(Quiet(AgentKind.ClaudeCode, blank, blank)).Reason);
    }

    [Theory]
    [InlineData(AgentKind.Pi)]
    [InlineData(AgentKind.Gemini)]
    [InlineData(AgentKind.OpenCode)]
    public void An_agent_without_a_composer_reader_is_unreadable(AgentKind agent)
    {
        Assert.Equal(FleetRingDeferReasons.ScreenUnreadable,
            DoorbellSafety.Check(Quiet(agent, "claude-idle-empty-after-turn")).Reason);
    }

    [Fact]
    public void A_codex_screen_is_not_read_as_a_claude_composer()
    {
        Assert.Equal(FleetRingDeferReasons.ScreenUnreadable,
            DoorbellSafety.Check(Quiet(AgentKind.ClaudeCode, "codex-idle-empty-placeholder")).Reason);
    }

    [Fact]
    public void A_prompt_row_not_framed_by_rules_is_not_a_composer()
    {
        // Take the idle screen and remove the rule above the prompt: what is left is a transcript line.
        var idle = Load("claude-idle-empty-after-turn");
        var rows = idle.Rows.ToArray();
        var prompt = Array.FindLastIndex(rows, r => r.StartsWith('❯'));
        rows[prompt - 1] = "some transcript text";

        Assert.Equal(ComposerReading.NotFound,
            DoorbellSafety.ReadComposer(AgentKind.ClaudeCode, idle with { Rows = rows }));
    }

    [Fact]
    public void Claude_text_on_a_continuation_row_alone_is_text()
    {
        // The owner pressed a newline first and typed on the second row: the prompt row itself is blank.
        var draft = Load("claude-owner-text-two-lines");
        var rows = draft.Rows.ToArray();
        var prompt = Array.FindLastIndex(rows, r => r.StartsWith('❯'));
        rows[prompt] = "❯";

        Assert.Equal(ComposerReading.HoldsText,
            DoorbellSafety.ReadComposer(AgentKind.ClaudeCode, draft with { Rows = rows }));
    }

    [Fact]
    public void Codex_with_the_cursor_hidden_has_no_composer()
    {
        // A drawn menu hides the cursor; a '›' row seen then is not the composer, even one that looks idle.
        var idle = Load("codex-idle-empty-placeholder");

        Assert.Equal(ComposerReading.NotFound,
            DoorbellSafety.ReadComposer(AgentKind.Codex, idle with { CursorVisible = false }));
    }

    [Fact]
    public void A_blank_screen_says_it_rendered_nothing()
    {
        var blank = new ScreenFrame(Enumerable.Repeat("", 40).ToArray(), 0, 0, true);

        Assert.Equal("the screen rendered nothing", DoorbellSafety.CheckFrame(AgentKind.ClaudeCode, blank).Detail);
    }

    // ---------- The line itself ----------

    [Fact]
    public void The_doorbell_is_one_fixed_line_with_the_count_and_the_read_command()
    {
        Assert.Equal(
            "[DevThrottle doorbell] 1 fleet message is waiting for you. To read, run: cc-devthrottle message inbox",
            FleetDoorbellLine.For(1));
        Assert.Equal(
            "[DevThrottle doorbell] 3 fleet messages are waiting for you. To read, run: cc-devthrottle message inbox",
            FleetDoorbellLine.For(3));
        Assert.DoesNotContain('\n', FleetDoorbellLine.For(12));
    }
}
