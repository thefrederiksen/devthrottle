using CcDirector.Core.Sessions;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Util;
using CcDirector.Core.Tenancy;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// WHO IS ASKING IS ESTABLISHED FROM THE CREDENTIAL, NEVER FROM THE BODY (issue #2838).
///
/// These drive <see cref="SpawnOrigin.TryEstablish"/> directly, because the interesting behaviour is
/// entirely in what it does to the request - which facts it overwrites, and which spawns it refuses.
/// Standing a Kestrel host up per case would test the routing, not the rule.
///
/// The case that matters most is <see cref="A_session_key_cannot_claim_to_be_human"/>. Every other test
/// here would still pass if the gate read <c>req.Origin</c> from the body - and a gate that reads a field
/// the caller controls is decoration, not enforcement.
/// </summary>
public sealed class SpawnOriginTests
{
    private const string CallerId = "11111111-2222-3333-4444-555555555555";
    private const string OwnerId = "99999999-8888-7777-6666-555555555555";

    private static HttpContext AsDevice(string deviceType)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[AuthMiddleware.DeviceTypeItemKey] = deviceType;
        return ctx;
    }

    private static HttpContext AsSession(string sessionId)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] =
            new SessionCredentialIdentity(Guid.Parse(sessionId), TenantId.System, "director-1");
        return ctx;
    }

    private static NewSessionRequest Body(string? origin = null, string? parent = null, string? owner = null) => new()
    {
        RepoPath = @"D:\repos\devthrottle",
        Agent = "ClaudeCode",
        Origin = origin,
        ParentSessionId = parent,
        ControllerSessionId = owner,
    };

    // ---------- a person is never asked ----------

    [Theory]
    [InlineData("phone")]
    [InlineData("browser")]
    public void A_person_is_never_asked_for_ownership(string deviceType)
    {
        var req = Body();
        Assert.True(SpawnOrigin.TryEstablish(req, AsDevice(deviceType), "test", out var error));
        Assert.Null(error);
        Assert.Equal(SessionOriginKinds.Human, req.Origin);
        Assert.Null(req.ControllerSessionId);
        Assert.Null(req.ParentSessionId);
    }

    [Fact]
    public void A_person_cannot_be_given_a_parent_by_the_body()
    {
        // A session a person opened has no parent, so a caller must not be able to invent one for it -
        // lineage is the thing the roster groups by, and a forged edge groups work under the wrong seat.
        var req = Body(origin: SessionOriginKinds.Agent, parent: CallerId, owner: OwnerId);
        Assert.True(SpawnOrigin.TryEstablish(req, AsDevice("phone"), "test", out _));
        Assert.Equal(SessionOriginKinds.Human, req.Origin);
        Assert.Null(req.ParentSessionId);
        Assert.Null(req.ControllerSessionId);
    }

    // ---------- an agent must declare ----------

    [Fact]
    public void A_session_that_declares_nothing_is_REFUSED()
    {
        var req = Body();
        Assert.False(SpawnOrigin.TryEstablish(req, AsSession(CallerId), "test", out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void A_session_may_declare_that_the_USER_owns_it()
    {
        var req = Body(owner: SpawnOrigin.UserOwned);
        Assert.True(SpawnOrigin.TryEstablish(req, AsSession(CallerId), "test", out var error));
        Assert.Null(error);
        // Null on the wire from here - but it now means "the user's" BECAUSE SOMEBODY SAID SO.
        Assert.Null(req.ControllerSessionId);
        Assert.Equal(SessionOriginKinds.Agent, req.Origin);
    }

    [Fact]
    public void A_session_may_declare_ITSELF_as_the_owner()
    {
        // Upper-cased on purpose: the id is compared as a session id, not as a string, and is stored in the
        // Gateway's own spelling.
        var req = Body(owner: CallerId.ToUpperInvariant());
        Assert.True(SpawnOrigin.TryEstablish(req, AsSession(CallerId), "test", out var error));
        Assert.Null(error);
        Assert.Equal(CallerId, req.ControllerSessionId);
    }

    [Fact]
    public void A_session_may_NOT_declare_another_session_as_the_owner()
    {
        // The Message Load mission, inspection 1, ruling 2. The owner is who the new session may message, so a
        // session key that could name an unrelated live session here would invent a messaging relationship.
        // This test was "A_session_may_declare_another_session_as_the_owner" until the slice 1 fix round.
        var req = Body(owner: OwnerId);
        Assert.False(SpawnOrigin.TryEstablish(req, AsSession(CallerId), "test", out var error));
        var json = Assert.IsAssignableFrom<IStatusCodeHttpResult>(error);
        Assert.Equal(StatusCodes.Status403Forbidden, json.StatusCode);
        var value = Assert.IsAssignableFrom<IValueHttpResult>(error).Value!;
        var text = System.Text.Json.JsonSerializer.Serialize(value);
        Assert.Contains(OwnerId, text);
        Assert.Contains("--controlled-by self", text);
    }

    [Fact]
    public void The_user_owned_spelling_is_the_literal_the_command_line_sends()
    {
        // Inspection 2, ruling 3. A CROSS-COMPONENT CONTRACT: cc-devthrottle sends the literal "none" for
        // --standalone and --controlled-by none (tools/cc-devthrottle/src/session_ops.py, pinned by
        // test_spawn_ops.py::test_standalone_sends_the_literal_the_gateway_recognises). The other tests here
        // pass SpawnOrigin.UserOwned back to itself and would stay green on any spelling; this one would not.
        Assert.Equal("none", SpawnOrigin.UserOwned);

        var req = Body(owner: "none");
        Assert.True(SpawnOrigin.TryEstablish(req, AsSession(CallerId), "test", out var error));
        Assert.Null(error);
        Assert.Null(req.ControllerSessionId);
    }

    [Fact]
    public void The_refusal_for_naming_another_owner_says_what_to_do_instead()
    {
        // Pinned to the literal, so a changed sentence is a deliberate edit of this test too.
        Assert.Equal(
            "The owner of a session is the session it may message and report to, so naming someone else would " +
            "create a messaging relationship that session never agreed to. Own it yourself " +
            "(--controlled-by self), or give it to the user (--standalone --why \"<reason>\"). If another " +
            "session should run this work, send that session a queued message and let it start the work itself.",
            SpawnOrigin.NamedAnotherOwner);
    }

    [Fact]
    public void The_owners_device_may_still_start_a_session_without_being_asked_about_ownership()
    {
        // The owner is unchanged by ruling 2: a device key never reaches the session arm, whatever the body says.
        var req = Body(owner: OwnerId);
        Assert.True(SpawnOrigin.TryEstablish(req, AsDevice("browser"), "test", out var error));
        Assert.Null(error);
    }

    [Fact]
    public void A_RELAYED_spawn_naming_an_owner_is_not_refused()
    {
        // A Director relaying a spawn carries its own key, not a session key, so the ruling does not reach it.
        var req = Body(origin: SessionOriginKinds.Agent, owner: OwnerId);
        Assert.True(SpawnOrigin.TryEstablish(req, new DefaultHttpContext(), "test", out var error));
        Assert.Null(error);
        Assert.Equal(OwnerId, req.ControllerSessionId);
    }

    [Fact]
    public void The_parent_is_the_CALLING_session_whatever_the_body_says()
    {
        // The body names somebody else entirely; the verified identity wins.
        var req = Body(parent: OwnerId, owner: SpawnOrigin.UserOwned);
        Assert.True(SpawnOrigin.TryEstablish(req, AsSession(CallerId), "test", out _));
        Assert.Equal(CallerId, req.ParentSessionId);
    }

    [Fact]
    public void A_session_key_cannot_claim_to_be_human()
    {
        // THE TEST THAT MATTERS. If the gate trusted req.Origin, this body would sail through unowned and
        // land red on the owner - which is the entire defect, reintroduced by one line.
        var req = Body(origin: SessionOriginKinds.Human);
        Assert.False(SpawnOrigin.TryEstablish(req, AsSession(CallerId), "test", out var error));
        Assert.NotNull(error);
        Assert.Equal(SessionOriginKinds.Agent, req.Origin);
    }

    // ---------- a malformed declaration is refused, not dropped ----------

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("self")]
    [InlineData("11111111-2222-3333-4444")]
    public void A_malformed_owner_id_is_REFUSED_rather_than_dropped(string bad)
    {
        // Falling through to null here is how a caller that DECLARED ownership and mistyped the id got an
        // unowned session and no error - the one outcome declaring ownership exists to prevent.
        var req = Body(owner: bad);
        Assert.False(SpawnOrigin.TryEstablish(req, AsSession(CallerId), "test", out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void The_user_owned_literal_is_case_insensitive()
    {
        var req = Body(owner: "NONE");
        Assert.True(SpawnOrigin.TryEstablish(req, AsSession(CallerId), "test", out _));
        Assert.Null(req.ControllerSessionId);
    }

    // ---------- everything else is left alone ----------

    [Fact]
    public void An_unrecognised_device_type_is_NOT_stamped_as_a_person()
    {
        // FromDeviceType never guesses. An unknown type is left alone rather than waved past as human -
        // the outcome that must never happen is an agent slipping through as a person.
        var req = Body(origin: SessionOriginKinds.Agent);
        Assert.True(SpawnOrigin.TryEstablish(req, AsDevice("something-new"), "test", out _));
        Assert.Equal(SessionOriginKinds.Agent, req.Origin);
    }

    [Fact]
    public void THE_BOUNDARY_a_non_session_credential_stating_human_is_left_alone()
    {
        // Said plainly rather than left to be discovered. The credential arm can only establish an origin
        // for callers it can identify: a person's device, or a session. A caller holding some OTHER
        // credential - a Director relaying, the cron starter - is trusted for its stated origin, because
        // those are internal hops whose own callers were already gated.
        //
        // This is where the enforcement ends, and the reason it is acceptable is that reaching this arm
        // requires a credential the product issues to its own components, not one an agent holds. If that
        // ever stops being true, this test is the one that should change first.
        var req = Body(origin: SessionOriginKinds.Human);
        Assert.True(SpawnOrigin.TryEstablish(req, new DefaultHttpContext(), "test", out var error));
        Assert.Null(error);
    }

    [Fact]
    public void A_scheduled_run_declares_nothing_and_is_not_refused()
    {
        // Nobody was at a keyboard, so there is no second candidate and nothing to state. A cron session
        // is the user's, and it goes red like anything else nobody is holding.
        var req = Body(origin: SessionOriginKinds.Schedule);
        Assert.True(SpawnOrigin.TryEstablish(req, new DefaultHttpContext(), "test", out var error));
        Assert.Null(error);
        Assert.Equal(SessionOriginKinds.Schedule, req.Origin);
    }

    [Fact]
    public void A_RELAYED_agent_spawn_is_not_refused()
    {
        // A Director relaying one of its agents' spawns carries its OWN key, and the body was written by a
        // command line already gated before it got there. Refusing on a stated origin would break
        // cross-machine spawning to close a door that needs a credential only our own components hold.
        var req = Body(origin: SessionOriginKinds.Agent, parent: CallerId);
        Assert.True(SpawnOrigin.TryEstablish(req, AsDevice("workstation"), "test", out var error));
        Assert.Null(error);
        Assert.Equal(SessionOriginKinds.Agent, req.Origin);
        Assert.Equal(CallerId, req.ParentSessionId);
    }
}
