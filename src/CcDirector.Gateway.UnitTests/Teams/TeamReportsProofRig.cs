using System.Text.Json;
using CcDirector.Gateway.Teams;
using CcDirector.Core.Tenancy;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// The rig the team Reports screenshots in docs/proof/teams-2309 were taken from (devthrottle_internal#2309). NOT a test:
/// it starts a hosted Gateway with Teams released on 127.0.0.1, seeds ONE team in which a Developer wrote a report, sent
/// it to a Collaborator, and the Collaborator commented on it, writes both browsers' keys to a file, and serves the built
/// Cockpit until a stop file appears.
///
/// The report is seeded through the store with its author recorded, exactly as the publish route records it, because a
/// team session cannot publish over the wire yet (the access lease answers 402 for a team tenant; recorded in the pull
/// request). Everything the browsers do after that - the list, opening, reading, the comments - goes over the wire.
///
/// SKIPPED unless <c>CC_TEAMS_2309_PROOF_RIG</c> names the directory to write <c>rig.json</c> into; the driver is
/// <c>docs/proof/teams-2309/take-screenshots.py</c>. Nothing is emailed and the database is a scratch one deleted
/// afterwards. It lives in the unit suite for the reason <see cref="TeamInvitationProofRig"/> gives.
/// </summary>
public sealed class TeamReportsProofRig
{
    private const string RigEnvVar = "CC_TEAMS_2309_PROOF_RIG";
    private const int RigPort = 7913;

    private sealed class RigFactAttribute : FactAttribute
    {
        public RigFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RigEnvVar)))
                Skip = $"Proof rig only. Set {RigEnvVar} to an output directory to run it.";
        }
    }

    private const string ReportHtml =
        "<header data-dev-report=\"header\" data-dev-report-status=\"waiting-on-you\"><h1>Signup page rewrite</h1></header>" +
        "<section data-dev-report=\"summary\"><h2>Summary</h2><p>The signup page now asks for an email address only. " +
        "The password is set from the first email, so a new person is in the product in one step instead of four.</p></section>" +
        "<section data-dev-report=\"questions\"><h2>Questions</h2>" +
        // A real question, so the screenshots show what a team reader gets: the report's own markup and no Queue button
        // or notes tray, because the Gateway says notes are off for them (review F2).
        "<div data-dev-report-question=\"old-page\" data-dev-report-question-text=\"Keep the old signup page for a week?\">" +
        "<h3>Keep the old signup page for a week?</h3>" +
        "<label><input type=\"radio\" name=\"old-page\" value=\"keep\" data-recommended> Keep it for a week</label>" +
        "<label><input type=\"radio\" name=\"old-page\" value=\"remove\"> Remove it now</label></div></section>" +
        "<section data-dev-report=\"detail\"><h2>Detail</h2><p>Measured on the test site: 41 of 50 trial signups finished, " +
        "against 22 of 50 on the old page.</p></section>";

    [RigFact]
    public async Task ServeTheTeamReportsPage_UntilTheStopFileAppears()
    {
        var outDir = Environment.GetEnvironmentVariable(RigEnvVar)!;
        Directory.CreateDirectory(outDir);
        var stopFile = Path.Combine(outDir, "stop");
        if (File.Exists(stopFile)) File.Delete(stopFile);
        var root = Path.Combine(Path.GetTempPath(), "cc-team-reports-rig-" + Guid.NewGuid().ToString("N"));

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

            // The fleet's own test accounts only: qa owns the team, tech is the Developer who wrote the report, docs is the
            // Collaborator it was sent to.
            gateway.TenantRegistry.MintOrLookupBySubject("sub-rig-qa", "qa@mindzie.com");
            var techKey = Browser(gateway, "tech", "sub-rig-tech", "tech@mindzie.com");
            var docsKey = Browser(gateway, "docs", "sub-rig-docs", "docs@mindzie.com");

            var registry = gateway.TeamRegistry;
            var team = registry.CreateTeam("sub-rig-qa", "DevThrottle").Team!.TeamId;
            Assert.True(registry.AddMember(team, "sub-rig-tech", TeamRole.Developer).IsDone);
            Assert.True(registry.AddMember(team, "sub-rig-docs", TeamRole.Collaborator).IsDone);
            var teamTenant = new TenantId(team);

            var now = DateTime.UtcNow;
            var report = gateway.DevReportsForTest.Publish(teamTenant, Guid.NewGuid().ToString("D"), @"C:\work\signup-rewrite.html",
                ReportHtml, "waiting-on-you", "Signup page rewrite", now.AddHours(-3), "sub-rig-tech").Report.Id;
            // A second, older report that went to nobody, so the author's list shows both states.
            gateway.DevReportsForTest.Publish(teamTenant, Guid.NewGuid().ToString("D"), @"C:\work\billing-notes.html",
                ReportHtml.Replace("Signup page rewrite", "Billing page notes"), "done", "Billing page notes", now.AddDays(-2), "sub-rig-tech");
            gateway.DevReportRecipientsForTest.Send(teamTenant, report, "sub-rig-tech", new[] { "sub-rig-docs" }, 1, now.AddHours(-2));
            gateway.DevReportRecipientsForTest.MarkRead(teamTenant, report, "sub-rig-docs", now.AddHours(-1));
            gateway.DevReportCommentsForTest.Add(teamTenant, report, "sub-rig-docs", "sub-rig-tech",
                "Looks good. Can we keep the old page for a week for anyone who has it bookmarked?", now.AddMinutes(-30));

            var rig = new
            {
                baseUrl = $"http://127.0.0.1:{gateway.Port}",
                team,
                reportId = report.ToString("D"),
                collaborator = new { email = "docs@mindzie.com", key = docsKey },
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
