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
        factoryAgents: factoryAgents,
        bossStartOf: (_, _) => throw new InvalidOperationException("not asked"));

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

    // ---------- StartedAsTheBoss, the production read (issue #4598, review finding 4) ----------

    private GatewayFactoryInboxWakeEnvironment WiredWithHistory(CcDirector.Gateway.History.SessionHistoryStore history) => new(
        factory: (_, _) => throw new InvalidOperationException("not asked"),
        published: (_, _) => throw new InvalidOperationException("not asked"),
        roster: _ => throw new InvalidOperationException("not asked"),
        lastKnown: (_, _) => throw new InvalidOperationException("not asked"),
        hasEnded: (_, _) => throw new InvalidOperationException("not asked"),
        publishedBrief: (_, _, _) => throw new InvalidOperationException("not asked"),
        directorOn: (_, _, _) => throw new InvalidOperationException("not asked"),
        start: (_, _, _, _) => throw new InvalidOperationException("not asked"),
        ring: (_, _, _, _, _) => throw new InvalidOperationException("not asked"),
        factoryAgents: new FactoryAgentsSwitch(true, new TenantSettingsStore(_h.Open())),
        bossStartOf: history.BossStartOf);

    [Fact]
    public void StartedAsTheBoss_AnswersOnlyForTheRecordsOwnFactorySeatAndAccount()
    {
        // Recorded in the harness's own account (the local one); the read names its account explicitly.
        var history = new CcDirector.Gateway.History.SessionHistoryStore(_h.Open());
        history.RecordBossStart("boss-1", "invoices", "boss", "owners-agent", CcDirector.Gateway.Data.Entities.FactoryBossStartKinds.Started, new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc));
        var env = WiredWithHistory(history);

        Assert.True(env.StartedAsTheBoss(TenantId.Local, "boss-1", "invoices", "boss"));
        Assert.False(env.StartedAsTheBoss(TenantId.Local, "boss-1", "payroll", "boss"));
        Assert.False(env.StartedAsTheBoss(TenantId.Local, "boss-1", "invoices", "secretary"));
        Assert.False(env.StartedAsTheBoss(TenantId.Local, "someone-else", "invoices", "boss"));
        Assert.False(env.StartedAsTheBoss(A, "boss-1", "invoices", "boss"));
    }
}
