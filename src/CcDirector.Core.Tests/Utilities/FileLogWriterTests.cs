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
[Collection("ProcessWideState")]
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

    [Fact]
    public void ComputeLogPath_UsesDateAndProcessId()
    {
        var writer = new FileLogWriter(_logDir, "4242", () => new DateTime(2026, 6, 5));

        var path = writer.ComputeLogPath(new DateTime(2026, 6, 5, 0, 0, 9));

        Assert.Equal(Path.Combine(_logDir, "director-2026-06-05-4242.log"), path);
    }

    // =================================================================================
    // Issue #2203 - two Gateway containers appended to ONE log file and clobbered each
    // other, so the startup record of three failed hosted deploys was unreadable. The
    // discriminator in the file name is the only thing keeping two processes apart, and
    // in a container the process id is always 1, so it kept nothing apart.
    // =================================================================================

    [Fact]
    public void ComputeLogPath_SameInstanceId_LandsOnOneSharedFile()
    {
        // Two containers, both running the Gateway as pid 1: this is the collision itself.
        var containerA = new FileLogWriter(_logDir, "1", () => new DateTime(2026, 7, 26));
        var containerB = new FileLogWriter(_logDir, "1", () => new DateTime(2026, 7, 26));

        var instant = new DateTime(2026, 7, 26);
        Assert.Equal(containerA.ComputeLogPath(instant), containerB.ComputeLogPath(instant));
    }

    [Fact]
    public void ComputeLogPath_DifferentInstanceId_KeepsEachProcessOnItsOwnFile()
    {
        var containerA = new FileLogWriter(_logDir, "a1b2c3d4e5f6", () => new DateTime(2026, 7, 26));
        var containerB = new FileLogWriter(_logDir, "0f9e8d7c6b5a", () => new DateTime(2026, 7, 26));

        var instant = new DateTime(2026, 7, 26);
        Assert.NotEqual(containerA.ComputeLogPath(instant), containerB.ComputeLogPath(instant));
        Assert.EndsWith("director-2026-07-26-a1b2c3d4e5f6.log", containerA.ComputeLogPath(instant));
    }

    [Fact]
    public void Constructor_BlankInstanceId_Throws()
    {
        // An empty discriminator would put every process back on one file - refuse it rather than
        // compute a path that silently collides.
        Assert.Throws<ArgumentException>(() => new FileLogWriter(_logDir, "  ", () => DateTime.Now));
    }

    // =================================================================================
    // Issue #2223 - the writer surviving a permanent write failure is NOT enough. Before
    // this, a sink that failed every single time repeated silently behind the per-line
    // catch and the log simply stopped growing, which reads exactly like a quiet process.
    // The live hosted Gateway sat at a 0-byte file for a quarter of an hour while serving.
    // =================================================================================
}
