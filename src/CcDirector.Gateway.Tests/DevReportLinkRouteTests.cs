using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// ONE ADDRESS PER REPORT THAT WORKS WITH NOBODY SIGNED IN, AND THE WAY BACK NAMED FOR A HUMAN (phase 3b of
/// the dev reports mission, issue #3025), on a REAL hosted Gateway over HTTP with real credentials. What only
/// a booted host proves:
///
///  - THE DEVICE ROUTING. A phone User-Agent on <c>/r/{id}</c> is sent to the phone app's report landing;
///    anything else to the Cockpit's. The report id rides in the address; nothing else does.
///  - NO ACCOUNT IS NEEDED, which is the change this phase makes. A request carrying NO credential at all gets
///    the same 302 - never the sign-in redirect, never a refusal - and so does a request carrying ANOTHER
///    ACCOUNT's credential, and so does an identifier no report has ever had. Re-add a tenant gate or a report
///    lookup here and those three go red.
///  - THE TWO ORDERINGS, and either reorder silently breaks the printed address. Registered after the
///    AUTHENTICATION middleware, a signed-out navigation is bounced to <c>/signin?next=/r/{id}</c> - a route no
///    shell router can resolve. Registered after the MOBILE FRONT DOOR, every phone link lands on the mobile
///    home screen. One test each, and each fails with exactly that symptom.
///  - THE WORDS. The report list and the report detail both carry <c>sessionLabel</c> and <c>backLabel</c>,
///    folded on the Gateway from the live roster, from the session history row when the session has gone, and
///    down to a plain true sentence when the Gateway knows neither - with no session identifier in any of them.
///
/// PARKED SUITE. Gateway.Tests serializes machine-wide and does not run in the default gate. Every test uses
/// its own session ids, because every Gateway in this process shares one gateway.db.
/// </summary>
public sealed class DevReportLinkRouteTests : IAsyncLifetime
{
    private const string SharedToken = "dev-report-link-token";
    private const string PhoneAgent = "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 Mobile Safari/537.36";
    private const string DesktopAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 Safari/537.36";

    // TWO options, because a question with one is not a question: the shape check refuses it. This fixture
    // carried only "tonight" and every publish here answered 422, which failed every test in this class for a
    // reason that had nothing to do with what any of them was about.
    private const string Question =
        "<div data-dev-report-question=\"deploy-window\" data-dev-report-question-text=\"When should we deploy?\">" +
        "<label><input type=\"radio\" name=\"deploy-window\" value=\"tonight\" data-recommended> Tonight - quiet traffic</label>" +
        "<label><input type=\"radio\" name=\"deploy-window\" value=\"monday\"> Monday - the team is around</label>" +
        "</div>";

    private readonly ITestOutputHelper _out;
    private readonly string _runId = Guid.NewGuid().ToString("N")[..12];
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-dev-report-link-" + Guid.NewGuid().ToString("N"));
    private readonly string _sessionOnTheRoster = Guid.NewGuid().ToString("D");
    private readonly string _sessionNeverSeen = Guid.NewGuid().ToString("D");
    private readonly string _sessionThatLeft = Guid.NewGuid().ToString("D");
    private readonly string _directorId;

    private string? _priorHosted;
    private GatewayHost _gateway = null!;
    private FakeTunnelDirector? _director;
    private string _deviceKeyA = "";
    private string _deviceKeyB = "";
    private string _sessionKeyRoster = "";
    private string _sessionKeyNeverSeen = "";
    private string _sessionKeyThatLeft = "";
    private TenantId _tenantA;

    public DevReportLinkRouteTests(ITestOutputHelper output)
    {
        _out = output;
        _directorId = "director-dl-" + _runId;
    }

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: SharedToken, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();

        var a = HostedTestEnrollment.Enroll(_gateway, $"sub-dl-a-{_runId}", $"dl-a-{_runId}@example.com", $"dev-dla-{_runId}", "MDLA");
        var b = HostedTestEnrollment.Enroll(_gateway, $"sub-dl-b-{_runId}", $"dl-b-{_runId}@example.com", $"dev-dlb-{_runId}", "MDLB");
        Assert.NotEqual(a.Tenant.Value, b.Tenant.Value);
        _tenantA = a.Tenant;
        _deviceKeyA = a.DeviceKey;
        _deviceKeyB = b.DeviceKey;

        _sessionKeyRoster = Mint(_sessionOnTheRoster);
        _sessionKeyNeverSeen = Mint(_sessionNeverSeen);
        _sessionKeyThatLeft = Mint(_sessionThatLeft);
    }

    private string Mint(string sessionId)
    {
        var key = GatewaySessionKey.Mint();
        Assert.True(_gateway.SessionKeys.Register(_tenantA, _directorId, sessionId, GatewaySessionKey.Hash(key), DateTime.UtcNow.AddHours(1)));
        return key;
    }

    public async Task DisposeAsync()
    {
        if (_director is not null) await _director.DisposeAsync();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
    }

    // ---------------------------------------------------------------- helpers

    private static SessionDto Row(string sid, int? number, string? name) => new()
    {
        SessionId = sid,
        Number = number,
        Name = name,
        ActivityState = "WaitingForInput",
        LastActivityAt = DateTime.UtcNow,
    };

    private static string Report() =>
        "<header data-dev-report=\"header\" data-dev-report-status=\"waiting-on-you\"><h1>Gateway failures</h1></header>" +
        "<section data-dev-report=\"summary\"><p>The failures doubled.</p></section>" +
        $"<section data-dev-report=\"questions\">{Question}</section>" +
        "<section data-dev-report=\"detail\"><table id=\"t\"><tr><th></th><th>Failures</th></tr>" +
        "<tr><th scope=\"row\">Gateway</th><td>42</td></tr></table></section>";

    private HttpClient Client(string? bearer)
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/"),
            Timeout = TimeSpan.FromMinutes(2),
        };
        if (bearer is not null) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return http;
    }

    private async Task<string> PublishAsync(string sid, string sessionKey, string key)
    {
        using var http = Client(sessionKey);
        using var resp = await http.PostAsync($"sessions/{sid}/dev-reports",
            JsonContent.Create(new { key, html = Report() }));
        var text = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"publish {sid} -> {(int)resp.StatusCode}: {(text.Length > 200 ? text[..200] : text)}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return JsonDocument.Parse(text).RootElement.GetProperty("report").GetProperty("id").GetString()!;
    }

    /// <summary>A browser navigation to the link: an HTML Accept and a device's User-Agent.</summary>
    private async Task<HttpResponseMessage> NavigateAsync(string path, string userAgent, string? bearer)
    {
        using var http = Client(bearer);
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
        req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        var resp = await http.SendAsync(req);
        // Read the body here: the client is disposed on the way out and a 404's sentence is part of the proof.
        var body = await resp.Content.ReadAsStringAsync();
        resp.Content = new StringContent(body);
        _out.WriteLine($"GET {path} ({(userAgent == PhoneAgent ? "phone" : "desktop")}) -> {(int)resp.StatusCode} {resp.Headers.Location}");
        return resp;
    }

    private async Task<JsonElement> ReadJsonAsync(string path, string bearer)
    {
        using var http = Client(bearer);
        using var resp = await http.GetAsync(path);
        var text = await resp.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    // ---------------------------------------------------------------- the pure policy

    [Fact]
    public void Target_PhoneUserAgent_IsThePhoneAppsReportLanding()
    {
        Assert.Equal("/mobile/report/r-1", DevReportLinkRoute.Target("r-1", PhoneAgent));
    }

    [Fact]
    public void Target_DesktopUserAgent_IsTheCockpitsReportLanding()
    {
        Assert.Equal("/report/r-1", DevReportLinkRoute.Target("r-1", DesktopAgent));
    }

    [Fact]
    public void Target_AnIdentifierNeedingEscaping_IsEscapedIntoThePath()
    {
        Assert.Equal("/mobile/report/a%20b%26c", DevReportLinkRoute.Target("a b&c", PhoneAgent));
        Assert.Equal("/report/a%20b%26c", DevReportLinkRoute.Target("a b&c", DesktopAgent));
    }

    [Theory]
    [InlineData("/r/abc", "abc")]
    [InlineData("/r/abc/", "abc")]
    [InlineData("/r/", null)]
    [InlineData("/r", null)]
    [InlineData("/r/a/b", null)]
    [InlineData("/reports/abc", null)]
    public void ReadReportId_OnlyMatchesOneSegmentUnderTheLinkPrefix(string path, string? expected)
    {
        Assert.Equal(expected, DevReportLinkRoute.ReadReportId(path));
    }

    // ---------------------------------------------------------------- the live route

    [Fact]
    public async Task LinkRoute_PhoneUserAgent_RedirectsToThePhoneAppsReportLanding()
    {
        var reportId = await PublishAsync(_sessionOnTheRoster, _sessionKeyRoster, @"C:\work\phone.html");

        using var resp = await NavigateAsync($"/r/{reportId}", PhoneAgent, _deviceKeyA);

        Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
        Assert.Equal($"/mobile/report/{reportId}", resp.Headers.Location?.ToString());
    }

    [Fact]
    public async Task LinkRoute_DesktopUserAgent_RedirectsToTheCockpitsReportLanding()
    {
        var reportId = await PublishAsync(_sessionOnTheRoster, _sessionKeyRoster, @"C:\work\desktop.html");

        using var resp = await NavigateAsync($"/r/{reportId}", DesktopAgent, _deviceKeyA);

        Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
        Assert.Equal($"/report/{reportId}", resp.Headers.Location?.ToString());
    }

    [Fact]
    public async Task LinkRoute_PhoneNavigation_AnswersBeforeTheMobileFrontDoor()
    {
        // ORDERING TEST ONE. The mobile front door sends every phone HTML navigation that is not already under
        // /mobile to /mobile/. If this route is ever registered after it, a phone opening a printed report link
        // lands on the mobile home screen and never sees the report - silently, with every other test green.
        var reportId = await PublishAsync(_sessionOnTheRoster, _sessionKeyRoster, @"C:\work\ordering.html");

        using var resp = await NavigateAsync($"/r/{reportId}", PhoneAgent, _deviceKeyA);

        var location = resp.Headers.Location?.ToString() ?? "";
        Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
        Assert.NotEqual("/mobile/", location);
        Assert.Contains(reportId, location);
    }

    // ------------------------------------------- no account is needed here, and nothing is looked up

    [Fact]
    public async Task LinkRoute_WithNoCredentialAtAll_StillRoutesTheDesktopToTheCockpitsLanding()
    {
        // ORDERING TEST TWO, and the point of this phase. Registered after the authentication middleware, this
        // request is bounced to /signin?next=/r/{id} instead - and `next` is resolved at the end of the sign-in
        // round trip by the shell's ROUTER, which has no /r/:id route on either surface. The printed address
        // would never be requested a second time, so signed out it would land nowhere at all.
        var reportId = await PublishAsync(_sessionOnTheRoster, _sessionKeyRoster, @"C:\work\signedout.html");

        using var resp = await NavigateAsync($"/r/{reportId}", DesktopAgent, bearer: null);

        Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
        Assert.Equal($"/report/{reportId}", resp.Headers.Location?.ToString());
    }

    [Fact]
    public async Task LinkRoute_WithNoCredentialAtAll_StillRoutesThePhoneToTheMobileLanding()
    {
        // The same request from a phone has to clear BOTH gates - authentication and the mobile front door - so
        // this one test goes red for either reorder, with a different wrong address each time.
        var reportId = await PublishAsync(_sessionOnTheRoster, _sessionKeyRoster, @"C:\work\signedoutphone.html");

        using var resp = await NavigateAsync($"/r/{reportId}", PhoneAgent, bearer: null);

        Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
        Assert.Equal($"/mobile/report/{reportId}", resp.Headers.Location?.ToString());
    }

    [Fact]
    public async Task LinkRoute_AnotherAccountsCredential_GetsTheSameAnswerBecauseNothingHereIsAuthorised()
    {
        // This route authorises nothing: it echoes back an identifier the caller already held and names which
        // app should open it. Whether THIS account may read that report is the app's authenticated read, behind
        // the app's own sign-in. Re-add a tenant gate here and this goes red.
        var reportId = await PublishAsync(_sessionOnTheRoster, _sessionKeyRoster, @"C:\work\tenant.html");

        using var resp = await NavigateAsync($"/r/{reportId}", DesktopAgent, _deviceKeyB);

        Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
        Assert.Equal($"/report/{reportId}", resp.Headers.Location?.ToString());
    }

    [Fact]
    public async Task LinkRoute_AnIdentifierNoReportHasEverHad_IsRoutedExactlyLikeARealOne()
    {
        // Nothing is looked up here, so there is no 404 and no 403 - and therefore nothing whose answer differs
        // between "this report exists" and "it does not". The APP says "this report does not appear". Re-add the
        // store lookup here and this goes red.
        var unknown = Guid.NewGuid().ToString("D");

        using var resp = await NavigateAsync($"/r/{unknown}", DesktopAgent, _deviceKeyA);

        Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
        Assert.Equal($"/report/{unknown}", resp.Headers.Location?.ToString());
        Assert.Equal("", await resp.Content.ReadAsStringAsync());
    }

    // ---------------------------------------------------------------- the words on the record

    [Fact]
    public async Task ReportRecord_SessionOnTheRoster_CarriesTheNumberAndNameFromTheRoster()
    {
        _director = await FakeTunnelDirector.StartAsync(_gateway, _deviceKeyA, _directorId, "MDLA");
        await _director.PushDeltaAsync(Row(_sessionOnTheRoster, 121, "devthrottle - tool not working on linux"));

        var reportId = await PublishAsync(_sessionOnTheRoster, _sessionKeyRoster, @"C:\work\roster.html");

        // The DETAIL carries them...
        var detail = await ReadJsonAsync($"dev-reports/{reportId}", _deviceKeyA);
        var report = detail.GetProperty("report");
        Assert.Equal("121 devthrottle - tool not working on linux", report.GetProperty("sessionLabel").GetString());
        Assert.Equal("back to 121 devthrottle - tool not working on linux", report.GetProperty("backLabel").GetString());

        // ...and so does the LIST, so no client has to compose them for itself.
        var list = await ReadJsonAsync($"dev-reports?sessionId={_sessionOnTheRoster}", _deviceKeyA);
        var row = list.GetProperty("reports").EnumerateArray().Single(r => r.GetProperty("id").GetString() == reportId);
        Assert.Equal("121 devthrottle - tool not working on linux", row.GetProperty("sessionLabel").GetString());
        Assert.Equal("back to 121 devthrottle - tool not working on linux", row.GetProperty("backLabel").GetString());
    }

    [Fact]
    public async Task ReportRecord_SessionGoneFromTheRoster_CarriesTheNumberAndNameFromTheHistoryRow()
    {
        _director = await FakeTunnelDirector.StartAsync(_gateway, _deviceKeyA, _directorId, "MDLA");
        await _director.PushDeltaAsync(Row(_sessionThatLeft, 307, "the gateway worker"));
        var reportId = await PublishAsync(_sessionThatLeft, _sessionKeyThatLeft, @"C:\work\history.html");

        // The session LEAVES THE ROSTER. A snapshot is authoritative and prunes anything not in it, so after
        // this the roster genuinely does not hold the session and the words can only come from the durable
        // history row. (Disposing the Director instead would be weaker: the roster and the history row carry
        // the same two values, so a test that merely hoped the roster had gone would pass either way. The
        // revert that drops the history fallback is what separates the two legs - see the worker report.)
        await _director.PushSnapshotAsync(Row(Guid.NewGuid().ToString("D"), 999, "some other session"));

        var detail = await ReadJsonAsync($"dev-reports/{reportId}", _deviceKeyA);
        var report = detail.GetProperty("report");
        Assert.Equal("307 the gateway worker", report.GetProperty("sessionLabel").GetString());
        Assert.Equal("back to 307 the gateway worker", report.GetProperty("backLabel").GetString());
    }

    [Fact]
    public async Task ReportRecord_SessionTheGatewayKnowsNothingAbout_SaysOnePlainTrueSentenceAndNoIdentifier()
    {
        var reportId = await PublishAsync(_sessionNeverSeen, _sessionKeyNeverSeen, @"C:\work\unknown.html");

        var detail = await ReadJsonAsync($"dev-reports/{reportId}", _deviceKeyA);
        var report = detail.GetProperty("report");
        var label = report.GetProperty("sessionLabel").GetString()!;
        var back = report.GetProperty("backLabel").GetString()!;

        Assert.Equal("the session", label);
        Assert.Equal("back to the session", back);
        // THE RULE: no internal identifier anywhere the owner can see - not the whole id, not a prefix of it.
        Assert.DoesNotContain(_sessionNeverSeen, label, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_sessionNeverSeen, back, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_sessionNeverSeen[..8], back, StringComparison.OrdinalIgnoreCase);
    }
}
