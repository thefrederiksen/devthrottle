using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Machine.ResourceSummaries;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.UnitTests.Boxes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static CcDirector.Gateway.UnitTests.Resources.ResourceWindows;

namespace CcDirector.Gateway.UnitTests.Resources;

/// <summary>
/// The five-minute summary travelling the WHOLE way (Mission: Resource Monitor, section 6): the Director's real
/// recorder, outbox and stream client, over a real SignalR connection speaking MessagePack - the protocol the Director
/// uses - into the real DirectorHub on an in-memory server, and into the statistics tables the migration creates.
///
/// WHAT THIS TRACES. The mission document listed "the new method rides the existing stream with no new connection and
/// no new authentication" as believed but never traced. Here the summary goes up the same HubConnection that carries
/// Hello and PushSnapshot, on /director-stream, inside the same ReseedAsync - there is no second connection to open.
/// The authentication is the connection's own: the hub takes the tenant from the Hello binding.
///
/// WHAT IT DOES NOT COVER. The production Gateway's authentication middleware is not in front of this hub (the test
/// server has none), so this proves the method rides the connection, not that the hosted key check admits it; that
/// check is the same one every hub method already passes at negotiate time, before any method is called.
///
/// No real port, no real process and no real clock: the server is in memory and every time is a fake.
/// </summary>
[Trait("Category", "Resources")]
public sealed class ResourceSummaryTunnelTests : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc);
    private const string DirectorId = "director-tunnel";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-resource-tunnel-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(T0));
    private readonly ResourceSummaryTestDb _db = new();
    private DirectorRegistry? _registry;
    private WebApplication? _app;
    private HubConnection? _connection;
    private ResourceSummaryStore? _store;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _registry = new DirectorRegistry(_dir);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
        if (_app is not null) await _app.DisposeAsync();
        _registry?.Dispose();
        _db.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* temporary folder */ }
    }

    /// <summary>The Gateway's DirectorHub on an in-memory server, with or without a resource summary store.</summary>
    private async Task StartGatewayAsync(bool withResourceSummaries)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddLogging();
        builder.Services.AddSignalR().AddMessagePackProtocol();
        builder.Services.AddSingleton(new PushedSessionStore());
        builder.Services.AddSingleton(_registry!);
        builder.Services.AddSingleton(InputStatsHandle.Unavailable("not exercised by the resource summary tests"));
        builder.Services.AddSingleton(new GatewayStreamRegistry());
        builder.Services.AddSingleton(new HostedTenantBoundary(new SingleTenantContext(), new DeviceRegistry(), hosted: false));
        if (withResourceSummaries)
        {
            _store = new ResourceSummaryStore(() => _db.Factory, _clock);
            builder.Services.AddSingleton(_store);
        }
        var app = builder.Build();
        app.UseWebSockets();
        app.MapHub<DirectorHub>("/director-stream");
        await app.StartAsync();
        _app = app;

        var server = app.GetTestServer();
        _connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "director-stream"), o =>
            {
                o.Transports = HttpTransportType.WebSockets;
                o.HttpMessageHandlerFactory = _ => server.CreateHandler();
                o.WebSocketFactory = async (context, ct) => await server.CreateWebSocketClient().ConnectAsync(context.Uri, ct);
            })
            .AddMessagePackProtocol()
            .Build();
        await _connection.StartAsync().Within();
    }

    /// <summary>The Director's real stream client, never started: the test hands it the connection to reseed on.</summary>
    private static GatewayStreamClient NewDirector(ResourceSummaryOutbox outbox) =>
        new(new GatewayConfig(), DirectorId, "test", snapshot: () => new List<SessionDto>(), resourceSummaries: outbox);

    private async Task ReseedAsync(GatewayStreamClient director)
    {
        var report = await director.ReseedAsync(new HubConnectionCalls(_connection!), director.CurrentConnectionGeneration).Within();
        Assert.True(report.Completed, $"the reseed did not complete: {report.Failure}");
    }

    private List<DateTime> StoredWindowStarts() =>
        _store!.MachineWindows(TenantId.Local, T0.AddHours(-1), T0.AddHours(2))!.Select(w => w.WindowStartUtc).ToList();

    private static IEnumerable<DateTime> EveryFiveMinutes(int count) => Enumerable.Range(0, count).Select(i => T0.AddMinutes(5 * i));

    [Fact]
    public async Task TenMinuteDisconnect_LeavesNoGapInTheStoredFiveMinuteRows()
    {
        await StartGatewayAsync(withResourceSummaries: true);
        var outbox = new ResourceSummaryOutbox(_clock);
        var recorder = new ResourceSummaryRecorder(outbox, "devlinux");
        var director = NewDirector(outbox);
        var heldDuringOutage = -1;
        var storedBeforeReconnect = new List<DateTime>();

        // Thirty minutes of five-second samples. The tunnel re-pushes every minute while it is up, and is down from
        // minute 10 to minute 20.
        for (var second = 0; second < 30 * 60; second += 5)
        {
            var now = T0.AddSeconds(second);
            _clock.SetUtcNow(new DateTimeOffset(now));
            recorder.Add(Sample(now, cpu: 30, usedBytes: 8 * Gb));

            if (second == 20 * 60)
            {
                heldDuringOutage = outbox.Count;
                storedBeforeReconnect = StoredWindowStarts();
                await _connection!.StartAsync().Within();
            }

            if (second % 60 != 0) continue;
            if (_connection!.State == HubConnectionState.Connected)
                await ReseedAsync(director);
            else
                // What the Director's own tick does while the tunnel is down: the push finds no connection and keeps
                // every window.
                await director.PushResourceSummariesAsync(new HubConnectionCalls(_connection));

            // The tunnel drops right after the minute-10 push.
            if (second == 10 * 60)
                await _connection.StopAsync().Within();
        }
        _clock.SetUtcNow(new DateTimeOffset(T0.AddMinutes(30)));
        Assert.True(recorder.CloseIfDue(T0.AddMinutes(30)));
        await ReseedAsync(director);

        // The outage really cost something: the two windows that closed while it was down were held, not sent.
        Assert.Equal(2, heldDuringOutage);
        Assert.Equal(EveryFiveMinutes(2), storedBeforeReconnect);
        // And the catch-up filled them: six contiguous windows, 10:00 to 10:25, each a whole window.
        Assert.Equal(EveryFiveMinutes(6), StoredWindowStarts());
        Assert.All(_store!.MachineWindows(TenantId.Local, T0, T0.AddHours(1))!, w => Assert.Equal(60, w.SampleCount));
        Assert.Equal(0, outbox.Count);
    }

    [Fact]
    public async Task Hello_OffersTheMethod_AndARedSpikeSurvivesTheWire()
    {
        await StartGatewayAsync(withResourceSummaries: true);
        var outbox = new ResourceSummaryOutbox(_clock);
        outbox.Add(WithRedSpike(T0));
        _clock.SetUtcNow(new DateTimeOffset(T0.AddMinutes(6)));
        var director = NewDirector(outbox);

        await ReseedAsync(director);

        Assert.True(director.GatewaySupports(ResourceSummaryContract.PushHubMethod));
        var row = Assert.Single(_store!.MachineWindows(TenantId.Local, T0, T0.AddHours(1))!);
        Assert.Equal(DirectorId, row.DirectorId);
        Assert.Equal(99, row.ProcessorPeakPercent);
        var crossing = Assert.Single(ResourceSummaryStore.CrossingsOf(row));
        Assert.Equal(ResourceSummaryContract.Red, crossing.Level);
        Assert.Equal(90, crossing.Seconds);
        Assert.Equal(0, outbox.Count);
    }

    [Fact]
    public async Task AWindowNamingAnotherMachine_IsFiledUnderTheMachineTheDirectorRegisteredInItsHello()
    {
        await StartGatewayAsync(withResourceSummaries: true);
        var outbox = new ResourceSummaryOutbox(_clock);
        outbox.Add(Steady(T0, 30, 8 * Gb, machine: "a-different-spelling"));
        _clock.SetUtcNow(new DateTimeOffset(T0.AddMinutes(6)));
        var director = NewDirector(outbox);

        await ReseedAsync(director);

        // The stream client's Hello names this machine (Environment.MachineName); the Machines view groups by it.
        var row = Assert.Single(_store!.MachineWindows(TenantId.Local, T0, T0.AddHours(1))!);
        Assert.Equal(Environment.MachineName, row.MachineName);
    }

    [Fact]
    public async Task AGatewayWithNoResourceStore_DoesNotOfferTheMethod_AndTheDirectorKeepsItsWindows()
    {
        await StartGatewayAsync(withResourceSummaries: false);
        var outbox = new ResourceSummaryOutbox(_clock);
        outbox.Add(Steady(T0, 30, 8 * Gb));
        _clock.SetUtcNow(new DateTimeOffset(T0.AddMinutes(6)));
        var director = NewDirector(outbox);

        await ReseedAsync(director);

        Assert.False(director.GatewaySupports(ResourceSummaryContract.PushHubMethod));
        Assert.True(director.GatewaySupports("PushSnapshot"));
        Assert.Equal(1, outbox.Count);
    }
}
