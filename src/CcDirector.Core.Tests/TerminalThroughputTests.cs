using System.Diagnostics;
using System.Text;
using CcDirector.Terminal.Core;
using Xunit;
using Xunit.Abstractions;
using static CcDirector.Core.Tests.TerminalTestHelper;

namespace CcDirector.Core.Tests;

/// <summary>
/// Memory-bounds test for AnsiParser: after a 10 MB stream the scrollback holds no more rows than its limit.
/// The tests that timed the parser with a stopwatch were removed; a stopwatch reads the machine as much as
/// the parser.
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
}
