using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Machine.ResourceSummaries;

/// <summary>
/// The closed five-minute windows this Director has not yet seen the Gateway acknowledge (Mission: Resource Monitor,
/// section 6). A window stays here until the Gateway says it holds it, so a Gateway that is unreachable for ten minutes
/// leaves no gap: the two windows that closed while it was gone are sent when the tunnel comes back.
///
/// BOUNDED AT 24 HOURS. A window that closed more than <see cref="ResourceSummaryContract.BufferSpan"/> ago by this Director's clock is
/// dropped, and so is the oldest window once <see cref="ResourceSummaryContract.MaxBufferedWindows"/> are held, so a
/// Director that never reaches its Gateway cannot grow without limit. Every drop is logged. The local recording still
/// has every five-second sample of a dropped window.
///
/// IN MEMORY. A Director restart loses what is buffered here. That is accepted for version 1: the windows a restart
/// loses are in the local recording, and a restart is short beside the 24 hours this exists to cover.
///
/// Keyed by window start: adding a window whose start is already held replaces it (a clock that jumped back can
/// close the same window twice), and the replacement is logged.
///
/// Thread-safe: the sampler adds on its timer while the tunnel's reseed drains on another thread.
/// </summary>
public sealed class ResourceSummaryOutbox
{
    private readonly object _gate = new();
    private readonly SortedDictionary<DateTime, ResourceSummaryWindow> _windows = new();
    private readonly TimeProvider _time;
    private long _dropped;

    public ResourceSummaryOutbox(TimeProvider time)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>How many windows are waiting.</summary>
    public int Count
    {
        get { lock (_gate) return _windows.Count; }
    }

    /// <summary>How many windows have been dropped, ever, for age or for room.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Hold a closed window until the Gateway acknowledges it.</summary>
    public void Add(ResourceSummaryWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var start = DateTime.SpecifyKind(window.WindowStartUtc, DateTimeKind.Utc);
        lock (_gate)
        {
            if (_windows.ContainsKey(start))
                FileLog.Write($"[ResourceSummaryOutbox] Add: window {start:O} was already held and is replaced (the clock moved back)");
            _windows[start] = window;
            PruneLocked();
            FileLog.Write($"[ResourceSummaryOutbox] Add: window {start:O} held, {_windows.Count} waiting");
        }
    }

    /// <summary>The oldest waiting windows, oldest first, at most <paramref name="max"/>. Nothing is removed: a window
    /// leaves only when <see cref="Acknowledge"/> is told the Gateway holds it.</summary>
    public IReadOnlyList<ResourceSummaryWindow> Peek(int max)
    {
        if (max <= 0) throw new ArgumentOutOfRangeException(nameof(max), "Peek needs a positive count.");
        lock (_gate)
        {
            PruneLocked();
            return _windows.Values.Take(max).ToList();
        }
    }

    /// <summary>Remove the windows the Gateway holds or refused for good. Returns how many were removed.</summary>
    public int Acknowledge(IEnumerable<DateTime> windowStartsUtc)
    {
        ArgumentNullException.ThrowIfNull(windowStartsUtc);
        var removed = 0;
        lock (_gate)
        {
            foreach (var start in windowStartsUtc)
                if (_windows.Remove(DateTime.SpecifyKind(start, DateTimeKind.Utc))) removed++;
            FileLog.Write($"[ResourceSummaryOutbox] Acknowledge: {removed} window(s) removed, {_windows.Count} waiting");
        }
        return removed;
    }

    private void PruneLocked()
    {
        // Measured from when a window CLOSED (its end), so a window is kept for a full 24 hours after it was ready to send.
        var oldestEndKept = _time.GetUtcNow().UtcDateTime - ResourceSummaryContract.BufferSpan;
        var tooOld = _windows.Keys.Where(k => k.AddSeconds(ResourceSummaryContract.WindowSeconds) < oldestEndKept).ToList();
        foreach (var start in tooOld)
        {
            _windows.Remove(start);
            Interlocked.Increment(ref _dropped);
        }
        if (tooOld.Count > 0)
            FileLog.Write($"[ResourceSummaryOutbox] dropped {tooOld.Count} window(s) older than 24 hours that the Gateway never acknowledged "
                          + $"(oldest {tooOld[0]:O}); the local recording still has their samples");

        var overflow = _windows.Count - ResourceSummaryContract.MaxBufferedWindows;
        if (overflow <= 0) return;
        var oldest = _windows.Keys.Take(overflow).ToList();
        foreach (var start in oldest)
        {
            _windows.Remove(start);
            Interlocked.Increment(ref _dropped);
        }
        FileLog.Write($"[ResourceSummaryOutbox] dropped the {overflow} oldest window(s) to stay within 24 hours of windows "
                      + $"(oldest {oldest[0]:O})");
    }
}
