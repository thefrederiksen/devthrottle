using System.Threading.Channels;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// Writes the Wingman inspector's traces OFF the verdict path.
///
/// WHY IT EXISTS. The trace is an observer: a verdict that is already stored must not wait for its copy to be
/// written, and a copy that cannot be written must not change the verdict. Written inline, the trace held the
/// flight open while it serialized a whole prompt and waited on the store's write lock - which the retention purge
/// also takes - so a person pressing Explain could wait on a purge, and a store fault landed inside the flight's
/// exception boundary and replaced a good verdict with a failed one. Handing the trace to this writer costs the
/// flight a few string cuts, a character count and one non-blocking queue write, none of which can throw.
///
/// BOUNDED IN MEMORY, NOT ONLY IN COUNT. The prompt, the raw reply and a refusal reason are cut to the store's
/// ceilings, and an oversized package is dropped, BEFORE a trace is queued (<see cref="TurnVerdictTraceStore.ApplyCeilings"/>), and
/// the queue holds <see cref="Capacity"/> traces.
///
/// A TRACE THAT CANNOT BE WRITTEN IS LOGGED AND COUNTED, NOT RECORDED IN THE TABLE - and that is a ruling. The two
/// ways it happens, a full queue and a failed write, are both the database not taking writes, and a marker row
/// written to that same database cannot be promised either. An earlier version tried: the marker needed its own
/// retry, its own ceiling and its own place in the queue, and review found a new way to lose each one. So the loss
/// is written where it can be: the Gateway log names the session, the stop's moment and the outcome of every trace
/// that was not kept, and <see cref="Dropped"/>, <see cref="Failed"/>, <see cref="Abandoned"/> and <see cref="Lost"/> count them.
///
/// ONE LIMIT THIS CANNOT REMOVE, stated rather than hidden: a write already inside the database call when shutdown
/// gives up waiting cannot be recalled. It finishes or fails on its own, possibly against a database being disposed
/// (a failure is caught and logged), and if the process ends first, that one trace has no line of its own - the
/// host's timeout line is its only record. Every trace still QUEUED at that moment is logged and counted by
/// <see cref="Abandon"/> itself, without waiting for that write.
///
/// ONE READER, IN ORDER. A single loop writes the traces in the order they were handed in, so one session's history
/// reads in the order it happened. The loop is this writer's entry point, and the only place an append fault is
/// caught.
/// </summary>
public sealed class TurnVerdictTraceWriter : IDisposable
{
    /// <summary>How many traces may wait to be written. A turn end writes one; a fleet of thirty stopping at once
    /// is thirty, so this is headroom for a database that is briefly slow, not a backlog to live in.</summary>
    public const int Capacity = 64;

    private readonly Action<TenantId, TurnVerdictTrace> _append;
    private readonly Func<TenantId, TurnVerdictTrace, TurnVerdictTrace>? _stamp;
    private readonly Channel<(TenantId Tenant, TurnVerdictTrace Trace)> _queue;
    private readonly Task _loop;
    private volatile bool _abandoned;
    private long _dropped;
    private long _failed;
    private long _written;
    private long _abandonedCount;
    private long _lost;

    /// <param name="append">Writes one trace. Production passes <see cref="TurnVerdictTraceStore.Append"/>.</param>
    /// <param name="stamp">Completes a trace just before it is written, on this writer's thread and never on the verdict
    /// path. Production passes <see cref="TurnVerdictTraceRowStamp.Stamp"/>, which records the colour the stop produced.
    /// A stamp that throws is a write that failed, logged and counted like any other.</param>
    public TurnVerdictTraceWriter(Action<TenantId, TurnVerdictTrace> append,
        Func<TenantId, TurnVerdictTrace, TurnVerdictTrace>? stamp = null)
    {
        _append = append ?? throw new ArgumentNullException(nameof(append));
        _stamp = stamp;
        // FullMode is Wait, and that is not a choice to block. Enqueue only ever calls TryWrite, which never waits and
        // answers false on a full queue. DropWrite would be the wrong mode: under it TryWrite answers TRUE and throws
        // the trace away, so every drop would be silent and uncounted - the test for a full queue caught exactly that.
        _queue = Channel.CreateBounded<(TenantId, TurnVerdictTrace)>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            // Two readers: the loop, and Abandon, which empties the queue itself rather than wait for a loop that may
            // be stuck inside a database call.
            SingleReader = false,
            SingleWriter = false,
        });
        _loop = Task.Run(RunAsync);
    }

    /// <summary>Traces refused because the queue was full or closed.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Traces whose write threw.</summary>
    public long Failed => Interlocked.Read(ref _failed);

    /// <summary>Traces written.</summary>
    public long Written => Interlocked.Read(ref _written);

    /// <summary>Traces still queued when the writer was abandoned, and so never written.</summary>
    public long Abandoned => Interlocked.Read(ref _abandonedCount);

    /// <summary>
    /// Hand one trace to the writer. Never blocks and never throws: the trace is cut to its ceilings, then queued.
    /// Answers false when it was not queued - the queue was full, or the writer is closing - and that is logged and
    /// counted.
    /// </summary>
    public bool Enqueue(TenantId tenant, TurnVerdictTrace trace)
    {
        if (trace is null) return false;
        if (_queue.Writer.TryWrite((tenant, TurnVerdictTraceStore.ApplyCeilings(trace)))) return true;
        Interlocked.Increment(ref _dropped);
        FileLog.Write($"[TurnVerdictTraceWriter] trace NOT KEPT (queue full or closing): outcome={trace.Outcome} " +
                      $"sid={trace.SessionId} observed={trace.TurnEndObservedAtUtc:O} dropped={Dropped}");
        return false;
    }

    /// <summary>Traces the verdict seat could not hand in at all - see <see cref="NotKept"/>.</summary>
    public long Lost => Interlocked.Read(ref _lost);

    /// <summary>
    /// A trace the verdict seat could not offer to the queue: no judgement for the session read its settings, so nothing
    /// can say whether the account is traced, or building the trace failed. It is the same kind of loss as a drop, so it
    /// is logged and counted HERE, beside the others, and the seat never logs a loss of its own. Never blocks, never throws.
    /// </summary>
    public void NotKept(TurnVerdictTrace trace, string cause)
    {
        if (trace is null) return;
        Interlocked.Increment(ref _lost);
        FileLog.Write($"[TurnVerdictTraceWriter] trace NOT KEPT ({cause}): outcome={trace.Outcome} " +
                      $"sid={trace.SessionId} observed={trace.TurnEndObservedAtUtc:O} lost={Lost}");
    }

    /// <summary>Stop taking traces and wait until every queued trace has been written. For shutdown and tests.</summary>
    public async Task CompleteAsync()
    {
        _queue.Writer.TryComplete();
        await _loop.ConfigureAwait(false);
    }

    /// <summary>
    /// Stop writing. For a shutdown whose drain ran out of time, so nothing more goes into a database that is being
    /// disposed. Every trace still queued is taken out, logged and counted HERE, on the caller's thread, and not by the
    /// loop - found in review: the loop may be stuck inside the very database call that made the drain time out, and a
    /// count that waits for it may never happen. A write already inside the database call cannot be recalled.
    /// </summary>
    public void Abandon()
    {
        _abandoned = true;
        _queue.Writer.TryComplete();
        while (_queue.Reader.TryRead(out var queued))
            NotKeptAtAbandon(queued.Trace);
    }

    private void NotKeptAtAbandon(TurnVerdictTrace trace)
    {
        Interlocked.Increment(ref _abandonedCount);
        FileLog.Write($"[TurnVerdictTraceWriter] trace NOT KEPT (writer abandoned at shutdown): outcome={trace.Outcome} " +
                      $"sid={trace.SessionId} observed={trace.TurnEndObservedAtUtc:O}");
    }

    private async Task RunAsync()
    {
        await foreach (var (tenant, trace) in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (_abandoned)
            {
                NotKeptAtAbandon(trace);
                continue;
            }

            try
            {
                _append(tenant, _stamp is null ? trace : _stamp(tenant, trace));
                Interlocked.Increment(ref _written);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failed);
                FileLog.Write($"[TurnVerdictTraceWriter] trace NOT KEPT (write failed): outcome={trace.Outcome} sid={trace.SessionId} " +
                              $"observed={trace.TurnEndObservedAtUtc:O} verdict={trace.VerdictId}: {ex.GetType().FullName}: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
    }
}
