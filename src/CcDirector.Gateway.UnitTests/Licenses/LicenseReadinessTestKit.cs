using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Settings;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// The readiness service for a test that only READS it (the overview route): built over the test's own database and
/// store, with a sign-in service whose Directors never answer and an alarm whose email and push go nowhere. A test of
/// what the readiness check DOES builds its own (<c>LicenseReadinessServiceTests</c>).
/// </summary>
internal static class LicenseReadinessTestKit
{
    public static LicenseReadinessService ForReading(GatewayDatabase db, LicenseStore store, Func<TenantId, string, bool> isConnected,
        Func<DateTime> nowUtc)
    {
        var reads = new LicenseReadModel(store);
        var log = new LicenseSignInLog(db);
        var directors = new FakeLicenseDirectors();
        var signIns = new LicenseSignInService(log, reads, isConnected, directors.SendAsync, directors.EnterScope, nowUtc,
            CancellationToken.None);
        var approvers = new LicenseApproverReadiness(new LicenseApproverStore(new TenantSettingsStore(db)), reads, nowUtc);
        var alarms = new LicenseAlarmService(db,
            (_, _, _) => Task.FromResult(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null)),
            (_, _, _) => Task.CompletedTask, nowUtc);
        return new LicenseReadinessService(store, new LicenseSnapshotSource(store, reads), log, signIns, approvers.WhyNotReady,
            isConnected, alarms, nowUtc);
    }
}
