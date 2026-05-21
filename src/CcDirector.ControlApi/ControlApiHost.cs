using System.Net;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Supervisor;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CcDirector.ControlApi;

/// <summary>
/// Hosts the Director's HTTP Control API on a stable, predictable port so the
/// URL is bookmarkable across restarts and reachable from Tailscale clients.
///
/// Binding:
///   - Listens on 0.0.0.0 (all interfaces) so loopback + LAN + Tailscale all work.
///   - Auth (cookie or Bearer token) is required for every state-changing or
///     potentially-sensitive request. Token lives in gateway-token.txt.
///
/// Lifecycle:
///   - StartAsync() -> picks port via PortAllocator, starts Kestrel, writes instances/{guid}.json
///   - StopAsync()  -> deletes registration file, releases port state, stops Kestrel
/// </summary>
public sealed class ControlApiHost : IAsyncDisposable
{
    private readonly SessionManager _sessionManager;
    private readonly RepositoryRegistry? _repositoryRegistry;
    private readonly string _version;
    private readonly Func<Task> _requestShutdownAsync;
    private readonly bool _useEphemeralPort;
    private readonly bool _authEnabled;

    public string DirectorId { get; }
    public int Port { get; private set; }
    public bool AuthEnabled => _authEnabled;

    private WebApplication? _app;
    private InstanceRegistration? _registration;
    private GatewayClient? _gatewayClient;
    private TurnSummaryCache? _turnSummaryCache;
    private SessionStatusSupervisor? _statusSupervisor;
    private bool _stopped;

    /// <summary>
    /// Construct a Director Control API host.
    /// </summary>
    /// <param name="useEphemeralPort">
    /// If true, Kestrel picks a free port and we bind only to loopback (intended for tests).
    /// If false (production), PortAllocator picks a stable port in [7879..7898] and we bind to 0.0.0.0.
    /// </param>
    /// <param name="authEnabled">
    /// If true, bearer-token or cookie auth is required for all routes except /healthz/login/logout.
    /// If false (default), the Director is completely open. The Tailscale tailnet is the trust boundary.
    /// </param>
    public ControlApiHost(SessionManager sessionManager, string version, Func<Task> requestShutdownAsync, bool useEphemeralPort = false, bool authEnabled = false, RepositoryRegistry? repositoryRegistry = null, string? directorId = null)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _version = version ?? "0.0.0";
        _requestShutdownAsync = requestShutdownAsync ?? throw new ArgumentNullException(nameof(requestShutdownAsync));
        _useEphemeralPort = useEphemeralPort;
        _authEnabled = authEnabled;
        _repositoryRegistry = repositoryRegistry;

        // Production: persisted id (same across restarts so the Gateway recognizes us).
        // Tests: inject a fresh id per fixture so parallel runs don't collide on the
        // single instances/{id}.json file.
        DirectorId = directorId ?? (useEphemeralPort
            ? Guid.NewGuid().ToString()
            : DirectorIdStore.LoadOrCreate());
    }

    /// <summary>Start Kestrel and write the instance registration file. Returns the chosen port.</summary>
    public async Task<int> StartAsync()
    {
        FileLog.Write($"[ControlApiHost] StartAsync: directorId={DirectorId}, ephemeral={_useEphemeralPort}");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = "CcDirector.ControlApi",
        });
        builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");

        if (_useEphemeralPort)
        {
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        }
        else
        {
            Port = PortAllocator.Allocate(DirectorId);
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Any, Port));
        }

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddRoutingCore();

        _app = builder.Build();

        // Global exception envelope so the browser sees a readable 500 instead of a hung connection.
        _app.Use(async (ctx, next) =>
        {
            try { await next(); }
            catch (Exception ex)
            {
                FileLog.Write($"[ControlApiHost] pipeline exception: {ex}");
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    ctx.Response.ContentType = "text/plain; charset=utf-8";
                    await ctx.Response.WriteAsync($"{ex.GetType().Name}: {ex.Message}");
                }
            }
        });

        if (_authEnabled)
        {
            var token = DirectorAuth.LoadOrCreateToken();
            _app.Use((ctx, next) => DirectorAuth.Run(ctx, token, next));
        }

        // Enable WebSocket support for /dictate and any future streaming endpoints.
        _app.UseWebSockets(new Microsoft.AspNetCore.Builder.WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        });
        _app.UseRouting();

        // Phase 3: the SessionStatusSupervisor is the sole writer of each Session's
        // StatusColor. Must start BEFORE TurnSummaryCache so brand-new sessions are
        // already "green/session created" by the time anything else observes them.
        _statusSupervisor = new SessionStatusSupervisor(_sessionManager);
        _statusSupervisor.Start();

        // Start the Supervisor's per-turn summary cache before mapping endpoints so
        // /sessions/{sid}/turn-summaries returns whatever is already cached.  Hooks
        // OnSessionCreated + per-session OnTurnCompleted.  See Phase 2 of
        // docs/goals/GOAL_CC_DIRECTOR_SUPERVISOR.md.
        _turnSummaryCache = new TurnSummaryCache(_sessionManager, _sessionManager.Options, _statusSupervisor);
        _turnSummaryCache.Start();

        ControlEndpoints.Map(_app, _sessionManager, DirectorId, _version, _requestShutdownAsync, _authEnabled, _repositoryRegistry, _turnSummaryCache);
        DictationEndpoint.Map(_app, _sessionManager.Options);

        await _app.StartAsync();

        if (_useEphemeralPort)
        {
            Port = ReadAssignedPort(_app)
                ?? throw new InvalidOperationException("Kestrel started but did not expose a bound address.");
        }
        FileLog.Write($"[ControlApiHost] Kestrel listening on " + (_useEphemeralPort ? $"http://127.0.0.1:{Port}" : $"http://0.0.0.0:{Port}"));

        _registration = new InstanceRegistration(DirectorId, Port, _version);
        _registration.Register();

        // Phase 1: if gateway.url is configured, register with the Gateway over HTTP and
        // start the heartbeat. Disabled (no-op) when local-only.
        var gatewayConfig = Core.Configuration.GatewayConfig.Load();
        _gatewayClient = new GatewayClient(gatewayConfig, DirectorId, Port, _version);
        _gatewayClient.Start();

        return Port;
    }

    private static int? ReadAssignedPort(WebApplication app)
    {
        var server = app.Services.GetService<IServer>();
        var addresses = server?.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is null) return null;
        foreach (var addr in addresses)
            if (Uri.TryCreate(addr, UriKind.Absolute, out var uri))
                return uri.Port;
        return null;
    }

    /// <summary>Stop Kestrel and delete the registration file. Safe to call multiple times.</summary>
    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;
        FileLog.Write($"[ControlApiHost] StopAsync");

        if (_gatewayClient is not null)
        {
            try { await _gatewayClient.StopAsync(); }
            catch (Exception ex) { FileLog.Write($"[ControlApiHost] GatewayClient.StopAsync error: {ex.Message}"); }
            _gatewayClient.Dispose();
            _gatewayClient = null;
        }

        _turnSummaryCache?.Dispose();
        _turnSummaryCache = null;
        _statusSupervisor?.Dispose();
        _statusSupervisor = null;

        _registration?.Unregister();

        // Release the persisted port file only if we used a real allocated port
        if (!_useEphemeralPort && Port > 0)
        {
            try { PortAllocator.Release(DirectorId); } catch { }
        }

        if (_app is not null)
        {
            try { await _app.StopAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception ex) { FileLog.Write($"[ControlApiHost] StopAsync error: {ex.Message}"); }
            await _app.DisposeAsync();
            _app = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
