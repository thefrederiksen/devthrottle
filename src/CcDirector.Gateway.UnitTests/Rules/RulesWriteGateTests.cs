using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Rules;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// THE VALIDATOR IS THE WRITE GATE, AND THESE TESTS TAKE THE ROUTE THAT WENT ROUND IT. The store's own
/// source claimed that nothing reaches <c>session_rules</c> without passing the validator. The independent
/// inspection of landing A found that claim was false: the entity, its setters, the DbSet and the context
/// factory are all public, so a Gateway caller could construct a rule with an arbitrary call document, an
/// arbitrary tenant and <c>State = "live"</c>, add it and save it, meeting neither the validator nor dry
/// run. One bypass defeated three properties at once.
///
/// So the check now lives in <c>SaveChanges</c>, where the route cannot go round it, and every test here
/// writes THROUGH THE CONTEXT rather than through the store - the bypass itself, run as a test.
///
/// The firing tests are the same shape for the other half of finding 9: the record is the product, and a
/// record that can be blank or can name a check that never ran is a record nobody can use as evidence.
/// </summary>
public sealed class RulesWriteGateTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    /// <summary>ONE database per test, opened once. Opening it again is a real cost - the local gate's
    /// two-minute ceiling is shared by everyone on every change - and nothing here needs a restart: the
    /// store reads through a fresh context on every call, so reading back through the same database is
    /// reading the stored row, not the object that was handed out. The one test that genuinely wants a
    /// restart says so and opens a second one.</summary>
    private GatewayDatabase Db => _db ??= _h.Open();
    private GatewayDatabase? _db;

    public void Dispose() => _h.Dispose();

    private static readonly DateTime Now = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    private static readonly string DryRun = RuleWireNames.ToWireName(nameof(RuleState.DryRun));
    private static readonly string Live = RuleWireNames.ToWireName(nameof(RuleState.Live));

    private static List<RulePrimitiveCall> GoodCalls() => new()
    {
        RulePrimitiveCall.To(
            "matches_any",
            RuleArgument.FromInput("text", RuleInput.ScreenText),
            RuleArgument.LiteralList("terms", new[] { "usage limit" })),
    };

    /// <summary>A rule entity as a caller going round the store would build one.</summary>
    private static SessionRuleEntity Straight(string tenant, string state, List<RulePrimitiveCall> calls) => new()
    {
        TenantId = tenant,
        Instruction = "When I run out of allowance, switch me to Opus.",
        ScreenDescription = "A session stopped on a provider allowance notice.",
        TriggerWords = new List<string> { "limit" },
        Calls = calls,
        CooldownSeconds = 300,
        DailyCap = 5,
        State = state,
        CreatedUtc = Now,
        UpdatedUtc = Now,
    };

    /// <summary>What the grounding check found, as every firing has to carry (Architect ruling A12).</summary>
    private const string Grounding = "grounding: nothing was cited, so there was nothing to check.";

    // ---- finding 2: the route round the validator ---------------------------------------------------

    [Fact]
    public void A_rule_written_straight_through_the_data_context_still_meets_the_validator()
    {
        using var ctx = Db.CreateContext();

        // A call naming a check the product does not ship - exactly what the store refuses, arriving by the
        // route that used to miss the store entirely.
        var invented = new List<RulePrimitiveCall>
        {
            RulePrimitiveCall.To("run_python", RuleArgument.Literal("code", "import os")),
        };
        ctx.SessionRules.Add(Straight(ctx.ActiveTenant!, DryRun, invented));

        var ex = Assert.Throws<RuleRejectedException>(() => ctx.SaveChanges());
        Assert.Contains("run_python", ex.Reason, StringComparison.Ordinal);

        Assert.Empty(new SessionRuleStore(Db).All());
    }

    [Fact]
    public void A_rule_written_straight_through_the_data_context_cannot_start_live()
    {
        using var ctx = Db.CreateContext();
        ctx.SessionRules.Add(Straight(ctx.ActiveTenant!, Live, GoodCalls()));

        var ex = Assert.Throws<RuleRejectedException>(() => ctx.SaveChanges());
        Assert.Contains("dry run", ex.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(new SessionRuleStore(Db).All());
    }

    [Fact]
    public void A_rule_written_straight_through_the_data_context_cannot_belong_to_another_account()
    {
        var other = _h.Open(new FixedTenantContext(new TenantId("tenant-a")));
        using var ctx = other.CreateContext();
        ctx.SessionRules.Add(Straight("tenant-b", DryRun, GoodCalls()));

        var ex = Assert.Throws<RuleRejectedException>(() => ctx.SaveChanges());
        Assert.Contains("tenant-b", ex.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rule_moved_to_live_straight_through_the_data_context_is_refused_without_a_grant()
    {
        var store = new SessionRuleStore(Db);
        var rule = store.Create(
            "When I run out of allowance, switch me to Opus.",
            "A session stopped on a provider allowance notice.",
            new[] { "limit" }, GoodCalls(), RuleScope.AllSessions, 300, 5, Now, AnActor.Owner);

        using var ctx = Db.CreateContext();
        var row = ctx.SessionRules.First(r => r.Id == rule.Id);
        row.State = Live;

        var ex = Assert.Throws<RuleRejectedException>(() => ctx.SaveChanges());
        Assert.Contains("person", ex.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RuleState.DryRun, new SessionRuleStore(Db).Get(rule.Id)!.State);
    }

    [Fact]
    public void The_gate_lets_a_rule_the_store_built_through_so_it_is_a_gate_and_not_a_wall()
    {
        // The PRESENCE half. A gate that refused everything would pass every test above while making the
        // feature impossible, and the tests above could not tell the difference.
        var store = new SessionRuleStore(Db);
        var rule = store.Create(
            "When I run out of allowance, switch me to Opus.",
            "A session stopped on a provider allowance notice.",
            new[] { "limit" }, GoodCalls(), RuleScope.AllSessions, 300, 5, Now, AnActor.Owner);

        Assert.Equal(RuleState.DryRun, rule.State);
        Assert.Single(new SessionRuleStore(Db).All());
    }

    // ---- finding 9: the record is the product -------------------------------------------------------

    private (SessionRuleStore Store, SessionRule Rule) StoreWithARule()
    {
        var store = new SessionRuleStore(Db);
        var rule = store.Create(
            "When I run out of allowance, switch me to Opus.",
            "A session stopped on a provider allowance notice.",
            new[] { "limit" }, GoodCalls(), RuleScope.AllSessions, 300, 5, Now, AnActor.Owner);
        return (store, rule);
    }

    /// <summary>
    /// EVERY REQUIRED PART OF THE RECORD, EACH MISSING IN TURN, and every one of them refused.
    ///
    /// Written as one test over a loop rather than as a theory with a case each, and the reason is the
    /// two-minute ceiling on the local gate: each test in this class builds its own migrated database, so
    /// eight cases cost eight of them. The cases are still checked one at a time and each names itself when
    /// it fails, so nothing is lost except seven database builds that everyone on every change was paying
    /// for.
    /// </summary>
    [Fact]
    public void A_firing_missing_any_part_of_the_record_is_refused_rather_than_blanked()
    {
        var (store, rule) = StoreWithARule();

        var cases = new (string What, string? SessionId, string? Decision, string? Reason, string? Outcome)[]
        {
            ("no session at all",   null,          RuleDecisions.Decline, "not covered", "left alone"),
            ("a blank session",     "",            RuleDecisions.Decline, "not covered", "left alone"),
            ("no decision at all",  "session-101", null,                  "not covered", "left alone"),
            ("a blank decision",    "session-101", "",                    "not covered", "left alone"),
            ("no reason at all",    "session-101", RuleDecisions.Decline, null,          "left alone"),
            ("a blank reason",      "session-101", RuleDecisions.Decline, "",            "left alone"),
            ("no outcome at all",   "session-101", RuleDecisions.Decline, "not covered", null),
            ("a blank outcome",     "session-101", RuleDecisions.Decline, "not covered", ""),
        };

        foreach (var (what, sessionId, decision, reason, outcome) in cases)
        {
            var ex = Assert.Throws<RuleRejectedException>(() => store.RecordFiring(
                rule.Id, sessionId!, "a screen", "an understanding", decision!, reason!,
                Array.Empty<RulePrimitiveRun>(), "", outcome!, "grounding: nothing was quoted.", Now));

            Assert.True(ex.Reason.Length > 0, "a firing with " + what + " was refused without a reason.");
        }

        Assert.Empty(store.FiringsFor(rule.Id));
    }

    /// <summary>
    /// THE OTHER WAYS A RECORD CAN BE A RECORD OF NOTHING, in one test for the same reason as above: a
    /// decision this build does not know, a check the product does not ship, a check that answered
    /// nothing, and a firing that cannot say what the grounding check found.
    ///
    /// That last one is the PRESENCE ruling A12 asks for. A firing carrying no grounding statement is a
    /// firing whose grounding check may simply never have run, and that must not be indistinguishable
    /// from one that ran and found nothing wrong.
    /// </summary>
    [Fact]
    public void A_firing_that_claims_something_it_cannot_support_is_refused()
    {
        var (store, rule) = StoreWithARule();

        var unknownDecision = Assert.Throws<RuleRejectedException>(() => store.RecordFiring(
            rule.Id, "session-101", "a screen", "an understanding", "would_have_acted", "dry run",
            Array.Empty<RulePrimitiveRun>(), "", "reported", "grounding: nothing was quoted.", Now));
        Assert.Contains("would_have_acted", unknownDecision.Reason, StringComparison.Ordinal);

        var inventedCheck = Assert.Throws<RuleRejectedException>(() => store.RecordFiring(
            rule.Id, "session-101", "a screen", "an understanding", RuleDecisions.Decline, "not covered",
            new[] { new RulePrimitiveRun("run_python", "code=import os", "true") },
            "", "left alone", "grounding: nothing was quoted.", Now));
        Assert.Contains("run_python", inventedCheck.Reason, StringComparison.Ordinal);

        var noAnswer = Assert.Throws<RuleRejectedException>(() => store.RecordFiring(
            rule.Id, "session-101", "a screen", "an understanding", RuleDecisions.Decline, "not covered",
            new[] { new RulePrimitiveRun("matches_any", "text=<screen_text>, terms=usage limit", "") },
            "", "left alone", "grounding: nothing was quoted.", Now));
        Assert.Contains("matches_any", noAnswer.Reason, StringComparison.Ordinal);

        var noGrounding = Assert.Throws<RuleRejectedException>(() => store.RecordFiring(
            rule.Id, "session-101", "a screen", "an understanding", RuleDecisions.Decline, "not covered",
            Array.Empty<RulePrimitiveRun>(), "", "left alone", grounding: "", Now));
        Assert.True(noGrounding.Reason.Length > 0);

        Assert.Empty(store.FiringsFor(rule.Id));
    }

    [Fact]
    public void A_complete_firing_is_written_whole_so_the_refusals_above_are_not_a_wall()
    {
        var (store, rule) = StoreWithARule();

        store.RecordFiring(
            rule.Id, "session-101", "Claude usage limit reached.", "stopped on an allowance notice",
            RuleDecisions.Act, "the screen is the notice the instruction is about",
            new[] { new RulePrimitiveRun("matches_any", "text=<screen_text>, terms=usage limit", "true") },
            "", "dry run: nothing was typed.",
            "grounding: 1 quoted passage checked against the screen, all found.", Now);

        var read = Assert.Single(store.FiringsFor(rule.Id));
        Assert.Equal(RuleDecisions.Act, read.Decision);
        Assert.Contains("grounding:", read.Grounding, StringComparison.Ordinal);
    }

    // ---- finding 5: a missing scope silently widened to every session -------------------------------

    [Fact]
    public void A_rule_with_no_scope_at_all_is_refused_because_every_session_has_to_be_said_out_loud()
    {
        var store = new SessionRuleStore(Db);

        var ex = Assert.Throws<RuleRejectedException>(() => store.Create(
            "When I run out of allowance, switch me to Opus.",
            "A session stopped on a provider allowance notice.",
            new[] { "limit" }, GoodCalls(), scope: null!, 300, 5, Now, AnActor.Owner));

        Assert.NotEqual("", ex.Reason);
        Assert.Empty(store.All());
    }

    [Fact]
    public void Every_session_is_still_a_scope_a_rule_can_have_when_it_is_chosen_on_purpose()
    {
        var store = new SessionRuleStore(Db);
        var rule = store.Create(
            "When I run out of allowance, switch me to Opus.",
            "A session stopped on a provider allowance notice.",
            new[] { "limit" }, GoodCalls(), RuleScope.AllSessions, 300, 5, Now, AnActor.Owner);

        Assert.Equal(RuleScope.AllSessions, store.Get(rule.Id)!.Scope);
    }

    // ---- the routes that still went round it ---------------------------------------------------------

    /// <summary>
    /// BULK SQL DOES NOT PASS THROUGH SaveChanges, SO THE GATE NEVER SAW IT.
    ///
    /// The gate above runs from SaveChanges and reads the change tracker. An ORM bulk update issues its SQL
    /// immediately and tracks nothing, so it could move a rule from dry run to live without ever meeting
    /// the gate - the one transition the whole feature exists to put a person in front of. The independent
    /// inspection of landing B did exactly this and read the rule back as live.
    /// </summary>
    [Fact]
    public void A_bulk_update_cannot_move_a_rule_out_of_dry_run()
    {
        var store = new SessionRuleStore(Db);
        var rule = store.Create(
            "When I run out of allowance, switch me to Opus.",
            "A session stopped on a provider allowance notice.",
            new[] { "limit" },
            GoodCalls(),
            RuleScope.AllSessions,
            cooldownSeconds: 300,
            dailyCap: 5,
            Now, AnActor.Owner);

        using (var ctx = Db.CreateContext())
        {
            Assert.ThrowsAny<Exception>(() =>
                ctx.SessionRules.Where(r => r.Id == rule.Id).ExecuteUpdate(set => set.SetProperty(r => r.State, Live)));
        }

        // The row is where it was. A refusal that half-promoted would be worse than none.
        Assert.Equal(RuleState.DryRun, new SessionRuleStore(Db).Get(rule.Id)!.State);
    }

    [Fact]
    public void A_bulk_delete_cannot_erase_the_record_of_what_a_rule_did()
    {
        // The firings are the product. Deleting them in bulk goes round the store exactly as the update
        // above goes round the gate, and a record that can be quietly removed is not evidence of anything.
        var store = new SessionRuleStore(Db);
        var rule = store.Create(
            "When I run out of allowance, switch me to Opus.",
            "A session stopped on a provider allowance notice.",
            new[] { "limit" },
            GoodCalls(),
            RuleScope.AllSessions,
            cooldownSeconds: 300,
            dailyCap: 5,
            Now, AnActor.Owner);

        store.RecordFiring(
            rule.Id, "sid-1", "screen", "u", RuleDecisions.Decline, "the screen is not what this is about",
            Array.Empty<RulePrimitiveRun>(), "", "declined - nothing was typed.", Grounding, Now);

        using (var ctx = Db.CreateContext())
        {
            Assert.ThrowsAny<Exception>(() =>
                ctx.SessionRuleFirings.Where(f => f.RuleId == rule.Id).ExecuteDelete());
        }

        Assert.Single(new SessionRuleStore(Db).FiringsFor(rule.Id));
    }

    /// <summary>
    /// A FIRING WRITTEN STRAIGHT THROUGH THE CONTEXT MEETS THE SAME RULES AS ONE WRITTEN THROUGH THE STORE.
    ///
    /// The gate read rule writes only. The firing table - the record, which IS the product - was not
    /// guarded at all, so an invented row saying nothing could be added directly and read back later as
    /// evidence of something that never happened.
    /// </summary>
    [Fact]
    public void A_firing_written_straight_through_the_context_cannot_be_a_record_of_nothing()
    {
        var store = new SessionRuleStore(Db);
        var rule = store.Create(
            "When I run out of allowance, switch me to Opus.",
            "A session stopped on a provider allowance notice.",
            new[] { "limit" },
            GoodCalls(),
            RuleScope.AllSessions,
            cooldownSeconds: 300,
            dailyCap: 5,
            Now, AnActor.Owner);

        using var ctx = Db.CreateContext();
        ctx.SessionRuleFirings.Add(new SessionRuleFiringEntity
        {
            TenantId = TenantId.Local.Value,
            RuleId = rule.Id,
            SessionId = "",
            OccurredUtc = Now,
            Decision = "",
            Reason = "",
            Outcome = "",
            Grounding = "",
        });

        Assert.Throws<RuleRejectedException>(() => ctx.SaveChanges());
    }

    [Fact]
    public void A_firing_written_straight_through_the_context_cannot_name_a_check_that_does_not_exist()
    {
        var store = new SessionRuleStore(Db);
        var rule = store.Create(
            "When I run out of allowance, switch me to Opus.",
            "A session stopped on a provider allowance notice.",
            new[] { "limit" },
            GoodCalls(),
            RuleScope.AllSessions,
            cooldownSeconds: 300,
            dailyCap: 5,
            Now, AnActor.Owner);

        using var ctx = Db.CreateContext();
        ctx.SessionRuleFirings.Add(new SessionRuleFiringEntity
        {
            TenantId = TenantId.Local.Value,
            RuleId = rule.Id,
            SessionId = "sid-1",
            OccurredUtc = Now,
            Decision = RuleDecisions.Decline,
            Reason = "it does not apply",
            Outcome = "declined - nothing was typed.",
            Grounding = Grounding,
            PrimitiveRuns = new List<RulePrimitiveRunEntity>
            {
                new() { Name = "run_shell", Arguments = "command=whoami", Answer = "soren" },
            },
        });

        var ex = Assert.Throws<RuleRejectedException>(() => ctx.SaveChanges());
        Assert.Contains("run_shell", ex.Reason);
    }

    [Fact]
    public void A_dry_run_rules_firing_cannot_claim_to_have_typed_however_it_is_written()
    {
        var store = new SessionRuleStore(Db);
        var rule = store.Create(
            "When I run out of allowance, switch me to Opus.",
            "A session stopped on a provider allowance notice.",
            new[] { "limit" },
            GoodCalls(),
            RuleScope.AllSessions,
            cooldownSeconds: 300,
            dailyCap: 5,
            Now, AnActor.Owner);

        using var ctx = Db.CreateContext();
        ctx.SessionRuleFirings.Add(new SessionRuleFiringEntity
        {
            TenantId = TenantId.Local.Value,
            RuleId = rule.Id,
            SessionId = "sid-1",
            OccurredUtc = Now,
            Decision = RuleDecisions.Act,
            Reason = "the screen says 'usage limit'",
            Outcome = "typed into the session: /model opus",
            Grounding = Grounding,
            TypedText = "/model opus",
        });

        Assert.Throws<RuleRejectedException>(() => ctx.SaveChanges());
    }

    [Fact]
    public void A_well_formed_firing_written_straight_through_the_context_is_accepted()
    {
        // THE PRESENCE. Every refusal above would pass just as happily on a gate that refused every firing,
        // which would make the record impossible to write at all.
        var store = new SessionRuleStore(Db);
        var rule = store.Create(
            "When I run out of allowance, switch me to Opus.",
            "A session stopped on a provider allowance notice.",
            new[] { "limit" },
            GoodCalls(),
            RuleScope.AllSessions,
            cooldownSeconds: 300,
            dailyCap: 5,
            Now, AnActor.Owner);

        using (var ctx = Db.CreateContext())
        {
            ctx.SessionRuleFirings.Add(new SessionRuleFiringEntity
            {
                TenantId = TenantId.Local.Value,
                RuleId = rule.Id,
                SessionId = "sid-1",
                OccurredUtc = Now,
                Decision = RuleDecisions.Decline,
                Reason = "the screen is only talking about a limit",
                Outcome = "declined - nothing was typed.",
                Grounding = Grounding,
            });
            ctx.SaveChanges();
        }

        Assert.Single(new SessionRuleStore(Db).FiringsFor(rule.Id));
    }

    // ---- the parts of a rule the gate did not look at ------------------------------------------------

    /// <summary>
    /// THE GATE CHECKED FOUR THINGS AND THE RECORD HAS MORE THAN FOUR PARTS. It looked at the tenant, the
    /// call document, the initial dry-run state and the promotion marker - so a rule with no instruction,
    /// no screen description, no trigger words, no cooldown and no daily cap passed straight through it,
    /// while the store refused every one of those. Two boundaries disagreeing about what a rule is means
    /// one of them is not a boundary.
    /// </summary>
    [Theory]
    [InlineData("instruction")]
    [InlineData("screen description")]
    [InlineData("trigger words")]
    [InlineData("cooldown")]
    [InlineData("daily cap")]
    public void A_rule_written_straight_through_the_context_still_has_to_be_a_rule(string missing)
    {
        var entity = Straight(TenantId.Local.Value, DryRun, GoodCalls());
        switch (missing)
        {
            case "instruction": entity.Instruction = "  "; break;
            case "screen description": entity.ScreenDescription = ""; break;
            case "trigger words": entity.TriggerWords = new List<string>(); break;
            case "cooldown": entity.CooldownSeconds = 0; break;
            case "daily cap": entity.DailyCap = 0; break;
        }

        using var ctx = Db.CreateContext();
        ctx.SessionRules.Add(entity);

        var ex = Assert.Throws<RuleRejectedException>(() => ctx.SaveChanges());
        Assert.NotEqual("", ex.Reason);
    }
}
