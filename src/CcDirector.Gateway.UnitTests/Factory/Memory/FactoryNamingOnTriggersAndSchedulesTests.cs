using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Memory;

/// <summary>
/// WRITING A TRIGGER OR A SCHEDULE MUST NOT BE A WAY OF JOINING A FACTORY (Factory Memory mission, phase 1;
/// review finding 1, the highest of the three high findings).
///
/// Why these tests exist rather than the spawn-door ones being enough: the Gateway ITSELF stamps the session a
/// trigger or a schedule starts into that row's factory, and every session key may create and change both. So
/// a session in no factory could write a trigger naming <c>website-factory</c>, run it, and be handed
/// membership by the product - one hop around the forged-claim refusal, with the spawn door's tests all still
/// green. That is the defect this gate closes, and
/// <see cref="An_outside_session_may_not_put_a_trigger_into_a_factory"/> is the case that proves it.
/// </summary>
[Trait("Category", "FactoryMemory")]
public sealed class FactoryNamingOnTriggersAndSchedulesTests
{
    private const string CallerId = "11111111-2222-3333-4444-555555555555";
    private const string TheFactory = "website-factory";
    private const string AnotherFactory = "invoice-factory";

    private static HttpContext AsDevice(string deviceType = "browser")
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

    private static Func<string, SessionFactoryLookup> Reads(SessionFactoryLookup answer) => _ => answer;

    private static bool Settle(HttpContext ctx, Func<string, SessionFactoryLookup>? reader,
        string? requested, string? existing, out IResult? error, out string? settled)
        => FactoryNaming.TrySettle(ctx, reader, requested, existing, "trigger", "test", out error, out settled);

    // ---------- an outside session is refused, both ways round ----------

    [Fact]
    public void An_outside_session_may_not_put_a_trigger_into_a_factory()
    {
        // THE TEST THAT MATTERS: without this, writing a trigger IS joining a factory.
        Assert.False(Settle(AsSession(CallerId), Reads(SessionFactoryLookup.InNoFactory),
            requested: TheFactory, existing: null, out var error, out _));
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(error);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);
        var text = System.Text.Json.JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(error).Value!);
        Assert.Contains(TheFactory, text);
    }

    [Fact]
    public void A_session_of_ANOTHER_factory_may_not_put_a_trigger_into_this_one()
    {
        Assert.False(Settle(AsSession(CallerId), Reads(SessionFactoryLookup.In(AnotherFactory)),
            requested: TheFactory, existing: null, out var error, out _));
        var text = System.Text.Json.JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(error).Value!);
        Assert.Contains(AnotherFactory, text);
    }

    [Fact]
    public void An_outside_session_may_not_CHANGE_an_existing_triggers_factory()
    {
        // The PUT half of the defect: aim the next real run of somebody else's trigger at a factory of your own
        // choosing. Refused for the same reason as the create.
        Assert.False(Settle(AsSession(CallerId), Reads(SessionFactoryLookup.InNoFactory),
            requested: AnotherFactory, existing: TheFactory, out var error, out _));
        Assert.NotNull(error);
    }

    [Fact]
    public void An_outside_session_may_not_TAKE_a_trigger_OUT_of_a_factory_either()
    {
        // Tampering with the same sign reversed. A session that cannot add itself to the Website Factory must
        // equally not be able to take the Website Factory's own trigger away from it.
        Assert.False(Settle(AsSession(CallerId), Reads(SessionFactoryLookup.In(AnotherFactory)),
            requested: AnotherFactory, existing: TheFactory, out var error, out _));
        Assert.NotNull(error);
    }

    [Fact]
    public void A_session_IN_the_factory_may_not_move_its_trigger_to_a_DIFFERENT_factory()
    {
        // It holds the right to the factory it is leaving but not to the one it is naming.
        Assert.False(Settle(AsSession(CallerId), Reads(SessionFactoryLookup.In(TheFactory)),
            requested: AnotherFactory, existing: TheFactory, out var error, out _));
        Assert.NotNull(error);
    }

    // ---------- who may ----------

    [Fact]
    public void A_factory_session_MAY_write_a_trigger_for_its_OWN_factory()
    {
        Assert.True(Settle(AsSession(CallerId), Reads(SessionFactoryLookup.In(TheFactory)),
            requested: TheFactory, existing: null, out var error, out var settled));
        Assert.Null(error);
        Assert.Equal(TheFactory, settled);
    }

    [Fact]
    public void A_PERSON_may_put_a_trigger_into_any_of_their_factories()
    {
        Assert.True(Settle(AsDevice(), Reads(SessionFactoryLookup.NotKnown),
            requested: TheFactory, existing: null, out var error, out var settled));
        Assert.Null(error);
        Assert.Equal(TheFactory, settled);
    }

    [Fact]
    public void THE_ACCOUNTS_OWN_MACHINE_TOKEN_MAY_NAME_A_FACTORY()
    {
        // This test asserted the opposite until the phase 2 review pointed out what the first cut had done: a
        // caller that is neither a person's device nor a session is the account's shared machine token, or a
        // Director relaying - not an agent's credential at all. Refusing it bought nothing against the threat
        // this gate exists for (a SESSION KEY joining a factory) and broke three shipped tests that created a
        // factory's trigger with that token.
        Assert.True(Settle(new DefaultHttpContext(), Reads(SessionFactoryLookup.NotKnown),
            requested: TheFactory, existing: null, out var error, out var settled));
        Assert.Null(error);
        Assert.Equal(TheFactory, settled);
    }

    [Fact]
    public void A_caller_the_Gateway_has_no_row_for_yet_gets_the_try_again_answer()
    {
        Assert.False(Settle(AsSession(CallerId), Reads(SessionFactoryLookup.NotKnown),
            requested: TheFactory, existing: null, out var error, out _));
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(error);
        Assert.Equal(StatusCodes.Status409Conflict, status.StatusCode);
    }

    // ---------- an ordinary edit is untouched ----------

    [Fact]
    public void A_request_that_says_NOTHING_about_the_factory_KEEPS_the_stored_one()
    {
        // The common case, and the one that must not break: changing an interval must not quietly take the
        // Website Factory's Scout out of its factory. Anyone may make this edit.
        Assert.True(Settle(AsSession(CallerId), Reads(SessionFactoryLookup.InNoFactory),
            requested: null, existing: TheFactory, out var error, out var settled));
        Assert.Null(error);
        Assert.Equal(TheFactory, settled);
    }

    [Fact]
    public void Naming_the_factory_that_is_ALREADY_there_changes_nothing_and_is_allowed()
    {
        // No change, so there is nothing to authorize - a re-PUT of an unchanged definition by any caller.
        Assert.True(Settle(AsSession(CallerId), Reads(SessionFactoryLookup.InNoFactory),
            requested: TheFactory.ToUpperInvariant(), existing: TheFactory, out var error, out var settled));
        Assert.Null(error);
        Assert.Equal(TheFactory, settled);
    }

    [Fact]
    public void A_create_with_no_factory_is_allowed_to_anyone_and_settles_to_none()
    {
        Assert.True(Settle(new DefaultHttpContext(), factoryOfNull(), requested: null, existing: null,
            out var error, out var settled));
        Assert.Null(error);
        Assert.Null(settled);

        static Func<string, SessionFactoryLookup>? factoryOfNull() => null;
    }

    [Fact]
    public void A_blank_factory_is_read_as_saying_nothing()
    {
        Assert.True(Settle(AsSession(CallerId), Reads(SessionFactoryLookup.InNoFactory),
            requested: "  ", existing: TheFactory, out _, out var settled));
        Assert.Equal(TheFactory, settled);
    }

    // ---------- acting on a row that already belongs to a factory (review finding 2) ----------

    private static bool Act(HttpContext ctx, Func<string, SessionFactoryLookup>? reader, string? rowFactory, out IResult? error)
        => FactoryNaming.TryAct(ctx, reader, rowFactory, "schedule", "test", out error);

    [Fact]
    public void AN_OUTSIDE_SESSION_MAY_NOT_TOUCH_A_FACTORYS_SCHEDULE_AT_ALL()
    {
        // THE TEST FOR THE SECOND HOLE. Gating only the factory FIELD left this open: an outsider rewrites the
        // Scout's seed, runs it, and the Gateway starts a session in website-factory running the outsider's
        // instructions - which from phase 2 may write that factory's memory. The owner closed it on 28 September.
        Assert.False(Act(AsSession(CallerId), Reads(SessionFactoryLookup.InNoFactory), TheFactory, out var error));
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(error);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);
        var text = System.Text.Json.JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(error).Value!);
        Assert.Contains(TheFactory, text);
    }

    [Fact]
    public void A_session_of_ANOTHER_factory_may_not_touch_it_either()
    {
        Assert.False(Act(AsSession(CallerId), Reads(SessionFactoryLookup.In(AnotherFactory)), TheFactory, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void A_SESSION_OF_THAT_FACTORY_MAY()
    {
        Assert.True(Act(AsSession(CallerId), Reads(SessionFactoryLookup.In(TheFactory)), TheFactory, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void A_person_may_touch_any_of_their_factories_rows()
    {
        Assert.True(Act(AsDevice(), Reads(SessionFactoryLookup.NotKnown), TheFactory, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void A_ROW_WITH_NO_FACTORY_IS_OPEN_TO_ANYONE_EXACTLY_AS_BEFORE()
    {
        // The limit of this rule, said as a test: it closes a door into a factory, it does not put triggers and
        // schedules behind a new permission in general. Every schedule that is not a factory's is unaffected.
        Assert.True(Act(AsSession(CallerId), Reads(SessionFactoryLookup.InNoFactory), rowFactory: null, out _));
        Assert.True(Act(new DefaultHttpContext(), Reads(SessionFactoryLookup.NotKnown), rowFactory: "  ", out _));
    }

    [Fact]
    public void The_machine_token_may_touch_a_factorys_row_too()
    {
        // The same reasoning as naming one. A SESSION of another factory is still refused, which is the case
        // immediately above and the one that matters.
        Assert.True(Act(new DefaultHttpContext(), Reads(SessionFactoryLookup.NotKnown), TheFactory, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void A_caller_with_no_row_yet_gets_the_try_again_answer_here_too()
    {
        Assert.False(Act(AsSession(CallerId), Reads(SessionFactoryLookup.NotKnown), TheFactory, out var error));
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(error);
        Assert.Equal(StatusCodes.Status409Conflict, status.StatusCode);
    }

    [Fact]
    public void The_refusal_for_acting_says_why_the_whole_definition_is_guarded()
    {
        var detail = FactoryNaming.ActDetail("schedule", TheFactory);
        Assert.Contains("will be told to do", detail);
        Assert.Contains("read and write that factory's memory", detail);
        Assert.Contains("no factory on it is open to", detail);
    }

    [Fact]
    public void The_refusal_explains_that_naming_the_factory_is_joining_it()
    {
        // Pinned, because the sentence is the whole help a blocked caller gets: it has to say what to do next.
        var detail = FactoryNaming.Detail("schedule");
        Assert.Contains("born into its factory", detail);
        Assert.Contains("without a factory", detail);
        Assert.Contains("Cockpit", detail);
    }
}
