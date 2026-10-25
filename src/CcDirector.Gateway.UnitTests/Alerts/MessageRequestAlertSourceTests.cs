using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Alerts;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Governance;
using CcDirector.Gateway.Messaging;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Alerts;

/// <summary>
/// THE REQUESTS FOR A MESSAGE LINK AS ALERTS (the Alerts mission), over the real <see cref="FleetMessageLinkRequestStore"/>
/// on a throwaway SQLite file. Every status folds to the right needs-answer flag, status word and sentence, written with
/// the sessions' numbers and names and never an id; and a waiting request one of whose sessions has ended is ended by the
/// read, exactly as the owner's own list ends it, so it never sits on the bell.
/// </summary>
public sealed class MessageRequestAlertSourceTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();
    private readonly FleetMessageLinkRequestStore _requests;
    private readonly MessageRequestAlertSource _source;
    private readonly List<string> _told = new();
    private readonly Dictionary<string, SessionDto> _sessions = new(StringComparer.Ordinal);

    private static readonly TenantId Tenant = TenantId.Local;
    private static readonly TenantId Other = new("acct-b");
    private const string Requester = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string Target = "bbbbbbbb-0000-0000-0000-000000000002";
    private static readonly DateTime T0 = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

    public MessageRequestAlertSourceTests()
    {
        _requests = new FleetMessageLinkRequestStore(_harness.Open());
        var record = new FleetMessageLinkRecord(new GovernanceAuditLog(_harness.Open(new FixedTenantContext(Tenant))), _ => new NoScope());
        _sessions[Requester] = new SessionDto { SessionId = Requester, Number = 121, Name = "Release - Manager" };
        _sessions[Target] = new SessionDto { SessionId = Target, Number = 125, Name = "Release - Release Manager - v2.31.0 checks" };
        _source = new MessageRequestAlertSource(_requests, record,
            (_, id) => _sessions.TryGetValue(id, out var s) ? s : null,
            (_, session, text) => _told.Add(session.SessionId + ": " + text));
    }

    public void Dispose() => _harness.Dispose();

    private string Ask(TenantId? tenant = null)
        => _requests.Ask(tenant ?? Tenant, Requester, Target, "hand over the merge", T0).Request!.RequestId;

    private AlertDto Only(DateTime now) => Assert.Single(_source.List(Tenant, now));

    [Fact]
    public void Pending_NeedsAnAnswer_NamingBothSessionsByNumberAndName()
    {
        var id = Ask();

        var alert = Only(T0.AddMinutes(1));

        Assert.True(alert.NeedsAnswer);
        Assert.Equal("message-request:" + id, alert.Id);
        Assert.Equal(AlertKinds.MessageRequest, alert.Kind);
        Assert.Equal("Waiting", alert.Status);
        // The owner's own example: each session by its mission and role, then its number - and the reason once, in the
        // summary, never repeating the title.
        Assert.Equal("Release Manager (121) wants to message Release Manager (125)", alert.Title);
        Assert.Equal("Its reason: hand over the merge", alert.Summary);
        Assert.Null(alert.ExpiresAtUtc);
        Assert.DoesNotContain(Requester, alert.Title + alert.Summary);
        var detail = Assert.IsType<FleetMessageLinkRequestDto>(alert.Detail);
        Assert.Equal("121 Release - Manager", detail.RequesterLabel);
    }

    [Theory]
    [InlineData(FleetMessageLinkAmounts.Once, "You allowed it: one message, with no reply.")]
    [InlineData(FleetMessageLinkAmounts.OnceWithReply, "You allowed it: one message and its reply.")]
    [InlineData(FleetMessageLinkAmounts.Ongoing, "You allowed it: as many messages as they need, both ways.")]
    public void Allowed_SaysHowMuchWasAllowed(string amount, string summary)
    {
        var id = Ask();
        Assert.True(_requests.TryAnswer(Tenant, id, FleetMessageLinkRequestStatuses.Allowed, "device phone p1", T0.AddMinutes(1), amount));

        var alert = Only(T0.AddMinutes(2));

        Assert.False(alert.NeedsAnswer);
        Assert.Equal("Allowed", alert.Status);
        Assert.Equal("Release Manager (121) may message Release Manager (125)", alert.Title);
        Assert.Equal(summary, alert.Summary);
        Assert.Equal(T0.AddMinutes(1), alert.UpdatedAtUtc);
    }

    [Fact]
    public void Declined_SaysNothingWasSetUp()
    {
        var id = Ask();
        _requests.TryAnswer(Tenant, id, FleetMessageLinkRequestStatuses.Declined, "device phone p1", T0.AddMinutes(1));

        var alert = Only(T0.AddMinutes(2));

        Assert.False(alert.NeedsAnswer);
        Assert.Equal("Declined", alert.Status);
        Assert.Equal("You declined it. Nothing was set up.", alert.Summary);
    }

    [Fact]
    public void Pending_WhoseTargetEnded_IsEndedByTheRead_AndTheAskerIsTold()
    {
        Ask();
        _sessions[Target].ActivityState = "Exited";

        var alert = Only(T0.AddMinutes(5));

        Assert.False(alert.NeedsAnswer);
        Assert.Equal("Ended", alert.Status);
        Assert.Equal("A session ended before you answered. Nothing was set up.", alert.Summary);
        Assert.Contains(_told, t => t.StartsWith(Requester, StringComparison.Ordinal) && t.Contains("has ended"));
    }

    [Fact]
    public void AnAnsweredRequest_LeavesTheListTwentyFourHoursAfterItsAnswer()
    {
        var id = Ask();
        _requests.TryAnswer(Tenant, id, FleetMessageLinkRequestStatuses.Declined, "device phone p1", T0.AddMinutes(1));

        Assert.Single(_source.List(Tenant, T0.AddMinutes(1) + AlertWindow.Earlier));
        Assert.Empty(_source.List(Tenant, T0.AddMinutes(1) + AlertWindow.Earlier + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void APendingRequest_StaysOnTheListHoweverOld()
    {
        Ask();

        Assert.True(Only(T0.AddDays(3)).NeedsAnswer);
    }

    [Fact]
    public void TheDetail_SerialisesWithTheFieldNamesTheClientsMessageLinkRequestTypeReads()
    {
        Ask();

        var json = System.Text.Json.JsonSerializer.Serialize(Only(T0.AddMinutes(1)),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Contains("\"detail\":{\"requestId\":", json);
        Assert.Contains("\"status\":\"pending\"", json);
        Assert.Contains($"\"requesterSessionId\":\"{Requester}\"", json);
    }

    [Fact]
    public void AnotherAccountsRequest_IsNeverListed()
    {
        Ask(Other);

        Assert.Empty(_source.List(Tenant, T0.AddMinutes(1)));
    }

    private sealed class NoScope : IDisposable { public void Dispose() { } }
}
