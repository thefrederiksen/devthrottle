using System.Text.Json;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Rules;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// What crosses the wire for hook rules (Session Rules part 2, phase 1): the create body, the W1 effective
/// rules answer, the W5 firings body and its answer, and the rule and firing read shapes. The routes are a
/// thin binding of HTTP to these methods, so this is what a caller really sends and receives.
///
/// The September screen-rule create body is run through unchanged, because "existing bodies keep working" is
/// a promise and this is where it is kept.
/// </summary>
public sealed class HookRuleWireTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase Db => _db ??= _h.Open();
    private GatewayDatabase? _db;

    public void Dispose() => _h.Dispose();

    private static readonly DateTime Now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
    private const string Repo = "github.com/thefrederiksen/devthrottle";

    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement;

    private static JsonElement AsWire(object projected) =>
        JsonDocument.Parse(JsonSerializer.Serialize(projected)).RootElement;

    private const string SeptemberScreenBody = """
        {
          "instruction": "When I run out of allowance, switch me to Opus.",
          "screenDescription": "A session stopped on a provider allowance notice.",
          "triggerWords": ["limit"],
          "checks": [ { "name": "matches_any", "arguments": { "text": "<screen_text>", "terms": ["usage limit"] } } ],
          "scope": "all-sessions",
          "cooldownSeconds": 300,
          "dailyCap": 5
        }
        """;

    private const string HookBody = """
        {
          "kind": "hook",
          "instruction": "Never echo the rule probe in this repository.",
          "appliesTo": { "kind": "repo", "repo": "git@github.com:TheFrederiksen/devthrottle.git" },
          "event": "before_command",
          "tool": "shell",
          "handler": "match",
          "match": ["echo RULE-PROBE"],
          "result": "deny"
        }
        """;

    // ---- the create body -------------------------------------------------------------------------------

    [Fact]
    public void CreateFromBody_SeptemberScreenBodyUnchanged_StillCreatesAScreenRuleThatAppliesEverywhere()
    {
        var rule = SessionRuleWire.CreateFromBody(new SessionRuleStore(Db), Body(SeptemberScreenBody), Now);

        Assert.Equal(RuleKinds.Screen, rule.Kind);
        Assert.Equal(RuleAppliesToKinds.Everywhere, rule.AppliesTo.Kind);
        Assert.Equal(RuleState.DryRun, rule.State);
        Assert.Equal(new[] { "limit" }, rule.TriggerWords);
    }

    [Fact]
    public void CreateFromBody_HookBody_CreatesADryRunHookRuleWithItsRepositoryNormalised()
    {
        var rule = SessionRuleWire.CreateFromBody(new SessionRuleStore(Db), Body(HookBody), Now);

        Assert.Equal(RuleKinds.Hook, rule.Kind);
        Assert.Equal(RuleState.DryRun, rule.State);
        Assert.Equal(RuleAppliesToKinds.Repo, rule.AppliesTo.Kind);
        Assert.Equal(Repo, rule.AppliesTo.Repo);
        Assert.Equal(new[] { "echo RULE-PROBE" }, rule.Hook!.Match);
        Assert.Equal(HookRuleWords.Deny, rule.Hook.Result);
    }

    [Fact]
    public void CreateFromBody_KindsNotBuiltOrNotKnown_AreRefusedWithAReason()
    {
        var store = new SessionRuleStore(Db);

        var sessionStart = Assert.Throws<RuleRejectedException>(() => SessionRuleWire.CreateFromBody(store,
            Body("""{ "kind": "session_start", "instruction": "Tell me when the licence is missing." }"""), Now));
        Assert.Contains("phase 5", sessionStart.Reason, StringComparison.Ordinal);

        var unknown = Assert.Throws<RuleRejectedException>(() => SessionRuleWire.CreateFromBody(store,
            Body("""{ "kind": "telepathy", "instruction": "x" }"""), Now));
        Assert.Contains("telepathy", unknown.Reason, StringComparison.Ordinal);

        var notText = Assert.Throws<RuleRejectedException>(() => SessionRuleWire.CreateFromBody(store,
            Body("""{ "kind": 3, "instruction": "x" }"""), Now));
        Assert.Contains("kind", notText.Reason, StringComparison.Ordinal);

        Assert.Empty(store.All());
    }

    [Fact]
    public void CreateFromBody_HookBodyWithScreenOnlyFields_IsRefused()
    {
        var store = new SessionRuleStore(Db);
        var withChecks = HookBody.Replace("\"result\": \"deny\"",
            "\"result\": \"deny\", \"checks\": [ { \"name\": \"matches_any\", \"arguments\": { \"text\": \"<screen_text>\", \"terms\": [\"x\"] } } ]");
        var ex = Assert.Throws<RuleRejectedException>(() => SessionRuleWire.CreateFromBody(store, Body(withChecks), Now));
        Assert.Contains("phase 4", ex.Reason, StringComparison.Ordinal);

        var withScope = HookBody.Replace("\"result\": \"deny\"", "\"result\": \"deny\", \"scope\": { \"agent\": \"claude_code\" }");
        var scoped = Assert.Throws<RuleRejectedException>(() => SessionRuleWire.CreateFromBody(store, Body(withScope), Now));
        Assert.Contains("scope", scoped.Reason, StringComparison.OrdinalIgnoreCase);

        var withWords = HookBody.Replace("\"result\": \"deny\"", "\"result\": \"deny\", \"triggerWords\": [\"limit\"]");
        Assert.Throws<RuleRejectedException>(() => SessionRuleWire.CreateFromBody(store, Body(withWords), Now));

        // A bound the caller wrote is refused, never silently dropped: they believed the rule would carry it.
        foreach (var bound in new[] { "cooldownSeconds", "dailyCap" })
        {
            var withBound = HookBody.Replace("\"result\": \"deny\"", $"\"result\": \"deny\", \"{bound}\": 1");
            var refused = Assert.Throws<RuleRejectedException>(() => SessionRuleWire.CreateFromBody(store, Body(withBound), Now));
            Assert.Contains(bound, refused.Reason, StringComparison.Ordinal);
        }

        Assert.Empty(store.All());
    }

    [Fact]
    public void CreateFromBody_AppliesToThatIsNotAnObject_IsRefused()
    {
        var store = new SessionRuleStore(Db);
        var asText = HookBody.Replace(
            "{ \"kind\": \"repo\", \"repo\": \"git@github.com:TheFrederiksen/devthrottle.git\" }", "\"everywhere\"");
        var ex = Assert.Throws<RuleRejectedException>(() => SessionRuleWire.CreateFromBody(store, Body(asText), Now));
        Assert.Contains("appliesTo", ex.Reason, StringComparison.Ordinal);
    }

    // ---- the read shapes -------------------------------------------------------------------------------

    [Fact]
    public void Project_HookRule_CarriesKindAppliesToAndTheTrigger()
    {
        var rule = SessionRuleWire.CreateFromBody(new SessionRuleStore(Db), Body(HookBody), Now);
        var wire = AsWire(SessionRuleWire.Project(rule));

        Assert.Equal("hook", wire.GetProperty("kind").GetString());
        var appliesTo = wire.GetProperty("appliesTo");
        Assert.Equal("repo", appliesTo.GetProperty("kind").GetString());
        Assert.Equal(Repo, appliesTo.GetProperty("repo").GetString());
        Assert.Equal(0, appliesTo.GetProperty("machines").GetArrayLength());
        Assert.Equal(0, appliesTo.GetProperty("exceptMachines").GetArrayLength());
        Assert.Equal("before_command", wire.GetProperty("event").GetString());
        Assert.Equal("shell", wire.GetProperty("tool").GetString());
        Assert.Equal("match", wire.GetProperty("handler").GetString());
        Assert.Equal("echo RULE-PROBE", wire.GetProperty("match")[0].GetString());
        Assert.Equal("deny", wire.GetProperty("result").GetString());
    }

    [Fact]
    public void Project_ScreenRule_SaysScreenAndEverywhere()
    {
        var rule = SessionRuleWire.CreateFromBody(new SessionRuleStore(Db), Body(SeptemberScreenBody), Now);
        var wire = AsWire(SessionRuleWire.Project(rule));

        Assert.Equal("screen", wire.GetProperty("kind").GetString());
        Assert.Equal("everywhere", wire.GetProperty("appliesTo").GetProperty("kind").GetString());
        Assert.Equal("limit", wire.GetProperty("triggerWords")[0].GetString());
    }

    // ---- W1 --------------------------------------------------------------------------------------------

    [Fact]
    public void ReadEffectiveQuery_HookMachineAndRepo_ReturnsTheMachineAndTheNormalisedRepo()
    {
        var (machine, repo) = SessionRuleWire.ReadEffectiveQuery("hook", " devlinux ", "https://github.com/TheFrederiksen/devthrottle.git");
        Assert.Equal("devlinux", machine);
        Assert.Equal(Repo, repo);

        var (_, none) = SessionRuleWire.ReadEffectiveQuery("hook", "devlinux", null);
        Assert.Equal("", none);
    }

    [Fact]
    public void ReadEffectiveQuery_KindNotHookOrNoMachine_IsRefused()
    {
        Assert.Contains("hook", Assert.Throws<RuleRejectedException>(
            () => SessionRuleWire.ReadEffectiveQuery("screen", "devlinux", Repo)).Reason, StringComparison.Ordinal);
        Assert.Contains("kind", Assert.Throws<RuleRejectedException>(
            () => SessionRuleWire.ReadEffectiveQuery(null, "devlinux", Repo)).Reason, StringComparison.Ordinal);
        Assert.Contains("machine", Assert.Throws<RuleRejectedException>(
            () => SessionRuleWire.ReadEffectiveQuery("hook", "", Repo)).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectEffective_HookRule_IsTheW1AndW2ItemShape()
    {
        var store = new SessionRuleStore(Db);
        var rule = SessionRuleWire.CreateFromBody(store, Body(HookBody), Now);

        var wire = AsWire(SessionRuleWire.ProjectEffective("devlinux", Repo, store.EffectiveHookRules("devlinux", Repo)));

        Assert.Equal("devlinux", wire.GetProperty("machine").GetString());
        Assert.Equal(Repo, wire.GetProperty("repo").GetString());
        var item = Assert.Single(wire.GetProperty("rules").EnumerateArray().ToList());
        Assert.Equal(
            new[] { "appliesTo", "event", "id", "match", "rank", "reason", "result", "state", "tool" },
            item.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(rule.Id, item.GetProperty("id").GetGuid());
        Assert.Equal(3, item.GetProperty("rank").GetInt32());
        Assert.Equal("repo", item.GetProperty("appliesTo").GetString());
        Assert.Equal("before_command", item.GetProperty("event").GetString());
        Assert.Equal("shell", item.GetProperty("tool").GetString());
        Assert.Equal("echo RULE-PROBE", item.GetProperty("match")[0].GetString());
        Assert.Equal("deny", item.GetProperty("result").GetString());
        Assert.Equal("Never echo the rule probe in this repository.", item.GetProperty("reason").GetString());
        Assert.Equal("dry_run", item.GetProperty("state").GetString());
    }

    // ---- W5 --------------------------------------------------------------------------------------------

    private static string W3(string decisionId, string decidingRuleId = "3f2c0000-0000-0000-0000-000000000001") => $$"""
        {
          "version": 1,
          "decisionId": "{{decisionId}}",
          "sessionId": "8a1e0000-0000-0000-0000-000000000001",
          "occurredUtc": "2026-10-18T12:00:00.1234567Z",
          "agent": "claude_code",
          "event": "before_command",
          "agentEvent": "PreToolUse",
          "tool": "shell",
          "agentTool": "Bash",
          "subject": "echo RULE-PROBE",
          "machine": "devlinux",
          "repo": "github.com/thefrederiksen/devthrottle",
          "decision": "would_deny",
          "decidingRuleId": "{{decidingRuleId}}",
          "overriddenRuleIds": ["3f2c0000-0000-0000-0000-000000000002"],
          "matchedRuleIds": ["{{decidingRuleId}}", "3f2c0000-0000-0000-0000-000000000002"],
          "evaluationMicroseconds": 812
        }
        """;

    [Fact]
    public void ReadHookDecisions_W3Decision_ReadsEveryField()
    {
        var unreadable = new List<HookFiringRefusal>();
        var read = SessionRuleWire.ReadHookDecisions(
            Body($$"""{ "decisions": [ {{W3("20261018T1200001234567Z-1a2b3c4d")}} ] }"""), unreadable);

        Assert.Empty(unreadable);
        var d = Assert.Single(read);
        Assert.Equal(1, d.Version);
        Assert.Equal("20261018T1200001234567Z-1a2b3c4d", d.DecisionId);
        Assert.Equal("8a1e0000-0000-0000-0000-000000000001", d.SessionId);
        Assert.Equal(new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc).AddTicks(1234567), d.OccurredUtc);
        Assert.Equal(DateTimeKind.Utc, d.OccurredUtc.Kind);
        Assert.Equal("claude_code", d.Agent);
        Assert.Equal("before_command", d.Event);
        Assert.Equal("PreToolUse", d.AgentEvent);
        Assert.Equal("shell", d.Tool);
        Assert.Equal("Bash", d.AgentTool);
        Assert.Equal("echo RULE-PROBE", d.Subject);
        Assert.Equal("devlinux", d.Machine);
        Assert.Equal(Repo, d.Repo);
        Assert.Equal("would_deny", d.Decision);
        Assert.Equal(Guid.Parse("3f2c0000-0000-0000-0000-000000000001"), d.DecidingRuleId);
        Assert.Equal(new[] { Guid.Parse("3f2c0000-0000-0000-0000-000000000002") }, d.OverriddenRuleIds);
        Assert.Equal(2, d.MatchedRuleIds.Count);
        Assert.Equal(812, d.EvaluationMicroseconds);
    }

    [Fact]
    public void ReadHookDecisions_UnreadableItems_AreRefusedByIdWhileTheReadableOnesAreRead()
    {
        var unreadable = new List<HookFiringRefusal>();
        var read = SessionRuleWire.ReadHookDecisions(Body($$"""
            { "decisions": [
                {{W3("good")}},
                {{W3("bad-rule", decidingRuleId: "not-a-guid")}},
                {{W3("bad-time").Replace("2026-10-18T12:00:00.1234567Z", "yesterday")}},
                "not an object"
            ] }
            """), unreadable);

        Assert.Equal("good", Assert.Single(read).DecisionId);
        Assert.Equal(3, unreadable.Count);
        Assert.Contains(unreadable, r => r.DecisionId == "bad-rule" && r.Reason.Contains("decidingRuleId", StringComparison.Ordinal));
        Assert.Contains(unreadable, r => r.DecisionId == "bad-time" && r.Reason.Contains("occurredUtc", StringComparison.Ordinal));
        Assert.Contains(unreadable, r => r.DecisionId == "");
    }

    [Fact]
    public void ReadHookDecisions_MoreThanOneHundredInOnePost_ReadsTheFirstHundredAndRefusesTheRestById()
    {
        var items = string.Join(",", Enumerable.Range(0, 102).Select(i => W3($"d-{i}")));
        var unreadable = new List<HookFiringRefusal>();

        var read = SessionRuleWire.ReadHookDecisions(Body($$"""{ "decisions": [ {{items}} ] }"""), unreadable);

        Assert.Equal(HookRuleWords.MaxDecisionsPerPost, read.Count);
        Assert.Equal("d-99", read.Last().DecisionId);
        Assert.Equal(new[] { "d-100", "d-101" }, unreadable.Select(r => r.DecisionId));
        Assert.All(unreadable, r => Assert.Contains("100", r.Reason, StringComparison.Ordinal));
    }

    [Fact]
    public void ReadHookDecisions_NoDecisionsList_IsRefused()
    {
        var ex = Assert.Throws<RuleRejectedException>(
            () => SessionRuleWire.ReadHookDecisions(Body("""{ "decision": [] }"""), new List<HookFiringRefusal>()));
        Assert.Contains("decisions", ex.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectHookFiringsAnswer_Answer_IsTheW5AnswerShape()
    {
        var wire = AsWire(SessionRuleWire.ProjectHookFiringsAnswer(
            new HookFiringsAnswer(2, 1, new[] { new HookFiringRefusal("d-1", "there is no rule with that id.") })));

        Assert.Equal(2, wire.GetProperty("accepted").GetInt32());
        Assert.Equal(1, wire.GetProperty("duplicates").GetInt32());
        var refused = Assert.Single(wire.GetProperty("refused").EnumerateArray().ToList());
        Assert.Equal("d-1", refused.GetProperty("decisionId").GetString());
        Assert.Equal("there is no rule with that id.", refused.GetProperty("reason").GetString());
    }

    [Fact]
    public void ProjectFiring_HookFiring_CarriesTheDecisionFields()
    {
        var store = new SessionRuleStore(Db);
        var rule = SessionRuleWire.CreateFromBody(store, Body(HookBody), Now);
        var unreadable = new List<HookFiringRefusal>();
        var decisions = SessionRuleWire.ReadHookDecisions(
            Body($$"""{ "decisions": [ {{W3("d-wire", rule.Id.ToString())}} ] }"""), unreadable);
        Assert.Equal(1, store.RecordHookFirings(decisions).Accepted);

        var wire = AsWire(SessionRuleWire.Project(Assert.Single(store.FiringsFor(rule.Id))));

        Assert.Equal("hook", wire.GetProperty("kind").GetString());
        Assert.Equal("d-wire", wire.GetProperty("decisionId").GetString());
        Assert.Equal("would_deny", wire.GetProperty("decision").GetString());
        Assert.Equal("before_command", wire.GetProperty("event").GetString());
        Assert.Equal("shell", wire.GetProperty("tool").GetString());
        Assert.Equal("echo RULE-PROBE", wire.GetProperty("subject").GetString());
        Assert.Equal("devlinux", wire.GetProperty("machine").GetString());
        Assert.Equal(Repo, wire.GetProperty("repo").GetString());
        Assert.Equal("claude_code", wire.GetProperty("agent").GetString());
        Assert.Equal("PreToolUse", wire.GetProperty("agentEvent").GetString());
        Assert.Equal("Bash", wire.GetProperty("agentTool").GetString());
        Assert.Equal(1, wire.GetProperty("overriddenRuleIds").GetArrayLength());
        Assert.Equal(2, wire.GetProperty("matchedRuleIds").GetArrayLength());
        Assert.Equal(812, wire.GetProperty("evaluationMicroseconds").GetInt64());
    }
    // ---- who may call the two new routes ---------------------------------------------------------------

    /// <summary>
    /// W1 and W5 are called with the Director's own credential (and the user's), never a session key. Whether
    /// session keys reach the /gateway/rules surface is phase 2's decision for the whole surface; this pins the
    /// phase 1 contract, so opening these two routes to sessions is a deliberate change to this test rather
    /// than a side effect.
    /// </summary>
    [Theory]
    [InlineData("GET", "/gateway/rules/effective")]
    [InlineData("POST", "/gateway/rules/hook-firings")]
    public void SessionKeyGuard_EffectiveRulesAndHookFiringsRoutes_AreRefusedToASessionKey(string method, string path)
        => Assert.False(CcDirector.Gateway.Util.SessionKeyGuard.Check(method, path).Allowed,
            $"{method} {path} must be refused to a session key in phase 1.");
}
