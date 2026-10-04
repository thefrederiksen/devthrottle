using System.Text.Json;
using CcDirector.Gateway.Teams;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// The rig the Collaborator's-app screenshots in docs/proof/teams-2306 were taken from (devthrottle_internal#2306). NOT a
/// test: it starts a hosted Gateway with Teams released on 127.0.0.1, seeds ONE account that is a Collaborator in one team
/// and a Developer in another, writes its device key to a file, and serves the built Cockpit until a stop file appears.
///
/// SKIPPED unless <c>CC_TEAMS_2306_PROOF_RIG</c> names the directory to write <c>rig.json</c> into; the driver is
/// <c>docs/proof/teams-2306/take-screenshots.py</c>. Nothing is emailed and the database is a scratch one deleted
/// afterwards. It lives in the unit suite for the reason <see cref="TeamInvitationProofRig"/> gives.
/// </summary>
public sealed class TeamCollaboratorAppProofRig
{
    private const string RigEnvVar = "CC_TEAMS_2306_PROOF_RIG";
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
    public async Task ServeTheCollaboratorsApp_UntilTheStopFileAppears()
    {
        var outDir = Environment.GetEnvironmentVariable(RigEnvVar)!;
        Directory.CreateDirectory(outDir);
        var stopFile = Path.Combine(outDir, "stop");
        if (File.Exists(stopFile)) File.Delete(stopFile);
        var root = Path.Combine(Path.GetTempPath(), "cc-team-collab-rig-" + Guid.NewGuid().ToString("N"));

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

            // The fleet's own test accounts only.
            gateway.TenantRegistry.MintOrLookupBySubject("sub-rig-qa", "qa@mindzie.com");
            gateway.TenantRegistry.MintOrLookupBySubject("sub-rig-tech", "tech@mindzie.com");
            var tenant = gateway.TenantRegistry.MintOrLookupBySubject("sub-rig-docs", "docs@mindzie.com");
            var key = gateway.Devices.Register("rig-docs", "RIG-docs").DeviceKey;
            gateway.Devices.SetAccountBinding("rig-docs", "sub-rig-docs", tenant.Value);

            var registry = gateway.TeamRegistry;
            var collaboratorTeam = registry.CreateTeam("sub-rig-qa", "DevThrottle").Team!.TeamId;
            Assert.True(registry.AddMember(collaboratorTeam, "sub-rig-tech", TeamRole.Manager).IsDone);
            Assert.True(registry.AddMember(collaboratorTeam, "sub-rig-docs", TeamRole.Collaborator).IsDone);
            var developerTeam = registry.CreateTeam("sub-rig-tech", "Paul's project").Team!.TeamId;
            Assert.True(registry.AddMember(developerTeam, "sub-rig-docs", TeamRole.Developer).IsDone);

            var rig = new
            {
                baseUrl = $"http://127.0.0.1:{gateway.Port}",
                collaboratorTeam,
                developerTeam,
                person = new { email = "docs@mindzie.com", key },
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
