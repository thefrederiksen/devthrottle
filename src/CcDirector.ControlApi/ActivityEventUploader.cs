using CcDirector.Core.Activity;
using CcDirector.Core.Utilities;

namespace CcDirector.ControlApi;

/// <summary>
/// Drains the Director's durable activity-event outbox to the Gateway ledger
/// (docs/PLAN-trustworthy-working-start-2026-07-24.md, increment 2). A timer wakes every
/// <see cref="Interval"/>, pushes the oldest batch through the current <see cref="GatewayClient"/>, and
/// deletes from the outbox exactly the events the Gateway confirmed it durably holds - written now or an
/// already-held duplicate; both are acknowledgement. A failed or partial push deletes nothing and simply
/// retries on the next tick with the SAME minted identities, which is what keeps the ledger loss-free and
/// duplicate-free across crashes and reconnects.
///
/// The Gateway client is resolved per tick (the same late-binding the prompt sink uses) so a Gateway
/// reconfigure is picked up without rewiring.
/// </summary>
public sealed class ActivityEventUploader : IDisposable
{
    /// <summary>How often the outbox is drained. Evidence is diagnostic, not interactive - half a minute
    /// of delivery lag is invisible to its consumers, and the tick is a no-op when the outbox is empty.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>The Gateway's own batch ceiling (ActivityEventStore.MaxBatchSize).</summary>
    public const int BatchSize = 500;

    private readonly Func<GatewayClient?> _gateway;
    private readonly ActivityEventOutbox _outbox;
    private readonly CcDirector.Core.Background.BackgroundJobs _jobs;
    private CcDirector.Core.Background.BackgroundJob? _job;
    private int _inFlight;
    private int _disposed;

    /// <param name="jobs">The scheduler the drain registers with; the process-wide one when null.</param>
    public ActivityEventUploader(Func<GatewayClient?> gateway, ActivityEventOutbox outbox, CcDirector.Core.Background.BackgroundJobs? jobs = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _jobs = jobs ?? CcDirector.Core.Background.BackgroundJobs.Default;
    }

    /// <summary>A slow-and-steady job on the scheduler (docs/BackgroundWork.md): first after one
    /// interval, then every interval, never overlapping, off the window's thread.</summary>
    public void Start()
    {
        _job = _jobs.Register(
            new CcDirector.Core.Background.BackgroundJobSpec("Activity event outbox", CcDirector.Core.Background.BackgroundJobTier.SlowAndSteady, Interval, "the uploader is disposed"),
            _ => OnTickAsync());
        _job.StartTimer();
        FileLog.Write($"[ActivityEventUploader] started: every {Interval.TotalSeconds:0}s, batch {BatchSize}, pending {_outbox.PendingCount}");
    }

    private Task OnTickAsync()
    {
        if (Volatile.Read(ref _disposed) != 0) return Task.CompletedTask;
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0) return Task.CompletedTask;
        return DrainAsync();
    }

    private async Task DrainAsync()
    {
        try
        {
            // Keep pushing while full batches clear, so a backlog after an outage drains in one tick
            // rather than one batch per 30 seconds.
            while (true)
            {
                var batch = _outbox.PendingBatch(BatchSize);
                if (batch.Count == 0) return;

                var gateway = _gateway();
                if (gateway is null) return;

                var ack = await gateway.PushActivityEventsAsync(batch).ConfigureAwait(false);
                if (ack is null)
                    return; // not acknowledged - keep everything, retry next tick

                if (ack.Written + ack.Duplicates < batch.Count)
                {
                    // The Gateway answered but did not account for the whole batch. Keep everything and
                    // retry - the minted ids make the replay idempotent - and say so loudly, because a
                    // persistent shortfall means a producer bug worth diagnosing.
                    FileLog.Write($"[ActivityEventUploader] partial acknowledgement: sent {batch.Count}, " +
                                  $"written {ack.Written}, duplicates {ack.Duplicates}; retrying the batch");
                    return;
                }

                _outbox.Acknowledge(batch.Select(e => e.EventId));
                if (batch.Count < BatchSize)
                    return; // the outbox is drained
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ActivityEventUploader] drain FAILED: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _job?.Dispose();
    }
}
