using CcDirector.Gateway.Util;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Inbox;

/// <summary>
/// WHAT A SESSION KEY MAY CALL ON THE FACTORY INBOX (Factory Inbox mission, phase 1). The session routes and the owner's
/// four reads are allowed, shape by shape; the owner's SEND is refused, and so is every neighbouring shape that was not
/// added on purpose - because an allow list that has quietly grown fails by allowing, and only a test that names the
/// thing it must refuse can see that.
/// </summary>
public sealed class FactoryInboxSessionKeyGuardTests
{
    private const string Sid = "11111111-1111-1111-1111-111111111111";
    private const string Mid = "22222222-2222-2222-2222-222222222222";

    [Theory]
    // The session routes: the route itself lets through only the factory's Secretary or Boss, as itself.
    [InlineData("GET", "/sessions/" + Sid + "/factory-inbox")]
    [InlineData("POST", "/sessions/" + Sid + "/factory-inbox/receive")]
    [InlineData("POST", "/sessions/" + Sid + "/factory-inbox/bundle")]
    [InlineData("GET", "/sessions/" + Sid + "/factory-inbox/bundle")]
    [InlineData("POST", "/sessions/" + Sid + "/factory-inbox/" + Mid + "/decide")]
    // The owner's reads: a Boss reads its factory's summary and to-do list.
    [InlineData("GET", "/gateway/factory/inbox")]
    [InlineData("GET", "/gateway/factory/invoices/inbox")]
    [InlineData("GET", "/gateway/factory/invoices/inbox/summary")]
    [InlineData("GET", "/gateway/factory/invoices/todo")]
    public void FactoryInboxRoute_IsAllowedToASessionKey(string method, string path)
        => Assert.True(SessionKeyGuard.Check(method, path).Allowed, $"{method} {path} should be allowed");

    [Theory]
    // THE OWNER'S SEND: a session never leaves a message in the owner's name.
    [InlineData("POST", "/gateway/factory/invoices/inbox")]
    [InlineData("PUT", "/gateway/factory/invoices/inbox")]
    [InlineData("DELETE", "/gateway/factory/invoices/inbox")]
    [InlineData("POST", "/gateway/factory/inbox")]
    [InlineData("POST", "/gateway/factory/invoices/todo")]
    [InlineData("DELETE", "/gateway/factory/invoices/todo")]
    [InlineData("POST", "/gateway/factory/invoices/inbox/summary")]
    // THE OWNER'S SPOKEN SEND (phase 4): refused exactly as the typed one is.
    [InlineData("POST", "/gateway/factory/invoices/inbox/voice")]
    [InlineData("POST", "/gateway/factory/invoices/inbox/voice/" + Mid + "/complete")]
    // Shapes nobody added.
    [InlineData("GET", "/gateway/factory/invoices/outbox")]
    [InlineData("GET", "/gateway/factory/invoices/inbox/everything")]
    [InlineData("GET", "/gateway/factory/invoices/inbox/summary/more")]
    [InlineData("POST", "/sessions/" + Sid + "/factory-inbox")]
    [InlineData("DELETE", "/sessions/" + Sid + "/factory-inbox")]
    [InlineData("GET", "/sessions/" + Sid + "/factory-inbox/receive")]
    [InlineData("DELETE", "/sessions/" + Sid + "/factory-inbox/bundle")]
    [InlineData("POST", "/sessions/" + Sid + "/factory-inbox/undo")]
    [InlineData("GET", "/sessions/" + Sid + "/factory-inbox/" + Mid + "/decide")]
    [InlineData("PUT", "/sessions/" + Sid + "/factory-inbox/" + Mid + "/decide")]
    [InlineData("POST", "/sessions/" + Sid + "/factory-inbox/" + Mid + "/undecide")]
    [InlineData("POST", "/sessions/" + Sid + "/factory-inbox/" + Mid + "/decide/again")]
    public void FactoryInboxRoute_NotAddedOnPurpose_IsRefusedToASessionKey(string method, string path)
        => Assert.False(SessionKeyGuard.Check(method, path).Allowed, $"{method} {path} must be refused");
}
