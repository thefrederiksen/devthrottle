using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Inbox;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Inbox;

/// <summary>
/// THE WAKE'S HEARTBEAT ASKS THE SWITCH QUIETLY (Factory Inbox mission, phase 2; production cost review, 6 November 2026).
/// The heartbeat asks every 15 seconds for every account with inbox work, so the production environment asks the Factory
/// Agents switch without its decision line, over the REAL switch and a throwaway settings store. The answer is the same.
/// </summary>
public sealed class GatewayFactoryInboxWakeEnvironmentTests : IDisposable
{
    private static readonly TenantId A = new("acct-a");
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static GatewayFactoryInboxWakeEnvironment Wired(FactoryAgentsSwitch factoryAgents) => new(
        factory: (_, _) => throw new InvalidOperationException("not asked"),
        published: (_, _) => throw new InvalidOperationException("not asked"),
        roster: _ => throw new InvalidOperationException("not asked"),
        lastKnown: (_, _) => throw new InvalidOperationException("not asked"),
        hasEnded: (_, _) => throw new InvalidOperationException("not asked"),
        publishedBrief: (_, _, _) => throw new InvalidOperationException("not asked"),
        directorOn: (_, _, _) => throw new InvalidOperationException("not asked"),
        start: (_, _, _, _) => throw new InvalidOperationException("not asked"),
        ring: (_, _, _, _, _) => throw new InvalidOperationException("not asked"),
        factoryAgents: factoryAgents);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FactoryAgentsOn_GivesTheSwitchsAnswer_WithNoDecisionLine(bool machineWide)
    {
        var lines = new List<string>();
        var factoryAgents = new FactoryAgentsSwitch(machineWide, new TenantSettingsStore(_h.Open()), log: lines.Add);

        var on = Wired(factoryAgents).FactoryAgentsOn(A);

        Assert.Equal(machineWide, on);
        Assert.Empty(lines);
        // The same question asked the logged way writes its line: the log seam is live, so the empty list above means quiet.
        Assert.Equal(machineWide, factoryAgents.IsOn(A));
        Assert.Contains(lines, l => l.StartsWith("[FactoryAgentsSwitch]", StringComparison.Ordinal));
    }
}
