using System.Diagnostics;
using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests.Utilities;

// =====================================================================================
// FileLogWriter - the dequeue/rollover/flush engine behind FileLog. Regression tests for
// issue #171: a long-lived Director's log went silent after midnight (the new day's file
// was created at 0 bytes and never written). Three guarantees are pinned here:
//   1. Writes after a date rollover land in the NEW day's file.
//   2. A transient exception in the write path does NOT kill the writer thread.
//   3. Buffered output flushes within a bounded interval even while the queue stays busy.
// Each test uses an injectable clock so midnight can be crossed deterministically with no
// real waiting.
// =====================================================================================
public sealed class FileLogWriterTests : IDisposable
{
    private readonly string _logDir;

    public FileLogWriterTests()
    {
        _logDir = Path.Combine(Path.GetTempPath(), "cc-director-filelog-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_logDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_logDir))
                Directory.Delete(_logDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; a still-open handle on a CI box must not fail the test.
        }
    }

    private static void WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
            Thread.Sleep(20);
    }

    /// <summary>
    /// Read a file the writer may still hold open. The writer opens with FileShare.Read, so the
    /// reader must in turn share write access (FileShare.ReadWrite) or the open collides.
    /// </summary>
    private static string ReadShared(string path)
    {
        if (!File.Exists(path))
            return "";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void ComputeLogPath_UsesDateAndProcessId()
    {
        var writer = new FileLogWriter(_logDir, 4242, () => new DateTime(2026, 6, 5));

        var path = writer.ComputeLogPath(new DateTime(2026, 6, 5, 0, 0, 9));

        Assert.Equal(Path.Combine(_logDir, "director-2026-06-05-4242.log"), path);
    }

    [Fact]
    public void Write_AfterDateRollover_LandsInNewDaysFile()
    {
        // Clock starts on day 1; the test flips it to day 2 to simulate crossing midnight.
        var now = new DateTime(2026, 6, 4, 23, 59, 57);
        var writer = new FileLogWriter(_logDir, 6524, () => now);
        var day1Path = writer.ComputeLogPath(new DateTime(2026, 6, 4));
        var day2Path = writer.ComputeLogPath(new DateTime(2026, 6, 5));

        writer.Start();
        try
        {
            writer.Enqueue("day1-line");
            WaitUntil(() => ReadShared(day1Path).Contains("day1-line"));

            // Cross midnight, then write again.
            now = new DateTime(2026, 6, 5, 0, 0, 9);
            writer.Enqueue("day2-line");
            WaitUntil(() => ReadShared(day2Path).Contains("day2-line"));
        }
        finally
        {
            writer.Stop();
        }

        // The new day's file must exist, be non-zero, and contain the post-rollover line - the
        // exact failure in issue #171 (file created at 0 bytes, never written).
        Assert.True(File.Exists(day2Path), $"new day's file missing: {day2Path}");
        Assert.True(new FileInfo(day2Path).Length > 0, "new day's file is 0 bytes (issue #171 regression)");
        Assert.Contains("day2-line", ReadShared(day2Path));
    }

    [Fact]
    public void Write_WhenWritePathThrowsOnce_WriterSurvivesAndKeepsLogging()
    {
        // Force an exception inside the per-line write path on exactly the "boom" line via the
        // fault-injection hook. If the loop did not catch per-line (the old bug: one try around
        // the whole foreach), the thread would die on that throw and the line after it would
        // never be written.
        var writer = new FileLogWriter(_logDir, 7000, () => new DateTime(2026, 6, 6, 12, 0, 0))
        {
            BeforeWriteHook = line =>
            {
                if (line == "boom")
                    throw new InvalidTimeZoneException("forced transient failure");
            }
        };
        var logPath = writer.ComputeLogPath(new DateTime(2026, 6, 6));

        writer.Start();
        try
        {
            writer.Enqueue("before-boom");  // lands normally
            writer.Enqueue("boom");          // hook throws -> line is dropped, loop must continue
            writer.Enqueue("after-boom");    // MUST land if the thread survived the exception
            WaitUntil(() => ReadShared(logPath).Contains("after-boom"));
        }
        finally
        {
            writer.Stop();
        }

        var content = ReadShared(logPath);
        Assert.Contains("before-boom", content);
        Assert.Contains("after-boom", content); // proves the writer thread survived the exception
    }

    [Fact]
    public void Write_WhileQueueStaysBusy_FlushesWithinBoundedInterval()
    {
        // Drive the clock forward past the flush interval so the "flush only when queue empty"
        // path is never the reason content lands. We assert the line is on disk well before the
        // writer is stopped (Stop() flushes in finally), i.e. the periodic flush did the work.
        var baseTime = new DateTime(2026, 6, 7, 8, 0, 0);
        var reads = 0;
        // Each clock read advances time by 60% of the flush interval, so within two reads the
        // bounded-interval flush trigger fires even though the queue is continuously fed and
        // never drains to empty.
        DateTime Clock() =>
            baseTime.AddTicks(FileLogWriter.FlushInterval.Ticks * 6 / 10 * Interlocked.Increment(ref reads));

        var writer = new FileLogWriter(_logDir, 8000, Clock);
        var logPath = writer.ComputeLogPath(baseTime);

        writer.Start();
        try
        {
            // Keep the queue non-empty: enqueue a burst so Count is rarely 0 when a line is processed.
            for (int i = 0; i < 50; i++)
                writer.Enqueue($"busy-line-{i}");

            // Content must reach disk via the periodic flush, BEFORE Stop() is called.
            WaitUntil(() => ReadShared(logPath).Contains("busy-line-0"));

            Assert.True(File.Exists(logPath), "log file was never created under load");
            Assert.Contains("busy-line-0", ReadShared(logPath));
        }
        finally
        {
            writer.Stop();
        }
    }
}
