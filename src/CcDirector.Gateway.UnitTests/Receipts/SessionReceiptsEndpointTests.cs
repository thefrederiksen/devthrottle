using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.ApiModeDisplay;
using CcDirector.Gateway.ApiModeEvents;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Contracts.ApiMode;
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

    // ----- a queued receipt's place in line, derived at read time -----

    private const string Ack = """{"type":"response","command":"prompt","success":true,"data":{"disposition":"started"}}""";

    private void RecordQueued(string prompt, long sequence, string generation = "gen-a", DateTime? started = null)
    {
        using (_boundary.EnterScope(Alice))
            _store.RecordQueued(AliceDirector, AliceSession, prompt, "turnRunning", new ApiModeEventPosition(generation, started ?? T0, sequence), T0);
    }

    private Dictionary<string, string> WordsByPrompt()
        => Body(Get(Alice, AliceSession)).Receipts.ToDictionary(r => r.PromptId, r => r.Words);

    [Fact]
    public void Read_ThreeQueuedPrompts_EachSaysItsPlaceInLine()
    {
        // Arrange
        RecordQueued("p1", 1);
        RecordQueued("p2", 2);
        RecordQueued("p3", 3);

        // Act
        var receipts = Body(Get(Alice, AliceSession)).Receipts;

        // Assert
        Assert.Equal(new int?[] { 1, 2, 3 }, receipts.Select(r => r.QueuePosition));
        Assert.Equal(new[]
        {
            "Queued, 1st in line: a turn is running",
            "Queued, 2nd in line: a turn is running",
            "Queued, 3rd in line: a turn is running",
        }, receipts.Select(r => r.Words));
    }

    [Fact]
    public void Read_TheOneAheadIsDelivered_TheWaitingPromptMovesUp()
    {
        // Arrange
        RecordQueued("p1", 1);
        RecordQueued("p2", 2);
        using (_boundary.EnterScope(Alice))
            _store.RecordDelivered(AliceDirector, AliceSession, "p1", "started", Ack, At(3), T0);

        // Act
        var receipts = Body(Get(Alice, AliceSession)).Receipts.ToDictionary(r => r.PromptId);

        // Assert: the place is what it is now, not what it was when p2 was queued.
        Assert.Equal(("Delivered", (int?)null), (receipts["p1"].Words, receipts["p1"].QueuePosition));
        Assert.Equal(("Queued, 1st in line: a turn is running", (int?)1), (receipts["p2"].Words, receipts["p2"].QueuePosition));
    }

    [Fact]
    public void Read_AReceiptStillQueuedFromAnOlderGeneration_SaysTheOldWordsWithNoPlace()
    {
        // Arrange: p1 was queued by a Director that has since restarted; p2 by the new one.
        RecordQueued("p1", 7);
        RecordQueued("p2", 1, "gen-b", T0.AddHours(1));

        // Act
        var receipts = Body(Get(Alice, AliceSession)).Receipts.ToDictionary(r => r.PromptId);

        // Assert
        Assert.Equal(("Queued: a turn is running", (int?)null), (receipts["p1"].Words, receipts["p1"].QueuePosition));
        Assert.Equal(("Queued, 1st in line: a turn is running", (int?)1), (receipts["p2"].Words, receipts["p2"].QueuePosition));
    }

    [Fact]
    public void Read_AndTheLiveRuling_GiveTheSameWordsForEveryReceiptAfterEveryEvent()
    {
        // Arrange: the same events reach the store (as the ingest writes it) and a live stream's ruling, and the stream's
        // opening after a reset words the stored receipts the way the live endpoint does.
        var events = new SessionEvent[]
        {
            Tests.ApiModeEvents.ApiModeEventTestData.Queued(1, "p1", sid: AliceSession, position: 1),
            Tests.ApiModeEvents.ApiModeEventTestData.Queued(2, "p2", sid: AliceSession, position: 2),
            Tests.ApiModeEvents.ApiModeEventTestData.Queued(3, "p3", sid: AliceSession, position: 3),
            Tests.ApiModeEvents.ApiModeEventTestData.Refused(4, "p2", "Pi is not running: the process exited", PromptRefusedBy.Director, AliceSession),
            Tests.ApiModeEvents.ApiModeEventTestData.Queued(5, "p2", sid: AliceSession, position: 2),
            Tests.ApiModeEvents.ApiModeEventTestData.Delivered(6, "p1", Ack, AliceSession),
            Tests.ApiModeEvents.ApiModeEventTestData.Delivered(7, "p3", Ack, AliceSession),
        };
        var ruling = new ApiModeLiveRuling();
        var shown = new Dictionary<string, string>();

        foreach (var e in events)
        {
            // Act
            Apply(e);
            foreach (var item in ruling.Rule(new ApiModeHeldEvent("gen-a", T0, e)))
                shown[item.PromptId!] = item.Text;
            var read = WordsByPrompt();
            IReadOnlyList<ApiModeStoredReceipt> stored;
            using (_boundary.EnterScope(Alice))
                stored = _store.ReadWithPositions(AliceSession);
            ApiModeLiveRuling.PlaceInLine(stored);
            var opening = stored.ToDictionary(r => r.Receipt.PromptId, r => ApiModeLiveRuling.ReceiptItem(r.Receipt, 0).Text);

            // Assert
            Assert.Equal(read.OrderBy(kv => kv.Key, StringComparer.Ordinal), shown.OrderBy(kv => kv.Key, StringComparer.Ordinal));
            Assert.Equal(read.OrderBy(kv => kv.Key, StringComparer.Ordinal), opening.OrderBy(kv => kv.Key, StringComparer.Ordinal));
        }
        Assert.Equal("Queued, 1st in line: a turn is running", WordsByPrompt()["p2"]);
    }

    /// <summary>One receipt event into the store, as the ingest writes it.</summary>
    private void Apply(SessionEvent e)
    {
        using var scope = _boundary.EnterScope(Alice);
        switch (e)
        {
            case PromptQueued q: _store.RecordQueued(AliceDirector, AliceSession, q.PromptId, ApiModeEventIngest.Word(q.Reason), At(q.Sequence), T0); break;
            case PromptDelivered d: _store.RecordDelivered(AliceDirector, AliceSession, d.PromptId, ApiModeEventIngest.Word(d.Disposition), d.Acknowledgement, At(d.Sequence), T0); break;
            case PromptRefused f: _store.RecordRefused(AliceDirector, AliceSession, f.PromptId, f.Reason, ApiModeEventIngest.Word(f.RefusedBy), At(f.Sequence), T0); break;
            default: throw new InvalidOperationException($"no store write for {e.GetType().Name}");
        }
    }
}
