using System.Text.Json;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;
using Xunit;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// THE MINUTE CHECK ON A REAL HOSTED GATEWAY, END TO END (License Switching): two accounts, each with one Director on
/// the real tunnel. Each Director sends its usage report through <c>DirectorHub.PushLicenseUsage</c>; the host's own
/// check runs (<c>GatewayHost.RunLicenseSwitchCheckAsync</c>, what its timer calls); each Director answers the
/// <c>license-switch</c> command that reaches it.
///
/// What this holds down that the service tests cannot: the host really builds the check over its own
/// <c>SendCommandAsync</c> and runs it inside the per-tenant pass, so on the HOSTED Gateway the command is delivered
/// rather than dropped for want of a tenant scope (contract section 6 rule 4) - and each account's switch reaches that
/// account's Director and no other.
/// </summary>
public sealed class LicenseSwitchCheckHostedTests : IAsyncLifetime
{
    private const string A = "account-a@example.com";
    private const string B = "account-b@example.com";

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

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-license-check-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "test-token", authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = true });
        // The host takes reports and decides by this clock, so the test's reports are exactly as old as it says.
        _gateway.LicenseClock = new FixedClock();
        await _gateway.StartAsync();

        // The Gateway database is shared by every host this test process starts, so the two accounts are new ones
        // for each test: an account left by another test would arrive with machines and a switch log of its own.
        var run = Guid.NewGuid().ToString("N");
        _alice = HostedTestEnrollment.Enroll(_gateway, $"sub-alice-{run}", $"alice-{run}@example.com", $"dev-a-{run}", "MA");
        _bob = HostedTestEnrollment.Enroll(_gateway, $"sub-bob-{run}", $"bob-{run}@example.com", $"dev-b-{run}", "MB");
        _aliceDirector = await FakeTunnelDirector.StartAsync(_gateway, _alice.DeviceKey, "d-alice", "MACHINE_OF_ALICE");
        _bobDirector = await FakeTunnelDirector.StartAsync(_gateway, _bob.DeviceKey, "d-bob", "MACHINE_OF_BOB");
    }

    public async Task DisposeAsync()
    {
        await _aliceDirector.DisposeAsync();
        await _bobDirector.DisposeAsync();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    /// <summary>A machine on A at 97% of its 5-hour window, holding a spare for B with room.</summary>
    private static LicenseUsageReport AtTheThreshold(string machine)
    {
        var now = Now;
        LicenseHolding Holding(string email, bool active, double fiveHour) => new()
        {
            AccountEmail = email,
            IsActive = active,
            Plan = "max",
            FiveHour = new LicenseWindow { Utilization = fiveHour, ResetsAtUtc = now.AddHours(2) },
            SevenDay = new LicenseWindow { Utilization = 20, ResetsAtUtc = now.AddDays(4) },
            ReadAtUtc = now,
        };
        return new LicenseUsageReport
        {
            MachineName = machine,
            OsUser = "test",
            SentAtUtc = now,
            Trigger = LicenseReportTriggers.Connected,
            Holdings = [Holding(A, true, 97), Holding(B, false, 10)],
        };
    }

    /// <summary>Make the Director run every switch it is sent and answer <c>switched</c>; returns what it was sent.</summary>
    private static List<DirectorCommand> RunsEverySwitch(FakeTunnelDirector director)
    {
        var received = new List<DirectorCommand>();
        director.OnCommand(command =>
        {
            lock (received) received.Add(command);
            var request = JsonSerializer.Deserialize<LicenseSwitchRequest>(command.PayloadJson, FakeTunnelDirector.WebJson)
                          ?? throw new InvalidOperationException("The command carried no switch request.");
            return FakeTunnelDirector.Ok(new LicenseSwitchResult
            {
                SwitchId = request.SwitchId,
                Outcome = LicenseSwitchOutcomes.Switched,
                PreviousActiveAccountEmail = request.ExpectedActiveAccountEmail,
                ActiveAccountEmailAfter = request.TargetAccountEmail,
                VerifiedByUsageRead = true,
                StartedAtUtc = Now,
                FinishedAtUtc = Now.AddSeconds(1),
                ExchangeMilliseconds = 41,
            });
        });
        return received;
    }

    [Fact]
    public async Task TheHostsCheck_SwitchesEachAccountsMachineOverTheTunnel_AndEachDirectorGetsOnlyItsOwnAccountsSwitch()
    {
        var sentToAlice = RunsEverySwitch(_aliceDirector);
        var sentToBob = RunsEverySwitch(_bobDirector);
        await _aliceDirector.PushLicenseUsageAsync(AtTheThreshold("MACHINE_OF_ALICE"));
        await _bobDirector.PushLicenseUsageAsync(AtTheThreshold("MACHINE_OF_BOB"));

        await _gateway.RunLicenseSwitchCheckAsync();

        foreach (var (device, sent, machine) in new[] { (_alice, sentToAlice, "MACHINE_OF_ALICE"), (_bob, sentToBob, "MACHINE_OF_BOB") })
        {
            var command = Assert.Single(sent);
            Assert.Equal((LicenseContract.SwitchVerb, ""), (command.Verb, command.SessionId));
            var request = JsonSerializer.Deserialize<LicenseSwitchRequest>(command.PayloadJson, FakeTunnelDirector.WebJson)!;
            Assert.Equal((B, A, LicenseSwitchReasons.Threshold), (request.TargetAccountEmail, request.ExpectedActiveAccountEmail, request.Reason));
            Assert.Equal(DateTimeKind.Utc, request.NotAfterUtc.Kind);
            Assert.Equal(LicenseContract.SwitchNotAfter, request.NotAfterUtc - request.RequestedAtUtc);

            // The row this account's Director was sent is this account's row, and it ended switched.
            var row = Assert.Single(_gateway.LicenseStore.ListLog(device.Tenant, Now.AddHours(-1)), r => r.Kind == LicenseLogKinds.Switch);
            Assert.Equal((request.SwitchId, LicenseLogStates.Switched, machine), (row.SwitchId, row.State, row.MachineName));
            Assert.Equal(B, Assert.Single(_gateway.LicenseReads.Read(device.Tenant, Now).Machines).ActiveAccountEmail);
        }
        Assert.NotEqual(
            JsonSerializer.Deserialize<LicenseSwitchRequest>(sentToAlice[0].PayloadJson, FakeTunnelDirector.WebJson)!.SwitchId,
            JsonSerializer.Deserialize<LicenseSwitchRequest>(sentToBob[0].PayloadJson, FakeTunnelDirector.WebJson)!.SwitchId);
    }

    [Fact]
    public async Task TheHostsCheck_ForAnAccountWhoseMachinesNeverReported_DoesNothing()
    {
        var sentToAlice = RunsEverySwitch(_aliceDirector);
        var sentToBob = RunsEverySwitch(_bobDirector);
        await _aliceDirector.PushLicenseUsageAsync(AtTheThreshold("MACHINE_OF_ALICE"));

        await _gateway.RunLicenseSwitchCheckAsync();

        Assert.Single(sentToAlice);
        Assert.Empty(sentToBob);
        Assert.Empty(_gateway.LicenseStore.ListLog(_bob.Tenant, Now.AddHours(-1)));
    }
}
