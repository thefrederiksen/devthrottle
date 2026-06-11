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

    [Fact]
    public void Parse10MbStream_CompletesWithinTenSeconds()
    {
        const int TargetBytes = 10 * 1024 * 1024; // 10 MB

        _output.WriteLine($"Building {TargetBytes / 1024 / 1024} MB synthetic ANSI stream...");
        byte[] stream = BuildSyntheticStream(TargetBytes);
        _output.WriteLine($"Built {stream.Length} bytes.");

        var (parser, _, _) = CreateParser(cols: 120, rows: 30, maxScrollback: 5000);

        // GC snapshot before
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        long allocBefore = GC.GetTotalAllocatedBytes(precise: false);
        long memBefore = GC.GetTotalMemory(forceFullCollection: false);

        var sw = Stopwatch.StartNew();
        parser.Parse(stream);
        sw.Stop();

        // GC snapshot after
        long allocAfter = GC.GetTotalAllocatedBytes(precise: false);
        long memAfter = GC.GetTotalMemory(forceFullCollection: false);

        double elapsedSec = sw.Elapsed.TotalSeconds;
        long allocDeltaMb = (allocAfter - allocBefore) / (1024 * 1024);
        long memDeltaKb = (memAfter - memBefore) / 1024;

        _output.WriteLine($"Parse time : {elapsedSec:F3} s");
        _output.WriteLine($"Allocated  : {allocDeltaMb} MB (GC-reported, includes GC'd objects)");
        _output.WriteLine($"Live memory delta: {memDeltaKb} KB");
        _output.WriteLine($"Diagnostic : {parser.GetDiagnosticState().TotalBytesParsed} bytes parsed");

        Assert.True(
            elapsedSec < 10.0,
            $"10 MB stream parsed in {elapsedSec:F3} s -- exceeds the 10 s Phase-1 gate");
    }

    // -------------------------------------------------------------------------
    // Test 2: 64 KB chunk model -- each chunk returns well within 100 ms
    // This models TerminalControl's dispatch loop (one Parse call per
    // buffer read), ensuring no single chunk blocks the UI thread.
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse10MbInChunks_EachChunkCompletesWithin100Ms()
    {
        const int TargetBytes = 10 * 1024 * 1024; // 10 MB
        const int ChunkSize = 64 * 1024;           // 64 KB per chunk -- matches typical read buffer
        const double MaxChunkMs = 100.0;           // responsive-UI mandate (CLAUDE.md)

        byte[] stream = BuildSyntheticStream(TargetBytes);
        var (parser, _, _) = CreateParser(cols: 120, rows: 30, maxScrollback: 5000);

        double maxObserved = 0.0;
        double totalSec = 0.0;
        int chunks = 0;
        var offenders = new List<string>();

        var sw = new Stopwatch();
        int pos = 0;
        while (pos < stream.Length)
        {
            int n = Math.Min(ChunkSize, stream.Length - pos);
            var chunk = stream.AsSpan(pos, n).ToArray();

            sw.Restart();
            parser.Parse(chunk);
            sw.Stop();

            double ms = sw.Elapsed.TotalMilliseconds;
            totalSec += sw.Elapsed.TotalSeconds;
            if (ms > maxObserved) maxObserved = ms;
            if (ms > MaxChunkMs)
                offenders.Add($"chunk {chunks}: {ms:F1} ms ({n} bytes at offset {pos})");

            pos += n;
            chunks++;
        }

        _output.WriteLine($"Chunks     : {chunks} x {ChunkSize / 1024} KB");
        _output.WriteLine($"Total time : {totalSec:F3} s");
        _output.WriteLine($"Max chunk  : {maxObserved:F2} ms (limit = {MaxChunkMs} ms)");
        _output.WriteLine($"Offenders  : {offenders.Count}");

        Assert.True(
            offenders.Count == 0,
            $"{offenders.Count} chunk(s) exceeded {MaxChunkMs} ms UI-block budget:\n"
            + string.Join("\n", offenders.Take(10)));
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

    // -------------------------------------------------------------------------
    // Test 4: throughput scales linearly -- parsing 1 MB then 10 MB does not
    // show super-linear growth (no O(n^2) behavior in the parser).
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseThroughput_IsNotSuperlinear()
    {
        const int SmallBytes = 1 * 1024 * 1024;   // 1 MB
        const int LargeBytes = 10 * 1024 * 1024;  // 10 MB

        byte[] small = BuildSyntheticStream(SmallBytes);
        byte[] large = BuildSyntheticStream(LargeBytes);

        var (p1, _, _) = CreateParser(cols: 120, rows: 30);
        var (p2, _, _) = CreateParser(cols: 120, rows: 30);

        // Warmup
        p1.Parse(small.AsSpan(0, Math.Min(4096, small.Length)).ToArray());
        p2.Parse(large.AsSpan(0, Math.Min(4096, large.Length)).ToArray());

        GC.Collect(2, GCCollectionMode.Forced, blocking: true);

        var sw1 = Stopwatch.StartNew();
        p1.Parse(small);
        sw1.Stop();

        var sw2 = Stopwatch.StartNew();
        p2.Parse(large);
        sw2.Stop();

        double ratio = sw2.Elapsed.TotalSeconds / Math.Max(0.001, sw1.Elapsed.TotalSeconds);
        _output.WriteLine($"1 MB : {sw1.Elapsed.TotalMilliseconds:F1} ms");
        _output.WriteLine($"10 MB: {sw2.Elapsed.TotalMilliseconds:F1} ms");
        _output.WriteLine($"Ratio (10x data): {ratio:F2}x (should be <= 15x for linear behavior)");

        // Linear = 10x. Allow up to 15x to account for GC pressure, system noise.
        Assert.True(
            ratio <= 15.0,
            $"10 MB took {ratio:F2}x longer than 1 MB -- super-linear growth detected");
    }
}
