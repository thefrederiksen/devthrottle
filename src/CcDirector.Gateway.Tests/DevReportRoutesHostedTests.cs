using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Security;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.DevReports;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The dev report routes on a REAL hosted Gateway (issue #2958, PLAN-phase-2.md), over HTTP with real
/// credentials and a Director really on the tunnel. What only a booted host proves:
///
///  - THE WHOLE JOURNEY. An agent publishes with its session key; the owner sends while the session works and the
///    item is HELD with nothing typed; the session's turn ends through a real push; exactly ONE prompt reaches the
///    Director, naming the cell's row and column and the chosen option.
///  - THE GUARD. A session key reaches the session routes (answered by the route, never the guard) and is refused
///    the owner routes; a device key is refused the session routes; one session cannot publish or reply for another.
///  - THE TENANT. Another account's device key gets 404 on read, html and send.
///  - THE ENDED SESSION. A session its Director closed refuses a send with "This session has ended".
///  - THE RESTART. Items held when the Gateway stops are delivered once by the NEXT Gateway on the same database,
///    through the turn-end watcher's own catch-up sweep.
///  - THE SIZE LIMIT, exactly at and one byte past 10 megabytes.
///  - THE SETTLE PASS (fix round): a held item for a session that then closes reads refused on the owner's detail; a
///    held item for a Director that drops and reconnects already idle is delivered once by the settle TIMER; an item a
///    crash left sending is, on the next Gateway, settled not confirmed and never sent again.
///
/// PARKED SUITE. Gateway.Tests serializes machine-wide and does not run in the default gate. Every test uses its
/// own session ids, because every Gateway in this process shares one gateway.db.
/// </summary>
public sealed class DevReportRoutesHostedTests : IAsyncLifetime
{
    private const string SharedToken = "dev-report-routes-token";
    private const string Question = """
        <div data-dev-report-question="deploy-window" data-dev-report-question-text="When should we deploy?">
          <label><input type="radio" name="deploy-window" value="tonight" data-recommended> Tonight - quiet traffic</label>
          <label><input type="radio" name="deploy-window" value="monday"> Monday - the team is around</label>
        </div>
        """;

    private readonly ITestOutputHelper _out;
    private readonly string _runId = Guid.NewGuid().ToString("N")[..12];
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-dev-report-routes-" + Guid.NewGuid().ToString("N"));
    private readonly string _sessionA = Guid.NewGuid().ToString("D");
    private readonly string _sessionB = Guid.NewGuid().ToString("D");
    private readonly string _directorId;
    private readonly ConcurrentQueue<PromptRequest> _prompts = new();

    private string? _priorHosted;
    private GatewayHost _gateway = null!;
    private FakeTunnelDirector? _director;
    private string _deviceKeyA = "";
    private string _deviceKeyB = "";
    private string _sessionKeyA = "";
    private string _sessionKeyB = "";
    private TenantId _tenantA;

    public DevReportRoutesHostedTests(ITestOutputHelper output)
    {
        _out = output;
        _directorId = "director-dr-" + _runId;
    }

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        await StartGatewayAsync();

        var a = HostedTestEnrollment.Enroll(_gateway, $"sub-dr-a-{_runId}", $"dr-a-{_runId}@example.com", $"dev-dra-{_runId}", "MDRA");
        var b = HostedTestEnrollment.Enroll(_gateway, $"sub-dr-b-{_runId}", $"dr-b-{_runId}@example.com", $"dev-drb-{_runId}", "MDRB");
        _tenantA = a.Tenant;
        Assert.NotEqual(a.Tenant.Value, b.Tenant.Value);
        _deviceKeyA = a.DeviceKey;
        _deviceKeyB = b.DeviceKey;

        _sessionKeyA = GatewaySessionKey.Mint();
        _sessionKeyB = GatewaySessionKey.Mint();
        Assert.True(_gateway.SessionKeys.Register(_tenantA, _directorId, _sessionA, GatewaySessionKey.Hash(_sessionKeyA), DateTime.UtcNow.AddHours(1)));
        Assert.True(_gateway.SessionKeys.Register(_tenantA, _directorId, _sessionB, GatewaySessionKey.Hash(_sessionKeyB), DateTime.UtcNow.AddHours(1)));
    }

    public async Task DisposeAsync()
    {
        if (_director is not null) await _director.DisposeAsync();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
    }

    private async Task StartGatewayAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: SharedToken, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();
    }

    /// <summary>The Director on the tunnel, bound to account A by its device key. It counts every prompt and
    /// answers it accepted; every other verb (a screen read by the Wingman, say) is refused and not counted.</summary>
    private async Task ConnectDirectorAsync()
    {
        _director = await FakeTunnelDirector.StartAsync(_gateway, _deviceKeyA, _directorId, "MDRA", cmd =>
        {
            if (cmd.Verb != "prompt")
                return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"not served in this test: {cmd.Verb}");
            _prompts.Enqueue(JsonSerializer.Deserialize<PromptRequest>(cmd.PayloadJson, FakeTunnelDirector.WebJson)!);
            return FakeTunnelDirector.Ok(new PromptResponse { Accepted = true, SentAt = DateTime.UtcNow, ActivityState = "Working" });
        });
    }

    private static SessionDto Row(string sid, string state) => new()
    {
        SessionId = sid,
        Name = "dev report session",
        ActivityState = state,
        LastActivityAt = DateTime.UtcNow,
    };

    private HttpClient Client(string bearer)
    {
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/"), Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return http;
    }

    private static string Report(string padding = "") =>
        "<header data-dev-report=\"header\" data-dev-report-status=\"waiting-on-you\"><h1>Gateway failures</h1></header>" +
        "<section data-dev-report=\"summary\"><p>The failures doubled.</p></section>" +
        $"<section data-dev-report=\"questions\">{Question}</section>" +
        "<section data-dev-report=\"detail\"><table id=\"t\"><tr><th></th><th>Failures</th></tr><tr><th scope=\"row\">Gateway</th><td>42</td></tr></table>" +
        (padding.Length > 0 ? "<p>" + padding + "</p>" : "") + "</section>";

    private async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpClient http, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var resp = await http.SendAsync(request);
        var text = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"{method} {path} -> {(int)resp.StatusCode}: {(text.Length > 400 ? text[..400] + "..." : text)}");
        var json = text.Length > 0 && (text[0] == '{' || text[0] == '[')
            ? JsonDocument.Parse(text).RootElement.Clone()
            : JsonDocument.Parse("null").RootElement.Clone();
        return (resp.StatusCode, json);
    }

    private async Task<string> PublishAsync(string sid, string key, string sessionKey)
    {
        using var http = Client(sessionKey);
        var (status, body) = await Send(http, HttpMethod.Post, $"sessions/{sid}/dev-reports", new { key, html = Report() });
        Assert.Equal(HttpStatusCode.OK, status);
        return body.GetProperty("report").GetProperty("id").GetString()!;
    }

    private static object NoteOnTheCell(string id) => new
    {
        id,
        kind = "note",
        text = "  This number is wrong\nIt was 21 yesterday",
        anchor = new { type = "table-cell", selector = "#t > tbody > tr:nth-of-type(2) > td", quote = "42", rowLabel = "Gateway", columnLabel = "Failures" },
    };

    private static object AnswerTonight(string id) => new
    {
        id,
        kind = "answer",
        questionId = "deploy-window",
        question = "When should we deploy?",
        optionValue = "tonight",
        optionLabel = "Tonight - quiet traffic",
        comment = "",
    };

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out waiting for " + what);
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task OwnerSendsWhileTheSessionWorks_ItemIsHeld_ThenTheTurnEndDeliversExactlyOnePrompt()
    {
        await ConnectDirectorAsync();
        await _director!.PushDeltaAsync(Row(_sessionA, "Working"));

        var reportId = await PublishAsync(_sessionA, @"C:\work\journey.html", _sessionKeyA);

        using var owner = Client(_deviceKeyA);
        var (status, body) = await Send(owner, HttpMethod.Post, $"dev-reports/{reportId}/send",
            new { items = new[] { NoteOnTheCell("n1"), AnswerTonight("a1") } });
        Assert.Equal(HttpStatusCode.OK, status);
        var updates = body.GetProperty("updates");
        Assert.Equal(2, updates.GetArrayLength());
        foreach (var u in updates.EnumerateArray())
        {
            Assert.Equal("held", u.GetProperty("status").GetString());
            Assert.Equal("Delivered when the agent finishes its turn", u.GetProperty("statusLabel").GetString());
        }
        Assert.Empty(_prompts);

        // The turn ends, through the push that carries it.
        await _director.PushDeltaAsync(Row(_sessionA, "WaitingForInput"));
        await WaitUntil(() => !_prompts.IsEmpty, "the held items to be delivered at the turn end");
        // Anything that would deliver a second time has had its chance.
        await Task.Delay(1500);

        var prompt = Assert.Single(_prompts);
        Assert.Contains("row \"Gateway\", column \"Failures\"", prompt.Text);
        Assert.Contains("\"When should we deploy?\": \"Tonight - quiet traffic\" (value \"tonight\")", prompt.Text);
        Assert.Matches(@"\n<<<owner-text-([0-9a-f]{8})\n  This number is wrong\nIt was 21 yesterday\nowner-text-\1>>>\n", prompt.Text);
        Assert.False(prompt.AgentDriven);
        Assert.Equal(SubmissionRoutes.GatewayDevReport, prompt.Provenance!.Route);
        Assert.Equal(SubmissionIdentityKinds.Device, prompt.Provenance.IdentityKind);

        var (_, detail) = await Send(owner, HttpMethod.Get, $"dev-reports/{reportId}");
        Assert.All(detail.GetProperty("items").EnumerateArray(),
            i => Assert.Equal("Delivered to the session", i.GetProperty("statusLabel").GetString()));
        Assert.Equal(0, detail.GetProperty("report").GetProperty("openItems").GetInt32());

        // The agent reads the owner's items through its own session route and replies.
        using var agent = Client(_sessionKeyA);
        var (readStatus, agentView) = await Send(agent, HttpMethod.Get, $"sessions/{_sessionA}/dev-reports/{reportId}");
        Assert.Equal(HttpStatusCode.OK, readStatus);
        Assert.Equal(2, agentView.GetProperty("items").GetArrayLength());
        var (replyStatus, reply) = await Send(agent, HttpMethod.Post, $"sessions/{_sessionA}/dev-reports/{reportId}/replies",
            new { text = "Fixed - see section 2" });
        Assert.Equal(HttpStatusCode.OK, replyStatus);
        Assert.Equal("Fixed - see section 2", reply.GetProperty("reply").GetProperty("text").GetString());
        var (_, afterReply) = await Send(owner, HttpMethod.Get, $"dev-reports/{reportId}");
        Assert.Equal("Fixed - see section 2", afterReply.GetProperty("replies")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ResendingTheSameItemsWhileIdle_IsOneItemAndOnePrompt()
    {
        await ConnectDirectorAsync();
        await _director!.PushDeltaAsync(Row(_sessionA, "WaitingForInput"));
        var reportId = await PublishAsync(_sessionA, @"C:\work\resend.html", _sessionKeyA);
        using var owner = Client(_deviceKeyA);

        var first = await Send(owner, HttpMethod.Post, $"dev-reports/{reportId}/send", new { items = new[] { AnswerTonight("a1") } });
        var second = await Send(owner, HttpMethod.Post, $"dev-reports/{reportId}/send", new { items = new[] { AnswerTonight("a1") } });

        Assert.Equal("delivered", first.Body.GetProperty("updates")[0].GetProperty("status").GetString());
        Assert.Equal("delivered", second.Body.GetProperty("updates")[0].GetProperty("status").GetString());
        Assert.Single(_prompts);
        var (_, detail) = await Send(owner, HttpMethod.Get, $"dev-reports/{reportId}");
        Assert.Equal(1, detail.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task AnotherAccountsDeviceKey_GetsNotFoundOnReadHtmlAndSend()
    {
        var reportId = await PublishAsync(_sessionA, @"C:\work\tenant.html", _sessionKeyA);
        using var mine = Client(_deviceKeyA);
        using var theirs = Client(_deviceKeyB);

        // POSITIVE CONTROL: the owner's own account reaches all three.
        Assert.Equal(HttpStatusCode.OK, (await Send(mine, HttpMethod.Get, $"dev-reports/{reportId}")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(mine, HttpMethod.Get, $"dev-reports/{reportId}/html")).Status);

        var read = await Send(theirs, HttpMethod.Get, $"dev-reports/{reportId}");
        var html = await Send(theirs, HttpMethod.Get, $"dev-reports/{reportId}/html");
        var send = await Send(theirs, HttpMethod.Post, $"dev-reports/{reportId}/send", new { items = new[] { AnswerTonight("x1") } });
        var list = await Send(theirs, HttpMethod.Get, $"dev-reports?sessionId={_sessionA}");

        Assert.Equal(HttpStatusCode.NotFound, read.Status);
        Assert.Equal(HttpStatusCode.NotFound, html.Status);
        Assert.Equal(HttpStatusCode.NotFound, send.Status);
        Assert.Equal("report_not_found", send.Body.GetProperty("code").GetString());
        Assert.Equal(0, list.Body.GetProperty("count").GetInt32());
        // Nothing the other account sent was stored on this report.
        var (_, detail) = await Send(mine, HttpMethod.Get, $"dev-reports/{reportId}");
        Assert.Equal(0, detail.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task TheHtmlRoute_ServesTheExactBytesAsPlainTextWithTheVersion()
    {
        var reportId = await PublishAsync(_sessionA, @"C:\work\bytes.html", _sessionKeyA);
        await PublishAsync(_sessionA, @"C:\work\bytes.html", _sessionKeyA);
        using var owner = Client(_deviceKeyA);

        using var latest = await owner.GetAsync($"dev-reports/{reportId}/html");
        using var first = await owner.GetAsync($"dev-reports/{reportId}/html?version=1");

        Assert.Equal("text/plain", latest.Content.Headers.ContentType!.MediaType);
        Assert.Equal("2", latest.Headers.GetValues("X-Dev-Report-Version").Single());
        Assert.Equal("1", first.Headers.GetValues("X-Dev-Report-Version").Single());
        Assert.Equal(Report(), await first.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"dev-reports/{reportId}/html?version=9")).StatusCode);
    }

    [Fact]
    public async Task ASessionKey_IsRefusedEveryOwnerRoute()
    {
        var reportId = await PublishAsync(_sessionA, @"C:\work\owner-only.html", _sessionKeyA);
        using var agent = Client(_sessionKeyA);

        var attempts = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Get, "dev-reports", null),
            (HttpMethod.Get, $"dev-reports/{reportId}", null),
            (HttpMethod.Get, $"dev-reports/{reportId}/html", null),
            (HttpMethod.Post, $"dev-reports/{reportId}/send", new { items = new[] { AnswerTonight("forged") } }),
        };
        foreach (var (method, path, body) in attempts)
        {
            var (status, answer) = await Send(agent, method, path, body);
            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.Equal("session_key_out_of_scope", answer.GetProperty("code").GetString());
        }

        using var owner = Client(_deviceKeyA);
        var (_, detail) = await Send(owner, HttpMethod.Get, $"dev-reports/{reportId}");
        Assert.Equal(0, detail.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task ADeviceKey_IsRefusedThePublishRoute()
    {
        using var owner = Client(_deviceKeyA);

        var (status, body) = await Send(owner, HttpMethod.Post, $"sessions/{_sessionA}/dev-reports",
            new { key = @"C:\work\forged.html", html = Report() });

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("session_key_required", body.GetProperty("code").GetString());
        var (_, list) = await Send(owner, HttpMethod.Get, $"dev-reports?sessionId={_sessionA}");
        Assert.DoesNotContain(list.GetProperty("reports").EnumerateArray(),
            r => r.GetProperty("key").GetString() == @"C:\work\forged.html");
    }

    [Fact]
    public async Task OneSessionsKey_CannotPublishReadOrReplyForAnotherSession()
    {
        var reportOfB = await PublishAsync(_sessionB, @"C:\work\b.html", _sessionKeyB);
        using var sessionA = Client(_sessionKeyA);

        var publish = await Send(sessionA, HttpMethod.Post, $"sessions/{_sessionB}/dev-reports", new { key = "k", html = Report() });
        var reply = await Send(sessionA, HttpMethod.Post, $"sessions/{_sessionB}/dev-reports/{reportOfB}/replies", new { text = "forged" });
        var read = await Send(sessionA, HttpMethod.Get, $"sessions/{_sessionB}/dev-reports/{reportOfB}");
        // Naming its OWN session in the path does not reach another session's report either.
        var sideways = await Send(sessionA, HttpMethod.Post, $"sessions/{_sessionA}/dev-reports/{reportOfB}/replies", new { text = "forged" });

        Assert.Equal((HttpStatusCode.Forbidden, "not_your_session"), (publish.Status, publish.Body.GetProperty("code").GetString()));
        Assert.Equal((HttpStatusCode.Forbidden, "not_your_session"), (reply.Status, reply.Body.GetProperty("code").GetString()));
        Assert.Equal((HttpStatusCode.Forbidden, "not_your_session"), (read.Status, read.Body.GetProperty("code").GetString()));
        Assert.Equal((HttpStatusCode.NotFound, "report_not_found"), (sideways.Status, sideways.Body.GetProperty("code").GetString()));

        using var owner = Client(_deviceKeyA);
        var (_, detail) = await Send(owner, HttpMethod.Get, $"dev-reports/{reportOfB}");
        Assert.Equal(0, detail.GetProperty("replies").GetArrayLength());
    }

    [Fact]
    public async Task SendToASessionItsDirectorClosed_IsRefusedThisSessionHasEnded()
    {
        await ConnectDirectorAsync();
        await _director!.PushSnapshotAsync(Row(_sessionA, "WaitingForInput"));
        var reportId = await PublishAsync(_sessionA, @"C:\work\ended.html", _sessionKeyA);
        using var owner = Client(_deviceKeyA);

        // POSITIVE CONTROL: while it runs, the report says it has not ended.
        Assert.False((await Send(owner, HttpMethod.Get, $"dev-reports/{reportId}")).Body
            .GetProperty("report").GetProperty("sessionEnded").GetBoolean());

        // The Director stops running it: a full snapshot without it closes it on the session history.
        await _director.PushSnapshotAsync();
        await WaitUntil(() => _gateway.PushedSessions.TryLocate(_tenantA, _sessionA, TimeSpan.FromMinutes(5)) is null,
            "the roster to drop the closed session");

        var (status, body) = await Send(owner, HttpMethod.Post, $"dev-reports/{reportId}/send",
            new { items = new[] { AnswerTonight("a1") } });

        Assert.Equal(HttpStatusCode.OK, status);
        var update = body.GetProperty("updates")[0];
        Assert.Equal("refused", update.GetProperty("status").GetString());
        Assert.Equal("This session has ended", update.GetProperty("statusLabel").GetString());
        Assert.Empty(_prompts);
        var (_, detail) = await Send(owner, HttpMethod.Get, $"dev-reports/{reportId}");
        Assert.Equal(0, detail.GetProperty("items").GetArrayLength());
        Assert.True(detail.GetProperty("report").GetProperty("sessionEnded").GetBoolean());
    }

    [Fact]
    public async Task HeldItems_SurviveAGatewayRestart_AndTheNextGatewaysCatchUpDeliversThemOnce()
    {
        await ConnectDirectorAsync();
        await _director!.PushDeltaAsync(Row(_sessionA, "Working"));
        var reportId = await PublishAsync(_sessionA, @"C:\work\restart.html", _sessionKeyA);
        using (var owner = Client(_deviceKeyA))
        {
            var (_, sent) = await Send(owner, HttpMethod.Post, $"dev-reports/{reportId}/send",
                new { items = new[] { NoteOnTheCell("n1"), AnswerTonight("a1") } });
            Assert.Equal("held", sent.GetProperty("updates")[0].GetProperty("status").GetString());
        }
        Assert.Empty(_prompts);

        // The Gateway stops while the items are held. A NEW Gateway starts over the same database.
        await _director.DisposeAsync();
        _director = null;
        await _gateway.StopAsync();
        await StartGatewayAsync();

        // The Director reconnects and reports the session - now waiting - in its full snapshot. A snapshot raises
        // no turn edge on its own; what does is the watcher's catch-up sweep, which in production runs at startup
        // and every fifteen seconds. The test assembly turns the timer off, so the test runs the sweep itself.
        await ConnectDirectorAsync();
        await _director!.PushSnapshotAsync(Row(_sessionA, "WaitingForInput"));
        await _gateway.TurnEndWatcherForTest!.SweepAsync(sweepAll: true);

        await WaitUntil(() => !_prompts.IsEmpty, "the held items to be delivered after the restart");
        // A second sweep - the reconcile - finds the session already seen and raises nothing.
        await _gateway.TurnEndWatcherForTest!.SweepAsync(sweepAll: true);
        await Task.Delay(1500);

        var prompt = Assert.Single(_prompts);
        Assert.Contains("row \"Gateway\", column \"Failures\"", prompt.Text);
        Assert.Contains("Tonight - quiet traffic", prompt.Text);
        using var ownerAfter = Client(_deviceKeyA);
        var (_, detail) = await Send(ownerAfter, HttpMethod.Get, $"dev-reports/{reportId}");
        Assert.All(detail.GetProperty("items").EnumerateArray(),
            i => Assert.Equal("delivered", i.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task HeldItem_ThenTheSessionCloses_TheOwnersDetailShowsItRefused()
    {
        await ConnectDirectorAsync();
        await _director!.PushSnapshotAsync(Row(_sessionA, "Working"));
        var reportId = await PublishAsync(_sessionA, @"C:\work\held-then-ended.html", _sessionKeyA);
        using var owner = Client(_deviceKeyA);
        var (_, sent) = await Send(owner, HttpMethod.Post, $"dev-reports/{reportId}/send", new { items = new[] { AnswerTonight("a1") } });
        Assert.Equal("held", sent.GetProperty("updates")[0].GetProperty("status").GetString());

        // The Director stops running it: a full snapshot without it closes it. No turn end is ever raised for it.
        await _director.PushSnapshotAsync();
        await WaitUntil(() => _gateway.PushedSessions.TryLocate(_tenantA, _sessionA, TimeSpan.FromMinutes(5)) is null,
            "the roster to drop the closed session");

        var (status, detail) = await Send(owner, HttpMethod.Get, $"dev-reports/{reportId}");

        Assert.Equal(HttpStatusCode.OK, status);
        var item = Assert.Single(detail.GetProperty("items").EnumerateArray());
        Assert.Equal("refused", item.GetProperty("status").GetString());
        Assert.Equal("This session has ended", item.GetProperty("statusLabel").GetString());
        Assert.Equal(0, detail.GetProperty("report").GetProperty("openItems").GetInt32());
        Assert.Empty(_prompts);
    }

    [Fact]
    public async Task HeldItem_ThenTheSessionCloses_TheOwnersListCountsNothingOpen()
    {
        // Phase 2 review round 2, finding 1: the list settles the sessions it returns before counting. The 30-second
        // timer never fires inside this test, so the only thing that can settle the item is the list route itself.
        await ConnectDirectorAsync();
        await _director!.PushSnapshotAsync(Row(_sessionA, "Working"));
        var reportId = await PublishAsync(_sessionA, @"C:\work\held-then-listed.html", _sessionKeyA);
        using var owner = Client(_deviceKeyA);
        var (_, sent) = await Send(owner, HttpMethod.Post, $"dev-reports/{reportId}/send", new { items = new[] { AnswerTonight("a1") } });
        Assert.Equal("held", sent.GetProperty("updates")[0].GetProperty("status").GetString());

        await _director.PushSnapshotAsync();
        await WaitUntil(() => _gateway.PushedSessions.TryLocate(_tenantA, _sessionA, TimeSpan.FromMinutes(5)) is null,
            "the roster to drop the closed session");

        var (status, list) = await Send(owner, HttpMethod.Get, $"dev-reports?sessionId={_sessionA}");

        Assert.Equal(HttpStatusCode.OK, status);
        var summary = Assert.Single(list.GetProperty("reports").EnumerateArray());
        Assert.True(summary.GetProperty("sessionEnded").GetBoolean());
        Assert.Equal(0, summary.GetProperty("openItems").GetInt32());
        Assert.Empty(_prompts);
    }

    [Fact]
    public async Task HeldItem_DirectorDropsAndReconnectsAlreadyIdle_TheSettleTimerDeliversItOnce()
    {
        // This test's own Gateway runs the settle timer at a short interval; every other test here leaves it at 30s.
        await _gateway.StopAsync();
        GatewayHost.DevReportSettleSweepScheduleForTests = TimeSpan.FromMilliseconds(300);
        try { await StartGatewayAsync(); }
        finally { GatewayHost.DevReportSettleSweepScheduleForTests = null; }

        // The watcher sees the session waiting.
        await ConnectDirectorAsync();
        await _director!.PushDeltaAsync(Row(_sessionA, "WaitingForInput"));
        var reportId = await PublishAsync(_sessionA, @"C:\work\reconnect.html", _sessionKeyA);

        // The Director drops. The owner sends while it is away, so the item is held.
        await _director.DisposeAsync();
        _director = null;
        await WaitUntil(() => !_gateway.PushedSessions.IsStreamConnected(_tenantA, _directorId), "the Director's stream to drop");
        using var owner = Client(_deviceKeyA);
        var (_, sent) = await Send(owner, HttpMethod.Post, $"dev-reports/{reportId}/send", new { items = new[] { AnswerTonight("a1") } });
        Assert.Equal("held", sent.GetProperty("updates")[0].GetProperty("status").GetString());
        Assert.Empty(_prompts);

        // It reconnects reporting the SAME waiting state the watcher last saw, so no turn end is raised.
        await ConnectDirectorAsync();
        await _director!.PushSnapshotAsync(Row(_sessionA, "WaitingForInput"));

        await WaitUntil(() => !_prompts.IsEmpty, "the settle timer to deliver the held item");
        await Task.Delay(1500);

        var prompt = Assert.Single(_prompts);
        Assert.Contains("\"Tonight - quiet traffic\" (value \"tonight\")", prompt.Text);
        var (_, detail) = await Send(owner, HttpMethod.Get, $"dev-reports/{reportId}");
        Assert.Equal("Delivered to the session", detail.GetProperty("items")[0].GetProperty("statusLabel").GetString());
    }

    [Fact]
    public async Task AnItemACrashLeftSending_OnTheNextGateway_IsSettledNotConfirmedAndNeverSent()
    {
        await ConnectDirectorAsync();
        await _director!.PushDeltaAsync(Row(_sessionA, "Working"));
        var reportId = await PublishAsync(_sessionA, @"C:\work\crash-sending.html", _sessionKeyA);
        using (var owner = Client(_deviceKeyA))
            await Send(owner, HttpMethod.Post, $"dev-reports/{reportId}/send", new { items = new[] { NoteOnTheCell("n1") } });

        // The Gateway dies mid-send: the item is claimed and in sending, exactly as a crash between the send and its
        // answer leaves it - and the claim is older than the claim timeout, so the next Gateway may rule it orphaned.
        var store = _gateway.DevReportsForTest;
        Assert.Single(store.ClaimWaiting(_tenantA, _sessionA, Guid.NewGuid(),
            DateTime.UtcNow - DevReportDelivery.SendingClaimTimeout - TimeSpan.FromMinutes(1)));
        await _director.DisposeAsync();
        _director = null;
        await _gateway.StopAsync();
        await StartGatewayAsync();

        // The session is idle on the new Gateway, so anything still deliverable WOULD be sent now.
        await ConnectDirectorAsync();
        await _director!.PushSnapshotAsync(Row(_sessionA, "WaitingForInput"));
        await _gateway.TurnEndWatcherForTest!.SweepAsync(sweepAll: true);
        using var ownerAfter = Client(_deviceKeyA);
        var (_, detail) = await Send(ownerAfter, HttpMethod.Get, $"dev-reports/{reportId}");
        await Task.Delay(1500);

        var item = Assert.Single(detail.GetProperty("items").EnumerateArray());
        Assert.Equal("delivered", item.GetProperty("status").GetString());
        Assert.Equal("Sent to the session, not confirmed", item.GetProperty("statusLabel").GetString());
        Assert.Equal(0, detail.GetProperty("report").GetProperty("openItems").GetInt32());
        Assert.Empty(_prompts);
    }

    [Fact]
    public async Task LongKeysAndIds_AreRefusedWith400_BeforeTheDatabaseSeesThem()
    {
        using var agent = Client(_sessionKeyA);
        var longKey = new string('k', 513);

        var publish = await Send(agent, HttpMethod.Post, $"sessions/{_sessionA}/dev-reports", new { key = longKey, html = Report() });
        var atLimit = await Send(agent, HttpMethod.Post, $"sessions/{_sessionA}/dev-reports", new { key = new string('k', 512), html = Report() });

        Assert.Equal(HttpStatusCode.BadRequest, publish.Status);
        Assert.Equal("The report key is 513 characters; the limit is 512.", publish.Body.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.OK, atLimit.Status);

        var reportId = atLimit.Body.GetProperty("report").GetProperty("id").GetString()!;
        using var owner = Client(_deviceKeyA);
        var send = await Send(owner, HttpMethod.Post, $"dev-reports/{reportId}/send",
            new { items = new[] { AnswerTonight("a1"), AnswerTonight(new string('i', 129)) } });

        Assert.Equal(HttpStatusCode.BadRequest, send.Status);
        Assert.Equal("malformed_item", send.Body.GetProperty("code").GetString());
        Assert.Contains("id is 129 characters; the limit is 128.", send.Body.GetProperty("error").GetString());
        var (_, detail) = await Send(owner, HttpMethod.Get, $"dev-reports/{reportId}");
        Assert.Equal(0, detail.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Publish_ExactlyTenMegabytesIsAccepted_OneByteMoreIsRefusedWith413()
    {
        const long limit = 10L * 1024 * 1024;
        var baseBytes = Encoding.UTF8.GetByteCount(Report("x")) - 1;
        var atLimit = Report(new string('x', (int)(limit - baseBytes)));
        var overLimit = Report(new string('x', (int)(limit - baseBytes + 1)));
        Assert.Equal(limit, Encoding.UTF8.GetByteCount(atLimit));
        Assert.Equal(limit + 1, Encoding.UTF8.GetByteCount(overLimit));
        using var agent = Client(_sessionKeyA);

        var accepted = await Send(agent, HttpMethod.Post, $"sessions/{_sessionA}/dev-reports", new { key = "big.html", html = atLimit });
        var refused = await Send(agent, HttpMethod.Post, $"sessions/{_sessionA}/dev-reports", new { key = "bigger.html", html = overLimit });

        Assert.Equal(HttpStatusCode.OK, accepted.Status);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, refused.Status);
        Assert.Equal(
            """{"error":"This report is 10485761 bytes. A dev report can be at most 10485760 bytes (10 megabytes).","code":"report_too_large","bytes":10485761,"limitBytes":10485760}""",
            refused.Body.GetRawText());
        using var owner = Client(_deviceKeyA);
        var (_, list) = await Send(owner, HttpMethod.Get, $"dev-reports?sessionId={_sessionA}");
        Assert.DoesNotContain(list.GetProperty("reports").EnumerateArray(), r => r.GetProperty("key").GetString() == "bigger.html");
    }

    [Fact]
    public async Task Publish_AReportOfTheWrongShape_IsRefusedWithEveryError()
    {
        using var agent = Client(_sessionKeyA);

        var (status, body) = await Send(agent, HttpMethod.Post, $"sessions/{_sessionA}/dev-reports",
            new { key = "bad.html", html = "<p>no markers</p>" });

        Assert.Equal((HttpStatusCode)422, status);
        Assert.Equal("shape_check_failed", body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").GetArrayLength() >= 3);
    }

    [Fact]
    public async Task Send_AMalformedItem_Is400AndNothingIsStored()
    {
        await ConnectDirectorAsync();
        await _director!.PushDeltaAsync(Row(_sessionA, "WaitingForInput"));
        var reportId = await PublishAsync(_sessionA, @"C:\work\malformed.html", _sessionKeyA);
        using var owner = Client(_deviceKeyA);

        var (status, body) = await Send(owner, HttpMethod.Post, $"dev-reports/{reportId}/send",
            new object[] { new { items = new object[] { AnswerTonight("good"), new { id = "bad", kind = "note", text = "no anchor" } } } }[0]);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("malformed_item", body.GetProperty("code").GetString());
        var (_, detail) = await Send(owner, HttpMethod.Get, $"dev-reports/{reportId}");
        Assert.Equal(0, detail.GetProperty("items").GetArrayLength());
        Assert.Empty(_prompts);
    }
}
