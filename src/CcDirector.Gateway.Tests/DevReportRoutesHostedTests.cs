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
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
    }

    private async Task StartGatewayAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: SharedToken, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = true });
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
    public async Task TheUnfilteredList_WhatThePersonalReportsPageReads_HoldsOnlyTheAccountsOwnReports()
    {
        // The Cockpit's Reports page on a personal account reads GET /dev-reports with no session named: every report
        // the account's sessions sent it, and nothing of anyone else's (owner, 8 Oct 2026).
        var reportId = await PublishAsync(_sessionA, @"C:\work\personal-list.html", _sessionKeyA);
        using var mine = Client(_deviceKeyA);
        using var theirs = Client(_deviceKeyB);

        var (myStatus, myList) = await Send(mine, HttpMethod.Get, "dev-reports");
        var (theirStatus, theirList) = await Send(theirs, HttpMethod.Get, "dev-reports");

        Assert.Equal(HttpStatusCode.OK, myStatus);
        Assert.Contains(myList.GetProperty("reports").EnumerateArray(), r => r.GetProperty("id").GetString() == reportId);
        Assert.Equal(HttpStatusCode.OK, theirStatus);
        Assert.DoesNotContain(theirList.GetProperty("reports").EnumerateArray(), r => r.GetProperty("id").GetString() == reportId);
    }

    [Fact]
    public async Task TheUnfilteredList_ComesAPageAtATime_AndTheNextMarkerWalksToTheEnd()
    {
        // The personal Reports page timed out on a long-lived account: the list read every report and settled every
        // session behind them on each refresh. It now answers one page, newest first, with a marker for the next.
        var published = new List<string>();
        for (var i = 0; i < 5; i++)
            published.Add(await PublishAsync(_sessionA, $@"C:\work\paged-{i}.html", _sessionKeyA));
        using var mine = Client(_deviceKeyA);

        var walked = new List<string>();
        string? next = null;
        for (var pages = 0; pages < 10; pages++)
        {
            var path = "dev-reports?limit=2" + (next is null ? "" : "&after=" + Uri.EscapeDataString(next));
            var (status, body) = await Send(mine, HttpMethod.Get, path);
            Assert.Equal(HttpStatusCode.OK, status);
            var reports = body.GetProperty("reports").EnumerateArray().ToList();
            Assert.True(reports.Count <= 2);
            Assert.Equal(reports.Count, body.GetProperty("count").GetInt32());
            walked.AddRange(reports.Select(r => r.GetProperty("id").GetString()!));
            var marker = body.GetProperty("next");
            if (marker.ValueKind == JsonValueKind.Null) break;
            next = marker.GetString();
        }

        Assert.Equal(walked.Count, walked.Distinct().Count());
        Assert.All(published, id => Assert.Contains(id, walked));
        // Newest first: the last report published is the first one listed.
        Assert.Equal(published[^1], walked[0]);
    }

    [Fact]
    public async Task TheList_RefusesALimitOutOfRange_AndAMarkerItDidNotMake()
    {
        using var mine = Client(_deviceKeyA);

        var (tooMany, tooManyBody) = await Send(mine, HttpMethod.Get, "dev-reports?limit=101");
        var (badMarker, badMarkerBody) = await Send(mine, HttpMethod.Get, "dev-reports?after=not-a-marker");

        Assert.Equal(HttpStatusCode.BadRequest, tooMany);
        Assert.Equal("bad_limit", tooManyBody.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, badMarker);
        Assert.Equal("bad_after", badMarkerBody.GetProperty("code").GetString());
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
        Assert.Equal("nosniff", latest.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("nosniff", first.Headers.GetValues("X-Content-Type-Options").Single());
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
