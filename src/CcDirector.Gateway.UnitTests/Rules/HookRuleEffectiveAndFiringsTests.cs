using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Rules;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// The two store halves of the phase 1 wire contract. W1: the hook rules that reach one machine and one
/// repository, each with its rank. W5: decisions the hook made, forwarded by the Director, each becoming a
/// firing on its deciding rule - once, however many times it is posted.
/// </summary>
public sealed class HookRuleEffectiveAndFiringsTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase Db => _db ??= _h.Open();
    private GatewayDatabase? _db;

    public void Dispose() => _h.Dispose();

    private static readonly DateTime Now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
    private const string RepoA = "github.com/thefrederiksen/devthrottle";
    private const string RepoB = "github.com/thefrederiksen/devthrottle_internal";

    private static SessionRule Hook(SessionRuleStore store, string sentence, RuleAppliesTo appliesTo, string result = HookRuleWords.Deny) =>
        store.CreateHook(sentence, appliesTo, HookRuleWords.BeforeCommand, HookRuleWords.Shell, HookRuleWords.Match,
            new[] { "echo RULE-PROBE" }, result, Now);

    private static SessionRule Screen(SessionRuleStore store) => store.Create(
        "When I run out of allowance, switch me to Opus.",
        "A session stopped on a provider allowance notice.",
        new[] { "echo RULE-PROBE" },
        new[]
        {
            RulePrimitiveCall.To("matches_any",
                RuleArgument.FromInput("text", RuleInput.ScreenText),
                RuleArgument.LiteralList("terms", new[] { "usage limit" })),
        },
        RuleScope.AllSessions, 300, 5, Now);

    // ---- W1: effective rules ---------------------------------------------------------------------------

    [Fact]
    public void EffectiveHookRules_MachineAndRepo_ReturnsOnlyTheHookRulesThatReachThemWithTheirRanks()
    {
        var store = new SessionRuleStore(Db);
        var everywhere = Hook(store, "everywhere", RuleAppliesTo.Everywhere);
        var onDevlinux = Hook(store, "on devlinux", RuleAppliesTo.OnMachines(new[] { "devlinux" }));
        var notOnDevlinux = Hook(store, "not on devlinux", RuleAppliesTo.ExceptOnMachines(new[] { "devlinux" }));
        var inA = Hook(store, "in A", RuleAppliesTo.InRepo(RepoA));
        var inB = Hook(store, "in B", RuleAppliesTo.InRepo(RepoB));
        var inAOnDevlinux = Hook(store, "in A on devlinux", RuleAppliesTo.InRepoOnMachines(RepoA, new[] { "devlinux" }));
        var inAOnLaptop = Hook(store, "in A on laptop", RuleAppliesTo.InRepoOnMachines(RepoA, new[] { "laptop" }));
        var screen = Screen(store);

        var onA = store.EffectiveHookRules("DevLinux", "https://github.com/TheFrederiksen/devthrottle.git");
        Assert.Equal(
            new[] { (inAOnDevlinux.Id, 4), (inA.Id, 3), (onDevlinux.Id, 2), (everywhere.Id, 1) }.OrderBy(x => x.Item1),
            onA.Select(e => (e.Rule.Id, e.Rank)).OrderBy(x => x.Item1));

        // The negative controls, as presences: the other repository and the other machine each get their own set.
        var onLaptopNoRepo = store.EffectiveHookRules("laptop", "");
        Assert.Equal(
            new[] { everywhere.Id, notOnDevlinux.Id }.OrderBy(x => x),
            onLaptopNoRepo.Select(e => e.Rule.Id).OrderBy(x => x));

        var onLaptopInA = store.EffectiveHookRules("laptop", RepoA);
        Assert.Contains(onLaptopInA, e => e.Rule.Id == inAOnLaptop.Id);
        Assert.DoesNotContain(onLaptopInA, e => e.Rule.Id == inAOnDevlinux.Id);
        Assert.DoesNotContain(onLaptopInA, e => e.Rule.Id == inB.Id);

        // A screen rule is never an effective hook rule, wherever it is asked about.
        Assert.DoesNotContain(onA.Concat(onLaptopNoRepo).Concat(onLaptopInA), e => e.Rule.Id == screen.Id);
    }

    [Fact]
    public void EffectiveHookRules_NoMachine_IsRefused()
    {
        var ex = Assert.Throws<RuleRejectedException>(() => new SessionRuleStore(Db).EffectiveHookRules(" ", RepoA));
        Assert.Contains("machine", ex.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ---- W5: firings -----------------------------------------------------------------------------------

    private static HookDecision Decision(Guid decidingRuleId, string decisionId = "20261018T1200001234567Z-1a2b3c4d",
        string decision = HookRuleWords.WouldDeny, string subject = "echo RULE-PROBE", IReadOnlyList<Guid>? overridden = null) =>
        new(
            Version: 1,
            DecisionId: decisionId,
            SessionId: "8a1e0000-0000-0000-0000-000000000001",
            OccurredUtc: new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc).AddTicks(1234567),
            Agent: "claude_code",
            Event: HookRuleWords.BeforeCommand,
            AgentEvent: "PreToolUse",
            Tool: HookRuleWords.Shell,
            AgentTool: "Bash",
            Subject: subject,
            Machine: "devlinux",
            Repo: RepoA,
            Decision: decision,
            DecidingRuleId: decidingRuleId,
            OverriddenRuleIds: overridden ?? Array.Empty<Guid>(),
            MatchedRuleIds: new[] { decidingRuleId },
            EvaluationMicroseconds: 812);

    [Fact]
    public void RecordHookFirings_AcceptedDecision_BecomesAFiringOnItsDecidingRuleCarryingTheWholeDecision()
    {
        var store = new SessionRuleStore(Db);
        var global = Hook(store, "Never echo the probe anywhere.", RuleAppliesTo.Everywhere, HookRuleWords.Allow);
        var rule = Hook(store, "Never echo the probe in this repository.", RuleAppliesTo.InRepo(RepoA));

        var answer = store.RecordHookFirings(new[] { Decision(rule.Id, overridden: new[] { global.Id }) }, Now);

        Assert.Equal(1, answer.Accepted);
        Assert.Equal(0, answer.Duplicates);
        Assert.Empty(answer.Refused);

        var firing = Assert.Single(new SessionRuleStore(_h.Open()).FiringsFor(rule.Id));
        Assert.Equal(RuleKinds.Hook, firing.Kind);
        Assert.Equal("20261018T1200001234567Z-1a2b3c4d", firing.DecisionId);
        Assert.Equal("8a1e0000-0000-0000-0000-000000000001", firing.SessionId);
        Assert.Equal(HookRuleWords.WouldDeny, firing.Decision);
        Assert.Equal(HookRuleWords.BeforeCommand, firing.Event);
        Assert.Equal(HookRuleWords.Shell, firing.Tool);
        Assert.Equal("echo RULE-PROBE", firing.Subject);
        Assert.Equal("devlinux", firing.Machine);
        Assert.Equal(RepoA, firing.Repo);
        Assert.Equal("claude_code", firing.Agent);
        Assert.Equal("PreToolUse", firing.AgentEvent);
        Assert.Equal("Bash", firing.AgentTool);
        Assert.Equal(new[] { global.Id }, firing.OverriddenRuleIds);
        Assert.Equal(new[] { rule.Id }, firing.MatchedRuleIds);
        Assert.Equal(812, firing.EvaluationMicroseconds);
        Assert.Equal(new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc).AddTicks(1234567), firing.OccurredUtc);
        // The reason is the rule's own sentence, and dry run typed nothing.
        Assert.Equal("Never echo the probe in this repository.", firing.Reason);
        Assert.Equal("", firing.TypedText);
        Assert.False(string.IsNullOrWhiteSpace(firing.Outcome));
    }

    [Fact]
    public void RecordHookFirings_SameDecisionPostedAgain_IsADuplicateAndNotASecondFiring()
    {
        var store = new SessionRuleStore(Db);
        var rule = Hook(store, "Never echo the probe.", RuleAppliesTo.Everywhere);

        var first = store.RecordHookFirings(new[] { Decision(rule.Id), Decision(rule.Id) }, Now);
        Assert.Equal(1, first.Accepted);
        Assert.Equal(1, first.Duplicates);

        var retried = store.RecordHookFirings(new[] { Decision(rule.Id) }, Now);
        Assert.Equal(0, retried.Accepted);
        Assert.Equal(1, retried.Duplicates);

        Assert.Single(store.FiringsFor(rule.Id));
    }

    [Fact]
    public void RecordHookFirings_SameDecisionIdInAnotherAccount_IsThatAccountsOwnFiring()
    {
        var a = new SessionRuleStore(_h.Open(new FixedTenantContext(new TenantId("tenant-a"))));
        var b = new SessionRuleStore(_h.Open(new FixedTenantContext(new TenantId("tenant-b"))));
        var ruleA = Hook(a, "Never echo the probe.", RuleAppliesTo.Everywhere);
        var ruleB = Hook(b, "Never echo the probe.", RuleAppliesTo.Everywhere);

        Assert.Equal(1, a.RecordHookFirings(new[] { Decision(ruleA.Id) }, Now).Accepted);
        Assert.Equal(1, b.RecordHookFirings(new[] { Decision(ruleB.Id) }, Now).Accepted);
    }

    [Fact]
    public void RecordHookFirings_DecisionNamingNoRuleOrAScreenRule_IsRefusedWithAReasonWhileTheRestAreAccepted()
    {
        var store = new SessionRuleStore(Db);
        var hook = Hook(store, "Never echo the probe.", RuleAppliesTo.Everywhere);
        var screen = Screen(store);
        var nobody = Guid.NewGuid();

        var answer = store.RecordHookFirings(new[]
        {
            Decision(nobody, decisionId: "d-nobody"),
            Decision(screen.Id, decisionId: "d-screen"),
            Decision(hook.Id, decisionId: "d-good"),
        }, Now);

        Assert.Equal(1, answer.Accepted);
        Assert.Equal(2, answer.Refused.Count);
        Assert.Contains(nobody.ToString(), answer.Refused.Single(r => r.DecisionId == "d-nobody").Reason, StringComparison.Ordinal);
        Assert.Contains("hook", answer.Refused.Single(r => r.DecisionId == "d-screen").Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(store.FiringsFor(screen.Id));
        Assert.Single(store.FiringsFor(hook.Id));
    }

    [Fact]
    public void RecordHookFirings_DecisionThatIsNotARecordOfAnything_IsRefused()
    {
        var store = new SessionRuleStore(Db);
        var rule = Hook(store, "Never echo the probe.", RuleAppliesTo.Everywhere);

        var bad = new[]
        {
            Decision(rule.Id, decisionId: "d1", decision: "denied"),
            Decision(rule.Id, decisionId: "d2", subject: "  "),
            Decision(rule.Id, decisionId: "d3", subject: new string('x', 4001)),
            Decision(rule.Id, decisionId: " "),
            Decision(rule.Id, decisionId: "d5") with { Event = "after_edit" },
            Decision(rule.Id, decisionId: "d6") with { Tool = "edit" },
            Decision(rule.Id, decisionId: "d7") with { SessionId = "" },
            Decision(rule.Id, decisionId: "d8") with { Machine = "" },
            Decision(rule.Id, decisionId: "d9") with { Version = 2 },
            Decision(rule.Id, decisionId: new string('d', 65)),
        };

        var answer = store.RecordHookFirings(bad, Now);

        Assert.Equal(0, answer.Accepted);
        Assert.Equal(bad.Length, answer.Refused.Count);
        Assert.All(answer.Refused, r => Assert.False(string.IsNullOrWhiteSpace(r.Reason)));
        Assert.Empty(store.FiringsFor(rule.Id));
    }

    [Fact]
    public void RecordFiring_ScreenFiringAgainstAHookRule_IsRefused()
    {
        var store = new SessionRuleStore(Db);
        var rule = Hook(store, "Never echo the probe.", RuleAppliesTo.Everywhere);

        var ex = Assert.Throws<RuleRejectedException>(() => store.RecordFiring(
            rule.Id, "s1", "screen", "understood", RuleDecisions.Decline, "nothing to do",
            Array.Empty<RulePrimitiveRun>(), "", "nothing happened", "grounding: nothing quoted.", Now));
        Assert.Contains("hook", ex.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
