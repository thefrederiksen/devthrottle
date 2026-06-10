using System.Diagnostics;
using CcDirector.Cockpit.Logging;
using Xunit;

namespace CcDirector.Cockpit.Tests.Logging;

// =====================================================================================
// CockpitFileLogWriter - the dequeue/rollover/flush engine behind the Cockpit's file sink
// (issue #199). It is a self-contained mirror of the Director's FileLogWriter, so the same
// three guarantees are pinned here:
//   1. The dated path uses the cockpit- prefix + date + process id.
//   2. Writes after a date rollover land in the NEW day's file (the issue #171 regression).
//   3. A transient exception in the write path does NOT kill the writer thread.
//   4. Buffered output flushes within a bounded interval even while the queue stays busy.
// Each test uses an injectable clock so midnight can be crossed deterministically.
// =====================================================================================
public sealed class CockpitFileLogWriterTests : IDisposable
{
    private readonly string _logDir;

    public CockpitFileLogWriterTests()
    {
        _logDir = Path.Combine(Path.GetTempPath(), "cc-cockpit-filelog-tests", Guid.NewGuid().ToString("N"));
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
            // Best-effort temp cleanup; a still-open handle must not fail the test.
        }
    }

    private static void WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
            Thread.Sleep(20);
    }

    private static string ReadShared(string path)
    {
        if (!File.Exists(path))
            return "";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void ComputeLogPath_UsesCockpitPrefixDateAndProcessId()
    {
        var writer = new CockpitFileLogWriter(_logDir, 4242, () => new DateTime(2026, 6, 10));

        var path = writer.ComputeLogPath(new DateTime(2026, 6, 10, 0, 0, 9));

        Assert.Equal(Path.Combine(_logDir, "cockpit-2026-06-10-4242.log"), path);
    }

    [Fact]
    public void Write_AfterDateRollover_LandsInNewDaysFile()
    {
        var now = new DateTime(2026, 6, 9, 23, 59, 57);
        var writer = new CockpitFileLogWriter(_logDir, 6524, () => now);
        var day1Path = writer.ComputeLogPath(new DateTime(2026, 6, 9));
        var day2Path = writer.ComputeLogPath(new DateTime(2026, 6, 10));

        writer.Start();
        try
        {
            writer.Enqueue("day1-line");
            WaitUntil(() => ReadShared(day1Path).Contains("day1-line"));

            now = new DateTime(2026, 6, 10, 0, 0, 9);
            writer.Enqueue("day2-line");
            WaitUntil(() => ReadShared(day2Path).Contains("day2-line"));
        }
        finally
        {
            writer.Stop();
        }

        Assert.True(File.Exists(day2Path), $"new day's file missing: {day2Path}");
        Assert.True(new FileInfo(day2Path).Length > 0, "new day's file is 0 bytes (issue #171 regression)");
        Assert.Contains("day2-line", ReadShared(day2Path));
    }

    [Fact]
    public void Write_WhenWritePathThrowsOnce_WriterSurvivesAndKeepsLogging()
    {
        var writer = new CockpitFileLogWriter(_logDir, 7000, () => new DateTime(2026, 6, 10, 12, 0, 0))
        {
            BeforeWriteHook = line =>
            {
                if (line == "boom")
                    throw new InvalidTimeZoneException("forced transient failure");
            },
        };
        var logPath = writer.ComputeLogPath(new DateTime(2026, 6, 10));

        writer.Start();
        try
        {
            writer.Enqueue("before-boom");
            writer.Enqueue("boom");
            writer.Enqueue("after-boom");
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
        var baseTime = new DateTime(2026, 6, 10, 8, 0, 0);
        var reads = 0;
        DateTime Clock() =>
            baseTime.AddTicks(CockpitFileLogWriter.FlushInterval.Ticks * 6 / 10 * Interlocked.Increment(ref reads));

        var writer = new CockpitFileLogWriter(_logDir, 8000, Clock);
        var logPath = writer.ComputeLogPath(baseTime);

        writer.Start();
        try
        {
            for (int i = 0; i < 50; i++)
                writer.Enqueue($"busy-line-{i}");

            WaitUntil(() => ReadShared(logPath).Contains("busy-line-0"));

            Assert.True(File.Exists(logPath), "log file was never created under load");
            Assert.Contains("busy-line-0", ReadShared(logPath));
        }
        finally
        {
            writer.Stop();
        }
    }

    [Fact]
    public void Ctor_WithBlankLogDir_Throws()
    {
        Assert.Throws<ArgumentException>(() => new CockpitFileLogWriter("  ", 1, () => DateTime.Now));
    }
}
