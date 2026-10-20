using System.Text.Json;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Rules;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// PHASE 2 MEETS PHASE 1 (Tech Lead ruling P18). Phase 2 opened the rule routes to session keys, added a change
/// route and recorded every write in <c>session_rule_changes</c>; phase 1 added a rule's kind, where it applies,
/// and hook rules. After the two met:
///
///  - every phase 1 field travels through the change route and the history's before and after;
///  - a change meets the same write gate as a create;
///  - a rule's kind cannot be changed;
///  - promoting a hook rule stays refused, a session asking included;
///  - the effective rules route (W1) and the hook firings route (W5) still refuse a session key.
///
/// The PATCH route is a thin binding of <see cref="SessionRuleStore.Change"/> over
/// <see cref="SessionRuleWire.ReadChange"/>, and the POST route of <see cref="SessionRuleWire.CreateFromBody"/>,
/// so these drive exactly what the routes drive, over a real migrated SQLite database.
/// </summary>
public sealed class SessionRuleKindsMeetChangesTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase? _db;

    private GatewayDatabase Db => _db ??= _h.Open();

    public void Dispose() => _h.Dispose();

    private static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    private static readonly Guid AgentSessionId = Guid.Parse("3d0c6a1e-8f7b-4b52-9e0a-6f2d1c4b8a90");

    private static readonly RuleChangeActor AgentSession = AnActor.Session(AgentSessionId, "ClaudeCode");

    private const string Repo = "github.com/thefrederiksen/devthrottle";

    private const string ScreenBody = """
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

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private SessionRule CreateHook(SessionRuleStore store) =>
        SessionRuleWire.CreateFromBody(store, Json(HookBody), Now, AgentSession);

    private SessionRule CreateScreen(SessionRuleStore store) =>
        SessionRuleWire.CreateFromBody(store, Json(ScreenBody), Now, AgentSession);

    /// <summary>What PATCH /gateway/rules/{id} does with this body.</summary>
    private static SessionRule Patch(SessionRuleStore store, Guid id, string changes)
    {
        var body = Json(changes);
        return store.Change(id, stored => SessionRuleWire.ReadChange(stored, body), Now.AddMinutes(5), AgentSession, out _);
    }

    private static JsonElement Side(string snapshot)
    {
        Assert.NotEqual("", snapshot);
        return Json(snapshot);
    }

    private static string[] Strings(JsonElement element, string name) =>
        element.GetProperty(name).EnumerateArray().Select(e => e.GetString()!).ToArray();

    // ---- every phase 1 field is in the history's before and after -------------------------------------

    [Fact]
    public void CreateHook_BySession_RecordsKindAppliesToAndTheTriggerInTheAfter()
    {
        // Arrange
        var store = new SessionRuleStore(Db);

        // Act
        var rule = CreateHook(store);

        // Assert
        var change = Assert.Single(store.History(rule.Id, null, null, 10));
        Assert.Equal(RuleChangeActions.Create, change.Action);
        Assert.Equal(AgentSessionId.ToString(), change.SessionId);
        var after = Side(change.After);
        Assert.Equal(RuleKinds.Hook, after.GetProperty("kind").GetString());
        Assert.Equal(RuleAppliesToKinds.Repo, after.GetProperty("appliesTo").GetProperty("kind").GetString());
        Assert.Equal(Repo, after.GetProperty("appliesTo").GetProperty("repo").GetString());
        Assert.Equal(HookRuleWords.BeforeCommand, after.GetProperty("event").GetString());
        Assert.Equal(HookRuleWords.Shell, after.GetProperty("tool").GetString());
        Assert.Equal(HookRuleWords.Match, after.GetProperty("handler").GetString());
        Assert.Equal(new[] { "echo RULE-PROBE" }, Strings(after, "match"));
        Assert.Equal(HookRuleWords.Deny, after.GetProperty("result").GetString());
        Assert.Equal("dry_run", after.GetProperty("state").GetString());
    }

    [Fact]
    public void CreateHook_History_CarriesNoScreenFields()
    {
        // Arrange
        var store = new SessionRuleStore(Db);

        // Act
        var rule = CreateHook(store);

        // Assert - a hook rule's body is the body its create takes; the screen fields would be refused there.
        var after = Side(Assert.Single(store.History(rule.Id, null, null, 10)).After);
        foreach (var screenField in new[] { "screenDescription", "triggerWords", "checks", "scope", "cooldownSeconds", "dailyCap" })
            Assert.False(after.TryGetProperty(screenField, out _), $"a hook rule's history carries '{screenField}'");
    }

    [Fact]
    public void CreateScreen_History_RecordsItsKindAndWhereItApplies()
    {
        // Arrange
        var store = new SessionRuleStore(Db);

        // Act
        var rule = CreateScreen(store);

        // Assert
        var after = Side(Assert.Single(store.History(rule.Id, null, null, 10)).After);
        Assert.Equal(RuleKinds.Screen, after.GetProperty("kind").GetString());
        Assert.Equal(RuleAppliesToKinds.Everywhere, after.GetProperty("appliesTo").GetProperty("kind").GetString());
    }

    // ---- every phase 1 field travels through the change route -------------------------------------------

    [Fact]
    public void ChangeHook_MatchText_IsStoredAndRecordedBeforeAndAfter()
    {
        // Arrange
        var store = new SessionRuleStore(Db);
        var rule = CreateHook(store);

        // Act
        var changed = Patch(store, rule.Id, """{ "match": ["git push --force", "git push -f"] }""");

        // Assert
        Assert.Equal(new[] { "git push --force", "git push -f" }, changed.Hook!.Match);
        Assert.Equal(new[] { "git push --force", "git push -f" }, store.Get(rule.Id)!.Hook!.Match);
        var change = store.History(rule.Id, null, RuleChangeActions.Change, 10).Single();
        Assert.Equal(new[] { "echo RULE-PROBE" }, Strings(Side(change.Before), "match"));
        Assert.Equal(new[] { "git push --force", "git push -f" }, Strings(Side(change.After), "match"));
        Assert.Equal(RuleKinds.Hook, Side(change.After).GetProperty("kind").GetString());
    }

    [Fact]
    public void ChangeHook_AppliesTo_IsStoredInItsOneSpellingAndRecorded()
    {
        // Arrange
        var store = new SessionRuleStore(Db);
        var rule = CreateHook(store);

        // Act
        var changed = Patch(store, rule.Id,
            """{ "appliesTo": { "kind": "repo_on_machine", "repo": "https://github.com/TheFrederiksen/devthrottle.git", "machines": ["devlinux"] } }""");

        // Assert
        Assert.Equal(RuleAppliesToKinds.RepoOnMachine, changed.AppliesTo.Kind);
        Assert.Equal(Repo, changed.AppliesTo.Repo);
        Assert.Equal(new[] { "devlinux" }, changed.AppliesTo.Machines);
        var change = store.History(rule.Id, null, RuleChangeActions.Change, 10).Single();
        Assert.Equal(RuleAppliesToKinds.Repo, Side(change.Before).GetProperty("appliesTo").GetProperty("kind").GetString());
        var afterAppliesTo = Side(change.After).GetProperty("appliesTo");
        Assert.Equal(RuleAppliesToKinds.RepoOnMachine, afterAppliesTo.GetProperty("kind").GetString());
        Assert.Equal(new[] { "devlinux" }, Strings(afterAppliesTo, "machines"));
    }

    [Fact]
    public void ChangeHook_ResultAndInstruction_AreStoredAndRecorded()
    {
        // Arrange
        var store = new SessionRuleStore(Db);
        var rule = CreateHook(store);

        // Act
        var changed = Patch(store, rule.Id, """{ "result": "allow", "instruction": "Echoing the probe is fine here." }""");

        // Assert
        Assert.Equal(HookRuleWords.Allow, changed.Hook!.Result);
        Assert.Equal("Echoing the probe is fine here.", changed.Instruction);
        var change = store.History(rule.Id, null, RuleChangeActions.Change, 10).Single();
        Assert.Equal(HookRuleWords.Deny, Side(change.Before).GetProperty("result").GetString());
        Assert.Equal(HookRuleWords.Allow, Side(change.After).GetProperty("result").GetString());
        Assert.Equal(HookRuleWords.BeforeCommand, Side(change.After).GetProperty("event").GetString());
        Assert.Equal(HookRuleWords.Shell, Side(change.After).GetProperty("tool").GetString());
        Assert.Equal(HookRuleWords.Match, Side(change.After).GetProperty("handler").GetString());
    }

    [Fact]
    public void ChangeHook_OnlyOneField_KeepsEveryOtherHookField()
    {
        // Arrange
        var store = new SessionRuleStore(Db);
        var rule = CreateHook(store);

        // Act
        var changed = Patch(store, rule.Id, """{ "result": "allow" }""");

        // Assert
        Assert.Equal(rule.Instruction, changed.Instruction);
        Assert.Equal(rule.AppliesTo.Kind, changed.AppliesTo.Kind);
        Assert.Equal(rule.AppliesTo.Repo, changed.AppliesTo.Repo);
        Assert.Equal(rule.Hook!.Event, changed.Hook!.Event);
        Assert.Equal(rule.Hook.Tool, changed.Hook.Tool);
        Assert.Equal(rule.Hook.Handler, changed.Hook.Handler);
        Assert.Equal(rule.Hook.Match, changed.Hook.Match);
        Assert.Equal(RuleKinds.Hook, changed.Kind);
    }

    [Theory]
    [InlineData("""{ "event": "after_edit" }""", "phase 4")]
    [InlineData("""{ "tool": "edit" }""", "phase 4")]
    [InlineData("""{ "handler": "check" }""", "phase 4")]
    [InlineData("""{ "result": "ask" }""", "deny")]
    public void ChangeHook_ToAnEventToolHandlerOrResultPhase1DoesNotAccept_IsRefusedAndNothingIsRecorded(string changes, string reasonSays)
    {
        // Arrange
        var store = new SessionRuleStore(Db);
        var rule = CreateHook(store);

        // Act
        var ex = Assert.Throws<RuleRejectedException>(() => Patch(store, rule.Id, changes));

        // Assert
        Assert.Contains(reasonSays, ex.Reason, StringComparison.Ordinal);
        Assert.Empty(store.History(rule.Id, null, RuleChangeActions.Change, 10));
        Assert.Equal(SessionRuleBody.Snapshot(rule), SessionRuleBody.Snapshot(store.Get(rule.Id)!));
    }

    // ---- a change meets the same write gate as a create (P4, P7) ----------------------------------------

    [Theory]
    [InlineData("""{ "appliesTo": { "kind": "machine", "machines": ["devlinux"] } }""")]
    [InlineData("""{ "appliesTo": { "kind": "repo", "repo": "github.com/thefrederiksen/devthrottle" } }""")]
    public void ChangeScreen_AppliesToNarrowerThanEverywhere_IsRefused(string changes)
    {
        // Arrange
        var store = new SessionRuleStore(Db);
        var rule = CreateScreen(store);

        // Act
        var ex = Assert.Throws<RuleRejectedException>(() => Patch(store, rule.Id, changes));

        // Assert
        Assert.Contains("everywhere", ex.Reason, StringComparison.Ordinal);
        Assert.Equal(RuleAppliesToKinds.Everywhere, store.Get(rule.Id)!.AppliesTo.Kind);
        Assert.Empty(store.History(rule.Id, null, RuleChangeActions.Change, 10));
    }

    [Theory]
    [InlineData("""{ "match": ["echo RULE-PROBE"] }""")]
    [InlineData("""{ "result": "deny" }""")]
    [InlineData("""{ "event": "before_command" }""")]
    public void ChangeScreen_GainingAHookField_IsRefused(string changes)
    {
        // Arrange
        var store = new SessionRuleStore(Db);
        var rule = CreateScreen(store);

        // Act
        var ex = Assert.Throws<RuleRejectedException>(() => Patch(store, rule.Id, changes));

        // Assert
        Assert.Contains("hook", ex.Reason, StringComparison.Ordinal);
        Assert.Empty(store.History(rule.Id, null, RuleChangeActions.Change, 10));
    }

    [Theory]
    [InlineData("""{ "scope": "all-sessions" }""", "appliesTo")]
    [InlineData("""{ "scope": { "agent": "ClaudeCode" } }""", "appliesTo")]
    [InlineData("""{ "screenDescription": "A session waiting." }""", "screen description")]
    [InlineData("""{ "triggerWords": ["limit"] }""", "trigger words")]
    [InlineData("""{ "checks": [ { "name": "matches_any", "arguments": { "text": "<screen_text>", "terms": ["x"] } } ] }""", "phase 4")]
    [InlineData("""{ "cooldownSeconds": 60 }""", "cooldownSeconds")]
    [InlineData("""{ "dailyCap": 3 }""", "dailyCap")]
    public void ChangeHook_GainingAScreenFieldScopeOrCeiling_IsRefusedWithTheCreatesReason(string changes, string reasonSays)
    {
        // Arrange
        var store = new SessionRuleStore(Db);
        var rule = CreateHook(store);

        // Act
        var ex = Assert.Throws<RuleRejectedException>(() => Patch(store, rule.Id, changes));

        // Assert
        Assert.Contains(reasonSays, ex.Reason, StringComparison.Ordinal);
        Assert.Empty(store.History(rule.Id, null, RuleChangeActions.Change, 10));
        Assert.Equal(SessionRuleBody.Snapshot(rule), SessionRuleBody.Snapshot(store.Get(rule.Id)!));
    }

    [Theory]
    [InlineData("""{ "match": [""] }""", "blank text")]
    [InlineData("""{ "match": ["   "] }""", "blank text")]
    [InlineData("""{ "match": ["git push", " "] }""", "blank text")]
    [InlineData("""{ "match": [] }""", "at least one piece of text")]
    public void ChangeHook_MatchTextThatIsEmptyOrOnlyWhitespace_IsRefused(string changes, string reasonSays)
    {
        // Arrange
        var store = new SessionRuleStore(Db);
        var rule = CreateHook(store);

        // Act
        var ex = Assert.Throws<RuleRejectedException>(() => Patch(store, rule.Id, changes));

        // Assert
        Assert.Contains(reasonSays, ex.Reason, StringComparison.Ordinal);
        Assert.Equal(new[] { "echo RULE-PROBE" }, store.Get(rule.Id)!.Hook!.Match);
        Assert.Empty(store.History(rule.Id, null, RuleChangeActions.Change, 10));
    }

    [Fact]
    public void SaveChanges_HookRuleGivenACeilingRoundTheStore_IsRefusedByTheWriteGate()
    {
        // Arrange - written straight through the context, with its history row, so only the gate stands in the way.
        var store = new SessionRuleStore(Db);
        var rule = CreateHook(store);
        using var ctx = Db.CreateContext();
        var row = ctx.SessionRules.First(r => r.Id == rule.Id);
        row.CooldownSeconds = 60;
        ctx.SessionRuleChanges.Add(AChangeRow(ctx, rule.Id));

        // Act
        var ex = Assert.Throws<RuleRejectedException>(() => ctx.SaveChanges());

        // Assert
        Assert.Contains("hook rule", ex.Reason, StringComparison.Ordinal);
    }

    // ---- a rule's kind cannot be changed ---------------------------------------------------------------

    [Theory]
    [InlineData("hook")]
    [InlineData("session_start")]
    public void ChangeScreen_Kind_IsRefusedSayingDeleteAndCreate(string kind)
    {
        // Arrange
        var store = new SessionRuleStore(Db);
        var rule = CreateScreen(store);

        // Act
        var ex = Assert.Throws<RuleRejectedException>(() => Patch(store, rule.Id, $$"""{ "kind": "{{kind}}" }"""));

        // Assert
        Assert.Contains("kind cannot be changed", ex.Reason, StringComparison.Ordinal);
        Assert.Contains("Delete", ex.Reason, StringComparison.Ordinal);
        Assert.Equal(RuleKinds.Screen, store.Get(rule.Id)!.Kind);
        Assert.Empty(store.History(rule.Id, null, RuleChangeActions.Change, 10));
    }

    [Fact]
    public void ChangeHook_KindToScreen_IsRefusedSayingDeleteAndCreate()
    {
        // Arrange
        var store = new SessionRuleStore(Db);
        var rule = CreateHook(store);

        // Act
        var ex = Assert.Throws<RuleRejectedException>(() => Patch(store, rule.Id, """{ "kind": "screen" }"""));

        // Assert
        Assert.Contains("kind cannot be changed", ex.Reason, StringComparison.Ordinal);
        Assert.Contains("Delete", ex.Reason, StringComparison.Ordinal);
        Assert.Equal(RuleKinds.Hook, store.Get(rule.Id)!.Kind);
    }

    [Fact]
    public void ChangeHook_KindSaidAsItAlreadyIs_IsAcceptedAlongsideARealChange()
    {
        // Arrange
        var store = new SessionRuleStore(Db);
        var rule = CreateHook(store);

        // Act
        var changed = Patch(store, rule.Id, """{ "kind": "hook", "result": "allow" }""");

        // Assert
        Assert.Equal(RuleKinds.Hook, changed.Kind);
        Assert.Equal(HookRuleWords.Allow, changed.Hook!.Result);
    }

    [Fact]
    public void Change_DraftOfAnotherKind_IsRefusedByTheStore()
    {
        // Arrange - a merge that answers a screen draft for a hook rule, as a caller going round the wire might.
        var store = new SessionRuleStore(Db);
        var rule = CreateHook(store);
        var screen = SessionRuleWire.ReadDraft(Json(ScreenBody));

        // Act
        var ex = Assert.Throws<RuleRejectedException>(() =>
            store.Change(rule.Id, _ => screen, Now, AgentSession, out _));

        // Assert
        Assert.Contains("kind cannot be changed", ex.Reason, StringComparison.Ordinal);
        Assert.Contains("Delete", ex.Reason, StringComparison.Ordinal);
        Assert.Equal(RuleKinds.Hook, store.Get(rule.Id)!.Kind);
    }

    [Fact]
    public void SaveChanges_KindChangedRoundTheStore_IsRefusedByTheWriteGate()
    {
        // Arrange - a hook rule turned into a well-formed screen rule straight through the context, history and all.
        var store = new SessionRuleStore(Db);
        var rule = CreateHook(store);
        using var ctx = Db.CreateContext();
        var row = ctx.SessionRules.First(r => r.Id == rule.Id);
        row.Kind = RuleKinds.Screen;
        row.AppliesToKind = RuleAppliesToKinds.Everywhere;
        row.AppliesToRepo = "";
        row.HookEvent = row.HookTool = row.HookHandler = row.HookResult = "";
        row.HookMatch = new List<string>();
        row.ScreenDescription = "A session stopped on a provider allowance notice.";
        row.TriggerWords = new List<string> { "limit" };
        row.CooldownSeconds = 300;
        row.DailyCap = 5;
        row.Calls = new List<RulePrimitiveCall>
        {
            RulePrimitiveCall.To("matches_any",
                RuleArgument.FromInput("text", RuleInput.ScreenText),
                RuleArgument.LiteralList("terms", new[] { "usage limit" })),
        };
        ctx.SessionRuleChanges.Add(AChangeRow(ctx, rule.Id));

        // Act
        var ex = Assert.Throws<RuleRejectedException>(() => ctx.SaveChanges());

        // Assert
        Assert.Contains("kind", ex.Reason, StringComparison.Ordinal);
    }

    // ---- promoting a hook rule stays refused, whoever asks ----------------------------------------------

    [Fact]
    public void PromoteHook_AskedForByASession_IsRefusedAndNothingIsRecorded()
    {
        // Arrange
        var store = new SessionRuleStore(Db);
        var rule = CreateHook(store);
        var grant = RulePromotionGrant.FromAuthenticatedRequest(
            rule.Id, AnInboundRequest.FromSession(AgentSessionId), "Dry run looked right; making it live.", Now);

        // Act
        var ex = Assert.Throws<RuleRejectedException>(() => store.Promote(rule.Id, grant, Now, AgentSession));

        // Assert
        Assert.Contains("phase 4", ex.Reason, StringComparison.Ordinal);
        Assert.Equal(RuleState.DryRun, store.Get(rule.Id)!.State);
        Assert.Empty(store.History(rule.Id, null, RuleChangeActions.Promote, 10));
    }

    // ---- W1 and W5 stay the Director's -----------------------------------------------------------------

    [Theory]
    [InlineData("GET", "/gateway/rules/effective")]
    [InlineData("HEAD", "/gateway/rules/effective")]
    [InlineData("PATCH", "/gateway/rules/effective")]
    [InlineData("DELETE", "/gateway/rules/effective")]
    [InlineData("POST", "/gateway/rules/hook-firings")]
    [InlineData("GET", "/gateway/rules/hook-firings")]
    [InlineData("PATCH", "/gateway/rules/hook-firings")]
    [InlineData("DELETE", "/gateway/rules/hook-firings")]
    [InlineData("GET", "/Gateway/Rules/Effective/")]
    public void SessionKeyGuard_EffectiveRulesAndHookFirings_AreRefusedToASessionKeyWhateverTheVerb(string method, string path)
    {
        // Act
        var verdict = CcDirector.Gateway.Util.SessionKeyGuard.Check(method, path);

        // Assert
        Assert.False(verdict.Allowed, $"{method} {path} was allowed to a session key");
        Assert.NotEqual("", verdict.Reason);
    }

    private static SessionRuleChangeEntity AChangeRow(GatewayDbContext ctx, Guid ruleId) => new()
    {
        TenantId = ctx.ActiveTenant!,
        RuleId = ruleId,
        Action = RuleChangeActions.Change,
        ChangedBy = AnActor.Owner.ChangedBy,
        OccurredUtc = Now,
        Sequence = 1000,
        Before = "{}",
        After = "{}",
    };
}
