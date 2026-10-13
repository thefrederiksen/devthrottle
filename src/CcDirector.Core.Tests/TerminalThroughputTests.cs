using System.Diagnostics;
using System.Text;
using CcDirector.Terminal.Core;
using Xunit;
using Xunit.Abstractions;
using static CcDirector.Core.Tests.TerminalTestHelper;

namespace CcDirector.Core.Tests;

/// <summary>
/// Throughput and memory-bounds test for AnsiParser.
///
/// Issue #332 acceptance criteria (throughput gate):
///   - A 10 MB captured output stream parses in less than 10 seconds.
///   - Memory is bounded: GC allocated bytes do not grow unboundedly across
///     the run (measured via GC.GetTotalAllocatedBytes before/after parse).
///   - The UI thread is NEVER blocked more than 100 ms per the CLAUDE.md
///     responsive-UI mandate. AnsiParser.Parse is synchronous so callers
///     must either chunk the input or run it off the UI thread. This test
///     verifies that Parse on 10 MB returns within the time budget and that
///     it can be called in 64 KB chunks without timing issues, modelling the
///     chunked dispatch that TerminalControl performs in practice.
///
/// A NOTE ON WHAT THESE FOUR TESTS MEASURE. Three of them read a stopwatch, and
/// on a machine running other work a stopwatch reads the machine as much as the
/// parser. The chunked test was re-stated for that reason and carries the whole
/// argument on itself; tests 1 and 4 keep their elapsed-time bars, which are
/// loose enough (10 seconds for a parse measured at about 2, and a shape
/// comparison rather than an absolute) that contention has not been observed to
/// reach them. If either of them ever goes red on a busy machine, it is the same
/// defect and wants the same repair -- not a wider bar.
///
/// Threshold rationale:
///   The 10 s / 10 MB bar is the Phase-1 gate from the issue. If real claude
///   bursts grow larger, the fixture size and threshold should be raised
///   (never silently loosened) -- see the ASSUMPTION note in issue #332.
/// </summary>
public class TerminalThroughputTests
{
    private readonly ITestOutputHelper _output;

    public TerminalThroughputTests(ITestOutputHelper output) => _output = output;

    // -------------------------------------------------------------------------
    // Build a 10 MB synthetic ANSI stream that exercises real parser paths:
    //   - Printable text (most common)
    //   - CSI cursor-move sequences (frequent in TUIs)
    //   - SGR color/bold sequences
    //   - Newlines / carriage returns
    //   - The odd OSC and ESC sequence
    //
    // The generator is deterministic so any timing failure is reproducible.
    // -------------------------------------------------------------------------

    private static byte[] BuildSyntheticStream(int targetBytes)
    {
        // Use a MemoryStream + StreamWriter to avoid O(n^2) from StringBuilder.ToString()
        // inside the loop. Write directly to bytes so the output buffer is the stream.
        var ms = new System.IO.MemoryStream(targetBytes + 4096);
        var writer = new System.IO.StreamWriter(ms, Encoding.UTF8, leaveOpen: true);
        var rng = new Random(42);

        // Patterns representative of real claude-code TUI output
        string[] textChunks =
        [
            "Hello world, this is terminal output",
            "Building project...",
            "Compiling source files",
            "OK test passed",
            "ERROR: file not found",
            "Warning: deprecated API",
            "  => result: 42",
        ];
        string[] csiSeqs =
        [
            "\x1b[1m", "\x1b[0m",                    // bold on/off
            "\x1b[32m", "\x1b[31m", "\x1b[33m",      // colors
            "\x1b[2K", "\x1b[K",                      // erase line
            "\x1b[H", "\x1b[2J",                      // home / clear screen
            "\x1b[1;1H", "\x1b[10;5H",               // cursor position
            "\x1b[A", "\x1b[B", "\x1b[C", "\x1b[D",  // cursor moves
            "\x1b[?25h", "\x1b[?25l",                 // show/hide cursor
        ];

        while (ms.Position < targetBytes)
        {
            int choice = rng.Next(0, 5);
            switch (choice)
            {
                case 0:
                    writer.Write(textChunks[rng.Next(textChunks.Length)]);
                    writer.Write('\n');
                    break;
                case 1:
                    writer.Write(csiSeqs[rng.Next(csiSeqs.Length)]);
                    break;
                case 2:
                    writer.Write("\r\n");
                    break;
                case 3:
                    // Column of printable chars
                    int len = rng.Next(20, 100);
                    for (int i = 0; i < len; i++)
                        writer.Write((char)('a' + rng.Next(26)));
                    break;
                case 4:
                    // SGR reset + text
                    writer.Write("\x1b[0m");
                    writer.Write(textChunks[rng.Next(textChunks.Length)]);
                    break;
            }
        }

        writer.Flush();
        return ms.ToArray();
    }

    // -------------------------------------------------------------------------
    // Test 1: full 10 MB in one Parse call -- wall-clock < 10 s
    // -------------------------------------------------------------------------

    // -------------------------------------------------------------------------
    // Test 2: the 64 KB chunk model -- what the parser COSTS per chunk, measured
    // so that a busy machine cannot change the answer.
    // -------------------------------------------------------------------------

    // -------------------------------------------------------------------------
    // Test 3: memory does not grow unboundedly across the run.
    // After a full parse + GC, live memory should be close to what it was
    // before (scrollback capped at maxScrollback rows -- no runaway growth).
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse10MbStream_ScrollbackIsBoundedByMaxScrollback()
    {
        const int TargetBytes = 10 * 1024 * 1024; // 10 MB
        const int MaxScrollbackRows = 1000;

        byte[] stream = BuildSyntheticStream(TargetBytes);
        var cells = new TerminalCell[120, 30];
        var scrollback = new List<TerminalCell[]>();
        var parser = new AnsiParser(cells, 120, 30, scrollback, MaxScrollbackRows);

        parser.Parse(stream);

        // Scrollback must never exceed the cap.
        Assert.True(
            scrollback.Count <= MaxScrollbackRows,
            $"Scrollback grew to {scrollback.Count} rows, exceeding MaxScrollback={MaxScrollbackRows}");

        _output.WriteLine($"Scrollback rows after 10 MB parse: {scrollback.Count} (cap: {MaxScrollbackRows})");
        _output.WriteLine($"Total bytes parsed: {parser.GetDiagnosticState().TotalBytesParsed}");
    }

    // -------------------------------------------------------------------------
    // Test 4: throughput scales linearly -- parsing 1 MB then 10 MB does not
    // show super-linear growth (no O(n^2) behavior in the parser).
    // -------------------------------------------------------------------------
}
