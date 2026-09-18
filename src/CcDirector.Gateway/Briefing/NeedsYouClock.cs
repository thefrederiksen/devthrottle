using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Briefing;

/// <summary>
/// Issue #218: the Gateway-owned per-session clock recording WHEN a session entered the
/// red / NEEDS-YOU effective state, so the Cockpit can show how long it has been waiting.
///
/// In-memory by design (derived state, not the durable record) and re-derived after a
/// Gateway restart on the next red transition.
///
/// The single rule, applied once per session per /sessions aggregation:
///   isRed (EffectiveColor == "red") and no timestamp yet -> stamp UtcNow (entry).
///   isRed and a timestamp already stands              -> hold it (waiting since the same
///                                                        moment - the value never advances
///                                                        while the session stays red).
///   not red                                           -> clear it (leaving red ends the
///                                                        waiting episode; a later re-entry
///                                                        stamps a strictly-later moment).
///
/// EffectiveColor folds in OnHold / Briefing / Explaining (see <see cref="Contracts.SessionOrdering"/>),
/// so a session the wingman is still reading is effective yellow/orange, not red, and is
/// correctly treated as not-yet-waiting here.
///
/// Hosted Multi-Tenancy (MTR-10 Gap C): the clock is keyed by (tenant, sessionId), never the
/// bare session id. Two accounts can run sessions with the SAME id; a bare-sid key let one
/// tenant's "left red" clear the other tenant's entry, or one tenant's held stamp be reported
/// as the other's "waiting since". The fold hands the OWNING tenant of the row it is stamping
/// (the roster's request tenant, the display push's ambient tenant), so each tenant's clock is
/// its own. Self-host resolves to the single <see cref="TenantId.Local"/> partition, unchanged.
/// </summary>
public sealed class NeedsYouClock
{
    private readonly ConcurrentDictionary<(TenantId Tenant, string SessionId), DateTime> _since = new();

    /// <summary>What <see cref="Stamp"/> would answer right now, WITHOUT entering or leaving red. For a fold that must
    /// not move this clock (the Wingman inspector's trace colour).</summary>
    public DateTime? Peek(TenantId tenant, string sessionId, bool isRed)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        if (!isRed) return null;
        return _since.TryGetValue((tenant, sessionId), out var since) ? since : DateTime.UtcNow;
    }

    /// <summary>
    /// Apply the entry/hold/clear rule for one session and return the timestamp to stamp on
    /// its <see cref="Contracts.SessionDto.NeedsYouSince"/> (UTC), or null when it is not red.
    /// </summary>
    /// <param name="tenant">The tenant that owns the session being stamped (MTR-10 Gap C).</param>
    /// <param name="sessionId">The session's stable id.</param>
    /// <param name="isRed">Whether the session's EffectiveColor is "red" this refresh.</param>
    public DateTime? Stamp(TenantId tenant, string sessionId, bool isRed)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);

        var key = (tenant, sessionId);
        if (!isRed)
        {
            if (_since.TryRemove(key, out _))
                FileLog.Write($"[NeedsYouClock] tenant={tenant.ToLogString()} sid={sessionId}: left red, cleared NeedsYouSince");
            return null;
        }

        // First red refresh stamps UtcNow; every subsequent red refresh holds that value.
        var added = false;
        var since = _since.GetOrAdd(key, _ =>
        {
            added = true;
            return DateTime.UtcNow;
        });
        if (added)
            FileLog.Write($"[NeedsYouClock] tenant={tenant.ToLogString()} sid={sessionId}: entered red, NeedsYouSince={since:o}");
        return since;
    }
}
