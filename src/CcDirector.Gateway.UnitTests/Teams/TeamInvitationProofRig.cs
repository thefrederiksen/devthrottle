using System.Text.Json;
using CcDirector.Core.Account;
using CcDirector.Gateway.Teams;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// The rig the invitation screenshots in docs/proof/teams-2301 were taken from (devthrottle_internal#2301). NOT a test:
/// it starts a hosted Gateway with Teams released on 127.0.0.1, seeds a billed team and the fleet test accounts, writes
/// their device keys and invitation links to a file, and serves the built Cockpit until a stop file appears.
///
/// SKIPPED unless <c>CC_TEAMS_2301_PROOF_RIG</c> names the directory to write <c>rig.json</c> into; the driver is
/// <c>docs/proof/teams-2301/take-screenshots.py</c>. The invitation mailer records and never sends, so no email leaves
/// the machine, and the database is a scratch one deleted afterwards.
///
/// It lives here rather than in CcDirector.Gateway.Tests on purpose: that suite holds a machine-wide lock for the whole of
/// a run, so a rig there waits behind every other session's parked gate. This rig is skipped in every suite run and is
/// only ever run on its own, by its driver.
/// </summary>
public sealed class TeamInvitationProofRig
{
    private const string RigEnvVar = "CC_TEAMS_2301_PROOF_RIG";
    private const int RigPort = 7911;

    private sealed class RigFactAttribute : FactAttribute
    {
        public RigFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RigEnvVar)))
                Skip = $"Proof rig only. Set {RigEnvVar} to an output directory to run it.";
        }
    }

    [RigFact]
    public async Task ServeTheInvitationScreens_UntilTheStopFileAppears()
    {
        var outDir = Environment.GetEnvironmentVariable(RigEnvVar)!;
        Directory.CreateDirectory(outDir);
        var stopFile = Path.Combine(outDir, "stop");
        if (File.Exists(stopFile)) File.Delete(stopFile);
        var root = Path.Combine(Path.GetTempPath(), "cc-team-inv-rig-" + Guid.NewGuid().ToString("N"));

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

            // The fleet's own test accounts only. Nothing is emailed to any of them: the mailer records.
            var keyOwner = Enroll("rig-owner", "sub-rig-qa", "qa@mindzie.com");
            var keyManager = Enroll("rig-manager", "sub-rig-tech", "tech@mindzie.com");
            var keyInvitee = Enroll("rig-invitee", "sub-rig-dev", "dev@mindzie.com");
            var keyNewcomer = Enroll("rig-newcomer", "sub-rig-docs", "docs@mindzie.com");

            var registry = gateway.TeamRegistry;
            var team = registry.CreateTeam("sub-rig-qa", "Acme QA").Team!.TeamId;
            TeamBillSeed.Active(gateway.GatewayDatabaseForTests, team, seats: 2);
            Assert.True(registry.AddMember(team, "sub-rig-tech", TeamRole.Manager).IsDone);

            string Invite(string by, string email, TeamRole role)
            {
                var result = registry.CreateInvitation(team, by, email, role);
                Assert.Equal(TeamInvitationOutcome.Done, result.Outcome);
                return result.AcceptToken!;
            }

            var tokenSignedIn = Invite("sub-rig-qa", "dev@mindzie.com", TeamRole.Developer);
            var tokenSignedOut = Invite("sub-rig-tech", "docs@mindzie.com", TeamRole.Collaborator);
            Invite("sub-rig-qa", "contractor@example.org", TeamRole.Collaborator);
            var cancelled = registry.CreateInvitation(team, "sub-rig-qa", "old.address@example.org", TeamRole.Developer);
            Assert.Equal(TeamInvitationOutcome.Done, registry.CancelInvitation(team, cancelled.Invitation!.Id, "sub-rig-qa").Outcome);

            var rig = new
            {
                baseUrl = $"http://127.0.0.1:{gateway.Port}",
                teamId = team,
                owner = new { email = "qa@mindzie.com", key = keyOwner },
                manager = new { email = "tech@mindzie.com", key = keyManager },
                invitee = new { email = "dev@mindzie.com", key = keyInvitee, token = tokenSignedIn },
                newcomer = new { email = "docs@mindzie.com", key = keyNewcomer, token = tokenSignedOut },
                cancelledToken = cancelled.AcceptToken,
            };
            File.WriteAllText(Path.Combine(outDir, "rig.json"), JsonSerializer.Serialize(rig));

            var deadline = DateTime.UtcNow.AddMinutes(8);
            while (!File.Exists(stopFile) && DateTime.UtcNow < deadline)
                await Task.Delay(250);
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
