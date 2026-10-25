using System.Net;
using System.Net.Http.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Alerts;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Alerts;

/// <summary>
/// THE FOUR ALERTS ROUTES, in memory (the Alerts mission). The routes are mapped by their own <see cref="AlertEndpoints.Map"/>
/// over a real <see cref="AlertsService"/>, a real restart-request store and the real marks on a throwaway SQLite file,
/// served by an in-memory test server - no port, no clock but the test's own. The account comes from a test header the
/// resolver reads, standing in for the credential, so two accounts are two header values. The same claims over a real
/// hosted Gateway with real device keys are in <c>AlertRoutesTenantIsolationTests</c>.
/// </summary>
public sealed class AlertEndpointsTests : IAsyncLifetime
{
    private const string AccountHeader = "X-Test-Account";
    private const string DeviceTypeHeader = "X-Test-Device-Type";
    private const string NoDevice = "none";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly DirectorRestartRequestStore _restarts;
    private DateTime _now = new(2026, 10, 25, 21, 0, 0, DateTimeKind.Utc);
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    private static readonly TenantId Alice = new("acct-alice");
    private static readonly TenantId Bob = new("acct-bob");

    public AlertEndpointsTests()
    {
        _restarts = new DirectorRestartRequestStore { Clock = () => _now };
    }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        _app = builder.Build();
        // Stands in for AuthMiddleware: the device the credential belongs to. "browser" and "phone" are the owner's own;
        // "workstation" is a Director's own key; "none" is a credential with no device row, as the shared token is.
        _app.Use(async (ctx, next) =>
        {
            var type = ctx.Request.Headers.TryGetValue(DeviceTypeHeader, out var t) ? t.ToString() : "browser";
            if (type != NoDevice)
                ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = new DeviceCredentialIdentity("dev-1", null, type, "active");
            await next();
        });
        var service = new AlertsService(new IAlertSource[] { new RestartRequestAlertSource(_restarts) },
            new AlertMarkStore(_harness.Open()), () => _now);
        AlertEndpoints.Map(_app,
            ctx => ctx.Request.Headers.TryGetValue(AccountHeader, out var v) ? new TenantId(v.ToString()) : null,
            service);
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _harness.Dispose();
    }

    private string Pending(TenantId tenant, string machine = "mac-mini")
    {
        var r = new DirectorRestartRequestDto { Machine = machine, DirectorId = "d", Reason = "x", Title = "t", AskedBySentence = "s", RequestedBySessionName = "s" };
        Assert.True(_restarts.TryCreate(tenant, r, out _));
        return r.Id;
    }

    private string Declined(TenantId tenant, string machine)
    {
        var id = Pending(tenant, machine);
        Assert.True(_restarts.Decline(tenant, id, "no", out _));
        return "restart-request:" + id;
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, TenantId? tenant, object? body = null,
        string deviceType = "browser")
    {
        var req = new HttpRequestMessage(method, path);
        if (tenant is { } t) req.Headers.Add(AccountHeader, t.Value);
        req.Headers.Add(DeviceTypeHeader, deviceType);
        if (body is not null) req.Content = JsonContent.Create(body);
        return _client.SendAsync(req);
    }

    private async Task<AlertListResponse> List(TenantId tenant)
    {
        var resp = await Send(HttpMethod.Get, AlertEndpoints.ListRoute, tenant);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<AlertListResponse>())!;
    }

    [Fact]
    public async Task EveryRoute_WithNoAccount_Is403()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, "/gateway/alerts", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, "/gateway/alerts/seen", null, new { ids = Array.Empty<string>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, "/gateway/alerts/clear", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, "/gateway/alerts/restart-request:x/clear", null)).StatusCode);
    }

    [Theory]
    [InlineData("workstation")]
    [InlineData(NoDevice)]
    public async Task Seen_FromAnythingButTheOwnersDevice_Is403_AndMarksNothing(string deviceType)
    {
        var id = "restart-request:" + Pending(Alice);

        var resp = await Send(HttpMethod.Post, "/gateway/alerts/seen", Alice, new { ids = new[] { id } }, deviceType);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Contains("owner_only", await resp.Content.ReadAsStringAsync());
        Assert.False(Assert.Single((await List(Alice)).Alerts).Seen);
    }

    [Theory]
    [InlineData("workstation")]
    [InlineData(NoDevice)]
    public async Task ClearOne_FromAnythingButTheOwnersDevice_Is403_AndClearsNothing(string deviceType)
    {
        var id = Declined(Alice, "mac-a");

        var resp = await Send(HttpMethod.Post, $"/gateway/alerts/{Uri.EscapeDataString(id)}/clear", Alice, null, deviceType);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Contains("owner_only", await resp.Content.ReadAsStringAsync());
        Assert.Single((await List(Alice)).Alerts);
    }

    [Theory]
    [InlineData("workstation")]
    [InlineData(NoDevice)]
    public async Task ClearAll_FromAnythingButTheOwnersDevice_Is403_AndClearsNothing(string deviceType)
    {
        Declined(Alice, "mac-a");

        var resp = await Send(HttpMethod.Post, "/gateway/alerts/clear", Alice, null, deviceType);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Contains("owner_only", await resp.Content.ReadAsStringAsync());
        Assert.Single((await List(Alice)).Alerts);
    }

    [Theory]
    [InlineData("phone")]
    [InlineData("workstation")]
    public async Task List_IsReadByAnyCredentialThatResolvesTheAccount(string deviceType)
    {
        Pending(Alice);

        var resp = await Send(HttpMethod.Get, "/gateway/alerts", Alice, null, deviceType);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task List_CarriesTheAlertsAndTheRedCount_ForTheCallersAccountOnly()
    {
        var aliceId = Pending(Alice);
        Pending(Bob);

        var list = await List(Alice);

        var alert = Assert.Single(list.Alerts);
        Assert.Equal("restart-request:" + aliceId, alert.Id);
        Assert.Equal(1, list.NeedsAnswerCount);
    }

    // Phase 5 review, observation 3: the client colours an outcome by the tone the Gateway stamps, under the field name
    // its AlertDto type reads.
    [Fact]
    public async Task List_CarriesEachAlertsTone_UnderTheFieldNameTheClientReads()
    {
        Pending(Alice);

        var resp = await Send(HttpMethod.Get, AlertEndpoints.ListRoute, Alice);

        using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var alert = Assert.Single(doc.RootElement.GetProperty("alerts").EnumerateArray());
        Assert.Equal(AlertTones.Neutral, alert.GetProperty("tone").GetString());
        Assert.Equal("Waiting", alert.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ClearOne_WhileItNeedsAnAnswer_Is409WithTheSentence()
    {
        var id = "restart-request:" + Pending(Alice);

        var resp = await Send(HttpMethod.Post, $"/gateway/alerts/{id}/clear", Alice);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var text = await resp.Content.ReadAsStringAsync();
        Assert.Contains("needs_answer", text);
        Assert.Contains(AlertsService.NeedsAnswerSentence, text);
    }

    [Fact]
    public async Task ClearOne_AnotherAccountsAlert_Is404_AndItsOwnerStillSeesIt()
    {
        var id = Declined(Alice, "mac-a");

        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, $"/gateway/alerts/{id}/clear", Bob)).StatusCode);
        Assert.Single((await List(Alice)).Alerts);
    }

    [Fact]
    public async Task ClearOne_AClosedAlert_Clears()
    {
        var id = Declined(Alice, "mac-a");

        var resp = await Send(HttpMethod.Post, $"/gateway/alerts/{id}/clear", Alice);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, (await resp.Content.ReadFromJsonAsync<AlertClearResponse>())!.Cleared);
        Assert.Empty((await List(Alice)).Alerts);
    }

    [Fact]
    public async Task ClearOne_AnIdWithItsColonUrlEncoded_AsTheClientSendsIt_Clears()
    {
        var id = Declined(Alice, "mac-a");

        var resp = await Send(HttpMethod.Post, $"/gateway/alerts/{Uri.EscapeDataString(id)}/clear", Alice);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("%3A", Uri.EscapeDataString(id));
        Assert.Empty((await List(Alice)).Alerts);
    }

    [Fact]
    public async Task ARefusal_CarriesItsSentenceInTheErrorField()
    {
        var id = "restart-request:" + Pending(Alice);

        var resp = await Send(HttpMethod.Post, $"/gateway/alerts/{Uri.EscapeDataString(id)}/clear", Alice);

        using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(AlertsService.NeedsAnswerSentence, doc.RootElement.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("restart%3Aabc")]
    [InlineData("nonsense")]
    [InlineData("restart-request%3A")]
    [InlineData("%3Aabc")]
    public async Task ClearOne_AMalformedId_Is400(string escapedId)
    {
        var resp = await Send(HttpMethod.Post, $"/gateway/alerts/{escapedId}/clear", Alice);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("invalid_alert_id", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ClearAll_SaysHowManyItCleared_AndLeavesWhatNeedsAnAnswer()
    {
        var pending = "restart-request:" + Pending(Alice, "mac-p");
        Declined(Alice, "mac-a");
        Declined(Alice, "mac-b");
        Declined(Bob, "mac-c");

        var resp = await Send(HttpMethod.Post, "/gateway/alerts/clear", Alice);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, (await resp.Content.ReadFromJsonAsync<AlertClearResponse>())!.Cleared);
        Assert.Equal(new[] { pending }, (await List(Alice)).Alerts.Select(a => a.Id));
        Assert.Single((await List(Bob)).Alerts);
    }

    [Fact]
    public async Task Seen_MarksTheGivenAlerts_AndAChangeMakesThemUnseenAgain()
    {
        var raw = Pending(Alice);
        var id = "restart-request:" + raw;

        var resp = await Send(HttpMethod.Post, "/gateway/alerts/seen", Alice, new { ids = new[] { id } });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, (await resp.Content.ReadFromJsonAsync<AlertSeenResponse>())!.Marked);
        Assert.True(Assert.Single((await List(Alice)).Alerts).Seen);

        _now = _now.AddMinutes(1);
        _restarts.Accept(Alice, raw, out _);
        Assert.False(Assert.Single((await List(Alice)).Alerts).Seen);
    }

    [Fact]
    public async Task Seen_ARequestWithAMalformedId_Is400_AndMarksNothing()
    {
        var id = "restart-request:" + Pending(Alice);

        var resp = await Send(HttpMethod.Post, "/gateway/alerts/seen", Alice, new { ids = new[] { id, "restart:abc" } });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.False(Assert.Single((await List(Alice)).Alerts).Seen);
    }

    [Fact]
    public async Task Seen_WithoutAnIdsArray_Is400()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, "/gateway/alerts/seen", Alice, new { })).StatusCode);
    }

    [Fact]
    public async Task Seen_NotJson_Is400()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/gateway/alerts/seen")
        {
            Content = new StringContent("{not json", System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(AccountHeader, Alice.Value);
        req.Headers.Add(DeviceTypeHeader, "browser");

        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(req)).StatusCode);
    }
}

/// <summary>
/// THE ALERTS ROUTES ARE THE OWNER'S (the Alerts mission). A session key - raised or not - is refused every one of them,
/// exactly as it is refused the owner's list of message requests, because none is on <see cref="SessionKeyGuard"/>'s
/// allow list. The positive control in front: the one comparable owner read a session key MAY make is still allowed, so
/// a refusal here is the guard deciding, not the guard refusing everything.
/// </summary>
public sealed class AlertRoutesSessionKeyGuardTests
{
    [Fact]
    public void TheGuard_StillAllowsTheComparableSessionRead()
    {
        Assert.True(SessionKeyGuard.Check("GET", "/gateway/director-restart-requests").Allowed);
    }

    [Theory]
    [InlineData("GET", "/gateway/alerts")]
    [InlineData("POST", "/gateway/alerts/seen")]
    [InlineData("POST", "/gateway/alerts/clear")]
    [InlineData("POST", "/gateway/alerts/restart-request:3f9c/clear")]
    [InlineData("GET", "/Gateway/Alerts")]
    public void ASessionKey_IsRefusedEveryAlertsRoute_RaisedOrNot(string method, string path)
    {
        Assert.False(SessionKeyGuard.Check(method, path).Allowed);
        Assert.False(SessionKeyGuard.Check(method, path, raised: true).Allowed);
    }
}
