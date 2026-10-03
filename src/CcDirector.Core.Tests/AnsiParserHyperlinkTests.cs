using System.Runtime.CompilerServices;
using System.Text;
using CcDirector.Terminal.Core;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// OSC 8 hyperlinks: a program wraps text in ESC ] 8 ; params ; URI ST ... ESC ] 8 ; ; ST and
/// every cell written in between carries the link (<see cref="TerminalCell.HyperlinkId"/>), with
/// the URI kept exactly as sent. The parser used to throw the payload away, so a link the program
/// named exactly was guessed from the text instead - and an OSC ended with ESC \ (Codex's ending)
/// was never dispatched at all.
/// </summary>
public class AnsiParserHyperlinkTests
{
    private const int Cols = 20;
    private const int Rows = 4;
    private const string Bel = "\x07";
    private const string St = "\x1b\\";

    private static string Open(string uri, string end = Bel) => "\x1b]8;;" + uri + end;
    private static string Close(string end = Bel) => "\x1b]8;;" + end;

    private static (AnsiParser parser, TerminalCell[,] cells, List<TerminalCell[]> scrollback) Feed(string text)
    {
        var cells = new TerminalCell[Cols, Rows];
        var scrollback = new List<TerminalCell[]>();
        var parser = new AnsiParser(cells, Cols, Rows, scrollback, 100);
        parser.Parse(Encoding.UTF8.GetBytes(text));
        return (parser, cells, scrollback);
    }

    private static string? LinkAt(AnsiParser parser, TerminalCell[,] cells, int col, int row) =>
        parser.GetHyperlink(cells[col, row].HyperlinkId);

    [Theory]
    [InlineData(Bel)]
    [InlineData(St)]
    public void LinkedText_CarriesTheExactUri_AndTextAfterTheCloseDoesNot(string end)
    {
        const string uri = "https://example.com/plain/x_(y)";
        var (parser, cells, _) = Feed("a " + Open(uri, end) + "docs" + Close(end) + " b");

        Assert.Null(LinkAt(parser, cells, 0, 0));
        for (int c = 2; c < 6; c++)
            Assert.Equal(uri, LinkAt(parser, cells, c, 0));
        Assert.Null(LinkAt(parser, cells, 6, 0));
        Assert.Null(LinkAt(parser, cells, 7, 0));
        Assert.Equal("a docs b", RowText(cells, 0).TrimEnd());
    }

    [Fact]
    public void ParamsBeforeTheUri_AreSkipped_AndTheUriKeepsItsSemicolons()
    {
        const string uri = "https://example.com/a;b=c";
        var (parser, cells, _) = Feed("\x1b]8;id=x1:foo=bar;" + uri + Bel + "go" + Close());

        Assert.Equal(uri, LinkAt(parser, cells, 0, 0));
        Assert.Equal(uri, LinkAt(parser, cells, 1, 0));
    }

    [Fact]
    public void NonAsciiUri_IsDecodedAsUtf8()
    {
        const string uri = "https://example.com/café/日本";
        var (parser, cells, _) = Feed(Open(uri, St) + "x" + Close(St));

        Assert.Equal(uri, LinkAt(parser, cells, 0, 0));
    }

    [Fact]
    public void LinkWrappedAcrossRows_CarriesTheWholeUriOnEveryRow()
    {
        const string uri = "https://example.com/a/very/long/path/that/wraps";
        var (parser, cells, _) = Feed(Open(uri) + uri + Close());

        Assert.Equal(uri, LinkAt(parser, cells, 0, 0));
        Assert.Equal(uri, LinkAt(parser, cells, Cols - 1, 0));
        Assert.Equal(uri, LinkAt(parser, cells, 0, 1));
        Assert.Equal(uri, LinkAt(parser, cells, uri.Length - 2 * Cols - 1, 2));
        Assert.Null(LinkAt(parser, cells, uri.Length - 2 * Cols, 2));
    }

    [Fact]
    public void EscFollowedByAnythingButBackslash_AbandonsTheOsc()
    {
        // ESC ] 8 ; ; uri ESC [ 0 m - the ESC ends the string without ST; nothing is linked
        // and the CSI after it still runs.
        var (parser, cells, _) = Feed("\x1b]8;;https://example.com/x\x1b[0mtext");

        Assert.Null(LinkAt(parser, cells, 0, 0));
        Assert.Equal("text", RowText(cells, 0).TrimEnd());
    }

    [Fact]
    public void OtherOscStrings_EndedWithSt_AreConsumedWithoutALink()
    {
        var (parser, cells, _) = Feed("\x1b]0;window title" + St + "hello");

        Assert.Equal("hello", RowText(cells, 0).TrimEnd());
        Assert.Null(LinkAt(parser, cells, 0, 0));
    }

    [Fact]
    public void SgrReset_DoesNotEndTheLink()
    {
        const string uri = "https://example.com/x";
        var (parser, cells, _) = Feed(Open(uri) + "\x1b[1mab\x1b[0mcd" + Close());

        for (int c = 0; c < 4; c++)
            Assert.Equal(uri, LinkAt(parser, cells, c, 0));
    }

    [Fact]
    public void ErasedCells_LoseTheLink()
    {
        const string uri = "https://example.com/x";
        var (parser, cells, _) = Feed(Open(uri) + "abcd" + Close() + "\r\x1b[2K");

        Assert.Null(LinkAt(parser, cells, 0, 0));
    }

    [Fact]
    public void FullReset_ClosesAnOpenLink()
    {
        var (parser, cells, _) = Feed(Open("https://example.com/x") + "ab\x1b" + "cafter");

        Assert.Equal("after", RowText(cells, 0).TrimEnd());
        Assert.Null(LinkAt(parser, cells, 0, 0));
    }

    [Fact]
    public void TheSameUri_IsOneId()
    {
        const string uri = "https://example.com/x";
        var (_, cells, _) = Feed(Open(uri) + "a" + Close() + " " + Open(uri) + "b" + Close());

        Assert.NotEqual(0, cells[0, 0].HyperlinkId);
        Assert.Equal(cells[0, 0].HyperlinkId, cells[2, 0].HyperlinkId);
    }

    [Fact]
    public void WhenTheTableFills_OldCellsLoseTheirLink_RatherThanPointAtANewOne()
    {
        var cells = new TerminalCell[Cols, Rows];
        var parser = new AnsiParser(cells, Cols, Rows, new List<TerminalCell[]>(), 100)
        {
            HyperlinkTableCapacity = 3, // id 0 plus two links
        };
        parser.Parse(Encoding.UTF8.GetBytes(
            Open("https://a.example/") + "a" + Close() +
            Open("https://b.example/") + "b" + Close() +
            Open("https://c.example/") + "c" + Close()));

        Assert.Null(parser.GetHyperlink(cells[0, 0].HyperlinkId));
        Assert.Null(parser.GetHyperlink(cells[1, 0].HyperlinkId));
        Assert.Equal("https://c.example/", parser.GetHyperlink(cells[2, 0].HyperlinkId));
    }

    [Fact]
    public void OversizedPayload_IsDroppedWhole_NotCut()
    {
        string uri = "https://example.com/" + new string('a', 5 * 1024);
        var (parser, cells, _) = Feed(Open(uri) + "x" + Close() + "y");

        Assert.Null(LinkAt(parser, cells, 0, 0));
        Assert.Equal("xy", RowText(cells, 0).TrimEnd());
    }

    [Fact]
    public void OversizedAddress_WhileALinkIsOpen_EndsThatLink()
    {
        // A program may go straight from one link to the next. The second address is too long to
        // keep, but its text must not be left opening the first one.
        string tooLong = "https://b.example/" + new string('a', 5 * 1024);
        var (parser, cells, _) = Feed(Open("https://a.example/") + "a" + Open(tooLong) + "b" + Close() + "c");

        Assert.Equal("https://a.example/", LinkAt(parser, cells, 0, 0));
        Assert.Null(LinkAt(parser, cells, 1, 0));
        Assert.Null(LinkAt(parser, cells, 2, 0));
    }

    [Fact]
    public void InvalidUtf8InTheAddress_OpensNothing_AndEndsTheOpenLink()
    {
        var cells = new TerminalCell[Cols, Rows];
        var parser = new AnsiParser(cells, Cols, Rows, new List<TerminalCell[]>(), 100);
        var bytes = new List<byte>(Encoding.UTF8.GetBytes(Open("https://a.example/") + "a" + "\x1b]8;;https://example.com/"));
        bytes.Add(0xFF);
        bytes.AddRange(Encoding.UTF8.GetBytes("x" + Bel + "t" + Close()));
        parser.Parse(bytes.ToArray());

        Assert.Equal("https://a.example/", LinkAt(parser, cells, 0, 0));
        Assert.Null(LinkAt(parser, cells, 1, 0));
    }

    [Fact]
    public void WhenTheTableFills_ARowLeavingTheRepaintFrame_EntersScrollbackWithoutAStaleLink()
    {
        // A program that repaints from the top (ESC [ H) has its previous frame kept as a snapshot,
        // whose rows are copied into scrollback when the screen moves up. That snapshot holds ids
        // too; when the table starts over they must not come to name a later link's address.
        const int cols = 20, rows = 6;
        var cells = new TerminalCell[cols, rows];
        var scrollback = new List<TerminalCell[]>();
        var parser = new AnsiParser(cells, cols, rows, scrollback, 100) { HyperlinkTableCapacity = 3 };
        static string Frame(params string[] lines)
        {
            var s = new StringBuilder("\x1b[H");
            for (int i = 0; i < lines.Length; i++)
                s.Append("\x1b[2K").Append(lines[i]).Append(i < lines.Length - 1 ? "\r\n" : "");
            return s.ToString();
        }

        parser.Parse(Encoding.UTF8.GetBytes(Frame(Open("https://a.example/") + "aaaa" + Close(), "l1", "l2", "l3", "l4", "l5")));
        parser.Parse(Encoding.UTF8.GetBytes(Frame("l1", "l2", "l3", "l4", "l5",
            Open("https://b.example/") + "bbbb" + Close() + Open("https://evil.example/") + "cccc" + Close())));
        parser.Parse(Encoding.UTF8.GetBytes("\x1b[H"));

        Assert.NotEmpty(scrollback);
        var row = scrollback[^1];
        Assert.Equal('a', row[0].Character);
        Assert.Null(parser.GetHyperlink(row[0].HyperlinkId));
    }

    [Fact]
    public void AnOscSplitAcrossTwoReads_IsStillOneLink()
    {
        const string uri = "https://example.com/café";
        byte[] all = Encoding.UTF8.GetBytes(Open(uri, St) + "x" + Close(St));
        for (int cut = 1; cut < all.Length; cut++)
        {
            var cells = new TerminalCell[Cols, Rows];
            var parser = new AnsiParser(cells, Cols, Rows, new List<TerminalCell[]>(), 100);
            parser.Parse(all[..cut]);
            parser.Parse(all[cut..]);
            Assert.Equal(uri, LinkAt(parser, cells, 0, 0));
        }
    }

    [Fact]
    public void ACellStaysSixteenBytes()
    {
        Assert.Equal(16, Unsafe.SizeOf<TerminalCell>());
    }

    private static string RowText(TerminalCell[,] cells, int row)
    {
        var sb = new StringBuilder();
        for (int c = 0; c < Cols; c++)
            sb.Append(cells[c, row].Character == '\0' ? ' ' : cells[c, row].Character);
        return sb.ToString();
    }
}
