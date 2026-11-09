using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory;

/// <summary>
/// THE QUIET ANSWER WRITES NOTHING (review of the Pro-carries-factories change, note 1). <see cref="FactoryAgentsSwitch.AnswerQuietly"/>
/// is asked on a heartbeat - the inbox wake every 15 seconds per account, the roster's factory chip on every session list
/// read - so it must write no log line per ask, for a team as for a personal account. The plan reads beneath it (the
/// account's plan, the trial ledger, the team's bill, the team Owner's plan) write straight to <see cref="FileLog"/>, so
/// the check watches FileLog itself rather than an injected log. A read that FAILS is still logged: that is a fault.
///
/// FileLog is process-wide, so this class is in the serial capture collection and keeps only lines written on its own
/// thread. HOW THESE FAIL ON PURPOSE: drop the quiet flag anywhere on the path (the switch, the gate, the entitlement
/// read, the trial read, the team Owner reader) and the matching row writes a line and goes red.
/// </summary>
[Collection(FileLogCaptureCollection.Name)]
public sealed class FactorySwitchQuietAnswerTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly TenantRegistry _tenants;
    private readonly EntitlementRegistry _entitlements;
    private readonly TeamRegistry _teams;
    private readonly FactoryAgentsSwitch _switch;

    public FactorySwitchQuietAnswerTests()
    {
        _db = _harness.Open();
        using (var ctx = _db.CreateUnscopedContext())
        {
            ctx.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS entitlements (" +
                "subject TEXT NOT NULL PRIMARY KEY, status TEXT NOT NULL, " +
                "current_period_end TEXT NULL, stripe_subscription_id TEXT NULL, updated_at TEXT NULL, " +
                "livemode INTEGER NULL, tier TEXT NULL)");
        }
        _tenants = new TenantRegistry(_db);
        _entitlements = new EntitlementRegistry(_db, requireLivemode: true, trials: new TrialRegistry(_db));
        _teams = new TeamRegistry(_db, _tenants);
        var plans = new TenantAiGate(_entitlements, _tenants, new TeamMemberEntitlement(_teams, _entitlements),
            clock: new FakeTimeProvider(new DateTimeOffset(Now)));
        _switch = new FactoryAgentsSwitch(false, new TenantSettingsStore(_db), plans, log: FileLog.Write,
            teamOwners: new TeamOwnerFactories(_teams, TeamOwnerPlan.Hosted(_entitlements), () => Now));
    }

    public void Dispose() => _harness.Dispose();

    private TenantId TenantOf(string subject) => _tenants.MintOrLookupBySubject(subject, subject + "@example.com");

    private TenantId TeamOwnedBy(string owner)
    {
        TenantOf(owner);
        return _teams.CreateTeam(owner, "A team").Team!.Tenant;
    }

    private void RunningTrial(string subject)
    {
        using var ctx = _db.CreateUnscopedContext();
        ctx.AccountTrials.Add(new AccountTrialEntity { Subject = subject, StartedAtUtc = Now.AddDays(-1), ExpiresAtUtc = Now.AddDays(13) });
        ctx.SaveChanges();
    }

    private void EndedTrial(string subject)
    {
        using var ctx = _db.CreateUnscopedContext();
        ctx.AccountTrials.Add(new AccountTrialEntity { Subject = subject, StartedAtUtc = Now.AddDays(-20), ExpiresAtUtc = Now.AddDays(-6) });
        ctx.SaveChanges();
    }

    /// <summary>Every FileLog line written on THIS thread while <paramref name="act"/> runs.</summary>
    private static (T Result, List<string> Lines) Watch<T>(Func<T> act)
    {
        var thread = Environment.CurrentManagedThreadId;
        var lines = new List<string>();
        FileLog.LineObserverForTest = line =>
        {
            if (Environment.CurrentManagedThreadId == thread)
                lock (lines) lines.Add(line);
        };
        try
        {
            return (act(), lines);
        }
        finally
        {
            FileLog.LineObserverForTest = null;
        }
    }

    [Fact]
    public void AnswerQuietly_TeamWhoseOwnerIsOnFreeWithNoBill_WritesNoLogLine()
    {
        // Arrange - the worst case before the fix: the team bill's line, the Owner's plan line and the free-plan line.
        var team = TeamOwnedBy("sub-quiet-free");
        _switch.Answer(team);

        // Act
        var (answer, lines) = Watch(() => _switch.AnswerQuietly(team));

        // Assert
        Assert.Equal(FactoryAgentsOffReasons.TeamOwnerPlanRequired, answer.OffReason);
        Assert.Empty(lines);
    }

    [Fact]
    public void AnswerQuietly_TeamWhoseOwnerIsOnATrial_WritesNoLogLine()
    {
        // Arrange
        RunningTrial("sub-quiet-trial");
        var team = TeamOwnedBy("sub-quiet-trial");
        _switch.Answer(team);

        // Act
        var (answer, lines) = Watch(() => _switch.AnswerQuietly(team));

        // Assert
        Assert.Equal(FactoryAgentsGrant.TeamOwnerPlan, answer.Grant);
        Assert.Empty(lines);
    }

    [Fact]
    public void AnswerQuietly_TeamWhoseOwnersTrialHasEnded_WritesNoLogLine()
    {
        // Arrange
        EndedTrial("sub-quiet-ended");
        var team = TeamOwnedBy("sub-quiet-ended");
        _switch.Answer(team);

        // Act
        var (answer, lines) = Watch(() => _switch.AnswerQuietly(team));

        // Assert
        Assert.Equal(FactoryAgentsOffReasons.TeamOwnerPlanRequired, answer.OffReason);
        Assert.Empty(lines);
    }

    [Fact]
    public void AnswerQuietly_PersonalAccountOnFree_WritesNoLogLine()
    {
        // Arrange
        var personal = TenantOf("sub-quiet-personal");

        // Act
        var (answer, lines) = Watch(() => _switch.AnswerQuietly(personal));

        // Assert
        Assert.Equal(FactoryAgentsOffReasons.PlanRequired, answer.OffReason);
        Assert.Empty(lines);
    }

    [Fact]
    public void Answer_TeamWhoseOwnerIsOnFree_StillWritesTheDecisionAndItsReads()
    {
        // Arrange
        var team = TeamOwnedBy("sub-loud-free");

        // Act
        var (quiet, _) = Watch(() => _switch.AnswerQuietly(team));
        var (loud, lines) = Watch(() => _switch.Answer(team));

        // Assert - the same answer, and the loud one still says why.
        Assert.Equal(loud, quiet);
        Assert.Contains(lines, l => l.StartsWith("[FactoryAgentsSwitch] ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("[TeamOwnerPlan] Decide: scope=factories", StringComparison.Ordinal));
    }

    [Fact]
    public void AnswerQuietly_PlanReadFails_StillLogsTheFault()
    {
        // Arrange - the entitlement table is gone, as when the database is unreachable.
        var team = TeamOwnedBy("sub-quiet-fault");
        using (var ctx = _db.CreateUnscopedContext())
            ctx.Database.ExecuteSqlRaw("DROP TABLE entitlements");

        // Act
        var (answer, lines) = Watch(() => _switch.AnswerQuietly(team));

        // Assert
        Assert.Equal(FactoryAgentsState.Unknown, answer.State);
        Assert.Contains(lines, l => l.Contains("[EntitlementRegistry] Evaluate: READ FAILED", StringComparison.Ordinal));
    }
}
