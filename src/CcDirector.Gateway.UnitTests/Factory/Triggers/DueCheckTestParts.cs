using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Factory.Triggers;
using CcDirector.Gateway.Running;

namespace CcDirector.Gateway.Tests.Factory.Triggers;

/// <summary>
/// The due-check service a route test needs when it is not about due checks (Factory Definition, phase 5b): over the
/// test's own database, with no schedules and a starter that is never reached. It hands out nothing, because the
/// test's database holds no run waiting on its check.
/// </summary>
internal static class DueCheckTestParts
{
    public static DueCheckService NoDueChecks(GatewayDatabase db, string legacyRunsPath)
        => new(new CronRunHistoryStore(db, legacyRunsPath), jobOf: (_, _) => null, new FactoryCheckStateStore(db),
            new NeverStarter(), directorRunsDefinitionChecks: (_, _) => true, resultRecorded: (_, _) => { },
            nowUtc: () => NoClock, startLifetime: CancellationToken.None);

    // A fixed time: nothing these tests ask of the service reads it, and a gate test never reads the real clock.
    private static readonly DateTime NoClock = new(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);

    private sealed class NeverStarter : ICronSessionStarter
    {
        public Task<(string? sessionId, string? directorId, string? error)> StartAsync(CronJobDto job, CancellationToken ct)
            => throw new InvalidOperationException("this test starts no scheduled session");
    }
}
