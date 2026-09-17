using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using CcDirector.Gateway.Wingman;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// <c>GET /sessions/{sid}/wingman-stops</c>, the handler's own exits (the Wingman inspector, phase 2, ruling 5), driven
/// through <see cref="GatewayEndpoints.ReadWingmanStops"/> with a real trace store and a real pushed-session store and
/// no booted host.
///
/// THE SESSION-KEY REFUSAL IS PROVEN HERE WITH THE GUARD OUT OF THE WAY. The route is not on
/// <see cref="SessionKeyGuard"/>'s allow list (<see cref="SessionKeyGuardTests"/> proves that), but a refusal that rested
/// only on that list would end the day somebody widened it. So the handler is called directly, with a session key's
/// identity on the request, and it refuses on its own - and the SAME request without that identity reads the stop, so
/// the refusal is not a route that serves nobody.
///
/// PARKED SUITE. Gateway.UnitTests runs under -Parked.
/// </summary>
public sealed class WingmanStopsRouteTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    private DeviceRegistry? _devices;

    public void Dispose()
    {
        _devices?.Dispose();
        _harness.Dispose();
    }

    // TenantId.Local is what the self-host boundary binds every request to.
    private static readonly TenantId Account = TenantId.Local;
    private static readonly string Sid = Guid.NewGuid().ToString();
    private static readonly DateTime T0 = new(2026, 9, 16, 15, 0, 0, DateTimeKind.Utc);

    // The device registry lives in this test's own directory. The parameterless registry opens the default store, which
    // every test class shares - two classes building it at once collide on its import marker.
    private CcDirector.Gateway.Tenancy.HostedTenantBoundary SelfHostBoundary()
    {
        if (_devices is null)
        {
            var path = _harness.LegacyPath("devices.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _devices = new DeviceRegistry(path);
        }
        return new(new SingleTenantContext(), _devices);
    }

    // A JSON result with no explicit status is a 200: the framework writes the default when none is set.
    private static int Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? StatusCodes.Status200OK;

    private static WingmanStopsResponse BodyOf(IResult result)
        => Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<WingmanStopsResponse>>(result).Value!;

    private static PushedSessionStore PushedHolding(params string[] sessionIds)
    {
        var pushed = new PushedSessionStore();
        pushed.RegisterConnection(Account, "director-stops", "conn-stops");
        Assert.True(pushed.ApplySnapshot(Account, "director-stops", "conn-stops", 1,
            sessionIds.Select(id => new SessionDto { SessionId = id, Name = id, ActivityState = "WaitingForInput", LastActivityAt = DateTime.UtcNow }).ToList()));
        return pushed;
    }

    /// <summary>What the authentication middleware leaves on a request, by the credential that authenticated it: a
    /// device's own key (a device identity), a session key (a session identity and no device), or the self-hosted
    /// shared machine token (the credential and no identity at all).</summary>
    public enum Caller { Device, SessionKey, SharedMachineToken, Nobody }

    private static DefaultHttpContext Request(Caller caller = Caller.Device)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        switch (caller)
        {
            case Caller.Device:
                ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "device-key";
                ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] =
                    new DeviceCredentialIdentity("device-1", null, "phone", "active");
                break;
            case Caller.SessionKey:
                ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "session-key";
                ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] =
                    new SessionCredentialIdentity(Guid.Parse(Sid), Account, "director-stops");
                break;
            case Caller.SharedMachineToken:
                ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "the-shared-machine-token";
                break;
        }
        return ctx;
    }

    private static string ErrorOf(IResult result)
    {
        var body = Assert.IsAssignableFrom<IValueHttpResult>(result).Value!;
        return (string)body.GetType().GetProperty("error")!.GetValue(body)!;
    }

    private TurnVerdictTraceStore StoreHolding(int stops)
    {
        var store = new TurnVerdictTraceStore(_harness.Open());
        for (var i = 0; i < stops; i++)
            store.Append(Account, new TurnVerdictTrace
            {
                TraceId = "trace-" + i,
                SessionId = Sid,
                RecordedAtUtc = T0.AddMinutes(i),
                TurnEndObservedAtUtc = T0.AddMinutes(i),
                Trigger = "turn-end",
                Outcome = TurnVerdictTraceOutcomes.Skipped,
                Cause = ActivityCauses.Held,
            });
        return store;
    }

    [Fact]
    public void A_session_key_is_refused_by_the_handler_itself_and_the_same_request_from_a_device_reads_the_stop()
    {
        var store = StoreHolding(1);
        var pushed = PushedHolding(Sid);

        var refused = GatewayEndpoints.ReadWingmanStops(Request(Caller.SessionKey), Sid, null, SelfHostBoundary(), store, pushed);
        Assert.Equal(StatusCodes.Status403Forbidden, Status(refused));
        // The refusal SAYS WHICH refusal it is: a bare 403 is also what an unresolved tenant produces.
        Assert.Contains("never to a session key", ErrorOf(refused));

        // THE POSITIVE CONTROL: the same session, the same store, a device's request - served.
        var served = GatewayEndpoints.ReadWingmanStops(Request(), Sid, null, SelfHostBoundary(), store, pushed);
        Assert.Equal(StatusCodes.Status200OK, Status(served));
        Assert.Equal("trace-0", Assert.Single(BodyOf(served).Stops).TraceId);
    }

    [Theory]
    [InlineData(Caller.SharedMachineToken)]
    [InlineData(Caller.Nobody)]
    public void A_caller_with_no_device_identity_is_refused_and_the_same_request_from_a_device_reads_the_stop(Caller caller)
    {
        // The self-hosted shared machine token authenticates with no device and resolves to the Local account - the
        // account these stops belong to. Refusing only a session key served it raw screens, prompts and answers.
        var store = StoreHolding(1);
        var pushed = PushedHolding(Sid);

        var refused = GatewayEndpoints.ReadWingmanStops(Request(caller), Sid, null, SelfHostBoundary(), store, pushed);
        Assert.Equal(StatusCodes.Status403Forbidden, Status(refused));
        Assert.Contains("only to a device signed in with its own device key", ErrorOf(refused));

        // THE POSITIVE CONTROL: the same session and store, with a device's identity - served.
        var served = GatewayEndpoints.ReadWingmanStops(Request(Caller.Device), Sid, null, SelfHostBoundary(), store, pushed);
        Assert.Equal("trace-0", Assert.Single(BodyOf(served).Stops).TraceId);
    }

    [Fact]
    public void A_caller_with_no_device_identity_is_refused_before_the_id_and_the_store_are_looked_at()
        => Assert.Equal(StatusCodes.Status403Forbidden,
            Status(GatewayEndpoints.ReadWingmanStops(Request(Caller.SharedMachineToken), "not-an-id", null, SelfHostBoundary(), null, null)));

    [Fact]
    public void A_session_key_is_refused_before_anything_else_is_looked_at()
    {
        // Ruling 5's order: the session-key refusal comes before the id check and before the store check, so a session
        // key learns nothing about which ids parse or whether this Gateway keeps the record.
        Assert.Equal(StatusCodes.Status403Forbidden,
            Status(GatewayEndpoints.ReadWingmanStops(Request(Caller.SessionKey), "not-an-id", null, SelfHostBoundary(), null, null)));
    }

    [Fact]
    public void A_session_id_that_is_not_an_identifier_is_refused_before_the_store_is_read()
        => Assert.Equal(StatusCodes.Status400BadRequest,
            Status(GatewayEndpoints.ReadWingmanStops(Request(), "not-an-id", null, SelfHostBoundary(), null, PushedHolding(Sid))));

    [Fact]
    public void A_Gateway_with_no_trace_store_answers_not_found()
        => Assert.Equal(StatusCodes.Status404NotFound,
            Status(GatewayEndpoints.ReadWingmanStops(Request(), Sid, null, SelfHostBoundary(), null, PushedHolding(Sid))));

    [Fact]
    public void A_session_that_is_not_in_the_callers_account_answers_not_found_and_serves_no_stop()
    {
        var store = StoreHolding(2);
        // The store holds this session's stops, but no Director in this account has pushed it.
        var result = GatewayEndpoints.ReadWingmanStops(Request(), Sid, null, SelfHostBoundary(), store, PushedHolding(Guid.NewGuid().ToString()));

        Assert.Equal(StatusCodes.Status404NotFound, Status(result));
        Assert.IsNotType<Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<WingmanStopsResponse>>(result);
    }

    [Fact]
    public void The_count_takes_the_newest_stops_and_the_chips_count_what_was_served()
    {
        var result = GatewayEndpoints.ReadWingmanStops(Request(), Sid, 2, SelfHostBoundary(), StoreHolding(3), PushedHolding(Sid));

        var answer = BodyOf(result);
        Assert.Equal(new[] { "trace-2", "trace-1" }, answer.Stops.Select(s => s.TraceId));
        Assert.Equal(2, answer.Groups.Single(g => g.Key == WingmanStopsFold.GroupAll).Count);
        Assert.Equal(2, answer.Groups.Single(g => g.Key == WingmanStopsFold.GroupNotAsked).Count);
        Assert.Equal(Sid, answer.SessionId);
    }
}
