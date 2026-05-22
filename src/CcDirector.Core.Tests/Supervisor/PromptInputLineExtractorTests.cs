using System.Text;
using CcDirector.Core.Supervisor;
using Xunit;

namespace CcDirector.Core.Tests.Supervisor;

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
}
