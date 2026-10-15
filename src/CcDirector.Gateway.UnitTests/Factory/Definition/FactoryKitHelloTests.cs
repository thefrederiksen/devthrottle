using System.Security.Claims;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Streaming;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// WHAT A DIRECTOR SAYS ON HELLO ABOUT THE SEAT'S KIT IS RECORDED (Factory Definition mission, phase 5), through the real
/// hub on a fake caller: a Director that says it builds the kit is recorded as one, an older Director that says nothing
/// is recorded as not, and the same Director coming back older is recorded again. And this build of the Director says
/// TRUE: the last Director pull request of the phase (the launch, P4) turned it on.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactoryKitHelloTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "cc-kit-hello-" + Guid.NewGuid().ToString("N"));
    private readonly DirectorRegistry _registry;
    private readonly PushedSessionStore _store = new(() => new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc));
    private readonly GatewayInputStatsAggregator _inputStats;
    private readonly FactoryKitCapabilityRegistry _kit = new();

    public FactoryKitHelloTests()
    {
        Directory.CreateDirectory(_tempDir);
        _registry = new DirectorRegistry(_tempDir);
        _inputStats = new GatewayInputStatsAggregator(Path.Combine(_tempDir, "gateway-stats.db"));
    }

    public void Dispose()
    {
        _registry.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { /* scratch folder */ }
    }

    private DirectorHub NewHub(string connectionId) => new(_store, _registry, InputStatsHandle.Available(_inputStats),
        new GatewayStreamRegistry(),
        new CcDirector.Gateway.Tenancy.HostedTenantBoundary(new SingleTenantContext(), new CcDirector.Gateway.Pairing.DeviceRegistry(), hosted: false),
        factoryKitCapabilities: _kit) { Context = new FakeHubCallerContext(connectionId) };

    [Fact]
    public void Hello_ADirectorThatBuildsTheKit_IsRecordedAsOne()
    {
        NewHub("conn-1").Hello(new DirectorStreamHello { DirectorId = "dir-A", Version = "test", BuildsFactoryKit = true });

        Assert.True(_kit.BuildsFactoryKit(TenantId.Local, "dir-A"));
    }

    [Fact]
    public void Hello_AnOlderDirectorThatSaysNothing_IsRecordedAsNot()
    {
        NewHub("conn-1").Hello(new DirectorStreamHello { DirectorId = "dir-A", Version = "test" });

        Assert.False(_kit.BuildsFactoryKit(TenantId.Local, "dir-A"));
    }

    [Fact]
    public void Hello_TheSameDirectorComingBackOlder_IsRecordedAgain()
    {
        NewHub("conn-1").Hello(new DirectorStreamHello { DirectorId = "dir-A", Version = "new", BuildsFactoryKit = true });
        NewHub("conn-2").Hello(new DirectorStreamHello { DirectorId = "dir-A", Version = "old" });

        Assert.False(_kit.BuildsFactoryKit(TenantId.Local, "dir-A"));
    }

    [Fact]
    public void GatewayStreamClient_ThisBuildOfTheDirector_SaysItBuildsTheKit()
    {
        // The flag was turned on by the LAST Director pull request of phase 5 (the launch: rules, skills and secrets),
        // once the kit, the launch and the machine question were all on main. This reads the line that says it; the
        // Hello itself is SecretTransferMachineKeyTests.ReseedAsync_Always_HelloSaysThisDirectorBuildsASeatsKit.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "CcDirector.ControlApi", "GatewayStreamClient.cs"));

        Assert.Contains("BuildsFactoryKit = true,", source);
        Assert.DoesNotContain("BuildsFactoryKit = false", source);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "cc-director.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("The repository root (cc-director.sln) was not found above the test binaries.");
    }

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
