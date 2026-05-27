using System.Text;
using CcDirector.Core.Wingman;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

/// <summary>
/// Tests for <see cref="PromptInputLineExtractor"/>. The extractor is heuristic;
/// these tests pin down the shapes we have actually observed (or expect) from
/// Claude Code's Ink TUI:
///
/// 1. Bordered input with text + mode line              -> returns the text
/// 2. Bordered input that is EMPTY + mode line          -> returns ""
/// 3. Borderless input with text + mode line            -> returns the text
/// 4. Mode line absent (no Claude Code frame visible)   -> returns null
/// 5. Stale mode-arrow line (">> ...") above the latest  -> not mistaken for input
/// 6. Two input frames in scrollback                    -> picks the most recent
/// 7. Raw bytes with ANSI escapes get cleaned end-to-end -> works
/// 8. Plan/Accept-edits anchors also resolve            -> works
/// 9. Whitespace-only buffer / null bytes               -> returns null
/// </summary>
public sealed class PromptInputLineExtractorTests
{
    [Fact]
    public void Bordered_input_with_text_returns_the_text()
    {
        var clean = """
                    ╭───────────────────────────────────────────────╮
                    │ > commit the cc-playwright changes too         │
                    ╰───────────────────────────────────────────────╯
                      >> bypass permissions on (shift+tab to cycle)
                    """;
        Assert.Equal("commit the cc-playwright changes too", PromptInputLineExtractor.ExtractFromCleanText(clean));
    }

    [Fact]
    public void Bordered_input_that_is_empty_returns_empty_string()
    {
        var clean = """
                    ╭───────────────────────────────────────────────╮
                    │ >                                              │
                    ╰───────────────────────────────────────────────╯
                      >> bypass permissions on (shift+tab to cycle)
                    """;
        Assert.Equal("", PromptInputLineExtractor.ExtractFromCleanText(clean));
    }

    [Fact]
    public void Borderless_input_with_text_returns_the_text()
    {
        var clean = """
                    > commit the cc-playwright changes too
                      >> bypass permissions on (shift+tab to cycle)
                    """;
        Assert.Equal("commit the cc-playwright changes too", PromptInputLineExtractor.ExtractFromCleanText(clean));
    }

    [Fact]
    public void No_mode_status_line_returns_null()
    {
        var clean = """
                    just some shell output here
                    > looks like a redirection but no mode line below
                    PS C:\repo>
                    """;
        Assert.Null(PromptInputLineExtractor.ExtractFromCleanText(clean));
    }

    [Fact]
    public void Mode_arrow_line_above_latest_is_not_mistaken_for_input()
    {
        // A stale mode line appears in scrollback above; the LATEST mode line is
        // what anchors. The extractor must walk up from the latest mode line and
        // find the matching ">" prompt — not interpret the ">>" arrow as input.
        var clean = """
                      >> bypass permissions on (shift+tab to cycle)
                    > later prompt content
                      >> plan mode on (shift+tab to cycle)
                    """;
        Assert.Equal("later prompt content", PromptInputLineExtractor.ExtractFromCleanText(clean));
    }

    [Fact]
    public void Two_input_frames_in_scrollback_picks_the_most_recent()
    {
        var clean = """
                    │ > older injected text │
                      >> bypass permissions on (shift+tab to cycle)
                    some agent output
                    more output
                    │ > newer injected text │
                      >> bypass permissions on (shift+tab to cycle)
                    """;
        Assert.Equal("newer injected text", PromptInputLineExtractor.ExtractFromCleanText(clean));
    }

    [Fact]
    public void Raw_bytes_with_ansi_escapes_are_cleaned_then_extracted()
    {
        // CSI color codes around the prompt; the extractor cleans then parses.
        var raw =
            "\x1b[2m╭───────╮\x1b[0m\n" +
            "\x1b[2m│\x1b[0m \x1b[1m> commit the cc-playwright changes too\x1b[0m \x1b[2m│\x1b[0m\n" +
            "\x1b[2m╰───────╯\x1b[0m\n" +
            "  \x1b[31m>>\x1b[0m bypass permissions on (shift+tab to cycle)\n";
        var bytes = Encoding.UTF8.GetBytes(raw);
        Assert.Equal("commit the cc-playwright changes too", PromptInputLineExtractor.ExtractClaudeCodeInputLine(bytes));
    }

    [Theory]
    [InlineData("plan mode on (shift+tab to cycle)")]
    [InlineData("accept edits on (shift+tab to cycle)")]
    [InlineData("auto-accept edits on (shift+tab to cycle)")]
    [InlineData("? for shortcuts")]
    public void Other_mode_anchors_also_resolve(string modeLine)
    {
        var clean = $"""
                    > injected suggestion
                      {modeLine}
                    """;
        Assert.Equal("injected suggestion", PromptInputLineExtractor.ExtractFromCleanText(clean));
    }

    [Fact]
    public void Null_or_empty_buffer_returns_null()
    {
        Assert.Null(PromptInputLineExtractor.ExtractClaudeCodeInputLine(null));
        Assert.Null(PromptInputLineExtractor.ExtractClaudeCodeInputLine(Array.Empty<byte>()));
        Assert.Null(PromptInputLineExtractor.ExtractFromCleanText(null));
        Assert.Null(PromptInputLineExtractor.ExtractFromCleanText(""));
        Assert.Null(PromptInputLineExtractor.ExtractFromCleanText("   \n\n  \n"));
    }

    [Fact]
    public void Prompt_line_above_window_is_not_picked_up()
    {
        // The "> ..." line is too far above the mode-status anchor — the extractor's
        // bounded look-back window should give up, not scan the whole scrollback.
        // 11 lines of gap, MaxLinesUpFromMode is 10.
        var sb = new StringBuilder();
        sb.AppendLine("> too far above");
        for (int i = 0; i < 11; i++) sb.AppendLine("filler line");
        sb.AppendLine("  >> bypass permissions on (shift+tab to cycle)");
        Assert.Null(PromptInputLineExtractor.ExtractFromCleanText(sb.ToString()));
    }

    [Fact]
    public void Trailing_whitespace_in_input_text_is_trimmed()
    {
        var clean = """
                    │ > commit the cc-playwright changes too                │
                      >> bypass permissions on (shift+tab to cycle)
                    """;
        Assert.Equal("commit the cc-playwright changes too", PromptInputLineExtractor.ExtractFromCleanText(clean));
    }

    // ---------- Grid + cursor aware extraction (ghost-suggestion fix) ----------
    //
    // These pin down the rule that an entry only counts when the edit cursor sits
    // at (or past) the end of the box text; a dim history/autocomplete suggestion
    // parks the cursor at the start, so its text must NOT be mirrored as an entry.
    // Indices: in "> Check the log", '>'=0 ' '=1 'C'=2 ... 'g'=14 (length 15),
    // so the text spans grid columns [2,15).

    private static readonly string[] BoxWithCheckTheLog =
    {
        "> Check the log",
        "  bypass permissions on (shift+tab to cycle)",
    };

    [Fact]
    public void Cursor_parked_at_start_of_box_is_a_suggestion_not_an_entry()
    {
        // Screenshot case: Claude Code offers "Check the log" as a dim suggestion
        // with the cursor parked on the first character. Nothing is authored.
        Assert.Equal("", PromptInputLineExtractor.ExtractUserAuthoredInput(BoxWithCheckTheLog, cursorRow: 0, cursorCol: 2));
    }

    [Fact]
    public void Cursor_at_end_of_box_is_a_real_entry()
    {
        Assert.Equal("Check the log", PromptInputLineExtractor.ExtractUserAuthoredInput(BoxWithCheckTheLog, cursorRow: 0, cursorCol: 15));
    }

    [Fact]
    public void Cursor_in_middle_keeps_only_text_left_of_cursor()
    {
        // User typed "Che"; Claude completes "ck the log" as a dim suggestion to its right.
        Assert.Equal("Che", PromptInputLineExtractor.ExtractUserAuthoredInput(BoxWithCheckTheLog, cursorRow: 0, cursorCol: 5));
    }

    [Fact]
    public void Cursor_on_another_row_takes_the_whole_box()
    {
        // Committed/injected text: the cursor is not editing the input line.
        Assert.Equal("Check the log", PromptInputLineExtractor.ExtractUserAuthoredInput(BoxWithCheckTheLog, cursorRow: 5, cursorCol: 0));
    }

    [Fact]
    public void Empty_box_returns_empty_regardless_of_cursor()
    {
        var rows = new[]
        {
            "> ",
            "  bypass permissions on (shift+tab to cycle)",
        };
        Assert.Equal("", PromptInputLineExtractor.ExtractUserAuthoredInput(rows, cursorRow: 0, cursorCol: 2));
    }

    [Fact]
    public void No_mode_line_returns_null_for_grid_extraction()
    {
        var rows = new[] { "> Check the log", "PS C:\\repo>" };
        Assert.Null(PromptInputLineExtractor.ExtractUserAuthoredInput(rows, cursorRow: 0, cursorCol: 15));
    }

    [Fact]
    public void Bordered_box_with_cursor_at_end_is_an_entry()
    {
        // '│'=0 ' '=1 '>'=2 ' '=3 'c'=4 ... text "commit the changes" spans [4,22).
        var rows = new[]
        {
            "╭───────────────────────────────────╮",
            "│ > commit the changes               │",
            "╰───────────────────────────────────╯",
            "  >> bypass permissions on (shift+tab to cycle)",
        };
        Assert.Equal("commit the changes", PromptInputLineExtractor.ExtractUserAuthoredInput(rows, cursorRow: 1, cursorCol: 22));
    }

    [Fact]
    public void Null_or_empty_rows_return_null()
    {
        Assert.Null(PromptInputLineExtractor.ExtractUserAuthoredInput(null, 0, 0));
        Assert.Null(PromptInputLineExtractor.ExtractUserAuthoredInput(System.Array.Empty<string>(), 0, 0));
    }
}
