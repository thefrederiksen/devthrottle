using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CcDirector.Core.Boxes;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.UnitTests.Boxes;

/// <summary>
/// Factory Boxes (issue #4258), the Delivery Lead's acceptance point: a busy desktop must not slow the terminal
/// streams and commands on the same Director connection.
///
/// This runs over a REAL SignalR connection - the real <see cref="DirectorHub"/> with the Gateway's own hub options
/// (MessagePack, the shared stream buffer of <see cref="DirectorStreamLimits.StreamBufferCapacity"/>) - on an
/// in-memory host, with one Director connection carrying both a terminal up-stream and a desktop relay stream. The
/// desktop's box floods 48-kilobyte frames as fast as it is allowed and its browser NEVER reads (the worst case: a
/// stalled phone). Then the terminal sends its frames and the Gateway sends the Director a command, on the same
/// connection, while the desktop sits saturated.
///
/// Why this is the danger: SignalR gives a client's up-streams one shared receive loop. When one stream's buffer is
/// full the hub stops reading the WHOLE connection. The terminal's own up-stream relies on that to push back - fine
/// for a few kilobytes of terminal output, fatal for a desktop: a stalled desktop browser would stop every terminal
/// frame and every command reply behind it. The relay's window keeps the desktop from ever filling that buffer.
/// </summary>
public sealed class DesktopRelayFairnessTests : IAsyncLifetime
{
    private const string DirectorId = "director-fair";
    private const int TerminalFrames = 200;

    private readonly ITestOutputHelper _output;
    private readonly GatewayDbTestHarness _harness = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-desktop-fairness-" + Guid.NewGuid().ToString("N"));
    private readonly GatewayStreamRegistry _streams = new();
    private readonly DesktopRelayRegistry _desktops = new();
    private DirectorRegistry? _registry;
    private GatewayInputStatsAggregator? _stats;
    private WebApplication? _app;
    private HubConnection? _director;
    private int _commandsAnswered;

    public DesktopRelayFairnessTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _registry = new DirectorRegistry(_dir);
        _stats = new GatewayInputStatsAggregator(Path.Combine(_dir, "gateway-stats.db"));
        var boundary = new Tenancy.HostedTenantBoundary(new SingleTenantContext(),
            new Pairing.DeviceRegistry(_harness.Open(), _harness.LegacyPath("devices.json")), hosted: false);

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        // The Gateway's own hub options (GatewayHost), so the shared stream buffer is the production one.
        builder.Services.AddSignalR()
            .AddMessagePackProtocol()
            .AddHubOptions<DirectorHub>(o =>
            {
                o.MaximumReceiveMessageSize = DirectorStreamLimits.MaxInboundMessageBytes;
                o.StreamBufferCapacity = DirectorStreamLimits.StreamBufferCapacity;
                o.KeepAliveInterval = DirectorStreamLimits.KeepAlivePing;
                o.ClientTimeoutInterval = DirectorStreamLimits.SilenceTolerance;
            });
        builder.Services.AddSingleton(new PushedSessionStore());
        builder.Services.AddSingleton(_registry);
        builder.Services.AddSingleton(InputStatsHandle.Available(_stats));
        builder.Services.AddSingleton(_streams);
        builder.Services.AddSingleton(boundary);
        builder.Services.AddSingleton(_desktops);
        var app = builder.Build();
        app.UseWebSockets();
        app.MapHub<DirectorHub>("/director-stream");
        await app.StartAsync();
        _app = app;

        var server = app.GetTestServer();
        _director = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "director-stream"), o =>
            {
                o.Transports = HttpTransportType.WebSockets;
                o.HttpMessageHandlerFactory = _ => server.CreateHandler();
                o.WebSocketFactory = async (context, ct) => await server.CreateWebSocketClient().ConnectAsync(context.Uri, ct);
            })
            .AddMessagePackProtocol()
            .Build();
        _director.On<DirectorCommand, DirectorCommandResult>("Command", cmd =>
        {
            Interlocked.Increment(ref _commandsAnswered);
            return DirectorCommandResult.Success("{\"pong\":true}");
        });
        await _director.StartAsync().Within();
        await _director.InvokeAsync<GatewayCapabilities?>("Hello", new DirectorStreamHello
        {
            DirectorId = DirectorId, Version = "test", MachineName = "fairness", User = "test", Pid = 1, StartedAt = DateTime.UtcNow,
        }).Within();
    }

    public async Task DisposeAsync()
    {
        if (_director is not null) await _director.DisposeAsync();
        if (_app is not null) await _app.DisposeAsync();
        _stats?.Dispose();
        _registry?.Dispose();
        _harness.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    [Fact]
    public async Task TerminalFramesAndCommands_StillFlow_WhileADesktopStreamIsSaturated()
    {
        var owner = new StreamOwner(TenantId.Local, DirectorId);

        // ---- the desktop: a flooding box on the Director, a browser on the Gateway that never reads ----
        var pending = _desktops.Register("desktop", owner);
        var box = new FloodingBox();
        var up = Channel.CreateUnbounded<DesktopRelayFrame>();
        var down = _director!.StreamAsync<DesktopRelayFrame>(DesktopRelayProtocol.HubMethod, "desktop", up.Reader.ReadAllAsync());
        var directorPump = new DesktopRelayPump(box, up.Writer, new DesktopRelayUsage("box", "desktop", "director"), new DesktopRelayBoxTally(),
            endpointIsBrowser: false, TimeProvider.System, idleTimeout: null, outboundLimit: null, "gateway gone", "box gone");
        var directorRun = directorPump.RunAsync(down, CancellationToken.None, CancellationToken.None);

        var attachment = await pending.Attached.Within();
        var browser = new StalledBrowser();
        var gatewayPump = new DesktopRelayPump(browser, pending.Down.Writer, new DesktopRelayUsage("box", "desktop", "gateway"), new DesktopRelayBoxTally(),
            endpointIsBrowser: true, TimeProvider.System, idleTimeout: null, outboundLimit: null, "director gone", "browser gone");
        var gatewayRun = gatewayPump.RunAsync(attachment.Up, attachment.HubCancellation, CancellationToken.None);

        // Saturated: the Director has used its whole window and the box's next frame is waiting for credit.
        await Until(() => directorPump.SendWindowAvailable < DesktopRelayLimits.MaxFrameBytes + DesktopRelayLimits.FrameOverheadBytes
                          && box.WaitingForTheRelay);
        var producedAtSaturation = box.FramesProduced;

        // ---- the terminal, on the SAME connection, while the desktop is saturated ----
        var terminal = new CountingSink(TerminalFrames);
        _streams.Register("terminal", owner, terminal);
        var watch = Stopwatch.StartNew();
        _ = _director!.SendAsync("StreamUp", "terminal", TerminalOutput());
        await terminal.AllArrived.Task.Within();
        var terminalElapsed = watch.Elapsed;

        // ---- a command from the Gateway to the Director, on the same connection ----
        var hub = _app!.Services.GetRequiredService<IHubContext<DirectorHub>>();
        var connectionId = _director!.ConnectionId!;
        watch.Restart();
        var reply = await hub.Clients.Client(connectionId).InvokeAsync<DirectorCommandResult>("Command",
            new DirectorCommand { CommandId = "c1", Verb = "ping", SessionId = "" }, CancellationToken.None).Within();
        var commandElapsed = watch.Elapsed;

        // The desktop is still saturated and nothing more of it was produced: it was slowed, not queued without limit.
        var producedAfter = box.FramesProduced;
        _output.WriteLine($"desktop frames produced before the window closed: {producedAtSaturation} (cost {DesktopRelayLimits.MaxFrameBytes + DesktopRelayLimits.FrameOverheadBytes} bytes each, window {DesktopRelayLimits.WindowBytes} bytes)");
        _output.WriteLine($"desktop frames delivered to the stalled browser: {browser.Delivered}");
        _output.WriteLine($"terminal frames delivered while the desktop was saturated: {terminal.Count} of {TerminalFrames}, in {terminalElapsed.TotalMilliseconds:F1} milliseconds");
        _output.WriteLine($"command answered while the desktop was saturated: {reply.Ok}, in {commandElapsed.TotalMilliseconds:F1} milliseconds");

        Assert.Equal(TerminalFrames, terminal.Count);
        Assert.True(reply.Ok);
        Assert.Equal(1, Volatile.Read(ref _commandsAnswered));
        Assert.Equal(0, browser.Delivered);
        Assert.Equal(producedAtSaturation, producedAfter);
        Assert.True(producedAtSaturation * (long)(DesktopRelayLimits.MaxFrameBytes + DesktopRelayLimits.FrameOverheadBytes) <= DesktopRelayLimits.WindowBytes + DesktopRelayLimits.MaxFrameBytes,
            $"the box got {producedAtSaturation} frames out with the browser stalled; the window allows {DesktopRelayLimits.WindowBytes} bytes");

        // ---- the browser starts reading again: the desktop resumes (it was slowed, never dropped) ----
        browser.Release();
        await Until(() => browser.Delivered >= producedAtSaturation + 20);
        Assert.True(box.FramesProduced > producedAtSaturation);
        Assert.Equal(DesktopRelayFrameKind.Data, browser.FirstKind);

        box.Stop();
        await Task.WhenAll(directorRun, gatewayRun).Within();
    }

    private static async IAsyncEnumerable<DirectorStreamFrame> TerminalOutput()
    {
        for (var i = 0; i < TerminalFrames; i++)
        {
            await Task.Yield();
            yield return new DirectorStreamFrame { Kind = DirectorStreamFrameType.Binary, Data = new byte[100] };
        }
        yield return new DirectorStreamFrame { Kind = DirectorStreamFrameType.Closed, Reason = "done" };
    }

    private static async Task Until(Func<bool> condition)
    {
        var watchdog = Stopwatch.StartNew();
        while (!condition())
        {
            if (watchdog.Elapsed > Watchdog.Limit) throw new TimeoutException("the relay never reached the expected state");
            await Task.Yield();
        }
    }

    /// <summary>A box whose desktop floods full frames for as long as the relay takes them.</summary>
    private sealed class FloodingBox : IDesktopRelayEndpoint
    {
        private readonly CancellationTokenSource _stop = new();
        private int _produced;
        private volatile bool _waiting;

        public int FramesProduced => Volatile.Read(ref _produced);

        /// <summary>True once the relay has stopped asking for the next frame (it is waiting on its window).</summary>
        public bool WaitingForTheRelay => _waiting;

        public void Stop() => _stop.Cancel();

        public async IAsyncEnumerable<DesktopRelayFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            var frame = new byte[DesktopRelayLimits.MaxFrameBytes];
            while (!ct.IsCancellationRequested && !_stop.IsCancellationRequested)
            {
                await Task.Yield();
                _waiting = true;
                yield return new DesktopRelayFrame { Kind = DesktopRelayFrameKind.Data, Data = frame, EndOfMessage = true };
                _waiting = false;
                Interlocked.Increment(ref _produced);
            }
            yield return new DesktopRelayFrame { Kind = DesktopRelayFrameKind.Closed, CloseStatus = 1000, Reason = "test over" };
        }

        public Task DeliverAsync(DesktopRelayFrame frame, CancellationToken ct) => Task.CompletedTask;

        public Task CloseAsync(int closeStatus, string reason) => Task.CompletedTask;
    }

    /// <summary>A browser that does not read until it is released - a phone on a dead connection.</summary>
    private sealed class StalledBrowser : IDesktopRelayEndpoint
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _delivered;

        public int Delivered => Volatile.Read(ref _delivered);
        public DesktopRelayFrameKind? FirstKind { get; private set; }

        public void Release() => _released.TrySetResult();

        public async IAsyncEnumerable<DesktopRelayFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { }, TaskScheduler.Default);
            yield break;
        }

        public async Task DeliverAsync(DesktopRelayFrame frame, CancellationToken ct)
        {
            await _released.Task.WaitAsync(ct);
            FirstKind ??= frame.Kind;
            Interlocked.Increment(ref _delivered);
        }

        public Task CloseAsync(int closeStatus, string reason)
        {
            _released.TrySetResult();
            return Task.CompletedTask;
        }
    }

    /// <summary>A terminal browser that counts the frames it is handed.</summary>
    private sealed class CountingSink : IStreamSink
    {
        private readonly int _expected;
        private int _count;

        public CountingSink(int expected) => _expected = expected;

        public int Count => Volatile.Read(ref _count);
        public TaskCompletionSource AllArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WriteFrameAsync(DirectorStreamFrame frame, CancellationToken cancellationToken)
        {
            if (frame.Kind == DirectorStreamFrameType.Binary && Interlocked.Increment(ref _count) == _expected)
                AllArrived.TrySetResult();
            return Task.CompletedTask;
        }

        public Task CompleteAsync(string? reason) => Task.CompletedTask;
    }
}
