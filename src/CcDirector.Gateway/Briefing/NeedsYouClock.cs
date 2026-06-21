using System.Collections.Concurrent;
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
/// </summary>
public sealed class NeedsYouClock
{
    private readonly ConcurrentDictionary<string, DateTime> _since = new();

    /// <summary>
    /// Apply the entry/hold/clear rule for one session and return the timestamp to stamp on
    /// its <see cref="Contracts.SessionDto.NeedsYouSince"/> (UTC), or null when it is not red.
    /// </summary>
    /// <param name="sessionId">The session's stable id.</param>
    /// <param name="isRed">Whether the session's EffectiveColor is "red" this refresh.</param>
    public DateTime? Stamp(string sessionId, bool isRed)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);

        if (!isRed)
        {
            if (_since.TryRemove(sessionId, out _))
                FileLog.Write($"[NeedsYouClock] sid={sessionId}: left red, cleared NeedsYouSince");
            return null;
        }

        // First red refresh stamps UtcNow; every subsequent red refresh holds that value.
        var added = false;
        var since = _since.GetOrAdd(sessionId, _ =>
        {
            added = true;
            return DateTime.UtcNow;
        });
        if (added)
            FileLog.Write($"[NeedsYouClock] sid={sessionId}: entered red, NeedsYouSince={since:o}");
        return since;
    }
}
