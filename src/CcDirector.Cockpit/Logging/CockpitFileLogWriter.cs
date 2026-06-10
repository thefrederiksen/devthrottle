using System.Collections.Concurrent;
using System.Diagnostics;

namespace CcDirector.Cockpit.Logging;

/// <summary>
/// Background log-writer engine behind <see cref="CockpitFileLog"/>. A self-contained mirror of
/// the Director/Gateway FileLogWriter (CcDirector.Core.Utilities.FileLogWriter): same dated-file
/// naming, FileShare.Read sharing, day-rollover, and bounded-interval flush. It is duplicated here
/// rather than referenced because the Cockpit is a standalone published exe that deliberately does
/// NOT reference CcDirector.Core (see Cockpit.razor: "Cockpit does not reference Core").
///
/// Robustness contract (carried over from issue #171 - "new day's file stays 0 bytes"):
///   1. A day rollover (clock crosses local midnight) reliably reopens the dated file and
///      subsequent writes land in the new day's file for the life of the process.
///   2. A transient exception in the per-line write/rollover path NEVER terminates the writer
///      loop - it is logged to the debugger and the loop continues with the next line.
///   3. Buffered output is flushed within a bounded interval even while the queue stays non-empty,
///      so a continuously busy Cockpit never buffers lines indefinitely.
/// </summary>
internal sealed class CockpitFileLogWriter
{
    /// <summary>Maximum time buffered lines may sit unflushed while the queue stays non-empty.</summary>
    internal static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    /// <summary>The file-name prefix for the dated log file: cockpit-yyyy-MM-dd-{pid}.log.</summary>
    private const string FilePrefix = "cockpit";

    private readonly string _logDir;
    private readonly int _processId;
    private readonly Func<DateTime> _clock;
    private readonly BlockingCollection<string> _queue = new(4096);

    private Thread? _writerThread;

    /// <summary>
    /// Test-only fault-injection seam: invoked with each line just before it is written, inside the
    /// loop's per-line try. A test can throw from here to prove a transient write failure does not
    /// kill the writer thread. Null in production - no behavior change.
    /// </summary>
    internal Action<string>? BeforeWriteHook { get; set; }

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

    /// <summary>Enqueue a pre-formatted line for the writer thread.</summary>
    internal void Enqueue(string line)
    {
        _queue.TryAdd(line);
    }

    /// <summary>Signal the writer to drain and stop, then wait briefly for it to finish.</summary>
    internal void Stop()
    {
        _queue.CompleteAdding();
        _writerThread?.Join(TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// The dated log path for the given instant: cockpit-yyyy-MM-dd-{pid}.log. The date component
    /// is what rolls over at local midnight, so this is the single place the file name is derived.
    /// </summary>
    internal string ComputeLogPath(DateTime instant) =>
        Path.Combine(_logDir, $"{FilePrefix}-{instant:yyyy-MM-dd}-{_processId}.log");

    /// <summary>
    /// Open the dated log file for appending with FileShare.Read so live log viewers (and tests)
    /// can read the file while the writer holds it open. AutoFlush stays off - flushing is driven
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
                // Per-line try: a transient write or rollover failure must not kill the loop.
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

                    BeforeWriteHook?.Invoke(line);
                    writer.WriteLine(line);

                    // Flush when the queue drains, OR when the bounded interval has elapsed since the
                    // last flush, so a busy Cockpit still gets its lines on disk promptly.
                    var now = _clock();
                    if (_queue.Count == 0 || now - lastFlush >= FlushInterval)
                    {
                        writer.Flush();
                        lastFlush = now;
                    }
                }
                catch (Exception ex)
                {
                    // Log and continue - the loop must outlive any single bad write so logging
                    // keeps working. Use the debugger channel because the file logger itself is the
                    // thing that failed; we cannot route this back through it.
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
