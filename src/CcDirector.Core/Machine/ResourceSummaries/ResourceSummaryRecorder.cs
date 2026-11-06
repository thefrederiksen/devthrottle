using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Machine.ResourceSummaries;

/// <summary>
/// Collects five-second samples into five-minute windows and hands each closed window to the outbox (Mission: Resource
/// Monitor, section 6). The sampler (phase 2) calls <see cref="Add"/> with every sample it takes; nothing else is
/// needed for summaries to reach the Gateway, because the tunnel drains the outbox on every reseed.
///
/// A window closes on the first sample that belongs to a different window - normally the first sample after the
/// five-minute boundary. A sample from an EARLIER window (the machine's clock moved back) also closes the current one
/// and starts that earlier window afresh; the Gateway keeps the first copy of a window it already holds, and the
/// outbox logs the replacement. <see cref="CloseIfDue"/> closes a window whose time is up when no later sample has
/// come, so a sampler that stops does not strand its last window.
///
/// Thread-safe.
/// </summary>
public sealed class ResourceSummaryRecorder
{
    private readonly object _gate = new();
    private readonly ResourceSummaryOutbox _outbox;
    private readonly string _machineName;
    private DateTime? _windowStart;
    private List<ResourceSummarySample> _samples = new();

    public ResourceSummaryRecorder(ResourceSummaryOutbox outbox, string machineName)
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        if (string.IsNullOrWhiteSpace(machineName))
            throw new ArgumentException("The recorder needs the machine's name.", nameof(machineName));
        _machineName = machineName.Trim();
        FileLog.Write($"[ResourceSummaryRecorder] ctor: recording five-minute windows for machine={_machineName}");
    }

    /// <summary>Add one five-second sample. Closes the current window first when the sample belongs to another.</summary>
    public void Add(ResourceSummarySample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var start = ResourceSummaryContract.WindowStartFor(sample.AtUtc);
        lock (_gate)
        {
            if (_windowStart is { } current && current != start)
            {
                if (start < current)
                    FileLog.Write($"[ResourceSummaryRecorder] Add: sample at {sample.AtUtc:O} is earlier than the open window {current:O} - the clock moved back; closing the open window");
                CloseLocked();
            }
            _windowStart ??= start;
            _samples.Add(sample);
        }
    }

    /// <summary>Close the open window if its five minutes are over by <paramref name="nowUtc"/>. Returns true when a
    /// window was closed.</summary>
    public bool CloseIfDue(DateTime nowUtc)
    {
        lock (_gate)
        {
            if (_windowStart is not { } current) return false;
            if (nowUtc < current.AddSeconds(ResourceSummaryContract.WindowSeconds)) return false;
            CloseLocked();
            return true;
        }
    }

    private void CloseLocked()
    {
        var start = _windowStart!.Value;
        var window = ResourceSummaryBuilder.Build(start, _machineName, _samples);
        FileLog.Write($"[ResourceSummaryRecorder] window closed: start={start:O} samples={window.SampleCount} sessions={window.Sessions.Count} "
                      + $"crossings={window.Machine.Crossings.Count}");
        _outbox.Add(window);
        _windowStart = null;
        _samples = new List<ResourceSummarySample>();
    }
}
