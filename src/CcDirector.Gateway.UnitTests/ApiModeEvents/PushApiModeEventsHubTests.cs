using System.Security.Claims;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.ApiModeConversation;
using CcDirector.Gateway.ApiModeEvents;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Receipts;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Xunit;
using static CcDirector.Gateway.Tests.ApiModeEvents.ApiModeEventTestData;

namespace CcDirector.Gateway.Tests.ApiModeEvents;

/// <summary>
/// <c>DirectorHub.PushApiModeEvents</c> (mission #4070, phase 2), driven through the real hub on a fake caller context:
/// refused before Hello, stored under the Director the connection is bound to, a malformed batch refused with nothing
/// stored, and offered in the Gateway's capabilities only when the Gateway has an ingest to keep the events in.
/// </summary>
public sealed class PushApiModeEventsHubTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "cc-api-mode-hub-" + Guid.NewGuid().ToString("N"));
    private readonly DirectorRegistry _registry;
    private readonly PushedSessionStore _store = new(() => T0.UtcDateTime);
    private readonly GatewayInputStatsAggregator _inputStats;
    private readonly GatewayDbTestHarness _harness = new();
    // The device registry lives over this test's own harness database. The parameterless registry opens the default store
    // every test class shares, and two classes building it at once collide on its import marker (a UNIQUE failure on
    // device_import_markers that failed this class beside DirectorHubTests).
    private readonly HostedTenantBoundary _boundary;
    private readonly ApiModeReceiptStore _receipts;
    private readonly ApiModeEventBuffer _buffer = new();

    public PushApiModeEventsHubTests()
    {
        Directory.CreateDirectory(_tempDir);
        _registry = new DirectorRegistry(_tempDir);
        _inputStats = new GatewayInputStatsAggregator(Path.Combine(_tempDir, "gateway-stats.db"));
        _receipts = new ApiModeReceiptStore(_harness.Open(), () => T0.UtcDateTime);
        var devices = _harness.LegacyPath("devices.json");
        Directory.CreateDirectory(Path.GetDirectoryName(devices)!);
        _boundary = new HostedTenantBoundary(new SingleTenantContext(), new CcDirector.Gateway.Pairing.DeviceRegistry(_harness.Open(), devices), hosted: false);
    }

    public void Dispose()
    {
        _registry.Dispose();
        _harness.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    private DirectorHub NewHub(string connectionId, bool withIngest = true, CcDirector.Gateway.Fleet.FleetDisplayStateObserver? fleetDisplayState = null) => new(_store, _registry, InputStatsHandle.Available(_inputStats),
        new GatewayStreamRegistry(), _boundary, fleetDisplayState: fleetDisplayState,
        apiModeEvents: withIngest ? new ApiModeEventIngest(_buffer, _receipts, new ApiModeConversationStore(_harness.Open(), () => T0.UtcDateTime), _boundary) : null) { Context = new FakeHubCallerContext(connectionId) };

    private const string OtherSid = "77777777-7777-4777-8777-777777777777";

    /// <summary>A display push over two sessions whose fold is the count of events held for each, and a sender that
    /// records which session each push went to. <paramref name="otherLabel"/> is the other session's fold.</summary>
    private (CcDirector.Gateway.Fleet.FleetDisplayStateObserver Observer, List<string> Sent) DisplayPush(Func<string> otherLabel)
    {
        var fleet = new List<(string, SessionDto)> { (Director, new SessionDto { SessionId = Sid }), (Director, new SessionDto { SessionId = OtherSid }) };
        var sent = new List<string>();
        var observer = new CcDirector.Gateway.Fleet.FleetDisplayStateObserver(
            () => fleet,
            sessions =>
            {
                foreach (var s in sessions)
                    s.StateLabel = s.SessionId == Sid ? $"{_buffer.Read(TenantId.Local, Sid).Events.Count} events" : otherLabel();
            },
            (_, command, _) =>
            {
                lock (sent) sent.Add(command.SessionId);
                return Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success());
            });
        return (observer, sent);
    }

    [Fact]
    public void PushApiModeEvents_AfterIngest_PushesThatSessionsDisplayStateAndNoOther()
    {
        // Arrange: both sessions already pushed; then the other session's fold moves too, but nothing arrived for it.
        var otherLabel = "before";
        var (observer, sent) = DisplayPush(() => otherLabel);
        observer.Sweep();
        var hub = NewHub("conn-1", fleetDisplayState: observer);
        hub.Hello(Hello());
        sent.Clear();
        otherLabel = "after";

        // Act
        Assert.NotNull(hub.PushApiModeEvents(A(DeliveredThenDone())));

        // Assert: only the session whose events landed is pushed.
        Assert.Equal(new[] { Sid }, sent);
    }

    [Fact]
    public void PushApiModeEvents_AMalformedBatch_PushesNoDisplayState()
    {
        var (observer, sent) = DisplayPush(() => "unchanged");
        var hub = NewHub("conn-1", fleetDisplayState: observer);
        hub.Hello(Hello());
        sent.Clear();
        var batch = A(DeliveredThenDone());
        batch.Events.Reverse();   // sequences no longer rise

        Assert.Null(hub.PushApiModeEvents(batch));

        Assert.Empty(sent);
    }

    private static DirectorStreamHello Hello() => new() { DirectorId = Director, Version = "test" };

    [Fact]
    public void PushApiModeEvents_BeforeHello_IsRefused()
    {
        var hub = NewHub("conn-1");

        Assert.Throws<HubException>(() => hub.PushApiModeEvents(A(DeliveredThenDone())));
        Assert.Empty(_receipts.Read(Sid));
    }

    [Fact]
    public void PushApiModeEvents_AfterHello_StoresTheReceiptsAndTheEvents_AndAnswersTheWatermark()
    {
        var hub = NewHub("conn-1");
        hub.Hello(Hello());

        var mark = hub.PushApiModeEvents(A(DeliveredThenCancelled()));

        Assert.NotNull(mark);
        Assert.Equal(Sid, mark.SessionId);
        Assert.Equal("gen-a", mark.Generation);
        Assert.Equal(5, mark.Sequence);
        var receipt = Assert.Single(_receipts.Read(Sid));
        Assert.Equal("cancelled", receipt.TurnOutcome);
        Assert.Equal(5, _buffer.Read(TenantId.Local, Sid).Events.Count);
    }

    [Fact]
    public void PushApiModeEvents_AMalformedBatch_IsRefusedWithNullAndNothingIsStored()
    {
        var hub = NewHub("conn-1");
        hub.Hello(Hello());
        var batch = A(DeliveredThenDone());
        batch.Events.Reverse();   // sequences no longer rise

        Assert.Null(hub.PushApiModeEvents(batch));
        Assert.Empty(_receipts.Read(Sid));
        Assert.Empty(_buffer.Read(TenantId.Local, Sid).Events);
    }

    [Fact]
    public void PushApiModeEvents_AnOlderGeneration_IsRefusedWithNull()
    {
        var hub = NewHub("conn-1");
        hub.Hello(Hello());
        hub.PushApiModeEvents(B(Ready(1)));

        Assert.Null(hub.PushApiModeEvents(A(Ready(1), Queued(2, "p1"))));
        Assert.Empty(_receipts.Read(Sid));
    }

    [Fact]
    public void GatewayCapabilities_WithAnIngest_OfferPushApiModeEvents()
        => Assert.Contains(nameof(DirectorHub.PushApiModeEvents), NewHub("conn-1").Hello(Hello())!.HubMethods);

    [Fact]
    public void GatewayCapabilities_WithNoIngest_DoNotOfferPushApiModeEvents()
    {
        var hub = NewHub("conn-1", withIngest: false);

        var methods = hub.Hello(Hello())!.HubMethods;

        Assert.DoesNotContain(nameof(DirectorHub.PushApiModeEvents), methods);
        Assert.Null(hub.PushApiModeEvents(A(DeliveredThenDone())));
    }

    /// <summary>The same fake caller as <c>DirectorHubTests</c>: a connection identifier, and a web request context so the
    /// self-host boundary can resolve the Local tenant.</summary>
    private sealed class FakeHubCallerContext : HubCallerContext
    {
        public FakeHubCallerContext(string connectionId)
        {
            ConnectionId = connectionId;
            Features.Set<Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature>(
                new HttpContextFeatureImpl { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() });
        }

        private sealed class HttpContextFeatureImpl : Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature
        {
            public Microsoft.AspNetCore.Http.HttpContext? HttpContext { get; set; }
        }

        public override string ConnectionId { get; }
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }
}
