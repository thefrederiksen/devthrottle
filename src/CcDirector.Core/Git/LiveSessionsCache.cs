using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

/// <summary>
/// Holds one answer of a live-session provider for a short time, so the repository monitor's full rescan asks
/// the Gateway for the fleet roster once per <see cref="MaxAge"/> instead of once per repository.
///
/// Why: the monitor consults its provider on EVERY compute, and a full rescan (every five minutes, plus each
/// reconciliation) computes every repository under the roots. On the owner's machine that is 75 repositories,
/// so each rescan fetched the whole fleet roster 75 times - about 68 KB each, never a 304 - which the Gateway's
/// traffic meter measured at roughly 80 MB an hour, most of the hosted Gateway's overnight data out.
///
/// What it costs: a repository computed within <see cref="MaxAge"/> of the last fetch sees a roster up to that
/// old, and the label it produces stands until that repository is next computed - which can be the next
/// five-minute rescan. So a session that starts in a new worktree just before a recompute can leave that worktree
/// shown as not in use (and counted in the orphan badge) for up to one rescan. That roster only decides how a
/// worktree is SHOWN; the reaper never reads it - it asks its own authoritative provider right before it removes
/// anything and refuses a worktree a live session is in - so a stale answer cannot cause a removal.
///
/// Fetches are serialised: a caller that arrives while a fetch is running waits for it and takes its answer,
/// rather than starting a second one. A fetch that throws is not held; the next caller fetches again. Note that
/// the Director's own provider does not throw on a Gateway failure - it answers with this Director's sessions
/// only - and that reduced answer is held for <see cref="MaxAge"/> like any other, so other slots' sessions can
/// drop out of the display for that long.
///
/// The age is measured on a monotonic clock, so setting the system clock back cannot stretch it.
/// </summary>
public sealed class LiveSessionsCache
{
    /// <summary>The default age after which the held answer is fetched again.</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromSeconds(30);

    private readonly Func<CancellationToken, Task<IReadOnlyList<LiveSessionRef>>> _fetch;
    private readonly Func<TimeSpan> _monotonicNow;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<LiveSessionRef>? _held;
    private TimeSpan _heldAt;

    public LiveSessionsCache(
        Func<CancellationToken, Task<IReadOnlyList<LiveSessionRef>>> fetch,
        TimeSpan? maxAge = null,
        Func<TimeSpan>? monotonicNow = null)
    {
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        MaxAge = maxAge ?? DefaultMaxAge;
        if (MaxAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxAge), MaxAge, "The maximum age must be positive.");
        _monotonicNow = monotonicNow ?? (() => TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    /// <summary>How long one answer is reused.</summary>
    public TimeSpan MaxAge { get; }

    /// <summary>The held answer when it is younger than <see cref="MaxAge"/>, else a fresh one.</summary>
    public async Task<IReadOnlyList<LiveSessionRef>> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_held is { } held && _monotonicNow() - _heldAt < MaxAge)
                return held;

            var fresh = await _fetch(ct);
            _held = fresh;
            _heldAt = _monotonicNow();
            FileLog.Write($"[LiveSessionsCache] fetched {fresh.Count} live session(s); reused for {MaxAge.TotalSeconds:0}s");
            return fresh;
        }
        finally
        {
            _gate.Release();
        }
    }
}
