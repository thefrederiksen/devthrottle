using System.Security.Claims;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// <c>DirectorHub.PushLicenseUsage</c> (License Switching, contract section 2.1), driven through the real hub on a fake
/// caller context: refused before Hello, taken from the bound Director's active connection into the bound tenant, and
/// dropped - not thrown - from a connection a reconnect has superseded.
/// </summary>
public sealed class PushLicenseUsageHubTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "cc-license-hub-" + Guid.NewGuid().ToString("N"));
    private readonly DirectorRegistry _registry;
    private readonly PushedSessionStore _store = new(() => T0);
    private readonly GatewayInputStatsAggregator _inputStats;
    private readonly GatewayDbTestHarness _harness = new();
    private readonly LicenseStore _licenses;
    private readonly LicenseUsageIngest _ingest;

    public PushLicenseUsageHubTests()
    {
        Directory.CreateDirectory(_tempDir);
        _registry = new DirectorRegistry(_tempDir);
        _inputStats = new GatewayInputStatsAggregator(Path.Combine(_tempDir, "gateway-stats.db"));
        _licenses = new LicenseStore(_harness.Open());
        _ingest = new LicenseUsageIngest(_licenses);
    }

    public void Dispose()
    {
        _registry.Dispose();
        _harness.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    private DirectorHub NewHub(string connectionId) => new(_store, _registry, InputStatsHandle.Available(_inputStats),
        new GatewayStreamRegistry(),
        new CcDirector.Gateway.Tenancy.HostedTenantBoundary(new SingleTenantContext(), new CcDirector.Gateway.Pairing.DeviceRegistry(), hosted: false),
        licenseUsage: _ingest) { Context = new FakeHubCallerContext(connectionId) };

    private static DirectorStreamHello Hello() => new() { DirectorId = "dir-A", Version = "test" };

    private double? StoredFiveHour()
        => new LicenseReadModel(_licenses).Read(TenantId.Local, T0).Accounts.SingleOrDefault()?.Freshest?.FiveHourUtilization;

    [Fact]
    public void PushLicenseUsage_BeforeHello_IsRefused()
    {
        var hub = NewHub("conn-1");

        Assert.Throws<HubException>(() => hub.PushLicenseUsage(1, Report("m1", Holding(A, true))));
        Assert.Null(StoredFiveHour());
    }

    [Fact]
    public void PushLicenseUsage_AfterHello_IsStoredUnderTheBoundTenant()
    {
        var hub = NewHub("conn-1");
        hub.Hello(Hello());

        hub.PushLicenseUsage(1, Report("m1", Holding(A, true, 42)));

        Assert.Equal(42, StoredFiveHour());
        Assert.Equal("dir-A", new LicenseReadModel(_licenses).Read(TenantId.Local, T0).Machines[0].DirectorId);
    }

    [Fact]
    public void PushLicenseUsage_FromASupersededConnection_IsDropped()
    {
        var old = NewHub("conn-old");
        old.Hello(Hello());
        var current = NewHub("conn-new");
        current.Hello(Hello());

        old.PushLicenseUsage(100, Report("m1", Holding(A, true, 99)));
        Assert.Null(StoredFiveHour());

        current.PushLicenseUsage(1, Report("m1", Holding(A, true, 12)));
        Assert.Equal(12, StoredFiveHour());
    }

    [Fact]
    public void GatewayCapabilities_ListPushLicenseUsage()
        => Assert.Contains(LicenseContract.PushUsageHubMethod, NewHub("conn-1").Hello(Hello())!.HubMethods);

    [Fact]
    public void GatewayCapabilities_WithNoLicenseIngest_DoNotOfferPushLicenseUsage()
    {
        // Pull request 3871 review, finding 2: a Gateway that would drop every report must not invite a Director to send one.
        var hub = new DirectorHub(_store, _registry, InputStatsHandle.Available(_inputStats), new GatewayStreamRegistry(),
            new CcDirector.Gateway.Tenancy.HostedTenantBoundary(new SingleTenantContext(), new CcDirector.Gateway.Pairing.DeviceRegistry(), hosted: false))
            { Context = new FakeHubCallerContext("conn-1") };

        var methods = hub.Hello(Hello())!.HubMethods;

        Assert.DoesNotContain(LicenseContract.PushUsageHubMethod, methods);
        Assert.Contains("PushRepoSnapshot", methods);
    }

    /// <summary>The same fake caller as <c>DirectorHubTests</c>: a connection identifier, and a web request context so the self-host
    /// boundary can resolve the Local tenant.</summary>
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
