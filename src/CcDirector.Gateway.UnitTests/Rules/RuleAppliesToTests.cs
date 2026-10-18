using CcDirector.Gateway.Rules;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// Where a rule applies (mission decision D1, Tech Lead rulings P3, P4 and P5): everywhere, some machines,
/// one repository, or one repository on some machines. These are the pure questions - does this rule reach
/// this machine and this repository, and how specific is it - that the effective-rules route answers with.
/// Every positive below has its negative beside it, because a scope that only ever says yes is not a scope.
/// </summary>
public sealed class RuleAppliesToTests
{
    private const string Repo = "github.com/thefrederiksen/devthrottle";
    private const string OtherRepo = "github.com/thefrederiksen/devthrottle_internal";

    [Fact]
    public void Reaches_Everywhere_ReachesAnyMachineAndAnyRepositoryIncludingNone()
    {
        var everywhere = RuleAppliesTo.Everywhere;
        Assert.True(everywhere.Reaches("devlinux", Repo));
        Assert.True(everywhere.Reaches("DESKTOP-1", ""));
    }

    [Fact]
    public void Reaches_MachineList_ReachesOnlyThoseMachinesIgnoringCase()
    {
        var rule = RuleAppliesTo.OnMachines(new[] { "devlinux", "DESKTOP-1" });
        Assert.True(rule.Reaches("DevLinux", Repo));
        Assert.True(rule.Reaches("desktop-1", ""));
        Assert.False(rule.Reaches("laptop", Repo));
    }

    [Fact]
    public void Reaches_ExceptMachines_ReachesEveryMachineButThose()
    {
        var rule = RuleAppliesTo.ExceptOnMachines(new[] { "devlinux" });
        Assert.False(rule.Reaches("DEVLINUX", Repo));
        Assert.True(rule.Reaches("laptop", Repo));
    }

    [Fact]
    public void Reaches_Repo_ReachesThatRepositoryOnAnyMachineAndNoOther()
    {
        var rule = RuleAppliesTo.InRepo(Repo);
        Assert.True(rule.Reaches("devlinux", Repo));
        Assert.True(rule.Reaches("laptop", Repo));
        Assert.False(rule.Reaches("devlinux", OtherRepo));
        // A checkout with no remote has no repository identity, so no repository rule reaches it (P6).
        Assert.False(rule.Reaches("devlinux", ""));
    }

    [Fact]
    public void Reaches_RepoOnMachine_NeedsBothTheRepositoryAndTheMachine()
    {
        var rule = RuleAppliesTo.InRepoOnMachines(Repo, new[] { "devlinux" });
        Assert.True(rule.Reaches("devlinux", Repo));
        Assert.False(rule.Reaches("laptop", Repo));
        Assert.False(rule.Reaches("devlinux", OtherRepo));
    }

    [Fact]
    public void Rank_EachKind_IsItsSpecificityFromRulingP3()
    {
        Assert.Equal(1, RuleAppliesTo.Everywhere.Rank);
        Assert.Equal(2, RuleAppliesTo.OnMachines(new[] { "devlinux" }).Rank);
        Assert.Equal(2, RuleAppliesTo.ExceptOnMachines(new[] { "devlinux" }).Rank);
        Assert.Equal(3, RuleAppliesTo.InRepo(Repo).Rank);
        Assert.Equal(4, RuleAppliesTo.InRepoOnMachines(Repo, new[] { "devlinux" }).Rank);
    }

    [Fact]
    public void Kinds_WireSpellings_AreTheOnesThePlanNames()
    {
        Assert.Equal("everywhere", RuleAppliesToKinds.Everywhere);
        Assert.Equal("machine", RuleAppliesToKinds.Machine);
        Assert.Equal("repo", RuleAppliesToKinds.Repo);
        Assert.Equal("repo_on_machine", RuleAppliesToKinds.RepoOnMachine);
        Assert.Equal("screen", RuleKinds.Screen);
        Assert.Equal("hook", RuleKinds.Hook);
        Assert.Equal("session_start", RuleKinds.SessionStart);
        Assert.Equal("before_command", HookRuleWords.BeforeCommand);
        Assert.Equal("shell", HookRuleWords.Shell);
        Assert.Equal("match", HookRuleWords.Match);
        Assert.Equal("deny", HookRuleWords.Deny);
        Assert.Equal("allow", HookRuleWords.Allow);
        Assert.Equal("would_deny", HookRuleWords.WouldDeny);
        Assert.Equal("would_allow", HookRuleWords.WouldAllow);
    }
}
