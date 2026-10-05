using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Pairing;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Teams;

/// <summary>
/// WHOSE DIRECTOR IS THIS, ON A TEAM - THE ONE ANSWER (devthrottle_internal#2312, shared with #2311). Every team
/// feature that needs to know which person a Director on a team belongs to asks <see cref="PersonOfDirector"/>; none
/// keeps its own copy of the lookup. The team Fleet Map reads it today; the team-key resolver of #2311 is to call the
/// same method rather than repeat it (seam-director-key.md: "#2311 rebases onto it and keeps one copy").
///
/// The answer is read in two steps, and only these two:
/// <list type="number">
/// <item>The credential the Director said Hello on in the team's tenant, from
/// <see cref="DirectorRegistry.RegisteringCredentialOf"/>. Only a per-device key (<c>device:&lt;device id&gt;</c>)
/// names a person; a Director registered with no credential, or on any other kind, has no person here.</item>
/// <item>That device's <c>device_credentials</c> row, which names the person who enrolled it in
/// <c>account_subject</c>. The row must be ACTIVE by the Gateway's one rule for a credential row,
/// <see cref="DeviceRegistry.IsActiveCredential"/> (status active and no revocation time - the same rule
/// <see cref="DeviceRegistry.ResolveCredential"/> applies to a presented key), and BOUND TO THIS TEAM's tenant. A
/// revoked credential, or one bound to another tenant, has no person here - so a key taken away from someone, or a
/// key that belongs to some other account, never makes a Director "theirs" on this team.</item>
/// </list>
///
/// It answers WHO, not WHETHER THEY MAY. A person whose role no longer lets them run sessions (a Collaborator) is still
/// the person named here, and so is a person whose key is refused by some LIVE rule that writes nothing to the row.
/// Every caller therefore asks the role table itself, as the Fleet Map does, and a gate resolves the caller's own key
/// through <see cref="DeviceRegistry.ResolveCredential"/> as it does today. A blank Director id answers null like every
/// other case where the person cannot be said, so a caller refuses it rather than faulting.
///
/// The account subject is personally identifying and is never logged; a team id is logged only hashed.
/// </summary>
public sealed class TeamDirectorOwnership
{
    /// <summary>How the Director registry records a Director that said Hello on a per-device key:
    /// <c>device:&lt;device id&gt;</c> (<see cref="Util.AuthMiddleware.RegisteringCredential"/>).</summary>
    public const string CredentialPrefix = "device:";

    private readonly DirectorRegistry _directors;
    private readonly GatewayDatabase _db;

    public TeamDirectorOwnership(DirectorRegistry directors, GatewayDatabase db)
    {
        _directors = directors ?? throw new ArgumentNullException(nameof(directors));
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// The account subject of the person <paramref name="directorId"/> belongs to in <paramref name="tenant"/> (a
    /// team's tenant), or null when it cannot be said: no Director id, the Director is not registered there, said Hello
    /// on no device key, or its device credential is missing, REVOKED, BOUND TO ANOTHER TENANT, or names no person.
    /// </summary>
    public string? PersonOfDirector(TenantId tenant, string directorId)
    {
        if (string.IsNullOrWhiteSpace(directorId))
        {
            FileLog.Write($"[TeamDirectorOwnership] PersonOfDirector: team {tenant.ToLogString()} - no Director id, no person");
            return null;
        }

        var credential = _directors.RegisteringCredentialOf(tenant, directorId);
        if (credential is null || !credential.StartsWith(CredentialPrefix, StringComparison.Ordinal))
        {
            FileLog.Write($"[TeamDirectorOwnership] PersonOfDirector: team {tenant.ToLogString()} - no device key, no person");
            return null;
        }
        var deviceId = credential[CredentialPrefix.Length..];
        var teamId = tenant.Value;

        using var ctx = _db.CreateUnscopedContext();
        // The device id is the table's key: at most one row. The row rule is asked in memory, of the whole row, so it is
        // the same method the key check uses and not a second copy written as a query.
        var row = ctx.DeviceCredentials.AsNoTracking()
            .FirstOrDefault(c => c.DeviceId == deviceId && c.TenantId == teamId);
        var subject = row is not null && DeviceRegistry.IsActiveCredential(row) ? row.AccountSubject : null;

        var found = !string.IsNullOrWhiteSpace(subject);
        FileLog.Write($"[TeamDirectorOwnership] PersonOfDirector: team {tenant.ToLogString()} person={(found ? "found" : "none")}");
        return found ? subject!.Trim() : null;
    }
}
