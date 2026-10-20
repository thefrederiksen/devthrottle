using System.Net;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Boxes;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Boxes;

/// <summary>
/// <c>GET /boxes</c> and the <c>boxes</c> flag on <c>GET /account/status</c> (the Factory Boxes mission, phase 1),
/// booted on an in-memory test server the way GatewayHost maps them: the list inside <see cref="BoxesGate"/>, over the
/// real store, and the account status over a HOSTED tenant boundary. A stand-in for the authentication middleware puts
/// on each request the device identity the real one would, for the account the test names; a request that names none
/// is a signed-out caller, for whom no account is bound. (In production the authentication middleware answers a
/// signed-out caller 401 before any route runs - <see cref="BoxesAuthenticationTests"/> proves that with the real
/// middleware; the gate's own answer below is the second line behind it.)
/// </summary>
public sealed class BoxesEndpointTests : IAsyncLifetime
{
    private const string AccountHeader = "X-Test-Account";
    private static readonly TenantId Owner = new("11111111-1111-1111-1111-111111111111");
    private static readonly TenantId Other = new("22222222-2222-2222-2222-222222222222");
    private static readonly TenantId Unflagged = new("33333333-3333-3333-3333-333333333333");

    private readonly PushedHostedDesktopStore _desktops = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        // The owner and the other account both have Boxes on, so the isolation tests compare two accounts that may
        // each see a list - only their own.
        var boxes = new BoxesSwitch($"{Owner.Value},{Other.Value}");
        var boundary = new HostedTenantBoundary(new AsyncLocalTenantContext(), new DeviceRegistry(), hosted: true);
        Func<HttpContext, TenantId?> resolve = ctx => GatewayEndpoints.ResolveReadTenant(ctx, boundary);

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Services.AddRouting();
        builder.Services.AddLogging();
        builder.WebHost.UseTestServer();
        _app = builder.Build();
        _app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Headers.TryGetValue(AccountHeader, out var account) && !string.IsNullOrWhiteSpace(account))
            {
                ctx.Items[AuthMiddleware.DeviceTypeItemKey] = "browser";
                ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] =
                    new DeviceCredentialIdentity("browser-of-" + account, account.ToString(), "browser", "active");
            }
            await next();
        });
        BoxesEndpoints.Map(BoxesGate.Group(_app, boxes, resolve), _desktops, resolve);
        AccountStatusEndpoint.Map(_app, hosted: true, account: null, boundary, boxes: boxes);
        await _app.StartAsync();
        _client = _app.GetTestClient();

        Hosts(Owner, "dir-owner", "devlinux", "devlinux slot 1", ("demo-desktop-soren", "Soren's demo desktop"), ("box-2", "Second box"));
        Hosts(Other, "dir-other", "other-machine", "", ("their-box", "Their desktop"));
        Hosts(Unflagged, "dir-unflagged", "third-machine", "", ("hidden-box", "Hidden"));
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private void Hosts(TenantId tenant, string directorId, string machine, string directorName, params (string BoxId, string Name)[] desktops)
    {
        _desktops.RegisterConnection(tenant, directorId, "conn-" + directorId, machine, directorName);
        _desktops.ApplySnapshot(tenant, directorId, "conn-" + directorId, 1,
            desktops.Select(d => new HostedDesktopDto { BoxId = d.BoxId, DisplayName = d.Name }).ToArray());
    }

    private Task<HttpResponseMessage> Get(string path, TenantId? account)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (account is { } a) request.Headers.Add(AccountHeader, a.Value);
        return _client.SendAsync(request);
    }

    // ---------- GET /boxes ----------

    [Fact]
    public async Task GetBoxes_FlaggedAccount_GetsItsListInTheContractShape()
    {
        var response = await Get("/boxes", Owner);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(
            """{"count":2,"boxes":[""" +
            """{"boxId":"box-2","displayName":"Second box","directorId":"dir-owner","directorName":"devlinux slot 1","machineName":"devlinux","state":"Online","relativeDesktopPath":"/boxes/box-2/desktop/"},""" +
            """{"boxId":"demo-desktop-soren","displayName":"Soren's demo desktop","directorId":"dir-owner","directorName":"devlinux slot 1","machineName":"devlinux","state":"Online","relativeDesktopPath":"/boxes/demo-desktop-soren/desktop/"}""" +
            "]}",
            body);
    }

    [Fact]
    public async Task GetBoxes_AnotherAccountsBoxes_NeverAppear()
    {
        using var owner = JsonDocument.Parse(await (await Get("/boxes", Owner)).Content.ReadAsStringAsync());
        using var other = JsonDocument.Parse(await (await Get("/boxes", Other)).Content.ReadAsStringAsync());

        var ownerIds = owner.RootElement.GetProperty("boxes").EnumerateArray().Select(b => b.GetProperty("boxId").GetString()).ToList();
        var otherIds = other.RootElement.GetProperty("boxes").EnumerateArray().Select(b => b.GetProperty("boxId").GetString()).ToList();
        Assert.Equal(new[] { "box-2", "demo-desktop-soren" }, ownerIds);
        Assert.Equal(new[] { "their-box" }, otherIds);
    }

    [Fact]
    public async Task GetBoxes_UnflaggedAccount_Gets404WithNoBodyAsIfTheRouteDidNotExist()
    {
        var response = await Get("/boxes", Unflagged);
        var unmapped = await Get("/no-such-route", Unflagged);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(unmapped.StatusCode, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    // The gate's own second line, for a request no account is bound to. That production authentication answers a
    // signed-out caller 401 before this is reached is proven in BoxesAuthenticationTests.
    [Fact]
    public async Task GetBoxes_NoAccountBound_GateAnswers404WithNoBody()
    {
        var response = await Get("/boxes", account: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GetBoxes_DirectorDisconnected_ItsBoxesAreGone()
    {
        _desktops.UnregisterConnection(Owner, "dir-owner", "conn-dir-owner");

        var body = await (await Get("/boxes", Owner)).Content.ReadAsStringAsync();

        Assert.Equal("""{"count":0,"boxes":[]}""", body);
    }

    // ---------- the flag on GET /account/status ----------

    [Fact]
    public async Task AccountStatus_FlaggedAccount_SaysBoxesIsOn()
    {
        using var status = JsonDocument.Parse(await (await Get("/account/status", Owner)).Content.ReadAsStringAsync());

        Assert.True(status.RootElement.GetProperty("boxes").GetBoolean());
    }

    [Fact]
    public async Task AccountStatus_UnflaggedAccount_SaysBoxesIsOff()
    {
        using var status = JsonDocument.Parse(await (await Get("/account/status", Unflagged)).Content.ReadAsStringAsync());

        Assert.False(status.RootElement.GetProperty("boxes").GetBoolean());
    }
}
