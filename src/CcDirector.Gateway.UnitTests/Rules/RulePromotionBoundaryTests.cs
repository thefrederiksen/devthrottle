using CcDirector.Gateway.Data;
using CcDirector.Gateway.Rules;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// DRY RUN IS THE OWNER'S MOST IMPORTANT BOUND, AND THIS IS WHERE IT IS ENFORCED RATHER THAN DESCRIBED.
/// The independent inspection of landing A found that <c>Promote</c> took a rule id and a timestamp and
/// nothing else, so anything that could read rules could move one to live, and that the test which claimed
/// "only a person moves it" proved only that a direct call worked - the caller in it had no property that
/// made it a person.
///
/// So these tests are written the other way round, exactly as the ruling asks: they show a caller that is
/// NOT a person being REFUSED. A promotion with no grant is refused; a grant that names nobody cannot be
/// minted; a grant with no acknowledgement cannot be minted; and a grant obtained for one rule cannot be
/// turned on another.
/// </summary>
public sealed class RulePromotionBoundaryTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static readonly DateTime Now = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>ONE database per test, opened once - see the note in RulesWriteGateTests. The single test
    /// that wants a real restart opens a second one and says why.</summary>
    private GatewayDatabase Db => _db ??= _h.Open();
    private GatewayDatabase? _db;

    private SessionRuleStore NewStore() => new(Db);

    private static IReadOnlyList<RulePrimitiveCall> GoodCalls() => new[]
    {
        RulePrimitiveCall.To(
            "matches_any",
            RuleArgument.FromInput("text", RuleInput.ScreenText),
            RuleArgument.LiteralList("terms", new[] { "usage limit" })),
    };

    private SessionRule CreateTheRule(SessionRuleStore store) => store.Create(
        "When I run out of allowance, switch me to Opus.",
        "A session stopped on a provider allowance notice.",
        new[] { "limit" },
        GoodCalls(),
        RuleScope.AllSessions,
        cooldownSeconds: 300,
        dailyCap: 5,
        Now, AnActor.Owner);

    /// <summary>A grant as the endpoint mints one, for a caller the request pipeline named.</summary>
    private static RulePromotionGrant GrantFor(Guid ruleId) =>
        RulePromotionGrant.FromAuthenticatedRequest(
            ruleId, AnInboundRequest.FromDevice(),
            "I have read this rule's dry-run record and I am making it live.", Now);

    [Fact]
    public void A_promotion_carrying_no_grant_is_refused_because_a_person_has_to_have_asked()
    {
        var store = NewStore();
        var rule = CreateTheRule(store);

        var ex = Assert.Throws<RuleRejectedException>(() => store.Promote(rule.Id, null!, Now, AnActor.Owner));

        Assert.Contains("person", ex.Reason, StringComparison.OrdinalIgnoreCase);
        // And the rule is still where it was. A refusal that half-promoted would be worse than none.
        Assert.Equal(RuleState.DryRun, store.Get(rule.Id)!.State);
    }

    [Fact]
    public void Code_with_no_inbound_request_cannot_mint_a_grant_at_all()
    {
        // THE AUTOMATED CALLER. It has no request, so there is nobody for the pipeline to have
        // authenticated - and it can no longer make up an identity, because the mint does not accept one.
        var nothing = Assert.Throws<RuleRejectedException>(() => RulePromotionGrant.FromAuthenticatedRequest(
            Guid.NewGuid(), http: null, acknowledgement: "make it live", Now));
        Assert.NotEqual("", nothing.Reason);
    }

    [Fact]
    public void A_request_the_pipeline_could_not_name_cannot_mint_a_grant()
    {
        // A REAL REQUEST that nothing authenticated. Having a request is not the bound; having one the
        // pipeline resolved a caller from is.
        var ex = Assert.Throws<RuleRejectedException>(() => RulePromotionGrant.FromAuthenticatedRequest(
            Guid.NewGuid(), AnInboundRequest.FromNobody(), "make it live", Now));
        Assert.NotEqual("", ex.Reason);
    }

    [Fact]
    public void A_promotion_with_nothing_said_cannot_mint_a_grant_so_an_empty_post_promotes_nothing()
    {
        var ex = Assert.Throws<RuleRejectedException>(() => RulePromotionGrant.FromAuthenticatedRequest(
            Guid.NewGuid(), AnInboundRequest.FromDevice(), acknowledgement: "", Now));
        Assert.NotEqual("", ex.Reason);
    }

    [Fact]
    public void The_actor_on_a_grant_is_read_from_the_request_and_not_from_the_caller()
    {
        // THE PRESENCE that makes the refusals above mean something: a request the pipeline DID name mints
        // a grant, and the name on it is the one the pipeline resolved - a signed-in person here, rather
        // than a device - so a live rule can always say who made it live.
        var grant = RulePromotionGrant.FromAuthenticatedRequest(
            Guid.NewGuid(), AnInboundRequest.FromSignedInPerson("soren@centerconsulting.com"),
            "I have read this rule's dry-run record and I am making it live.", Now);

        Assert.Equal("soren@centerconsulting.com", grant.Actor);
    }

    [Fact]
    public void A_grant_is_spent_by_the_promotion_it_was_obtained_for_and_cannot_be_used_again()
    {
        var store = NewStore();
        var first = CreateTheRule(store);
        var second = CreateTheRule(store);

        var grant = GrantFor(first.Id);
        Assert.Equal(RuleState.Live, store.Promote(first.Id, grant, Now, AnActor.Owner).State);

        // The same evidence, presented a second time. A person agreed once, to one rule.
        var again = Assert.Throws<RuleRejectedException>(() => store.Promote(first.Id, grant, Now, AnActor.Owner));
        Assert.Contains("already been used", again.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RuleState.DryRun, store.Get(second.Id)!.State);
    }

    [Fact]
    public void A_grant_obtained_for_one_rule_cannot_promote_another()
    {
        var store = NewStore();
        var mine = CreateTheRule(store);
        var other = CreateTheRule(store);

        var ex = Assert.Throws<RuleRejectedException>(() => store.Promote(other.Id, GrantFor(mine.Id), Now, AnActor.Owner));

        Assert.NotEqual("", ex.Reason);
        Assert.Equal(RuleState.DryRun, store.Get(other.Id)!.State);
    }

    [Fact]
    public void A_person_who_asked_does_promote_it_and_the_rule_records_who()
    {
        var store = NewStore();
        var rule = CreateTheRule(store);

        var promoted = store.Promote(rule.Id, GrantFor(rule.Id), Now.AddHours(1), AnActor.Owner);

        Assert.Equal(RuleState.Live, promoted.State);
        Assert.Equal("device:device-9f2c", promoted.PromotedBy);
        // Read back through a SECOND store over the same database, so this is the stored row and not the
        // object that was just handed out.
        var read = new SessionRuleStore(_h.Open()).Get(rule.Id)!;
        Assert.Equal(RuleState.Live, read.State);
        Assert.Equal("device:device-9f2c", read.PromotedBy);
    }

    [Fact]
    public void The_seam_the_evaluator_is_given_has_no_way_to_promote_create_or_delete_a_rule()
    {
        // The evaluator's environment is handed IRuleReading, not the store. A method that could change a
        // rule's life cycle must not be on it - this is the interface's whole reason for existing, so it is
        // asserted rather than left to whoever edits it next.
        var members = typeof(IRuleReading).GetMembers().Select(m => m.Name).ToList();
        Assert.NotEmpty(members);
        Assert.DoesNotContain(members, n => n.Contains("Promote", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(members, n => n.Contains("Create", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(members, n => n.Contains("Delete", StringComparison.OrdinalIgnoreCase));

        // A PRESENCE too: the seam must actually carry what the evaluator needs, or "it cannot promote"
        // would be true of an empty interface that also cannot do anything else.
        Assert.Contains(members, n => n == "All");
        Assert.Contains(members, n => n == "FiringsFor");
        Assert.Contains(members, n => n == "RecordFiring");
    }
}
