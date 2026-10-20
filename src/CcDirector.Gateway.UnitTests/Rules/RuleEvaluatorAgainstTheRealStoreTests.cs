using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Rules;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// THE EVALUATOR AGAINST THE REAL STORE, over the real migrated schema.
///
/// Every other evaluator test holds a fake store, which is right for what those tests ask: they are about
/// what the evaluator DOES. This file asks the question a fake cannot answer - what happens when the thing
/// that has to write the record REFUSES - and the answer used to be "the keystroke has already gone".
///
/// The failure is not hypothetical. A rule can be deleted while the model is being asked; that gap is the
/// longest in the pass. The store then refuses to record a firing against a rule that no longer exists,
/// and before this round that refusal arrived AFTER text had been typed into somebody's session, leaving
/// the action with nothing durable to account for it.
///
/// The send seam is instrumented, so "nothing was typed" here is a counted fact about the one method that
/// can type rather than the absence of a log line.
/// </summary>
public sealed class RuleEvaluatorAgainstTheRealStoreTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static readonly DateTime Now = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId Tenant = TenantId.Local;
    private const string DirectorId = "director-1";
    private const string SessionId = "sid-1";

    private const string TheNotice =
        "You've reached your Fable 5 limit. Run /usage-credits to continue or switch models with /model.";

    private static readonly string[] Screen =
    {
        "C:\\scratch>echo " + TheNotice,
        TheNotice,
        "C:\\scratch>",
    };

    /// <summary>The evaluator's environment over a REAL store, with the send seam instrumented and one
    /// hook: something the test does at the moment the model is asked.</summary>
    private sealed class StoreBackedEnvironment : IRuleEnvironment
    {
        private readonly SessionRuleStore _store;

        public StoreBackedEnvironment(SessionRuleStore store) => _store = store;

        public string? AgentReply { get; set; }
        public Action? WhileTheModelIsBeingAsked { get; set; }
        public List<string> Typed { get; } = new();
        public DateTime NowUtc => Now;

        public IReadOnlyList<SessionRule> Rules(TenantId tenant) => _store.All();

        public IReadOnlyList<SessionRuleFiring> FiringsFor(TenantId tenant, Guid ruleId) =>
            _store.FiringsFor(ruleId);

        public RuleSessionFacts? ReadSessionFacts(TenantId tenant, string sessionId) =>
            new(sessionId, "RawCli", @"D:\ReposFred\scratch", "SOREN_NORTH", "Session Rules", "WaitingForInput");

        public Task<IReadOnlyList<string>?> ReadScreenRowsAsync(
            TenantId tenant, string directorId, string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>?>(Screen);

        public Task<string?> AskAgentAsync(TenantId tenant, string prompt, CancellationToken ct)
        {
            WhileTheModelIsBeingAsked?.Invoke();
            return Task.FromResult(AgentReply);
        }

        public Task<RuleSendResult> TypeIntoSessionAsync(
            TenantId tenant, string directorId, string sessionId, string text, CancellationToken ct)
        {
            Typed.Add(text);
            return Task.FromResult(RuleSendResult.Confirmed());
        }

        public Guid RecordFiring(TenantId tenant, RuleFiringDraft draft) => _store.RecordFiring(
            draft.RuleId, draft.SessionId, draft.ScreenText, draft.Understanding, draft.Decision,
            draft.Reason, draft.Runs, draft.TypedText, draft.Outcome, draft.Grounding, Now).Id;

        public void CompleteFiring(TenantId tenant, Guid firingId, string typedText, string outcome) =>
            _store.CompleteFiring(firingId, typedText, outcome, Now);
    }

    private SessionRule ALiveRule(SessionRuleStore store)
    {
        var rule = store.Create(
            "If a session's screen says it has run out of its model allowance, type the command that shows " +
            "me what is left.",
            "A session stopped on a provider allowance notice.",
            new[] { "reached your", "limit" },
            Array.Empty<RulePrimitiveCall>(),
            RuleScope.AllSessions,
            cooldownSeconds: 300,
            dailyCap: 5,
            Now, AnActor.Owner);

        return store.Promote(
            rule.Id,
            RulePromotionGrant.FromAuthenticatedRequest(
                rule.Id, AnInboundRequest.FromDevice(),
                "I have read this rule's dry-run record and I am making it live.", Now),
            Now, AnActor.Owner);
    }

    private static string ActReply(Guid ruleId) => $$"""
        {
          "rule_id": "{{ruleId}}",
          "understanding": "The session is blocked on its model allowance.",
          "decision": "act",
          "reason": "The screen says 'reached your Fable 5 limit', which is the session's own state.",
          "checks": [],
          "type": "/usage-credits"
        }
        """;

    [Fact]
    public async Task A_rule_deleted_while_the_model_was_being_asked_types_nothing()
    {
        var store = new SessionRuleStore(_h.Open());
        var rule = ALiveRule(store);

        var env = new StoreBackedEnvironment(store) { AgentReply = ActReply(rule.Id) };
        // The gap the failure lives in: the rule goes away while the model is being asked, which is the
        // longest wait in the whole pass.
        env.WhileTheModelIsBeingAsked = () => store.Delete(rule.Id, Now, AnActor.Owner);

        var pass = await new RuleEvaluator(env).EvaluateAsync(Tenant, DirectorId, SessionId, CancellationToken.None);

        Assert.Equal(RulePassOutcomes.NotRecorded, pass.What);
        Assert.Empty(env.Typed);
        Assert.Empty(store.FiringsFor(rule.Id));
    }

    [Fact]
    public async Task An_act_that_the_real_store_accepts_leaves_one_completed_record_of_what_was_typed()
    {
        // THE PRESENCE. A pass that refused everything would satisfy the test above while proving nothing,
        // so the same wiring is required to carry an act all the way through a real database and leave one
        // row saying what was typed.
        var store = new SessionRuleStore(_h.Open());
        var rule = ALiveRule(store);

        var env = new StoreBackedEnvironment(store) { AgentReply = ActReply(rule.Id) };

        var pass = await new RuleEvaluator(env).EvaluateAsync(Tenant, DirectorId, SessionId, CancellationToken.None);

        Assert.Equal(RulePassOutcomes.Acted, pass.What);
        Assert.Equal("/usage-credits", Assert.Single(env.Typed));

        // Read back through a SECOND store over the same database - a real read, not the object just handed
        // out - because the whole point is that the record is DURABLE before the keystroke.
        var reopened = new SessionRuleStore(_h.Open());
        var firing = Assert.Single(reopened.FiringsFor(rule.Id));
        Assert.Equal(RuleDecisions.Act, firing.Decision);
        Assert.Equal("/usage-credits", firing.TypedText);
        Assert.Contains("/usage-credits", firing.Outcome);
    }
}
