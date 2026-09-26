using CcDirector.Gateway.Util;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// What a RAISED session key reaches that an unraised one does not (the Fleet Manager Improvement mission, phase 1),
/// and - the half that matters more - what it still does not. The guard stays an allow list: a raised key passes
/// exactly two named refusals, agent input and the owner-only Fleet Manager routes, and nothing else changes.
///
/// Every row here is asked BOTH ways, raised and not, so a row proves the widening and not merely that the route is
/// open. That the middleware really consults the list, with a real key over the real pipeline, is proven in
/// <c>RaisedSessionHostTests</c> (the parked suite); this file pins the pure rule.
/// </summary>
public sealed class SessionKeyGuardRaisedTests
{
    private const string Sid = "11111111-1111-1111-1111-111111111111";

    [Theory]
    [InlineData("POST", "/sessions/" + Sid + "/interrupt")]
    [InlineData("POST", "/sessions/" + Sid + "/escape")]
    [InlineData("POST", "/fanout")]
    [InlineData("POST", "/sessions/" + Sid + "/turn-verdict/answer")]
    [InlineData("POST", "/Sessions/" + Sid + "/INTERRUPT/")]
    public void Check_AgentInput_RaisedPasses_UnraisedIsRefusedAsToday(string method, string path)
    {
        var raised = SessionKeyGuard.Check(method, path, raised: true);
        Assert.True(raised.Allowed, $"{method} {path} must pass for a raised key");
        Assert.Equal(RaisedGrant.AgentInput, raised.RaisedGrant);

        var unraised = SessionKeyGuard.Check(method, path, raised: false);
        Assert.False(unraised.Allowed);
        Assert.Equal(AgentInputRefusal.Typing, unraised.Reason);
        Assert.Equal(RaisedGrant.None, unraised.RaisedGrant);
    }

    /// <summary>The prompt shape (Parent Control, fix 1): a raised key still passes with the recorded grant; an unraised
    /// key is let through to the route, which types only into a session the caller owns.</summary>
    [Theory]
    [InlineData("POST", "/sessions/" + Sid + "/prompt")]
    [InlineData("POST", "/Sessions/" + Sid + "/PROMPT/")]
    public void Check_Prompt_RaisedPassesWithTheGrant_UnraisedReachesTheOwnershipRoute(string method, string path)
    {
        var raised = SessionKeyGuard.Check(method, path, raised: true);
        Assert.True(raised.Allowed);
        Assert.Equal(RaisedGrant.AgentInput, raised.RaisedGrant);

        var unraised = SessionKeyGuard.Check(method, path, raised: false);
        Assert.True(unraised.Allowed);
        Assert.Equal(RaisedGrant.None, unraised.RaisedGrant);
    }

    [Theory]
    [InlineData("GET", "/gateway/fleet-manager/placement")]
    [InlineData("PUT", "/gateway/fleet-manager/placement")]
    [InlineData("POST", "/gateway/fleet-manager/start")]
    [InlineData("POST", "/gateway/fleet-manager/restart")]
    [InlineData("POST", "/gateway/fleet-manager/move")]
    [InlineData("POST", "/Gateway/Fleet-Manager/Move")]
    [InlineData("GET", "/gateway/fleet-manager/page")]
    [InlineData("HEAD", "/gateway/fleet-manager/page")]
    [InlineData("GET", "/gateway/fleet-manager/walkthrough")]
    public void Check_OwnerOnlyFleetManagerRoute_RaisedPasses_UnraisedIsRefusedAsToday(string method, string path)
    {
        var raised = SessionKeyGuard.Check(method, path, raised: true);
        Assert.True(raised.Allowed, $"{method} {path} must pass for a raised key");
        Assert.Equal(RaisedGrant.FleetManagerOwnerRoute, raised.RaisedGrant);

        var unraised = SessionKeyGuard.Check(method, path, raised: false);
        Assert.False(unraised.Allowed);
        Assert.Equal(RaisedGrant.None, unraised.RaisedGrant);
    }

    /// <summary>
    /// WHAT RAISED DOES NOT BUY. Each of these is refused to a raised key with the very same verdict an unraised key
    /// gets - the two are compared, so a future edit that opens one of them for raised keys only turns this red.
    /// </summary>
    [Theory]
    // Raising and lowering: never another session, never itself.
    [InlineData("POST", "/sessions/" + Sid + "/raise")]
    [InlineData("POST", "/sessions/" + Sid + "/lower")]
    // The admission surface, by the routes the Gateway really maps: devices, enrolling one, signing out, and the
    // account with its trial and its credits (the Gateway's side of billing - the payment pages are not the Gateway's).
    [InlineData("GET", "/devices")]
    [InlineData("POST", "/devices/enroll-signed-in")]
    [InlineData("POST", "/mobile/enroll")]
    [InlineData("GET", "/account/devices")]
    [InlineData("DELETE", "/account/devices/abc")]
    [InlineData("POST", "/account/logout")]
    [InlineData("POST", "/account/email")]
    [InlineData("GET", "/account/status")]
    [InlineData("GET", "/account/trial")]
    [InlineData("GET", "/account/credits")]
    // Shutting the Gateway down.
    [InlineData("POST", "/shutdown")]
    // The walkthrough's writes store "the owner answered, snoozed, closed". A session doing them would be a false record.
    [InlineData("POST", "/gateway/fleet-manager/walkthrough/5b1c2d3e-0000-4000-8000-000000000001/answered")]
    [InlineData("POST", "/gateway/fleet-manager/walkthrough/5b1c2d3e-0000-4000-8000-000000000001/snoozed")]
    [InlineData("POST", "/gateway/fleet-manager/walkthrough/5b1c2d3e-0000-4000-8000-000000000001/close")]
    // Verbs the Gateway does not route on the owner's words stay refused.
    [InlineData("POST", "/gateway/fleet-manager/placement")]
    [InlineData("DELETE", "/gateway/fleet-manager/placement")]
    [InlineData("GET", "/gateway/fleet-manager/start")]
    [InlineData("POST", "/gateway/fleet-manager/page")]
    [InlineData("POST", "/gateway/fleet-manager/anything-new")]
    // The governance record is the owner's to read; a raised session does not read its own trail.
    [InlineData("GET", "/gateway/governance/audit-events")]
    public void Check_EverythingElseTheOwnerAloneMayDo_IsRefusedToARaisedKeyExactlyAsToAnUnraisedOne(string method, string path)
    {
        var raised = SessionKeyGuard.Check(method, path, raised: true);
        var unraised = SessionKeyGuard.Check(method, path, raised: false);

        Assert.False(raised.Allowed, $"{method} {path} must stay refused to a raised key");
        Assert.Equal(RaisedGrant.None, raised.RaisedGrant);
        Assert.Equal(unraised, raised);
    }

    /// <summary>A route every session key reaches carries no raised grant, so it leaves no raised record.</summary>
    [Theory]
    [InlineData("GET", "/sessions")]
    [InlineData("POST", "/sessions/" + Sid + "/message")]
    [InlineData("PUT", "/gateway/fleet-manager")]
    public void Check_ARouteEverySessionKeyReaches_CarriesNoRaisedGrant(string method, string path)
    {
        var raised = SessionKeyGuard.Check(method, path, raised: true);

        Assert.True(raised.Allowed);
        Assert.Equal(RaisedGrant.None, raised.RaisedGrant);
        Assert.Equal(SessionKeyGuard.Check(method, path, raised: false), raised);
    }

    [Fact]
    public void Check_TheTwoArgumentForm_IsTheUnraisedAnswer()
    {
        var path = "/sessions/" + Sid + "/interrupt";

        Assert.Equal(SessionKeyGuard.Check("POST", path, raised: false), SessionKeyGuard.Check("POST", path));
        Assert.False(SessionKeyGuard.Check("POST", path).Allowed);
    }
}
