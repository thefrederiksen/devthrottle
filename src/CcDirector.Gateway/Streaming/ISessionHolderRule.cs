using CcDirector.Core.Tenancy;

namespace CcDirector.Gateway.Streaming;

/// <summary>
/// WHICH DIRECTOR HOLDS A SESSION, for a tenant where the roster's first row is not the answer
/// (devthrottle_internal#2311). The roster accepts any session id from any Director, so in a team's tenant a colleague's
/// Director can list your session id; a lookup that took the first row found could send what is meant for your session
/// to their Director. <see cref="PushedSessionStore"/> asks this rule inside every per-session lookup it answers
/// (<see cref="PushedSessionStore.TryLocate"/>, <see cref="PushedSessionStore.TryLocateIgnoringFreshness"/>,
/// <see cref="PushedSessionStore.TryGetLastKnownSession"/>, <see cref="PushedSessionStore.HoldsSession"/>), so no caller
/// can reach the first row around it. A tenant the rule does not govern keeps the first row, exactly as before.
/// </summary>
public interface ISessionHolderRule
{
    /// <summary>Whether this rule decides who holds a session in <paramref name="tenant"/>. False keeps today's
    /// answer - the first row - unchanged.</summary>
    bool Governs(TenantId tenant);

    /// <summary>
    /// The one Director, out of <paramref name="holders"/> (every Director whose roster lists
    /// <paramref name="sessionId"/>, freshness ignored), that holds the session - or null when none does, which the
    /// store answers as "nobody holds it". Asked only for a tenant <see cref="Governs"/> said yes to, and never under a
    /// store lock.
    /// </summary>
    string? HolderOf(TenantId tenant, string sessionId, IReadOnlyList<string> holders);
}
