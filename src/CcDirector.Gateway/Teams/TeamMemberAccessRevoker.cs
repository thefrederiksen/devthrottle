using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Streaming;

namespace CcDirector.Gateway.Teams;

/// <summary>
/// REMOVING A PERSON FROM A TEAM STOPS THEIR DIRECTORS ON THAT TEAM (devthrottle_internal#2311). Attached to
/// <see cref="TeamRegistry.MembershipCommitted"/>, the one place a membership change is committed. When a person is
/// removed from a team, or their role changes to one the role table does not let run sessions (a Collaborator), it:
///
/// <list type="number">
/// <item>tombstones every key that person holds in that team's tenant - durable, on every Gateway replica, so the next
/// request and the next Hello on any of them are refused (the device registry's own membership check already refuses
/// them on the very next request; the tombstone makes it stay that way if the person is invited back, exactly as a
/// restart's quarantine would), and</item>
/// <item>cuts that person's OPEN tunnels on that team, on this Gateway - only theirs: never the team's other members',
/// never the person's tunnels on their other teams or on their personal tenant.</item>
/// </list>
///
/// Ordered tombstone first, so a reconnect racing the cut authenticates against the revoked key and loses - the same
/// order <see cref="Tenancy.TenantAccessRevoker"/> uses for a whole tenant. The subject is never logged.
/// </summary>
public sealed class TeamMemberAccessRevoker
{
    /// <summary>The revocation reason on a key whose person was removed from the team.</summary>
    public const string RemovedReason = "team_member_removed";

    /// <summary>The revocation reason on a key whose person's new role may not run sessions in the team.</summary>
    public const string RoleCannotRunSessionsReason = "team_role_cannot_run_sessions";

    private readonly DeviceRegistry _devices;
    private readonly DirectorConnectionRegistry _connections;

    public TeamMemberAccessRevoker(DeviceRegistry devices, DirectorConnectionRegistry connections)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    /// <summary>
    /// Act on one committed membership change. Returns the reason the person's access on the team was cut, or null
    /// when the change leaves them able to run sessions there (a team created, a member added, or a role change to a
    /// role that may still run sessions).
    /// </summary>
    public string? OnMembershipCommitted(TeamMembershipCommitted change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var reason = ReasonToCut(change);
        var tenant = new TenantId(change.TeamId);
        if (reason is null)
        {
            FileLog.Write($"[TeamMemberAccessRevoker] OnMembershipCommitted: team {tenant.ToLogString()} change={change.Change} - the person may still run sessions there, nothing cut");
            return null;
        }

        var revoked = _devices.RevokeTenantMember(tenant, change.AccountSubject, reason);
        var aborted = _connections.AbortForTenantMember(tenant, change.AccountSubject, reason);
        FileLog.Write($"[TeamMemberAccessRevoker] OnMembershipCommitted: team {tenant.ToLogString()} change={change.Change} - " +
                      $"revoked {revoked} key(s) and cut {aborted} open tunnel(s) of that one person (reason={reason})");
        return reason;
    }

    /// <summary>Why a change cuts the person's access on the team, or null when it does not. Pure.</summary>
    public static string? ReasonToCut(TeamMembershipCommitted change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return change.Change switch
        {
            TeamMembershipChange.MemberRemoved => RemovedReason,
            TeamMembershipChange.RoleChanged when change.Role is { } role
                && !TeamPermissions.Allows(role, TeamAction.RunSessionsOnOwnComputers) => RoleCannotRunSessionsReason,
            _ => null,
        };
    }
}
