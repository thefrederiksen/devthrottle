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
/// BOUNDED, AND A FULL QUEUE DROPS. A queue that grows without limit trades a slow database for a memory problem.
/// Past <see cref="Capacity"/> a trace is dropped, counted in <see cref="Dropped"/>, and logged - the same call the
/// turn log makes: a record that is missing and says so is honest, a Gateway that fell over is not.
///
/// ONE READER, IN ORDER. A single loop writes the traces in the order they were handed in, so one session's history
/// reads in the order it happened. The loop is this writer's entry point, and the only place an append fault is
/// caught: it is logged, counted in <see cref="Failed"/>, and the next trace is written.
/// </summary>
public sealed class TurnVerdictTraceWriter : IDisposable
{
    /// <summary>How many traces may wait to be written. A turn end writes one; a fleet of thirty stopping at once
    /// is thirty, so this is headroom for a database that is briefly slow, not a backlog to live in.</summary>
    public const int Capacity = 512;

    private readonly Action<TenantId, TurnVerdictTrace> _append;
    private readonly Channel<(TenantId Tenant, TurnVerdictTrace Trace)> _queue;
    private readonly Task _loop;
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

    /// <summary>Traces dropped because the queue was full.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Traces whose write threw.</summary>
    public long Failed => Interlocked.Read(ref _failed);

    /// <summary>Traces written.</summary>
    public long Written => Interlocked.Read(ref _written);

    /// <summary>
    /// Hand one trace to the writer. Never blocks and never throws: answers false when the trace was not queued -
    /// the queue was full, or the writer is shutting down - and that is logged and counted here.
    /// </summary>
    public bool Enqueue(TenantId tenant, TurnVerdictTrace trace)
    {
        if (trace is null) return false;
        if (_queue.Writer.TryWrite((tenant, trace))) return true;
        Interlocked.Increment(ref _dropped);
        FileLog.Write($"[TurnVerdictTraceWriter] trace DROPPED (queue full or closing): outcome={trace.Outcome} sid={trace.SessionId} dropped={Dropped}");
        return false;
    }

    /// <summary>Stop taking traces and wait for the ones already queued to be written. For shutdown and tests.</summary>
    public async Task CompleteAsync()
    {
        _queue.Writer.TryComplete();
        await _loop.ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        await foreach (var (tenant, trace) in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                _append(tenant, trace);
                Interlocked.Increment(ref _written);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failed);
                FileLog.Write($"[TurnVerdictTraceWriter] trace append FAILED: outcome={trace.Outcome} sid={trace.SessionId} " +
                              $"verdict={trace.VerdictId}: {ex.GetType().FullName}: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
    }
}
