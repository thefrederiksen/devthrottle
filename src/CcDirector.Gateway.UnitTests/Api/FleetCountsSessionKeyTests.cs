using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Api;

/// <summary>
/// <c>GET /fleet/counts</c> called with a SESSION key, through the real authentication middleware and its session-key
/// guard, on an in-memory test server (Web UI Polish phase 3). A new Gateway route is not added to the guard by mapping
/// it: a route the guard does not list answers a session key 403 while every handler test stays green. This test
/// fails that way if the "fleet/counts" line is removed from <see cref="SessionKeyGuard"/>.
/// </summary>
public sealed class FleetCountsSessionKeyTests : IAsyncLifetime
{
    private const string MachineToken = "test-machine-token-fleet-counts";
    private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-fleet-counts-key-" + Guid.NewGuid().ToString("N"));
    private readonly DirectorRegistry _registry;
    private readonly PushedSessionStore _pushed = new();
    private readonly SessionKeyRegistry _sessions;
    private readonly DeviceRegistry _devices;
    private WebApplication? _app;
    private HttpClient _http = null!;

    public FleetCountsSessionKeyTests()
    {
        Directory.CreateDirectory(_root);
        _registry = new DirectorRegistry(Path.Combine(_root, "instances"));
        _sessions = new SessionKeyRegistry(_harness.Open(), isHosted: false, clock: () => Now);
        _devices = _harness.OpenDevices();
    }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Services.AddRouting();
        builder.Services.AddLogging();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        var config = new AuthMiddleware.RequireToken { Hosted = false, Token = MachineToken, Devices = _devices, Sessions = _sessions };
        app.Use(async (ctx, next) => await AuthMiddleware.Run(ctx, config, next));
        FleetCountsEndpoints.Map(app, _registry, _pushed, TimeSpan.FromMinutes(10),
            new HostedTenantBoundary(new SingleTenantContext(), _devices, hosted: false));
        await app.StartAsync();
        _app = app;
        _http = app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        _registry.Dispose();
        _harness.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    private string SessionKey()
    {
        var key = GatewaySessionKey.Mint();
        if (!_sessions.Register(TenantId.Local, "director-local", Guid.NewGuid().ToString(), GatewaySessionKey.Hash(key), Now + TimeSpan.FromHours(1)))
            throw new InvalidOperationException("the test session key was not registered");
        return key;
    }

    private async Task<HttpResponseMessage> GetAsync(string? bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/fleet/counts");
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await _http.SendAsync(request);
    }

    [Fact]
    public async Task FleetCounts_WithASessionKey_Answers200WithTheCounts()
    {
        // Arrange: one Director of the account, with two sessions in its pushed roster.
        _registry.RegisterFromStream("director-local", "PC", "user", "1.0", pid: 1, startedAt: Now, tenant: TenantId.Local);
        _pushed.RegisterConnection(TenantId.Local, "director-local", "conn-1");
        Assert.True(_pushed.ApplySnapshot(TenantId.Local, "director-local", "conn-1", 1, new[]
        {
            new SessionDto { SessionId = Guid.NewGuid().ToString(), DirectorId = "director-local" },
            new SessionDto { SessionId = Guid.NewGuid().ToString(), DirectorId = "director-local" },
        }));

        // Act
        using var response = await GetAsync(SessionKey());

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var counts = await response.Content.ReadFromJsonAsync<FleetCountsDto>();
        Assert.NotNull(counts);
        Assert.Equal((1, 2), (counts!.Directors, counts.Sessions));
    }

    [Fact]
    public async Task FleetCounts_WithNoCredential_IsRefused()
    {
        using var response = await GetAsync(bearer: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
