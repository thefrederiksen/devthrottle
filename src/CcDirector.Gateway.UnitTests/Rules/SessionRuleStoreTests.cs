using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Rules;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// The rule store (Session Rules mission, phase 1). Covers the acceptance rows phase 1 owes: a rule
/// ROUND-TRIPS through the store with every part intact; a call naming a check that does not exist is
/// REFUSED at write time with a stated reason; a call with the wrong arguments to a real check is
/// REFUSED with a stated reason; a new rule is always in DRY RUN; and a dry-run rule cannot record having
/// typed anything.
///
/// Every test runs over an isolated on-disk SQLite database through the real migrated schema, so the
/// round-trip is a real write and a real read rather than an object handed back to itself.
/// </summary>
public sealed class SessionRuleStoreTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static readonly DateTime Now = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    private SessionRuleStore NewStore() => new(_h.Open());

    /// <summary>A grant as the promote route mints one, for a caller the request pipeline named.</summary>
    private static RulePromotionGrant GrantFor(Guid ruleId) =>
        RulePromotionGrant.FromAuthenticatedRequest(
            ruleId, AnInboundRequest.FromDevice(),
            "I have read this rule's dry-run record and I am making it live.", Now);

    /// <summary>What the grounding check found, as every firing has to carry (Architect ruling A12).</summary>
    private const string Grounding = "grounding: nothing was quoted, so there was nothing to check.";

    private static readonly string TheSentence =
        "When I run out of allowance on a model, switch me to Opus and carry on with whatever you were doing.";

    private static IReadOnlyList<RulePrimitiveCall> GoodCalls() => new[]
    {
        RulePrimitiveCall.To(
            "matches_any",
            RuleArgument.FromInput("text", RuleInput.ScreenText),
            RuleArgument.LiteralList("terms", new[] { "usage limit", "out of credits" })),
    };

    private SessionRule CreateTheRule(SessionRuleStore store) => store.Create(
        TheSentence,
        "A session stopped on a provider allowance notice, waiting for a person.",
        new[] { "limit", "usage-credits", "out of credits", "allowance", "/model" },
        GoodCalls(),
        RuleScope.AllSessions,
        cooldownSeconds: 300,
        dailyCap: 5,
        Now, AnActor.Owner);

    // ---- acceptance row 1: a rule round-trips ------------------------------------------------------

    [Fact]
    public void A_rule_round_trips_through_the_store_with_every_part_intact()
    {
        var store = NewStore();
        var created = CreateTheRule(store);

        // Read it back through a SECOND store over the same database - a real restart, not the object
        // that was just handed out.
        var reopened = new SessionRuleStore(_h.Open());
        var read = reopened.Get(created.Id);

        Assert.NotNull(read);
        Assert.Equal(TheSentence, read!.Instruction);
        Assert.Equal("A session stopped on a provider allowance notice, waiting for a person.",
            read.ScreenDescription);
        Assert.Equal(new[] { "limit", "usage-credits", "out of credits", "allowance", "/model" },
            read.TriggerWords);
        Assert.Equal(300, read.CooldownSeconds);
        Assert.Equal(5, read.DailyCap);
        Assert.Equal(RuleState.DryRun, read.State);
        Assert.Equal(Now, read.CreatedUtc);
        Assert.Equal(RuleScope.AllSessions, read.Scope);

        // The derived call survives whole: the name, the parameter, the source and the values.
        var call = Assert.Single(read.Calls);
        Assert.Equal("matches_any", call.Name);
        Assert.Equal(2, call.Arguments.Count);
        var text = call.Arguments.Single(a => a.Parameter == "text");
        Assert.Equal("input", text.Source);
        Assert.Equal(new[] { "screen_text" }, text.Values);
        var terms = call.Arguments.Single(a => a.Parameter == "terms");
        Assert.Equal("literal", terms.Source);
        Assert.Equal(new[] { "usage limit", "out of credits" }, terms.Values);
    }

    [Fact]
    public void A_rule_scoped_to_one_repository_round_trips_its_scope()
    {
        var store = NewStore();
        var scope = new RuleScope("claude", "D:\\ReposFred\\devthrottle", "SOREN_NORTH", "Session Rules");
        var created = store.Create(TheSentence, "a screen", new[] { "limit" }, GoodCalls(), scope, 60, 3, Now, AnActor.Owner);

        var read = new SessionRuleStore(_h.Open()).Get(created.Id);
        Assert.Equal(scope, read!.Scope);
    }

    [Fact]
    public void All_returns_the_rules_newest_first()
    {
        var store = NewStore();
        var first = store.Create(TheSentence, "a screen", new[] { "limit" }, GoodCalls(),
            RuleScope.AllSessions, 60, 3, Now, AnActor.Owner);
        var second = store.Create("Another instruction entirely.", "another screen", new[] { "permission" },
            GoodCalls(), RuleScope.AllSessions, 60, 3, Now.AddMinutes(5), AnActor.Owner);

        var all = store.All();
        Assert.Equal(2, all.Count);
        Assert.Equal(second.Id, all[0].Id);
        Assert.Equal(first.Id, all[1].Id);
    }

    [Fact]
    public void One_account_never_reads_another_accounts_rules()
    {
        var mine = new SessionRuleStore(_h.Open(new FixedTenantContext(new TenantId("tenant-a"))));
        var theirs = new SessionRuleStore(_h.Open(new FixedTenantContext(new TenantId("tenant-b"))));

        var created = mine.Create(TheSentence, "a screen", new[] { "limit" }, GoodCalls(),
            RuleScope.AllSessions, 60, 3, Now, AnActor.Owner);

        Assert.NotNull(mine.Get(created.Id));
        Assert.Null(theirs.Get(created.Id));
        Assert.Empty(theirs.All());
    }

    // ---- acceptance rows 2 and 3: refused at write time, with a reason -----------------------------

    [Fact]
    public void A_rule_naming_a_check_that_does_not_exist_is_refused_at_write_time_with_a_reason()
    {
        var store = NewStore();
        var badCall = RulePrimitiveCall.To("run_python", RuleArgument.Literal("code", "import os"));

        var ex = Assert.Throws<RuleRejectedException>(() => store.Create(
            TheSentence, "a screen", new[] { "limit" }, new[] { badCall },
            RuleScope.AllSessions, 60, 3, Now, AnActor.Owner));

        Assert.Contains("run_python", ex.Reason, StringComparison.Ordinal);
        Assert.Contains("matches_any", ex.Reason, StringComparison.Ordinal);

        // And nothing was written - a refusal that half-stored the rule would be worse than none.
        Assert.Empty(store.All());
    }

    [Fact]
    public void A_rule_supplying_the_wrong_arguments_to_a_real_check_is_refused_with_a_reason()
    {
        var store = NewStore();
        var badCall = RulePrimitiveCall.To(
            "is_path_inside",
            RuleArgument.Literal("target", "D:\\ReposFred\\devthrottle\\src\\file.cs"));

        var ex = Assert.Throws<RuleRejectedException>(() => store.Create(
            TheSentence, "a screen", new[] { "limit" }, new[] { badCall },
            RuleScope.AllSessions, 60, 3, Now, AnActor.Owner));

        Assert.Contains("is_path_inside", ex.Reason, StringComparison.Ordinal);
        Assert.Contains("root", ex.Reason, StringComparison.Ordinal);
        Assert.Empty(store.All());
    }

    [Theory]
    [InlineData("", "a screen", 60, 3)]
    [InlineData("   ", "a screen", 60, 3)]
    public void A_rule_with_no_instruction_is_refused_because_the_instruction_is_the_authority(
        string instruction, string screen, int cooldown, int cap)
    {
        var store = NewStore();
        var ex = Assert.Throws<RuleRejectedException>(() => store.Create(
            instruction, screen, new[] { "limit" }, GoodCalls(), RuleScope.AllSessions, cooldown, cap, Now, AnActor.Owner));
        Assert.NotEqual("", ex.Reason);
    }

    [Fact]
    public void A_rule_with_no_trigger_words_is_refused_because_it_would_cost_a_model_call_every_time()
    {
        var store = NewStore();
        var ex = Assert.Throws<RuleRejectedException>(() => store.Create(
            TheSentence, "a screen", Array.Empty<string>(), GoodCalls(),
            RuleScope.AllSessions, 60, 3, Now, AnActor.Owner));
        Assert.NotEqual("", ex.Reason);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(-1, 3)]
    [InlineData(60, 0)]
    [InlineData(60, -5)]
    public void A_rule_without_both_halves_of_the_ceiling_is_refused(int cooldown, int cap)
    {
        var store = NewStore();
        var ex = Assert.Throws<RuleRejectedException>(() => store.Create(
            TheSentence, "a screen", new[] { "limit" }, GoodCalls(),
            RuleScope.AllSessions, cooldown, cap, Now, AnActor.Owner));
        Assert.NotEqual("", ex.Reason);
    }

    // ---- acceptance row 6: dry run, and nothing types --------------------------------------------

    [Fact]
    public void A_new_rule_is_always_in_dry_run_and_only_a_person_moves_it()
    {
        var store = NewStore();
        var created = CreateTheRule(store);
        Assert.Equal(RuleState.DryRun, created.State);

        var promoted = store.Promote(created.Id, GrantFor(created.Id), Now.AddHours(1), AnActor.Owner);
        Assert.Equal(RuleState.Live, promoted.State);
        Assert.Equal(Now.AddHours(1), promoted.UpdatedUtc);
        Assert.Equal(RuleState.Live, new SessionRuleStore(_h.Open()).Get(created.Id)!.State);
    }

    [Fact]
    public void Promoting_a_rule_that_does_not_exist_is_refused_with_a_reason()
    {
        var store = NewStore();
        var missing = Guid.NewGuid();
        var ex = Assert.Throws<RuleRejectedException>(() => store.Promote(missing, GrantFor(missing), Now, AnActor.Owner));
        Assert.NotEqual("", ex.Reason);
    }

    [Fact]
    public void A_dry_run_firing_records_what_it_would_have_done_and_types_nothing()
    {
        var store = NewStore();
        var rule = CreateTheRule(store);

        var firing = store.RecordFiring(
            rule.Id,
            "session-101",
            "Claude usage limit reached. Your limit will reset at 14:30.",
            "The session is stopped on a provider allowance notice.",
            RuleDecisions.Act,
            "In dry run, so nothing was typed.",
            new[] { new RulePrimitiveRun("matches_any", "text=<screen_text>, terms=usage limit", "true") },
            typedText: "",
            outcome: "Reported only.",
            grounding: Grounding,
            Now);

        var read = Assert.Single(store.FiringsFor(rule.Id));
        Assert.Equal(firing.Id, read.Id);
        Assert.Equal("session-101", read.SessionId);
        Assert.Equal("Claude usage limit reached. Your limit will reset at 14:30.", read.ScreenText);
        Assert.Equal("The session is stopped on a provider allowance notice.", read.Understanding);
        Assert.Equal(RuleDecisions.Act, read.Decision);
        Assert.Equal("In dry run, so nothing was typed.", read.Reason);
        Assert.Equal("", read.TypedText);
        Assert.Equal("Reported only.", read.Outcome);

        var run = Assert.Single(read.PrimitiveRuns);
        Assert.Equal("matches_any", run.Name);
        Assert.Equal("text=<screen_text>, terms=usage limit", run.Arguments);
        Assert.Equal("true", run.Answer);
    }

    [Fact]
    public void A_firing_that_claims_a_dry_run_rule_typed_something_is_refused_with_a_reason()
    {
        var store = NewStore();
        var rule = CreateTheRule(store);

        var ex = Assert.Throws<RuleRejectedException>(() => store.RecordFiring(
            rule.Id, "session-101", "Claude usage limit reached.", "stopped on a limit",
            RuleDecisions.Act, "switched the model",
            Array.Empty<RulePrimitiveRun>(),
            typedText: "/model opus",
            outcome: "recovered",
            grounding: Grounding,
            Now));

        Assert.Contains("dry run", ex.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(store.FiringsFor(rule.Id));
    }

    [Fact]
    public void A_live_rule_may_record_what_it_typed()
    {
        var store = NewStore();
        var rule = CreateTheRule(store);
        store.Promote(rule.Id, GrantFor(rule.Id), Now, AnActor.Owner);

        var firing = store.RecordFiring(
            rule.Id, "session-101", "Claude usage limit reached.", "stopped on a limit",
            RuleDecisions.Act, "switched the model",
            Array.Empty<RulePrimitiveRun>(),
            typedText: "/model opus",
            outcome: "recovered",
            grounding: Grounding,
            Now);

        Assert.Equal("/model opus", store.FiringsFor(rule.Id).Single().TypedText);
        Assert.Equal(firing.Id, store.FiringsFor(rule.Id).Single().Id);
    }

    [Fact]
    public void A_firing_against_a_rule_that_does_not_exist_is_refused_with_a_reason()
    {
        var store = NewStore();
        var ex = Assert.Throws<RuleRejectedException>(() => store.RecordFiring(
            Guid.NewGuid(), "session-101", "a screen", "an understanding", RuleDecisions.Decline,
            "not covered", Array.Empty<RulePrimitiveRun>(), "", "nothing", Grounding, Now));
        Assert.NotEqual("", ex.Reason);
    }

    [Fact]
    public void A_declined_firing_is_recorded_like_any_other_because_a_decline_is_an_outcome()
    {
        var store = NewStore();
        var rule = CreateTheRule(store);

        store.RecordFiring(
            rule.Id, "session-101",
            "The team was discussing what happens when you hit a usage limit.",
            "A conversation about limits, not a session stopped on one.",
            RuleDecisions.Decline,
            "The instruction covers a session BLOCKED on an allowance notice; this session is not blocked.",
            Array.Empty<RulePrimitiveRun>(), "", "Left alone.", Grounding, Now);

        var read = Assert.Single(store.FiringsFor(rule.Id));
        Assert.Equal(RuleDecisions.Decline, read.Decision);
        Assert.Contains("not blocked", read.Reason, StringComparison.Ordinal);
        Assert.Equal("", read.TypedText);
    }

    [Fact]
    public void Deleting_a_rule_leaves_its_firings_behind_because_the_record_outlives_the_rule()
    {
        var store = NewStore();
        var rule = CreateTheRule(store);
        store.RecordFiring(rule.Id, "session-101", "a screen", "an understanding", RuleDecisions.Act,
            "dry run", Array.Empty<RulePrimitiveRun>(), "", "reported", Grounding, Now);

        Assert.True(store.Delete(rule.Id, Now, AnActor.Owner));
        Assert.Null(store.Get(rule.Id));
        Assert.Single(store.FiringsFor(rule.Id));
        Assert.False(store.Delete(rule.Id, Now, AnActor.Owner));
    }
}
