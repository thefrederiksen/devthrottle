using CcDirector.Core.Agents;
using CcDirector.Core.Drivers;
using CcDirector.Core.Wingman;
using CcDirector.Terminal.Core;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

/// <summary>
/// Covers the fix for issue 766: agents whose idle terminal never goes byte-silent (Grok)
/// must not be pinned to Working forever. Two deterministic pieces are tested here - the
/// per-agent capability flag (approach C) and the screen-body extraction that the detector
/// uses instead of raw bytes (approach A). The detector's timer machinery itself is exercised
/// live in the slot build, not in this pure unit test.
/// </summary>
public sealed class ContinuousIdleStateTests
{
    // ---- Approach C: the per-agent capability flag ----

    [Fact]
    public void Grok_declares_continuous_idle_output()
    {
        // Grok's TUI repaints an animated footer forever, so it is the one built-in agent that
        // opts into the screen-body idle rule.
        Assert.True(AgentDrivers.For(AgentKind.Grok).EmitsContinuousIdleOutput);
    }

    [Theory]
    [InlineData(AgentKind.ClaudeCode)]
    [InlineData(AgentKind.Codex)]
    [InlineData(AgentKind.Gemini)]
    [InlineData(AgentKind.OpenCode)]
    [InlineData(AgentKind.Pi)]
    [InlineData(AgentKind.Copilot)]
    public void Other_agents_keep_the_byte_only_rule(AgentKind kind)
    {
        // Everyone else goes byte-silent when idle, so they stay on the cheap byte rule and must
        // NOT opt into the screen-body path.
        Assert.False(AgentDrivers.For(kind).EmitsContinuousIdleOutput);
    }

    // ---- Approach A: screen-body extraction (rows above the cursor) ----

    [Fact]
    public void Body_is_rows_above_the_cursor()
    {
        var rows = new[] { "answer line 1", "answer line 2", "> composer", "shortcuts -" };
        Assert.True(TerminalStateDetector.TryExtractBody(rows, cursorRow: 2, out var body));
        Assert.Equal("answer line 1\nanswer line 2", body);
    }

    [Fact]
    public void Footer_only_change_does_not_change_the_body()
    {
        // The spinner glyph cycles in the footer (at/below the cursor) while the answer body is
        // unchanged. Both frames must extract the SAME body, so the detector sees no activity.
        var frameA = new[] { "the answer", "> composer", "shortcuts -" };
        var frameB = new[] { "the answer", "> composer", "shortcuts \\" };
        Assert.True(TerminalStateDetector.TryExtractBody(frameA, 1, out var bodyA));
        Assert.True(TerminalStateDetector.TryExtractBody(frameB, 1, out var bodyB));
        Assert.Equal(bodyA, bodyB);
    }

    [Fact]
    public void Body_change_above_the_cursor_is_detected()
    {
        // A new answer token lands above the composer: the bodies must differ, so the detector
        // re-arms Working.
        var before = new[] { "partial ans", "> composer", "shortcuts -" };
        var after = new[] { "partial answer", "> composer", "shortcuts -" };
        Assert.True(TerminalStateDetector.TryExtractBody(before, 1, out var b1));
        Assert.True(TerminalStateDetector.TryExtractBody(after, 1, out var b2));
        Assert.NotEqual(b1, b2);
    }

    [Fact]
    public void No_body_when_cursor_at_top()
    {
        // Cursor at row 0 (or -1 when there is no grid): the body cannot be isolated, so extraction
        // fails and the caller treats the frame as activity rather than risk a false idle.
        var rows = new[] { "> composer", "shortcuts -" };
        Assert.False(TerminalStateDetector.TryExtractBody(rows, cursorRow: 0, out _));
        Assert.False(TerminalStateDetector.TryExtractBody(rows, cursorRow: -1, out _));
        Assert.False(TerminalStateDetector.TryExtractBody(System.Array.Empty<string>(), cursorRow: 5, out _));
    }

    // ---- AnsiParser.SnapshotActiveRows: the alternate-screen correctness fix ----

    [Fact]
    public void SnapshotActiveRows_reads_the_primary_grid()
    {
        var parser = MakeParser(out _);
        parser.Parse(System.Text.Encoding.UTF8.GetBytes("hello world"));
        var (rows, _, _) = parser.SnapshotActiveRows();
        Assert.Equal("hello world", rows[0]);
    }

    [Fact]
    public void SnapshotActiveRows_reflects_alternate_screen_content()
    {
        // This is the core of the bug: on the alternate screen the parser swaps its internal grid,
        // so a caller iterating the array it was handed at construction sees the FROZEN primary
        // grid. SnapshotActiveRows reads the live grid, so it must show the alt-screen text.
        var parser = MakeParser(out _);
        parser.Parse(System.Text.Encoding.UTF8.GetBytes("primary content"));

        // Enter the alternate screen (ESC[?1049h) and draw different content.
        parser.Parse(System.Text.Encoding.UTF8.GetBytes("\x1b[?1049h\x1b[H\x1b[2Jalt screen body"));

        Assert.True(parser.IsAlternateScreen);
        var (rows, _, _) = parser.SnapshotActiveRows();
        var joined = string.Join("\n", rows);
        Assert.Contains("alt screen body", joined);
        Assert.DoesNotContain("primary content", joined);
    }

    [Fact]
    public void SnapshotActiveRows_returns_cursor_position()
    {
        var parser = MakeParser(out _);
        parser.Parse(System.Text.Encoding.UTF8.GetBytes("\x1b[3;5H")); // row 3, col 5 (1-based)
        var (_, cursorRow, cursorCol) = parser.SnapshotActiveRows();
        Assert.Equal(2, cursorRow); // 0-based
        Assert.Equal(4, cursorCol);
    }

    private static AnsiParser MakeParser(out TerminalCell[,] cells)
    {
        const int cols = 80, rows = 24;
        cells = new TerminalCell[cols, rows];
        var scrollback = new System.Collections.Generic.List<TerminalCell[]>();
        return new AnsiParser(cells, cols, rows, scrollback, 1000);
    }
}
