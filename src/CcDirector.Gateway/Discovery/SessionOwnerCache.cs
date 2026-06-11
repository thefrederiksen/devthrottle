using System.Collections.Concurrent;

namespace CcDirector.Gateway.Discovery;

/// <summary>
/// Remembers which Director last owned each session id, so the per-session WebSocket proxy can tell
/// "unknown session" (404) apart from "known session whose owning Director has gone offline /
/// unreachable" (503).
///
/// Issue #288: live ownership resolution (the <c>GetSessionAsync</c> fan-out in
/// <see cref="Api.SessionWsProxyEndpoints"/>) only confirms ownership by reaching the owning
/// Director, so an OFFLINE Director makes resolution fail and every per-session WS request collapse
/// to 404 - contradicting issue #268's AC4 ("offline owning Director -> 503"). This cache breaks the
/// tie: the fleet aggregator (<c>GET /sessions</c>) records every session it observes, and the WS
/// proxy records every session it successfully forwards, so any session the Cockpit has seen
/// resolves to a 503 - not a 404 - once its Director goes dark.
///
/// In-memory only; it rebuilds from the next aggregation after a Gateway restart. Session ids are
/// GUIDs and never reused, so a stale entry can only point at a Director that is itself gone, which
/// is exactly the 503 case. Bounded in practice by the number of sessions the fleet has run.
/// </summary>
public sealed class SessionOwnerCache
{
    private readonly ConcurrentDictionary<string, string> _ownerBySession = new();

    /// <summary>Record (or refresh) the Director that owns <paramref name="sessionId"/>. No-op on empty input.</summary>
    public void Remember(string sessionId, string directorId)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(directorId)) return;
        _ownerBySession[sessionId] = directorId;
    }

    /// <summary>The Director id last seen owning <paramref name="sessionId"/>, or null if never observed.</summary>
    public string? OwnerOf(string sessionId)
        => !string.IsNullOrEmpty(sessionId) && _ownerBySession.TryGetValue(sessionId, out var id) ? id : null;

    /// <summary>
    /// Drop the cached owner for <paramref name="sessionId"/> (e.g. the session has exited).
    /// No-op when the id is empty or not cached.
    /// </summary>
    public void Forget(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        _ownerBySession.TryRemove(sessionId, out _);
    }

    /// <summary>
    /// Reconcile the cache against a REACHABLE Director's live session set (issue #291): drop every
    /// cached entry that points at <paramref name="directorId"/> but whose session id is no longer in
    /// <paramref name="liveSessionIds"/>. A session is considered gone when the Director (which we just
    /// reached) no longer reports it as live - it exited or disappeared - so the per-session WS proxy
    /// reverts to 404 instead of the 503 "owner offline" verdict from #288.
    ///
    /// Caller contract: only invoke this for a Director that just answered (reachable). Entries owned
    /// by a DIFFERENT Director are never touched, so an offline owner's sessions stay cached -> still
    /// 503 (#288 must not regress).
    /// </summary>
    public void RetainForDirector(string directorId, IReadOnlyCollection<string> liveSessionIds)
    {
        if (string.IsNullOrEmpty(directorId)) return;

        var live = liveSessionIds as HashSet<string> ?? new HashSet<string>(liveSessionIds, StringComparer.Ordinal);
        foreach (var kvp in _ownerBySession)
        {
            if (!string.Equals(kvp.Value, directorId, StringComparison.Ordinal)) continue;
            if (live.Contains(kvp.Key)) continue;
            _ownerBySession.TryRemove(new KeyValuePair<string, string>(kvp.Key, kvp.Value));
        }
    }
}
