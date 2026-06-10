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
}
