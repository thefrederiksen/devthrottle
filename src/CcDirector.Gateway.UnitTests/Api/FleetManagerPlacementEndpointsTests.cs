using System.Text;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Tests.Fleet;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace CcDirector.Gateway.Tests.Api;

/// <summary>
/// The Fleet Manager setting's routes (the Fleet Manager mission, step 5), driven through their handlers with a
/// real settings store and a fake world. What these cannot prove is that the host maps them on these paths; the
/// route strings are pinned as literals here, and the guard's refusal of a session key is in
/// <see cref="SessionKeyGuardTests"/>.
/// </summary>
public sealed class FleetManagerPlacementEndpointsTests : IDisposable
{
    private const string TenantHeader = "X-Test-Account";
    private static readonly TenantId Tenant = new("acct-fm-routes");
    private static readonly DateTime Now = new(2026, 9, 16, 14, 30, 0, DateTimeKind.Utc);
    private const string NewId = "50000000-0000-4000-8000-000000000002";
    private const string CallingSession = "50000000-0000-4000-8000-000000000009";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly TenantSettingsResolver _settings;
    private readonly FakePlacementWorld _world = new(Now, NewId);
    private readonly FleetManagerPlacementService _service;

    public FleetManagerPlacementEndpointsTests()
    {
        _settings = new TenantSettingsResolver(new TenantSettingsStore(_harness.Open()));
        _service = new FleetManagerPlacementService(_settings, _world, retirePoll: TimeSpan.Zero);
        _world.Machines.Add(new FleetManagerMachineFacts("WORKSTATION-A",
            new LauncherDto { MachineName = "WORKSTATION-A", LastSeenAt = Now }, LauncherReach.Connected, true,
            new[] { new DirectorDto { DirectorId = "dir-a", MachineName = "WORKSTATION-A", LastSeen = Now } }));
    }

    public void Dispose()
    {
        _service.Dispose();
        _harness.Dispose();
    }

    private static TenantId? ResolveTenant(HttpContext ctx)
        => ctx.Request.Headers.TryGetValue(TenantHeader, out var v) && v.ToString().Length > 0 ? new TenantId(v.ToString()) : null;

    private static DefaultHttpContext Request(TenantId? tenant, string? body = null, bool asSession = false, string? deviceType = null)
    {
        var ctx = new DefaultHttpContext();
        if (tenant is { } t) ctx.Request.Headers[TenantHeader] = t.Value;
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body ?? ""));
        if (asSession)
            ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] =
                new SessionCredentialIdentity(Guid.Parse(CallingSession), tenant ?? TenantId.Local, "dir-a");
        if (deviceType is not null)
            ctx.Items[AuthMiddleware.DeviceTypeItemKey] = deviceType;
        return ctx;
    }

    private static int Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    private static string? Error(IResult result)
    {
        var value = result.GetType().GetProperty("Value")!.GetValue(result)!;
        return value.GetType().GetProperty("error")!.GetValue(value) as string;
    }

    [Fact]
    public void Routes_AreTheMappedLiterals()
    {
        Assert.Equal("/gateway/fleet-manager/placement", FleetManagerPlacementEndpoints.PlacementRoute);
        Assert.Equal("/gateway/fleet-manager/start", FleetManagerPlacementEndpoints.StartRoute);
        Assert.Equal("/gateway/fleet-manager/restart", FleetManagerPlacementEndpoints.RestartRoute);
        Assert.Equal("/gateway/fleet-manager/move", FleetManagerPlacementEndpoints.MoveRoute);
    }

    [Fact]
    public async Task Read_ReturnsTheFoldedPlacement()
    {
        var result = await FleetManagerPlacementEndpoints.ReadAsync(Request(Tenant), ResolveTenant, _service);

        Assert.Equal(200, Status(result));
        var dto = Assert.IsType<JsonHttpResult<FleetManagerPlacementDto>>(result).Value!;
        Assert.Equal("WORKSTATION-A", Assert.Single(dto.Machines).Machine);
    }

    [Fact]
    public async Task EveryRoute_NoAccount_IsRefused()
    {
        Assert.Equal(403, Status(await FleetManagerPlacementEndpoints.ReadAsync(Request(null), ResolveTenant, _service)));
        Assert.Equal(403, Status(await FleetManagerPlacementEndpoints.SaveAsync(Request(null, "{}"), ResolveTenant, _service)));
        Assert.Equal(403, Status(await FleetManagerPlacementEndpoints.StartAsync(Request(null), ResolveTenant, _service)));
        Assert.Equal(403, Status(await FleetManagerPlacementEndpoints.RestartAsync(Request(null), ResolveTenant, _service)));
        Assert.Equal(403, Status(await FleetManagerPlacementEndpoints.MoveAsync(Request(null, "{}"), ResolveTenant, _service)));
        Assert.Empty(_world.Spawns);
    }

    [Fact]
    public async Task StartRestartMove_FromASessionKey_AreRefusedAndNothingStarts()
    {
        var body = "{\"agent\":\"Codex\",\"machine\":\"WORKSTATION-A\"}";

        var start = await FleetManagerPlacementEndpoints.StartAsync(Request(Tenant, asSession: true), ResolveTenant, _service);
        var restart = await FleetManagerPlacementEndpoints.RestartAsync(Request(Tenant, asSession: true), ResolveTenant, _service);
        var move = await FleetManagerPlacementEndpoints.MoveAsync(Request(Tenant, body, asSession: true), ResolveTenant, _service);

        foreach (var r in new[] { start, restart, move })
        {
            Assert.Equal(403, Status(r));
            Assert.Equal("Only the owner can start, restart or move the Fleet Manager.", Error(r));
        }
        Assert.Empty(_world.Spawns);
        Assert.Null(_settings.FleetManagerMachine(Tenant));
    }

    [Fact]
    public async Task Save_BadJson_IsRefused()
    {
        var result = await FleetManagerPlacementEndpoints.SaveAsync(Request(Tenant, "{not json"), ResolveTenant, _service);

        Assert.Equal(400, Status(result));
        Assert.StartsWith("The body is not valid JSON", Error(result));
    }

    [Fact]
    public async Task Save_MissingMachine_IsRefusedWithTheSentence()
    {
        var result = await FleetManagerPlacementEndpoints.SaveAsync(Request(Tenant, "{\"agent\":\"Codex\"}"), ResolveTenant, _service);

        Assert.Equal(400, Status(result));
        Assert.StartsWith("Both an agent and a computer are required", Error(result));
        Assert.Null(_settings.FleetManagerAgent(Tenant));
    }

    [Fact]
    public async Task Start_FromTheCockpit_RecordsAPersonsActionAndMarksTheNewSession()
    {
        _settings.SetFleetManagerPlacement(Tenant, "ClaudeCode", "WORKSTATION-A", Now);
        var result = await FleetManagerPlacementEndpoints.StartAsync(Request(Tenant, deviceType: "browser"), ResolveTenant, _service);

        Assert.Equal(200, Status(result));
        var spawn = Assert.Single(_world.Spawns);
        Assert.Equal("human", spawn.Request.Origin);
        Assert.Equal("cockpit", spawn.Request.OriginSurface);
        Assert.Null(spawn.Request.ParentSessionId);
        Assert.Null(spawn.Request.ControllerSessionId);
        Assert.Equal(NewId, _settings.FleetManagerSessionId(Tenant));
    }

    [Fact]
    public async Task Start_WhenRunning_Answers409WithTheSentence()
    {
        _settings.SetFleetManagerSessionId(Tenant, "50000000-0000-4000-8000-000000000001", Now);
        _world.Roster.Add(("dir-a", new SessionDto
        {
            SessionId = "50000000-0000-4000-8000-000000000001", ActivityState = "Idle", MachineName = "WORKSTATION-A",
            Agent = "ClaudeCode", CreatedAt = Now,
        }));

        var result = await FleetManagerPlacementEndpoints.StartAsync(Request(Tenant, deviceType: "browser"), ResolveTenant, _service);

        Assert.Equal(409, Status(result));
        Assert.StartsWith("The Fleet Manager is already running", Error(result));
        Assert.Empty(_world.Spawns);
    }
}
