using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Governance;
using CcDirector.Gateway.Inbox;
using CcDirector.Gateway.Messaging;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Inbox;

/// <summary>
/// AN AGENT'S REPLY IS SENT, AND RECORDED, EXACTLY AS <c>message send</c> (re-review of pull request 4767): the relationship
/// rule and both rates apply; a raised agent gets the same waiver and it is recorded; a message a link carried is recorded
/// in the governance audit log, as <c>message send</c> records it. Over the real fleet message service, link store and
/// audit log on a throwaway database.
/// </summary>
public sealed class YourInboxAgentReplySenderTests : IDisposable
{
    private const string Lead = "11111111-1111-4111-8111-111111111111";
    private const string Developer = "22222222-2222-4222-8222-222222222222";
    private const string Stranger = "33333333-3333-4333-8333-333333333333";
    private static readonly TenantId A = new("acct-a");
    private static readonly DateTime T0 = new(2026, 11, 7, 14, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _h = new();
    private readonly GatewayDatabase _db;
    private readonly GovernanceAuditLog _audit;
    private readonly FleetMessageLinkStore _links;
    private readonly FleetMessageService _fleet;
    private readonly HashSet<string> _raised = new(StringComparer.Ordinal);

    public YourInboxAgentReplySenderTests()
    {
        _db = _h.Open();
        _audit = new GovernanceAuditLog(_h.Open(new FixedTenantContext(A)));
        _links = new FleetMessageLinkStore(_db);
        _fleet = new FleetMessageService(new FleetMessageStore(_db), clock: () => T0);
    }

    public void Dispose() => _h.Dispose();

    private YourInboxAgentReplySender Sender() => new(_fleet,
        (_, sid) => new FleetParty(sid, sid == Developer ? Lead : null, null, null),
        (_, sid) => _raised.Contains(sid),
        new RaisedSessionRecord(_audit, _ => new NoScope()),
        new FleetMessageLinkRecord(_audit, _ => new NoScope()));

    private IReadOnlyList<GovernanceAuditEventDto> Audit(string eventType) => _audit.List(eventType: eventType);

    [Fact]
    public void Send_ToASessionItStarted_IsQueuedFromTheAgent_AndRecordsNothing()
    {
        var outcome = Sender().Send(A, Lead, Developer, "Tag it.");

        Assert.Equal(FleetMessageOutcome.Queued, outcome.Outcome);
        Assert.Equal(Lead, Assert.Single(_fleet.ReadInbox(A, Developer, includeRecent: false).Unread).FromSessionId);
        Assert.Empty(Audit(GovernanceAuditEventType.MessageOverLink));
        Assert.Empty(Audit(GovernanceAuditEventType.RaisedAction));
    }

    [Fact]
    public void Send_FromAnUnrelatedSession_IsRefused_AndRecordsNothing()
    {
        var outcome = Sender().Send(A, Stranger, Developer, "Tag it.");

        Assert.Equal(FleetMessageOutcome.RefusedNotRelated, outcome.Outcome);
        Assert.Empty(_fleet.ReadInbox(A, Developer, includeRecent: false).Unread);
        Assert.Empty(Audit(GovernanceAuditEventType.MessageOverLink));
    }

    [Fact]
    public void Send_OverAOneTimeMessageLink_IsQueued_AndRecordedInTheAuditLog_AsMessageSendRecordsIt()
    {
        _links.SetUp(A, Stranger, Developer, FleetMessageLinkAmounts.Once, "owner-phone", T0);

        var outcome = Sender().Send(A, Stranger, Developer, "Tag it.");

        Assert.Equal(FleetMessageOutcome.Queued, outcome.Outcome);
        Assert.NotNull(outcome.Link);
        var row = Assert.Single(Audit(GovernanceAuditEventType.MessageOverLink));
        Assert.Equal(Stranger, row.SessionId);
        Assert.Contains($"sent message {outcome.Response.MessageId} to session {Developer} over message link", row.Detail);
        Assert.Contains("which used the link up", row.Detail);

        // The link is used up: a second reply is refused, and nothing more is recorded.
        Assert.Equal(FleetMessageOutcome.RefusedNotRelated, Sender().Send(A, Stranger, Developer, "And again.").Outcome);
        Assert.Single(Audit(GovernanceAuditEventType.MessageOverLink));
    }

    [Fact]
    public void Send_FromARaisedSession_GetsTheSameWaiverAsMessageSend_AndItIsRecorded()
    {
        _raised.Add(Stranger);

        var outcome = Sender().Send(A, Stranger, Developer, "Tag it.");

        Assert.Equal(FleetMessageOutcome.Queued, outcome.Outcome);
        Assert.True(outcome.WaivedForRaisedSender);
        var row = Assert.Single(Audit(GovernanceAuditEventType.RaisedAction));
        Assert.Equal(Stranger, row.SessionId);
        Assert.Contains("past the relationship rule or a rate limit (a reply from Your inbox)", row.Detail);
    }

    private sealed class NoScope : IDisposable
    {
        public void Dispose() { }
    }
}
