using System.Text.Json;
using CcDirector.Core.Account;
using CcDirector.Gateway.Teams;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// The rig the Billing section screenshots in docs/proof/teams-bill-without-stripe were taken from (Teams v1, the team
/// bill without Stripe). NOT a test: it starts a hosted Gateway with Teams released on 127.0.0.1, seeds a team with one
/// member in each role and NO bill - so the Owner starts the plan through the page itself - writes the members' device
/// keys to a file, and serves the built Cockpit until a stop file appears. It then writes what the server holds.
///
/// SKIPPED unless <c>CC_TEAMS_BILL_PROOF_RIG</c> names the directory to write <c>rig.json</c> into; the driver is
/// <c>docs/proof/teams-bill-without-stripe/take-screenshots.py</c>. Every address is at example.org and the invitation
/// mailer records and never sends; the database is a scratch one deleted afterwards. Lives in the unit project for the
/// reason <see cref="TeamInvitationProofRig"/> gives: the Gateway suite's machine-wide lock.
/// </summary>
public sealed class TeamBillProofRig
{
    private const string RigEnvVar = "CC_TEAMS_BILL_PROOF_RIG";
    private const int RigPort = 7914;

    private sealed class RigFactAttribute : FactAttribute
    {
        public RigFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RigEnvVar)))
                Skip = $"Proof rig only. Set {RigEnvVar} to an output directory to run it.";
        }
    }

    [RigFact]
    public async Task ServeTheBillingSection_UntilTheStopFileAppears()
    {
        var outDir = Environment.GetEnvironmentVariable(RigEnvVar)!;
        Directory.CreateDirectory(outDir);
        var stopFile = Path.Combine(outDir, "stop");
        if (File.Exists(stopFile)) File.Delete(stopFile);
        var root = Path.Combine(Path.GetTempPath(), "cc-team-bill-rig-" + Guid.NewGuid().ToString("N"));

        var priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        var priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        var gateway = new GatewayHost(port: RigPort, token: "rig-token", authEnabled: true, instancesDirectory: root,
            workListsPath: Path.Combine(root, "worklists", "worklists.json"),
            snoozePath: Path.Combine(root, "snooze", "snooze.json"),
            streamMode: true, teamsReleased: true);
        gateway.TeamInvitationMailer = new RecordingMailer();
        try
        {
            Assert.True(Directory.Exists(Cockpit.CockpitReactApp.WebRoot),
                $"The built Cockpit is not beside the test binaries ({Cockpit.CockpitReactApp.WebRoot}). The driver copies apps/cockpit/dist there.");
            await gateway.StartAsync();

            string Enroll(string deviceId, string subject, string email)
            {
                var tenant = gateway.TenantRegistry.MintOrLookupBySubject(subject, email);
                var key = gateway.Devices.Register(deviceId, "RIG-" + deviceId).DeviceKey;
                gateway.Devices.SetAccountBinding(deviceId, subject, tenant.Value);
                return key;
            }

            var keyOwner = Enroll("rig-bill-owner", "sub-bill-owner", "owner@example.org");
            var keyManager = Enroll("rig-bill-manager", "sub-bill-manager", "manager@example.org");
            var keyDeveloper = Enroll("rig-bill-developer", "sub-bill-developer", "developer@example.org");
            gateway.TenantRegistry.MintOrLookupBySubject("sub-bill-developer-2", "developer.two@example.org");
            gateway.TenantRegistry.MintOrLookupBySubject("sub-bill-collaborator", "client@example.org");

            var registry = gateway.TeamRegistry;
            var team = registry.CreateTeam("sub-bill-owner", "Acme").Team!.TeamId;
            Assert.True(registry.AddMember(team, "sub-bill-manager", TeamRole.Manager).IsDone);
            Assert.True(registry.AddMember(team, "sub-bill-developer", TeamRole.Developer).IsDone);
            Assert.True(registry.AddMember(team, "sub-bill-developer-2", TeamRole.Developer).IsDone);
            Assert.True(registry.AddMember(team, "sub-bill-collaborator", TeamRole.Collaborator).IsDone);

            var rig = new
            {
                baseUrl = $"http://127.0.0.1:{gateway.Port}",
                teamId = team,
                owner = new { email = "owner@example.org", key = keyOwner },
                manager = new { email = "manager@example.org", key = keyManager },
                developer = new { email = "developer@example.org", key = keyDeveloper },
            };
            File.WriteAllText(Path.Combine(outDir, "rig.json"), JsonSerializer.Serialize(rig));

            var deadline = DateTime.UtcNow.AddMinutes(8);
            while (!File.Exists(stopFile) && DateTime.UtcNow < deadline)
                await Task.Delay(250);

            // What the screenshots changed, read back from the server, for the driver to check.
            var bill = gateway.TeamBills.Find(team);
            File.WriteAllText(Path.Combine(outDir, "after.json"), JsonSerializer.Serialize(new
            {
                status = bill?.Status,
                seats = bill?.Seats,
                autoRenew = bill?.AutoRenew,
                history = gateway.TeamBills.History(team).Select(h => new { h.Reason, h.Seats, h.AmountCents, h.ChargedCents }),
            }));
        }
        finally
        {
            await gateway.StopAsync();
            Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", priorHosted);
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", priorRoot);
            try { if (Directory.Exists(root)) Directory.Delete(root, true); }
            catch (IOException) { /* a file still held by the stopped host; the temp directory is left behind */ }
        }
    }

    private sealed class RecordingMailer : ITeamInvitationMailer
    {
        public Task<TeamInvitationMailResult> SendAsync(string invitationId, string teamId, string acceptToken, CancellationToken ct = default) =>
            Task.FromResult(new TeamInvitationMailResult(true, null, 200, null));
    }
}
