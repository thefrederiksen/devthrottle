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
    private static ScreenFrame Load(string name) => DoorbellCaptures.Load(name);

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

    // ---------- Inspection 4, ruling 3: whitespace is text ----------

    [Theory]
    [InlineData("claude-idle-whitespace-draft-after-turn")]
    [InlineData("claude-idle-whitespace-draft-fresh")]
    public void Claude_spaces_after_the_glyph_are_text_even_though_the_row_is_trimmed(string capture)
    {
        var verdict = DoorbellSafety.Check(Quiet(AgentKind.ClaudeCode, capture));

        Assert.False(verdict.Ring);
        Assert.Equal("composer-holds-text", verdict.Reason);
    }

    [Theory]
    [InlineData("❯\u00A0 ")]
    [InlineData("❯\u00A0   ")]
    [InlineData("❯\u00A0\t")]
    [InlineData("❯  ")]
    public void Claude_an_untrimmed_row_with_whitespace_after_the_separator_is_text(string row)
    {
        var idle = Load("claude-idle-empty-after-turn");
        var frame = DoorbellCaptures.WithRow(idle, idle.CursorRow, row);

        Assert.Equal(ComposerReading.HoldsText, DoorbellSafety.ReadComposer(AgentKind.ClaudeCode, frame));
    }

    [Fact]
    public void Claude_the_separator_alone_is_still_empty()
    {
        var idle = Load("claude-idle-empty-after-turn");
        var frame = DoorbellCaptures.WithRow(idle, idle.CursorRow, "❯\u00A0");

        Assert.Equal(ComposerReading.Empty, DoorbellSafety.ReadComposer(AgentKind.ClaudeCode, frame));
    }

    [Fact]
    public void Claude_a_blank_continuation_row_holding_the_cursor_is_text()
    {
        // The owner pressed a new line and nothing else: the prompt row is bare, the next row is blank, and the
        // cursor is on that next row.
        var idle = Load("claude-idle-empty-after-turn");
        var rule = idle.Rows[idle.CursorRow - 1];
        var frame = DoorbellCaptures.WithRow(idle, idle.CursorRow - 2, rule);
        frame = DoorbellCaptures.WithRow(frame, idle.CursorRow - 1, "❯");
        frame = DoorbellCaptures.WithRow(frame, idle.CursorRow, "");
        frame = frame with { CursorRow = idle.CursorRow, CursorCol = 2 };

        Assert.Equal(ComposerReading.HoldsText, DoorbellSafety.ReadComposer(AgentKind.ClaudeCode, frame));
    }

    [Fact]
    public void Claude_an_untrimmed_continuation_row_of_spaces_is_text_wherever_the_cursor_is()
    {
        // A row that reaches the check untrimmed, holding only whitespace, is still a character on the screen.
        var idle = Load("claude-idle-empty-after-turn");
        var rule = idle.Rows[idle.CursorRow - 1];
        var frame = DoorbellCaptures.WithRow(idle, idle.CursorRow - 2, rule);
        frame = DoorbellCaptures.WithRow(frame, idle.CursorRow - 1, "❯");
        frame = DoorbellCaptures.WithRow(frame, idle.CursorRow, "  \t");
        frame = frame with { CursorRow = idle.CursorRow - 1, CursorCol = 2 };

        Assert.Equal(ComposerReading.HoldsText, DoorbellSafety.ReadComposer(AgentKind.ClaudeCode, frame));
    }

    [Fact]
    public void Claude_a_bare_prompt_with_the_cursor_hidden_is_unreadable_not_empty()
    {
        var idle = Load("claude-idle-empty-after-turn") with { CursorVisible = false };

        Assert.Equal(ComposerReading.NotFound, DoorbellSafety.ReadComposer(AgentKind.ClaudeCode, idle));
    }

    [Fact]
    public void Codex_spaces_typed_into_an_empty_composer_are_text()
    {
        var idle = Load("codex-idle-empty-placeholder");
        var frame = DoorbellCaptures.WithRow(idle, idle.CursorRow, "›") with { CursorCol = 5 };

        Assert.Equal(ComposerReading.HoldsText, DoorbellSafety.ReadComposer(AgentKind.Codex, frame));
    }

    [Fact]
    public void Codex_a_bare_prompt_with_the_cursor_at_the_start_is_empty()
    {
        var idle = Load("codex-idle-empty-placeholder");
        var frame = DoorbellCaptures.WithRow(idle, idle.CursorRow, "›");

        Assert.Equal(ComposerReading.Empty, DoorbellSafety.ReadComposer(AgentKind.Codex, frame));
    }

    [Fact]
    public void Codex_whitespace_after_the_separator_on_an_untrimmed_row_is_text()
    {
        var idle = Load("codex-idle-empty-placeholder");
        var frame = DoorbellCaptures.WithRow(idle, idle.CursorRow, "›  ");

        Assert.Equal(ComposerReading.HoldsText, DoorbellSafety.ReadComposer(AgentKind.Codex, frame));
    }

    // ---------- a wrapped Codex composer is the whole block, not one row (phase 6, review finding 1) ----------

    /// <summary>The prompt the real capture holds, as the reader must return it: the '›' row and the continuation
    /// row after their indents, joined by new lines (fixture codex-wrapped-composer, captured from Codex 0.157.1).</summary>
    private const string WrappedCapturePrompt =
        "Reply with only the word OK. Marker: fresh quokka seventy one two three four five six seven eight\nnine ten eleven twelve thirteen fourteen fifteen sixteen seventeen";

    [Fact]
    public void Codex_with_a_wrapped_prompt_in_the_composer_reads_the_whole_block_as_text()
    {
        // The cursor sits on the continuation row, which does not start with '›': the one-row reader answered
        // NotFound for exactly this frame, and every region judgement on a wrapped Codex prompt went blind.
        var frame = DoorbellCaptures.Load("codex-wrapped-composer", 30);

        var (reading, text) = DoorbellSafety.ReadComposerText(AgentKind.Codex, frame);

        Assert.Equal(ComposerReading.HoldsText, reading);
        Assert.Equal(WrappedCapturePrompt, text);
    }

    [Fact]
    public void Codex_with_a_wrapped_prompt_in_the_composer_defers_the_ring()
    {
        var frame = DoorbellCaptures.Load("codex-wrapped-composer", 30);

        var verdict = DoorbellSafety.Check(Quiet(AgentKind.Codex, frame, frame));

        Assert.False(verdict.Ring);
        Assert.Equal(FleetRingDeferReasons.ComposerHoldsText, verdict.Reason);
    }

    [Fact]
    public void Codex_holding_a_wrapped_doorbell_line_with_the_cursor_after_it_is_exactly_the_line()
    {
        // The doorbell line is longer than the captured screen is wide, so it wraps the same way. The frame is
        // DERIVED from the capture (the doorbell line typed in place of the prompt), because the doorbell ringer
        // needs exactly this shape on a screen narrower than its line.
        var line = FleetDoorbellLine.For(1);
        var cut = line.LastIndexOf(' ', 97); // the last space that fits the 100-column '›' row
        var rest = line[(cut + 1)..];
        var frame = DoorbellCaptures.Load("codex-wrapped-composer", 30);
        frame = DoorbellCaptures.WithRow(frame, 25, ("› " + line[..cut]).TrimEnd());
        frame = DoorbellCaptures.WithRow(frame, 26, "  " + rest);
        frame = frame with { CursorRow = 26, CursorCol = 2 + rest.Length, CursorVisible = true };

        Assert.True(DoorbellSafety.ComposerHoldsExactly(AgentKind.Codex, frame, line));
        var (reading, text) = DoorbellSafety.ReadComposerText(AgentKind.Codex, frame);
        Assert.Equal(ComposerReading.HoldsText, reading);
        Assert.Equal(line[..cut] + "\n" + rest, text);
    }

    // ---------- Inspection 5, ruling 1: exactly means exactly ----------

    private static ScreenFrame CodexHolding(string row, int cursorCol)
    {
        var idle = Load("codex-idle-empty-placeholder");
        return DoorbellCaptures.WithRow(idle, idle.CursorRow, row.TrimEnd()) with { CursorCol = cursorCol, CursorVisible = true };
    }

    [Fact]
    public void Codex_holding_exactly_the_line_with_the_cursor_after_it_is_exactly_the_line()
    {
        var line = FleetDoorbellLine.For(1);

        Assert.True(DoorbellSafety.ComposerHoldsExactly(AgentKind.Codex, CodexHolding("› " + line, 2 + line.Length), line));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\u00A0")]
    public void Codex_holding_the_line_and_one_whitespace_character_is_not_exactly_the_line(string added)
    {
        var line = FleetDoorbellLine.For(1);

        Assert.False(DoorbellSafety.ComposerHoldsExactly(AgentKind.Codex, CodexHolding("› " + line + added, 3 + line.Length), line));
    }

    [Fact]
    public void A_line_with_a_hidden_cursor_is_not_exactly_the_line()
    {
        var line = FleetDoorbellLine.For(1);
        var idle = Load("claude-idle-empty-after-turn");
        var frame = DoorbellCaptures.WithRow(idle, idle.CursorRow, "❯ " + line) with
        {
            CursorCol = 2 + line.Length, CursorVisible = false,
        };

        Assert.False(DoorbellSafety.ComposerHoldsExactly(AgentKind.ClaudeCode, frame, line));
        Assert.True(DoorbellSafety.ComposerHoldsExactly(AgentKind.ClaudeCode, frame with { CursorVisible = true }, line));
    }

    /// <summary>The Claude idle capture with the composer block rebuilt to hold <paramref name="composerRows"/>
    /// (the first is the prompt row), the cursor after the last one's visible text.</summary>
    private static ScreenFrame ClaudeHolding(params string[] composerRows)
    {
        var idle = Load("claude-idle-empty-after-turn");
        var prompt = idle.CursorRow;
        var top = prompt - (composerRows.Length - 1);
        var frame = DoorbellCaptures.WithRow(idle, top - 1, new string('─', 120));
        for (var k = 0; k < composerRows.Length; k++)
            frame = DoorbellCaptures.WithRow(frame, top + k, composerRows[k].TrimEnd());
        return frame with { CursorRow = prompt, CursorCol = composerRows[^1].TrimEnd().Length, CursorVisible = true };
    }

    [Fact]
    public void Part_of_the_line_is_not_exactly_the_line()
    {
        var line = FleetDoorbellLine.For(1);

        Assert.False(DoorbellSafety.ComposerHoldsExactly(AgentKind.ClaudeCode, ClaudeHolding("❯ " + line[..50]), line));
        Assert.True(DoorbellSafety.ComposerHoldsExactly(AgentKind.ClaudeCode, ClaudeHolding("❯ " + line), line));
    }

    [Fact]
    public void The_line_below_an_empty_prompt_row_is_not_exactly_the_line()
    {
        // The owner pressed Shift+Enter first; the line sits on the continuation row.
        var line = FleetDoorbellLine.For(1);

        Assert.False(DoorbellSafety.ComposerHoldsExactly(AgentKind.ClaudeCode, ClaudeHolding("❯", "  " + line), line));
    }

    [Fact]
    public void A_wrap_absorbs_at_most_one_space_of_the_line()
    {
        // The doorbell line has single spaces only; this pins the rule for any line.
        const string line = "wrapped  here";

        Assert.False(DoorbellSafety.ComposerHoldsExactly(AgentKind.ClaudeCode, ClaudeHolding("❯ wrapped", "   here"), line));
        Assert.False(DoorbellSafety.ComposerHoldsExactly(AgentKind.ClaudeCode, ClaudeHolding("❯ wrapped", "  here"), line));
        Assert.True(DoorbellSafety.ComposerHoldsExactly(AgentKind.ClaudeCode, ClaudeHolding("❯ wrapped", "  here"), "wrapped here"));
    }

    [Fact]
    public void A_space_the_owner_put_at_a_wrap_shows_as_a_wider_indent_and_is_not_exactly_the_line()
    {
        var line = FleetDoorbellLine.For(1);
        var at = line.IndexOf(' ', 40);

        Assert.True(DoorbellSafety.ComposerHoldsExactly(AgentKind.ClaudeCode, ClaudeHolding("❯ " + line[..at], "  " + line[(at + 1)..]), line));
        Assert.False(DoorbellSafety.ComposerHoldsExactly(AgentKind.ClaudeCode, ClaudeHolding("❯ " + line[..at], "   " + line[(at + 1)..]), line));
        Assert.False(DoorbellSafety.ComposerHoldsExactly(AgentKind.ClaudeCode, ClaudeHolding("❯ " + line[..at], " " + line[(at + 1)..]), line));
    }

    [Fact]
    public void A_space_the_owner_put_at_a_wrap_on_a_middle_row_is_not_exactly_the_line()
    {
        // On the last row the cursor would also move; on a middle row only the indent shows it.
        var line = FleetDoorbellLine.For(1);
        var a = line.IndexOf(' ', 30);
        var b = line.IndexOf(' ', 60);

        Assert.True(DoorbellSafety.ComposerHoldsExactly(AgentKind.ClaudeCode,
            ClaudeHolding("❯ " + line[..a], "  " + line[(a + 1)..b], "  " + line[(b + 1)..]), line));
        Assert.False(DoorbellSafety.ComposerHoldsExactly(AgentKind.ClaudeCode,
            ClaudeHolding("❯ " + line[..a], "   " + line[(a + 1)..b], "  " + line[(b + 1)..]), line));
    }

    [Fact]
    public void The_empty_cursor_column_is_the_one_both_captures_show()
    {
        Assert.Equal(2, DoorbellSafety.EmptyComposerCursorColumn);
        Assert.Equal(2, Load("claude-idle-empty-after-turn").CursorCol);
        Assert.Equal(2, Load("codex-idle-empty-placeholder").CursorCol);
    }
}
