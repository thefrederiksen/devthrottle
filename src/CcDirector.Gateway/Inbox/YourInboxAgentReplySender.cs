using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Messaging;

namespace CcDirector.Gateway.Inbox;

/// <summary>
/// AN AGENT'S REPLY FROM YOUR INBOX TO A SESSION, SENT EXACTLY AS <c>message send</c> SENDS (review of pull request 4767).
///
/// The reply is an ordinary fleet message FROM the agent, kind <c>message</c>, judged by the normal policy: the
/// relationship rule (the session that started it, a session it started, or a message link) and both rates. A RAISED
/// agent gets the same waiver it gets for <c>message send</c>, and nothing more. What <c>message send</c> records, this
/// records too, in the governance audit log: a message let through only because the sender is raised, and a message a
/// link carried (which may use a one-time link up). Never a Gateway notice: a notice waives the policy and names no
/// sender, which would let any session key write into any running session unattributed.
/// </summary>
internal sealed class YourInboxAgentReplySender
{
    private readonly FleetMessageService _fleet;
    private readonly Func<TenantId, string, FleetParty> _partyOf;
    private readonly Func<TenantId, string, bool> _isRaised;
    private readonly RaisedSessionRecord _raisedRecord;
    private readonly FleetMessageLinkRecord _linkRecord;

    /// <param name="partyOf">One end of the message as the roster describes it - its supervisor, name and machine - read
    /// exactly as <c>message send</c> reads it.</param>
    /// <param name="isRaised">Whether the owner raised this session (<c>RaisedSessionStore.IsRaised</c>).</param>
    public YourInboxAgentReplySender(FleetMessageService fleet, Func<TenantId, string, FleetParty> partyOf,
        Func<TenantId, string, bool> isRaised, RaisedSessionRecord raisedRecord, FleetMessageLinkRecord linkRecord)
    {
        _fleet = fleet ?? throw new ArgumentNullException(nameof(fleet));
        _partyOf = partyOf ?? throw new ArgumentNullException(nameof(partyOf));
        _isRaised = isRaised ?? throw new ArgumentNullException(nameof(isRaised));
        _raisedRecord = raisedRecord ?? throw new ArgumentNullException(nameof(raisedRecord));
        _linkRecord = linkRecord ?? throw new ArgumentNullException(nameof(linkRecord));
    }

    /// <summary>Send the reply from <paramref name="fromSessionId"/> to <paramref name="toSessionId"/>, and record what
    /// <c>message send</c> records. The outcome is the policy's, refusals included; the caller answers with it.</summary>
    public FleetSendOutcome Send(TenantId tenant, string fromSessionId, string toSessionId, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toSessionId);
        var exemption = _isRaised(tenant, fromSessionId) ? FleetMessageExemption.Raised : FleetMessageExemption.None;
        var outcome = _fleet.Send(tenant, _partyOf(tenant, fromSessionId), _partyOf(tenant, toSessionId), text,
            FleetMessageKinds.Message, exemption);
        if (outcome.WaivedForRaisedSender)
            _raisedRecord.Action(tenant, fromSessionId,
                $"sent message {outcome.Response.MessageId} to session {outcome.Response.RecipientSessionId} past the "
                + "relationship rule or a rate limit (a reply from Your inbox)");
        if (outcome.Link is { } link && outcome.Response.MessageId is { } messageId)
            _linkRecord.MessageOver(tenant, fromSessionId, messageId, outcome.Response.RecipientSessionId, link);
        FileLog.Write($"[YourInboxAgentReplySender] Send: from={fromSessionId} to={toSessionId} exemption={exemption} outcome={outcome.Outcome} link={outcome.Link?.LinkId ?? "(none)"}");
        return outcome;
    }
}
