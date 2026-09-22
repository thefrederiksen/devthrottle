using CcDirector.Terminal.Core;
using Xunit;
using static CcDirector.Core.Tests.TerminalTestHelper;

namespace CcDirector.Core.Tests;

/// <summary>
/// A resize that lands WHILE the alternate screen is up must resize BOTH buffers.
///
/// THIS IS THE DIRECTOR'S DEAD-CLICK FREEZE, and it is worth saying exactly how a parser
/// bug became a window that ignored every click. Full-screen agents run on the alternate
/// screen. Entering it saves the primary grid aside and hands the parser a fresh grid of
/// the CURRENT size; <see cref="AnsiParser.UpdateGrid"/> then used to replace only the
/// grid it was drawing into, leaving the saved primary grid frozen at the size the window
/// had when the agent started. So: start an agent, resize the window, and the moment the
/// agent EXITS - which is what leaving the alternate screen means - the parser restored a
/// grid smaller than the dimensions it had just been told to use.
///
/// The terminal control renders whatever <see cref="AnsiParser.ActiveCells"/> points at,
/// over its own cols and rows. An undersized grid there is an IndexOutOfRangeException
/// thrown out of the control's Render, INSIDE the compositor's update pass - and that pass
/// is what rebuilds hit-testing for the whole window. The window kept painting the last
/// frame it had and routed no clicks anywhere until a resize forced a fresh pass.
///
/// The fix keeps the saved buffer the same size as the live one, so leaving the alternate
/// screen can only ever restore a grid that matches the dimensions the parser reports.
/// </summary>
public class AnsiParserAltScreenResizeTests
{
    private const string EnterAlt = "\x1b[?1049h";
    private const string LeaveAlt = "\x1b[?1049l";

    /// <summary>
    /// The exact sequence from the freeze: an agent takes the alternate screen, the owner
    /// grows the window, the agent exits. Before the fix the restored grid was still 80x24
    /// while the parser reported 120x40, and the first render past that threw.
    /// </summary>
    [Fact]
    public void LeaveAlternateScreen_AfterGrowingResize_RestoresAGridMatchingTheNewSize()
    {
        var (parser, _, _) = CreateParser(cols: 80, rows: 24);

        Parse(parser, EnterAlt);
        Assert.True(parser.IsAlternateScreen);

        parser.UpdateGrid(new TerminalCell[120, 40], 120, 40);
        Parse(parser, LeaveAlt);

        Assert.False(parser.IsAlternateScreen);
        Assert.Equal(120, parser.ActiveCells.GetLength(0));
        Assert.Equal(40, parser.ActiveCells.GetLength(1));
    }

    /// <summary>
    /// The same fault shrinking. A grid LARGER than the parser's dimensions does not throw,
    /// but it silently renders the wrong cells, so it is held to the same rule.
    /// </summary>
    [Fact]
    public void LeaveAlternateScreen_AfterShrinkingResize_RestoresAGridMatchingTheNewSize()
    {
        var (parser, _, _) = CreateParser(cols: 120, rows: 40);

        Parse(parser, EnterAlt);
        parser.UpdateGrid(new TerminalCell[60, 20], 60, 20);
        Parse(parser, LeaveAlt);

        Assert.Equal(60, parser.ActiveCells.GetLength(0));
        Assert.Equal(20, parser.ActiveCells.GetLength(1));
    }

    /// <summary>
    /// Resizing while the alternate screen is up must not cost the primary buffer its
    /// content: the shell's last screen is still there when the agent hands the terminal
    /// back, within the overlap of the old and new sizes.
    /// </summary>
    [Fact]
    public void LeaveAlternateScreen_AfterResize_KeepsThePrimaryBuffersText()
    {
        var (parser, _, _) = CreateParser(cols: 80, rows: 24);
        Parse(parser, "hello from the shell");

        Parse(parser, EnterAlt);
        parser.UpdateGrid(new TerminalCell[120, 40], 120, 40);
        Parse(parser, LeaveAlt);

        var restored = parser.ActiveCells;
        var text = new string(Enumerable.Range(0, "hello from the shell".Length)
            .Select(c => restored[c, 0].Character)
            .ToArray());
        Assert.Equal("hello from the shell", text);
    }

    /// <summary>
    /// Several resizes in a row while the alternate screen is up - the owner dragging the
    /// window edge - still leave exactly one correctly sized primary buffer to restore.
    /// </summary>
    [Fact]
    public void LeaveAlternateScreen_AfterRepeatedResizes_RestoresTheLastSize()
    {
        var (parser, _, _) = CreateParser(cols: 80, rows: 24);

        Parse(parser, EnterAlt);
        parser.UpdateGrid(new TerminalCell[100, 30], 100, 30);
        parser.UpdateGrid(new TerminalCell[90, 50], 90, 50);
        parser.UpdateGrid(new TerminalCell[147, 41], 147, 41);
        Parse(parser, LeaveAlt);

        Assert.Equal(147, parser.ActiveCells.GetLength(0));
        Assert.Equal(41, parser.ActiveCells.GetLength(1));
    }

    /// <summary>
    /// The no-alternate-screen case is unchanged: UpdateGrid takes the caller's array as is.
    /// </summary>
    [Fact]
    public void UpdateGrid_OnThePrimaryScreen_TakesTheCallersGrid()
    {
        var (parser, _, _) = CreateParser(cols: 80, rows: 24);

        var replacement = new TerminalCell[120, 40];
        parser.UpdateGrid(replacement, 120, 40);

        Assert.Same(replacement, parser.ActiveCells);
    }
}
