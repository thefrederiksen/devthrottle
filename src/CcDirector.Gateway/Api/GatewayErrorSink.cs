using System.Reflection;
using System.Runtime.InteropServices;
using CcDirector.Core.ErrorReports;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Api;

/// <summary>
/// One error the Gateway itself had, on its way into the error store. Everything here is written by the Gateway's own
/// code; <see cref="GatewayErrorSink"/> scrubs every free-text field again before it is kept.
/// </summary>
/// <param name="Account">The account the request belonged to, or empty for the Gateway's own internals - which are
/// the operator's to read, not an account's.</param>
internal sealed record GatewayError(
    string Source,
    string Kind,
    string Message,
    string? ExceptionType = null,
    string? Stack = null,
    string Account = "",
    bool? UserVisible = null,
    string? Surface = null,
    string? Action = null,
    string? CorrelationId = null,
    int? HttpStatus = null,
    string? ErrorCode = null,
    string? SessionId = null);

/// <summary>
/// The hosted Gateway's OWN errors, into the central error store (the Error Logging mission, step 2, issue #3675).
///
/// Before this the Gateway's failures never reached the store: its log lives on the container's temporary disk
/// (<see cref="GatewayEntryPoint"/>) and standard output, both lost on every deploy. Three things now feed it:
///   - every failure line the Gateway logs, recognised by the SAME <see cref="ErrorLine"/> the Director uses for its
///     FAILED lines (<see cref="OnLogLine"/>, attached to <see cref="FileLog.ErrorObserver"/>), so no call site has
///     to remember to report;
///   - an unhandled endpoint exception, with the correlation id its answer carried (<see cref="GatewayRequestErrors"/>);
///   - a prompt the Gateway refused because the session's Director was offline or the delivery failed.
/// A line logged while a request is being served is stamped with that request's correlation id and route, so the
/// client that showed the error can report the same id and the two rows read as one incident.
///
/// WHY IT CAN NEVER HURT THE GATEWAY, the same reasoning as the Director's <see cref="ErrorReporter"/>:
///   - <see cref="OnLogLine"/> runs on whatever thread logged and only adds to an in-memory table under a lock. The
///     store is written by <see cref="Flush"/>, on its own timer, never on a request thread.
///   - The same error repeated is ONE row with a count. The table holds at most <see cref="MaxPending"/> distinct
///     errors and at most <see cref="MaxStoredPerHour"/> rows an hour are written; past either, errors wait or are
///     dropped and COUNTED, and the count becomes the hour's flood record (<see cref="ErrorIntakeFloods"/>).
///   - A write that fails is tried <see cref="MaxAttempts"/> times, then dropped and counted. Nothing is swallowed:
///     every failure is logged.
///
/// IT NEVER REPORTS ITSELF. Its own log lines start with <see cref="ErrorLine.ReporterTag"/>, which the recogniser
/// never treats as an error. And while the sink is writing to the store, any line logged on that thread - the store's
/// own "UpdateSummaries FAILED", say - is not observed: otherwise an unwritable store would produce a failure line,
/// which would produce a write, which would fail, for ever. Such a line is still in the process log.
/// </summary>
internal sealed class GatewayErrorSink : IDisposable
{
    internal const int MaxPending = 200;
    internal const int MaxStoredPerHour = 600;
    internal const int MaxAttempts = 3;
    internal const int MaxLineChars = 4 * ErrorReportLimits.MaxStack;
    internal static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Set by <see cref="GatewayEntryPoint"/> for the real Gateway process: the sink then attaches to the process log.
    /// A test that starts a <see cref="GatewayHost"/> leaves it off, so its host does not collect every failure line
    /// the whole test process writes.
    /// </summary>
    internal static bool AttachToProcessLog { get; set; }

    [ThreadStatic] private static bool _observing;
    [ThreadStatic] private static bool _writing;
    [ThreadStatic] private static int _suppressed;

    private readonly ErrorReportStore _store;
    private readonly ErrorIntakeFloods _floods;
    private readonly Func<DateTime> _clock;
    private readonly ProcessFacts _facts;

    private readonly object _lock = new();
    private readonly LinkedList<Pending> _order = new();
    private readonly Dictionary<string, LinkedListNode<Pending>> _bySignature = new(StringComparer.Ordinal);
    private readonly Queue<(DateTime At, int Count)> _storedWindow = new();
    private readonly object _flushLock = new();
    private long _dropped;
    private long _stored;
    private bool _tableFullLogged;

    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private bool _attached;
    private bool _disposed;

    private sealed class Pending
    {
        public required string Signature;
        public required GatewayError Error;
        public required DateTime FirstSeenUtc;
        public DateTime LastSeenUtc;
        public int Count = 1;
        public int Attempts;
    }

    /// <summary>What the Gateway process is, stamped on every row it stores.</summary>
    internal sealed record ProcessFacts(string ProductVersion, string Os, string OsVersion, string Arch, string MachineId)
    {
        public static ProcessFacts Current()
        {
            var assembly = typeof(GatewayErrorSink).Assembly;
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString()
                ?? "unknown";
            return new ProcessFacts(
                ErrorTextScrubber.Clean(version, ErrorReportLimits.MaxShortField),
                OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "other",
                ErrorTextScrubber.Clean(RuntimeInformation.OSDescription, ErrorReportLimits.MaxShortField),
                RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
                ErrorReportMachineId.Of(Environment.MachineName));
        }
    }

    public GatewayErrorSink(ErrorReportStore store, ErrorIntakeFloods? floods = null, Func<DateTime>? clock = null,
        ProcessFacts? facts = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _floods = floods ?? ErrorIntakeFloods.Shared;
        _clock = clock ?? (() => DateTime.UtcNow);
        _facts = facts ?? ProcessFacts.Current();
    }

    /// <summary>Errors dropped: the table was full, or a write ran out of attempts. Each one is also in a flood record.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Rows written to the store, flood records included.</summary>
    public long Stored => Interlocked.Read(ref _stored);

    internal int PendingCount { get { lock (_lock) return _order.Count; } }

    /// <summary>
    /// Start the flush timer, and attach to the process log when <see cref="AttachToProcessLog"/> is set. Call once.
    /// </summary>
    public void Start()
    {
        if (_loop is not null) throw new InvalidOperationException("the Gateway error sink is already started");
        if (AttachToProcessLog)
        {
            FileLog.ErrorObserver += OnLogLine;
            _attached = true;
        }
        _loop = Task.Run(RunLoopAsync);
        FileLog.Write($"{ErrorLine.ReporterTag} gateway: started, store={_store.Root}, attachedToProcessLog={_attached}, maxPerHour={MaxStoredPerHour}");
    }

    /// <summary>
    /// While the returned scope is open, a line logged on this thread is not observed. For a site that records its
    /// error explicitly and ALSO logs it, so the one failure is not stored twice.
    /// </summary>
    internal static IDisposable SuppressObservation()
    {
        _suppressed++;
        return new Unsuppress();
    }

    private sealed class Unsuppress : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _suppressed--;
        }
    }

    /// <summary>
    /// The <see cref="FileLog"/> observer. Called on whatever thread logged the line. Adds or counts; never blocks on
    /// anything but the table lock, never throws.
    /// </summary>
    public void OnLogLine(string message)
    {
        if (_observing || _writing || _suppressed > 0) return;
        _observing = true;
        try
        {
            if (!ErrorLine.IsError(message)) return;
            if (message.Length > MaxLineChars) message = message[..MaxLineChars];
            var (source, text, exceptionType, stack) = ErrorLine.Parse(message);
            var scope = GatewayRequestErrors.Current;
            Report(new GatewayError(source, ErrorLine.KindOf(message), text, exceptionType, stack,
                Surface: scope?.Route, CorrelationId: scope?.CorrelationId));
        }
        catch (Exception ex)
        {
            // An observer that throws would throw into every caller of FileLog.Write. Counted, and written where an
            // operator reads it - standard error, not FileLog, which is what called us.
            Interlocked.Increment(ref _dropped);
            Console.Error.WriteLine($"{ErrorLine.ReporterTag} gateway: could not queue a line ({ex.GetType().Name}): {ex.Message}");
        }
        finally
        {
            _observing = false;
        }
    }

    /// <summary>Queue one error. Every free-text field is scrubbed and capped here, whoever built it.</summary>
    public void Report(GatewayError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var clean = error with
        {
            Source = ErrorTextScrubber.Clean(error.Source, ErrorReportLimits.MaxShortField),
            Kind = ErrorTextScrubber.Clean(error.Kind, 40),
            Message = ErrorTextScrubber.Clean(error.Message, ErrorReportLimits.MaxMessage),
            ExceptionType = NullIfEmpty(ErrorTextScrubber.Clean(error.ExceptionType, ErrorReportLimits.MaxShortField)),
            Stack = NullIfEmpty(ErrorTextScrubber.Clean(error.Stack, ErrorReportLimits.MaxStack)),
            Surface = NullIfEmpty(ErrorTextScrubber.Clean(error.Surface, ErrorReportLimits.MaxShortField)),
            Action = NullIfEmpty(ErrorTextScrubber.Clean(error.Action, ErrorReportLimits.MaxAction)),
            CorrelationId = NullIfEmpty(ErrorTextScrubber.Clean(error.CorrelationId, ErrorReportLimits.MaxShortField)),
            ErrorCode = NullIfEmpty(ErrorTextScrubber.Clean(error.ErrorCode, ErrorReportLimits.MaxShortField)),
            SessionId = NullIfEmpty(ErrorTextScrubber.Clean(error.SessionId, ErrorReportLimits.MaxShortField)),
        };
        if (clean.Message.Length == 0)
            throw new ArgumentException("a Gateway error needs a message", nameof(error));

        // Two reports are the same entry when they are the same problem AND the same incident: one request's repeats
        // fold, two requests' failures stay two rows so each keeps the id its client was shown.
        var signature = string.Join('|',
            ErrorFingerprint.Of(ErrorReportLimits.Gateway, clean.Source, clean.ExceptionType, clean.Message, clean.HttpStatus, clean.ErrorCode),
            clean.Kind, clean.CorrelationId ?? "", clean.Account, clean.SessionId ?? "");
        var now = _clock();
        var dropped = false;
        var announceFull = false;
        lock (_lock)
        {
            if (_bySignature.TryGetValue(signature, out var node))
            {
                node.Value.Count++;
                node.Value.LastSeenUtc = now;
                return;
            }
            if (_order.Count >= MaxPending)
            {
                _dropped++;
                dropped = true;
                announceFull = !_tableFullLogged;
                _tableFullLogged = true;
            }
            else
            {
                _tableFullLogged = false;
                _bySignature[signature] = _order.AddLast(new Pending
                {
                    Signature = signature,
                    Error = clean,
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                });
            }
        }

        if (!dropped) return;
        _floods.Dropped(ErrorIntakeFloods.GatewayLog, 1, now);
        // Once per episode, and in the reporter's own tag, so the announcement is never itself a report.
        if (announceFull)
            FileLog.Write($"{ErrorLine.ReporterTag} gateway: {MaxPending} distinct errors are waiting to be stored; new ones are dropped and counted until some are stored");
    }

    /// <summary>
    /// Write what is waiting: first every flood hour that has closed (never subject to any budget), then the waiting
    /// errors within this hour's budget. Returns the number of rows written. Never throws for a store that cannot be
    /// written - the rows are kept, retried, and in the end counted as dropped, and each failure is logged.
    /// </summary>
    public int Flush() => FlushCore(final: false);

    private int FlushCore(bool final)
    {
        lock (_flushLock)
        {
            var now = _clock();
            var written = 0;

            var floods = final ? _floods.TakeAll() : _floods.TakeClosed(now);
            if (floods.Count > 0)
            {
                if (TryWrite(floods.Select(f => f.ToRecord(now, _facts)).ToList(), out var failure))
                {
                    written += floods.Count;
                }
                else
                {
                    // Kept, never dropped: a flood record is the one row that says reports were lost.
                    _floods.Restore(floods);
                    FileLog.Write($"{ErrorLine.ReporterTag} gateway: {floods.Count} flood record(s) not stored ({failure!.GetType().Name}): {failure.Message}; kept, next flush tries again");
                }
            }

            List<Pending> batch;
            lock (_lock)
            {
                var budget = final ? _order.Count : MaxStoredPerHour - StoredInLastHour(now);
                if (_order.Count == 0 || budget <= 0) return written;
                batch = _order.Take(budget).ToList();
                foreach (var p in batch)
                {
                    _order.Remove(_bySignature[p.Signature]);
                    _bySignature.Remove(p.Signature);
                }
            }

            if (TryWrite(batch.Select(p => ToRecord(p, now)).ToList(), out var error))
            {
                lock (_lock) _storedWindow.Enqueue((now, batch.Count));
                return written + batch.Count;
            }

            var givenUp = Requeue(batch, now);
            FileLog.Write($"{ErrorLine.ReporterTag} gateway: {batch.Count} error(s) not stored ({error!.GetType().Name}): {error.Message}; "
                + (givenUp > 0 ? $"{givenUp} given up after {MaxAttempts} attempts and counted as dropped" : "kept for the next flush"));
            return written;
        }
    }

    /// <summary>One write to the store. While it runs, nothing this thread logs is observed - the store's own failure
    /// lines included - so an unwritable store cannot feed itself.</summary>
    private bool TryWrite(List<ErrorReportRecord> records, out Exception? failure)
    {
        failure = null;
        _writing = true;
        try
        {
            _store.Append(records);
            Interlocked.Add(ref _stored, records.Count);
            return true;
        }
        catch (Exception ex) when (ex is StoreBusyException or IOException or UnauthorizedAccessException)
        {
            failure = ex;
            return false;
        }
        finally
        {
            _writing = false;
        }
    }

    /// <summary>Put a failed batch back at the front, except what has used up its attempts. Returns how many were given up.</summary>
    private int Requeue(List<Pending> batch, DateTime now)
    {
        var givenUp = 0;
        lock (_lock)
        {
            for (var i = batch.Count - 1; i >= 0; i--)
            {
                var p = batch[i];
                p.Attempts++;
                if (_bySignature.TryGetValue(p.Signature, out var again))
                {
                    again.Value.Count += p.Count;
                    if (p.FirstSeenUtc < again.Value.FirstSeenUtc) again.Value.FirstSeenUtc = p.FirstSeenUtc;
                    continue;
                }
                if (p.Attempts >= MaxAttempts || _order.Count >= MaxPending)
                {
                    givenUp++;
                    continue;
                }
                _bySignature[p.Signature] = _order.AddFirst(p);
            }
            _dropped += givenUp;
        }
        if (givenUp > 0) _floods.Dropped(ErrorIntakeFloods.GatewayLog, givenUp, now);
        return givenUp;
    }

    private ErrorReportRecord ToRecord(Pending p, DateTime now) => new()
    {
        ReceivedUtc = now,
        Component = ErrorReportLimits.Gateway,
        Account = p.Error.Account,
        Device = "",
        MachineId = _facts.MachineId,
        ProductVersion = _facts.ProductVersion,
        Os = _facts.Os,
        OsVersion = _facts.OsVersion,
        Arch = _facts.Arch,
        Source = p.Error.Source,
        Kind = p.Error.Kind,
        Message = p.Error.Message,
        ExceptionType = p.Error.ExceptionType,
        Stack = p.Error.Stack,
        RepeatCount = p.Count,
        FirstSeenUtc = p.FirstSeenUtc,
        LastSeenUtc = p.LastSeenUtc,
        UserVisible = p.Error.UserVisible,
        Surface = p.Error.Surface,
        Action = p.Error.Action,
        CorrelationId = p.Error.CorrelationId,
        HttpStatus = p.Error.HttpStatus,
        ErrorCode = p.Error.ErrorCode,
        SessionId = p.Error.SessionId,
    };

    private int StoredInLastHour(DateTime now)
    {
        var cutoff = now - TimeSpan.FromHours(1);
        while (_storedWindow.Count > 0 && _storedWindow.Peek().At < cutoff) _storedWindow.Dequeue();
        return _storedWindow.Sum(e => e.Count);
    }

    private async Task RunLoopAsync()
    {
        using var timer = new PeriodicTimer(FlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                // One bad tick costs that tick, never the loop.
                try
                {
                    Flush();
                }
                catch (Exception ex)
                {
                    FileLog.Write($"{ErrorLine.ReporterTag} gateway: flush failed this tick ({ex.GetType().Name}): {ex.Message}; next tick tries again");
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }

    private static string? NullIfEmpty(string value) => value.Length > 0 ? value : null;

    /// <summary>Detach from the log, stop the timer, and write what is left - the running flood hour included.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_attached)
        {
            FileLog.ErrorObserver -= OnLogLine;
            _attached = false;
        }
        _stop.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException ex)
        {
            FileLog.Write($"{ErrorLine.ReporterTag} gateway: the flush loop ended with {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}");
        }
        var written = FlushCore(final: true);
        FileLog.Write($"{ErrorLine.ReporterTag} gateway: stopped; wrote {written} row(s) on the way out, stored={Stored}, dropped={Dropped}, still waiting={PendingCount}");
        _stop.Dispose();
    }
}
