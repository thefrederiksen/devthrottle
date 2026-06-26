using CcDirector.Terminal.Core;
using Xunit;
using static CcDirector.Core.Tests.TerminalTestHelper;

namespace CcDirector.Core.Tests;

/// <summary>
/// Synchronized output (DEC private mode ?2026, the Begin/End Synchronized Update
/// protocol). Modern TUIs - including the xAI Grok CLI - bracket every frame in
/// ?2026h ... ?2026l so the terminal can render the whole frame at once. The owning
/// terminal control reads <see cref="AnsiParser.InSynchronizedUpdate"/> to hold its
/// repaint while a frame is open and paint once it closes; without that, Grok's
/// mid-frame clear-and-redraw paints a half-built frame and flickers on screen.
/// These tests pin the parser-side flag that decision depends on.
/// </summary>
public class AnsiParserSynchronizedOutputTests
{
    [Fact]
    public void FreshParser_IsNotInSynchronizedUpdate()
    {
        var (parser, _, _) = CreateParser();

        Assert.False(parser.InSynchronizedUpdate);
    }

    [Fact]
    public void BeginSynchronizedUpdate_2026h_SetsFlag()
    {
        var (parser, _, _) = CreateParser();

        Parse(parser, "\x1b[?2026h");

        Assert.True(parser.InSynchronizedUpdate);
    }

    [Fact]
    public void EndSynchronizedUpdate_2026l_ClearsFlag()
    {
        var (parser, _, _) = CreateParser();

        Parse(parser, "\x1b[?2026h");
        Assert.True(parser.InSynchronizedUpdate);

        Parse(parser, "\x1b[?2026l");
        Assert.False(parser.InSynchronizedUpdate);
    }

    [Fact]
    public void CompleteFrame_LeavesParserNotInSynchronizedUpdate()
    {
        // A whole frame in one chunk: begin, draw, end. After it the control is free
        // to paint, so the flag must be clear.
        var (parser, cells, _) = CreateParser();

        Parse(parser, "\x1b[?2026hHELLO\x1b[?2026l");

        Assert.False(parser.InSynchronizedUpdate);
        // Content drawn inside the frame is still written to the grid (we do not buffer
        // cell writes; we only defer the repaint).
        Assert.Equal('H', cells[0, 0].Character);
        Assert.Equal('O', cells[4, 0].Character);
    }

    [Fact]
    public void OpenFrameWithoutEnd_RemainsInSynchronizedUpdate()
    {
        // Begin and partial draw with no matching end (a frame split across feeds). The
        // control should still be holding its repaint until the end arrives.
        var (parser, _, _) = CreateParser();

        Parse(parser, "\x1b[?2026hPARTIAL");

        Assert.True(parser.InSynchronizedUpdate);
    }

    [Fact]
    public void GrokIdleHeartbeat_EmptyBeginEndPairs_LeaveFrameClosed()
    {
        // The exact idle stream observed from the Grok CLI: empty begin/end pairs
        // repeated forever. Each pair opens and immediately closes a frame, so the
        // parser must end not-in-frame - otherwise the control would stop repainting.
        var (parser, _, _) = CreateParser();

        Parse(parser, string.Concat(System.Linq.Enumerable.Repeat("\x1b[?2026h\x1b[?2026l", 50)));

        Assert.False(parser.InSynchronizedUpdate);
    }

    [Fact]
    public void FullReset_ClearsSynchronizedUpdate()
    {
        var (parser, _, _) = CreateParser();

        Parse(parser, "\x1b[?2026h");
        Assert.True(parser.InSynchronizedUpdate);

        // RIS (full reset). Split so C#'s \x escape does not consume the 'c'.
        Parse(parser, "\x1b" + "c");

        Assert.False(parser.InSynchronizedUpdate);
    }
}
