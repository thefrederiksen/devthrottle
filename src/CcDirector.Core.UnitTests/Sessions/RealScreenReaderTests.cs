using System.Text;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// THE DIRECTOR READS THE REAL SCREEN (issue #3406, Voice Delivery mission, phase 6). Every reader that decides whether
/// text is in the composer, whether it echoed, or whether the agent is working reads the session's screen. That screen
/// used to be a fixed 220 by 40 grid that was never resized to the terminal, so a full-width row the agent let wrap by
/// itself ran together with the next row, and rows of earlier frames were left behind - and on 25 September 2026 a
/// session refused every send after one failed send, reading a stale row while its composer was empty on screen.
/// These tests draw a Claude Code screen the way the Windows pseudo console hands it over, at sizes other than 220 by 40.
/// </summary>
public sealed class RealScreenReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-real-screen-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _cleanup = new();

    public RealScreenReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var d in _cleanup) d.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private (Session Session, ScriptedAgentTerminal Terminal) NewClaudeSession(short cols, short rows, string composer = "",
        bool working = false)
    {
        var transcript = Path.Combine(_dir, Guid.NewGuid() + ".jsonl");
        File.WriteAllText(transcript, "");
        var terminal = new ScriptedAgentTerminal(AgentKind.ClaudeCode, transcript, _dir)
        {
            Working = working,
            ConPtyPaint = true,
            Width = cols,
            Height = rows,
        };
        terminal.SetComposer(composer);
        _cleanup.Add(terminal);
        var session = new Session(Guid.NewGuid(), _dir, _dir, null, terminal, SessionBackendType.ConPty) { AgentKind = AgentKind.ClaudeCode };
        _cleanup.Add(session);
        session.Resize(cols, rows);
        session.UpdateClaudeSessionPointer(Guid.NewGuid().ToString(), transcript, "test");
        terminal.StartDrawing();
        return (session, terminal);
    }

    private static ScreenFrame FrameOf(Session session)
    {
        var (rows, cursorRow, cursorCol, cursorVisible, _) = session.SnapshotLiveScreen();
        return new ScreenFrame(rows, cursorRow, cursorCol, cursorVisible);
    }

    private static string[] ExpectedBottom(int width, string composer, bool working) =>
    [
        working ? "" : "Done.",
        new string('─', width),
        ("❯ " + composer).TrimEnd(),
        new string('─', width),
        working ? "  esc to interrupt" : "  ? for shortcuts",
    ];

    // ===== the wedge (case 2f) ==========================================================================================

    [Fact]
    public async Task SendTextAsync_StaleRowNoLongerOnTheRealScreen_NextSendIsJudgedOnTheRealScreenAndGoesIn()
    {
        // Arrange: case 2f. An earlier send left 52 characters that may still be in the composer (the mark), and the
        // composer did hold them - drawn with the rule above it wrapping by itself. Since then the composer was
        // emptied, and only that row was repainted. On the real 80-column screen the composer is empty.
        const string orphan = "Reply with only the word OK. Marker, choral lemur 24";
        var (session, terminal) = NewClaudeSession(80, 24, composer: orphan);
        ComposerRetention.MarkMayHoldText(terminal, "ClaudeCode", orphan);
        terminal.SetComposer("");
        terminal.Redraw();

        // Act: the next send, and one after it.
        const string first = "Reply with only the word OK. Marker: probe quokka seventy.";
        const string second = "Reply with only the word OK. Marker: second quokka.";
        await session.SendTextAsync(first, SessionTestDoors.TestDoor);
        await session.SendTextAsync(second, SessionTestDoors.TestDoor);

        // Assert: both are judged on the screen as it is now and go in, once each.
        Assert.Equal(new[] { first, second }, terminal.Recorded);
        Assert.Equal("", terminal.Composer);
    }

    [Fact]
    public async Task SendTextAsync_FailedSendsCharactersStillUnreadWhenTheNextSendLooks_AreClearedNotSubmittedWithIt()
    {
        // Arrange: found by the delivery rig on a real Claude Code (phase 6). A send gave up with its text typed once and
        // never drawn - the agent was too starved to read it. When the next send looks, the composer is empty on
        // screen, but the characters are still waiting in the terminal's input, and are read before anything typed
        // after them. The Director called the text "provably gone", typed, and the two were submitted as one prompt.
        const string orphan = "Reply with only the word OK. Marker Q7365ED30FA";
        var (session, terminal) = NewClaudeSession(120, 30);
        ComposerRetention.MarkMayHoldText(terminal, "ClaudeCode", orphan);
        terminal.HoldUnreadInput(orphan);
        const string next = "Reply with only the word OK. Marker Q3062DE01F0";

        // Act
        await session.SendTextAsync(next, SessionTestDoors.TestDoor);

        // Assert: only the new prompt was submitted; the failed one never reached the agent.
        Assert.Equal(new[] { next }, terminal.Recorded);
    }

    [Fact]
    public async Task SendTextAsync_ComposerReallyHoldsTextTheClearCannotEmpty_RefusalSaysWhatWasReadAndTheNextSendLooksAgain()
    {
        // Arrange: an earlier send's text really is still in the composer, and the clear keys do not empty it.
        const string orphan = "Reply with only the word OK. Marker, choral lemur 24";
        var (session, terminal) = NewClaudeSession(80, 24, composer: orphan);
        ComposerRetention.MarkMayHoldText(terminal, "ClaudeCode", orphan);
        terminal.IgnoreClearKeys = true;
        const string first = "Reply with only the word OK. Marker: refused quokka.";

        // Act 1: a send is refused, and says what it read.
        var refused = await Assert.ThrowsAsync<ComposerNotAcceptingInputException>(
            () => session.SendTextAsync(first, SessionTestDoors.TestDoor));

        // Assert 1: nothing typed into text it cannot account for (#3290), and the refusal names the reading, the text
        // and the screen it read.
        Assert.Equal(orphan, terminal.Composer);
        Assert.Equal("", terminal.TypedText);
        Assert.Contains("reading=HoldsText", refused.Message);
        Assert.Contains($"text='{orphan}'", refused.Message);
        Assert.Contains("screen=80x24", refused.Message);

        // Act 2: the text leaves the composer (the owner sent or deleted it), and the next send is made.
        terminal.IgnoreClearKeys = false;
        terminal.SetComposer("");
        terminal.Redraw();
        const string second = "Reply with only the word OK. Marker: accepted quokka.";
        await session.SendTextAsync(second, SessionTestDoors.TestDoor);

        // Assert 2: judged on the screen as it is now, it goes in once.
        Assert.Equal(new[] { second }, terminal.Recorded);
    }

    // ===== every reader agrees with the real terminal ===================================================================

    [Fact]
    public void Readers_FrameWithRowsThatWrapByThemselves_ReadTheRealRowsNotMergedOnes()
    {
        // Arrange: the first paint lets both full-width rules wrap by themselves, on an 80 by 24 terminal.
        var (session, _) = NewClaudeSession(80, 24, composer: "hello there");

        // Act
        var rows = session.SnapshotScreenRows();
        var (rowsWithCursor, cursorRow, cursorCol) = session.SnapshotScreenRowsWithCursor();
        var frame = FrameOf(session);
        var coloured = session.SnapshotScreenColoredRows().Select(r => string.Concat(r.Select(s => s.Text)).TrimEnd()).ToArray();

        // Assert: the grid is the terminal's size, and its bottom five rows are exactly what the agent drew.
        Assert.Equal(24, rows.Length);
        Assert.All(rows, r => Assert.True(r.Length <= 80, $"a row wider than the terminal: '{r}'"));
        Assert.Equal(ExpectedBottom(80, "hello there", working: false), rows[19..24]);
        Assert.Equal(rows, rowsWithCursor);
        Assert.Equal((21, 13), (cursorRow, cursorCol));
        Assert.Equal(rows.Take(coloured.Length), coloured);
        // The composer reader, the echo witness and the working marker all read the same real rows.
        Assert.Equal((ComposerReading.HoldsText, "hello there"), DoorbellSafety.ReadComposerText(AgentKind.ClaudeCode, frame));
        Assert.True(DoorbellSafety.ComposerHoldsExactly(AgentKind.ClaudeCode, frame, "hello there"));
        Assert.False(DoorbellSafety.ShowsWorking(rows));
    }

    [Fact]
    public void Readers_AgentStopsWorkingAndOnlyChangedRowsArePainted_NoLeftOverWorkingMarker()
    {
        // Arrange: a working frame, then the agent finishes and repaints only the rows that changed.
        var (session, terminal) = NewClaudeSession(100, 30, working: true);
        Assert.True(DoorbellSafety.ShowsWorking(session.SnapshotScreenRows()));

        // Act
        terminal.Working = false;
        terminal.Redraw();

        // Assert: the doorbell's and the state detector's working marker is gone, and the composer reads empty.
        var rows = session.SnapshotScreenRows();
        Assert.False(DoorbellSafety.ShowsWorking(rows), string.Join(" | ", rows.Where(r => r.Length > 0)));
        Assert.Equal(ExpectedBottom(100, "", working: false), rows[25..30]);
        Assert.Equal(ComposerReading.Empty, DoorbellSafety.ReadComposer(AgentKind.ClaudeCode, FrameOf(session)));
        Assert.False(DoorbellSafety.ShowsWorking(session.SnapshotScreenRowsWithCursor().Rows));
    }

    [Fact]
    public void Readers_TerminalShrinksAndTheAgentRepaints_ReadTheSmallerScreenWithNoStaleRows()
    {
        // Arrange: a composer holding text on a 100 by 30 terminal.
        var (session, terminal) = NewClaudeSession(100, 30, composer: "draft before the resize");

        // Act: the terminal shrinks to 70 by 18 and the agent paints its frame again at the new size.
        session.Resize(70, 18);
        terminal.Width = 70;
        terminal.Height = 18;
        terminal.SetComposer("");
        terminal.RepaintAll();

        // Assert
        var rows = session.SnapshotScreenRows();
        Assert.Equal(18, rows.Length);
        Assert.All(rows, r => Assert.True(r.Length <= 70, $"a row wider than the terminal: '{r}'"));
        Assert.Equal(ExpectedBottom(70, "", working: false), rows[13..18]);
        Assert.DoesNotContain(rows, r => r.Contains("draft before the resize", StringComparison.Ordinal));
        Assert.Equal(ComposerReading.Empty, DoorbellSafety.ReadComposer(AgentKind.ClaudeCode, FrameOf(session)));
    }

    [Fact]
    public void Resize_OnTheAlternateScreen_KeepsTheAlternateScreenNotThePrimaryOne()
    {
        // Arrange: primary-screen text, then a full-screen agent on the alternate screen.
        var (session, terminal) = NewClaudeSession(80, 24);
        terminal.Buffer!.Write(Encoding.UTF8.GetBytes("\x1b[2J\x1b[1;1HPRIMARY SCREEN LINE"));
        terminal.Buffer!.Write(Encoding.UTF8.GetBytes("\x1b[?1049h\x1b[2J\x1b[1;1HALTERNATE SCREEN LINE"));

        // Act
        session.Resize(90, 26);

        // Assert: every reader, and the attach snapshot, still show the alternate screen.
        Assert.True(session.IsAlternateScreen);
        var rows = session.SnapshotScreenRows();
        Assert.Equal("ALTERNATE SCREEN LINE", rows[0]);
        Assert.DoesNotContain(rows, r => r.Contains("PRIMARY", StringComparison.Ordinal));
        var attach = Encoding.UTF8.GetString(session.GetTerminalSnapshot().Snapshot);
        Assert.Contains("ALTERNATE SCREEN LINE", attach);
        Assert.DoesNotContain("PRIMARY SCREEN LINE", attach);
    }

    [Fact]
    public void GetHtmlSnapshotSplit_ScrollbackFromANarrowerTerminal_RendersEachRowAtItsOwnWidth()
    {
        // Arrange: forty full lines scroll off an 80-column terminal, which is then widened to 120 columns.
        var (session, terminal) = NewClaudeSession(80, 24);
        for (var i = 0; i < 40; i++)
            terminal.Buffer!.Write(Encoding.UTF8.GetBytes($"\r\nscrolled line {i:D2} " + new string('x', 60)));
        session.Resize(120, 24);

        // Act
        var (scrollback, grid, count) = session.GetHtmlSnapshotSplit();

        // Assert: the Cockpit's "Raw terminal" render reads the real screen and its history, rows of any width.
        Assert.True(count > 0);
        Assert.Contains("scrolled line 00", scrollback);
        Assert.Contains("scrolled line 39", grid);
    }

    [Fact]
    public async Task Readers_WhileTheTerminalIsResizedAndPainted_NeverSeeAHalfResizedGrid()
    {
        // Arrange
        var (session, terminal) = NewClaudeSession(80, 24, composer: "concurrent");
        var sizes = new (short Cols, short Rows)[] { (80, 24), (60, 20), (120, 35), (90, 18) };
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(1.5);
        var failures = new List<string>();

        // Act: resize, paint and read at once.
        var resizer = Task.Run(() =>
        {
            for (var i = 0; DateTime.UtcNow < until; i++)
                session.Resize(sizes[i % sizes.Length].Cols, sizes[i % sizes.Length].Rows);
        });
        var painter = Task.Run(() =>
        {
            while (DateTime.UtcNow < until) terminal.RepaintAll();
        });
        var reader = Task.Run(() =>
        {
            while (DateTime.UtcNow < until)
            {
                var (rows, cursorRow, _, _, _) = session.SnapshotLiveScreen();
                var size = sizes.FirstOrDefault(s => s.Rows == rows.Length);
                if (size == default) { lock (failures) failures.Add($"{rows.Length} rows is no size the terminal had"); continue; }
                if (rows.Any(r => r.Length > size.Cols)) lock (failures) failures.Add($"a row wider than {size.Cols} columns");
                if (cursorRow >= rows.Length) lock (failures) failures.Add($"cursor row {cursorRow} outside {rows.Length} rows");
                _ = session.SnapshotScreenColoredRows();
                _ = session.GetHtmlSnapshotSplit();
            }
        });
        await Task.WhenAll(resizer, painter, reader);

        // Assert
        Assert.Empty(failures.Distinct());
    }
}
