using System.Collections.Concurrent;
using System.Diagnostics;

namespace CcDirector.Core.Utilities;

/// <summary>
/// Background log-writer engine behind <see cref="FileLog"/>. Extracted so the day-rollover and
/// flush behavior can be unit-tested with an injectable clock (issue #171).
///
/// Robustness contract (issue #171 - "new day's file stays 0 bytes"):
///   1. A day rollover (clock crosses local midnight) reliably reopens the dated file and
///      subsequent writes land in the new day's file for the life of the process.
///   2. A transient exception in the per-line write/rollover path NEVER terminates the writer
///      loop - it is logged to the debugger and the loop continues with the next line. The old
///      design wrapped the entire consuming loop in one try, so a single throw killed the thread
///      and all logging stopped silently.
///   3. Buffered output is flushed within a bounded interval even while the queue stays non-empty,
///      so a continuously busy Director never buffers lines indefinitely. The old design flushed
///      only when the queue drained to empty, which never happened under load.
/// </summary>
internal sealed class FileLogWriter
{
    /// <summary>Maximum time buffered lines may sit unflushed while the queue stays non-empty.</summary>
    internal static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    private readonly string _logDir;
    private readonly string _instanceId;
    private readonly string _filePrefix;
    private readonly Func<DateTime> _clock;
    private readonly BlockingCollection<string> _queue = new(1024);

    private Thread? _writerThread;

    // Lines the bounded queue refused because the writer could not keep up. Enqueue must never block the
    // caller (a stalled network file share would otherwise stall the application), so a full queue drops -
    // but a SILENT drop is what made three hosted startup failures unreadable: the file share stalled, the
    // queue filled, and every startup line vanished with nothing saying so. The count is published and the
    // writer emits it into the log the moment it can write again, so a gap in the record always announces
    // itself. See FileLog.MirrorToConsole for the second sink that survives the stall entirely.
    private long _droppedLines;
    private long _reportedDrops;

    // Consecutive failures of the WRITE itself (issue #2223). The per-line catch below exists so a
    // transient failure cannot kill the writer thread (issue #171), and that is right - but a PERMANENT
    // failure then repeats forever and, reported only to the debugger, is invisible. The live hosted
    // Gateway sat at a 0-byte log file for a quarter of an hour while serving traffic and nothing said so.
    // A dead sink must not look like a quiet process. Note this is a different failure from a dropped
    // line: nothing is dropped here - every line is accepted and then silently thrown away by the sink.
    private int _consecutiveWriteFailures;
    private bool _sinkFailureReported;

    /// <summary>How many consecutive write attempts have failed. Zero on a healthy writer.</summary>
    internal int ConsecutiveWriteFailures => Volatile.Read(ref _consecutiveWriteFailures);

    /// <summary>
    /// Raised once when the sink has failed <see cref="SinkFailureThreshold"/> times in a row, and again
    /// when it recovers. The handler's job is to report the failure somewhere that still works - the file
    /// cannot report on itself. Arguments: the consecutive failure count, and the last exception (null on
    /// the recovery call).
    /// </summary>
    internal Action<int, Exception?>? OnSinkHealthChanged { get; set; }

    /// <summary>Consecutive failures before the sink is declared dead. Small: one or two failures can be a
    /// genuine transient, a steady stream cannot.</summary>
    internal const int SinkFailureThreshold = 3;

    /// <summary>
    /// Test-only fault-injection seam: invoked with each line just before it is written, inside the
    /// loop's per-line try. A test can throw from here to prove a transient write failure does not
    /// kill the writer thread (issue #171). Null in production - no behavior change.
    /// </summary>
    internal Action<string>? BeforeWriteHook { get; set; }

    internal FileLogWriter(string logDir, string instanceId, Func<DateTime> clock, string filePrefix = FileLog.DirectorFilePrefix)
    {
        if (string.IsNullOrWhiteSpace(logDir))
            throw new ArgumentException("Log directory is required", nameof(logDir));
        if (string.IsNullOrWhiteSpace(instanceId))
            throw new ArgumentException("Instance id is required", nameof(instanceId));

        _logDir = logDir;
        _instanceId = instanceId;
        _filePrefix = string.IsNullOrWhiteSpace(filePrefix)
            ? throw new ArgumentException("File prefix is required", nameof(filePrefix))
            : filePrefix;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Lines the bounded queue has refused so far because the writer could not keep up.</summary>
    internal long DroppedLines => Interlocked.Read(ref _droppedLines);

    /// <summary>Start the background writer thread.</summary>
    internal void Start()
    {
        Directory.CreateDirectory(_logDir);

        _writerThread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "FileLog-Writer"
        };
        _writerThread.Start();
    }

    /// <summary>
    /// True once <see cref="Stop"/> has completed the queue. A completed
    /// <see cref="BlockingCollection{T}"/> can NEVER accept another item - there is no reopen - so this
    /// writer is spent and a caller wanting to log again needs a NEW one. <see cref="FileLog.Start"/>
    /// reads this to decide exactly that.
    /// </summary>
    internal bool IsSpent => _queue.IsAddingCompleted;

    /// <summary>
    /// Enqueue a pre-formatted line for the writer thread. NEVER BLOCKS AND NEVER THROWS: if the line
    /// cannot be accepted it is dropped and counted rather than holding up - or breaking - the caller.
    /// <see cref="DroppedLines"/> makes the loss visible and the writer records it as soon as it can
    /// write again.
    ///
    /// Two different refusals land here, and until 2026-08-03 only the first was handled:
    ///   * The queue is FULL because the writer is stalled (a network file share that has stopped
    ///     responding). TryAdd returns false, and the line is counted as dropped.
    ///   * The queue is COMPLETED because Stop has run. TryAdd does NOT return false in that case - it
    ///     THROWS InvalidOperationException - so the exception escaped into whoever happened to be
    ///     logging, in flat contradiction of the guarantee written directly above it. The victim was
    ///     always a bystander doing something unrelated, which is why this presented as several
    ///     unconnected flaky tests rather than as one logging defect (devthrottle_internal#1312).
    ///
    /// A logging call must not be able to take down its caller, whoever completed the queue and for
    /// whatever reason. That is why this is fixed here as well as in FileLog's lifetime handling: the
    /// lifetime fix stops the queue being completed under a live logger, and this stops the next thing
    /// that completes it from re-arming the same failure.
    ///
    /// The completed case is checked AND caught. The check alone would be a race - the queue can be
    /// completed between the test and the add - and the catch alone would make an ordinary, expected
    /// state cost an exception throw on every line.
    /// </summary>
    internal void Enqueue(string line)
    {
        if (_queue.IsAddingCompleted)
        {
            Interlocked.Increment(ref _droppedLines);
            return;
        }

        try
        {
            if (!_queue.TryAdd(line))
                Interlocked.Increment(ref _droppedLines);
        }
        catch (InvalidOperationException)
        {
            // Completed between the check above and the add. Same outcome as any other refusal.
            Interlocked.Increment(ref _droppedLines);
        }
    }

    /// <summary>
    /// Signal the writer to drain and stop, then wait briefly for it to finish.
    ///
    /// THIS IS ONE-WAY. Completing the queue cannot be undone, so a stopped writer is finished for good
    /// and must be replaced rather than restarted - see <see cref="IsSpent"/>.
    /// </summary>
    internal void Stop()
    {
        _queue.CompleteAdding();
        _writerThread?.Join(TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// The dated log path for the given instant: director-yyyy-MM-dd-{instance}.log. The date component
    /// is what rolls over at local midnight, so this is the single place the file name is derived.
    ///
    /// The instance component must be unique per RUNNING PROCESS, because it is the only thing keeping two
    /// processes off one file. On the desktop the process id supplies that (and the desktop tooling looks
    /// logs up by it - see App.axaml.cs and scripts/agent-session-isolation.ps1, which glob
    /// <c>director-*-{pid}.log</c>). Inside a container the process id is ALWAYS 1, so it supplies nothing:
    /// during a slot swap two Gateway containers ran as pid 1 and appended to one file on an SMB share
    /// mounted <c>nobrl</c>, where FileShare.Read is not enforced across clients. They clobbered each
    /// other mid-record. Hosted therefore passes a per-process token instead - see
    /// <see cref="FileLog.UseUniqueInstanceId"/>.
    /// </summary>
    internal string ComputeLogPath(DateTime instant) =>
        Path.Combine(_logDir, $"{_filePrefix}-{instant:yyyy-MM-dd}-{_instanceId}.log");

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
                // Without this, a single throw escapes the consuming foreach and the writer thread
                // dies, so all logging stops silently for the life of the process (issue #171).
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

                    // A gap in the record must announce itself. If the queue overflowed while this thread
                    // was stalled, say so in the file BEFORE the next surviving line, so a reader can never
                    // mistake a truncated record for a quiet period.
                    var dropped = Interlocked.Read(ref _droppedLines);
                    if (dropped > _reportedDrops)
                    {
                        writer.WriteLine(
                            $"{_clock():yyyy-MM-dd HH:mm:ss.fff} [FileLogWriter] LOG GAP: {dropped - _reportedDrops} " +
                            "line(s) were dropped because the writer could not keep up - the record above is incomplete");
                        _reportedDrops = dropped;
                    }

                    writer.WriteLine(line);

                    // Flush when the queue drains, OR when the bounded interval has elapsed since the
                    // last flush. The interval guarantees a busy Director (queue never empty) still
                    // gets its lines on disk instead of buffering them indefinitely (issue #171).
                    var now = _clock();
                    if (_queue.Count == 0 || now - lastFlush >= FlushInterval)
                    {
                        writer.Flush();
                        lastFlush = now;
                    }

                    // The write landed. If the sink had been declared dead, say that it is back - a
                    // recovery that nobody announces leaves the reader believing the log is still lying.
                    if (Volatile.Read(ref _consecutiveWriteFailures) > 0)
                    {
                        Volatile.Write(ref _consecutiveWriteFailures, 0);
                        if (_sinkFailureReported)
                        {
                            _sinkFailureReported = false;
                            OnSinkHealthChanged?.Invoke(0, null);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Continue - the loop must outlive any single bad write so logging keeps working
                    // (issue #171). Use the debugger channel because FileLog itself is the thing that
                    // failed; we cannot route this back through it.
                    Debug.WriteLine($"[FileLogWriter] write FAILED, continuing: {ex.Message}");

                    // ...but a failure that KEEPS happening is not transient, and surviving it quietly is
                    // how a dead log came to look like a quiet process (issue #2223). Announce it ONCE, on
                    // a channel that is not the thing that just broke, and name the exception type so the
                    // report is actionable rather than merely alarming.
                    var failures = Interlocked.Increment(ref _consecutiveWriteFailures);
                    if (failures >= SinkFailureThreshold && !_sinkFailureReported)
                    {
                        _sinkFailureReported = true;
                        try { OnSinkHealthChanged?.Invoke(failures, ex); }
                        catch (Exception handlerEx)
                        {
                            // The reporter itself failing must not kill the writer thread either.
                            Debug.WriteLine($"[FileLogWriter] sink-failure report FAILED: {handlerEx.Message}");
                        }
                    }
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
