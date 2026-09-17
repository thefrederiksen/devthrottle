using System.Text;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace CcDirector.Gateway.Tests.Api;

/// <summary>
/// The hand-over route (the Fleet Manager mission, step 8), driven through its handler: only the owner's own phone or
/// browser reaches the service; a session key - the Fleet Manager's included - a Director's key and a request with no
/// account are refused with a sentence before anything is changed. The session rules themselves are in
/// <see cref="Fleet.FleetManagerHandOverServiceTests"/>, and the guard's refusal of a session key in
/// <see cref="SessionKeyGuardTests"/>.
/// </summary>
public sealed class FleetManagerHandOverEndpointsTests
{
    private const string TenantHeader = "X-Test-Account";
    private static readonly TenantId Tenant = new("acct-hand-over-route");
    private const string Fm = "20000000-0000-4000-8000-000000000001";
    private const string Plain = "20000000-0000-4000-8000-000000000002";

    private sealed class CountingWorld : IFleetManagerHandOverEnvironment
    {
        public int Reads;
        public int Sends;
        public string? LastActor;

        public string? MarkedFleetManager(TenantId tenant) => Fm;

        public IReadOnlyList<(string DirectorId, SessionDto Session)> Roster(TenantId tenant)
        {
            Reads++;
            var list = new List<SessionDto>
            {
                new() { SessionId = Fm, Name = "The Fleet Manager", ActivityState = "WaitingForInput" },
                new() { SessionId = Plain, Name = "Plain work", ActivityState = "WaitingForInput" },
            };
            FleetRoleResolver.Stamp(list, Fm);
            return list.Select(s => ("dir-1", s)).ToList();
        }

        public bool ChangesOwner(TenantId tenant, string directorId) => true;

        public Task<(SessionDto? Session, string? Error)> SetControllerAsync(TenantId tenant, string directorId,
            string sessionId, string? controllerSessionId, CancellationToken ct)
        {
            Sends++;
            return Task.FromResult<(SessionDto?, string?)>((new SessionDto
            {
                SessionId = sessionId, Name = "Plain work", IsControlled = true, ControllerSessionId = controllerSessionId,
            }, null));
        }

        public void Audit(TenantId tenant, string sessionId, string actor, string detail) => LastActor = actor;

        public void OwnerChanged(TenantId tenant, string directorId, SessionDto row) { }
    }

    private readonly CountingWorld _world = new();
    private readonly FleetManagerHandOverService _service;

    public FleetManagerHandOverEndpointsTests()
    {
        _service = new FleetManagerHandOverService(_world);
    }

    private static TenantId? ResolveTenant(HttpContext ctx)
        => ctx.Request.Headers.TryGetValue(TenantHeader, out var v) && v.ToString().Length > 0 ? new TenantId(v.ToString()) : null;

    private static DefaultHttpContext Request(TenantId? tenant, string body, string? sessionKeyFor = null,
        string? deviceType = null, bool machineToken = false)
    {
        var ctx = new DefaultHttpContext();
        if (tenant is { } t) ctx.Request.Headers[TenantHeader] = t.Value;
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        if (sessionKeyFor is not null)
            ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] =
                new SessionCredentialIdentity(Guid.Parse(sessionKeyFor), tenant ?? TenantId.Local, "dir-1");
        if (deviceType is not null)
        {
            ctx.Items[AuthMiddleware.DeviceTypeItemKey] = deviceType;
            ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] =
                new DeviceCredentialIdentity("device-7", tenant?.Value, deviceType, "active");
        }
        if (machineToken)
            ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = true;
        return ctx;
    }

    private static string Body(string session = Plain, string to = "fleet-manager")
        => $"{{\"session\":\"{session}\",\"to\":\"{to}\"}}";

    private static int Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    private static string? Error(IResult result)
    {
        var value = result.GetType().GetProperty("Value")!.GetValue(result)!;
        return value.GetType().GetProperty("error")!.GetValue(value) as string;
    }

    [Fact]
    public void HandOverRoute_IsTheMappedLiteral()
        => Assert.Equal("/gateway/fleet-manager/hand-over", FleetManagerHandOverEndpoints.HandOverRoute);

    [Theory]
    [InlineData("phone")]
    [InlineData("browser")]
    public async Task HandOverAsync_OwnersOwnDevice_IsAllowedAndRecordedAsThatDevice(string deviceType)
    {
        var result = await FleetManagerHandOverEndpoints.HandOverAsync(Request(Tenant, Body(), deviceType: deviceType), ResolveTenant, _service);

        Assert.Equal(200, Status(result));
        var answer = Assert.IsType<JsonHttpResult<FleetHandOverResultDto>>(result).Value!;
        Assert.Equal(Fm, answer.OwnerSessionId);
        Assert.Equal(1, _world.Sends);
        Assert.Equal($"device {deviceType} device-7", _world.LastActor);
    }

    [Theory]
    [InlineData(Fm)]      // the Fleet Manager's own key
    [InlineData(Plain)]   // the session being handed over
    public async Task HandOverAsync_SessionKey_IsRefusedBeforeAnythingIsRead(string callingSession)
    {
        var result = await FleetManagerHandOverEndpoints.HandOverAsync(
            Request(Tenant, Body(), sessionKeyFor: callingSession), ResolveTenant, _service);

        Assert.Equal(403, Status(result));
        Assert.Equal("Only the owner can hand a session over, from the Cockpit or the phone. A session's own key cannot - " +
                     "not even the Fleet Manager's.", Error(result));
        Assert.Equal((0, 0), (_world.Reads, _world.Sends));
    }

    [Fact]
    public async Task HandOverAsync_DirectorsDeviceKey_IsRefused()
    {
        var result = await FleetManagerHandOverEndpoints.HandOverAsync(Request(Tenant, Body(), deviceType: "director"), ResolveTenant, _service);

        Assert.Equal(403, Status(result));
        Assert.Contains("this request was made with a device (director) credential", Error(result));
        Assert.Equal((0, 0), (_world.Reads, _world.Sends));
    }

    [Fact]
    public async Task HandOverAsync_MachineToken_IsRefused()
    {
        var result = await FleetManagerHandOverEndpoints.HandOverAsync(Request(Tenant, Body(), machineToken: true), ResolveTenant, _service);

        Assert.Equal(403, Status(result));
        Assert.Equal("only the owner on their own signed-in phone or browser may hand a session over; "
                     + "this request was made with a machine-token credential", Error(result));
        Assert.Equal(0, _world.Sends);
    }

    [Fact]
    public async Task HandOverAsync_NoAccount_IsRefused()
    {
        var result = await FleetManagerHandOverEndpoints.HandOverAsync(Request(null, Body(), deviceType: "phone"), ResolveTenant, _service);

        Assert.Equal(403, Status(result));
        Assert.Equal("no account is bound to this request", Error(result));
        Assert.Equal(0, _world.Sends);
    }

    [Fact]
    public async Task HandOverAsync_BodyIsNotJson_IsRefusedWithASentence()
    {
        var result = await FleetManagerHandOverEndpoints.HandOverAsync(Request(Tenant, "{not json", deviceType: "phone"), ResolveTenant, _service);

        Assert.Equal(400, Status(result));
        Assert.StartsWith("The body is not valid JSON", Error(result));
    }

    [Fact]
    public async Task HandOverAsync_ServiceRefusal_IsAnsweredWithItsStatusAndSentence()
    {
        var result = await FleetManagerHandOverEndpoints.HandOverAsync(
            Request(Tenant, Body(session: Fm), deviceType: "browser"), ResolveTenant, _service);

        Assert.Equal(409, Status(result));
        Assert.Contains("is the Fleet Manager itself", Error(result));
    }
}
