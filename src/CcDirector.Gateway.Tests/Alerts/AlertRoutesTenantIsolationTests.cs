using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests.Alerts;

/// <summary>
/// THE FOUR ALERTS ROUTES OVER A REAL HOSTED GATEWAY, with two accounts (the Alerts mission). Each account's records are
/// seeded straight into the host's own restart-request store, pending or declined, and every claim is read back
/// over HTTP on that account's own device key, so what is proven is the composition the running Gateway does: the routes
/// are mapped, the account is resolved from the credential, and one account never lists, marks or clears another's.
///
/// Every absence claim has a positive control in front of it: the account that owns a record reads it first.
/// </summary>
public sealed class AlertRoutesTenantIsolationTests : IAsyncLifetime
{
    private const string GatewayToken = "test-token";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string _keyA = "";
    private string _keyB = "";
    private TenantId _tenantA;
    private TenantId _tenantB;
    private string? _priorRoot;

    private readonly string _storageRoot =
        Path.Combine(Path.GetTempPath(), "cc-alerts-iso-storage-" + Guid.NewGuid().ToString("N"));
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-alerts-iso-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _storageRoot);

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: GatewayToken, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            promptLogPath: Path.Combine(_instancesDir, "prompt-log"),
            options: new GatewayHostOptions { Hosted = true });
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        _keyA = _gateway.Devices.Register("dev-a", "MA").DeviceKey;
        _keyB = _gateway.Devices.Register("dev-b", "MB").DeviceKey;
        _tenantA = _gateway.TenantRegistry.MintOrLookupBySubject("sub-alice", "alice@example.com");
        _tenantB = _gateway.TenantRegistry.MintOrLookupBySubject("sub-bob", "bob@example.com");
        _gateway.Devices.SetAccountBinding("dev-a", "sub-alice", _tenantA.Value);
        _gateway.Devices.SetAccountBinding("dev-b", "sub-bob", _tenantB.Value);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, true); } catch { /* best-effort */ }
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best-effort */ }
    }

    /// <summary>A pending restart request in the account; returns its alert id.</summary>
    private string SeedPendingRestart(TenantId tenant, string machine)
    {
        var request = new DirectorRestartRequestDto
        {
            Machine = machine,
            DirectorId = "d-" + machine,
            Reason = "install the update",
            Title = $"Restart the Director on {machine}?",
            AskedBySentence = "a session asks: install the update",
        };
        Assert.True(_gateway.DirectorRestartRequests.TryCreate(tenant, request, out _));
        return "restart-request:" + request.Id;
    }

    /// <summary>A declined restart request in the account - closed, so it can be cleared; returns its alert id. The store
    /// stamps it on its own clock, so the test names no clock.</summary>
    private string SeedDeclinedRestart(TenantId tenant, string machine)
    {
        var alertId = SeedPendingRestart(tenant, machine);
        Assert.True(_gateway.DirectorRestartRequests.Decline(tenant, alertId["restart-request:".Length..], "not now", out _));
        return alertId;
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string key, object? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        if (body is not null) req.Content = JsonContent.Create(body);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return _http.SendAsync(req);
    }

    private async Task<AlertListResponse> List(string key)
    {
        var resp = await Send(HttpMethod.Get, "gateway/alerts", key);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<AlertListResponse>())!;
    }

    [Fact]
    public async Task List_ShowsEachAccountOnlyItsOwnAlerts_WithTheRedCountOfOnlyItsOwnNeedsAnswer()
    {
        var restartA = SeedPendingRestart(_tenantA, "mac-a");
        var closedA = SeedDeclinedRestart(_tenantA, "mac-a2");
        var restartB = SeedPendingRestart(_tenantB, "mac-b");

        var a = await List(_keyA);
        var b = await List(_keyB);

        // Positive controls: each account reads its own.
        Assert.Equal(new[] { restartA, closedA }, a.Alerts.Select(x => x.Id));
        Assert.Equal(1, a.NeedsAnswerCount);
        Assert.Equal(new[] { restartB }, b.Alerts.Select(x => x.Id));
        Assert.Equal(1, b.NeedsAnswerCount);
        // And the absence claims, both directions.
        Assert.DoesNotContain(a.Alerts, x => x.Id == restartB);
        Assert.DoesNotContain(b.Alerts, x => x.Id == restartA || x.Id == closedA);
    }

    [Fact]
    public async Task List_TheBodyAlwaysCarriesTheAlertsArray_EvenWhenEmpty()
    {
        var resp = await Send(HttpMethod.Get, "gateway/alerts", _keyA);
        var text = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("\"alerts\":[]", text);
        Assert.Contains("\"needsAnswerCount\":0", text);
    }

    [Fact]
    public async Task ClearOne_IsRefused409WhileTheAlertNeedsAnAnswer_WithTheSentence()
    {
        var restartA = SeedPendingRestart(_tenantA, "mac-a");

        var resp = await Send(HttpMethod.Post, $"gateway/alerts/{restartA}/clear", _keyA);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Contains("This still needs your answer; it leaves when you answer it or when it expires.",
            await resp.Content.ReadAsStringAsync());
        Assert.Single((await List(_keyA)).Alerts);
    }

    [Fact]
    public async Task ClearOne_ClearsAClosedAlert_ForThatAccountOnly_AndAnotherAccountGets404()
    {
        var closedA = SeedDeclinedRestart(_tenantA, "mac-a2");

        var fromB = await Send(HttpMethod.Post, $"gateway/alerts/{closedA}/clear", _keyB);
        Assert.Equal(HttpStatusCode.NotFound, fromB.StatusCode);
        Assert.Single((await List(_keyA)).Alerts);

        var fromA = await Send(HttpMethod.Post, $"gateway/alerts/{closedA}/clear", _keyA);
        Assert.Equal(HttpStatusCode.OK, fromA.StatusCode);
        Assert.Equal(1, (await fromA.Content.ReadFromJsonAsync<AlertClearResponse>())!.Cleared);
        Assert.Empty((await List(_keyA)).Alerts);
    }

    [Fact]
    public async Task ClearAll_ClearsEverythingThatNeedsNoAnswer_SaysHowMany_AndTouchesNoOtherAccount()
    {
        var restartA = SeedPendingRestart(_tenantA, "mac-a");
        SeedDeclinedRestart(_tenantA, "mac-a1");
        SeedDeclinedRestart(_tenantA, "mac-a2");
        var closedB = SeedDeclinedRestart(_tenantB, "mac-b2");

        var resp = await Send(HttpMethod.Post, "gateway/alerts/clear", _keyA);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, (await resp.Content.ReadFromJsonAsync<AlertClearResponse>())!.Cleared);
        Assert.Equal(new[] { restartA }, (await List(_keyA)).Alerts.Select(x => x.Id));
        Assert.Equal(new[] { closedB }, (await List(_keyB)).Alerts.Select(x => x.Id));
    }

    [Theory]
    [InlineData("restart:abc")]
    [InlineData("nonsense")]
    [InlineData("restart-request:")]
    public async Task ClearOne_AMalformedId_Is400_NamingTheShape(string id)
    {
        var resp = await Send(HttpMethod.Post, $"gateway/alerts/{Uri.EscapeDataString(id)}/clear", _keyA);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("invalid_alert_id", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Seen_MarksOnlyTheCallersAlerts_AndAMalformedIdIs400()
    {
        var restartA = SeedPendingRestart(_tenantA, "mac-a");
        var restartB = SeedPendingRestart(_tenantB, "mac-b");

        var bad = await Send(HttpMethod.Post, "gateway/alerts/seen", _keyA, new { ids = new[] { "nonsense" } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var resp = await Send(HttpMethod.Post, "gateway/alerts/seen", _keyA, new { ids = new[] { restartA, restartB } });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, (await resp.Content.ReadFromJsonAsync<AlertSeenResponse>())!.Marked);

        Assert.True(Assert.Single((await List(_keyA)).Alerts).Seen);
        Assert.False(Assert.Single((await List(_keyB)).Alerts).Seen);
    }

    [Fact]
    public async Task Seen_WithNoIdsArray_Is400()
    {
        var resp = await Send(HttpMethod.Post, "gateway/alerts/seen", _keyA, new { });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
