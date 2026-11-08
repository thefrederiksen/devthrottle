using CcDirector.Gateway.Util;
using Xunit;

namespace CcDirector.Gateway.Tests.Inbox;

/// <summary>
/// YOUR INBOX AND THE SESSION KEY GUARD (Factory Inbox mission). Every route was decided one by one. A session key may
/// SEND to the account's inbox (phase 3), and since the owner's decision of 7 November 2026 ("An agent should be able to do
/// what it can do with regular email") it may also read, mark, archive, restore and reply - through its OWN routes under
/// /sessions/{sid}, which record every act as the agent's - and leave a message in a factory's Factory inbox as itself.
/// The owner's own routes under /gateway/inbox, and his send to a factory, stay his alone and are refused here.
/// </summary>
public sealed class YourInboxSessionKeyGuardTests
{
    private const string Sid = "11111111-1111-4111-8111-111111111111";
    private const string Id = "22222222-2222-4222-8222-222222222222";

    [Theory]
    [InlineData("POST", "/sessions/" + Sid + "/inbox")]
    [InlineData("GET", "/sessions/" + Sid + "/inbox")]
    [InlineData("GET", "/sessions/" + Sid + "/inbox/" + Id)]
    [InlineData("POST", "/sessions/" + Sid + "/inbox/" + Id + "/state")]
    [InlineData("POST", "/sessions/" + Sid + "/inbox/" + Id + "/reply")]
    [InlineData("POST", "/sessions/" + Sid + "/factories/booking/inbox")]
    public void SessionKey_MayUseTheInboxThroughItsOwnRoutes(string method, string path)
        => Assert.True(SessionKeyGuard.Check(method, path).Allowed, $"{method} {path}");

    [Theory]
    [InlineData("PUT", "/sessions/" + Sid + "/inbox")]
    [InlineData("DELETE", "/sessions/" + Sid + "/inbox")]
    [InlineData("DELETE", "/sessions/" + Sid + "/inbox/" + Id)]
    [InlineData("POST", "/sessions/" + Sid + "/inbox/" + Id)]
    [InlineData("POST", "/sessions/" + Sid + "/inbox/" + Id + "/delete")]
    [InlineData("GET", "/sessions/" + Sid + "/inbox/" + Id + "/state")]
    [InlineData("POST", "/sessions/" + Sid + "/inbox/" + Id + "/state/extra")]
    [InlineData("GET", "/sessions/" + Sid + "/factories/booking/inbox")]
    [InlineData("POST", "/sessions/" + Sid + "/factories/booking")]
    [InlineData("GET", "/gateway/inbox")]
    [InlineData("GET", "/gateway/inbox/count")]
    [InlineData("GET", "/gateway/inbox/" + Id)]
    [InlineData("POST", "/gateway/inbox/" + Id + "/state")]
    [InlineData("POST", "/gateway/inbox/" + Id + "/reply")]
    [InlineData("DELETE", "/gateway/inbox/" + Id)]
    [InlineData("POST", "/gateway/factory/booking/inbox")]
    public void SessionKey_IsRefusedEveryOtherShape_AndEveryOwnerRoute(string method, string path)
    {
        var verdict = SessionKeyGuard.Check(method, path);

        Assert.False(verdict.Allowed);
        Assert.False(string.IsNullOrWhiteSpace(verdict.Reason));
    }

    [Fact]
    public void ARaisedSession_IsStillRefusedTheOwnersOwnRoutes()
    {
        Assert.False(SessionKeyGuard.Check("GET", "/gateway/inbox", raised: true).Allowed);
        Assert.False(SessionKeyGuard.Check("POST", $"/gateway/inbox/{Id}/reply", raised: true).Allowed);
        Assert.False(SessionKeyGuard.Check("POST", "/gateway/factory/booking/inbox", raised: true).Allowed);
    }
}
