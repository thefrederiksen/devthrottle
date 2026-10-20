using CcDirector.RuleHook;
using Xunit;

namespace CcDirector.RuleHook.Tests;

/// <summary>
/// The P3 resolver: literal, case-sensitive matching (P1); the most specific scope decides (D2); deny
/// wins a tie at the same rank; and a decision names the rule that decided and the rules it went against.
/// </summary>
public sealed class RuleResolverTests
{
    internal static HookRule Rule(string id, string appliesTo, string result, params string[] match) => new()
    {
        Id = id,
        Rank = RuleResolver.RankFor(appliesTo),
        AppliesTo = appliesTo,
        Event = RuleResolver.BeforeCommandEvent,
        Tool = RuleResolver.ShellTool,
        Match = [.. match],
        Result = result,
        Reason = "reason for " + id,
        State = "dry_run",
    };

    [Fact]
    public void Decide_NoRuleMatches_ReturnsNoMatch()
    {
        var rules = new[] { Rule("a", "everywhere", "deny", "rm -rf") };

        var resolution = RuleResolver.Decide(rules, "echo hello");

        Assert.Null(resolution.Decision);
        Assert.Null(resolution.DecidingRule);
        Assert.Empty(resolution.MatchedRuleIds);
    }

    [Fact]
    public void Decide_NoRules_ReturnsNoMatch()
    {
        Assert.Null(RuleResolver.Decide([], "echo RULE-PROBE").Decision);
    }

    [Fact]
    public void Decide_OneDenyRuleContainedInCommand_WouldDenyNamingIt()
    {
        var rules = new[] { Rule("a", "repo", "deny", "echo RULE-PROBE") };

        var resolution = RuleResolver.Decide(rules, "cd /x && echo RULE-PROBE; ls");

        Assert.Equal("would_deny", resolution.Decision);
        Assert.Equal("a", resolution.DecidingRule!.Id);
        Assert.Equal(["a"], resolution.MatchedRuleIds);
        Assert.Empty(resolution.OverriddenRuleIds);
    }

    [Fact]
    public void Decide_AnyOfSeveralMatchTexts_Matches()
    {
        var rules = new[] { Rule("a", "everywhere", "deny", "git push --force", "git push -f") };

        Assert.Equal("would_deny", RuleResolver.Decide(rules, "git push -f origin main").Decision);
    }

    [Fact]
    public void Decide_MatchDiffersOnlyInCase_DoesNotMatch()
    {
        var rules = new[] { Rule("a", "everywhere", "deny", "echo RULE-PROBE") };

        Assert.Null(RuleResolver.Decide(rules, "echo rule-probe").Decision);
    }

    [Fact]
    public void Decide_MatchTextLooksLikeAPattern_IsTakenLiterally()
    {
        var rules = new[] { Rule("a", "everywhere", "deny", "rm .*") };

        Assert.Null(RuleResolver.Decide(rules, "rm file.txt").Decision);
        Assert.Equal("would_deny", RuleResolver.Decide(rules, "rm .*").Decision);
    }

    [Fact]
    public void Decide_RepoAllowAgainstEverywhereDeny_RepoAllowDecidesAndOverridesTheDeny()
    {
        var rules = new[]
        {
            Rule("global", "everywhere", "deny", "git push"),
            Rule("repo", "repo", "allow", "git push"),
        };

        var resolution = RuleResolver.Decide(rules, "git push origin main");

        Assert.Equal("would_allow", resolution.Decision);
        Assert.Equal("repo", resolution.DecidingRule!.Id);
        Assert.Equal(["global"], resolution.OverriddenRuleIds);
        Assert.Equal(["global", "repo"], resolution.MatchedRuleIds);
    }

    [Theory]
    [InlineData("repo_on_machine", "repo")]
    [InlineData("repo", "machine")]
    [InlineData("machine", "everywhere")]
    [InlineData("repo_on_machine", "everywhere")]
    public void Decide_MoreSpecificScopeDisagrees_MoreSpecificDecides(string specific, string broader)
    {
        var rules = new[]
        {
            Rule("broad", broader, "allow", "x"),
            Rule("specific", specific, "deny", "x"),
        };

        var resolution = RuleResolver.Decide(rules, "x");

        Assert.Equal("specific", resolution.DecidingRule!.Id);
        Assert.Equal("would_deny", resolution.Decision);
        Assert.Equal(["broad"], resolution.OverriddenRuleIds);
    }

    [Fact]
    public void Decide_SameRankAllowAndDeny_DenyWinsAndOverridesTheAllow()
    {
        var rules = new[]
        {
            Rule("allow", "machine", "allow", "x"),
            Rule("deny", "machine", "deny", "x"),
        };

        var resolution = RuleResolver.Decide(rules, "x");

        Assert.Equal("would_deny", resolution.Decision);
        Assert.Equal("deny", resolution.DecidingRule!.Id);
        Assert.Equal(["allow"], resolution.OverriddenRuleIds);
    }

    [Fact]
    public void Decide_LowerRankRuleAgrees_IsMatchedButNotOverridden()
    {
        var rules = new[]
        {
            Rule("global", "everywhere", "deny", "x"),
            Rule("repo", "repo", "deny", "x"),
        };

        var resolution = RuleResolver.Decide(rules, "x");

        Assert.Equal("repo", resolution.DecidingRule!.Id);
        Assert.Empty(resolution.OverriddenRuleIds);
        Assert.Equal(["global", "repo"], resolution.MatchedRuleIds);
    }

    [Fact]
    public void Decide_TwoTopRankRulesAgree_FirstInFileDecides()
    {
        var rules = new[]
        {
            Rule("first", "repo", "deny", "x"),
            Rule("second", "repo", "deny", "x"),
        };

        Assert.Equal("first", RuleResolver.Decide(rules, "x").DecidingRule!.Id);
    }

    [Fact]
    public void Decide_RuleForAnotherEventOrTool_NeverMatchesAShellCommand()
    {
        var otherEvent = Rule("e", "repo", "deny", "x");
        otherEvent.Event = "after_edit";
        var otherTool = Rule("t", "repo", "deny", "x");
        otherTool.Tool = "edit";

        Assert.Null(RuleResolver.Decide([otherEvent, otherTool], "x").Decision);
    }

    [Fact]
    public void Validate_GoodFile_ReturnsItsRules()
    {
        var file = new RulesFile { Version = 1, Rules = [Rule("a", "repo", "deny", "x")] };

        Assert.Single(RuleResolver.Validate(file));
    }

    [Fact]
    public void Validate_WrongVersion_Throws()
    {
        var file = new RulesFile { Version = 2, Rules = [] };

        Assert.Contains("version 2", Assert.Throws<RulesFileException>(() => RuleResolver.Validate(file)).Message);
    }

    [Fact]
    public void Validate_NoRulesList_Throws()
    {
        Assert.Throws<RulesFileException>(() => RuleResolver.Validate(new RulesFile { Version = 1 }));
    }

    [Fact]
    public void Validate_RankDisagreesWithAppliesTo_Throws()
    {
        var rule = Rule("a", "repo", "deny", "x");
        rule.Rank = 1;

        var ex = Assert.Throws<RulesFileException>(() => RuleResolver.Validate(new RulesFile { Version = 1, Rules = [rule] }));
        Assert.Contains("rank 1", ex.Message);
    }

    [Fact]
    public void Validate_EmptyMatchText_Throws()
    {
        var rule = Rule("a", "repo", "deny", "");

        Assert.Throws<RulesFileException>(() => RuleResolver.Validate(new RulesFile { Version = 1, Rules = [rule] }));
    }

    [Fact]
    public void Validate_NoMatchTexts_Throws()
    {
        var rule = Rule("a", "repo", "deny");

        Assert.Throws<RulesFileException>(() => RuleResolver.Validate(new RulesFile { Version = 1, Rules = [rule] }));
    }

    [Theory]
    [InlineData("result", "block")]
    [InlineData("state", "paused")]
    [InlineData("appliesTo", "galaxy")]
    [InlineData("id", "")]
    public void Validate_UnknownOrMissingValue_Throws(string field, string value)
    {
        var rule = Rule("a", "repo", "deny", "x");
        switch (field)
        {
            case "result": rule.Result = value; break;
            case "state": rule.State = value; break;
            case "appliesTo": rule.AppliesTo = value; break;
            case "id": rule.Id = value; break;
        }

        Assert.Throws<RulesFileException>(() => RuleResolver.Validate(new RulesFile { Version = 1, Rules = [rule] }));
    }

    [Fact]
    public void Validate_DuplicateRuleId_Throws()
    {
        var file = new RulesFile { Version = 1, Rules = [Rule("a", "repo", "deny", "x"), Rule("a", "machine", "allow", "y")] };

        Assert.Throws<RulesFileException>(() => RuleResolver.Validate(file));
    }
}
