using System.Text.Json;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Rules;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// AGENTS MANAGE RULES, AND EVERY CHANGE IS RECORDED (Session Rules part 2, phase 2, owner decision D7). The store
/// writes a history row in the same save as each create, change, promote and delete, naming who made it - for a
/// session, its id and agent - with the rule before and after. These run over a real migrated SQLite database, so
/// a history row is a real row read back, not an object handed to itself.
/// </summary>
public sealed class SessionRuleChangeHistoryTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase? _db;

    private GatewayDatabase Db => _db ??= _h.Open();

    public void Dispose() => _h.Dispose();

    private static readonly DateTime Now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    private static readonly Guid AgentSessionId = Guid.Parse("9a6f1510-57a2-4cde-8571-8bbdbd9938a0");

    private static readonly RuleChangeActor AgentSession = AnActor.Session(AgentSessionId, "ClaudeCode");

    private SessionRuleStore NewStore() => new(Db);

    private static List<RulePrimitiveCall> GoodCalls() => new()
    {
        RulePrimitiveCall.To(
            "matches_any",
            RuleArgument.FromInput("text", RuleInput.ScreenText),
            RuleArgument.LiteralList("terms", new[] { "usage limit" })),
    };

    private static SessionRule CreateTheRule(SessionRuleStore store, RuleChangeActor actor) => store.Create(
        "When I run out of allowance, switch me to Opus.",
        "A session stopped on a provider allowance notice.",
        new[] { "limit" }, GoodCalls(), RuleScope.AllSessions, 300, 5, Now, actor);

    private static SessionRule ChangeDailyCap(SessionRuleStore store, SessionRule rule, int dailyCap, RuleChangeActor actor) =>
        store.Change(rule.Id, rule.Instruction, rule.ScreenDescription, rule.TriggerWords, rule.Calls, rule.Scope,
            rule.CooldownSeconds, dailyCap, Now.AddMinutes(5), actor);

    private static RulePromotionGrant SessionGrantFor(Guid ruleId) =>
        RulePromotionGrant.FromAuthenticatedRequest(
            ruleId, AnInboundRequest.FromSession(AgentSessionId), "Dry run looked right; making it live.", Now);

    // ---- create ---------------------------------------------------------------------------------------

    [Fact]
    public void Create_BySession_RecordsTheSessionItsAgentAndTheRuleAfter()
    {
        // Arrange
        var store = NewStore();

        // Act
        var rule = CreateTheRule(store, AgentSession);

        // Assert
        var change = Assert.Single(store.History(rule.Id, null, null, 10));
        Assert.Equal(RuleChangeActions.Create, change.Action);
        Assert.Equal("session:" + AgentSessionId, change.ChangedBy);
        Assert.Equal(AgentSessionId.ToString(), change.SessionId);
        Assert.Equal("ClaudeCode", change.Agent);
        Assert.Equal("", change.Before);
        using var after = JsonDocument.Parse(change.After);
        Assert.Equal("dry_run", after.RootElement.GetProperty("state").GetString());
        Assert.Equal(5, after.RootElement.GetProperty("dailyCap").GetInt32());
    }

    [Fact]
    public void Create_ThatIsRefused_RecordsNoHistory()
    {
        // Arrange
        var store = NewStore();

        // Act
        Assert.Throws<RuleRejectedException>(() => store.Create(
            "When I run out of allowance, switch me to Opus.", "A screen.", new[] { "limit" },
            new[] { RulePrimitiveCall.To("no_such_check") }, RuleScope.AllSessions, 300, 5, Now, AgentSession));

        // Assert
        Assert.Empty(store.History(null, null, null, 10));
    }

    // ---- change ---------------------------------------------------------------------------------------

    [Fact]
    public void Change_ALiveRule_PutsItBackInDryRunAndRecordsBeforeAndAfter()
    {
        // Arrange
        var store = NewStore();
        var rule = CreateTheRule(store, AnActor.Owner);
        store.Promote(rule.Id, SessionGrantFor(rule.Id), Now, AgentSession);

        // Act
        var changed = ChangeDailyCap(store, store.Get(rule.Id)!, 2, AgentSession);

        // Assert
        Assert.Equal(RuleState.DryRun, changed.State);
        Assert.Equal("", changed.PromotedBy);
        Assert.Equal(2, new SessionRuleStore(Db).Get(rule.Id)!.DailyCap);
        var change = store.History(rule.Id, null, RuleChangeActions.Change, 10).Single();
        using var before = JsonDocument.Parse(change.Before);
        using var after = JsonDocument.Parse(change.After);
        Assert.Equal("live", before.RootElement.GetProperty("state").GetString());
        Assert.Equal(5, before.RootElement.GetProperty("dailyCap").GetInt32());
        Assert.Equal("dry_run", after.RootElement.GetProperty("state").GetString());
        Assert.Equal(2, after.RootElement.GetProperty("dailyCap").GetInt32());
        Assert.Equal(AgentSessionId.ToString(), change.SessionId);
    }

    [Fact]
    public void Change_ARuleThatDoesNotExist_IsRefusedWithTheIdAndRecordsNothing()
    {
        // Arrange
        var store = NewStore();
        var missing = Guid.NewGuid();

        // Act
        var ex = Assert.Throws<RuleRejectedException>(() => store.Change(
            missing, "An instruction.", "A screen.", new[] { "limit" }, GoodCalls(), RuleScope.AllSessions,
            300, 5, Now, AgentSession));

        // Assert
        Assert.Contains(missing.ToString(), ex.Reason);
        Assert.Empty(store.History(null, null, null, 10));
    }

    [Fact]
    public void Change_ThatChangesNothing_IsRefusedAndTheLiveRuleStaysLive()
    {
        // Arrange
        var store = NewStore();
        var rule = CreateTheRule(store, AnActor.Owner);
        store.Promote(rule.Id, SessionGrantFor(rule.Id), Now, AgentSession);
        var live = store.Get(rule.Id)!;

        // Act
        var ex = Assert.Throws<RuleRejectedException>(() => ChangeDailyCap(store, live, live.DailyCap, AgentSession));

        // Assert
        Assert.Contains("exactly as it was", ex.Reason);
        Assert.Equal(RuleState.Live, store.Get(rule.Id)!.State);
        Assert.Empty(store.History(rule.Id, null, RuleChangeActions.Change, 10));
    }

    [Fact]
    public void Change_ToACheckTheProductDoesNotShip_IsRefusedAndTheRuleIsUnchanged()
    {
        // Arrange
        var store = NewStore();
        var rule = CreateTheRule(store, AgentSession);

        // Act
        var ex = Assert.Throws<RuleRejectedException>(() => store.Change(
            rule.Id, rule.Instruction, rule.ScreenDescription, rule.TriggerWords,
            new[] { RulePrimitiveCall.To("run_this_python") }, rule.Scope, 300, 5, Now, AgentSession));

        // Assert
        Assert.Contains("run_this_python", ex.Reason);
        Assert.Equal("matches_any", store.Get(rule.Id)!.Calls.Single().Name);
        Assert.Empty(store.History(rule.Id, null, RuleChangeActions.Change, 10));
    }

    [Fact]
    public void Change_WithNoScope_IsRefusedRatherThanReadAsEverySession()
    {
        // Arrange
        var store = NewStore();
        var rule = CreateTheRule(store, AgentSession);

        // Act
        var ex = Assert.Throws<RuleRejectedException>(() => store.Change(
            rule.Id, rule.Instruction, rule.ScreenDescription, rule.TriggerWords, rule.Calls, scope: null,
            300, 3, Now, AgentSession));

        // Assert
        Assert.Contains("which sessions", ex.Reason);
    }

    // ---- promote and delete ---------------------------------------------------------------------------

    [Fact]
    public void Promote_BySession_NamesTheSessionOnTheRuleAndInTheHistory()
    {
        // Arrange
        var store = NewStore();
        var rule = CreateTheRule(store, AgentSession);

        // Act
        var promoted = store.Promote(rule.Id, SessionGrantFor(rule.Id), Now, AgentSession);

        // Assert
        Assert.Equal(RuleState.Live, promoted.State);
        Assert.Equal("session:" + AgentSessionId, promoted.PromotedBy);
        var change = store.History(rule.Id, null, RuleChangeActions.Promote, 10).Single();
        Assert.Equal("ClaudeCode", change.Agent);
        using var after = JsonDocument.Parse(change.After);
        Assert.Equal("live", after.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public void Promote_ARuleAlreadyLive_RecordsNothingMore()
    {
        // Arrange
        var store = NewStore();
        var rule = CreateTheRule(store, AgentSession);
        store.Promote(rule.Id, SessionGrantFor(rule.Id), Now, AgentSession);

        // Act
        store.Promote(rule.Id, SessionGrantFor(rule.Id), Now, AgentSession);

        // Assert
        Assert.Single(store.History(rule.Id, null, RuleChangeActions.Promote, 10));
    }

    [Fact]
    public void Delete_RecordsTheRuleBeforeAndTheHistoryOutlivesTheRule()
    {
        // Arrange
        var store = NewStore();
        var rule = CreateTheRule(store, AgentSession);

        // Act
        var deleted = store.Delete(rule.Id, Now, AgentSession);

        // Assert
        Assert.True(deleted);
        Assert.Null(store.Get(rule.Id));
        var history = store.History(rule.Id, null, null, 10);
        Assert.Equal(new[] { RuleChangeActions.Delete, RuleChangeActions.Create }, history.Select(c => c.Action));
        Assert.Equal("", history[0].After);
        Assert.Contains("When I run out of allowance", history[0].Before);
    }

    [Fact]
    public void Delete_ARuleThatDoesNotExist_RecordsNothing()
    {
        // Arrange
        var store = NewStore();

        // Act
        var deleted = store.Delete(Guid.NewGuid(), Now, AgentSession);

        // Assert
        Assert.False(deleted);
        Assert.Empty(store.History(null, null, null, 10));
    }

    // ---- reading the history --------------------------------------------------------------------------

    [Fact]
    public void History_FiltersBySessionRuleAndAction_EachNarrowing()
    {
        // Arrange
        var store = NewStore();
        var mine = CreateTheRule(store, AgentSession);
        var theOwners = CreateTheRule(store, AnActor.Owner);
        store.Promote(mine.Id, SessionGrantFor(mine.Id), Now, AgentSession);

        // Act
        var bySession = store.History(null, AgentSessionId.ToString(), null, 10);
        var byRule = store.History(theOwners.Id, null, null, 10);
        var byAction = store.History(null, null, RuleChangeActions.Promote, 10);
        var all = store.History(null, null, null, 10);

        // Assert
        Assert.Equal(2, bySession.Count);
        Assert.All(bySession, c => Assert.Equal(mine.Id, c.RuleId));
        Assert.Equal(theOwners.Id, Assert.Single(byRule).RuleId);
        Assert.Equal(mine.Id, Assert.Single(byAction).RuleId);
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public void History_AnActionThatIsNotAChange_IsRefusedNamingTheValidOnes()
    {
        // Arrange
        var store = NewStore();

        // Act
        var ex = Assert.Throws<RuleRejectedException>(() => store.History(null, null, "rename", 10));

        // Assert
        Assert.Contains("create, change, promote, delete", ex.Reason);
    }

    [Fact]
    public void History_ALimitBelowOne_IsRefused()
    {
        // Arrange
        var store = NewStore();

        // Act + Assert
        Assert.Throws<RuleRejectedException>(() => store.History(null, null, null, 0));
    }

    // ---- the history is append only -------------------------------------------------------------------

    [Fact]
    public void HistoryRow_EditedThroughTheContext_IsRefusedByTheWriteGate()
    {
        // Arrange
        var store = NewStore();
        CreateTheRule(store, AgentSession);
        using var ctx = Db.CreateContext();
        var row = ctx.SessionRuleChanges.First();

        // Act
        row.ChangedBy = "somebody else";
        var ex = Assert.Throws<RuleRejectedException>(() => ctx.SaveChanges());

        // Assert
        Assert.Contains("only ever added to", ex.Reason);
    }

    [Fact]
    public void HistoryRow_RemovedThroughTheContext_IsRefusedByTheWriteGate()
    {
        // Arrange
        var store = NewStore();
        CreateTheRule(store, AgentSession);
        using var ctx = Db.CreateContext();

        // Act
        ctx.SessionRuleChanges.Remove(ctx.SessionRuleChanges.First());
        var ex = Assert.Throws<RuleRejectedException>(() => ctx.SaveChanges());

        // Assert
        Assert.Contains("only ever added to", ex.Reason);
        Assert.Single(store.History(null, null, null, 10));
    }

    [Fact]
    public void HistoryRow_DeletedByBulkSql_IsRefusedByTheInterceptor()
    {
        // Arrange
        var store = NewStore();
        CreateTheRule(store, AgentSession);
        using var ctx = Db.CreateContext();

        // Act
        var ex = Assert.ThrowsAny<Exception>(() => ctx.SessionRuleChanges.ExecuteDelete());

        // Assert
        Assert.Contains("session_rule_changes", (ex as RuleRejectedException ?? ex.InnerException as RuleRejectedException)!.Reason);
        Assert.Single(store.History(null, null, null, 10));
    }

    [Fact]
    public void HistoryRow_ThatNamesNobody_IsRefusedByTheWriteGate()
    {
        // Arrange
        var store = NewStore();
        var rule = CreateTheRule(store, AgentSession);
        using var ctx = Db.CreateContext();

        // Act
        ctx.SessionRuleChanges.Add(new SessionRuleChangeEntity
        {
            TenantId = ctx.ActiveTenant!,
            RuleId = rule.Id,
            Action = RuleChangeActions.Delete,
            ChangedBy = " ",
            OccurredUtc = Now,
            Before = "{}",
        });
        var ex = Assert.Throws<RuleRejectedException>(() => ctx.SaveChanges());

        // Assert
        Assert.Contains("who made it", ex.Reason);
    }
}
