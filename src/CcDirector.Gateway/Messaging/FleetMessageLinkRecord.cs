using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Governance;

namespace CcDirector.Gateway.Messaging;

/// <summary>
/// THE RECORD OF MESSAGE LINKS (issue #3548): every link set up, every link stopped, and every message a link carried
/// that the relationship rule alone would have refused. Written into the Gateway's governance audit log (the
/// <c>governance_audit_events</c> table) beside raise and lower, so it is answerable by query through
/// <c>GET /gateway/governance/audit-events?category=permission</c>, and not only present in a log file.
///
/// NOTHING HERE CATCHES. A row that cannot be written is an exception the caller sees; the link routes write the row
/// before they answer, so a link is never reported as set up without its record.
/// </summary>
public sealed class FleetMessageLinkRecord
{
    private readonly GovernanceAuditLog _log;
    private readonly Func<TenantId, IDisposable> _enterTenantScope;

    /// <param name="enterTenantScope">Enters the account the row belongs to; the audit log reads it ambiently.</param>
    public FleetMessageLinkRecord(GovernanceAuditLog log, Func<TenantId, IDisposable> enterTenantScope)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _enterTenantScope = enterTenantScope ?? throw new ArgumentNullException(nameof(enterTenantScope));
    }

    /// <summary>A link was set up. <paramref name="actor"/> is who set it up.</summary>
    public void SetUp(TenantId tenant, FleetMessageLink link, string actor)
        => Append(tenant, link.SenderSessionId, GovernanceAuditEventType.MessageLinkSetUp, actor,
            $"set up message link {link.LinkId} from session {link.SenderSessionId} to session {link.RecipientSessionId}, "
            + $"amount {link.Amount}");

    /// <summary>A link stopped. <paramref name="actor"/> is who stopped it; <paramref name="why"/> says how.</summary>
    public void Stopped(TenantId tenant, FleetMessageLink link, string actor, string why)
        => Append(tenant, link.SenderSessionId, GovernanceAuditEventType.MessageLinkStopped, actor,
            $"message link {link.LinkId} from session {link.SenderSessionId} to session {link.RecipientSessionId} "
            + $"stopped: {why}");

    /// <summary>A link carried a message the relationship rule would have refused.</summary>
    public void MessageOver(TenantId tenant, string senderSessionId, string messageId, string recipientSessionId,
        FleetMessageLinkFacts link)
        => Append(tenant, senderSessionId, GovernanceAuditEventType.MessageOverLink, $"session {senderSessionId}",
            $"sent message {messageId} to session {recipientSessionId} over message link {link.LinkId} ({link.Amount})"
            + (link.IsOneTime ? ", which used the link up" : ""));

    /// <summary>A session asked for a link. The session is the actor: it asked, nobody allowed anything yet.</summary>
    public void Requested(TenantId tenant, FleetMessageLinkRequest request)
        => Append(tenant, request.RequesterSessionId, GovernanceAuditEventType.MessageLinkRequested,
            $"session {request.RequesterSessionId}",
            $"asked for message link request {request.RequestId} to session {request.TargetSessionId}: {request.Reason}");
    // The fixed part above is 113 characters, so a reason of FleetMessageLinkRequestStore.MaxReasonLength (380) fits the
    // record's 500 whole.

    /// <summary>A request was answered or ended. <paramref name="answer"/> says how, in words.</summary>
    public void RequestAnswered(TenantId tenant, FleetMessageLinkRequest request, string actor, string answer)
        => Append(tenant, request.RequesterSessionId, GovernanceAuditEventType.MessageLinkRequestAnswered, actor,
            $"message link request {request.RequestId} from session {request.RequesterSessionId} to session "
            + $"{request.TargetSessionId}: {answer}");

    private void Append(TenantId tenant, string sessionId, string eventType, string actor, string detail)
    {
        FileLog.Write($"[FleetMessageLinkRecord] {eventType}: tenant={tenant.ToLogString()}, session={sessionId}, actor={actor}");
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
