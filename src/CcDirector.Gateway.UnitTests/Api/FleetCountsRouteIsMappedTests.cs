using System.Net;
using System.Net.Http.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests.Api;

/// <summary>
/// <c>GET /fleet/counts</c> is MAPPED by the Gateway (Web UI Polish phase 3). The handler tests call
/// <see cref="FleetCountsEndpoints.Handle"/> directly, which proves the rules and nothing about a request reaching
/// them: deleting the <c>FleetCountsEndpoints.Map</c> line from <see cref="GatewayEndpoints.Map"/> would leave them all
/// green while the phone's About page got a 404. So this maps the real Gateway routes, the way the host does, on an
/// in-memory test server (no port), and asks for the counts. Self-host, so the read is the one Local tenant.
/// </summary>
public sealed class FleetCountsRouteIsMappedTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-fleet-counts-route-" + Guid.NewGuid().ToString("N"));
    private readonly GatewayDbTestHarness _harness = new();
    private readonly DirectorRegistry _registry;
    private readonly PushedSessionStore _pushed = new();
    private WebApplication? _app;
    private HttpClient _http = null!;

    public FleetCountsRouteIsMappedTests()
    {
        Directory.CreateDirectory(_root);
        _registry = new DirectorRegistry(Path.Combine(_root, "instances"));
    }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.UseRouting();
        GatewayEndpoints.Map(app, false, _registry, version: "test", token: "test-token",
            tenantBoundary: new HostedTenantBoundary(new SingleTenantContext(), _harness.OpenDevices(), hosted: false),
            aiGate: TenantAiGate.SelfHost, sessionFactoryOf: _ => CcDirector.Gateway.History.SessionFactoryLookup.NotKnown,
            pushedSessions: _pushed);
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

    [Fact]
    public async Task GetFleetCounts_ThroughTheGatewaysOwnRoutes_AnswersTheAccountsCounts()
    {
        // Arrange: two Directors of the account; one has pushed three sessions, the other no roster at all.
        _registry.RegisterFromStream("director-1", "PC-1", "user", "1.0", pid: 1, startedAt: Now, tenant: TenantId.Local);
        _registry.RegisterFromStream("director-2", "PC-2", "user", "1.0", pid: 2, startedAt: Now, tenant: TenantId.Local);
        _pushed.RegisterConnection(TenantId.Local, "director-1", "conn-1");
        Assert.True(_pushed.ApplySnapshot(TenantId.Local, "director-1", "conn-1", 1,
            Enumerable.Range(0, 3).Select(_ => new SessionDto { SessionId = Guid.NewGuid().ToString(), DirectorId = "director-1" }).ToArray()));

        // Act
        using var response = await _http.GetAsync("/fleet/counts");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var counts = await response.Content.ReadFromJsonAsync<FleetCountsDto>();
        Assert.NotNull(counts);
        Assert.Equal((2, 3), (counts!.Directors, counts.Sessions));
    }
}
