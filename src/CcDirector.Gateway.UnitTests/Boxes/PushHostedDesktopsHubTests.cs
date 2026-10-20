using System.Security.Claims;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Streaming;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace CcDirector.Gateway.Tests.Boxes;

/// <summary>
/// <c>DirectorHub.PushHostedDesktops</c> (the Factory Boxes mission, phase 1), driven through the real hub against the
/// real store: a push after Hello lands under the Director and the machine its Hello named, a push before Hello is
/// refused, and the list goes when the connection does.
/// </summary>
public sealed class PushHostedDesktopsHubTests
{
    private const string DirectorId = "dir-devlinux";
    private const string Connection = "connection-1";

    private readonly PushedHostedDesktopStore _desktops = new();

    // The registry is never started, so its directory is never watched or created.
    private readonly DirectorRegistry _registry = new(Path.Combine(Path.GetTempPath(), "never-created-" + Guid.NewGuid().ToString("N")));

    private DirectorHub NewHub(string connection = Connection) =>
        new(new PushedSessionStore(), _registry, InputStatsHandle.Unavailable("not under test"), new GatewayStreamRegistry(),
            new CcDirector.Gateway.Tenancy.HostedTenantBoundary(new SingleTenantContext(), new CcDirector.Gateway.Pairing.DeviceRegistry(), hosted: false),
            hostedDesktops: _desktops)
        { Context = new FakeCallerContext(connection) };

    private static DirectorStreamHello Hello() => new() { DirectorId = DirectorId, Version = "test", MachineName = "devlinux", DisplayName = "devlinux slot 1" };

    private static HostedDesktopDto[] Demo() => new[] { new HostedDesktopDto { BoxId = "demo-desktop-soren", DisplayName = "Soren's demo desktop" } };

    [Fact]
    public void PushHostedDesktops_AfterHello_IsLocatedOnThatDirectorAndMachine()
    {
        var hub = NewHub();
        hub.Hello(Hello());

        hub.PushHostedDesktops(1, Demo());

        var located = _desktops.TryLocate(TenantId.Local, "demo-desktop-soren");
        Assert.NotNull(located);
        Assert.Equal(DirectorId, located!.DirectorId);
        Assert.Equal("devlinux", located.MachineName);
        Assert.Equal("devlinux slot 1", located.DirectorName);
    }

    [Fact]
    public void PushHostedDesktops_BeforeHello_IsRefused()
    {
        var hub = NewHub();

        Assert.Throws<HubException>(() => hub.PushHostedDesktops(1, Demo()));
        Assert.Empty(_desktops.ListFor(TenantId.Local));
    }

    [Fact]
    public async Task OnDisconnectedAsync_TheDirectorsDesktopsAreDropped()
    {
        var hub = NewHub();
        hub.Hello(Hello());
        hub.PushHostedDesktops(1, Demo());

        await hub.OnDisconnectedAsync(null);

        Assert.Empty(_desktops.ListFor(TenantId.Local));
    }

    [Fact]
    public void Capabilities_TheHubAdvertisesPushHostedDesktops()
    {
        // The Director pushes only to a Gateway whose Hello answer lists the method, so the hub must list it.
        var hub = NewHub();

        var capabilities = hub.Hello(Hello());

        Assert.Contains("PushHostedDesktops", capabilities!.HubMethods);
    }

    private sealed class FakeCallerContext : HubCallerContext
    {
        public FakeCallerContext(string connectionId)
        {
            ConnectionId = connectionId;
            // The self-host boundary resolves Local only for a connection that has an HttpContext, as a real
            // SignalR negotiate does.
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
