using System.Text;
using CcDirector.Terminal.Core;
using CcDirector.Terminal.Core.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Core.Tests;

/// <summary>
/// Tests for <see cref="TerminalSnapshotSerializer"/> - the attach-snapshot serializer that lets a
/// freshly-attached client (mobile PWA / Cockpit xterm) reconstruct a long-running session's screen
/// without replaying mid-stream raw bytes.
///
/// The central property is a ROUND TRIP: an authoritative parser P1 (the server, fed from byte 0)
/// holds the true screen; we serialize it to ANSI; a fresh parser P2 (the client) replays only that
/// snapshot; P2's screen must equal P1's. If that holds for every capture, the client always sees
/// exactly what the server sees, regardless of how the agent drew it.
///
/// The second group DOCUMENTS THE BUG this fixes: replaying a mid-stream byte window into a blank
/// terminal (the old behavior) reconstructs a torn screen for an incrementally-repainting agent
/// (Codex) - the confirmation dialog is missing - which is exactly why a snapshot is needed.
/// </summary>
public class TerminalSnapshotSerializerTests
{
    private readonly ITestOutputHelper _output;
    public TerminalSnapshotSerializerTests(ITestOutputHelper output) => _output = output;

    private static string LocateTestData(string name)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "TestData", name);
        if (File.Exists(dir)) return dir;
        var alt = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestData", name);
        if (File.Exists(alt)) return alt;
        throw new FileNotFoundException($"Test data not found: {name}", name);
    }

    // Build the attach snapshot from a parser's current screen, exactly as the server endpoint will.
    private static byte[] Snapshot(AnsiParser parser, List<TerminalCell[]> scrollback)
    {
        var cells = parser.ActiveCells;
        int cols = cells.GetLength(0);
        int rows = cells.GetLength(1);
        var (cc, cr) = parser.GetCursorPosition();
        return TerminalSnapshotSerializer.ToAnsi(
            scrollback, cells, cols, rows, cc, cr, parser.IsCursorVisible, parser.IsAlternateScreen);
    }

    // Replay a snapshot into a fresh "client" parser at the same geometry and return it.
    private static (AnsiParser Parser, TerminalCell[,] Cells) ReplayIntoClient(byte[] snapshot, int cols, int rows)
    {
        var cells = new TerminalCell[cols, rows];
        var scrollback = new List<TerminalCell[]>();
        var client = new AnsiParser(cells, cols, rows, scrollback, 5000);
        client.Parse(snapshot);
        return (client, cells);
    }

    private static void AssertActiveScreensMatch(AnsiParser expected, AnsiParser actual, string ctx)
    {
        var (er, ecr, ecc) = expected.SnapshotActiveRows();
        var (ar, acr, acc) = actual.SnapshotActiveRows();
        Assert.True(er.Length == ar.Length, $"{ctx}: row count {er.Length} vs {ar.Length}");
        for (int i = 0; i < er.Length; i++)
            Assert.True(er[i] == ar[i],
                $"{ctx}: row {i} differs\n  server: [{er[i]}]\n  client: [{ar[i]}]");
        Assert.True(ecr == acr && ecc == acc,
            $"{ctx}: cursor server=({ecr},{ecc}) client=({acr},{acc})");
    }

    private static void AssertActiveCellStylesMatch(TerminalCell[,] expected, TerminalCell[,] actual, int cols, int rows, string ctx)
    {
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                var e = expected[c, r];
                var a = actual[c, r];
                char ec = e.Character == '\0' ? ' ' : e.Character;
                char dc = a.Character == '\0' ? ' ' : a.Character;
                if (ec != dc)
                    Assert.Fail($"{ctx}: char[{c},{r}] '{ec}' vs '{dc}'");
                // Compare style only where a glyph is present (blank cells' latent style is invisible
                // and legitimately differs after a trailing-trim round trip).
                if (ec != ' ')
                {
                    if (e.Foreground != a.Foreground) Assert.Fail($"{ctx}: fg[{c},{r}] {e.Foreground} vs {a.Foreground}");
                    if (e.Background != a.Background) Assert.Fail($"{ctx}: bg[{c},{r}] {e.Background} vs {a.Background}");
                    if (e.Bold != a.Bold) Assert.Fail($"{ctx}: bold[{c},{r}] {e.Bold} vs {a.Bold}");
                    if (e.Italic != a.Italic) Assert.Fail($"{ctx}: italic[{c},{r}] {e.Italic} vs {a.Italic}");
                    if (e.Underline != a.Underline) Assert.Fail($"{ctx}: underline[{c},{r}] {e.Underline} vs {a.Underline}");
                    if (e.Faint != a.Faint) Assert.Fail($"{ctx}: faint[{c},{r}] {e.Faint} vs {a.Faint}");
                }
            }
    }

    // ---------- Synthetic screens: exact behaviors, no fixtures ----------

    [Fact]
    public void PlainText_RoundTrips()
    {
        var (p, _, sb) = TerminalTestHelper.CreateParser(40, 6);
        TerminalTestHelper.Parse(p, "Hello, world!\r\nsecond line\r\n> prompt");
        var (client, _) = ReplayIntoClient(Snapshot(p, sb), 40, 6);
        AssertActiveScreensMatch(p, client, "plain");
    }

    [Fact]
    public void Colors_And_Attributes_RoundTrip()
    {
        var (p, cells, sb) = TerminalTestHelper.CreateParser(40, 4);
        // red bold "ERR", default " ok ", green underline "done", truecolor bg, a faint suggestion
        TerminalTestHelper.Parse(p, "\x1b[1;31mERR\x1b[0m ok \x1b[4;32mdone\x1b[0m\r\n\x1b[48;2;10;20;30mBG\x1b[0m \x1b[2mfaint suggestion\x1b[22m");
        var snap = Snapshot(p, sb);
        var (client, ccells) = ReplayIntoClient(snap, 40, 4);
        AssertActiveScreensMatch(p, client, "styled");
        AssertActiveCellStylesMatch(cells, ccells, 40, 4, "styled-cells");
    }

    [Fact]
    public void BlankInteriorAndTrailingRows_ArePreserved()
    {
        var (p, _, sb) = TerminalTestHelper.CreateParser(20, 8);
        // content on row 0, gap, content on row 4, then blank rows to the bottom
        TerminalTestHelper.Parse(p, "top\r\n\r\n\r\n\r\nmiddle");
        var (client, _) = ReplayIntoClient(Snapshot(p, sb), 20, 8);
        AssertActiveScreensMatch(p, client, "gaps");
        var (rows, _, _) = client.SnapshotActiveRows();
        Assert.Equal("top", rows[0]);
        Assert.Equal("", rows[1]);
        Assert.Equal("middle", rows[4]);
    }

    [Fact]
    public void CursorPositionAndVisibility_RoundTrip()
    {
        var (p, _, sb) = TerminalTestHelper.CreateParser(30, 5);
        TerminalTestHelper.Parse(p, "line one\r\nline two\x1b[?25l\x1b[1;3H"); // hide cursor, move to row1 col3
        var snap = Snapshot(p, sb);
        var (client, _) = ReplayIntoClient(snap, 30, 5);
        AssertActiveScreensMatch(p, client, "cursor");
        Assert.Equal(p.IsCursorVisible, client.IsCursorVisible);
    }

    [Fact]
    public void FullWidthLine_DoesNotWrapOrShift()
    {
        var (p, _, sb) = TerminalTestHelper.CreateParser(10, 3);
        TerminalTestHelper.Parse(p, "1234567890\r\ntail"); // exactly cols wide on row 0
        var (client, _) = ReplayIntoClient(Snapshot(p, sb), 10, 3);
        AssertActiveScreensMatch(p, client, "fullwidth");
        var (rows, _, _) = client.SnapshotActiveRows();
        Assert.Equal("1234567890", rows[0]);
        Assert.Equal("tail", rows[1]);
    }

    [Fact]
    public void ScrollbackAboveViewport_RoundTrips()
    {
        var (p, _, sb) = TerminalTestHelper.CreateParser(20, 3, maxScrollback: 100);
        var text = new StringBuilder();
        for (int i = 0; i < 12; i++) text.Append($"row{i}\r\n"); // pushes early rows into scrollback
        text.Append("live");
        TerminalTestHelper.Parse(p, text.ToString());
        Assert.True(sb.Count > 0, "precondition: some scrollback accumulated");
        var (client, _) = ReplayIntoClient(Snapshot(p, sb), 20, 3);
        AssertActiveScreensMatch(p, client, "scrollback-active");
    }

    // ---------- Real captures: round trip must hold on true agent output ----------

    [Theory]
    [InlineData("claude-startup", 120, 30)]
    [InlineData("claude-stray-chars", 147, 50)]
    [InlineData("claude-stray-today", 147, 47)]
    [InlineData("claude-session-medium", 147, 50)]
    [InlineData("claude-session-large-47", 147, 47)]
    [InlineData("claude-session-huge-50", 147, 50)]
    public void Capture_ServerScreen_RoundTripsToClient(string baseName, int cols, int rows)
    {
        var bytes = File.ReadAllBytes(LocateTestData(baseName + ".bin"));
        var cells = new TerminalCell[cols, rows];
        var scrollback = new List<TerminalCell[]>();
        var server = new AnsiParser(cells, cols, rows, scrollback, 5000);
        server.Parse(bytes);

        var snap = Snapshot(server, scrollback);
        _output.WriteLine($"{baseName}: snapshot {snap.Length} bytes vs source {bytes.Length} bytes");
        var (client, ccells) = ReplayIntoClient(snap, cols, rows);

        AssertActiveScreensMatch(server, client, baseName);
        AssertActiveCellStylesMatch(cells, ccells, cols, rows, baseName + "-cells");
    }

    // ---------- The bug this fixes is documented end-to-end in SessionTerminalSnapshotTests
    // (Snapshot_ReconstructsScreen_AfterRingBufferWrappedPastEarlyContent): a mid-stream replay
    // into a blank client loses content the snapshot rebuilds. ----------

    [Fact]
    public void Snapshot_IsSmallRelativeToRawReplay()
    {
        // The snapshot is one screen (+ scrollback), not a 256KB-2MB byte window, so attach is cheap.
        var bytes = File.ReadAllBytes(LocateTestData("claude-session-huge-50.bin"));
        var cells = new TerminalCell[147, 50];
        var scrollback = new List<TerminalCell[]>();
        var server = new AnsiParser(cells, 147, 50, scrollback, 5000);
        server.Parse(bytes);
        var snap = Snapshot(server, scrollback);
        _output.WriteLine($"huge-50: raw={bytes.Length} snapshot={snap.Length}");
        Assert.True(snap.Length < bytes.Length,
            $"snapshot ({snap.Length}) should be far smaller than the 2MB raw replay ({bytes.Length})");
    }
}
