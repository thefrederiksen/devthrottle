using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Diagnostics;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Diagnostics;

/// <summary>
/// The traffic meter answers one question - which route sends the bytes, to which kind of caller, in which hour -
/// so these pin each part of that answer: the route TEMPLATE (never the concrete path), the caller kind from the
/// credential the auth middleware stashed, a socket's bytes landing in the hour they were sent, a 304 counted by
/// its headers, and the request's own features handed back afterwards. And the three things that keep it safe on
/// the shared hosted Gateway: a caller cannot write its own text into it, cannot grow it without bound, and cannot
/// read another tenant's rows.
///
/// Revert-proof: key on <c>ctx.Request.Path</c> instead of the route template and
/// <see cref="InvokeAsync_EndpointWrite_KeyedByRouteTemplateAndCaller"/> goes red; count socket bytes only when
/// the request finishes and <see cref="InvokeAsync_SocketWrites_LandInTheHourTheyWereSent"/> goes red; drop the
/// tenant filter in Snapshot and <see cref="Snapshot_OtherTenantsRows_NeverShown"/> goes red; drop the prune in
/// TallyFor and <see cref="TallyFor_NewHour_PrunesWithoutARead"/> goes red.
/// </summary>
public sealed class TrafficMeterTests
{
    private static readonly TenantId Alpha = new("tenant-alpha");
    private static readonly TenantId Beta = new("tenant-beta");

    private sealed class Clock
    {
        public DateTime Now = new(2026, 10, 3, 4, 10, 0, DateTimeKind.Utc);
    }

    private sealed class FakeUpgrade : IHttpUpgradeFeature
    {
        public readonly MemoryStream Wire = new();
        public bool IsUpgradableRequest => true;
        public Task<Stream> UpgradeAsync() => Task.FromResult<Stream>(Wire);
    }

    private sealed class FakeExtendedConnect : IHttpExtendedConnectFeature
    {
        public readonly MemoryStream Wire = new();
        public bool IsExtendedConnect => true;
        public string? Protocol => "websocket";
        public ValueTask<Stream> AcceptAsync() => ValueTask.FromResult<Stream>(Wire);
    }

    private static RouteEndpoint Endpoint(string template) =>
        new(_ => Task.CompletedTask, RoutePatternFactory.Parse(template), 0, EndpointMetadataCollection.Empty, template);

    /// <summary>The application maps these, so their first segments are the only ones a request with no endpoint
    /// may be named by.</summary>
    private static readonly IServiceProvider Services = new ServiceCollection()
        .AddSingleton<EndpointDataSource>(new DefaultEndpointDataSource(Endpoint("/sessions/{sid}/history"), Endpoint("/director-stream")))
        .BuildServiceProvider();

    private static DefaultHttpContext Context(string method, string path, string? template = null)
    {
        var ctx = new DefaultHttpContext { RequestServices = Services };
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();
        if (template is not null)
            ctx.SetEndpoint(Endpoint(template));
        return ctx;
    }

    private static Task Run(TrafficMeter meter, HttpContext ctx, Func<Task> handler, TenantId? tenant = null)
        => meter.InvokeAsync(ctx, handler, _ => tenant ?? TenantId.Local);

    private static TrafficRow OnlyRow(TrafficMeter meter, TenantId? viewer = null)
        => Assert.Single(Assert.Single(meter.Snapshot(viewer ?? TenantId.Local).Hours).Rows);

    [Fact]
    public async Task InvokeAsync_EndpointWrite_KeyedByRouteTemplateAndCaller()
    {
        var clock = new Clock();
        var meter = new TrafficMeter(() => clock.Now);
        var ctx = Context("GET", "/sessions/5b0e1c52-7f59-4c1e-9d1a-0b8b1f6d0a11/history", "/sessions/{sid}/history");
        ctx.Items[AuthMiddleware.DeviceTypeItemKey] = "browser";
        ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "secret";

        await Run(meter, ctx, () => ctx.Response.Body.WriteAsync(new byte[1000]).AsTask());

        var row = OnlyRow(meter);
        Assert.Equal("GET /sessions/{sid}/history", row.Route);
        Assert.Equal("device:browser", row.Caller);
        Assert.Equal(1000, row.BodyBytes);
        Assert.Equal(1, row.Requests);
        Assert.True(row.HeaderBytes > 0);
        Assert.Equal(row.HeaderBytes + row.BodyBytes, row.Bytes);
        Assert.DoesNotContain("5b0e1c52", row.Route);
    }

    [Fact]
    public async Task InvokeAsync_UnknownDeviceType_NamedOther()
    {
        var meter = new TrafficMeter(() => DateTime.UtcNow);
        var ctx = Context("GET", "/sessions", "/sessions");
        ctx.Items[AuthMiddleware.DeviceTypeItemKey] = "<script>chosen by a client</script>";

        await Run(meter, ctx, () => Task.CompletedTask);

        Assert.Equal("device:other", OnlyRow(meter).Caller);
    }

    [Fact]
    public async Task InvokeAsync_NotModified_CountedByHeadersAlone()
    {
        var meter = new TrafficMeter(() => new DateTime(2026, 10, 3, 4, 0, 0, DateTimeKind.Utc));
        var ctx = Context("GET", "/sessions", "/sessions");
        ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] = new object();

        await Run(meter, ctx, () =>
        {
            ctx.Response.StatusCode = StatusCodes.Status304NotModified;
            ctx.Response.Headers.ETag = "\"abc\"";
            return Task.CompletedTask;
        });

        var row = OnlyRow(meter);
        Assert.Equal("session-key", row.Caller);
        Assert.Equal(1, row.NotModified);
        Assert.Equal(0, row.BodyBytes);
        Assert.True(row.HeaderBytes > 0);
    }

    [Fact]
    public async Task InvokeAsync_SocketWrites_LandInTheHourTheyWereSent()
    {
        var clock = new Clock();
        var meter = new TrafficMeter(() => clock.Now);
        var ctx = Context("GET", "/director-stream", "/director-stream");
        var upgrade = new FakeUpgrade();
        ctx.Features.Set<IHttpUpgradeFeature>(upgrade);

        await Run(meter, ctx, async () =>
        {
            var socket = await ctx.Features.Get<IHttpUpgradeFeature>()!.UpgradeAsync();
            await socket.WriteAsync(new byte[300]);
            clock.Now = clock.Now.AddHours(1);
            await socket.WriteAsync(new byte[500]);
        });

        var hours = meter.Snapshot(TenantId.Local).Hours;
        Assert.Equal(2, hours.Count);
        Assert.Equal(300, Assert.Single(hours[0].Rows).SocketBytes);
        Assert.Equal(500, Assert.Single(hours[1].Rows).SocketBytes);
        Assert.Equal("GET /director-stream", hours[1].Rows[0].Route);
        Assert.Equal("unauthenticated", hours[1].Rows[0].Caller);
        Assert.Equal(800, upgrade.Wire.Length);
    }

    [Fact]
    public async Task InvokeAsync_ExtendedConnectWrites_CountedAsSocketBytes()
    {
        var meter = new TrafficMeter(() => DateTime.UtcNow);
        var ctx = Context("GET", "/director-stream", "/director-stream");
        var connect = new FakeExtendedConnect();
        ctx.Features.Set<IHttpExtendedConnectFeature>(connect);

        await Run(meter, ctx, async () =>
        {
            var socket = await ctx.Features.Get<IHttpExtendedConnectFeature>()!.AcceptAsync();
            await socket.WriteAsync(new byte[250]);
        });

        Assert.Equal(250, OnlyRow(meter).SocketBytes);
        Assert.Equal(250, connect.Wire.Length);
    }

    [Fact]
    public async Task InvokeAsync_UnflushedWriterBytes_FlushedAndCounted()
    {
        var meter = new TrafficMeter(() => DateTime.UtcNow);
        var ctx = Context("GET", "/sessions", "/sessions");
        var wire = (MemoryStream)ctx.Response.Body;

        await Run(meter, ctx, () =>
        {
            var span = ctx.Response.BodyWriter.GetSpan(64);
            span[..64].Fill(7);
            ctx.Response.BodyWriter.Advance(64);
            return Task.CompletedTask;
        });

        Assert.Equal(64, wire.Length);
        Assert.Equal(64, OnlyRow(meter).BodyBytes);
    }

    [Fact]
    public async Task InvokeAsync_NoEndpoint_KnownSegmentNamed_UnknownSegmentIsOther()
    {
        var meter = new TrafficMeter(() => new DateTime(2026, 10, 3, 4, 0, 0, DateTimeKind.Utc));
        var shell = Context("GET", "/assets/index-abc123.js");
        var refused = Context("GET", "/sessions/5b0e1c52-7f59-4c1e-9d1a-0b8b1f6d0a11/history");
        var invented = Context("GET", "/an-attacker-chose-this-text/x");
        var oddMethod = Context("BREW", "/sessions");

        foreach (var ctx in new[] { shell, refused, invented, oddMethod })
            await Run(meter, ctx, () => ctx.Response.Body.WriteAsync(new byte[10]).AsTask(), tenant: null);

        var routes = Assert.Single(meter.Snapshot(TenantId.Local).Hours).Rows.Select(r => r.Route).OrderBy(r => r).ToList();
        Assert.Equal(new[] { "GET /assets/* (no endpoint)", "GET /sessions/* (no endpoint)", TrafficMeter.OtherRoute }, routes);
        Assert.DoesNotContain(routes, r => r.Contains("attacker") || r.Contains("BREW"));
    }

    [Fact]
    public async Task InvokeAsync_PastTheRowCap_NewKeysFoldIntoOneRow()
    {
        var meter = new TrafficMeter(() => new DateTime(2026, 10, 3, 4, 0, 0, DateTimeKind.Utc));
        for (var i = 0; i < TrafficMeter.MaxRowsPerHour + 25; i++)
        {
            var ctx = Context("GET", $"/route{i}", $"/route{i}");
            await Run(meter, ctx, () => Task.CompletedTask);
        }

        var rows = Assert.Single(meter.Snapshot(TenantId.Local).Hours).Rows;
        Assert.Equal(TrafficMeter.MaxRowsPerHour + 1, rows.Count);
        Assert.Equal(25, Assert.Single(rows, r => r.Route == TrafficMeter.OverflowRoute).Requests);
    }

    [Fact]
    public async Task InvokeAsync_ReCasedSegment_NamedInTheGatewaysSpelling_OneRow()
    {
        var meter = new TrafficMeter(() => new DateTime(2026, 10, 3, 4, 0, 0, DateTimeKind.Utc));
        foreach (var path in new[] { "/SESSIONS/a", "/SeSsIoNs/b", "/sessions/c" })
            await Run(meter, Context("GET", path), () => Task.CompletedTask, tenant: null);

        var row = OnlyRow(meter);
        Assert.Equal("GET /sessions/* (no endpoint)", row.Route);
        Assert.Equal(3, row.Requests);
    }

    [Fact]
    public async Task InvokeAsync_AnonymousRowsPastTheirCap_DoNotCrowdOutATenantsRoutes()
    {
        var meter = new TrafficMeter(() => new DateTime(2026, 10, 3, 4, 0, 0, DateTimeKind.Utc));
        for (var i = 0; i < TrafficMeter.MaxRowsPerHour + 50; i++)
        {
            var anonymous = Context("GET", $"/route{i}", $"/route{i}");
            await meter.InvokeAsync(anonymous, () => Task.CompletedTask, _ => null);
        }

        var owned = Context("GET", "/sessions", "/sessions");
        owned.Items[AuthMiddleware.DeviceTypeItemKey] = "browser";
        await Run(meter, owned, () => Task.CompletedTask, Alpha);

        var rows = Assert.Single(meter.Snapshot(Alpha).Hours).Rows;
        Assert.Contains(rows, r => r.Route == "GET /sessions" && r.Caller == "device:browser");
        Assert.Equal(50, Assert.Single(rows, r => r.Route == TrafficMeter.OverflowRoute).Requests);
    }

    [Fact]
    public async Task Snapshot_AuthenticatedRowWithNoTenant_ShownToNobody()
    {
        var meter = new TrafficMeter(() => new DateTime(2026, 10, 3, 4, 0, 0, DateTimeKind.Utc));
        var machine = Context("GET", "/sessions", "/sessions");
        machine.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "machine token";
        await meter.InvokeAsync(machine, () => Task.CompletedTask, _ => null);

        Assert.Empty(Assert.Single(meter.Snapshot(Alpha).Hours).Rows);
        Assert.Empty(Assert.Single(meter.Snapshot(TenantId.Local).Hours).Rows);
    }

    [Fact]
    public async Task InvokeAsync_AbortedRequest_WriterNotFlushed()
    {
        var meter = new TrafficMeter(() => DateTime.UtcNow);
        var ctx = Context("GET", "/events", "/events");
        using var gone = new CancellationTokenSource();
        gone.Cancel();
        ctx.RequestAborted = gone.Token;
        var wire = (MemoryStream)ctx.Response.Body;

        await Run(meter, ctx, () =>
        {
            ctx.Response.BodyWriter.GetSpan(16);
            ctx.Response.BodyWriter.Advance(16);
            return Task.CompletedTask;
        });

        Assert.Equal(0, wire.Length);
    }

    [Fact]
    public async Task Snapshot_OtherTenantsRows_NeverShown()
    {
        var meter = new TrafficMeter(() => new DateTime(2026, 10, 3, 4, 0, 0, DateTimeKind.Utc));
        await Run(meter, Context("GET", "/sessions", "/sessions"), () => Task.CompletedTask, Alpha);
        await Run(meter, Context("GET", "/sessions/{sid}/history", "/sessions/{sid}/history"), () => Task.CompletedTask, Beta);
        await meter.InvokeAsync(Context("GET", "/assets/a.js"), () => Task.CompletedTask, _ => null);

        var alphaSees = Assert.Single(meter.Snapshot(Alpha).Hours).Rows.Select(r => r.Route).OrderBy(r => r).ToList();
        var betaSees = Assert.Single(meter.Snapshot(Beta).Hours).Rows.Select(r => r.Route).OrderBy(r => r).ToList();

        Assert.Equal(new[] { "GET /assets/* (no endpoint)", "GET /sessions" }, alphaSees);
        Assert.Equal(new[] { "GET /assets/* (no endpoint)", "GET /sessions/{sid}/history" }, betaSees);
    }

    [Fact]
    public async Task InvokeAsync_AfterTheRequest_OriginalFeaturesRestored()
    {
        var meter = new TrafficMeter(() => DateTime.UtcNow);
        var ctx = Context("GET", "/sessions", "/sessions");
        var body = ctx.Features.Get<IHttpResponseBodyFeature>();
        var upgrade = new FakeUpgrade();
        ctx.Features.Set<IHttpUpgradeFeature>(upgrade);

        await Run(meter, ctx, () => Task.CompletedTask);

        Assert.Same(body, ctx.Features.Get<IHttpResponseBodyFeature>());
        Assert.Same(upgrade, ctx.Features.Get<IHttpUpgradeFeature>());
    }

    [Fact]
    public async Task Snapshot_HoursPastRetention_Dropped_TheEdgeHourKept()
    {
        var clock = new Clock();
        var meter = new TrafficMeter(() => clock.Now);
        await Run(meter, Context("GET", "/sessions", "/sessions"), () => Task.CompletedTask);
        clock.Now = clock.Now.AddHours(1);
        await Run(meter, Context("GET", "/sessions", "/sessions"), () => Task.CompletedTask);

        clock.Now = clock.Now.AddHours(TrafficMeter.RetainedHours - 1);

        var hours = meter.Snapshot(TenantId.Local).Hours;
        Assert.Equal(new DateTime(2026, 10, 3, 5, 0, 0, DateTimeKind.Utc), Assert.Single(hours).HourUtc);
    }

    [Fact]
    public async Task TallyFor_NewHour_PrunesWithoutARead()
    {
        var clock = new Clock();
        var meter = new TrafficMeter(() => clock.Now);
        for (var i = 0; i < TrafficMeter.RetainedHours + 10; i++)
        {
            await Run(meter, Context("GET", "/sessions", "/sessions"), () => Task.CompletedTask);
            clock.Now = clock.Now.AddHours(1);
        }

        Assert.Equal(TrafficMeter.RetainedHours, meter.HeldHourCount);
    }
}
