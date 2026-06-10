namespace CcDirector.Cockpit.Logging;

/// <summary>
/// Thin thread-safe static facade over <see cref="CockpitFileLogWriter"/>. The Cockpit's file
/// sink: it writes to %LOCALAPPDATA%\cc-director\logs\cockpit\cockpit-YYYY-MM-DD-&lt;PID&gt;.log,
/// the same per-tool log layout the Director uses (logs/director/director-...). The path honors
/// the CC_DIRECTOR_ROOT override exactly as CcStorage does, so a relocated install (or a test)
/// keeps Cockpit logs alongside the rest of the fleet's logs.
///
/// The Cockpit deliberately does NOT reference CcDirector.Core, so the path resolution and the
/// writer are reproduced here rather than calling CcStorage/FileLog. This is the single product
/// component that previously had no persisted logging (issue #199).
/// </summary>
public static class CockpitFileLog
{
    private static readonly string LogDir = ResolveLogDir();

    private static readonly CockpitFileLogWriter Writer =
        new(LogDir, Environment.ProcessId, () => DateTime.Now);

    private static int _started;

    /// <summary>
    /// Resolve logs/cockpit under the cc-director root. Mirrors CcStorage: CC_DIRECTOR_ROOT wins,
    /// otherwise %LOCALAPPDATA%\cc-director. No FileLog dependency (and no circular init).
    /// </summary>
    private static string ResolveLogDir()
    {
        var root = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        if (string.IsNullOrEmpty(root))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            root = Path.Combine(localAppData, "cc-director");
        }
        return Path.Combine(root, "logs", "cockpit");
    }

    /// <summary>Start the background writer thread. Safe to call multiple times.</summary>
    public static void Start()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            return;

        Writer.Start();
    }

    /// <summary>Log a pre-rendered message with a timestamp prefix (matches the Director format).</summary>
    public static void Write(string message)
    {
        if (_started == 0) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        Writer.Enqueue(line);
    }

    /// <summary>Flush remaining messages and stop the writer thread.</summary>
    public static void Stop()
    {
        if (Interlocked.CompareExchange(ref _started, 0, 1) != 1)
            return;
        Writer.Stop();
    }

    /// <summary>The current log file path (useful for display / startup banner).</summary>
    public static string CurrentLogPath =>
        Path.Combine(LogDir, $"cockpit-{DateTime.Now:yyyy-MM-dd}-{Environment.ProcessId}.log");
}
