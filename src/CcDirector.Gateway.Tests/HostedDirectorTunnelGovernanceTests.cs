using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection; // AddMessagePackProtocol (client)
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE 8-DAY EMPTY LEDGER, PROVED FIXED END TO END (issue #3124).
///
/// The governance ledger's emitter is fed by a funnel that, until this fix, was invoked from exactly one
/// place: the legacy same-machine HTTP legs (heartbeat + doorbell). Those legs are 403 on the hosted
/// gateway, so on hosted NO session transition could ever reach the ledger - not for any account, not
/// ever. Production proved it for eight consecutive days: every daily report read "0 agent sessions ran
/// yesterday" (and carried no waiting rows) for a tenant whose Directors were connected and working daily,
/// while every tunnel-riding feed (repo-state, dictation, rosters) landed normally.
///
/// This test rebuilds the exact hosted shape - a real hosted Gateway, a device key bound to an account,
/// a real SignalR tunnel - and proves the whole chain through PRODUCT surfaces:
///
///   tunnel PushSnapshot  ->  the funnel  ->  the ledger  ->  GET /gateway/reports/morning
///
/// The report is read the way the website sender reads it, so a green run here is the daily email's
/// waiting row existing on hosted AT ALL. On the pre-fix code this test cannot pass: the tunnel never fed
/// the funnel, the ledger stayed empty, and the report carried no waiting row for anyone.
/// </summary>
[Collection("DirectorRoot")]
public sealed class HostedDirectorTunnelGovernanceTests : IAsyncLifetime
{
    private const string GatewayToken = "test-token-tunnel-governance";
    private const string ServiceToken = "test-service-token-tunnel-governance";
    private const string AccountEmail = "gov-alice@example.com";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string? _priorHosted;
    private readonly string? _priorServiceToken;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-tunnel-governance-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string _deviceKey = "";

    public HostedDirectorTunnelGovernanceTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-tunnel-governance-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        _priorServiceToken = Environment.GetEnvironmentVariable(MorningReportEndpoint.ServiceTokenEnvVar);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _http?.Dispose();
        if (_gateway is not null) await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable(MorningReportEndpoint.ServiceTokenEnvVar, _priorServiceToken);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private async Task StartHostedGatewayAsync()
    {
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Environment.SetEnvironmentVariable(MorningReportEndpoint.ServiceTokenEnvVar, ServiceToken);
        Assert.True(GatewayHostedMode.IsHosted);

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: GatewayToken,
            authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        _deviceKey = _gateway.Devices.Register("dev-gov-alice", "GOV-PC").DeviceKey;
        var tenant = _gateway.TenantRegistry.MintOrLookupBySubject("sub-gov-alice", AccountEmail);
        _gateway.Devices.SetAccountBinding("dev-gov-alice", "sub-gov-alice", tenant.Value);
    }

    /// <summary>Connect the director tunnel exactly as a hosted Director does: device key auth, Hello, then pushes.</summary>
    private async Task<HubConnection> JoinDirectorTunnelAsync(string directorId)
    {
        var conn = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{_gateway.Port}/director-stream",
                o => o.AccessTokenProvider = () => Task.FromResult<string?>(_deviceKey))
            .AddMessagePackProtocol()
            .Build();
        await conn.StartAsync();
        await conn.InvokeAsync("Hello", new DirectorStreamHello
        {
            DirectorId = directorId,
            Version = "test",
            MachineName = "GOV-PC",
            User = "test",
            Pid = 1,
            StartedAt = DateTime.UtcNow,
        });
        return conn;
    }

    /// <summary>The report the website sender reads - same route, same auth, same shape.</summary>
    private async Task<JsonElement> GetMorningReportAsync()
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/gateway/reports/morning?account={AccountEmail}&date={today}&tz=UTC");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ServiceToken);
        var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private static string? TypeOf(JsonElement item) =>
        item.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

    [Fact]
    public async Task A_tunnel_push_reaches_the_ledger_and_the_morning_report()
    {
        await StartHostedGatewayAsync();
        await using var conn = await JoinDirectorTunnelAsync("dir-gov");

        // A session stops and waits for its human - exactly the transition the daily report exists to surface.
        await conn.InvokeAsync("PushSnapshot", 1L, new[]
        {
            new SessionDto { SessionId = "s-gov-1", Name = "gov - the waiting session", ActivityState = "WaitingForInput" },
        });

        var report = await GetMorningReportAsync();
        var attention = report.GetProperty("attention").EnumerateArray().ToList();
        // Exactly one waiting row, and it is THIS session: the tunnel push reached the ledger, the report
        // read it back, and the row the daily email exists to surface exists at all on hosted.
        Assert.Single(attention, a => TypeOf(a) == "waiting-session");
        var waiting = attention.Single(a => TypeOf(a) == "waiting-session");
        Assert.Equal("s-gov-1", waiting.GetProperty("session").GetString());

        // And the LAST WORD wins, through the tunnel too: the session came back, so the waiting row must go.
        await conn.InvokeAsync("PushSnapshot", 2L, new[]
        {
            new SessionDto { SessionId = "s-gov-1", Name = "gov - the waiting session", ActivityState = "Working" },
        });

        var after = await GetMorningReportAsync();
        Assert.DoesNotContain(after.GetProperty("attention").EnumerateArray(), a => TypeOf(a) == "waiting-session");
    }
}
