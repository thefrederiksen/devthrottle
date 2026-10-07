using System.Text.Json;
using CcDirector.Core.Account;
using CcDirector.Gateway.Teams;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// The rig the Team page screenshots in docs/proof/teams-2303 were taken from (devthrottle_internal#2303). NOT a test:
/// it starts a hosted Gateway with Teams released on 127.0.0.1, seeds a billed team with one member in each role and a
/// waiting invitation, writes the members' device keys to a file, and serves the built Cockpit until a stop file
/// appears.
///
/// SKIPPED unless <c>CC_TEAMS_2303_PROOF_RIG</c> names the directory to write <c>rig.json</c> into; the driver is
/// <c>docs/proof/teams-2303/take-screenshots.py</c>. The invitation mailer records and never sends, the seat sync fails
/// closed without the service credential (the driver removes it), and the database is a scratch one deleted afterwards.
/// Lives in the unit project for the reason <see cref="TeamInvitationProofRig"/> gives: the Gateway suite's machine-wide
/// lock.
/// </summary>
public sealed class TeamPageProofRig
{
    private const string RigEnvVar = "CC_TEAMS_2303_PROOF_RIG";
    private const int RigPort = 7912;

    private sealed class RigFactAttribute : FactAttribute
    {
        public RigFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RigEnvVar)))
                Skip = $"Proof rig only. Set {RigEnvVar} to an output directory to run it.";
        }
    }

    [RigFact]
    public async Task ServeTheTeamPage_UntilTheStopFileAppears()
    {
        var outDir = Environment.GetEnvironmentVariable(RigEnvVar)!;
        Directory.CreateDirectory(outDir);
        var stopFile = Path.Combine(outDir, "stop");
        if (File.Exists(stopFile)) File.Delete(stopFile);
        var root = Path.Combine(Path.GetTempPath(), "cc-team-page-rig-" + Guid.NewGuid().ToString("N"));

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

            // The fleet's own test accounts, one per role, plus a second Collaborator at a client address. Nothing is
            // emailed to anyone: the mailer records.
            var keyOwner = Enroll("rig-owner", "sub-rig-qa", "qa@mindzie.com");
            var keyManager = Enroll("rig-manager", "sub-rig-tech", "tech@mindzie.com");
            var keyDeveloper = Enroll("rig-developer", "sub-rig-dev", "dev@mindzie.com");
            var keyCollaborator = Enroll("rig-collaborator", "sub-rig-docs", "docs@mindzie.com");
            gateway.TenantRegistry.MintOrLookupBySubject("sub-rig-client", "james@client.example");

            var registry = gateway.TeamRegistry;
            var team = registry.CreateTeam("sub-rig-qa", "Acme QA").Team!.TeamId;
            TeamBillSeed.Active(gateway.GatewayDatabaseForTests, team, seats: 3);
            Assert.True(registry.AddMember(team, "sub-rig-tech", TeamRole.Manager).IsDone);
            Assert.True(registry.AddMember(team, "sub-rig-dev", TeamRole.Developer).IsDone);
            Assert.True(registry.AddMember(team, "sub-rig-docs", TeamRole.Collaborator).IsDone);
            Assert.True(registry.AddMember(team, "sub-rig-client", TeamRole.Collaborator).IsDone);
            Assert.Equal(TeamInvitationOutcome.Done,
                registry.CreateInvitation(team, "sub-rig-tech", "new.hire@example.org", TeamRole.Developer).Outcome);

            var rig = new
            {
                baseUrl = $"http://127.0.0.1:{gateway.Port}",
                teamId = team,
                owner = new { email = "qa@mindzie.com", key = keyOwner },
                manager = new { email = "tech@mindzie.com", key = keyManager },
                developer = new { email = "dev@mindzie.com", key = keyDeveloper },
                collaborator = new { email = "docs@mindzie.com", key = keyCollaborator },
            };
            File.WriteAllText(Path.Combine(outDir, "rig.json"), JsonSerializer.Serialize(rig));

            var deadline = DateTime.UtcNow.AddMinutes(8);
            while (!File.Exists(stopFile) && DateTime.UtcNow < deadline)
                await Task.Delay(250);

            // What the screenshots changed, read back from the server, for the driver to check.
            File.WriteAllText(Path.Combine(outDir, "after.json"), JsonSerializer.Serialize(
                registry.ListMembers(team, "sub-rig-qa").Members.Select(m => new { m.Email, Role = m.Role.ToString() })));
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
