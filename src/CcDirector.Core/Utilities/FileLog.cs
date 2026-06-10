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

    private static readonly FileLogWriter _writer =
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
}
