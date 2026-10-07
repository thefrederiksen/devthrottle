using System.Text.Json;
using CcDirector.Core.Account;
using CcDirector.Gateway.Teams;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// The rig the screenshots in docs/proof/teams-copy-invitation-link were taken from (Teams v1, the Owner can copy an
/// invitation link). NOT a test: it starts a hosted Gateway with Teams released on 127.0.0.1, whose public address is the
/// rig's own address so the copied link really opens; seeds a team with a running plan and an Owner; enrolls a second
/// account with no team; writes both device keys to a file; and serves the built Cockpit until a stop file appears. It
/// then writes who the team's members are, so the driver can check the copied link made the newcomer one.
///
/// SKIPPED unless <c>CC_TEAMS_LINK_PROOF_RIG</c> names the directory to write <c>rig.json</c> into; the driver is
/// <c>docs/proof/teams-copy-invitation-link/take-screenshots.py</c>. Every address is at example.org, the invitation
/// mailer records and never sends, and the database is a scratch one deleted afterwards.
/// </summary>
public sealed class TeamInviteLinkProofRig
{
    private const string RigEnvVar = "CC_TEAMS_LINK_PROOF_RIG";
    private const int RigPort = 7915;

    private sealed class RigFactAttribute : FactAttribute
    {
        public RigFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RigEnvVar)))
                Skip = $"Proof rig only. Set {RigEnvVar} to an output directory to run it.";
        }
    }

    [RigFact]
    public async Task ServeTheInviteFlow_UntilTheStopFileAppears()
    {
        var outDir = Environment.GetEnvironmentVariable(RigEnvVar)!;
        Directory.CreateDirectory(outDir);
        var stopFile = Path.Combine(outDir, "stop");
        if (File.Exists(stopFile)) File.Delete(stopFile);
        var root = Path.Combine(Path.GetTempPath(), "cc-team-link-rig-" + Guid.NewGuid().ToString("N"));

        var priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        var priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        var priorPublicUrl = Environment.GetEnvironmentVariable(GatewayPublicUrl.PublicBaseUrlEnvVar);
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        Environment.SetEnvironmentVariable(GatewayPublicUrl.PublicBaseUrlEnvVar, $"http://127.0.0.1:{RigPort}");
        var gateway = new GatewayHost(port: RigPort, token: "rig-token", authEnabled: true, instancesDirectory: root,
            workListsPath: Path.Combine(root, "worklists", "worklists.json"),
            snoozePath: Path.Combine(root, "snooze", "snooze.json"),
            streamMode: true, teamsReleased: true);
        // Not sent: the website's sender is not live yet, which is exactly why the link is shown.
        gateway.TeamInvitationMailer = new NotSentMailer();
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

            var keyOwner = Enroll("rig-link-owner", "sub-link-owner", "owner@example.org");
            var keyNewcomer = Enroll("rig-link-newcomer", "sub-link-newcomer", "newcomer@example.org");

            var team = gateway.TeamRegistry.CreateTeam("sub-link-owner", "Acme").Team!.TeamId;
            TeamBillSeed.Active(gateway.GatewayDatabaseForTests, team, seats: 1);

            File.WriteAllText(Path.Combine(outDir, "rig.json"), JsonSerializer.Serialize(new
            {
                baseUrl = $"http://127.0.0.1:{gateway.Port}",
                teamId = team,
                owner = new { email = "owner@example.org", key = keyOwner },
                newcomer = new { email = "newcomer@example.org", key = keyNewcomer },
            }));

            var deadline = DateTime.UtcNow.AddMinutes(8);
            while (!File.Exists(stopFile) && DateTime.UtcNow < deadline)
                await Task.Delay(250);

            File.WriteAllText(Path.Combine(outDir, "after.json"), JsonSerializer.Serialize(
                gateway.TeamRegistry.ListMembers(team, "sub-link-owner").Members.Select(m => new { m.Email, Role = m.Role.ToString() })));
        }
        finally
        {
            await gateway.StopAsync();
            Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", priorHosted);
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", priorRoot);
            Environment.SetEnvironmentVariable(GatewayPublicUrl.PublicBaseUrlEnvVar, priorPublicUrl);
            try { if (Directory.Exists(root)) Directory.Delete(root, true); }
            catch (IOException) { /* a file still held by the stopped host; the temp directory is left behind */ }
        }
    }

    private sealed class NotSentMailer : ITeamInvitationMailer
    {
        public Task<TeamInvitationMailResult> SendAsync(string invitationId, string teamId, string acceptToken, CancellationToken ct = default) =>
            Task.FromResult(new TeamInvitationMailResult(false, "The email service is not set up yet.", 503, null));
    }
}
