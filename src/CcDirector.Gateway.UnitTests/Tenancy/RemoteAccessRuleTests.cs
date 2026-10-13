using CcDirector.Gateway.Tenancy;
using Xunit;

namespace CcDirector.Gateway.Tests.Tenancy;

/// <summary>
/// THE REMOTE ACCESS SCOPE, THE PRE-LAUNCH GRACE AND THE ONE RULE OVER BOTH (Pricing Change mission, B2 and B4). Pure:
/// no database, no host. What a plan includes is the scope table's; what the grace covers is two instants; and
/// <see cref="RemoteAccessRule.Decide"/> is the one function the mint and the request path both ask.
///
/// HOW THESE FAIL ON PURPOSE: give the free line or the self-host line <c>remote_access</c> and the scope tests go
/// red; take it from Remote, Pro, team, hosted or the pre-column row and they go red the other way; make an unset
/// launch instant mean "nobody is in grace" and the default-deploy test goes red.
/// </summary>
public sealed class RemoteAccessRuleTests
{
    private static readonly DateTime Launch = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime BeforeLaunch = new(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime AfterLaunch = new(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CutOff = new(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Person = "sub-a-person";

    // ================================================================= the scope table

    [Theory]
    [InlineData(EntitlementRegistry.TierRemote)]
    [InlineData(EntitlementRegistry.TierPro)]
    [InlineData(EntitlementRegistry.TierTeam)]
    [InlineData(EntitlementRegistry.TierHosted)]
    [InlineData(null)]
    [InlineData("   ")]
    public void GrantsRemoteAccess_RemoteProTeamTheRetiredHostedPlanAndThePreColumnRow_HaveIt(string? tier)
    {
        Assert.True(EntitlementScopes.GrantsRemoteAccess(tier));
        // Every one of them also keeps its Director: remote access is added to a plan, never swapped for the tunnel.
        Assert.True(EntitlementScopes.GrantsHostedGateway(tier));
    }

    [Theory]
    [InlineData(EntitlementRegistry.TierFree)]
    [InlineData(EntitlementRegistry.TierProSelfHost)]
    [InlineData("a-plan-nobody-taught-the-gateway")]
    [InlineData("REMOTE")]
    public void GrantsRemoteAccess_FreeTheSelfHostPlanAndAnUnknownTier_DoNotHaveIt(string tier)
    {
        Assert.False(EntitlementScopes.GrantsRemoteAccess(tier));
    }

    [Fact]
    public void ForTier_FreeKeepsTheDirector_AndRemoteIsFreePlusRemoteAccessAndNothingElse()
    {
        // The split, stated as sets: free is the Director alone; Remote adds the one scope it is sold for and no
        // artificial-intelligence scope.
        Assert.Equal(new[] { EntitlementScopes.HostedGateway }, EntitlementScopes.ForTier(EntitlementRegistry.TierFree).ToArray());
        Assert.Equal(
            new[] { EntitlementScopes.HostedGateway, EntitlementScopes.RemoteAccess }.OrderBy(s => s),
            EntitlementScopes.ForTier(EntitlementRegistry.TierRemote).OrderBy(s => s));
    }

    // ================================================================= the pre-launch grace

    [Fact]
    public void Covers_WithNeitherInstantSet_EveryAccountIsInGrace_EvenOneWithNoTenantRow()
    {
        // The default deploy: nothing set takes nothing from anyone.
        var grace = PreLaunchGrace.NotSet;

        Assert.True(grace.Covers(BeforeLaunch, AfterLaunch));
        Assert.True(grace.Covers(AfterLaunch, AfterLaunch));
        Assert.True(grace.Covers(null, AfterLaunch));
    }

    [Fact]
    public void Covers_WithALaunchInstant_OnlyAnAccountCreatedBeforeIt_IsInGrace()
    {
        var grace = new PreLaunchGrace(Launch, CutOffUtc: null);

        Assert.True(grace.Covers(BeforeLaunch, AfterLaunch));
        Assert.True(grace.Covers(Launch.AddTicks(-1), AfterLaunch));
        // Created AT the launch instant is not "before launch".
        Assert.False(grace.Covers(Launch, AfterLaunch));
        Assert.False(grace.Covers(AfterLaunch, AfterLaunch));
        // An account that never reached the hosted Gateway has no row: it never had the Cockpit to keep.
        Assert.False(grace.Covers(null, AfterLaunch));
    }

    [Fact]
    public void Covers_EndsAtTheCutOffInstant_AndNotATickBefore()
    {
        var grace = new PreLaunchGrace(Launch, CutOff);

        Assert.True(grace.Covers(BeforeLaunch, CutOff.AddTicks(-1)));
        Assert.False(grace.Covers(BeforeLaunch, CutOff));
        Assert.False(grace.Covers(BeforeLaunch, CutOff.AddDays(1)));
    }

    [Fact]
    public void Covers_ACutOffWithNoLaunchInstant_EndsTheGraceForEveryone()
    {
        var grace = new PreLaunchGrace(LaunchUtc: null, CutOff);

        Assert.True(grace.Covers(null, CutOff.AddTicks(-1)));
        Assert.False(grace.Covers(BeforeLaunch, CutOff));
    }

    [Fact]
    public void Parse_BlankValuesAreNotSet_AndAnInstantIsReadAsCoordinatedUniversalTime()
    {
        Assert.Equal(PreLaunchGrace.NotSet, PreLaunchGrace.Parse(null, null));
        Assert.Equal(PreLaunchGrace.NotSet, PreLaunchGrace.Parse("", "   "));

        var grace = PreLaunchGrace.Parse(" 2025-01-01T00:00:00Z ", "2025-06-01T00:00:00Z");

        Assert.Equal(Launch, grace.LaunchUtc);
        Assert.Equal(DateTimeKind.Utc, grace.LaunchUtc!.Value.Kind);
        Assert.Equal(CutOff, grace.CutOffUtc);
    }

    [Theory]
    [InlineData("2025-01-01")]
    [InlineData("2025-01-01T00:00:00")]
    [InlineData("2025-01-01T00:00:00+02:00")]
    [InlineData("next tuesday")]
    public void Parse_AValueThatIsNotAnInstantInTheOneForm_Throws_AndNamesTheSetting(string value)
    {
        // A mistyped cut-off read as "not set" would leave the grace on for good with nobody told, so it stops the
        // Gateway from starting instead.
        var launch = Assert.Throws<InvalidOperationException>(() => PreLaunchGrace.Parse(value, null));
        Assert.Contains(PreLaunchGrace.LaunchEnvVar, launch.Message);

        var cutOff = Assert.Throws<InvalidOperationException>(() => PreLaunchGrace.Parse(null, value));
        Assert.Contains(PreLaunchGrace.CutOffEnvVar, cutOff.Message);
    }

    [Fact]
    public void GatewayHostOptions_ByDefault_HaveNeitherInstantSet()
    {
        // No date lives in code: a host built with no options stated is a host with the grace unset.
        Assert.Equal(PreLaunchGrace.NotSet, new GatewayHostOptions().RemoteAccessGrace);
    }

    // ================================================================= the one rule

    [Fact]
    public void Decide_ThePlanGrantsWhateverTheGraceSays_AndTheGraceGrantsWhereThePlanDoesNot()
    {
        var ended = new RemoteAccessRule(new PreLaunchGrace(Launch, CutOff));
        var running = new RemoteAccessRule(new PreLaunchGrace(Launch, CutOffUtc: null));
        var now = CutOff.AddDays(30);

        // A plan with remote access has it whether or not the account is in grace.
        Assert.Equal(RemoteAccessAnswer.Granted, ended.Decide(EntitlementRegistry.TierRemote, Person, AfterLaunch, now));
        Assert.Equal(RemoteAccessAnswer.Granted, ended.Decide(EntitlementRegistry.TierPro, Person, null, now));

        // Free has it only through the grace: an account created before launch, while the grace runs.
        Assert.Equal(RemoteAccessAnswer.Granted, running.Decide(EntitlementRegistry.TierFree, Person, BeforeLaunch, now));
        Assert.Equal(RemoteAccessAnswer.NotGranted, running.Decide(EntitlementRegistry.TierFree, Person, AfterLaunch, now));
        Assert.Equal(RemoteAccessAnswer.NotGranted, ended.Decide(EntitlementRegistry.TierFree, Person, BeforeLaunch, now));
    }

    [Fact]
    public void Decide_TheGraceGrantsRemoteAccessAndNothingElse_ItNeverMakesAFreeAccountAnythingButFree()
    {
        // The grace is beside the table, not in it: an account in grace is still on the free tier, so the AI gate,
        // which asks the table, still refuses it every model call.
        var rule = new RemoteAccessRule(PreLaunchGrace.NotSet);

        Assert.Equal(RemoteAccessAnswer.Granted, rule.Decide(EntitlementRegistry.TierFree, Person, BeforeLaunch, AfterLaunch));
        Assert.False(EntitlementScopes.GrantsRemoteAccess(EntitlementRegistry.TierFree));
        foreach (var scope in new[] { EntitlementScopes.Dictation, EntitlementScopes.Tts, EntitlementScopes.Wingman })
            Assert.False(EntitlementScopes.Grants(EntitlementRegistry.TierFree, scope));
    }

    [Fact]
    public void Decide_WithNoTeamHalf_IsNeverUnknown_AndRefusesABlankSubject()
    {
        // Where Teams is not released the rule has two things to ask, and neither can fail to be read.
        var rule = new RemoteAccessRule(new PreLaunchGrace(Launch, CutOffUtc: null));

        Assert.Equal(RemoteAccessAnswer.NotGranted, rule.Decide(EntitlementRegistry.TierFree, Person, AfterLaunch, AfterLaunch));
        Assert.Throws<ArgumentException>(() => rule.Decide(EntitlementRegistry.TierFree, " ", AfterLaunch, AfterLaunch));
    }

    [Theory]
    [InlineData("browser", true)]
    [InlineData("phone", true)]
    [InlineData(" Browser ", true)]
    [InlineData("PHONE", true)]
    [InlineData("workstation", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("tablet", false)]
    public void IsCockpitOrPhoneDevice_IsTrueForTheTwoTypesTheGatewayWritesAtThatDoor_AndForNothingElse(string? deviceType, bool expected)
    {
        Assert.Equal(expected, RemoteAccessRule.IsCockpitOrPhoneDevice(deviceType));
    }
}
