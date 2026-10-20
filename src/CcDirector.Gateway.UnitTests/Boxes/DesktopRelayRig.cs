using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using CcDirector.ControlApi.Boxes;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Boxes;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace CcDirector.Gateway.UnitTests.Boxes;

/// <summary>
/// The whole desktop relay leg, in process, with nothing real underneath it: a HOSTED Gateway with two accounts on
/// an in-memory host (the real sign-in middleware, the real route, the real legs and registry), the REAL Director
/// handler, joined by an in-memory tunnel (the Gateway's command send calls the Director's handler; the Director's
/// relay call lands in the Gateway registry's Attach, which is what the hub method does), and a fake desktop on its
/// own in-memory host. The clock is a fake one the test moves.
///
/// Two hosts on the one Gateway, as in production: the Gateway's own host (<see cref="MainOrigin"/>), where the
/// Cockpit signs in and asks for a ticket, and the desktop host (<see cref="DesktopOrigin"/>), where the desktop is
/// served against a box-only cookie. Account A hosts <see cref="BoxId"/> on Director <see cref="DirectorId"/>;
/// account B hosts nothing.
/// </summary>
internal sealed class DesktopRelayRig : IAsyncDisposable
{
    public const string BoxId = "box-one";
    public const string DirectorId = "director-a";
    public const int BoxPort = 3110;

    private readonly GatewayDbTestHarness _harness = new();
    private WebApplication? _gateway;

    public static readonly Uri MainOrigin = new("http://gateway.test/");
    public static readonly Uri DesktopOrigin = new("http://desktop.test/");
    public const string GatewayAddress = "https://gateway.public.test";

    public FakeDesktop Box { get; } = new();
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));
    public DesktopRelayRegistry Registry { get; } = new();
    public DirectorDesktopRelayHandler Director { get; private set; } = null!;
    public DesktopRelayLegs Legs { get; private set; } = null!;
    public FakeHostedDesktops Desktops { get; } = new();
    public FakeBoxHosts Hosts { get; } = new();
    public FakeBoxesFeature Feature { get; } = new();
    public RecordingConnector Connector { get; private set; } = null!;
    public BoxDesktopTickets Tickets { get; private set; } = null!;

    /// <summary>The Director's tunnel connection: cancel it and the Director has disconnected.</summary>
    public CancellationTokenSource DirectorConnection { get; } = new();

    /// <summary>Every frame the Director sent up the tunnel, in order.</summary>
    public ConcurrentQueue<DesktopRelayFrame> UpFrames { get; } = new();

    /// <summary>How many open-desktop-stream commands reached the Director.</summary>
    public int OpenCommands;

    /// <summary>Commands the Gateway tried to send with no tenant scope in effect (a hosted Gateway drops them).</summary>
    public int UnscopedCommands;

    /// <summary>The tenant scope in effect for each command the Gateway sent.</summary>
    public ConcurrentQueue<TenantId> CommandTenants { get; } = new();

    private readonly AsyncLocalTenantContext _scope = new();

    public TenantId TenantA { get; private set; }
    public TenantId TenantB { get; private set; }
    public string KeyA { get; private set; } = "";
    public string KeyB { get; private set; } = "";

    public TestServer Gateway => _gateway?.GetTestServer() ?? throw new InvalidOperationException("not started");

    public static async Task<DesktopRelayRig> StartAsync(long maxBoxBytesPerSecond = long.MaxValue / 4)
    {
        var rig = new DesktopRelayRig();
        await rig.StartCoreAsync(maxBoxBytesPerSecond);
        return rig;
    }

    private async Task StartCoreAsync(long maxBoxBytesPerSecond)
    {
        await Box.StartAsync();
        Connector = new RecordingConnector(Box.Server);

        // The registry judges a key's binding against the account registry only on a hosted IMAGE; here each key
        // simply carries its account's tenant, which is all the hosted boundary below reads.
        var devices = new DeviceRegistry(_harness.Open(), _harness.LegacyPath("devices.json"));
        TenantA = new TenantId("tenant-a-" + Guid.NewGuid().ToString("N")[..8]);
        TenantB = new TenantId("tenant-b-" + Guid.NewGuid().ToString("N")[..8]);
        KeyA = devices.RegisterForTenant(TenantA, "subject-a", "browser-a", "laptop-a", deviceType: "browser").DeviceKey;
        KeyB = devices.RegisterForTenant(TenantB, "subject-b", "browser-b", "laptop-b", deviceType: "browser").DeviceKey;
        var boundary = new HostedTenantBoundary(_scope, devices, hosted: true);

        Desktops.Ports[BoxId] = BoxPort;
        Hosts.Hosted[(TenantA.Value, BoxId)] = DirectorId;
        Feature.Enabled.Add(TenantA.Value);
        Feature.Enabled.Add(TenantB.Value);

        Director = new DirectorDesktopRelayHandler(Desktops, Connector, RelayOverInMemoryTunnel, Time, maxBoxBytesPerSecond);
        Legs = new DesktopRelayLegs(Registry, SendCommandAsync, Time);
        Tickets = new BoxDesktopTickets(Time);

        // The Gateway's pipeline in the order GatewayHost builds it: the desktop host ahead of sign-in, then sign-in,
        // then the routes - here /healthz, one ordinary route, and the ticket route.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        var app = builder.Build();
        BoxDesktopEndpoints.UseDesktopHost(app, DesktopOrigin, Hosts, Feature, Tickets, Legs, boundary, GatewayAddress);
        var gate = new AuthMiddleware.RequireToken { Hosted = true, Token = "", Devices = devices, Boundary = boundary };
        app.Use((ctx, next) => AuthMiddleware.Run(ctx, gate, () => next(ctx)));
        app.MapGet("/healthz", () => "healthy");
        app.MapGet("/sessions", () => "the main host's sessions");
        BoxDesktopEndpoints.MapTicket(app, DesktopOrigin, Hosts, Feature, Tickets, boundary);
        await app.StartAsync();
        _gateway = app;
    }

    // The Gateway's command send, as the tunnel would carry it: the open goes to the real Director handler. Like
    // GatewayHost.SendCommandAsync on a hosted Gateway, it sends NOTHING when no tenant scope is in effect, and it finds
    // the Director only in the scope's own account.
    private async Task<DirectorCommandResult?> SendCommandAsync(string directorId, DirectorCommand command, CancellationToken ct)
    {
        if (_scope.CurrentOrNull is not { } scopeTenant)
        {
            Interlocked.Increment(ref UnscopedCommands);
            return null;
        }
        CommandTenants.Enqueue(scopeTenant);
        if (scopeTenant != TenantA) return null;
        if (directorId != DirectorId || DirectorConnection.IsCancellationRequested) return null;
        if (command.Verb != DesktopRelayProtocol.OpenVerb)
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {command.Verb}");
        Interlocked.Increment(ref OpenCommands);
        return await Director.HandleAsync(command);
    }

    // The Director's hub call, as the tunnel would carry it: its frames reach the Gateway registry's Attach exactly as
    // the hub's DesktopRelay method hands them over, bound to the Director's own tenant and id.
    private IAsyncEnumerable<DesktopRelayFrame> RelayOverInMemoryTunnel(string streamId, IAsyncEnumerable<DesktopRelayFrame> up, CancellationToken ct) =>
        WithCancellation(Registry.Attach(streamId, new StreamOwner(TenantA, DirectorId), Observe(up), DirectorConnection.Token), ct);

    private async IAsyncEnumerable<DesktopRelayFrame> Observe(IAsyncEnumerable<DesktopRelayFrame> up)
    {
        await foreach (var frame in up)
        {
            UpFrames.Enqueue(frame);
            yield return frame;
        }
    }

    private static async IAsyncEnumerable<DesktopRelayFrame> WithCancellation(IAsyncEnumerable<DesktopRelayFrame> frames, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var frame in frames.WithCancellation(ct))
            yield return frame;
    }

    // ------------------------------------------------------------------------------- the browser ----

    /// <summary>A client on the Gateway's own host, signed in as account A or B (or signed out), redirects left to the test.</summary>
    public HttpClient Browser(string? key)
    {
        var client = new HttpClient(Gateway.CreateHandler()) { BaseAddress = MainOrigin };
        if (key is not null) client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    /// <summary>A client on the desktop host carrying <paramref name="cookie"/> (a <c>name=value</c> pair), or no cookie.</summary>
    public HttpClient Desktop(string? cookie)
    {
        var client = new HttpClient(Gateway.CreateHandler()) { BaseAddress = DesktopOrigin };
        if (cookie is not null) client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    /// <summary>Ask the Gateway's own host for a ticket, as the Cockpit does. Returns the response and, on success, the address.</summary>
    public async Task<(HttpResponseMessage Response, string? Url)> AskForTicketAsync(string? key, string boxId = BoxId)
    {
        using var browser = Browser(key);
        var response = await browser.PostAsync($"/boxes/{boxId}/desktop-ticket", content: null);
        if (!response.IsSuccessStatusCode) return (response, null);
        var answer = RelayJson.Parse(await response.Content.ReadAsStringAsync());
        return (response, answer.GetProperty("url").GetString());
    }

    /// <summary>
    /// The whole way in, as a browser takes it: a ticket from the main host, opened on the desktop host, exchanged
    /// for the box cookie. Returns the cookie as a <c>name=value</c> pair for the requests after it.
    /// </summary>
    public async Task<string> SignInToDesktopAsync(string key, string boxId = BoxId)
    {
        var (response, url) = await AskForTicketAsync(key, boxId);
        if (url is null) throw new InvalidOperationException($"no ticket: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        using var desktop = Desktop(cookie: null);
        var opened = await desktop.GetAsync(url);
        if (!opened.IsSuccessStatusCode) throw new InvalidOperationException($"the ticket was refused: {(int)opened.StatusCode}");
        var setCookie = opened.Headers.GetValues("Set-Cookie").Single();
        return setCookie[..setCookie.IndexOf(';')];
    }

    /// <summary>Open the desktop's WebSocket on the desktop host, carrying <paramref name="cookie"/>, as a browser does.</summary>
    public Task<WebSocket> OpenDesktopSocketAsync(string? cookie, string path = "/boxes/box-one/desktop/api/websockets")
    {
        var client = Gateway.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            if (cookie is not null) request.Headers.Cookie = $"{cookie}; selkies-pref=dark";
        };
        return client.ConnectAsync(new Uri(DesktopOrigin, path), CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        DirectorConnection.Cancel();
        if (_gateway is not null) await _gateway.DisposeAsync();
        await Box.DisposeAsync();
        _harness.Dispose();
    }
}

/// <summary>A hosted-desktop list the test fills: box id to port.</summary>
internal sealed class FakeHostedDesktops : IHostedDesktopLookup
{
    public ConcurrentDictionary<string, int> Ports { get; } = new();
    public bool TryResolvePort(string boxId, out int port) => Ports.TryGetValue(boxId, out port);
}

/// <summary>Which Director hosts which box, per account.</summary>
internal sealed class FakeBoxHosts : IBoxDesktopHosts
{
    public ConcurrentDictionary<(string Tenant, string Box), string> Hosted { get; } = new();
    public ConcurrentQueue<(TenantId Tenant, string Box)> Asked { get; } = new();

    public string? LocateDirector(TenantId tenant, string boxId)
    {
        Asked.Enqueue((tenant, boxId));
        return Hosted.TryGetValue((tenant.Value, boxId), out var directorId) ? directorId : null;
    }
}

/// <summary>The Boxes flag, per account.</summary>
internal sealed class FakeBoxesFeature : IBoxesFeature
{
    public ConcurrentBag<string> Enabled { get; } = new();
    public bool IsEnabled(TenantId tenant) => Enabled.Contains(tenant.Value);
}

/// <summary>
/// The Director's connector, aimed at the fake desktop's in-memory host instead of a real port. It records every
/// address it is handed, so a test can prove the Director only ever aimed at 127.0.0.1 and the listed port.
/// </summary>
internal sealed class RecordingConnector : IDesktopBoxConnector
{
    private readonly TestServer _box;

    public RecordingConnector(TestServer box)
    {
        _box = box;
        Http = new HttpClient(new AddressRecorder(this, box.CreateHandler()));
    }

    public ConcurrentQueue<Uri> Addresses { get; } = new();
    public HttpClient Http { get; }

    /// <summary>When set, the box refuses WebSocket connections.</summary>
    public bool RefuseSockets { get; set; }

    public async Task<WebSocket> ConnectWebSocketAsync(Uri address, IReadOnlyList<string> subProtocols, CancellationToken ct)
    {
        Addresses.Enqueue(address);
        if (RefuseSockets) throw new WebSocketException("the box refused the connection");
        var client = _box.CreateWebSocketClient();
        foreach (var protocol in subProtocols) client.SubProtocols.Add(protocol);
        return await client.ConnectAsync(address, ct);
    }

    private sealed class AddressRecorder : DelegatingHandler
    {
        private readonly RecordingConnector _owner;

        public AddressRecorder(RecordingConnector owner, HttpMessageHandler inner) : base(inner) => _owner = owner;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _owner.Addresses.Enqueue(request.RequestUri!);
            return base.SendAsync(request, cancellationToken);
        }
    }
}

/// <summary>Turns a hang into a named failure. A watchdog only: it never decides a pass.</summary>
internal static class Watchdog
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    public static Task<T> Within<T>(this Task<T> task) => task.WaitAsync(Limit);
    public static Task Within(this Task task) => task.WaitAsync(Limit);

    /// <summary>
    /// Wait for a state the relay reaches on its own with no event to await (an end freed after the browser already
    /// has its answer). It looks again each time the thread pool comes round, and only the watchdog bounds it.
    /// </summary>
    public static async Task Eventually(Func<bool> condition)
    {
        using var stop = new CancellationTokenSource();
        try
        {
            await Task.Run(async () =>
            {
                while (!condition() && !stop.IsCancellationRequested) await Task.Yield();
            }).Within();
        }
        finally
        {
            stop.Cancel();
        }
    }
}
