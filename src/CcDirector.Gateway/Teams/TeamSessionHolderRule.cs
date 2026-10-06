using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Streaming;

namespace CcDirector.Gateway.Teams;

/// <summary>
/// ONE ANSWER FOR WHICH DIRECTOR HOLDS A SESSION IN A TEAM (devthrottle_internal#2311). In a team's tenant any Director
/// may list any session id in its roster, so the roster's first row is no answer: a colleague's Director that lists
/// your session id would be sent your typed prompts, your held deliveries, your dev reports and your Fleet Manager's
/// close. This rule is what <see cref="PushedSessionStore"/> answers every per-session lookup through in a team: the
/// holder is the Director the one ownership rule, <see cref="TeamCallerOwnership.ClaimOf(TenantId, string, string)"/>,
/// says the session is its own (stored writers, then the session key row, then the sole roster holder). When it names
/// none of the Directors listing the id, nobody holds it - withheld and logged, never the first row.
///
/// SEVERAL CLAIMANTS ARE ONE PERSON'S, so choosing among them reaches nobody else. ClaimOf answers "its own" for more
/// than one Director only on its stored-writers branch (a key row names exactly one Director; the roster branch needs a
/// sole holder), and there every writer must be that Director or another Director of its person. Two claimants of two
/// different people would need a writer that is both people's, which cannot be. Among one person's claimants the key
/// row's Director is preferred, then the lowest id, so the answer never depends on the roster's hash order.
///
/// Governs a tenant only where Teams is released and the tenant is a team's. Personal tenants and a dark Gateway are
/// not governed, and the store keeps today's answer there.
/// </summary>
public sealed class TeamSessionHolderRule : ISessionHolderRule
{
    private readonly Func<TenantId, bool> _isReleasedTeam;
    private readonly TeamCallerOwnership _ownership;

    /// <param name="isReleasedTeam">Whether a tenant is a team's tenant on a Gateway where Teams is released.</param>
    /// <param name="ownership">The one ownership rule.</param>
    public TeamSessionHolderRule(Func<TenantId, bool> isReleasedTeam, TeamCallerOwnership ownership)
    {
        _isReleasedTeam = isReleasedTeam ?? throw new ArgumentNullException(nameof(isReleasedTeam));
        _ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
    }

    /// <inheritdoc />
    public bool Governs(TenantId tenant) => tenant.IsValid && _isReleasedTeam(tenant);

    /// <inheritdoc />
    public string? HolderOf(TenantId tenant, string sessionId, IReadOnlyList<string> holders)
    {
        ArgumentNullException.ThrowIfNull(holders);
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("sessionId is required", nameof(sessionId));

        var claimants = new List<string>();
        foreach (var director in holders)
        {
            var claim = _ownership.ClaimOf(tenant, director, sessionId);
            if (claim == TeamSessionClaim.Its)
                claimants.Add(director);
            else
                FileLog.Write($"[TeamSessionHolderRule] HolderOf: session={sessionId} tenant={tenant.ToLogString()} director={director} lists it but its claim is {claim} - not the holder");
        }

        if (claimants.Count == 0)
        {
            FileLog.Write($"[TeamSessionHolderRule] HolderOf: session={sessionId} tenant={tenant.ToLogString()} - none of {holders.Count} listing Director(s) holds it; nobody");
            return null;
        }
        if (claimants.Count == 1)
            return claimants[0];

        var keyed = _ownership.SessionKeyDirectorOf(tenant, sessionId);
        var chosen = claimants.FirstOrDefault(c => keyed is not null && DeviceCredentialIdentity.SameDirectorId(c, keyed))
                     ?? claimants.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).First();
        FileLog.Write($"[TeamSessionHolderRule] HolderOf: session={sessionId} tenant={tenant.ToLogString()} - {claimants.Count} Directors of one person claim it; director={chosen}");
        return chosen;
    }
}
