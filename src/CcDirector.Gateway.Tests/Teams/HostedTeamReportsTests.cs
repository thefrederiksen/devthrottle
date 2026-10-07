using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Teams;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// A dev report sent to a member of the team (devthrottle_internal#2309), END TO END through a REAL hosted
/// <see cref="GatewayHost"/> with Teams released, over REAL HTTP with each person's own account key, through the real auth
/// middleware and the team gate the host installed.
///
/// The team: the Owner, Alice and Bob (Developers), Mike and Nina (Collaborators). Alice writes the reports.
///
/// WHERE THE REPORT COMES FROM. A report in a team's tenant is published by a session on a Director set up for the team,
/// and the team pays (a live team bill, set up below), so that session reaches the Gateway over the wire: the access lease
/// reads the TEAM's bill (devthrottle_internal#2311 step 2, #3552). The publish itself is proven over the wire by
/// <see cref="Issue2309_TeamKeyVariant_ATeamSessionPublishesOverTheWire_AuthoredByItsDirectorsPerson_AndAColleagueCannot"/>.
/// The other tests write the team's report through the store the publish route writes with, carrying the author the
/// publish route records, and every person-facing step after that is over the wire.
///
/// WHERE "NEVER REACHES AN AGENT" IS PROVEN WITH A LIVE SESSION. For the same reason, a live session on the tunnel can
/// only be a personal account's today. <see cref="Issue2309_ACommentNeverReachesTheSession_LiveSessionOnTheTunnel"/> runs
/// the real delivery - a held owner note, the settle pass and a real turn end, through a Director on the tunnel - with a
/// person's comment stored on the same report, and reads every prompt the Director received and every read the session's
/// own key can make. The team half is <see cref="Issue2309_ACollaboratorsComment_ReachesTheAuthor_OverTheWire"/>.
///
/// PARKED SUITE. Gateway.Tests serializes machine-wide and does not run in the default gate.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class HostedTeamReportsTests : IAsyncLifetime
{
    private const string Token = "team-reports-token";
    private readonly ITestOutputHelper _out;
    private readonly string _run = Guid.NewGuid().ToString("N")[..10];
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-team-reports-" + Guid.NewGuid().ToString("N"));
    private string _owner = "", _alice = "", _bob = "", _mike = "", _nina = "", _stranger = "";
    private string _ownerKey = "", _aliceKey = "", _bobKey = "", _mikeKey = "", _ninaKey = "", _strangerKey = "";
    private TenantId _aliceHome;
    private string _team = "";
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string? _priorHosted;
    private string? _priorRoot;

    public HostedTeamReportsTests(ITestOutputHelper output) => _out = output;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _instancesDir);

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true, teamsReleased: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/"), Timeout = TimeSpan.FromMinutes(2) };

        (_owner, _ownerKey) = Person("owner");
        (_alice, _aliceKey) = Person("alice");
        (_bob, _bobKey) = Person("bob");
        (_mike, _mikeKey) = Person("mike");
        (_nina, _ninaKey) = Person("nina");
        (_stranger, _strangerKey) = Person("stranger");
        _aliceHome = _gateway.TenantRegistry.LookupBySubject(_alice)!.Value;

        _team = _gateway.TeamRegistry.CreateTeam(_owner, "Acme").Team!.TeamId;
        Assert.True(_gateway.TeamRegistry.AddMember(_team, _alice, TeamRole.Developer).IsDone);
        Assert.True(_gateway.TeamRegistry.AddMember(_team, _bob, TeamRole.Developer).IsDone);
        Assert.True(_gateway.TeamRegistry.AddMember(_team, _mike, TeamRole.Collaborator).IsDone);
        Assert.True(_gateway.TeamRegistry.AddMember(_team, _nina, TeamRole.Collaborator).IsDone);
        // The team pays: a team key's access is read from the team's bill (devthrottle_internal#2311 step 2).
        HostedTeamBill.Start(_gateway, _team, seats: 5);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    /// <summary>A person with their own account and a browser signed in to it - the key the Cockpit holds.</summary>
    private (string Subject, string Key) Person(string name)
    {
        var subject = $"sub-tr-{name}-{_run}";
        var device = HostedTestEnrollment.Enroll(_gateway, subject, $"{name}@example.com", $"browser-{name}-{_run}", "Browser");
        return (subject, device.DeviceKey);
    }

    /// <summary>A report written in the team by a session of Alice's, as the publish route writes it.</summary>
    private Guid TeamReport(string title = "Signup page rewrite", string? sessionId = null) =>
        _gateway.DevReportsForTest.Publish(new TenantId(_team), sessionId ?? Guid.NewGuid().ToString("D"), $@"C:\work\{Guid.NewGuid():N}.html",
            Html(title), "waiting-on-you", title, DateTime.UtcNow, _alice).Report.Id;

    private static string Html(string title) =>
        $"<header data-dev-report=\"header\" data-dev-report-status=\"waiting-on-you\"><h1>{title}</h1></header>" +
        "<section data-dev-report=\"summary\"><p>What changed.</p></section>" +
        "<section data-dev-report=\"questions\"><p data-dev-report-no-questions>No questions.</p></section>" +
        "<section data-dev-report=\"detail\"><p>The detail.</p></section>";

    private string MemberId(string subject) => TeamMemberIds.For(_team, subject);

    private async Task<(HttpStatusCode Status, string Text)> Call(HttpMethod method, string path, string key, object? body = null)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (body is not null) req.Content = JsonContent.Create(body);
        using var resp = await _http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"{method} {path} -> {(int)resp.StatusCode}: {(text.Length > 300 ? text[..300] + "..." : text)}");
        return (resp.StatusCode, text);
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private async Task SendTo(Guid report, params string[] subjects)
    {
        var (status, _) = await Call(HttpMethod.Post, $"teams/{_team}/reports/mine/{report}/recipients", _aliceKey,
            new { memberIds = subjects.Select(MemberId).ToArray(), version = 1 });
        Assert.Equal(HttpStatusCode.OK, status);
    }

    private async Task<string[]> SentToMe(string key)
    {
        var (status, text) = await Call(HttpMethod.Get, $"teams/{_team}/reports/sent-to-me", key);
        Assert.Equal(HttpStatusCode.OK, status);
        return Json(text).GetProperty("reports").EnumerateArray().Select(r => r.GetProperty("id").GetString()!).ToArray();
    }

    // ---- the issue's three tests --------------------------------------------------------------------------------

    [Fact]
    public async Task Issue2309_OverTheWire_AReportSentToMike_AppearsForMike_AndForNoOtherCollaborator()
    {
        var report = TeamReport();

        // Alice picks Mike from the choices the Gateway offers her, as her screen does.
        var (_, before) = await Call(HttpMethod.Get, $"teams/{_team}/reports/mine/{report}", _aliceKey);
        var mikeChoice = Json(before).GetProperty("choices").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "mike@example.com").GetProperty("memberId").GetString()!;
        var (sent, _) = await Call(HttpMethod.Post, $"teams/{_team}/reports/mine/{report}/recipients", _aliceKey, new { memberIds = new[] { mikeChoice }, version = 1 });
        Assert.Equal(HttpStatusCode.OK, sent);

        Assert.Equal(new[] { report.ToString("D") }, await SentToMe(_mikeKey));
        Assert.Empty(await SentToMe(_ninaKey));
        Assert.Empty(await SentToMe(_bobKey));
        var (_, mike) = await Call(HttpMethod.Get, $"teams/{_team}/reports/sent-to-me", _mikeKey);
        var row = Json(mike).GetProperty("reports")[0];
        Assert.Equal("alice@example.com", row.GetProperty("from").GetString());
        Assert.Equal("New", row.GetProperty("readLabel").GetString());
        Assert.False(Json(mike).GetProperty("showYourReports").GetBoolean());
    }

    /// <summary>
    /// THE VERSION RULE ON THE ROUTE (delta review D3), not only on the class behind it: a version published after the
    /// send is not what the recipient's html route serves, and asking for it by number is refused with the Gateway's
    /// sentence. And the author's page, still showing version 1, cannot send the newer one it has not shown (D2).
    /// </summary>
    [Fact]
    public async Task Issue2309_D3_OverTheWire_AVersionPublishedAfterTheSend_IsNotServedToTheRecipient_AndASendOfTheOlderOneIsRefused()
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var key = $@"C:\work\{Guid.NewGuid():N}.html";
        var report = _gateway.DevReportsForTest.Publish(new TenantId(_team), sessionId, key, Html("First version"), "waiting-on-you",
            "First version", DateTime.UtcNow, _alice).Report.Id;
        await SendTo(report, _mike);
        var second = _gateway.DevReportsForTest.Publish(new TenantId(_team), sessionId, key, Html("Second version"), "done",
            "Second version", DateTime.UtcNow, _alice).Report;
        Assert.Equal(2, second.Version);

        // No query: the version Mike was sent, its bytes and its header.
        using (var req = new HttpRequestMessage(HttpMethod.Get, $"teams/{_team}/reports/sent-to-me/{report}/html"))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _mikeKey);
            using var resp = await _http.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("1", resp.Headers.GetValues("X-Dev-Report-Version").Single());
            Assert.Equal(Html("First version"), await resp.Content.ReadAsStringAsync());
        }
        // Asked for by number: refused with the Gateway's own sentence, and none of the newer bytes.
        var (status, text) = await Call(HttpMethod.Get, $"teams/{_team}/reports/sent-to-me/{report}/html?version=2", _mikeKey);
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal(CcDirector.Gateway.Api.TeamReportEndpoints.VersionNotSentToYou, Json(text).GetProperty("error").GetString());
        Assert.DoesNotContain("Second version", text);

        // Alice's page still shows version 1: sending that is refused, because it is no longer the newest, and Mike
        // still holds version 1.
        var (refused, why) = await Call(HttpMethod.Post, $"teams/{_team}/reports/mine/{report}/recipients", _aliceKey,
            new { memberIds = new[] { MemberId(_nina) }, version = 1 });
        Assert.Equal(HttpStatusCode.Conflict, refused);
        Assert.Equal("version_not_newest", Json(why).GetProperty("code").GetString());
        Assert.Equal(CcDirector.Gateway.Api.TeamReportEndpoints.NotTheNewestVersion, Json(why).GetProperty("error").GetString());
        Assert.DoesNotContain(report.ToString("D"), await SentToMe(_ninaKey));
    }

    [Fact]
    public async Task Issue2309_OverTheWire_AReportNotSentToACollaborator_IsRefused_OnTheListTheMetadataAndTheHtml()
    {
        var report = TeamReport();
        await SendTo(report, _mike);

        // POSITIVE CONTROL: Mike, who it was sent to, reads all three - the html as the exact bytes, with its version.
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, $"teams/{_team}/reports/sent-to-me/{report}", _mikeKey)).Status);
        using (var req = new HttpRequestMessage(HttpMethod.Get, $"teams/{_team}/reports/sent-to-me/{report}/html"))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _mikeKey);
            using var resp = await _http.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("text/plain", resp.Content.Headers.ContentType!.MediaType);
            Assert.Equal("1", resp.Headers.GetValues("X-Dev-Report-Version").Single());
            Assert.Equal("nosniff", resp.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal(Html("Signup page rewrite"), await resp.Content.ReadAsStringAsync());
        }

        // Nina asks for it directly, every way there is.
        Assert.DoesNotContain(report.ToString("D"), await SentToMe(_ninaKey));
        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Get, $"teams/{_team}/reports/sent-to-me/{report}"),
                     (HttpMethod.Get, $"teams/{_team}/reports/sent-to-me/{report}/html"),
                     (HttpMethod.Get, $"teams/{_team}/reports/sent-to-me/{report}/html?version=1"),
                     (HttpMethod.Post, $"teams/{_team}/reports/sent-to-me/{report}/read"),
                 })
        {
            var (status, text) = await Call(method, path, _ninaKey, method == HttpMethod.Post ? new { version = 1 } : null);
            Assert.Equal(HttpStatusCode.NotFound, status);
            Assert.DoesNotContain("Signup page rewrite", text);
        }
        // The author's routes are the gate's refusal for her: a Collaborator has no reports of her own.
        foreach (var path in new[] { $"teams/{_team}/reports/mine/{report}", $"teams/{_team}/reports/mine/{report}/html", $"teams/{_team}/reports/mine" })
        {
            var (status, text) = await Call(HttpMethod.Get, path, _ninaKey);
            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.Equal(TeamEndpointGate.RefusalCode, Json(text).GetProperty("code").GetString());
        }
        // And the owner's dev report routes read her own account, where the team's report does not exist.
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"dev-reports/{report}", _ninaKey)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"dev-reports/{report}/html", _ninaKey)).Status);
    }

    [Fact]
    public async Task Issue2309_ACollaboratorsComment_ReachesTheAuthor_OverTheWire()
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var report = TeamReport(sessionId: sessionId);
        await SendTo(report, _mike);
        var marker = "MARKER-2309-TEAM-" + Guid.NewGuid().ToString("N");

        var (posted, _) = await Call(HttpMethod.Post, $"teams/{_team}/reports/sent-to-me/{report}/comments", _mikeKey, new { text = marker });
        Assert.Equal(HttpStatusCode.OK, posted);

        // PRESENT in what the author person reads, from their own account, with who wrote it.
        var (_, mine) = await Call(HttpMethod.Get, $"teams/{_team}/reports/mine/{report}", _aliceKey);
        var comment = Assert.Single(Json(mine).GetProperty("comments").EnumerateArray());
        Assert.Equal(marker, comment.GetProperty("text").GetString());
        Assert.Equal("mike@example.com", comment.GetProperty("from").GetString());
        var (_, list) = await Call(HttpMethod.Get, $"teams/{_team}/reports/mine", _aliceKey);
        Assert.Equal("1 comment", Json(list).GetProperty("reports")[0].GetProperty("commentsLabel").GetString());
        // ...and in the writer's own view, and nowhere a third member can read.
        Assert.Contains(marker, (await Call(HttpMethod.Get, $"teams/{_team}/reports/sent-to-me/{report}", _mikeKey)).Text);
        Assert.DoesNotContain(marker, (await Call(HttpMethod.Get, $"teams/{_team}/reports/mine/{report}", _bobKey)).Text);

        // ABSENT from everything a session acts on: the settle pass finds nothing to hand the session, and the report
        // carries no item and no reply - the agent's conversation is untouched.
        var team = new TenantId(_team);
        await _gateway.DevReportDeliveryForTest.SettleAsync(team, sessionId, CancellationToken.None);
        Assert.Empty(_gateway.DevReportsForTest.Items(team, report));
        Assert.Empty(_gateway.DevReportsForTest.Replies(team, report));
        Assert.Empty(_gateway.DevReportsForTest.SessionsWithOpenItems(team));

        // ABSENT from every read a session key can make. The team pays, so the session's key reaches the routes: Alice's
        // team Director is connected (so the key's Director names Alice) and registers the session's key through the hub.
        // The session's own two routes answer 200 - the report is there, by its title, the positive control - and the
        // marker is absent from both. The person-facing team routes and the owner's route refuse a session key outright.
        var aliceDirectorKey = _gateway.Devices.RegisterForTenant(team, _alice,
            CcDirector.Gateway.Api.HostedEnrollmentEndpoint.TeamScopedDeviceId(_team, _alice, "director-alice-team"), "M-alice").DeviceKey;
        await using var aliceDirector = await FakeTunnelDirector.StartAsync(_gateway, aliceDirectorKey, "director-alice-team");
        var sessionKey = GatewaySessionKey.Mint();
        await aliceDirector.RegisterSessionKeyAsync(sessionId, sessionKey, DateTime.UtcNow.AddHours(1));
        foreach (var path in new[] { $"sessions/{sessionId}/dev-reports", $"sessions/{sessionId}/dev-reports/{report}" })
        {
            var (status, text) = await Call(HttpMethod.Get, path, sessionKey);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Contains("Signup page rewrite", text);
            Assert.DoesNotContain(marker, text);
        }
        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Get, $"teams/{_team}/reports/mine/{report}"),
                     (HttpMethod.Get, $"teams/{_team}/reports/sent-to-me/{report}"),
                     (HttpMethod.Get, $"dev-reports/{report}"),
                 })
        {
            var (status, text) = await Call(method, path, sessionKey);
            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.DoesNotContain(marker, text);
        }
    }

    [Fact]
    public async Task Issue2309_ACommentNeverReachesTheSession_LiveSessionOnTheTunnel()
    {
        // A live session on a Director on the tunnel (a personal account's, the only kind that can run over the wire
        // today), its report published through the real route with its own session key.
        const string directorId = "director-alice-home";
        var sessionId = Guid.NewGuid().ToString("D");
        var sessionKey = GatewaySessionKey.Mint();
        Assert.True(_gateway.SessionKeys.Register(_aliceHome, directorId, sessionId, GatewaySessionKey.Hash(sessionKey), DateTime.UtcNow.AddHours(1)));
        var prompts = new ConcurrentQueue<string>();
        await using var director = await FakeTunnelDirector.StartAsync(_gateway, _aliceKey, directorId, "M-alice", cmd =>
        {
            // EVERY command toward the Director is recorded whole, whatever its verb.
            prompts.Enqueue(cmd.Verb + " " + cmd.PayloadJson);
            return cmd.Verb == "prompt"
                ? FakeTunnelDirector.Ok(new PromptResponse { Accepted = true, SentAt = DateTime.UtcNow, ActivityState = "Working" })
                : DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"not served in this test: {cmd.Verb}");
        });
        await director.PushDeltaAsync(new SessionDto { SessionId = sessionId, Name = "alice", ActivityState = "Working", LastActivityAt = DateTime.UtcNow });
        var (published, publishedText) = await Call(HttpMethod.Post, $"sessions/{sessionId}/dev-reports", sessionKey,
            new { key = @"C:\work\live.html", html = Html("Live report") });
        Assert.Equal(HttpStatusCode.OK, published);
        var report = Guid.Parse(Json(publishedText).GetProperty("report").GetProperty("id").GetString()!);

        // A person's comment on that report, stored exactly as the comment route stores it, carrying the marker.
        var marker = "MARKER-2309-LIVE-" + Guid.NewGuid().ToString("N");
        _gateway.DevReportCommentsForTest.Add(_aliceHome, report, _mike, _alice, marker, DateTime.UtcNow);

        // POSITIVE CONTROL: the owner's own note, held while the session works, is what the session IS sent.
        var control = "CONTROL-2309-" + Guid.NewGuid().ToString("N");
        var (sent, _) = await Call(HttpMethod.Post, $"dev-reports/{report}/send", _aliceKey, new
        {
            items = new object[] { new { id = "n-" + Guid.NewGuid().ToString("N"), kind = "note", text = control,
                anchor = new { type = "text", selector = "p", quote = "What changed." } } },
        });
        Assert.Equal(HttpStatusCode.OK, sent);

        // The settle pass, then the turn end through the push that carries it.
        await _gateway.DevReportDeliveryForTest.SettleAsync(_aliceHome, sessionId, CancellationToken.None);
        await director.PushDeltaAsync(new SessionDto { SessionId = sessionId, Name = "alice", ActivityState = "WaitingForInput", LastActivityAt = DateTime.UtcNow });
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!prompts.Any(p => p.Contains(control, StringComparison.Ordinal)))
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("The owner's note never reached the session - the harness would prove nothing.");
            await Task.Delay(50);
        }
        await Task.Delay(1500); // anything else that would be sent has had its chance
        await _gateway.DevReportDeliveryForTest.SettleAsync(_aliceHome, sessionId, CancellationToken.None);

        _out.WriteLine($"{prompts.Count} command(s) reached the Director.");
        Assert.All(prompts, p => Assert.DoesNotContain(marker, p));

        // Every read the session's own key can make: its reports, and the report with its items and replies.
        foreach (var path in new[] { $"sessions/{sessionId}/dev-reports", $"sessions/{sessionId}/dev-reports/{report}" })
        {
            var (status, text) = await Call(HttpMethod.Get, path, sessionKey);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.DoesNotContain(marker, text);
        }
        Assert.Contains(control, (await Call(HttpMethod.Get, $"sessions/{sessionId}/dev-reports/{report}", sessionKey)).Text);
        // The owner's reply path carries the owner's own items, never a person's comment.
        Assert.DoesNotContain(marker, (await Call(HttpMethod.Get, $"dev-reports/{report}", _aliceKey)).Text);
        // The comment is there to be read by the person it goes to - it simply never went toward the agent.
        Assert.Equal(marker, Assert.Single(_gateway.DevReportCommentsForTest.To(_aliceHome, report, _alice)).Text);
    }

    // ---- refusals --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Issue2309_OverTheWire_SendingToSomeoneWhoIsNotAMember_IsRefused_AndNothingIsSent()
    {
        var report = TeamReport();
        var other = _gateway.TeamRegistry.CreateTeam(_owner, "Other").Team!.TeamId;
        Assert.True(_gateway.TeamRegistry.AddMember(other, _stranger, TeamRole.Collaborator).IsDone);

        var (status, text) = await Call(HttpMethod.Post, $"teams/{_team}/reports/mine/{report}/recipients", _aliceKey,
            new { memberIds = new[] { MemberId(_mike), TeamMemberIds.For(other, _stranger) }, version = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("not_a_member", Json(text).GetProperty("code").GetString());
        Assert.Empty(await SentToMe(_mikeKey));
    }

    [Fact]
    public async Task Issue2309_OverTheWire_SendingAReportYouDidNotWrite_IsRefusedByTheGate()
    {
        var report = TeamReport();

        var (status, text) = await Call(HttpMethod.Post, $"teams/{_team}/reports/mine/{report}/recipients", _bobKey,
            new { memberIds = new[] { MemberId(_mike) }, version = 1 });

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(TeamEndpointGate.RefusalCode, Json(text).GetProperty("code").GetString());
        Assert.Empty(await SentToMe(_mikeKey));
    }

    [Fact]
    public async Task Issue2309_PrivacyCheck_ADeveloperCannotReadAnotherDevelopersReports()
    {
        var alices = TeamReport("Alice's private report");

        // From his own account: his list is his own, and her report is watching her session - refused.
        var (_, list) = await Call(HttpMethod.Get, $"teams/{_team}/reports/mine", _bobKey);
        Assert.Equal(0, Json(list).GetProperty("count").GetInt32());
        foreach (var path in new[] { $"teams/{_team}/reports/mine/{alices}", $"teams/{_team}/reports/mine/{alices}/html" })
        {
            var (status, text) = await Call(HttpMethod.Get, path, _bobKey);
            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.DoesNotContain("Alice's private report", text);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"teams/{_team}/reports/sent-to-me/{alices}", _bobKey)).Status);
        // The owner's dev report routes from his own account read his own account only.
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"dev-reports/{alices}", _bobKey)).Status);
        var (_, own) = await Call(HttpMethod.Get, "dev-reports", _bobKey);
        Assert.Equal(0, Json(own).GetProperty("count").GetInt32());

        // Inside the team's tenant, the owner's dev report routes state no action, so the gate the host installed refuses
        // every one of them for every role - the team's reports cannot be listed or read there by anyone.
        foreach (var (method, pattern) in new[] { ("GET", "/dev-reports"), ("GET", "/dev-reports/{reportId}"),
                     ("GET", "/dev-reports/{reportId}/html"), ("POST", "/dev-reports/{reportId}/send") })
        {
            Assert.Contains(pattern, _gateway.MappedEndpoints.OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
                .Select(e => TeamEndpointRules.Normalize(e.RoutePattern.RawText)));
            foreach (var subject in new[] { _owner, _bob })
            {
                var verdict = _gateway.TeamGate.Check(method, pattern, _ => null, new TenantId(_team), () => subject, _ => TeamOwnership.Callers);
                Assert.Equal(TeamGateOutcome.Refused, verdict.Outcome);
                Assert.Equal(TeamEndpointGate.UndeclaredRefusal, verdict.Message);
            }
        }
    }

    /// <summary>
    /// A session on a Director set up for the team publishes a report OVER THE WIRE, and the report is its Director's
    /// person's: Alice's team Director connects on its team key and registers a session key through the hub, the session
    /// publishes with that key, and the report lands in the team's tenant authored by Alice (the publish route names the
    /// author as the owner of the calling session key's Director). A colleague cannot publish under her session's id: Bob's
    /// team Director is refused a key for it (#3552 review S2-F8, the session key row is Alice's), so the key it minted
    /// authenticates nothing; and Bob's own session's key may publish only for his own session.
    /// </summary>
    [Fact]
    public async Task Issue2309_TeamKeyVariant_ATeamSessionPublishesOverTheWire_AuthoredByItsDirectorsPerson_AndAColleagueCannot()
    {
        var tenant = new TenantId(_team);
        var aliceDirectorKey = _gateway.Devices.RegisterForTenant(tenant, _alice,
            CcDirector.Gateway.Api.HostedEnrollmentEndpoint.TeamScopedDeviceId(_team, _alice, "director-alice-team"), "M-alice").DeviceKey;
        await using var aliceDirector = await FakeTunnelDirector.StartAsync(_gateway, aliceDirectorKey, "director-alice-team");
        var aliceSession = Guid.NewGuid().ToString("D");
        var aliceSessionKey = GatewaySessionKey.Mint();
        await aliceDirector.RegisterSessionKeyAsync(aliceSession, aliceSessionKey, DateTime.UtcNow.AddHours(1));
        await aliceDirector.PushSnapshotAsync(new SessionDto { SessionId = aliceSession });

        // PRESENCE: the team session publishes, and the report is Alice's.
        var publish = await Call(HttpMethod.Post, $"sessions/{aliceSession}/dev-reports", aliceSessionKey,
            new { key = @"C:\team.html", html = Html("Team report") });
        Assert.Equal(HttpStatusCode.OK, publish.Status);
        var reportId = Guid.Parse(Json(publish.Text).GetProperty("report").GetProperty("id").GetString()!);
        Assert.Equal(_alice, _gateway.DevReportsForTest.Get(tenant, reportId)?.AuthorSubject);

        // A colleague's Director is refused a key for Alice's session, so the key it minted authenticates nothing.
        var bobDirectorKey = _gateway.Devices.RegisterForTenant(tenant, _bob,
            CcDirector.Gateway.Api.HostedEnrollmentEndpoint.TeamScopedDeviceId(_team, _bob, "director-bob-team"), "M-bob").DeviceKey;
        await using var bobDirector = await FakeTunnelDirector.StartAsync(_gateway, bobDirectorKey, "director-bob-team");
        var bobKeyForAlices = GatewaySessionKey.Mint();
        await Assert.ThrowsAnyAsync<Exception>(() => bobDirector.RegisterSessionKeyAsync(aliceSession, bobKeyForAlices, DateTime.UtcNow.AddHours(1)));
        var underAlices = await Call(HttpMethod.Post, $"sessions/{aliceSession}/dev-reports", bobKeyForAlices,
            new { key = @"C:\bob.html", html = Html("Bob under Alice") });
        Assert.Equal(HttpStatusCode.Unauthorized, underAlices.Status);

        // Bob's own session's key publishes only for his own session, never under Alice's id.
        var bobSession = Guid.NewGuid().ToString("D");
        var bobSessionKey = GatewaySessionKey.Mint();
        await bobDirector.RegisterSessionKeyAsync(bobSession, bobSessionKey, DateTime.UtcNow.AddHours(1));
        var crossed = await Call(HttpMethod.Post, $"sessions/{aliceSession}/dev-reports", bobSessionKey,
            new { key = @"C:\bob2.html", html = Html("Bob crossed") });
        Assert.NotEqual(HttpStatusCode.OK, crossed.Status);
        Assert.NotEqual(HttpStatusCode.Unauthorized, crossed.Status);
    }

    [Fact]
    public async Task Issue2309_TenantIsolation_AnotherTeam_SeesNothingOfThisOne()
    {
        var report = TeamReport();
        await SendTo(report, _mike);
        var other = _gateway.TeamRegistry.CreateTeam(_stranger, "Other").Team!.TeamId;
        Assert.True(_gateway.TeamRegistry.AddMember(other, _mike, TeamRole.Collaborator).IsDone);

        // Mike in the other team: nothing was sent to him there, and this team's report is not found through it.
        var (_, there) = await Call(HttpMethod.Get, $"teams/{other}/reports/sent-to-me", _mikeKey);
        Assert.Equal(0, Json(there).GetProperty("count").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"teams/{other}/reports/sent-to-me/{report}", _mikeKey)).Status);
        // The other team's Owner, not a member here, is told there is no such team.
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"teams/{_team}/reports/sent-to-me", _strangerKey)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"teams/{_team}/reports/mine/{report}", _strangerKey)).Status);
        // And this team's report, asked for as the other team's own, is not the stranger's.
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, $"teams/{other}/reports/mine/{report}", _strangerKey)).Status);
    }

    /// <summary>
    /// Every route mapped under the team report group, read off the Gateway's own route table (review F5) - so a route
    /// added later is covered without anyone remembering to list it - with its methods and a real address for each.
    /// </summary>
    private List<(HttpMethod Method, string Path, string Pattern)> MappedReportRoutes(string reportId)
    {
        var routes = new List<(HttpMethod, string, string)>();
        foreach (var endpoint in _gateway.MappedEndpoints.OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>())
        {
            var pattern = TeamEndpointRules.Normalize(endpoint.RoutePattern.RawText);
            if (!pattern.StartsWith(CcDirector.Gateway.Api.TeamReportEndpoints.GroupPath, StringComparison.Ordinal))
                continue;
            var methods = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods
                          ?? throw new InvalidOperationException($"The team report route {pattern} declares no method.");
            var path = pattern.Replace("{teamId}", _team).Replace("{reportId}", reportId).TrimStart('/');
            foreach (var method in methods)
                routes.Add((new HttpMethod(method), path, pattern));
        }
        return routes;
    }

    [Fact]
    public async Task Issue2309_ASessionKey_IsRefusedEveryTeamReportRoute()
    {
        var report = TeamReport();
        await SendTo(report, _mike);
        var sessionKey = GatewaySessionKey.Mint();
        Assert.True(_gateway.SessionKeys.Register(_aliceHome, "director-x", Guid.NewGuid().ToString("D"), GatewaySessionKey.Hash(sessionKey), DateTime.UtcNow.AddHours(1)));

        var routes = MappedReportRoutes(report.ToString("D"));
        // The nine routes the group maps today: a smaller table means the read found the wrong thing, not that a route went.
        Assert.Equal(9, routes.Count);
        foreach (var (method, path, pattern) in routes)
        {
            var (status, text) = await Call(method, path, sessionKey, method == HttpMethod.Post ? new { text = "agent", memberIds = new[] { MemberId(_nina) }, version = 1 } : null);
            _out.WriteLine($"session key {method} {pattern}: {(int)status}");
            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.DoesNotContain("Signup page rewrite", text);
        }
        Assert.Empty(_gateway.DevReportCommentsForTest.To(new TenantId(_team), report, _alice));
        Assert.Empty(await SentToMe(_ninaKey));
    }

    [Fact]
    public async Task Issue2309_F4_KeysBoundToTheTeamsTenant_AreRefusedByTheTeamReportRoutesOwnAdmission_WhateverTheLeaseDoes()
    {
        // Over the wire these keys may be refused before a route runs (a session key by the session-key guard; any team key
        // by the access lease when the team's bill cannot be read), so the wire cannot always show what the team report
        // routes do with them. This asks the routes' own admission directly - the
        // filter every one of them runs, TeamLibraryEndpoints.AdmitIntoTeam - with the identity the auth layer records for
        // each key, and the gate's "allowed in this team" already set, the worst case. It must refuse, for its own reason:
        // the team's tenant is not a person's account, so there is nobody to answer for.
        var directorId = "director-bob-f4";
        var deviceKey = _gateway.Devices.RegisterForTenant(new TenantId(_team), _bob,
            CcDirector.Gateway.Api.HostedEnrollmentEndpoint.TeamScopedDeviceId(_team, _bob, directorId), "M-bob").DeviceKey;
        var sessionKey = GatewaySessionKey.Mint();
        Assert.True(_gateway.SessionKeys.Register(new TenantId(_team), directorId, Guid.NewGuid().ToString("D"),
            GatewaySessionKey.Hash(sessionKey), DateTime.UtcNow.AddHours(1)));
        var device = _gateway.Devices.ResolveCredential(deviceKey).Identity;
        var session = _gateway.SessionKeys.ResolveCredential(sessionKey).Identity;
        Assert.NotNull(device);
        Assert.NotNull(session);
        Assert.Equal(_team, device!.TenantId);
        Assert.Equal(new TenantId(_team), session!.Tenant);

        foreach (var (kind, itemKey, identity) in new (string, string, object)[]
                 {
                     ("device key", CcDirector.Gateway.Util.AuthMiddleware.AuthenticatedDeviceItemKey, device),
                     ("session key", CcDirector.Gateway.Util.AuthMiddleware.AuthenticatedSessionItemKey, session),
                 })
        {
            var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            ctx.Request.Method = "GET";
            ctx.Request.RouteValues["teamId"] = _team;
            ctx.Items[itemKey] = identity;
            ctx.Items[TeamEndpointGate.AllowedTeamItemKey] = _team;

            var (team, subject, denial) = CcDirector.Gateway.Api.TeamLibraryEndpoints.AdmitIntoTeam(ctx, _gateway.TenantBoundary, _gateway.TenantRegistry);

            Assert.Null(team);
            Assert.Null(subject);
            var (status, error) = await Render(denial!);
            _out.WriteLine($"{kind}: {status} {error}");
            Assert.Equal(403, status);
            Assert.Equal(CcDirector.Gateway.Api.TeamEndpoints.NotAPersonalAccountRefusal, error);
        }
    }

    private static async Task<(int Status, string? Error)> Render(Microsoft.AspNetCore.Http.IResult result)
    {
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        using var body = new MemoryStream();
        ctx.Response.Body = body;
        await result.ExecuteAsync(ctx);
        body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(body);
        return (ctx.Response.StatusCode, doc.RootElement.GetProperty("error").GetString());
    }
}
