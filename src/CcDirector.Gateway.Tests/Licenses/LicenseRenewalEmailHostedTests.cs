using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;
using Xunit;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// THE FAILED-RENEWAL EMAIL ON A REAL HOSTED GATEWAY, END TO END (License Switching, contract section 8.1): two
/// accounts, each with one Director on the real tunnel. Each Director sends its usage report through
/// <c>DirectorHub.PushLicenseUsage</c>; the host's own once-a-minute check runs
/// (<c>GatewayHost.RunLicenseSwitchCheckAsync</c>, what its timer calls); the owner email is taken by a sender the
/// test put in the host's place, so nothing here reaches the account service.
///
/// What this holds down that the service tests cannot: the host really builds the email check, runs it in the minute
/// check inside the per-tenant pass, and gives it the host's own mail sender - so a failing renewal a Director
/// reports over the tunnel emails that account's owner, once, and no other account's.
/// </summary>
public sealed class LicenseRenewalEmailHostedTests : IAsyncLifetime
{
    private const string A = "account-a@example.com";

    /// <summary>The one moment every test here happens at.</summary>
    private static readonly DateTime Now = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Now);
    }

    private GatewayHost _gateway = null!;
    private HostedTestDevice _alice;
    private HostedTestDevice _bob;
    private FakeTunnelDirector _aliceDirector = null!;
    private FakeTunnelDirector _bobDirector = null!;
    private readonly List<(TenantId Tenant, LicenseOwnerEmail Email)> _sent = new();

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-license-renewal-email-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "test-token", authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = true });
        _gateway.LicenseClock = new FixedClock();
        // Every owner email of this host is taken here: no test of this class reaches the account service.
        _gateway.LicenseOwnerMail = (tenant, email, _) =>
        {
            lock (_sent) _sent.Add((tenant, email));
            return Task.FromResult(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null));
        };
        await _gateway.StartAsync();

        // The Gateway database is shared by every host this test process starts, so the two accounts are new ones
        // for each test.
        var run = Guid.NewGuid().ToString("N");
        _alice = HostedTestEnrollment.Enroll(_gateway, $"sub-alice-{run}", $"alice-{run}@example.com", $"dev-a-{run}", "MA");
        _bob = HostedTestEnrollment.Enroll(_gateway, $"sub-bob-{run}", $"bob-{run}@example.com", $"dev-b-{run}", "MB");
        _aliceDirector = await FakeTunnelDirector.StartAsync(_gateway, _alice.DeviceKey, $"d-alice-{run}", "MACHINE_OF_ALICE");
        _bobDirector = await FakeTunnelDirector.StartAsync(_gateway, _bob.DeviceKey, $"d-bob-{run}", "MACHINE_OF_BOB");
    }

    public async Task DisposeAsync()
    {
        await _aliceDirector.DisposeAsync();
        await _bobDirector.DisposeAsync();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    /// <summary>A machine on A, far from any threshold, whose sign-in ends in two days.</summary>
    private static LicenseUsageReport Report(string machine, bool renewalFailed) => new()
    {
        MachineName = machine,
        OsUser = "test",
        SentAtUtc = Now,
        Trigger = LicenseReportTriggers.Connected,
        Holdings =
        [
            new LicenseHolding
            {
                AccountEmail = A,
                IsActive = true,
                Plan = "max",
                FiveHour = new LicenseWindow { Utilization = 10, ResetsAtUtc = Now.AddHours(2) },
                SevenDay = new LicenseWindow { Utilization = 20, ResetsAtUtc = Now.AddDays(4) },
                ReadAtUtc = Now,
                SignInExpiresAtUtc = Now.AddDays(2),
                RenewalFailedAtUtc = renewalFailed ? Now.AddMinutes(-10) : null,
                RenewalError = renewalFailed ? LicenseRenewalErrors.ApproverOffline : null,
            },
        ],
    };

    private List<CcDirector.Gateway.Data.Entities.LicenseSwitchLogEntity> Warnings(TenantId tenant)
        => _gateway.LicenseStore.ListLog(tenant, Now.AddHours(-1)).Where(r => r.Kind == LicenseRenewalFailureLog.Kind).ToList();

    [Fact]
    public async Task TheHostsMinuteCheck_EmailsTheOwnerOfTheAccountWhoseRenewalIsFailing_Once_AndNoOtherAccount()
    {
        await _aliceDirector.PushLicenseUsageAsync(Report("MACHINE_OF_ALICE", renewalFailed: true));
        await _bobDirector.PushLicenseUsageAsync(Report("MACHINE_OF_BOB", renewalFailed: false));

        await _gateway.RunLicenseSwitchCheckAsync();
        await _gateway.RunLicenseSwitchCheckAsync();

        // One email, to Alice's account, for her machine - although the check ran twice.
        var (tenant, email) = Assert.Single(_sent);
        Assert.Equal(_alice.Tenant, tenant);
        Assert.Equal($"DevThrottle: the sign-in for {A} on MACHINE_OF_ALICE expires in 2 days and could not be renewed", email.Subject);
        Assert.Contains("Why the last try failed: the machine that approves this account was not connected.", email.BodyText);

        var row = Assert.Single(Warnings(_alice.Tenant));
        Assert.Equal((LicenseLogStates.Open, LicenseGatewayReasonCodes.OwnerEmailed, "MACHINE_OF_ALICE", A, 1),
            (row.State, row.ReasonCode, row.MachineName, row.ToAccountEmail, row.SendCount));
        Assert.Empty(Warnings(_bob.Tenant));
    }

    [Fact]
    public async Task ARenewalThatWorks_ClosesTheWarning_OnTheNextMinuteCheck()
    {
        await _aliceDirector.PushLicenseUsageAsync(Report("MACHINE_OF_ALICE", renewalFailed: true));
        await _gateway.RunLicenseSwitchCheckAsync();
        Assert.Equal(LicenseLogStates.Open, Assert.Single(Warnings(_alice.Tenant)).State);

        await _aliceDirector.PushLicenseUsageAsync(Report("MACHINE_OF_ALICE", renewalFailed: false));
        await _gateway.RunLicenseSwitchCheckAsync();

        Assert.Equal(LicenseLogStates.Ended, Assert.Single(Warnings(_alice.Tenant)).State);
        Assert.Single(_sent);
    }
}
