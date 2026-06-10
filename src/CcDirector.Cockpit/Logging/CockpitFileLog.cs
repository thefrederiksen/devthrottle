using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CcDirector.Cockpit.Logging;

/// <summary>
/// Persisted file sink for the Cockpit (issue #199). The Cockpit was the only product component
/// with no persisted logging - its <see cref="ILogger"/> lines went to an invisible console - so
/// a misbehaving web UI left no trace to debug from. This provider routes every Cockpit log line
/// to <c>%LOCALAPPDATA%\cc-director\logs\cockpit\cockpit-YYYY-MM-DD-&lt;PID&gt;.log</c>, matching the
/// Director/Gateway <c>FileLog</c> format (timestamp prefix), filename shape, and day-rollover
/// behaviour.
///
/// Why a self-contained writer instead of reusing Core's FileLog: the Cockpit is deliberately a
/// lean web app referencing only the Gateway contracts and Markdig. Core pulls in native
/// dependencies (Whisper.net runtime, SQLite) that have no place in the web app's publish output,
/// so the small, well-understood writer convention is replicated here rather than imported.
///
/// Level filtering is left entirely to the framework's <see cref="ILoggerFactory"/> category
/// rules (configured in appsettings.json, where <c>CcDirector.Cockpit: Debug</c> is in effect for
/// this sink); the provider writes whatever it is handed.
/// </summary>
public sealed class CockpitFileLoggerProvider : ILoggerProvider
{
    private readonly CockpitFileLogWriter _writer;

    /// <summary>Create the provider and start its background writer thread.</summary>
    /// <param name="logDirectory">
    /// Target directory for the dated log file. When null/empty the canonical Cockpit log
    /// directory under <c>%LOCALAPPDATA%\cc-director\logs\cockpit</c> is used.
    /// </param>
    public CockpitFileLoggerProvider(string? logDirectory = null)
    {
        var dir = string.IsNullOrWhiteSpace(logDirectory) ? DefaultLogDirectory() : logDirectory;
        _writer = new CockpitFileLogWriter(dir, Environment.ProcessId, () => DateTime.Now);
        _writer.Start();
    }

    /// <summary>The current dated log file path, for a startup banner / display.</summary>
    public string CurrentLogPath => _writer.ComputeLogPath(DateTime.Now);

    /// <summary>
    /// The canonical Cockpit log directory: <c>%LOCALAPPDATA%\cc-director\logs\cockpit</c>, with
    /// the same <c>CC_DIRECTOR_ROOT</c> override the rest of cc-director honours. Resolved here
    /// (rather than via Core's CcStorage) to keep this sink free of the Core reference.
    /// </summary>
    public static string DefaultLogDirectory()
    {
        var root = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        if (string.IsNullOrEmpty(root))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            root = Path.Combine(localAppData, "cc-director");
        }
        return Path.Combine(root, "logs", "cockpit");
    }

    public ILogger CreateLogger(string categoryName) => new CockpitFileLogger(categoryName, _writer);

    public void Dispose() => _writer.Stop();

    /// <summary>
    /// The <see cref="ILogger"/> the provider hands out. Formats one line per enabled log event in
    /// the FileLog shape: <c>{timestamp} [{category}] {message}</c> (plus the exception on a new
    /// line when present) and enqueues it on the shared writer.
    /// </summary>
    private sealed class CockpitFileLogger : ILogger
    {
        private readonly string _category;
        private readonly CockpitFileLogWriter _writer;

        public CockpitFileLogger(string category, CockpitFileLogWriter writer)
        {
            _category = category;
            _writer = writer;
        }

        // No scopes are used by the Cockpit; a no-op scope keeps the contract satisfied.
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        // The ILoggerFactory applies the configured category-level filter before calling Log, so
        // every level the framework lets through is persisted.
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (formatter is null) return;
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);
            if (string.IsNullOrEmpty(message) && exception is null) return;

            var shortCategory = ShortCategory(_category);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {LevelTag(logLevel)} [{shortCategory}] {message}";
            if (exception is not null)
                line += Environment.NewLine + exception;

            _writer.Enqueue(line);
        }

        // The last dotted segment of the category (e.g. "Cockpit" from
        // "CcDirector.Cockpit.Components.Pages.Cockpit") keeps lines readable while still
        // identifying the source component.
        private static string ShortCategory(string category)
        {
            var idx = category.LastIndexOf('.');
            return idx >= 0 && idx < category.Length - 1 ? category[(idx + 1)..] : category;
        }

        private static string LevelTag(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO ",
            LogLevel.Warning => "WARN ",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "CRIT ",
            _ => "     ",
        };

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}

/// <summary>
/// Background log-writer engine behind <see cref="CockpitFileLoggerProvider"/>. A faithful copy of
/// the Director/Gateway <c>FileLogWriter</c> robustness contract (issue #171), differing only in
/// the filename prefix (<c>cockpit-</c> instead of <c>director-</c>):
///   1. A day rollover (local midnight) reliably reopens the new dated file.
///   2. A transient per-line write/rollover failure NEVER kills the writer loop - it is logged to
///      the debugger and the loop continues with the next line.
///   3. Buffered output is flushed within a bounded interval even while the queue stays non-empty,
///      so a continuously busy Cockpit never buffers lines indefinitely.
/// </summary>
internal sealed class CockpitFileLogWriter
{
    /// <summary>Maximum time buffered lines may sit unflushed while the queue stays non-empty.</summary>
    internal static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    private readonly string _logDir;
    private readonly int _processId;
    private readonly Func<DateTime> _clock;
    private readonly BlockingCollection<string> _queue = new(4096);

    private Thread? _writerThread;

    internal CockpitFileLogWriter(string logDir, int processId, Func<DateTime> clock)
    {
        if (string.IsNullOrWhiteSpace(logDir))
            throw new ArgumentException("Log directory is required", nameof(logDir));

        _logDir = logDir;
        _processId = processId;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Start the background writer thread.</summary>
    internal void Start()
    {
        Directory.CreateDirectory(_logDir);

        _writerThread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "CockpitFileLog-Writer",
        };
        _writerThread.Start();
    }

    /// <summary>Enqueue a pre-formatted line for the writer thread (drops silently if shutting down).</summary>
    internal void Enqueue(string line)
    {
        try { _queue.TryAdd(line); }
        catch (InvalidOperationException) { /* CompleteAdding called during shutdown - drop */ }
    }

    /// <summary>Signal the writer to drain and stop, then wait briefly for it to finish.</summary>
    internal void Stop()
    {
        _queue.CompleteAdding();
        _writerThread?.Join(TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// The dated log path for the given instant: <c>cockpit-yyyy-MM-dd-{pid}.log</c>. The date
    /// component rolls over at local midnight, so this is the single place the file name is derived.
    /// </summary>
    internal string ComputeLogPath(DateTime instant) =>
        Path.Combine(_logDir, $"cockpit-{instant:yyyy-MM-dd}-{_processId}.log");

    /// <summary>
    /// Open the dated log file for appending with FileShare.Read so live log viewers (and tests)
    /// can read it while the writer holds it open. AutoFlush stays off - flushing is driven
    /// explicitly by the bounded-interval logic in the loop.
    /// </summary>
    private static StreamWriter OpenWriter(string path)
    {
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        return new StreamWriter(stream) { AutoFlush = false };
    }

    private void WriterLoop()
    {
        StreamWriter? writer = null;
        string? currentDate = null;
        var lastFlush = _clock();

        try
        {
            foreach (var line in _queue.GetConsumingEnumerable())
            {
                // Per-line try: a transient write or rollover failure must not kill the loop, or
                // all logging stops silently for the life of the process (issue #171).
                try
                {
                    var today = _clock().ToString("yyyy-MM-dd");
                    if (today != currentDate)
                    {
                        writer?.Flush();
                        writer?.Dispose();
                        currentDate = today;
                        writer = OpenWriter(ComputeLogPath(_clock()));
                        lastFlush = _clock();
                    }

                    if (writer is null)
                        continue;

                    writer.WriteLine(line);

                    var now = _clock();
                    if (_queue.Count == 0 || now - lastFlush >= FlushInterval)
                    {
                        writer.Flush();
                        lastFlush = now;
                    }
                }
                catch (Exception ex)
                {
                    // Log and continue - the loop must outlive any single bad write. Use the
                    // debugger channel because this writer IS the logging facility that failed.
                    Debug.WriteLine($"[CockpitFileLogWriter] write FAILED, continuing: {ex.Message}");
                }
            }
        }
        catch (InvalidOperationException)
        {
            // GetConsumingEnumerable throws when CompleteAdding has been called - normal shutdown.
        }
        finally
        {
            writer?.Flush();
            writer?.Dispose();
        }
    }
}
