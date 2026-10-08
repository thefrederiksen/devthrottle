using CcDirector.Gateway.Api;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Factory;

/// <summary>
/// The Factories menu row is always shown (owner, 8 Oct 2026). When the area is off, the Factories page says so and how
/// to start - and how to start depends on the kind of Gateway, so the Gateway writes the sentence and the Cockpit shows
/// it verbatim (rule 7).
/// </summary>
public sealed class FactoryAgentsHowToStartTests
{
    [Fact]
    public void HowToStart_AreaOn_IsNull()
    {
        Assert.Null(FactoryAgentsViewEndpoints.HowToStart(enabled: true, hosted: false));
        Assert.Null(FactoryAgentsViewEndpoints.HowToStart(enabled: true, hosted: true));
    }

    [Fact]
    public void HowToStart_SelfHostedAndOff_NamesTheConfigSwitchAndTheRestart()
    {
        var sentence = FactoryAgentsViewEndpoints.HowToStart(enabled: false, hosted: false);

        Assert.Equal(
            "Factories is off on this Gateway. To start, add \"factoryAgents\": { \"enabled\": true } to the Gateway's config.json, then restart the Gateway.",
            sentence);
    }

    [Fact]
    public void HowToStart_HostedAndOff_SendsThePersonToDevThrottle_NeverToAConfigFileTheyCannotReach()
    {
        var sentence = FactoryAgentsViewEndpoints.HowToStart(enabled: false, hosted: true);

        Assert.Equal(
            "Factories is not switched on for your account yet. To start, ask DevThrottle to switch Factories on for your account.",
            sentence);
        Assert.DoesNotContain("config.json", sentence);
    }
}
