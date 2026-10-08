using System.Text;
using CcDirector.Core.Agents;
using CcDirector.Core.Drivers;
using CcDirector.Gateway.Contracts;
using CcDirector.Terminal.Core;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// CLAUDE CODE'S GREY SUGGESTION IS NOT TEXT IN THE COMPOSER (the Prompt Delivery mission, 7 October 2026).
///
/// After a turn, Claude Code draws its guess at the owner's next prompt FAINT inside the empty composer, with the cursor
/// straight after the glyph; Tab accepts it. The composer reader threw the faint attribute away and read the guess as
/// typed text. On session 110 that did two things to the owner's "go":
///  - the guess, "yes, go on 2.18.0 after the follow-up merges", already contained "go", so the echo check counted one
///    copy of the text in the composer BEFORE typing, and the composer then showing "go" never counted as its echo;
///  - the recovery cleared the composer correctly, Claude Code drew its guess again in the empty composer, and the
///    check that the clear worked read the guess as "still holds text after it was cleared" and refused the send.
///
/// The bytes below are Claude Code 2.1.293's own, taken from session 110's raw output on 8 October 2026 (the rule, the
/// prompt row, the footer and the cursor moves exactly as it wrote them; only the conversation above is left out).
/// </summary>
public sealed class ClaudeSuggestionComposerTests
{
    private const short Cols = 124;
    private const short Rows = 12;
    private const string Suggestion = "yes, go on 2.18.0 after the follow-up merges";

    private static readonly string Rule = new('─', Cols);

    /// <summary>The idle screen: a past answer, the composer holding nothing but Claude Code's faint suggestion.</summary>
    private static readonly string IdleWithSuggestion =
        "\x1b[2J\x1b[H● I'll check that they're still moving before I start the release.\r\n\r\n" +
        "\x1b[38;2;153;153;153m✻ Cooked for 13s · done 11:18 PM\x1b[K\x1b[m\r\n\x1b[K" +
        "\x1b[38;2;136;136;136m\r\n" + Rule +
        "\x1b[m❯ \x1b[2m" + Suggestion + "\x1b[22m\x1b[K\x1b[38;2;136;136;136m\r\n" + Rule +
        "\x1b[m  \x1b[38;2;255;107;128m⏵⏵ bypass permissions on \x1b[38;2;153;153;153m(shift+tab to cycle) · ← 1 agent\x1b[K\x1b[m\r\n" +
        "\x1b[K\r\n\x1b[K\x1b[6;3H\x1b[?25h";

    /// <summary>What Claude Code wrote when "go" was typed over the suggestion.</summary>
    private const string TypedGo = "\x1b[?25lgo\x1b[K\x1b[8;48H\x1b[K\x1b[6;5H\x1b[?25h";

    /// <summary>What it wrote when the composer was emptied again: the suggestion, faint, the cursor back at the start.</summary>
    private const string SuggestionRedrawn =
        "\x1b[?25l\x1b[2m\x1b[6;3H" + Suggestion + "\x1b[38;2;153;153;153m\x1b[22m\x1b[8;48H · ← 1 agent\x1b[6;3H\x1b[?25h\x1b[m";

    private static AnsiParser Parse(params string[] chunks)
    {
        var parser = new AnsiParser(new TerminalCell[Cols, Rows], Cols, Rows, new List<TerminalCell[]>(), 100);
        foreach (var chunk in chunks)
            parser.Parse(Encoding.UTF8.GetBytes(chunk));
        return parser;
    }

    /// <summary>The frame the Director takes from a live session: rows, cursor, and the rows without faint text.</summary>
    private static ScreenFrame LiveFrame(AnsiParser parser)
    {
        var (rows, row, col) = parser.SnapshotActiveRows();
        var (withoutFaint, _, _) = parser.SnapshotActiveRows(faintAsBlank: true);
        return new ScreenFrame(rows, row, col, parser.IsCursorVisible, withoutFaint);
    }

    [Fact]
    public void Parse_TextAfterSgr2_IsFaintUntilSgr22()
    {
        var parser = Parse("ab\x1b[2mcd\x1b[22mef");
        var cells = parser.ActiveCells;

        Assert.False(cells[1, 0].Faint);
        Assert.True(cells[2, 0].Faint);
        Assert.True(cells[3, 0].Faint);
        Assert.False(cells[4, 0].Faint);
    }

    [Fact]
    public void Parse_Sgr0_EndsFaint()
    {
        var parser = Parse("\x1b[2ma\x1b[mb");

        Assert.True(parser.ActiveCells[0, 0].Faint);
        Assert.False(parser.ActiveCells[1, 0].Faint);
    }

    [Fact]
    public void TerminalCell_FaintAndTheOtherStyles_AreIndependentBits()
    {
        var cell = new TerminalCell { Bold = true, Faint = true, WrapsToNextRow = true };

        cell.Faint = false;

        Assert.True(cell.Bold);
        Assert.False(cell.Italic);
        Assert.False(cell.Underline);
        Assert.True(cell.WrapsToNextRow);
        Assert.False(cell.Faint);
    }

    [Fact]
    public void SnapshotActiveRows_FaintAsBlank_LeavesTheFaintTextOut()
    {
        var parser = Parse(IdleWithSuggestion);

        var (rows, _, _) = parser.SnapshotActiveRows();
        var (withoutFaint, _, _) = parser.SnapshotActiveRows(faintAsBlank: true);

        Assert.Equal("❯ " + Suggestion, rows[5]);
        Assert.Equal("❯", withoutFaint[5]);
        Assert.Equal(rows[4], withoutFaint[4]);
    }

    [Fact]
    public void ReadComposerText_OnlyTheFaintSuggestion_ReadsEmpty()
    {
        var frame = LiveFrame(Parse(IdleWithSuggestion));

        var (reading, text) = DoorbellSafety.ReadComposerText(AgentKind.ClaudeCode, frame);

        Assert.Equal(ComposerReading.Empty, reading);
        Assert.Equal("", text);
    }

    [Fact]
    public void ReadComposerText_RowsAloneWithTheSuggestion_StillReadsItAsText()
    {
        // The defect, stated: without the faint attribute the guess is indistinguishable from typed text. A frame
        // built from rows alone (a fixture with no faintness recorded) keeps that reading, which defers rather than types.
        var parser = Parse(IdleWithSuggestion);
        var (rows, row, col) = parser.SnapshotActiveRows();

        var (reading, text) = DoorbellSafety.ReadComposerText(AgentKind.ClaudeCode, new ScreenFrame(rows, row, col, true));

        Assert.Equal(ComposerReading.HoldsText, reading);
        Assert.Equal(Suggestion, text);
    }

    [Fact]
    public void ReadComposerText_GoTypedOverTheSuggestion_ReadsExactlyGo()
    {
        var frame = LiveFrame(Parse(IdleWithSuggestion, TypedGo));

        var (reading, text) = DoorbellSafety.ReadComposerText(AgentKind.ClaudeCode, frame);

        Assert.Equal(ComposerReading.HoldsText, reading);
        Assert.Equal("go", text);
        Assert.True(DoorbellSafety.ComposerHoldsExactly(AgentKind.ClaudeCode, frame, "go"));
    }

    [Fact]
    public void ReadComposerText_AfterTheClearTheSuggestionReturns_ReadsEmpty()
    {
        // Session 110 at 23:49:10: the clear emptied the composer and Claude Code drew its guess again. This is the
        // frame that was reported as "the composer still holds text after it was cleared".
        var frame = LiveFrame(Parse(IdleWithSuggestion, TypedGo, SuggestionRedrawn));

        var (reading, text) = DoorbellSafety.ReadComposerText(AgentKind.ClaudeCode, frame);

        Assert.Equal(ComposerReading.Empty, reading);
        Assert.Equal("", text);
    }

    [Fact]
    public void ReadComposerText_OwnerTextDrawnAtNormalWeight_IsStillText()
    {
        // Only FAINT text is left out: the owner's own words, with the cursor moved back to the start, are still theirs.
        var parser = Parse(IdleWithSuggestion.Replace("\x1b[2m" + Suggestion, "make a new video of version 36"));
        var frame = LiveFrame(parser);

        var (reading, text) = DoorbellSafety.ReadComposerText(AgentKind.ClaudeCode, frame);

        Assert.Equal(ComposerReading.HoldsText, reading);
        Assert.Equal("make a new video of version 36", text);
    }

    [Fact]
    public void Check_IdleSessionShowingASuggestion_Rings()
    {
        // Fifteen idle sessions deferred their doorbell as "composer holds text" every fifteen seconds on 8 October
        // 2026, each showing nothing but a suggestion.
        var frame = LiveFrame(Parse(IdleWithSuggestion));
        var facts = new DoorbellFacts(AgentKind.ClaudeCode, Exited: false, DirectorSaysWorking: false, HasTerminalGrid: true,
            ProductMayHaveLeftText: false, Frames: [frame, frame]);

        var verdict = DoorbellSafety.Check(facts);

        Assert.True(verdict.Ring, verdict.Detail);
    }

    [Fact]
    public void ReadComposerText_RowsWithoutFaintOfAnotherSize_Throws()
    {
        var parser = Parse(IdleWithSuggestion);
        var (rows, row, col) = parser.SnapshotActiveRows();

        Assert.Throws<ArgumentException>(() =>
            DoorbellSafety.ReadComposerText(AgentKind.ClaudeCode, new ScreenFrame(rows, row, col, true, rows.Take(3).ToArray())));
    }

    [Fact]
    public void SameFrame_SameCharactersDifferentFaintness_IsNotTheSameFrame()
    {
        var suggestion = LiveFrame(Parse(IdleWithSuggestion));
        var typed = LiveFrame(Parse(IdleWithSuggestion.Replace("\x1b[2m" + Suggestion, Suggestion)));

        Assert.Equal(suggestion.Rows, typed.Rows);
        Assert.False(DoorbellSafety.SameFrame(suggestion, typed));
    }

    [Fact]
    public void DescribeKeys_ClaudeClearKeys_NamesEachKeyAndRun()
    {
        Assert.Equal("Ctrl+E, Backspace x64", TerminalSubmit.DescribeKeys(ComposerClearKeys.For(AgentKind.ClaudeCode, 2)!));
        Assert.Equal("Escape", TerminalSubmit.DescribeKeys([0x1B]));
    }
}
