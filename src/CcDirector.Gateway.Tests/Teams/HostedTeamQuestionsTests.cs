using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.DevReports;
using CcDirector.Gateway.Teams;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// The questions waiting on a member of the team (devthrottle_internal#2307), END TO END through a REAL hosted
/// <see cref="GatewayHost"/> with Teams released, over REAL HTTP with each person's own account key, through the real auth
/// middleware and the team gate the host installed.
///
/// The team: the Owner, Alice (a Developer, who writes the reports), Mike and Nina (Collaborators).
///
/// WHERE THE REPORT COMES FROM. The team pays (a live team bill, set up below), so a session on a Director set up for the
/// team reaches the Gateway over the wire: the access lease reads the TEAM's bill (devthrottle_internal#2311 step 2,
/// #3552). <see cref="Issue2307_TeamKeyVariant_AMembersAnswer_ReachesALiveTeamSessionOverTheWire_AndTheirCommentNever"/>
/// runs the whole thing over the wire: the team session publishes, Alice sends the report, Mike answers through the
/// answer route, and the settle pass and a real turn end deliver his choice to that session on the tunnel. The other tests
/// write the team's report through the store the publish route writes with, carrying the author it records, and every
/// person-facing step after that is over the wire; with no Director holding the session that asked, a member's answer is
/// HELD for it.
///
/// A PERSONAL ACCOUNT'S LIVE SESSION TOO. <see cref="Issue2307_AMembersAnswer_ReachesALiveSessionOnTheTunnel_AndTheirCommentNever"/>
/// runs the same delivery for a session on a personal account's Director - the item the answer route builds
/// (<see cref="TeamQuestions.ChoiceItem"/>), the settle pass and a real turn end - with the member's comment stored exactly
/// as the route stores it. The unit tests (TeamQuestionsTests) drive the same delivery through the answer route's own
/// class.
///
/// PARKED SUITE. Gateway.Tests serializes machine-wide and does not run in the default gate.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class HostedTeamQuestionsTests : IAsyncLifetime
{
    private const string Token = "team-questions-token";
    private const string Question = "Should the trial be 14 days or 30?";
    private readonly ITestOutputHelper _out;
    private readonly string _run = Guid.NewGuid().ToString("N")[..10];
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-team-questions-" + Guid.NewGuid().ToString("N"));
    private string _owner = "", _alice = "", _mike = "", _nina = "", _stranger = "";
    private string _aliceKey = "", _mikeKey = "", _ninaKey = "", _strangerKey = "";
    private TenantId _aliceHome;
    private string _team = "";
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string? _priorHosted;
    private string? _priorRoot;

    public HostedTeamQuestionsTests(ITestOutputHelper output) => _out = output;

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

        (_owner, _) = Person("owner");
        (_alice, _aliceKey) = Person("alice");
        (_mike, _mikeKey) = Person("mike");
        (_nina, _ninaKey) = Person("nina");
        (_stranger, _strangerKey) = Person("stranger");
        _aliceHome = _gateway.TenantRegistry.LookupBySubject(_alice)!.Value;

        _team = _gateway.TeamRegistry.CreateTeam(_owner, "Acme").Team!.TeamId;
        Assert.True(_gateway.TeamRegistry.AddMember(_team, _alice, TeamRole.Developer).IsDone);
        Assert.True(_gateway.TeamRegistry.AddMember(_team, _mike, TeamRole.Collaborator).IsDone);
        Assert.True(_gateway.TeamRegistry.AddMember(_team, _nina, TeamRole.Collaborator).IsDone);
        // The team pays: a team key's access is read from the team's bill (devthrottle_internal#2311 step 2).
        HostedTeamBill.CreateTable(_gateway);
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

    private (string Subject, string Key) Person(string name)
    {
        var subject = $"sub-tq-{name}-{_run}";
        var device = HostedTestEnrollment.Enroll(_gateway, subject, $"{name}@example.com", $"browser-{name}-{_run}", "Browser");
        return (subject, device.DeviceKey);
    }

    internal static string Html() =>
        "<header data-dev-report=\"header\" data-dev-report-status=\"waiting-on-you\"><h1>Pricing</h1></header>" +
        "<section data-dev-report=\"summary\"><p>The signup page says 14 today.</p></section>" +
        "<section data-dev-report=\"questions\">" +
        $"<div data-dev-report-question=\"trial-length\" data-dev-report-question-text=\"{Question}\">" +
        "<label><input type=\"radio\" name=\"trial-length\" value=\"14\" data-recommended> 14 days</label>" +
        "<label><input type=\"radio\" name=\"trial-length\" value=\"30\"> 30 days</label>" +
        "<textarea data-dev-report-comment></textarea></div>" +
        "</section><section data-dev-report=\"detail\"><p>The detail.</p></section>";

    /// <summary>A report with the question, written in the team by a session of Alice's as the publish route writes it.</summary>
    private (Guid Id, string SessionId) TeamReport()
    {
        var sid = Guid.NewGuid().ToString("D");
        var id = _gateway.DevReportsForTest.Publish(new TenantId(_team), sid, $@"C:\work\{Guid.NewGuid():N}.html",
            Html(), "waiting-on-you", "Pricing", DateTime.UtcNow, _alice).Report.Id;
        return (id, sid);
    }

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

    /// <summary>Alice sends the report to people as her screen does: by their Team page ids, naming version 1.</summary>
    private async Task SendTo(Guid report, params string[] subjects)
    {
        var (status, _) = await Call(HttpMethod.Post, $"teams/{_team}/reports/mine/{report}/recipients", _aliceKey,
            new { memberIds = subjects.Select(s => TeamMemberIds.For(_team, s)).ToArray(), version = 1 });
        Assert.Equal(HttpStatusCode.OK, status);
    }

    private async Task<string[]> Waiting(string key, string? team = null)
    {
        var (status, text) = await Call(HttpMethod.Get, $"teams/{team ?? _team}/questions", key);
        Assert.Equal(HttpStatusCode.OK, status);
        return Json(text).GetProperty("waiting").EnumerateArray()
            .Select(q => q.GetProperty("reportId").GetString() + "/" + q.GetProperty("questionId").GetString()).ToArray();
    }

    private Task<(HttpStatusCode Status, string Text)> Answer(string key, Guid report, string option, string comment = "", string? team = null) =>
        Call(HttpMethod.Post, $"teams/{team ?? _team}/questions/{report}/trial-length/answer", key, new { version = 1, optionValue = option, comment });

    // ---- the issue's tests, over the wire ---------------------------------------------------------------------------

    [Fact]
    public async Task Issue2307_OverTheWire_AQuestionSentToMike_AppearsForMike_AndForNoOtherCollaborator_WhoIsRefused()
    {
        var (report, _) = TeamReport();
        await SendTo(report, _mike);

        Assert.Equal(new[] { $"{report}/trial-length" }, await Waiting(_mikeKey));
        Assert.Empty(await Waiting(_ninaKey));
        var (_, mike) = await Call(HttpMethod.Get, $"teams/{_team}/questions", _mikeKey);
        var card = Json(mike).GetProperty("waiting")[0];
        Assert.Equal(Question, card.GetProperty("question").GetString());
        Assert.Equal("alice@example.com", card.GetProperty("askedBy").GetString());
        Assert.Equal("Your words go to alice@example.com. Only your choice reaches the agent.", card.GetProperty("commentNote").GetString());

        // Nina, asked directly for Mike's question, is refused by the server - and nothing is stored or sent.
        var (refused, text) = await Answer(_ninaKey, report, "30", "Nina's words");
        Assert.Equal(HttpStatusCode.NotFound, refused);
        Assert.Equal(TeamQuestions.NoSuchQuestion, Json(text).GetProperty("error").GetString());
        Assert.Empty(_gateway.DevReportsForTest.Items(new TenantId(_team), report));
        Assert.Empty(_gateway.DevReportCommentsForTest.To(new TenantId(_team), report, _alice));
        Assert.Equal(new[] { $"{report}/trial-length" }, await Waiting(_mikeKey));
    }

    [Fact]
    public async Task Issue2307_OverTheWire_MikesChoiceIsHeldForTheSessionThatAsked_AndHisWordsReachAlice_AndNoSessionRead()
    {
        var (report, sessionId) = TeamReport();
        await SendTo(report, _mike);
        var marker = "MARKER-2307-WIRE-" + Guid.NewGuid().ToString("N");

        var (status, text) = await Answer(_mikeKey, report, "30", "We trialled 14 days last year. " + marker);

        Assert.Equal(HttpStatusCode.OK, status);
        var answer = Json(text).GetProperty("question").GetProperty("answer");
        Assert.Equal("You chose \"30 days\"", answer.GetProperty("chosenLabel").GetString());
        // No Director holds the session that asked, so the choice waits for it - with no words in it.
        Assert.Equal("Delivered when the agent finishes its turn", answer.GetProperty("statusLabel").GetString());
        var item = Assert.Single(_gateway.DevReportsForTest.Items(new TenantId(_team), report));
        Assert.Equal((sessionId, "30", "30 days", "", ""), (item.SessionId, item.OptionValue, item.OptionLabel, item.Comment, item.Text));
        Assert.Equal(_mike, item.AnswererSubject);

        // The words reach Alice, the person behind the session that asked, on her own report's page.
        var (_, mine) = await Call(HttpMethod.Get, $"teams/{_team}/reports/mine/{report}", _aliceKey);
        var comment = Assert.Single(Json(mine).GetProperty("comments").EnumerateArray());
        Assert.Contains(marker, comment.GetProperty("text").GetString());
        Assert.Equal("mike@example.com", comment.GetProperty("from").GetString());
        Assert.Equal($"About \"{Question}\" - chose \"30 days\"", comment.GetProperty("aboutLabel").GetString());

        // No read a session key can make carries them. Alice's team Director connects and registers the session's key
        // through the hub; the session's own two routes answer 200 - the report is there, by its title, the positive
        // control - and the words are absent. The person-facing routes refuse a session key outright.
        var aliceDirectorKey = _gateway.Devices.RegisterForTenant(new TenantId(_team), _alice,
            HostedEnrollmentEndpoint.TeamScopedDeviceId(_team, _alice, "director-alice-team"), "M-alice").DeviceKey;
        await using var aliceDirector = await FakeTunnelDirector.StartAsync(_gateway, aliceDirectorKey, "director-alice-team");
        var sessionKey = GatewaySessionKey.Mint();
        await aliceDirector.RegisterSessionKeyAsync(sessionId, sessionKey, DateTime.UtcNow.AddHours(1));
        foreach (var path in new[] { $"sessions/{sessionId}/dev-reports", $"sessions/{sessionId}/dev-reports/{report}" })
        {
            var (s, t) = await Call(HttpMethod.Get, path, sessionKey);
            Assert.Equal(HttpStatusCode.OK, s);
            Assert.Contains("Pricing", t);
            Assert.DoesNotContain(marker, t);
        }
        foreach (var path in new[]
                 {
                     $"teams/{_team}/questions", $"teams/{_team}/reports/mine/{report}",
                     $"teams/{_team}/reports/sent-to-me/{report}", $"dev-reports/{report}",
                 })
        {
            var (s, t) = await Call(HttpMethod.Get, path, sessionKey);
            Assert.Equal(HttpStatusCode.Forbidden, s);
            Assert.DoesNotContain(marker, t);
        }

        // Answered: it no longer waits on Mike, and his Reports list no longer says it does.
        Assert.Empty(await Waiting(_mikeKey));
        var (_, sent) = await Call(HttpMethod.Get, $"teams/{_team}/reports/sent-to-me", _mikeKey);
        Assert.Equal(JsonValueKind.Null, Json(sent).GetProperty("reports")[0].GetProperty("questionsLabel").ValueKind);
    }

    [Fact]
    public async Task Issue2307_AMembersAnswer_ReachesALiveSessionOnTheTunnel_AndTheirCommentNever()
    {
        // A live session on a personal account's Director on the tunnel, its report published through the real route with
        // its own session key.
        const string directorId = "director-alice-home";
        var sessionId = Guid.NewGuid().ToString("D");
        var sessionKey = GatewaySessionKey.Mint();
        Assert.True(_gateway.SessionKeys.Register(_aliceHome, directorId, sessionId, GatewaySessionKey.Hash(sessionKey), DateTime.UtcNow.AddHours(1)));
        var commands = new ConcurrentQueue<string>();
        await using var director = await FakeTunnelDirector.StartAsync(_gateway, _aliceKey, directorId, "M-alice", cmd =>
        {
            // EVERY command toward the Director is recorded whole, whatever its verb.
            commands.Enqueue(cmd.Verb + " " + cmd.SessionId + " " + cmd.PayloadJson);
            return cmd.Verb == "prompt"
                ? FakeTunnelDirector.Ok(new PromptResponse { Accepted = true, SentAt = DateTime.UtcNow, ActivityState = "Working" })
                : DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"not served in this test: {cmd.Verb}");
        });
        await director.PushDeltaAsync(new SessionDto { SessionId = sessionId, Name = "alice", ActivityState = "Working", LastActivityAt = DateTime.UtcNow });
        var (published, publishedText) = await Call(HttpMethod.Post, $"sessions/{sessionId}/dev-reports", sessionKey,
            new { key = @"C:\work\live.html", html = Html() });
        Assert.Equal(HttpStatusCode.OK, published);
        var reportId = Guid.Parse(Json(publishedText).GetProperty("report").GetProperty("id").GetString()!);
        var report = _gateway.DevReportsForTest.Get(_aliceHome, reportId)!;

        // Mike's answer exactly as the answer route makes it: the choice item through the one delivery, the words through the
        // person-only path.
        var marker = "MARKER-2307-LIVE-" + Guid.NewGuid().ToString("N");
        var question = Assert.Single(DevReportQuestions.Read(Html()));
        var updates = await _gateway.DevReportDeliveryForTest.SendAsync(_aliceHome, report,
            [TeamQuestions.ChoiceItem(question, question.Option("30")!)], "device", CancellationToken.None, _mike);
        Assert.Equal(DevReportItemStates.Held, Assert.Single(updates).Status);
        _gateway.DevReportCommentsForTest.Add(_aliceHome, reportId, _mike, _alice, marker, DateTime.UtcNow, question.Id);

        // The settle pass while it works, then the turn end through the push that carries it.
        await _gateway.DevReportDeliveryForTest.SettleAsync(_aliceHome, sessionId, CancellationToken.None);
        // Held while it works: no prompt yet. (The Gateway sends the Director other verbs about the session meanwhile -
        // its role and display state - which is why every command, whatever its verb, is checked for the words below.)
        Assert.DoesNotContain(commands, c => c.StartsWith("prompt ", StringComparison.Ordinal));
        await director.PushDeltaAsync(new SessionDto { SessionId = sessionId, Name = "alice", ActivityState = "WaitingForInput", LastActivityAt = DateTime.UtcNow });
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!commands.Any(c => c.StartsWith("prompt ", StringComparison.Ordinal)))
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("The member's choice never reached the session - the harness would prove nothing.");
            await Task.Delay(50);
        }
        await Task.Delay(1500); // anything else that would be sent has had its chance
        await _gateway.DevReportDeliveryForTest.SettleAsync(_aliceHome, sessionId, CancellationToken.None);

        _out.WriteLine($"{commands.Count} command(s) reached the Director.");
        var prompt = Assert.Single(commands, c => c.StartsWith("prompt ", StringComparison.Ordinal));
        Assert.Contains(sessionId, prompt);
        // POSITIVE CONTROL: the choice IS what the session received.
        Assert.Contains("A person this report was sent to answered your dev report", prompt);
        Assert.Contains("30 days", prompt);
        Assert.All(commands, c => Assert.DoesNotContain(marker, c));

        // Every read the session's own key can make.
        foreach (var path in new[] { $"sessions/{sessionId}/dev-reports", $"sessions/{sessionId}/dev-reports/{reportId}" })
        {
            var (status, text) = await Call(HttpMethod.Get, path, sessionKey);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.DoesNotContain(marker, text);
        }
        Assert.DoesNotContain(marker, (await Call(HttpMethod.Get, $"dev-reports/{reportId}", _aliceKey)).Text);
        // The words are there to be read by the person who asked.
        Assert.Equal(marker, Assert.Single(_gateway.DevReportCommentsForTest.To(_aliceHome, reportId, _alice)).Text);
    }

    // ---- refusals and isolation ------------------------------------------------------------------------------------

    [Fact]
    public async Task Issue2307_TenantIsolation_AnotherTeam_SeesAndAnswersNothingOfThisOne()
    {
        var (report, _) = TeamReport();
        await SendTo(report, _mike);
        var other = _gateway.TeamRegistry.CreateTeam(_stranger, "Other").Team!.TeamId;
        Assert.True(_gateway.TeamRegistry.AddMember(other, _mike, TeamRole.Collaborator).IsDone);

        // Mike in the other team: nothing waits on him there, and this team's question cannot be answered through it.
        Assert.Empty(await Waiting(_mikeKey, other));
        Assert.Equal(HttpStatusCode.NotFound, (await Answer(_mikeKey, report, "30", "words", other)).Status);
        // The other team's Owner, not a member here, is told there is no such team.
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"teams/{_team}/questions", _strangerKey)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Answer(_strangerKey, report, "30")).Status);

        Assert.Empty(_gateway.DevReportsForTest.Items(new TenantId(_team), report));
        Assert.Equal(new[] { $"{report}/trial-length" }, await Waiting(_mikeKey));
    }

    /// <summary>Every route mapped under the questions group, read off the Gateway's own route table.</summary>
    private List<(HttpMethod Method, string Path, string Pattern)> MappedQuestionRoutes(Guid report)
    {
        var routes = new List<(HttpMethod, string, string)>();
        foreach (var endpoint in _gateway.MappedEndpoints.OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>())
        {
            var pattern = TeamEndpointRules.Normalize(endpoint.RoutePattern.RawText);
            if (!pattern.StartsWith(TeamQuestionEndpoints.GroupPath, StringComparison.Ordinal))
                continue;
            var methods = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods
                          ?? throw new InvalidOperationException($"The questions route {pattern} declares no method.");
            var path = pattern.Replace("{teamId}", _team).Replace("{reportId}", report.ToString("D")).Replace("{questionId}", "trial-length").TrimStart('/');
            foreach (var method in methods)
                routes.Add((new HttpMethod(method), path, pattern));
        }
        return routes;
    }

    [Fact]
    public async Task Issue2307_ASessionKey_IsRefusedEveryQuestionsRoute()
    {
        var (report, sessionId) = TeamReport();
        await SendTo(report, _mike);
        var sessionKey = GatewaySessionKey.Mint();
        Assert.True(_gateway.SessionKeys.Register(_aliceHome, "director-x", sessionId, GatewaySessionKey.Hash(sessionKey), DateTime.UtcNow.AddHours(1)));

        var routes = MappedQuestionRoutes(report);
        // The two routes the group maps today: a smaller table means the read found the wrong thing, not that a route went.
        Assert.Equal(2, routes.Count);
        foreach (var (method, path, pattern) in routes)
        {
            var (status, text) = await Call(method, path, sessionKey, method == HttpMethod.Post ? new { version = 1, optionValue = "30", comment = "agent" } : null);
            _out.WriteLine($"session key {method} {pattern}: {(int)status}");
            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.DoesNotContain(Question, text);
        }
        Assert.Empty(_gateway.DevReportsForTest.Items(new TenantId(_team), report));
    }

    /// <summary>
    /// THE TEAM KEY VARIANT, end to end over the wire. Alice's Director set up for the team connects on its team key and
    /// registers a session's key through the hub; the session publishes the report with that key; Alice sends it to Mike;
    /// Mike answers through the answer route with a choice and words carrying a unique marker. The settle pass while the
    /// session works holds it; the turn end delivers it. The choice IS in the prompt the team session receives; the marker
    /// is in no command toward the Director and no read the session's key can make, and it IS in what Alice reads.
    /// </summary>
    [Fact]
    public async Task Issue2307_TeamKeyVariant_AMembersAnswer_ReachesALiveTeamSessionOverTheWire_AndTheirCommentNever()
    {
        var team = new TenantId(_team);
        const string directorId = "director-alice-team";
        var aliceDirectorKey = _gateway.Devices.RegisterForTenant(team, _alice,
            HostedEnrollmentEndpoint.TeamScopedDeviceId(_team, _alice, directorId), "M-alice").DeviceKey;
        var commands = new ConcurrentQueue<string>();
        await using var director = await FakeTunnelDirector.StartAsync(_gateway, aliceDirectorKey, directorId, "M-alice", cmd =>
        {
            // EVERY command toward the Director is recorded whole, whatever its verb.
            commands.Enqueue(cmd.Verb + " " + cmd.SessionId + " " + cmd.PayloadJson);
            return cmd.Verb == "prompt"
                ? FakeTunnelDirector.Ok(new PromptResponse { Accepted = true, SentAt = DateTime.UtcNow, ActivityState = "Working" })
                : DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"not served in this test: {cmd.Verb}");
        });
        var sessionId = Guid.NewGuid().ToString("D");
        var sessionKey = GatewaySessionKey.Mint();
        await director.RegisterSessionKeyAsync(sessionId, sessionKey, DateTime.UtcNow.AddHours(1));
        await director.PushDeltaAsync(new SessionDto { SessionId = sessionId, Name = "alice", ActivityState = "Working", LastActivityAt = DateTime.UtcNow });

        // The team session publishes over the wire, and the report is Alice's, in the team.
        var (published, publishedText) = await Call(HttpMethod.Post, $"sessions/{sessionId}/dev-reports", sessionKey,
            new { key = @"C:\work\team-live.html", html = Html() });
        Assert.Equal(HttpStatusCode.OK, published);
        var report = Guid.Parse(Json(publishedText).GetProperty("report").GetProperty("id").GetString()!);
        Assert.Equal(_alice, _gateway.DevReportsForTest.Get(team, report)?.AuthorSubject);
        await SendTo(report, _mike);

        // Mike answers through the answer route: a choice and his own words.
        var marker = "MARKER-2307-TEAM-LIVE-" + Guid.NewGuid().ToString("N");
        var (answered, _) = await Answer(_mikeKey, report, "30", "We trialled 14 days last year. " + marker);
        Assert.Equal(HttpStatusCode.OK, answered);

        // The settle pass while it works: held, no prompt yet.
        await _gateway.DevReportDeliveryForTest.SettleAsync(team, sessionId, CancellationToken.None);
        Assert.DoesNotContain(commands, c => c.StartsWith("prompt ", StringComparison.Ordinal));
        var held = Assert.Single(_gateway.DevReportsForTest.Items(team, report));
        Assert.Equal((DevReportItemStates.Held, "30", "", ""), (held.Status, held.OptionValue, held.Comment, held.Text));

        // The turn end, through the push that carries it.
        await director.PushDeltaAsync(new SessionDto { SessionId = sessionId, Name = "alice", ActivityState = "WaitingForInput", LastActivityAt = DateTime.UtcNow });
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!commands.Any(c => c.StartsWith("prompt ", StringComparison.Ordinal)))
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Mike's choice never reached the team session - the harness would prove nothing.");
            await Task.Delay(50);
        }
        await Task.Delay(1500); // anything else that would be sent has had its chance
        await _gateway.DevReportDeliveryForTest.SettleAsync(team, sessionId, CancellationToken.None);

        _out.WriteLine($"{commands.Count} command(s) reached the team Director.");
        var prompt = Assert.Single(commands, c => c.StartsWith("prompt ", StringComparison.Ordinal));
        Assert.Contains(sessionId, prompt);
        // POSITIVE CONTROL: the choice IS what the team session received.
        Assert.Contains("A person this report was sent to answered your dev report", prompt);
        Assert.Contains("30 days", prompt);
        Assert.All(commands, c => Assert.DoesNotContain(marker, c));

        // Every read the session's own key can make: its own two routes answer, without the words.
        foreach (var path in new[] { $"sessions/{sessionId}/dev-reports", $"sessions/{sessionId}/dev-reports/{report}" })
        {
            var (status, text) = await Call(HttpMethod.Get, path, sessionKey);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Contains("Pricing", text);
            Assert.DoesNotContain(marker, text);
        }

        // The words reach Alice, the person behind the session that asked.
        var (_, mine) = await Call(HttpMethod.Get, $"teams/{_team}/reports/mine/{report}", _aliceKey);
        var comment = Assert.Single(Json(mine).GetProperty("comments").EnumerateArray());
        Assert.Contains(marker, comment.GetProperty("text").GetString());
        Assert.Equal("mike@example.com", comment.GetProperty("from").GetString());
    }
}
