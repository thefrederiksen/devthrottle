using System.Collections.Concurrent;
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
/// flight one non-blocking queue write and can do neither.
///
/// BOUNDED IN MEMORY, NOT ONLY IN COUNT. A trace is cut to the store's ceilings BEFORE it is queued
/// (<see cref="TurnVerdictTraceStore.ApplyCeilings"/>), so a queued trace can never hold more than the row it will
/// become. At <see cref="Capacity"/> traces that bounds the queue at about 64 x 450,000 characters - under 60
/// megabytes in the worst case where every trace is at every ceiling at once, and a few megabytes in practice.
///
/// A TRACE THAT IS NOT WRITTEN LEAVES A ROW SAYING SO. A full queue drops the trace, and a write can fail. Either
/// way a small "lost" row - the session, the stop's observed moment, the outcome that was lost and why, and no
/// content - is written as soon as the database takes writes again, so the inspector shows a gap where a stop was
/// rather than nothing. That holds while this process lives: a Gateway that stops with gap rows still pending, or
/// a database that never comes back before it stops, loses them, and each is logged when it is noted. At most
/// <see cref="MaxPendingGaps"/> gap rows wait at once; past that a loss is logged only.
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

    /// <summary>How many "lost" gap rows may wait to be written at once.</summary>
    public const int MaxPendingGaps = 1_000;

    /// <summary>The cause a gap row carries when its trace was dropped from a full queue.</summary>
    public const string QueueFullCause = "queue-full";

    /// <summary>The cause a gap row carries when its trace's write threw.</summary>
    public const string WriteFailedCause = "write-failed";

    private readonly Action<TenantId, TurnVerdictTrace> _append;
    private readonly Channel<(TenantId Tenant, TurnVerdictTrace Trace)> _queue;
    private readonly ConcurrentQueue<(TenantId Tenant, TurnVerdictTrace Gap)> _gaps = new();
    private readonly Task _loop;
    private int _pendingGaps;
    private long _dropped;
    private long _failed;
    private long _written;

    /// <param name="append">Writes one trace. Production passes <see cref="TurnVerdictTraceStore.Append"/>.</param>
    public TurnVerdictTraceWriter(Action<TenantId, TurnVerdictTrace> append)
    {
        _append = append ?? throw new ArgumentNullException(nameof(append));
        // FullMode is Wait, and that is not a choice to block. Enqueue only ever calls TryWrite, which never waits and
        // answers false on a full queue. DropWrite would be the wrong mode: under it TryWrite answers TRUE and throws
        // the trace away, so every drop would be silent and uncounted - the test for a full queue caught exactly that.
        _queue = Channel.CreateBounded<(TenantId, TurnVerdictTrace)>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        _loop = Task.Run(RunAsync);
    }

    /// <summary>Traces dropped because the queue was full or closed.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Writes that threw - traces and gap rows alike.</summary>
    public long Failed => Interlocked.Read(ref _failed);

    /// <summary>Rows written - traces and gap rows alike.</summary>
    public long Written => Interlocked.Read(ref _written);

    /// <summary>
    /// Hand one trace to the writer. Never blocks and never throws: the trace is cut to its ceilings, then queued.
    /// Answers false when it was not queued - the queue was full, or the writer is closing - and that is logged,
    /// counted, and noted as a gap row.
    /// </summary>
    public bool Enqueue(TenantId tenant, TurnVerdictTrace trace)
    {
        if (trace is null) return false;
        var bounded = TurnVerdictTraceStore.ApplyCeilings(trace);
        if (_queue.Writer.TryWrite((tenant, bounded))) return true;
        Interlocked.Increment(ref _dropped);
        FileLog.Write($"[TurnVerdictTraceWriter] trace DROPPED (queue full or closing): outcome={trace.Outcome} sid={trace.SessionId} dropped={Dropped}");
        NoteGap(tenant, trace, QueueFullCause);
        return false;
    }

    /// <summary>Stop taking traces and wait until every queued trace and pending gap row has been written. For
    /// shutdown and tests.</summary>
    public async Task CompleteAsync()
    {
        _queue.Writer.TryComplete();
        await _loop.ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        await foreach (var (tenant, trace) in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (!TryAppend(tenant, trace))
                NoteGap(tenant, trace, WriteFailedCause);
            WritePendingGaps();
        }
        WritePendingGaps();
    }

    private void NoteGap(TenantId tenant, TurnVerdictTrace lost, string cause)
    {
        if (Interlocked.Increment(ref _pendingGaps) > MaxPendingGaps)
        {
            Interlocked.Decrement(ref _pendingGaps);
            FileLog.Write($"[TurnVerdictTraceWriter] gap NOT noted ({MaxPendingGaps} already pending): outcome={lost.Outcome} sid={lost.SessionId}");
            return;
        }
        _gaps.Enqueue((tenant, TurnVerdictTraceStore.GapFor(lost, cause)));
    }

    private void WritePendingGaps()
    {
        while (_gaps.TryDequeue(out var gap))
        {
            Interlocked.Decrement(ref _pendingGaps);
            // A gap row that cannot be written is logged and let go: re-noting it would loop on a database that is down.
            TryAppend(gap.Tenant, gap.Gap);
        }
    }

    private bool TryAppend(TenantId tenant, TurnVerdictTrace trace)
    {
        try
        {
            _append(tenant, trace);
            Interlocked.Increment(ref _written);
            return true;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failed);
            FileLog.Write($"[TurnVerdictTraceWriter] append FAILED: outcome={trace.Outcome} sid={trace.SessionId} " +
                          $"verdict={trace.VerdictId}: {ex.GetType().FullName}: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
    }
}
