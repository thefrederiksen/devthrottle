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

    /// <summary>The figure every sample of the k-th window carries, so each stored row can be checked against its own
    /// window: a catch-up that filed one window's figures under another, or sent the same figures twice, fails.</summary>
    private static double CpuOfWindow(int k) => 10 + 5 * k;

    [Fact]
    public async Task TenMinuteDisconnect_LeavesNoGapInTheStoredFiveMinuteRows()
    {
        await StartGatewayAsync(withResourceSummaries: true);
        var outbox = new ResourceSummaryOutbox(_clock);
        var recorder = new ResourceSummaryRecorder(outbox, "devlinux");
        var director = NewDirector(outbox);
        var heldDuringOutage = new List<DateTime>();
        var storedBeforeReconnect = new List<DateTime>();

        // Thirty minutes of five-second samples, each window at its own processor figure and every sample carrying one
        // session. The tunnel re-pushes every minute while it is up. It DROPS on the very tick the 10:05 window closes,
        // before that window is pushed, and is back at minute 20.
        for (var second = 0; second < 30 * 60; second += 5)
        {
            var now = T0.AddSeconds(second);
            _clock.SetUtcNow(new DateTimeOffset(now));
            var cpu = CpuOfWindow(second / 300);
            recorder.Add(Sample(now, cpu, 8 * Gb, Core.Machine.ResourceLevel.Normal,
                new ResourceSummarySessionSample("s-tunnel", "Proof", cpu / 2, 1 * Gb, null)));

            if (second == 10 * 60)
                await _connection!.StopAsync().Within();
            if (second == 20 * 60)
            {
                heldDuringOutage = outbox.Peek(10).Select(w => w.WindowStartUtc).ToList();
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
        }
        _clock.SetUtcNow(new DateTimeOffset(T0.AddMinutes(30)));
        Assert.True(recorder.CloseIfDue(T0.AddMinutes(30)));
        await ReseedAsync(director);

        // The outage really cost something: the three windows that closed while it was down - including the one that
        // closed on the tick it dropped - were held, not sent, and only 10:00 had reached the Gateway.
        Assert.Equal(new[] { T0.AddMinutes(5), T0.AddMinutes(10), T0.AddMinutes(15) }, heldDuringOutage);
        Assert.Equal(EveryFiveMinutes(1), storedBeforeReconnect);
        // And the catch-up filled them: six contiguous windows, 10:00 to 10:25, each whole and each with ITS OWN figure.
        Assert.Equal(EveryFiveMinutes(6), StoredWindowStarts());
        var rows = _store!.MachineWindows(TenantId.Local, T0, T0.AddHours(1))!;
        for (var k = 0; k < 6; k++)
        {
            Assert.Equal(60, rows[k].SampleCount);
            Assert.Equal(CpuOfWindow(k), rows[k].ProcessorAveragePercent);
            Assert.Null(rows[k].RefusedReason);
        }
        // The session rows came through the catch-up too, one per window, each with its own figure.
        var sessions = _store.SessionWindows(TenantId.Local, Environment.MachineName, T0, T0.AddHours(1))!;
        Assert.Equal(EveryFiveMinutes(6), sessions.Select(s => s.WindowStartUtc));
        Assert.Equal(Enumerable.Range(0, 6).Select(k => CpuOfWindow(k) / 2), sessions.Select(s => s.ProcessorAveragePercent!.Value));
        Assert.Equal(0, outbox.Count);
    }

    /// <summary>The Gateway stores a batch and the answer is lost on the way back.</summary>
    private sealed class AnswerLostOnce : IDirectorHubCalls
    {
        private readonly IDirectorHubCalls _inner;
        private bool _lost;
        public AnswerLostOnce(IDirectorHubCalls inner) => _inner = inner;
        public bool IsConnected => _inner.IsConnected;
        public Task<GatewayCapabilities?> HelloAsync(DirectorStreamHello hello) => _inner.HelloAsync(hello);
        public Task RegisterSessionKeyAsync(SessionKeyRegistration registration) => _inner.RegisterSessionKeyAsync(registration);
        public Task PushSnapshotAsync(long sequence, SessionDto[] sessions) => _inner.PushSnapshotAsync(sequence, sessions);
        public Task PushDeltaAsync(long sequence, SessionDto session) => _inner.PushDeltaAsync(sequence, session);
        public Task<TurnWatermark?> PushTurnsAsync(long sequence, TurnPushBatch batch, CancellationToken ct) => _inner.PushTurnsAsync(sequence, batch, ct);
        public Task<ApiModeEventWatermark?> PushApiModeEventsAsync(ApiModeEventBatch batch, CancellationToken ct) => _inner.PushApiModeEventsAsync(batch, ct);
        public Task RevokeSessionKeyAsync(string sessionId) => _inner.RevokeSessionKeyAsync(sessionId);
        public Task PushRepoSnapshotAsync(long sequence, RepoStatusDto[] repositories) => _inner.PushRepoSnapshotAsync(sequence, repositories);
        public Task PushHostedDesktopsAsync(long sequence, HostedDesktopDto[] desktops) => _inner.PushHostedDesktopsAsync(sequence, desktops);

        public async Task<ResourceSummaryAck?> PushResourceSummariesAsync(ResourceSummaryBatch batch)
        {
            var ack = await _inner.PushResourceSummariesAsync(batch);
            if (_lost) return ack;
            _lost = true;
            throw new IOException("the connection dropped before the answer arrived");
        }
    }

    [Fact]
    public async Task ALostAcknowledgement_KeepsTheWindow_AndTheResendIsStoredOnce()
    {
        await StartGatewayAsync(withResourceSummaries: true);
        var outbox = new ResourceSummaryOutbox(_clock);
        _clock.SetUtcNow(new DateTimeOffset(T0.AddMinutes(6)));
        var director = NewDirector(outbox);
        await ReseedAsync(director);
        outbox.Add(Steady(T0, 30, 8 * Gb));

        await director.PushResourceSummariesAsync(new AnswerLostOnce(new HubConnectionCalls(_connection!)));

        // Stored on the Gateway, but the Director never heard so: it keeps the window.
        Assert.Single(StoredWindowStarts());
        Assert.Equal(1, outbox.Count);

        await ReseedAsync(director);

        Assert.Single(StoredWindowStarts());
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
