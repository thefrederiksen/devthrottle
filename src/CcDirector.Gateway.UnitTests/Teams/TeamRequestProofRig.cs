using System.Text.Json;
using CcDirector.Gateway.Teams;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// The rig the Requests screenshots in docs/proof/teams-2308 are taken from (devthrottle_internal#2308). NOT a test: it
/// starts a hosted Gateway with Teams released on 127.0.0.1, seeds a team of the fleet's test accounts with requests in
/// every state, writes their browser keys to a file, and serves the built Cockpit until a stop file appears.
///
/// SKIPPED unless <c>CC_TEAMS_2308_PROOF_RIG</c> names the directory to write <c>rig.json</c> into; the driver is
/// <c>docs/proof/teams-2308/take-screenshots.py</c>. The database is a scratch one deleted afterwards. It lives in this
/// suite, beside the invitations rig, for the same reason: Gateway.Tests holds a machine-wide lock for a whole run.
/// </summary>
public sealed class TeamRequestProofRig
{
    private const string RigEnvVar = "CC_TEAMS_2308_PROOF_RIG";
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
    public async Task ServeTheRequestScreens_UntilTheStopFileAppears()
    {
        var outDir = Environment.GetEnvironmentVariable(RigEnvVar)!;
        Directory.CreateDirectory(outDir);
        var stopFile = Path.Combine(outDir, "stop");
        if (File.Exists(stopFile)) File.Delete(stopFile);
        var root = Path.Combine(Path.GetTempPath(), "cc-team-req-rig-" + Guid.NewGuid().ToString("N"));

        var priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        var priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        var gateway = new GatewayHost(port: RigPort, token: "rig-token", authEnabled: true, instancesDirectory: root,
            workListsPath: Path.Combine(root, "worklists", "worklists.json"),
            snoozePath: Path.Combine(root, "snooze", "snooze.json"),
            streamMode: true, teamsReleased: true);
        try
        {
            Assert.True(Directory.Exists(Cockpit.CockpitReactApp.WebRoot),
                $"The built Cockpit is not beside the test binaries ({Cockpit.CockpitReactApp.WebRoot}). The driver copies apps/cockpit/dist there.");
            await gateway.StartAsync();

            // A person's own BROWSER key - the only kind of key the request routes serve.
            string Enroll(string deviceId, string subject, string email)
            {
                var tenant = gateway.TenantRegistry.MintOrLookupBySubject(subject, email);
                return gateway.Devices.RegisterForTenant(tenant, subject, deviceId, "RIG-" + deviceId, deviceType: "browser").DeviceKey;
            }

            // The fleet's own test accounts only.
            var keyOwner = Enroll("rig-req-owner", "sub-rig-req-qa", "qa@mindzie.com");
            var keyManager = Enroll("rig-req-manager", "sub-rig-req-tech", "tech@mindzie.com");
            var keyDeveloper = Enroll("rig-req-developer", "sub-rig-req-dev", "dev@mindzie.com");
            var keyCollaborator = Enroll("rig-req-collaborator", "sub-rig-req-docs", "docs@mindzie.com");

            var teams = gateway.TeamRegistry;
            var team = teams.CreateTeam("sub-rig-req-qa", "Acme QA").Team!.TeamId;
            Assert.True(teams.AddMember(team, "sub-rig-req-tech", TeamRole.Manager).IsDone);
            Assert.True(teams.AddMember(team, "sub-rig-req-dev", TeamRole.Developer).IsDone);
            Assert.True(teams.AddMember(team, "sub-rig-req-docs", TeamRole.Collaborator).IsDone);

            var requests = gateway.TeamRequests;
            string Send(string text) => requests.Send(team, "sub-rig-req-docs", text).Request!.Id;
            void Decide(string id, string by, TeamRequestDecision decision, string? reason = null) =>
                Assert.Equal(TeamRequestOutcome.Done, requests.Decide(team, id, by, decision, reason).Outcome);

            var dark = Send("Dark mode for the report viewer");
            Decide(dark, "sub-rig-req-tech", TeamRequestDecision.Decline, "Not this quarter: the viewer is being replaced in November, and the new one has it.");
            var csv = Send("Export the event log as CSV from the dashboard");
            Decide(csv, "sub-rig-req-tech", TeamRequestDecision.Accept);
            var title = Send("Put the client's name in the report title");
            Decide(title, "sub-rig-req-tech", TeamRequestDecision.Accept);
            Decide(title, "sub-rig-req-qa", TeamRequestDecision.MarkDone);
            Send("Send the weekly summary on Monday mornings instead of Friday afternoons");

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
}
