using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests.Boxes;

/// <summary>
/// The Director's half of "the Gateway knows which Director hosts which desktop" (the Factory Boxes mission, phase 1):
/// the hosted-desktop list goes up on every reseed, an empty list goes up too, nothing goes to a Gateway that does not
/// have the method, and a failure there never fails the reseed. Every hub call goes to a recording hub; nothing
/// touches the network. Here because this is the test project the default local gate runs that can see the control
/// plane.
/// </summary>
public sealed class HostedDesktopPushTests
{
    private static readonly GatewayCapabilities WithBoxes = new() { HubMethods = { "PushSnapshot", "PushHostedDesktops" } };
    private static readonly GatewayCapabilities WithoutBoxes = new() { HubMethods = { "PushSnapshot" } };

    private static GatewayStreamClient NewClient(Func<IReadOnlyList<HostedDesktopDto>>? hostedDesktops)
        // No URL: the client is never started, so nothing dials.
        => new(new GatewayConfig(), "dir-A", "test", snapshot: () => new List<SessionDto>(),
            hostedDesktops: hostedDesktops);

    private static IReadOnlyList<HostedDesktopDto> Demo() =>
        new[] { new HostedDesktopDto { BoxId = "demo-desktop-soren", DisplayName = "Soren's demo desktop" } };

    [Fact]
    public async Task ReseedAsync_GatewayHasTheMethod_PushesTheListAfterTheRoster()
    {
        var hub = new RecordingHub { Capabilities = WithBoxes };
        var client = NewClient(Demo);

        var report = await client.ReseedAsync(hub, client.CurrentConnectionGeneration);

        Assert.True(report.Completed);
        var push = Assert.Single(hub.Pushes);
        Assert.Equal("demo-desktop-soren", Assert.Single(push).BoxId);
        Assert.True(hub.Order.IndexOf("PushHostedDesktops") > hub.Order.IndexOf("PushSnapshot"));
    }

    [Fact]
    public async Task ReseedAsync_EveryReseed_PushesTheListAgainWithAHigherSequence()
    {
        var hub = new RecordingHub { Capabilities = WithBoxes };
        var client = NewClient(Demo);

        await client.ReseedAsync(hub, client.CurrentConnectionGeneration);
        await client.ReseedAsync(hub, client.CurrentConnectionGeneration);

        Assert.Equal(2, hub.Pushes.Count);
        Assert.True(hub.Sequences[1] > hub.Sequences[0]);
    }

    [Fact]
    public async Task ReseedAsync_NoDesktops_PushesAnEmptyListSoTheGatewayDropsOldOnes()
    {
        var hub = new RecordingHub { Capabilities = WithBoxes };
        var client = NewClient(() => Array.Empty<HostedDesktopDto>());

        await client.ReseedAsync(hub, client.CurrentConnectionGeneration);

        Assert.Empty(Assert.Single(hub.Pushes));
    }

    [Fact]
    public async Task ReseedAsync_GatewayWithoutTheMethod_PushesNothing()
    {
        var hub = new RecordingHub { Capabilities = WithoutBoxes };
        var client = NewClient(Demo);

        await client.ReseedAsync(hub, client.CurrentConnectionGeneration);

        Assert.Empty(hub.Pushes);
    }

    [Fact]
    public async Task ReseedAsync_NoHostedDesktopSource_PushesNothing()
    {
        var hub = new RecordingHub { Capabilities = WithBoxes };
        var client = NewClient(null);

        await client.ReseedAsync(hub, client.CurrentConnectionGeneration);

        Assert.Empty(hub.Pushes);
    }

    [Fact]
    public async Task ReseedAsync_ThePushThrows_TheReseedStillCompletes()
    {
        var hub = new RecordingHub { Capabilities = WithBoxes, RefuseDesktops = true };
        var client = NewClient(Demo);

        var report = await client.ReseedAsync(hub, client.CurrentConnectionGeneration);

        Assert.True(report.Completed);
        Assert.Null(report.Failure);
    }

    private sealed class RecordingHub : IDirectorHubCalls
    {
        public GatewayCapabilities? Capabilities { get; init; }
        public bool RefuseDesktops { get; init; }
        public List<string> Order { get; } = new();
        public List<HostedDesktopDto[]> Pushes { get; } = new();
        public List<long> Sequences { get; } = new();

        public bool IsConnected => true;

        public Task<GatewayCapabilities?> HelloAsync(DirectorStreamHello hello)
        {
            Order.Add("Hello");
            return Task.FromResult(Capabilities);
        }

        public Task RegisterSessionKeyAsync(SessionKeyRegistration registration) => Task.CompletedTask;

        public Task PushSnapshotAsync(long sequence, SessionDto[] sessions)
        {
            Order.Add("PushSnapshot");
            return Task.CompletedTask;
        }

        public Task PushDeltaAsync(long sequence, SessionDto session) => Task.CompletedTask;
        public Task<TurnWatermark?> PushTurnsAsync(long sequence, TurnPushBatch batch, CancellationToken ct) => Task.FromResult<TurnWatermark?>(null);
        public Task<ApiModeEventWatermark?> PushApiModeEventsAsync(ApiModeEventBatch batch, CancellationToken ct) => Task.FromResult<ApiModeEventWatermark?>(null);
        public Task RevokeSessionKeyAsync(string sessionId) => Task.CompletedTask;
        public Task PushRepoSnapshotAsync(long sequence, RepoStatusDto[] repositories) => Task.CompletedTask;

        public Task PushHostedDesktopsAsync(long sequence, HostedDesktopDto[] desktops)
        {
            Order.Add("PushHostedDesktops");
            if (RefuseDesktops) throw new InvalidOperationException("the Gateway refused the list");
            Pushes.Add(desktops);
            Sequences.Add(sequence);
            return Task.CompletedTask;
        }
    }
}
