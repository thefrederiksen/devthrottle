using System.Text.Json;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Teams.Mentor;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams.Mentor;

/// <summary>
/// The rig the screenshots in docs/proof/mentor-reports-personal-2026-10-08 were taken from (owner, 8 Oct 2026: Mentor
/// and Reports in Work, for everyone). NOT a test: it starts a hosted Gateway with Teams released on 127.0.0.1, seeds ONE
/// account with a Mentor page on its own account, two dev reports its own sessions sent it, and a team it owns, writes
/// its device key to a file, and serves the built Cockpit until a stop file appears.
///
/// SKIPPED unless <c>CC_MENTOR_PERSONAL_PROOF_RIG</c> names the directory to write <c>rig.json</c> into; the driver is
/// <c>docs/proof/mentor-reports-personal-2026-10-08/take-screenshots.py</c>. Nothing is emailed and the database is a
/// scratch one deleted afterwards.
/// </summary>
public sealed class PersonalMentorReportsProofRig
{
    private const string RigEnvVar = "CC_MENTOR_PERSONAL_PROOF_RIG";
    private const int RigPort = 7913;

    private sealed class RigFactAttribute : FactAttribute
    {
        public RigFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RigEnvVar)))
                Skip = $"Proof rig only. Set {RigEnvVar} to an output directory to run it.";
        }
    }

    [RigFact]
    public async Task ServeMentorAndReportsOnTheOwnAccount_UntilTheStopFileAppears()
    {
        var outDir = Environment.GetEnvironmentVariable(RigEnvVar)!;
        Directory.CreateDirectory(outDir);
        var stopFile = Path.Combine(outDir, "stop");
        if (File.Exists(stopFile)) File.Delete(stopFile);
        var root = Path.Combine(Path.GetTempPath(), "cc-mentor-personal-rig-" + Guid.NewGuid().ToString("N"));

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

            // The fleet's own test account only.
            const string subject = "sub-rig-docs";
            var own = gateway.TenantRegistry.MintOrLookupBySubject(subject, "docs@mindzie.com");
            var key = gateway.Devices.Register("rig-docs", "RIG-docs", platform: "browser",
                deviceType: Gateway.Account.MobileDeviceEnrollmentService.BrowserDeviceType).DeviceKey;
            gateway.Devices.SetAccountBinding("rig-docs", subject, own.Value);

            // The person's own Mentor page for the last closed week - the words of Screen 1 of the approved mockups.
            var now = DateTime.UtcNow;
            var week = MentorWeek.LastClosed(now, TimeZoneInfo.Utc);
            gateway.TeamMentorStore.SaveBlock(own, new MentorBlock(week.ToString(), subject, MentorTones.Mixed,
                "41 sessions on 6 repositories. Most time: the Teams first version (18 sessions), the website (9), the Reddit tool (6).",
                "23 pull requests merged, median 3 hours from start to merge. 5 sessions stopped on a usage limit and waited 40 minutes on average.",
                "On Tuesday you restarted the Cockpit menu task three times. The first instruction did not say which file holds the menu, so each session searched for it again. The fourth attempt named the file and the sections and finished in 40 minutes.",
                new[] { new MentorQuote("p-rig-1", week.UtcBounds(TimeZoneInfo.Utc).FromUtc.AddDays(1).AddHours(9), "fix the menu, it's too long") },
                "Start a task by naming the file or page and what \"done\" looks like. Your sessions that did this finished 2.4 times faster this week.",
                now, "rig"));

            // Two dev reports the person's own sessions sent them.
            var reports = gateway.DevReportsForTest;
            reports.Publish(own, Guid.NewGuid().ToString("D"), @"C:\work\invoice-export.html", Report("Invoice export is ready to try"),
                "waiting-on-you", "Invoice export is ready to try", now.AddHours(-3));
            reports.Publish(own, Guid.NewGuid().ToString("D"), @"C:\work\release.html", Report("Release 2.18 checklist passed"),
                "done", "Release 2.18 checklist passed", now.AddDays(-2));

            // A team the person owns, so the menu can be shown with a team on screen.
            var teamId = gateway.TeamRegistry.CreateTeam(subject, "DevThrottle").Team!.TeamId;

            var rig = new
            {
                baseUrl = $"http://127.0.0.1:{gateway.Port}",
                teamId,
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

    private static string Report(string title) =>
        $"<header data-dev-report=\"header\" data-dev-report-status=\"waiting-on-you\"><h1>{title}</h1></header>" +
        "<section data-dev-report=\"summary\"><p>Ready for you to read.</p></section>";
}
