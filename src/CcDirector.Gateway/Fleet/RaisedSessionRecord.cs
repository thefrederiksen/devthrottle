using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Governance;
using CcDirector.Gateway.Util;

namespace CcDirector.Gateway.Fleet;

/// <summary>
/// THE RECORD OF RAISED SESSIONS (the Fleet Manager Improvement mission, phase 1): every raise, every lower, and every
/// action a raised session took that no other session key may. Written into the Gateway's existing governance audit
/// log (<see cref="GovernanceAuditLog"/>, the <c>governance_audit_events</c> table) - the trail that already holds who
/// stopped a session and who handed one over - so it is answerable by query, through
/// <c>GET /gateway/governance/audit-events?category=permission&amp;eventType=raised-action</c>, and not only present
/// in a log file.
///
/// NOTHING HERE CATCHES. A row that cannot be written is an exception the caller sees: the middleware writes the row
/// before the route runs, so an action that cannot be recorded does not happen.
/// </summary>
internal sealed class RaisedSessionRecord
{
    private readonly GovernanceAuditLog _log;
    private readonly Func<TenantId, IDisposable> _enterTenantScope;

    /// <param name="enterTenantScope">Enters the account the row belongs to: the audit log reads it ambiently, and the
    /// middleware writes before any request scope exists.</param>
    public RaisedSessionRecord(GovernanceAuditLog log, Func<TenantId, IDisposable> enterTenantScope)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _enterTenantScope = enterTenantScope ?? throw new ArgumentNullException(nameof(enterTenantScope));
    }

    /// <summary>A session was raised. <paramref name="actor"/> is who raised it.</summary>
    public void Raised(TenantId tenant, string sessionId, string actor, string detail)
        => Append(tenant, sessionId, GovernanceAuditEventType.SessionRaised, actor, detail);

    /// <summary>A session was lowered. <paramref name="actor"/> is who lowered it.</summary>
    public void Lowered(TenantId tenant, string sessionId, string actor, string detail)
        => Append(tenant, sessionId, GovernanceAuditEventType.SessionLowered, actor, detail);

    /// <summary>A raised session took an action no other session key may. The row is the acting session's, and names
    /// it as the actor.</summary>
    public void Action(TenantId tenant, string actingSessionId, string detail)
        => Append(tenant, actingSessionId, GovernanceAuditEventType.RaisedAction, $"session {actingSessionId}", detail);

    /// <summary>What the guard let a raised session do, in the words the row carries. The path names the session acted
    /// on; nothing from the body - no prompt text - is ever recorded.</summary>
    public static string DescribeGuardGrant(RaisedGrant grant, string method, string path) => grant switch
    {
        RaisedGrant.AgentInput => $"typed into a session as the owner would: {method} {path}",
        RaisedGrant.FleetManagerOwnerRoute => $"called a Fleet Manager route that is otherwise the owner's alone: {method} {path}",
        _ => throw new ArgumentOutOfRangeException(nameof(grant), grant, "only a raised grant is recorded"),
    };

    private void Append(TenantId tenant, string sessionId, string eventType, string actor, string detail)
    {
        FileLog.Write($"[RaisedSessionRecord] {eventType}: tenant={tenant.ToLogString()}, session={sessionId}, actor={actor}");
        using var scope = _enterTenantScope(tenant);
        _log.Append(new AppendGovernanceAuditEventRequest
        {
            SessionId = sessionId,
            Category = GovernanceAuditCategory.Permission,
            EventType = eventType,
            Actor = actor,
            Detail = detail.Length > GovernanceAuditLog.MaxDetailChars ? detail[..GovernanceAuditLog.MaxDetailChars] : detail,
        });
    }
}
