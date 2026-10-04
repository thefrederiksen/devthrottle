using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Discovery;
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
/// <c>account_subject</c>. The row must be ACTIVE (not revoked) and BOUND TO THIS TEAM's tenant. A revoked
/// credential, or one bound to another tenant, has no person here - so a key taken away from someone, or a key that
/// belongs to some other account, never makes a Director "theirs" on this team.</item>
/// </list>
///
/// It answers WHO, not WHETHER THEY MAY: a person whose role no longer lets them run sessions (a Collaborator) is
/// still the person named here. Each caller asks the role table for that itself, as the Fleet Map does.
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
    /// team's tenant), or null when it cannot be said: the Director is not registered there, said Hello on no device
    /// key, or its device credential is missing, REVOKED, BOUND TO ANOTHER TENANT, or names no person.
    /// </summary>
    public string? PersonOfDirector(TenantId tenant, string directorId)
    {
        if (string.IsNullOrWhiteSpace(directorId))
            throw new ArgumentException("A Director id is required.", nameof(directorId));

        var credential = _directors.RegisteringCredentialOf(tenant, directorId);
        if (credential is null || !credential.StartsWith(CredentialPrefix, StringComparison.Ordinal))
        {
            FileLog.Write($"[TeamDirectorOwnership] PersonOfDirector: team {tenant.ToLogString()} - no device key, no person");
            return null;
        }
        var deviceId = credential[CredentialPrefix.Length..];
        var teamId = tenant.Value;

        using var ctx = _db.CreateUnscopedContext();
        var subject = ctx.DeviceCredentials.AsNoTracking()
            .Where(c => c.DeviceId == deviceId && c.TenantId == teamId && c.RevokedAtUtc == null && c.AccountSubject != null)
            .Select(c => c.AccountSubject)
            .FirstOrDefault();

        var found = !string.IsNullOrWhiteSpace(subject);
        FileLog.Write($"[TeamDirectorOwnership] PersonOfDirector: team {tenant.ToLogString()} person={(found ? "found" : "none")}");
        return found ? subject!.Trim() : null;
    }
}
