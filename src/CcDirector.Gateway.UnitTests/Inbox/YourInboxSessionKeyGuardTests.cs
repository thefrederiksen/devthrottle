using CcDirector.Gateway.Util;
using Xunit;

namespace CcDirector.Gateway.Tests.Inbox;

/// <summary>
/// YOUR INBOX AND THE SESSION KEY GUARD (Factory Inbox mission, phase 3). Every new route was decided one by one: a session
/// key may SEND to the owner's inbox and do nothing else with it. The owner's list, one message, the marks, the reply and
/// the unread count are his alone, so a session key is refused each of them here, before the route runs.
/// </summary>
public sealed class YourInboxSessionKeyGuardTests
{
    private const string Sid = "11111111-1111-4111-8111-111111111111";
    private const string Id = "22222222-2222-4222-8222-222222222222";

    [Fact]
    public void SessionKey_MaySendToTheOwnersInbox()
        => Assert.True(SessionKeyGuard.Check("POST", $"/sessions/{Sid}/inbox").Allowed);

    [Theory]
    [InlineData("GET", "/sessions/" + Sid + "/inbox")]
    [InlineData("PUT", "/sessions/" + Sid + "/inbox")]
    [InlineData("DELETE", "/sessions/" + Sid + "/inbox")]
    [InlineData("POST", "/sessions/" + Sid + "/inbox/extra")]
    [InlineData("GET", "/gateway/inbox")]
    [InlineData("GET", "/gateway/inbox/count")]
    [InlineData("GET", "/gateway/inbox/" + Id)]
    [InlineData("POST", "/gateway/inbox/" + Id + "/state")]
    [InlineData("POST", "/gateway/inbox/" + Id + "/reply")]
    [InlineData("DELETE", "/gateway/inbox/" + Id)]
    public void SessionKey_IsRefusedEveryOtherShape_AndEveryOwnerRoute(string method, string path)
    {
        var verdict = SessionKeyGuard.Check(method, path);

        Assert.False(verdict.Allowed);
        Assert.False(string.IsNullOrWhiteSpace(verdict.Reason));
    }

    [Fact]
    public void ARaisedSession_IsStillRefusedTheOwnersMail()
    {
        Assert.False(SessionKeyGuard.Check("GET", "/gateway/inbox", raised: true).Allowed);
        Assert.False(SessionKeyGuard.Check("POST", $"/gateway/inbox/{Id}/reply", raised: true).Allowed);
    }
}
