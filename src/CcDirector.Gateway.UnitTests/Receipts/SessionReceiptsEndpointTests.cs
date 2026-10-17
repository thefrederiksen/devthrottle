using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Receipts;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests.Receipts;

/// <summary>
/// <c>GET /sessions/{sid}/receipts</c> (mission #4070, phase 2), driven through its handler with a REAL receipt store
/// over the harness database, a REAL pushed-session roster and a REAL hosted tenant boundary, and no booted host. The
/// request's tenant comes from the authenticated device identity the auth middleware leaves on it, exactly as in
/// production, so the isolation proven here is the route's own: another account's session is answered 404, the same as
/// an id that does not exist, and its receipts are never served.
/// </summary>
public sealed class SessionReceiptsEndpointTests : IDisposable
{
    private static readonly TenantId Alice = new("aaaaaaaa-0000-4000-8000-00000000000a");
    private static readonly TenantId Bob = new("bbbbbbbb-0000-4000-8000-00000000000b");
    private const string AliceDirector = "dir-alice";
    private static readonly string AliceSession = "5d1c0f3e-8a2b-4c6d-9e0f-1a2b3c4d5e6f";
    private static readonly DateTime T0 = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly AsyncLocalTenantContext _ambient = new();
    private readonly HostedTenantBoundary _boundary;
    private readonly ApiModeReceiptStore _store;
    private readonly PushedSessionStore _pushed = new(() => T0);

    public SessionReceiptsEndpointTests()
    {
        var path = _harness.LegacyPath("devices.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var devices = new DeviceRegistry(_harness.Open(), path);
        _boundary = new HostedTenantBoundary(_ambient, devices, hosted: false);
        _store = new ApiModeReceiptStore(_harness.Open(_ambient), () => T0);
    }

    public void Dispose() => _harness.Dispose();

    private static ApiModeEventPosition At(long sequence) => new("gen-a", T0, sequence);

    private static DefaultHttpContext RequestFrom(TenantId? tenant)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        if (tenant is { } t)
        {
            ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "device-key";
            ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = new DeviceCredentialIdentity("device-" + t.Value[..4], t.Value, "phone", "active");
        }
        return ctx;
    }

    private IResult Get(TenantId? caller, string sid)
        => SessionReceiptsEndpoint.Read(RequestFrom(caller), sid, _store, _pushed, TimeSpan.FromSeconds(30), _boundary);

    private static int Status(IResult result)
        => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? StatusCodes.Status200OK;

    private static SessionReceiptsDto Body(IResult result)
        => Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<SessionReceiptsDto>>(result).Value!;

    /// <summary>Alice's Director pushes her session onto the roster, and it reports one delivered prompt.</summary>
    private void AliceHasADeliveredPrompt()
    {
        _pushed.RegisterConnection(Alice, AliceDirector, "conn-alice");
        Assert.True(_pushed.ApplySnapshot(Alice, AliceDirector, "conn-alice", 1,
            new List<SessionDto> { new() { SessionId = AliceSession, Name = "Pi in API mode", Agent = "Pi", ActivityState = "Working", LastActivityAt = T0 } }));
        using (_boundary.EnterScope(Alice))
            _store.RecordDelivered(AliceDirector, AliceSession, "p1", "started", """{"type":"response","command":"prompt","success":true,"data":{"disposition":"started"}}""", At(1), T0);
    }

    [Fact]
    public void Read_TheOwnersSession_AnswersItsReceipts()
    {
        AliceHasADeliveredPrompt();

        var result = Get(Alice, AliceSession);

        Assert.Equal(StatusCodes.Status200OK, Status(result));
        var body = Body(result);
        Assert.Equal(AliceSession, body.SessionId);
        Assert.Equal(1, body.Count);
        var receipt = Assert.Single(body.Receipts);
        Assert.Equal("p1", receipt.PromptId);
        Assert.Equal(ApiModeReceiptState.Delivered, receipt.State);
    }

    [Fact]
    public void Read_AReceiptWithItsPromptText_ServesTheTextVerbatim()
    {
        AliceHasADeliveredPrompt();
        const string spoken = "  run the tests\nthen tell me what failed  \n";
        using (_boundary.EnterScope(Alice))
            _store.RecordQueued(AliceDirector, AliceSession, "p2", "turnRunning", At(2), T0, spoken);

        var body = Body(Get(Alice, AliceSession));

        Assert.Equal(spoken, Assert.Single(body.Receipts, r => r.PromptId == "p2").PromptText);
    }

    [Fact]
    public void Read_AReceiptFromADirectorThatSentNoText_PromptTextIsNull()
    {
        AliceHasADeliveredPrompt();   // recorded with no prompt text, as from an older Director

        var body = Body(Get(Alice, AliceSession));

        Assert.Null(Assert.Single(body.Receipts).PromptText);
    }

    [Fact]
    public void Read_AnotherAccountsSession_IsNotFoundAndServesNothing()
    {
        AliceHasADeliveredPrompt();

        var result = Get(Bob, AliceSession);

        Assert.Equal(StatusCodes.Status404NotFound, Status(result));
        Assert.IsNotType<Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<SessionReceiptsDto>>(result);
    }

    [Fact]
    public void Read_AKnownSessionWithNoReceipts_AnswersCountZero()
    {
        _pushed.RegisterConnection(Alice, AliceDirector, "conn-alice");
        Assert.True(_pushed.ApplySnapshot(Alice, AliceDirector, "conn-alice", 1,
            new List<SessionDto> { new() { SessionId = AliceSession, Name = "Terminal session", Agent = "Pi", ActivityState = "Working", LastActivityAt = T0 } }));

        var result = Get(Alice, AliceSession);

        Assert.Equal(StatusCodes.Status200OK, Status(result));
        Assert.Equal(0, Body(result).Count);
        Assert.Empty(Body(result).Receipts);
    }

    [Fact]
    public void Read_ASessionWithReceiptsButOffTheRoster_StillAnswersItsReceipts()
    {
        using (_boundary.EnterScope(Alice))
            _store.RecordRefused(AliceDirector, AliceSession, "p1", "Pi exited before acknowledging", "director", At(1), T0);

        var result = Get(Alice, AliceSession);

        Assert.Equal(StatusCodes.Status200OK, Status(result));
        Assert.Equal(ApiModeReceiptState.Refused, Assert.Single(Body(result).Receipts).State);
    }

    [Fact]
    public void Read_AnUnknownSession_IsNotFound()
    {
        Assert.Equal(StatusCodes.Status404NotFound, Status(Get(Alice, Guid.NewGuid().ToString())));
    }

    [Fact]
    public void Read_ARequestWithNoBoundTenant_IsForbidden()
    {
        AliceHasADeliveredPrompt();

        Assert.Equal(StatusCodes.Status403Forbidden, Status(Get(null, AliceSession)));
    }

    [Fact]
    public void Read_ASessionIdThatIsNotAnIdentifier_IsABadRequest()
    {
        Assert.Equal(StatusCodes.Status400BadRequest, Status(Get(Alice, "not-a-session-id")));
    }
}
