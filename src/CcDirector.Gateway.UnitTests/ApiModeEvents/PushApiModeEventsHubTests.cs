using System.Security.Claims;
using CcDirector.Core.Tenancy;
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
    private readonly HostedTenantBoundary _boundary = new(new SingleTenantContext(), new CcDirector.Gateway.Pairing.DeviceRegistry(), hosted: false);
    private readonly ApiModeReceiptStore _receipts;
    private readonly ApiModeEventBuffer _buffer = new();

    public PushApiModeEventsHubTests()
    {
        Directory.CreateDirectory(_tempDir);
        _registry = new DirectorRegistry(_tempDir);
        _inputStats = new GatewayInputStatsAggregator(Path.Combine(_tempDir, "gateway-stats.db"));
        _receipts = new ApiModeReceiptStore(_harness.Open(), () => T0.UtcDateTime);
    }

    public void Dispose()
    {
        _registry.Dispose();
        _harness.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    private DirectorHub NewHub(string connectionId, bool withIngest = true) => new(_store, _registry, InputStatsHandle.Available(_inputStats),
        new GatewayStreamRegistry(), _boundary,
        apiModeEvents: withIngest ? new ApiModeEventIngest(_buffer, _receipts, _boundary) : null) { Context = new FakeHubCallerContext(connectionId) };

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
        Assert.Equal(Director, Assert.Single(_receipts.DirectorsOf(Sid)));
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
