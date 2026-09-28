using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Memory;

/// <summary>
/// MEMBERSHIP OF A FACTORY IS ESTABLISHED FROM THE CREDENTIAL, NEVER FROM THE BODY (Factory Memory mission,
/// phase 1; the mission document's section 7, membership).
///
/// These drive <see cref="SpawnFactory.TryEstablish"/> directly, for the reason
/// <see cref="SpawnOriginTests"/> gives about its own subject: the interesting behaviour is entirely in what
/// the helper does to the request and which spawns it refuses. Standing a Kestrel host up per case would test
/// the routing instead of the rule.
///
/// The case that matters most is <see cref="A_session_may_NOT_aim_a_child_at_another_factory"/> together with
/// <see cref="A_session_in_no_factory_may_not_claim_one"/>. Every other test here would still pass if the
/// helper read the factory out of the request body, and a gate that reads a field the caller controls is
/// decoration rather than enforcement.
/// </summary>
[Trait("Category", "FactoryMemory")]
public sealed class FactoryMembershipAtTheSpawnDoorTests
{
    private const string CallerId = "11111111-2222-3333-4444-555555555555";
    private const string ParentId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string TheFactory = "website-factory";
    private const string AnotherFactory = "invoice-factory";

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

    private static NewSessionRequest Body(string? factory = null) => new()
    {
        RepoPath = @"D:\repos\devthrottle",
        Agent = "ClaudeCode",
        Factory = factory,
    };

    /// <summary>A reader that answers for one session and records every id it was asked about.</summary>
    private sealed class Reader
    {
        private readonly Dictionary<string, SessionFactoryLookup> _answers = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Asked { get; } = new();

        public Reader In(string sessionId, string factory) { _answers[sessionId] = SessionFactoryLookup.In(factory); return this; }
        public Reader InNoFactory(string sessionId) { _answers[sessionId] = SessionFactoryLookup.InNoFactory; return this; }

        public Func<string, SessionFactoryLookup> Read => id =>
        {
            Asked.Add(id);
            return _answers.TryGetValue(id, out var answer) ? answer : SessionFactoryLookup.NotKnown;
        };
    }

    // ---------- inheritance: a child, and a child's child ----------

    [Fact]
    public void A_child_of_a_factory_session_is_born_into_that_factory()
    {
        var reader = new Reader().In(CallerId, TheFactory);
        var req = Body();
        Assert.True(SpawnFactory.TryEstablish(req, AsSession(CallerId), "test", reader.Read, out var error));
        Assert.Null(error);
        Assert.Equal(TheFactory, req.Factory);
    }

    [Fact]
    public void A_GRANDCHILD_is_born_into_the_factory_too_because_the_child_carries_it()
    {
        // The depth-2 case is the depth-1 case applied again, and saying so is the point: there is no special
        // handling for depth, which is exactly why a grandchild cannot be left out. The child asked about here
        // is a session whose own record already names the factory - which is what the first spawn produced.
        var reader = new Reader().In(ParentId, TheFactory);
        var req = Body();
        Assert.True(SpawnFactory.TryEstablish(req, AsSession(ParentId), "test", reader.Read, out _));
        Assert.Equal(TheFactory, req.Factory);
    }

    [Fact]
    public void THE_PARENT_CHAIN_IS_NEVER_WALKED_only_the_caller_is_asked()
    {
        // The negative control the review asked for, in its cheapest honest form. A parent's history row is
        // pruned ninety days after it ends, so a membership check that walked the chain would start refusing
        // long-lived grandchildren. If this helper ever asks about anything other than the calling session,
        // this test fails - and the pruned-parent defect is back.
        var reader = new Reader().In(CallerId, TheFactory);
        var req = Body();
        Assert.True(SpawnFactory.TryEstablish(req, AsSession(CallerId), "test", reader.Read, out _));
        Assert.Equal(new[] { CallerId }, reader.Asked);
    }

    [Fact]
    public void A_session_may_name_its_OWN_factory_and_it_grants_nothing()
    {
        // Naming your own factory documents intent and is accepted; it is not how membership is obtained.
        var reader = new Reader().In(CallerId, TheFactory);
        var req = Body(factory: TheFactory);
        Assert.True(SpawnFactory.TryEstablish(req, AsSession(CallerId), "test", reader.Read, out _));
        Assert.Equal(TheFactory, req.Factory);
    }

    [Fact]
    public void The_factory_is_recorded_in_the_GATEWAYS_spelling_not_the_callers()
    {
        var reader = new Reader().In(CallerId, TheFactory);
        var req = Body(factory: TheFactory.ToUpperInvariant());
        Assert.True(SpawnFactory.TryEstablish(req, AsSession(CallerId), "test", reader.Read, out _));
        Assert.Equal(TheFactory, req.Factory);
    }

    // ---------- a forged claim is refused ----------

    [Fact]
    public void A_session_may_NOT_aim_a_child_at_another_factory()
    {
        // THE TEST THAT MATTERS. One factory's agent starting a session in another factory would hand that
        // session the other factory's memory - the whole thing membership decides.
        var reader = new Reader().In(CallerId, TheFactory);
        var req = Body(factory: AnotherFactory);
        Assert.False(SpawnFactory.TryEstablish(req, AsSession(CallerId), "test", reader.Read, out var error));
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(error);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);
        var text = System.Text.Json.JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(error).Value!);
        Assert.Contains(AnotherFactory, text);
        Assert.Contains(TheFactory, text);
    }

    [Fact]
    public void A_session_in_no_factory_may_not_claim_one()
    {
        var reader = new Reader().InNoFactory(CallerId);
        var req = Body(factory: TheFactory);
        Assert.False(SpawnFactory.TryEstablish(req, AsSession(CallerId), "test", reader.Read, out var error));
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(error);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);
    }

    [Fact]
    public void A_session_in_no_factory_starts_a_session_in_no_factory()
    {
        var reader = new Reader().InNoFactory(CallerId);
        var req = Body();
        Assert.True(SpawnFactory.TryEstablish(req, AsSession(CallerId), "test", reader.Read, out _));
        Assert.Null(req.Factory);
    }

    // ---------- not yet known is not "in no factory" ----------

    [Fact]
    public void A_session_the_Gateway_has_no_row_for_yet_is_REFUSED_rather_than_given_nothing_to_inherit()
    {
        // The distinction review finding 4 exists for. Treating an absent row as "in no factory" would, for the
        // seconds after a Gateway restart, hand every child an empty factory - and that child stays outside its
        // parent's factory for life, because the field is written once. A refusal that says "try again" is
        // recoverable; a silently exiled grandchild is not.
        var reader = new Reader();
        var req = Body();
        Assert.False(SpawnFactory.TryEstablish(req, AsSession(CallerId), "test", reader.Read, out var error));
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(error);
        Assert.Equal(StatusCodes.Status409Conflict, status.StatusCode);
        var text = System.Text.Json.JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(error).Value!);
        Assert.Contains(SpawnFactory.NotYetKnown, text);
    }

    [Fact]
    public void The_not_yet_known_refusal_says_to_try_again_and_never_says_you_are_outside()
    {
        // Pinned to the literal, so a reworded sentence is a deliberate edit of this test too. The two answers
        // must not converge: one is a retry, the other is a judgement about membership.
        Assert.Equal("this session is not yet known to the Gateway; try again in a moment", SpawnFactory.NotYetKnown);
        Assert.DoesNotContain("no factory", SpawnFactory.NotYetKnown);
    }

    // ---------- a Gateway that cannot read membership hands none out ----------

    [Fact]
    public void With_no_reader_a_stated_factory_is_refused()
    {
        var req = Body(factory: TheFactory);
        Assert.False(SpawnFactory.TryEstablish(req, AsSession(CallerId), "test", factoryOf: null, out var error));
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(error);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status.StatusCode);
    }

    [Fact]
    public void With_no_reader_a_spawn_that_names_no_factory_still_starts()
    {
        // Fail closed on the GRANT, not on spawning itself: a Gateway built without the reader must not lose
        // the ability to start sessions, which is what refusing here would cost.
        var req = Body();
        Assert.True(SpawnFactory.TryEstablish(req, AsSession(CallerId), "test", factoryOf: null, out var error));
        Assert.Null(error);
        Assert.Null(req.Factory);
    }

    // ---------- a person, and the internal hops ----------

    [Theory]
    [InlineData("phone")]
    [InlineData("browser")]
    public void A_PERSON_may_put_a_session_into_a_factory_by_hand(string deviceType)
    {
        // The only way a factory gains a session nothing in the factory started. The command line cannot reach
        // this arm - it holds a session key - so this is the Cockpit's and the phone's spawn.
        var req = Body(factory: TheFactory);
        Assert.True(SpawnFactory.TryEstablish(req, AsDevice(deviceType), "test", new Reader().Read, out var error));
        Assert.Null(error);
        Assert.Equal(TheFactory, req.Factory);
    }

    [Fact]
    public void A_RESTORE_or_a_RELAY_carries_the_factory_through_as_it_arrived()
    {
        // A Director's credential. This arm is what makes a restore possible at all: the seat's factory is
        // handed back on the create, and if this door overwrote it the restored agent would come back outside
        // its own factory.
        var req = Body(factory: TheFactory);
        Assert.True(SpawnFactory.TryEstablish(req, new DefaultHttpContext(), "test", new Reader().Read, out var error));
        Assert.Null(error);
        Assert.Equal(TheFactory, req.Factory);
    }

    [Fact]
    public void A_blank_factory_is_read_as_none_rather_than_as_an_empty_name()
    {
        var req = Body(factory: "   ");
        Assert.True(SpawnFactory.TryEstablish(req, new DefaultHttpContext(), "test", new Reader().Read, out _));
        Assert.Null(req.Factory);
    }

    // ---------- a handover ----------

    [Fact]
    public void A_handover_asked_for_BY_THE_SOURCE_ITSELF_keeps_the_factory()
    {
        Assert.Equal(TheFactory, SpawnFactory.ForHandover(AsSession(CallerId), CallerId, TheFactory));
    }

    [Fact]
    public void A_handover_asked_for_BY_A_PERSON_keeps_the_factory()
    {
        Assert.Equal(TheFactory, SpawnFactory.ForHandover(AsDevice("browser"), CallerId, TheFactory));
    }

    [Fact]
    public void A_handover_asked_for_by_ANOTHER_SESSION_does_not_pass_the_factory_on()
    {
        // An unrelated session moving somebody else's work would otherwise mint a member of a factory it does
        // not belong to, and could then read that factory's memory through the session it created.
        Assert.Null(SpawnFactory.ForHandover(AsSession(CallerId), ParentId, TheFactory));
    }

    [Fact]
    public void A_handover_of_a_session_in_no_factory_produces_none()
    {
        Assert.Null(SpawnFactory.ForHandover(AsDevice("browser"), CallerId, sourceFactory: null));
    }
}
