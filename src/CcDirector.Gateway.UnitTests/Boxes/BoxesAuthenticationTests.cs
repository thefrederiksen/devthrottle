using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Boxes;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Boxes;

/// <summary>
/// <c>GET /boxes</c> behind the PRODUCTION authentication middleware (the Factory Boxes mission, phase 1; the
/// review finding on pull request #4271). <see cref="BoxesEndpointTests"/> stands in a fake identity for the
/// authentication middleware, so its signed-out case only reaches the gate's own 404. These tests put the real
/// <see cref="AuthMiddleware.Run"/>, configured as a hosted Gateway configures it, in front of the Boxes gate on an
/// in-memory test server, over a hosted device registry in a throwaway database file. A signed-out caller must be
/// answered 401 by authentication before the gate runs, and a real browser key of a flagged account must pass
/// authentication and reach the list - the second proves the first is authentication's answer and not a dead pipeline.
/// </summary>
public sealed class BoxesAuthenticationTests : IAsyncLifetime
{
    private const string OwnerSubject = "subject-boxes-owner";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly PushedHostedDesktopStore _desktops = new();
    private DeviceRegistry _devices = null!;
    private TenantId _owner;
    private string _ownerBrowserKey = "";
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var database = _harness.Open();
        _devices = new DeviceRegistry(database, _harness.LegacyPath("devices.json"), isHosted: true);
        _owner = new TenantRegistry(database).MintOrLookupBySubject(OwnerSubject, "owner@example.com");
        _ownerBrowserKey = _devices.RegisterForTenant(_owner, OwnerSubject, "owner-browser", "LAPTOP", deviceType: "browser").DeviceKey;

        var boxes = new BoxesSwitch(_owner.Value);
        var boundary = new HostedTenantBoundary(new AsyncLocalTenantContext(), _devices, hosted: true);
        Func<HttpContext, TenantId?> resolve = ctx => GatewayEndpoints.ResolveReadTenant(ctx, boundary);
        var requireToken = new AuthMiddleware.RequireToken { Hosted = true, Devices = _devices, Boundary = boundary };

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Services.AddRouting();
        builder.Services.AddLogging();
        builder.WebHost.UseTestServer();
        _app = builder.Build();
        _app.Use(async (ctx, next) => await AuthMiddleware.Run(ctx, requireToken, next));
        BoxesEndpoints.Map(BoxesGate.Group(_app, boxes, resolve), _desktops, resolve);
        await _app.StartAsync();
        _client = _app.GetTestClient();

        _desktops.RegisterConnection(_owner, "dir-owner", "conn-dir-owner", "devlinux", "devlinux slot 1");
        _desktops.ApplySnapshot(_owner, "dir-owner", "conn-dir-owner", 1,
            new[] { new HostedDesktopDto { BoxId = "demo-desktop-soren", DisplayName = "Soren's demo desktop" } });
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        _devices.Dispose();
        _harness.Dispose();
    }

    private Task<HttpResponseMessage> Get(string? bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, BoxesEndpoints.ListRoute);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return _client.SendAsync(request);
    }

    [Fact]
    public async Task GetBoxes_SignedOut_ProductionAuthenticationAnswers401()
    {
        var response = await Get(bearer: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetBoxes_UnknownKey_ProductionAuthenticationAnswers401()
    {
        var response = await Get(bearer: "not-a-device-key");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetBoxes_FlaggedAccountsBrowserKey_PassesAuthenticationAndGetsTheList()
    {
        var response = await Get(_ownerBrowserKey);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, body.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("demo-desktop-soren", body.RootElement.GetProperty("boxes")[0].GetProperty("boxId").GetString());
    }
}
