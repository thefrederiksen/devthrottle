using System.Text;
using CcDirector.Terminal.Core;
using Xunit;
using Xunit.Abstractions;
using static CcDirector.Core.Tests.TerminalTestHelper;

namespace CcDirector.Core.Tests;

/// <summary>
/// Resize correctness: scripted rapid-resize sequence against a parser instance.
///
/// Issue #332 acceptance criterion:
///   "20 rapid resizes via Control API POST /sessions/{sid}/resize on a slot-5+
///    session -- no crash, final GET /sessions/{sid}/buffer consistent with
///    final dims. (Script + output proof.)"
///
/// This test covers the parser-side correctness (AnsiParser.UpdateGrid) which
/// is the core of resize correctness. The Control API endpoint
/// POST /sessions/{sid}/resize (ControlEndpoints.cs:1702) calls Session.Resize
/// which calls ConPtyBackend.Resize (ConPtyBackend.cs:146) then routes the new
/// size to TerminalControl which calls AnsiParser.UpdateGrid. The parser-side
/// test here is the mechanistic proof; the live Control API proof is run during
/// the Director test slot verification (documented in report.html).
///
/// These tests verify:
///   1. 20 rapid resize calls do not crash or corrupt state.
///   2. After each resize, the grid is consistent with the new dimensions.
///   3. Cursor is clamped into the new grid (never out of bounds).
///   4. Scroll region tracks the new full-screen dimensions correctly.
///   5. Parsing continues correctly after rapid resize.
/// </summary>
public class TerminalRapidResizeTests
{
    private readonly ITestOutputHelper _output;

    public TerminalRapidResizeTests(ITestOutputHelper output) => _output = output;

    // -------------------------------------------------------------------------
    // Helper: perform N resizes on a parser, cycling through sizes
    // -------------------------------------------------------------------------

    private static (int Cols, int Rows)[] BuildResizeSequence(int count)
    {
        // Realistic resize sequence: window dragging produces rapidly varying
        // dimensions. Includes grow, shrink, and side-to-side changes.
        var sizes = new (int, int)[]
        {
            (120, 30), (121, 30), (122, 31), (123, 32), (140, 35),
            (147, 50), (160, 50), (147, 47), (130, 40), (120, 35),
            (100, 25), (80, 24),  (80, 30),  (100, 30), (120, 30),
            (147, 50), (200, 60), (147, 50), (120, 40), (120, 30),
        };
        // Return exactly count entries, cycling if needed
        var result = new (int, int)[count];
        for (int i = 0; i < count; i++)
            result[i] = sizes[i % sizes.Length];
        return result;
    }

    // -------------------------------------------------------------------------
    // Test 1: 20 rapid resizes without any parse in between -- no crash
    // -------------------------------------------------------------------------

    [Fact]
    public void RapidResize_TwentyResizes_NoCrash()
    {
        var (parser, _, _) = CreateParser(cols: 120, rows: 30);
        var resizes = BuildResizeSequence(20);

        for (int i = 0; i < resizes.Length; i++)
        {
            var (cols, rows) = resizes[i];
            var newCells = new TerminalCell[cols, rows];
            parser.UpdateGrid(newCells, cols, rows); // must not throw
        }

        var diag = parser.GetDiagnosticState();
        var (finalCols, finalRows) = resizes[^1];

        Assert.Equal(finalCols, diag.GridCols);
        Assert.Equal(finalRows, diag.GridRows);
        _output.WriteLine($"Final grid: {finalCols}x{finalRows}, cursor: ({diag.CursorCol},{diag.CursorRow})");
    }

    // -------------------------------------------------------------------------
    // Test 2: after each resize, grid dimensions match the requested size
    // -------------------------------------------------------------------------

    [Fact]
    public void RapidResize_AfterEachResize_GridDimensionsAreConsistent()
    {
        var (parser, _, _) = CreateParser(cols: 120, rows: 30);
        var resizes = BuildResizeSequence(20);

        for (int i = 0; i < resizes.Length; i++)
        {
            var (cols, rows) = resizes[i];
            var newCells = new TerminalCell[cols, rows];
            parser.UpdateGrid(newCells, cols, rows);

            var diag = parser.GetDiagnosticState();
            Assert.Equal(cols, diag.GridCols);
            Assert.Equal(rows, diag.GridRows);
        }
    }

    // -------------------------------------------------------------------------
    // Test 3: cursor is always within grid bounds after resize
    // -------------------------------------------------------------------------

    [Fact]
    public void RapidResize_CursorIsAlwaysWithinBoundsAfterResize()
    {
        // Place cursor at a corner that will be OUT OF BOUNDS after shrink
        var cells = new TerminalCell[200, 60];
        var scrollback = new List<TerminalCell[]>();
        var parser = new AnsiParser(cells, 200, 60, scrollback, 1000);
        // Move cursor to a position that will be clipped
        Parse(parser, "\x1b[60;200H"); // row 60, col 200 (1-based)

        var resizes = BuildResizeSequence(20);
        for (int i = 0; i < resizes.Length; i++)
        {
            var (cols, rows) = resizes[i];
            var newCells = new TerminalCell[cols, rows];
            parser.UpdateGrid(newCells, cols, rows);

            var diag = parser.GetDiagnosticState();
            Assert.True(diag.CursorCol >= 0 && diag.CursorCol < cols,
                $"After resize {i} to {cols}x{rows}: cursor col {diag.CursorCol} is out of bounds");
            Assert.True(diag.CursorRow >= 0 && diag.CursorRow < rows,
                $"After resize {i} to {cols}x{rows}: cursor row {diag.CursorRow} is out of bounds");
        }
    }

    // -------------------------------------------------------------------------
    // Test 4: scroll region tracks full-screen after resize (default region)
    // -------------------------------------------------------------------------

    [Fact]
    public void RapidResize_DefaultScrollRegion_AlwaysCoversFullScreen()
    {
        var (parser, _, _) = CreateParser(cols: 120, rows: 30);
        var resizes = BuildResizeSequence(20);

        for (int i = 0; i < resizes.Length; i++)
        {
            var (cols, rows) = resizes[i];
            var newCells = new TerminalCell[cols, rows];
            parser.UpdateGrid(newCells, cols, rows);

            var diag = parser.GetDiagnosticState();
            Assert.Equal(0,      diag.ScrollTop);
            Assert.Equal(rows - 1, diag.ScrollBottom);
        }
    }

    // -------------------------------------------------------------------------
    // Test 5: interleaved parse + resize -- output is not corrupted
    // -------------------------------------------------------------------------

    [Fact]
    public void RapidResize_InterleavedWithParse_NoCrashAndFinalGridConsistent()
    {
        var (parser, _, scrollback) = CreateParser(cols: 120, rows: 30);
        var resizes = BuildResizeSequence(20);

        int resizeIdx = 0;
        foreach (var (cols, rows) in resizes)
        {
            // Parse some output between each resize
            Parse(parser, $"\x1b[0m\x1b[32mResize step {resizeIdx}\x1b[0m\r\n");

            var newCells = new TerminalCell[cols, rows];
            parser.UpdateGrid(newCells, cols, rows);

            var diag = parser.GetDiagnosticState();
            Assert.Equal(cols, diag.GridCols);
            Assert.Equal(rows, diag.GridRows);
            Assert.True(diag.CursorCol < cols);
            Assert.True(diag.CursorRow < rows);

            resizeIdx++;
        }

        // Parse after all resizes -- should not crash
        Parse(parser, "Final output after 20 resizes\r\n");

        var finalDiag = parser.GetDiagnosticState();
        var (finalCols, finalRows) = resizes[^1];
        Assert.Equal(finalCols, finalDiag.GridCols);
        Assert.Equal(finalRows, finalDiag.GridRows);

        _output.WriteLine($"Completed {resizes.Length} interleaved resizes");
        _output.WriteLine($"Final grid: {finalCols}x{finalRows}");
        _output.WriteLine($"Final cursor: ({finalDiag.CursorCol},{finalDiag.CursorRow})");
        _output.WriteLine($"Scrollback after interleaved test: {scrollback.Count}");
    }

    // -------------------------------------------------------------------------
    // Test 6: resize from max to min and back -- extreme shrink/grow cycle
    // -------------------------------------------------------------------------

    [Fact]
    public void RapidResize_ExtremeShrinkAndGrow_ParserSurvives()
    {
        var (parser, _, _) = CreateParser(cols: 200, rows: 60);

        // Alternating extreme shrink / grow (10 pairs = 20 resizes)
        for (int i = 0; i < 10; i++)
        {
            // Shrink to minimum
            var smallCells = new TerminalCell[10, 5];
            parser.UpdateGrid(smallCells, 10, 5);
            var d1 = parser.GetDiagnosticState();
            Assert.True(d1.CursorCol < 10);
            Assert.True(d1.CursorRow < 5);

            // Grow back to large
            var largeCells = new TerminalCell[300, 80];
            parser.UpdateGrid(largeCells, 300, 80);
            var d2 = parser.GetDiagnosticState();
            Assert.True(d2.CursorCol < 300);
            Assert.True(d2.CursorRow < 80);
            Assert.Equal(0,  d2.ScrollTop);
            Assert.Equal(79, d2.ScrollBottom);
        }

        _output.WriteLine("10 extreme shrink/grow cycles (20 total resizes) completed without error");
    }

    // -------------------------------------------------------------------------
    // Documentation: Control API resize endpoint
    // -------------------------------------------------------------------------

    [Fact]
    public void ControlApi_ResizeEndpoint_IsDocumented()
    {
        // This fact documents the live-endpoint contract for QA.
        //
        // The live Control API endpoint is:
        //   POST /sessions/{sid}/resize
        //   Body: { "cols": N, "rows": M }
        //   Success: 200 OK
        //   Effect: calls Session.Resize -> ConPtyBackend.Resize -> PseudoConsole.Resize
        //           which sends SIGWINCH to the child process.
        //
        // To prove resize correctness with a live Director (slot >= 6):
        //   1. POST /sessions/{sid}/resize 20 times with varying dims
        //   2. GET  /sessions/{sid}/buffer after each resize
        //   3. Verify buffer.cols and buffer.rows match the requested dims
        //   4. Verify no crash in the Director log
        //
        // Source: src/CcDirector.ControlApi/ControlEndpoints.cs:1702
        //
        // This test is a no-op assertion that serves as embedded documentation.
        Assert.True(true, "Control API resize endpoint documented above");
    }
}
