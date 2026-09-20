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
    // Test 2: the 64 KB chunk model -- what the parser COSTS per chunk, measured
    // so that a busy machine cannot change the answer.
    // -------------------------------------------------------------------------

    /// <summary>
    /// THE PARSER MUST NOT COST THE USER INTERFACE THREAD MORE THAN ITS BUDGET, AND THIS TEST MUST SAY SO
    /// WHETHER OR NOT THE MACHINE IT RUNS ON IS BUSY.
    ///
    /// <c>TerminalControl</c> calls <see cref="AnsiParser.Parse"/> once per buffer read on the user interface
    /// thread, so a 64 KB chunk that takes longer than 100 milliseconds freezes the screen (the responsive
    /// interface mandate in CLAUDE.md). That is the property worth guarding.
    ///
    /// WHAT THIS TEST USED TO DO, AND WHY IT WAS A DEFECT. It timed every chunk with a stopwatch and failed
    /// when more than 5 percent of them exceeded 100 milliseconds, or when any one exceeded 1000. Elapsed
    /// wall-clock is not a property of the parser: it is the parser's own work PLUS however long the
    /// operating system left this thread off a processor. Measured on this Mac, the same binary at the same
    /// commit FAILED at a load average of 11.93 and PASSED at 2.99 minutes later - it was reporting how busy
    /// the machine was. This is a fleet machine that routinely runs a dozen agent sessions at once, so the
    /// test went red at random; a performance check that cries wolf is worse than none, because it is
    /// dismissed as noise on the day the parser genuinely does get slower. The wrong repair would have been
    /// to widen the budget until the machine stopped failing it, which throws away the only thing the test
    /// is for. The measurement is recorded in
    /// docs/missions/one-repository-list/proofs/throughput-test-measures-the-machine.md.
    ///
    /// WHAT IT ASSERTS NOW. Two things, neither of which a busy machine can push the wrong way:
    ///
    /// 1. THE WORK PER BYTE, as bytes allocated on this thread per byte parsed. Allocation is deterministic -
    ///    the same input over the same code allocates the same amount however contended the box is - it is
    ///    read per THREAD so nothing else in the test run is counted, and it is where a managed parser's
    ///    regressions actually appear (a string built per cell, a LINQ query in the hot loop, a boxed struct).
    ///    It is also the direct cause of the pauses this test exists to prevent: allocation is garbage
    ///    collector pressure, and the collector is what stops the user interface thread. Measured here at
    ///    34.47 bytes per byte parsed, repeatable to five significant figures across runs; the budget leaves
    ///    room for ordinary drift and still catches any regression that changes the order of the work.
    ///
    /// 2. THE COST OF A CHUNK, taken from the FASTEST whole chunk of the run rather than the median or the
    ///    worst. Scheduler contention is one-sided: being descheduled can only ADD elapsed time to a chunk,
    ///    never remove it. So the fastest of 160 samples is the closest this suite can get to the parser's
    ///    own cost, and it can never read BELOW that cost. Read the guarantee in the direction it actually
    ///    runs: a PASS is never false, because a fastest sample under the budget proves the parser's own
    ///    cost is under the budget too. A RED is not the mirror of that. It means the parser is over
    ///    budget, OR was within this machine's interference of it - a parser whose own cost sat close to
    ///    100 milliseconds could be pushed past it by the smallest interference present in every one of the
    ///    160 chunks. That the red is not happening today is an empirical fact about today's margin, not a
    ///    property of the measurement: the parser costs about 10 milliseconds a chunk, so a false red would
    ///    need 90 milliseconds of interference on all 160 samples, and the fastest chunk measured 11.07
    ///    milliseconds at a load average above 40. If a later legitimate change puts the parser at 70 to 90
    ///    milliseconds a chunk, still inside budget, a loaded machine could turn this red - so check the
    ///    margin before hunting a regression.
    ///
    ///    What a fully contended machine gets is still a green, and it is a true one ABOUT THE PARSER: the
    ///    delivered latency on such a machine may be far over budget while the parser itself is not. This
    ///    test deliberately declines to judge the machine, which is why the strong assertion is the first
    ///    one and this is the catastrophe guard it was always really serving as.
    ///
    /// WHAT IT NO LONGER ASSERTS, DELIBERATELY: the median, the worst chunk, and the share of chunks over
    /// budget. All three are printed, because a human reading a run wants to see them, and none is a verdict,
    /// because on a shared machine all three are verdicts on the machine. A truly load-independent TIMING
    /// claim needs processor time for this thread rather than elapsed time, and .NET exposes no portable way
    /// to read it - it would take a platform call per operating system (<c>clock_gettime</c> with
    /// <c>CLOCK_THREAD_CPUTIME_ID</c>, <c>GetThreadTimes</c>) whose constants nobody here can check on
    /// Windows, or a dedicated benchmark run on a machine reserved for it. Neither belongs in this suite.
    /// </summary>
    [Fact]
    public void Parse10MbInChunks_TypicalChunkStaysWithinUiBudget()
    {
        const int ChunkSize = 64 * 1024;            // 64 KB per chunk -- matches the typical read buffer
        const int Chunks = 160;                     // 160 x 64 KB = exactly 10 MB, no short chunk to skew the sample
        const int TargetBytes = Chunks * ChunkSize;
        const double MaxChunkMs = 100.0;            // responsive-interface mandate (CLAUDE.md)

        // Bytes allocated per byte parsed. Measured on macOS at 34.474, and 34.474 again on two further runs
        // minutes apart under a load average above 12 - allocation does not move with the machine. The budget
        // above it is head-room for ordinary drift (a cell struct gaining a field, a runtime upgrade), not a
        // licence: a regression that allocates per character rather than per row lands many times over it.
        // Re-baseline it against a measurement, in a commit that says what changed, never to quiet a red run.
        const double MaxAllocatedBytesPerByte = 40.0;

        byte[] stream = BuildSyntheticStream(TargetBytes);
        var chunks = new byte[Chunks][];
        for (int i = 0; i < Chunks; i++)
            chunks[i] = stream.AsSpan(i * ChunkSize, ChunkSize).ToArray();

        var (parser, _, _) = CreateParser(cols: 120, rows: 30, maxScrollback: 5000);
        var perChunkMs = new double[Chunks];
        var sw = new Stopwatch();

        // Read the allocation counter around the parse loop only: the chunk copies above are the test's own
        // allocations, not the parser's.
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Chunks; i++)
        {
            sw.Restart();
            parser.Parse(chunks[i]);
            sw.Stop();
            perChunkMs[i] = sw.Elapsed.TotalMilliseconds;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        long bytesParsed = (long)Chunks * ChunkSize;
        double allocatedPerByte = (double)allocated / bytesParsed;

        var sorted = (double[])perChunkMs.Clone();
        Array.Sort(sorted);
        double fastest = sorted[0];
        double median = sorted[sorted.Length / 2];
        double slowest = sorted[^1];
        int overBudget = sorted.Count(ms => ms > MaxChunkMs);

        _output.WriteLine($"Chunks        : {Chunks} x {ChunkSize / 1024} KB ({bytesParsed / 1024 / 1024} MB)");
        _output.WriteLine($"Allocated     : {allocated} bytes, {allocatedPerByte:F3} per byte parsed "
                          + $"(budget {MaxAllocatedBytesPerByte:F1}) -- THE ASSERTION");
        _output.WriteLine($"Fastest chunk : {fastest:F2} ms (budget {MaxChunkMs} ms) -- THE ASSERTION");
        _output.WriteLine($"Median chunk  : {median:F2} ms -- reported only, a busy machine moves this");
        _output.WriteLine($"Slowest chunk : {slowest:F2} ms -- reported only, a busy machine moves this");
        _output.WriteLine($"Over budget   : {overBudget}/{Chunks} chunks -- reported only, a busy machine moves this");

        // 1. The work per byte. Deterministic, per-thread, and unmoved by anything else running.
        Assert.True(
            allocatedPerByte <= MaxAllocatedBytesPerByte,
            $"the parser allocated {allocatedPerByte:F3} bytes per byte parsed ({allocated} bytes for "
            + $"{bytesParsed}), over the {MaxAllocatedBytesPerByte:F1} budget - work per byte has grown, and "
            + "allocation is what pauses the user interface thread. This number does not move with machine "
            + "load, so a busy box is not the explanation.");

        // 2. The cost of a chunk, from the sample least interfered with. Contention only ever inflates this,
        //    so a pass here is never false; a red means the parser is over budget, or was within this
        //    machine's interference of it.
        Assert.True(
            fastest <= MaxChunkMs,
            $"the fastest of {Chunks} chunks took {fastest:F1} ms, over the {MaxChunkMs} ms user-interface "
            + $"budget (median {median:F1} ms, slowest {slowest:F1} ms). Scheduler contention can only add "
            + "to an elapsed time, so a PASS here is never false - but a red is not the mirror of that: it "
            + "means the parser is over budget, OR was within this machine's interference of it. Check the "
            + "margin before hunting a regression. The parser cost about 10 ms a chunk when this budget was "
            + "set, so at that cost a red needs 90 ms of interference on every one of the samples and is a "
            + "parser problem; a parser already close to the budget can be pushed over it by a loaded "
            + "machine, and then the machine is part of the answer.");
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
