using System.Text.Json;
using CcDirector.Gateway.Teams;
using CcDirector.Core.Tenancy;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// The rig the Questions screenshots in docs/proof/teams-2307 were taken from (devthrottle_internal#2307). NOT a test: it
/// starts a hosted Gateway with Teams released on 127.0.0.1, seeds ONE team in which a Developer wrote a report with a
/// question and sent it to two Collaborators, writes the browsers' keys to a file, and serves the built Cockpit until a
/// stop file appears. The driver (<c>docs/proof/teams-2307/take-screenshots.py</c>) then answers the question over the
/// wire as each Collaborator - one at desktop width, one at phone width - and reads the comment as the author.
///
/// The report is seeded through the store with its author recorded, exactly as the publish route records it, because a
/// team session cannot publish over the wire yet (the access lease answers 402 for a team tenant; recorded in the pull
/// request). For the same reason the asking session is not live, so an answer is held for it - what the page says.
///
/// SKIPPED unless <c>CC_TEAMS_2307_PROOF_RIG</c> names the directory to write <c>rig.json</c> into. Nothing is emailed
/// and the database is a scratch one deleted afterwards. It lives in the unit suite for the reason
/// <see cref="TeamInvitationProofRig"/> gives.
/// </summary>
public sealed class TeamQuestionsProofRig
{
    private const string RigEnvVar = "CC_TEAMS_2307_PROOF_RIG";
    private const int RigPort = 7914;

    private sealed class RigFactAttribute : FactAttribute
    {
        public RigFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RigEnvVar)))
                Skip = $"Proof rig only. Set {RigEnvVar} to an output directory to run it.";
        }
    }

    // The mockup's question (S8).
    private const string ReportHtml =
        "<header data-dev-report=\"header\" data-dev-report-status=\"waiting-on-you\"><h1>Pricing</h1></header>" +
        "<section data-dev-report=\"summary\"><h2>Summary</h2><p>The signup page is being rewritten. The trial length on it " +
        "can change at the same time.</p></section>" +
        "<section data-dev-report=\"questions\"><h2>Questions</h2>" +
        "<div data-dev-report-question=\"trial-length\" data-dev-report-question-text=\"Should the trial be 14 days or 30?\">" +
        "<h3>Should the trial be 14 days or 30?</h3>" +
        "<label><input type=\"radio\" name=\"trial-length\" value=\"14\" data-recommended> 14 days - matches the page today</label>" +
        "<label><input type=\"radio\" name=\"trial-length\" value=\"30\"> 30 days - more time to reach a first session</label>" +
        "<textarea data-dev-report-comment placeholder=\"Anything to add (optional)\"></textarea></div></section>" +
        "<section data-dev-report=\"detail\"><h2>Detail</h2><p>The signup page says 14 days today.</p></section>";

    [RigFact]
    public async Task ServeTheQuestionsPage_UntilTheStopFileAppears()
    {
        var outDir = Environment.GetEnvironmentVariable(RigEnvVar)!;
        Directory.CreateDirectory(outDir);
        var stopFile = Path.Combine(outDir, "stop");
        if (File.Exists(stopFile)) File.Delete(stopFile);
        var root = Path.Combine(Path.GetTempPath(), "cc-team-questions-rig-" + Guid.NewGuid().ToString("N"));

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

            // The fleet's own test accounts only: qa owns the team, tech is the Developer who asked, docs and dev are the
            // Collaborators the report was sent to.
            gateway.TenantRegistry.MintOrLookupBySubject("sub-rig-qa", "qa@mindzie.com");
            var techKey = Browser(gateway, "tech", "sub-rig-tech", "tech@mindzie.com");
            var docsKey = Browser(gateway, "docs", "sub-rig-docs", "docs@mindzie.com");
            var devKey = Browser(gateway, "dev", "sub-rig-dev", "dev@mindzie.com");

            var registry = gateway.TeamRegistry;
            var team = registry.CreateTeam("sub-rig-qa", "DevThrottle").Team!.TeamId;
            Assert.True(registry.AddMember(team, "sub-rig-tech", TeamRole.Developer).IsDone);
            Assert.True(registry.AddMember(team, "sub-rig-docs", TeamRole.Collaborator).IsDone);
            Assert.True(registry.AddMember(team, "sub-rig-dev", TeamRole.Collaborator).IsDone);
            var teamTenant = new TenantId(team);

            var now = DateTime.UtcNow;
            var report = gateway.DevReportsForTest.Publish(teamTenant, Guid.NewGuid().ToString("D"), @"C:\work\pricing.html",
                ReportHtml, "waiting-on-you", "Pricing", now.AddMinutes(-40), "sub-rig-tech").Report.Id;
            gateway.DevReportRecipientsForTest.Send(teamTenant, report, "sub-rig-tech", new[] { "sub-rig-docs", "sub-rig-dev" }, 1, now.AddMinutes(-20));

            var rig = new
            {
                baseUrl = $"http://127.0.0.1:{gateway.Port}",
                team,
                reportId = report.ToString("D"),
                desktopCollaborator = new { email = "docs@mindzie.com", key = docsKey },
                phoneCollaborator = new { email = "dev@mindzie.com", key = devKey },
                author = new { email = "tech@mindzie.com", key = techKey },
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

    /// <summary>A BROWSER signed in to the person's own account, as a Cockpit sign-in records it - not a Director.</summary>
    private static string Browser(GatewayHost gateway, string name, string subject, string email)
    {
        var tenant = gateway.TenantRegistry.MintOrLookupBySubject(subject, email);
        var key = gateway.Devices.Register($"rig-{name}", $"RIG-{name}", platform: "browser",
            deviceType: Gateway.Account.MobileDeviceEnrollmentService.BrowserDeviceType).DeviceKey;
        gateway.Devices.SetAccountBinding($"rig-{name}", subject, tenant.Value);
        return key;
    }
}
