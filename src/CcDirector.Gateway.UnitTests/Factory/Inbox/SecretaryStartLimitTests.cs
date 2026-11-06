using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Inbox;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Util;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Inbox;

/// <summary>
/// The Secretary's limit, pure (Factory Inbox mission, phase 2, task D): which requests are start doors, and who is refused
/// at one. Every door named is one a session key can reach - so the list holds no dead shape - and the routes a Secretary
/// needs to do its job are not doors.
/// </summary>
public sealed class SecretaryStartLimitTests
{
    [Theory]
    [MemberData(nameof(SecretaryStartDoorTests.Doors), MemberType = typeof(SecretaryStartDoorTests))]
    public void IsAStartDoor_EveryListedDoor_IsADoor_InAnyCase(string method, string path)
    {
        Assert.True(SecretaryStartLimit.IsAStartDoor(method, path));
        Assert.True(SecretaryStartLimit.IsAStartDoor(method.ToLowerInvariant(), path.ToUpperInvariant() + "/"));
    }

    [Theory]
    [MemberData(nameof(SecretaryStartDoorTests.Doors), MemberType = typeof(SecretaryStartDoorTests))]
    public void IsAStartDoor_EveryListedDoor_IsReachableByASessionKey(string method, string path)
    {
        // A door the guard already refuses to every session key would be a dead entry: the list names only real doors.
        Assert.True(SessionKeyGuard.Check(method, path, raised: true).Allowed, $"{method} {path} is refused to every session key");
    }

    [Theory]
    [InlineData("GET", "/machines/SOREN_NORTH/sessions")]
    [InlineData("GET", "/cron/jobs")]
    [InlineData("GET", "/triggers")]
    [InlineData("DELETE", "/cron/jobs/job-1")]
    [InlineData("DELETE", "/triggers/trig-1")]
    [InlineData("POST", "/triggers/trig-1/pause")]
    [InlineData("POST", "/directors/dir-1/browsers")]
    [InlineData("POST", "/directors/dir-1/browsers/b-1/stop")]
    [InlineData("POST", "/machines/SOREN_NORTH/director/restart-requests")]
    [InlineData("POST", "/sessions/11111111-1111-4111-8111-111111111111/factory-inbox/receive")]
    [InlineData("POST", "/sessions/11111111-1111-4111-8111-111111111111/factory-inbox/bundle")]
    [InlineData("POST", "/sessions/11111111-1111-4111-8111-111111111111/message")]
    [InlineData("POST", "/sessions/11111111-1111-4111-8111-111111111111/request-deletion")]
    [InlineData("POST", "/gateway/factory/activity")]
    [InlineData("POST", "/gateway/workspaces")]
    [InlineData("POST", "/cron/runs/result")]
    [InlineData("POST", "/gateway/fleet-manager/outcomes")]
    [InlineData("", "")]
    public void IsAStartDoor_NotADoor_IsNot(string method, string path)
    {
        Assert.False(SecretaryStartLimit.IsAStartDoor(method, path));
    }

    [Fact]
    public void Refuse_ASecretaryStartedAsOne_InheritedOrMoved_IsRefused()
    {
        foreach (var kit in new[] { FactoryKits.Seat, FactoryKits.RulesOnly, null })
        {
            var refusal = SecretaryStartLimit.Refuse(SessionFactoryLookup.In("invoices", FactoryInboxSeats.Secretary) with { Kit = kit });
            Assert.NotNull(refusal);
            Assert.Equal(403, refusal!.Status);
            Assert.Equal("secretary_starts_nothing", refusal.Code);
            Assert.Equal(SecretaryStartLimit.Refusal, refusal.Sentence);
        }
    }

    [Fact]
    public void Refuse_ABossAWorkerAndASessionInNoFactory_AreNot()
    {
        Assert.Null(SecretaryStartLimit.Refuse(SessionFactoryLookup.In("invoices", "boss", 3) with { Kit = FactoryKits.Seat }));
        Assert.Null(SecretaryStartLimit.Refuse(SessionFactoryLookup.In("invoices", "writer", 3) with { Kit = FactoryKits.RulesOnly }));
        Assert.Null(SecretaryStartLimit.Refuse(SessionFactoryLookup.In("invoices")));
        Assert.Null(SecretaryStartLimit.Refuse(SessionFactoryLookup.InNoFactory));
    }

    [Fact]
    public void Refuse_ASessionWithNoHistoryRow_IsARetry_NeverAPass()
    {
        var refusal = SecretaryStartLimit.Refuse(SessionFactoryLookup.NotKnown);

        Assert.NotNull(refusal);
        Assert.Equal(409, refusal!.Status);
        Assert.Equal("session_not_yet_known", refusal.Code);
    }
}
