using System.Text.Json;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Rules;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// A CHANGE CARRIES THE RULE'S BODY GENERICALLY (Session Rules part 2, phase 2). A change is the rule's current
/// body - <see cref="SessionRuleBody.Of"/> - with the caller's fields laid over it, read back through the same
/// reader a create uses. So the body has to round trip exactly: a field the create reads and the body forgets
/// would be silently reset by every change. These tests hold the round trip and the change reader's refusals.
/// </summary>
public sealed class SessionRuleBodyTests
{
    private static readonly DateTime Now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    private static SessionRule ARule(RuleScope scope, RuleState state = RuleState.DryRun) => new(
        Guid.NewGuid(),
        "When I run out of allowance, switch me to Opus.",
        "A session stopped on a provider allowance notice.",
        new[] { "limit", "allowance" },
        new[]
        {
            RulePrimitiveCall.To(
                "matches_any",
                RuleArgument.FromInput("text", RuleInput.ScreenText),
                // A literal that LOOKS like an input request: written as a list, it must come back a literal.
                RuleArgument.LiteralList("terms", new[] { "<screen_text>", "usage limit" })),
            RulePrimitiveCall.To(
                "is_path_inside",
                RuleArgument.Literal("target", "/repo/src"),
                RuleArgument.Literal("root", "/repo")),
        },
        scope,
        300,
        5,
        state,
        state == RuleState.Live ? "session:abc" : "",
        Now,
        Now);

    private static SessionRuleDraft ReadBack(SessionRule rule)
    {
        using var document = JsonDocument.Parse(SessionRuleBody.Of(rule).ToJsonString());
        return SessionRuleWire.ReadDraft(document.RootElement.Clone());
    }

    private static string CallsOf(IEnumerable<RulePrimitiveCall> calls) =>
        string.Join(" | ", calls.Select(c => c.Name + ":" + string.Join(";",
            c.Arguments.Select(a => a.Parameter + "/" + a.Source + "/" + string.Join(",", a.Values)))));

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    // ---- the round trip -------------------------------------------------------------------------------

    [Fact]
    public void Of_ReadBackThroughTheCreateReader_GivesTheSameRule()
    {
        // Arrange
        var rule = ARule(new RuleScope("ClaudeCode", "github.com/acme/app", "devlinux", null));

        // Act
        var draft = ReadBack(rule);

        // Assert
        Assert.Equal(rule.Instruction, draft.Instruction);
        Assert.Equal(rule.ScreenDescription, draft.ScreenDescription);
        Assert.Equal(rule.TriggerWords, draft.TriggerWords);
        Assert.Equal(CallsOf(rule.Calls), CallsOf(draft.Calls));
        Assert.Equal(rule.Scope, draft.Scope);
        Assert.Equal(rule.CooldownSeconds, draft.CooldownSeconds);
        Assert.Equal(rule.DailyCap, draft.DailyCap);
    }

    [Fact]
    public void Of_EverySession_IsSaidOutLoudAndReadsBackAsEverySession()
    {
        // Arrange
        var rule = ARule(RuleScope.AllSessions);

        // Act
        var body = SessionRuleBody.Of(rule);
        var draft = ReadBack(rule);

        // Assert
        Assert.Equal("all-sessions", body["scope"]!.GetValue<string>());
        Assert.Equal(RuleScope.AllSessions, draft.Scope);
    }

    [Fact]
    public void Of_EveryFieldTheCreateReaderReads_IsInTheBody()
    {
        // Arrange - the fields a create reads, taken from a draft's own properties rather than a list kept here.
        var rule = ARule(RuleScope.AllSessions);
        var readable = typeof(SessionRuleDraft).GetProperties()
            .Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..])
            .Select(name => name == "calls" ? "checks" : name)
            .ToList();

        // Act
        var body = SessionRuleBody.Of(rule);

        // Assert
        Assert.NotEmpty(readable);
        Assert.All(readable, field => Assert.True(body.ContainsKey(field), $"the body has no '{field}'"));
    }

    [Fact]
    public void Snapshot_CarriesTheStateAndWhoMadeItLive()
    {
        // Arrange
        var rule = ARule(RuleScope.AllSessions, RuleState.Live);

        // Act
        using var snapshot = JsonDocument.Parse(SessionRuleBody.Snapshot(rule));

        // Assert
        Assert.Equal("live", snapshot.RootElement.GetProperty("state").GetString());
        Assert.Equal("session:abc", snapshot.RootElement.GetProperty("promotedBy").GetString());
    }

    // ---- reading a change -----------------------------------------------------------------------------

    [Fact]
    public void ReadChange_OneField_ChangesThatFieldAndKeepsEveryOther()
    {
        // Arrange
        var rule = ARule(new RuleScope(null, "github.com/acme/app", null, null));

        // Act
        var draft = SessionRuleWire.ReadChange(rule, Json("{\"dailyCap\": 2}"));

        // Assert
        Assert.Equal(2, draft.DailyCap);
        Assert.Equal(rule.Instruction, draft.Instruction);
        Assert.Equal(rule.Scope, draft.Scope);
        Assert.Equal(CallsOf(rule.Calls), CallsOf(draft.Calls));
    }

    [Fact]
    public void ReadChange_AFieldTheBodyDoesNotHave_IsRefusedNamingTheFieldsItCan()
    {
        // Arrange
        var rule = ARule(RuleScope.AllSessions);

        // Act
        var ex = Assert.Throws<RuleRejectedException>(() =>
            SessionRuleWire.ReadChange(rule, Json("{\"dailycap\": 2}")));

        // Assert
        Assert.Contains("'dailycap'", ex.Reason);
        Assert.Contains("dailyCap", ex.Reason);
    }

    [Fact]
    public void ReadChange_TheState_IsRefusedPointingAtPromote()
    {
        // Arrange
        var rule = ARule(RuleScope.AllSessions);

        // Act
        var ex = Assert.Throws<RuleRejectedException>(() =>
            SessionRuleWire.ReadChange(rule, Json("{\"state\": \"live\"}")));

        // Assert
        Assert.Contains("promote", ex.Reason);
    }

    [Fact]
    public void ReadChange_AnEmptyObject_IsRefused()
    {
        // Arrange
        var rule = ARule(RuleScope.AllSessions);

        // Act
        var ex = Assert.Throws<RuleRejectedException>(() => SessionRuleWire.ReadChange(rule, Json("{}")));

        // Assert
        Assert.Contains("names no fields", ex.Reason);
    }

    [Fact]
    public void ReadChange_NotAnObject_IsRefused()
    {
        // Arrange
        var rule = ARule(RuleScope.AllSessions);

        // Act
        var ex = Assert.Throws<RuleRejectedException>(() => SessionRuleWire.ReadChange(rule, Json("[1, 2]")));

        // Assert
        Assert.Contains("an object", ex.Reason);
    }

    [Fact]
    public void ReadChange_ChecksThatAreNotAList_AreRefusedByTheCreateReader()
    {
        // Arrange
        var rule = ARule(RuleScope.AllSessions);

        // Act
        var ex = Assert.Throws<RuleRejectedException>(() =>
            SessionRuleWire.ReadChange(rule, Json("{\"checks\": \"matches_any\"}")));

        // Assert
        Assert.Contains("list of checks", ex.Reason);
    }

    [Fact]
    public void ReadDraft_ACeilingThatIsNotAWholeNumber_IsRefused()
    {
        // Arrange
        var body = Json("{\"dailyCap\": 2.5}");

        // Act
        var ex = Assert.Throws<RuleRejectedException>(() => SessionRuleWire.Number(body, "dailyCap"));

        // Assert
        Assert.Contains("whole number", ex.Reason);
    }
}
