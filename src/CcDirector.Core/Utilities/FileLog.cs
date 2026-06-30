using CcDirector.Core.Storage;

namespace CcDirector.Core.Utilities;

/// <summary>
/// Simple thread-safe file logger. Writes to cc-director logs/director/ directory.
///
/// The actual dequeue/rollover/flush work lives in <see cref="FileLogWriter"/> so the day-rollover
/// behavior can be unit-tested with an injectable clock (issue #171). This type is the thin static
/// facade the rest of the app calls; it wires the engine to wall-clock time and the real log
/// directory.
/// </summary>
public static class FileLog
{
    private static readonly string LogDir = CcStorage.ToolLogs("director");

    // The active writer. Production never reassigns it; the test-only RedirectForTests seam (issue
    // #862) swaps it for an isolated writer for the duration of one test, which is safe because the
    // test assemblies disable parallelization (one test owns FileLog at a time).
    private static FileLogWriter _writer =
        new(LogDir, Environment.ProcessId, () => DateTime.Now);

    private static int _started;

    /// <summary>Start the background writer thread. Safe to call multiple times.</summary>
    public static void Start()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            return;

        _writer.Start();
    }

    /// <summary>Log a message with a timestamp prefix.</summary>
    public static void Write(string message)
    {
        if (_started == 0) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        _writer.Enqueue(line);
        System.Diagnostics.Debug.WriteLine(line);
    }

    /// <summary>Flush remaining messages and stop the writer thread.</summary>
    public static void Stop()
    {
        if (Interlocked.CompareExchange(ref _started, 0, 1) != 1)
            return;
        _writer.Stop();
    }

    /// <summary>Returns the current log file path (useful for display).</summary>
    public static string CurrentLogPath =>
        Path.Combine(LogDir, $"director-{DateTime.Now:yyyy-MM-dd}-{Environment.ProcessId}.log");

    /// <summary>
    /// TEST-ONLY seam (issue #862). Redirects FileLog to a private, throwaway directory for the life
    /// of the returned scope, then lets a test read exactly the lines it produced by draining the
    /// writer synchronously. This removes the two flakiness sources of asserting against the shared,
    /// process-wide writer: (1) <em>carryover</em> - a previous test's still-queued lines flushing
    /// into this test's file; and (2) <em>flush timing</em> - reading before the 1-second background
    /// flush landed the lines. Swapping the single static writer is safe because the test assemblies
    /// disable parallelization, so exactly one test owns FileLog at a time. Not for production use.
    /// </summary>
    internal static FileLogTestScope RedirectForTests() => new();

    /// <summary>The scope returned by <see cref="RedirectForTests"/>; restores the previous writer
    /// on dispose and deletes the throwaway directory. See that method for the rationale.</summary>
    internal sealed class FileLogTestScope : IDisposable
    {
        private readonly string _dir;
        private readonly FileLogWriter _previousWriter;
        private readonly int _previousStarted;
        private readonly FileLogWriter _testWriter;
        private List<string>? _lines;

        internal FileLogTestScope()
        {
            _dir = Path.Combine(Path.GetTempPath(), "cc-filelog-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _previousWriter = _writer;
            _previousStarted = _started;
            _testWriter = new FileLogWriter(_dir, Environment.ProcessId, () => DateTime.Now);
            _writer = _testWriter;
            _started = 1;
            _testWriter.Start();
        }

        /// <summary>
        /// Synchronously drain the writer to disk and return every line it wrote during this scope.
        /// Stop() completes the queue and joins the writer thread, so all lines are flushed before
        /// the read - no polling, no carryover. Idempotent: repeated calls return the same lines.
        /// </summary>
        internal IReadOnlyList<string> DrainAndReadLines()
        {
            if (_lines is not null) return _lines;
            _testWriter.Stop();
            var lines = new List<string>();
            foreach (var file in Directory.EnumerateFiles(_dir, "*.log"))
                lines.AddRange(ReadAllLinesShared(file));
            _lines = lines;
            return lines;
        }

        public void Dispose()
        {
            // Ensure the writer thread is stopped (and its file handle released) before restoring,
            // even if the test never called DrainAndReadLines.
            if (_lines is null) _testWriter.Stop();
            _writer = _previousWriter;
            _started = _previousStarted;
            // Best-effort cleanup of the throwaway directory; a leftover temp dir is harmless.
            try { Directory.Delete(_dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>Read a log file with FileShare.ReadWrite so a still-open writer handle never
        /// blocks the read.</summary>
        private static List<string> ReadAllLinesShared(string path)
        {
            var lines = new List<string>();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) is not null)
                lines.Add(line);
            return lines;
        }
    }
}
