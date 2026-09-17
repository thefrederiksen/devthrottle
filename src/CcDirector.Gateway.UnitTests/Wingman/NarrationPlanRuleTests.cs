using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>The narration needs a Pro account (owner ruling, 2026-09-17): the one rule, over every plan the table knows.</summary>
public sealed class NarrationPlanRuleTests
{
    private const string Subject = "account-subject";

    [Theory]
    [InlineData(EntitlementRegistry.TierPro)]
    [InlineData(EntitlementRegistry.TierHosted)]
    [InlineData(EntitlementRegistry.TierProSelfHost)]
    public void Decide_APlanThatIncludesTheWingman_IsAllowed(string tier)
    {
        var plan = NarrationPlanRule.Decide(hosted: true, Subject, new EntitlementDecision(EntitlementOutcome.Entitled, tier));

        Assert.Equal(NarrationPlan.Allowed, plan);
    }

    [Fact]
    public void Decide_TheFreePlan_NeedsPro()
    {
        var plan = NarrationPlanRule.Decide(hosted: true, Subject, new EntitlementDecision(EntitlementOutcome.Entitled, EntitlementRegistry.TierFree));

        Assert.Equal(NarrationPlan.NeedsPro, plan);
    }

    [Fact]
    public void Decide_NoEntitlement_NeedsPro()
    {
        var plan = NarrationPlanRule.Decide(hosted: true, Subject, new EntitlementDecision(EntitlementOutcome.NotEntitled, null));

        Assert.Equal(NarrationPlan.NeedsPro, plan);
    }

    [Fact]
    public void Decide_ATenantWithNoAccountSubject_NeedsPro()
    {
        Assert.Equal(NarrationPlan.NeedsPro, NarrationPlanRule.Decide(hosted: true, subject: null, decision: null));
    }

    [Fact]
    public void Decide_AReadThatCouldNotBeMade_IsUnknown_NotNeedsPro()
    {
        var plan = NarrationPlanRule.Decide(hosted: true, Subject, new EntitlementDecision(EntitlementOutcome.Unknown, null));

        Assert.Equal(NarrationPlan.Unknown, plan);
    }

    [Fact]
    public void Decide_ASelfHostGateway_IsAllowed()
    {
        Assert.Equal(NarrationPlan.Allowed, NarrationPlanRule.Decide(hosted: false, subject: null, decision: null));
    }
}
