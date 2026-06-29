using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>
/// Allocates short, human-friendly three-digit session numbers (100-999) that are unique among the
/// currently-active sessions of ONE Director (issue #820). Thread-safe.
///
/// Per the issue's resolved design decision, the Director ALWAYS allocates locally so a session
/// always gets a number at creation, even when no Gateway is reachable. The Gateway is the authority
/// for fleet-wide global uniqueness only when reachable; the accepted trade-off is that two sessions
/// on different Directors may share a number when no Gateway coordinates them.
///
/// A number is released when its session ends and may later be reused. The allocator deliberately
/// avoids handing back the most-recently-freed numbers first, so a code a human just saw leave is
/// not instantly reused for a different session.
/// </summary>
public sealed class SessionNumberAllocator
{
    /// <summary>Lowest assignable number (inclusive). Codes below this are never used.</summary>
    public const int MinNumber = 100;

    /// <summary>Highest assignable number (inclusive).</summary>
    public const int MaxNumber = 999;

    /// <summary>Total size of the number pool (900 distinct codes).</summary>
    public const int PoolCapacity = MaxNumber - MinNumber + 1;

    /// <summary>How many freed numbers to hold back from immediate reuse.</summary>
    private const int RecentlyFreedHoldback = 16;

    private readonly object _lock = new();
    private readonly HashSet<int> _inUse = new();
    private readonly Queue<int> _recentlyFreed = new();

    /// <summary>Count of numbers currently reserved (active sessions). For tests and diagnostics.</summary>
    public int InUseCount
    {
        get { lock (_lock) return _inUse.Count; }
    }

    /// <summary>True when <paramref name="number"/> is currently reserved.</summary>
    public bool IsReserved(int number)
    {
        lock (_lock) return _inUse.Contains(number);
    }

    /// <summary>
    /// Allocate a free number in [<see cref="MinNumber"/>, <see cref="MaxNumber"/>], preferring a
    /// number that was not just freed. Returns null only when every number in the range is currently
    /// in use (pool exhausted) - the caller then creates the session WITHOUT a number rather than
    /// blocking real work over a cosmetic handle.
    /// </summary>
    public int? Allocate()
    {
        lock (_lock)
        {
            if (_inUse.Count >= PoolCapacity)
            {
                FileLog.Write($"[SessionNumberAllocator] Allocate: pool exhausted ({_inUse.Count} in use), returning no number");
                return null;
            }

            // First pass: pick the lowest free number that is not in the recently-freed holdback,
            // so a code a human just saw leave is not reused immediately.
            var chosen = FirstFree(skipHoldback: true);

            // Every free number is in the holdback (heavy churn in a small pool): take any free one.
            chosen ??= FirstFree(skipHoldback: false);

            // The capacity check above guarantees a free number exists, so this is a programming
            // error, not an expected condition - fail loud rather than return a fake value.
            if (chosen is not int number)
                throw new InvalidOperationException(
                    "SessionNumberAllocator: no free number found despite available capacity.");

            _inUse.Add(number);
            FileLog.Write($"[SessionNumberAllocator] Allocate: assigned {number} ({_inUse.Count} in use)");
            return number;
        }
    }

    /// <summary>
    /// Reserve a SPECIFIC number (used when re-applying a persisted number on restore, issue #820).
    /// Returns false when the number is out of range or already reserved, so the caller can fall back
    /// to allocating a fresh one. Caller must hold no assumption that the number was free.
    /// </summary>
    public bool TryReserve(int number)
    {
        if (number < MinNumber || number > MaxNumber)
        {
            FileLog.Write($"[SessionNumberAllocator] TryReserve: {number} out of range [{MinNumber},{MaxNumber}], refused");
            return false;
        }

        lock (_lock)
        {
            if (!_inUse.Add(number))
            {
                FileLog.Write($"[SessionNumberAllocator] TryReserve: {number} already in use, refused");
                return false;
            }
            FileLog.Write($"[SessionNumberAllocator] TryReserve: reserved {number} ({_inUse.Count} in use)");
            return true;
        }
    }

    /// <summary>
    /// Release a number back to the pool when its session ends. The number is held back from
    /// immediate reuse for a short while. Releasing a number that is not reserved is a harmless no-op.
    /// </summary>
    public void Release(int number)
    {
        lock (_lock)
        {
            if (!_inUse.Remove(number))
            {
                FileLog.Write($"[SessionNumberAllocator] Release: {number} was not in use, no-op");
                return;
            }

            _recentlyFreed.Enqueue(number);
            while (_recentlyFreed.Count > RecentlyFreedHoldback)
                _recentlyFreed.Dequeue();

            FileLog.Write($"[SessionNumberAllocator] Release: freed {number} ({_inUse.Count} in use)");
        }
    }

    /// <summary>Lowest free number, optionally skipping the recently-freed holdback. Caller holds the lock.</summary>
    private int? FirstFree(bool skipHoldback)
    {
        var holdback = skipHoldback ? new HashSet<int>(_recentlyFreed) : null;
        for (int n = MinNumber; n <= MaxNumber; n++)
        {
            if (_inUse.Contains(n)) continue;
            if (holdback is not null && holdback.Contains(n)) continue;
            return n;
        }
        return null;
    }
}
