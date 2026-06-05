using CcDirector.Core.Utilities;
using CcDirector.Gateway.Cockpit;
using Microsoft.Extensions.Hosting;

namespace CcDirector.Gateway;

/// <summary>
/// Hosts the Gateway's Kestrel host (plus the env-gated Cockpit supervisor) inside the generic
/// host for the DEV console loop (<c>dotnet run</c>, Ctrl+C to stop). The shipped Gateway is the
/// tray app (CcDirector.GatewayApp), which owns Cockpit supervision and self-update in managed
/// mode; this host deliberately has neither.
/// </summary>
public sealed class GatewayWorker : BackgroundService
{
    private readonly int _port;
    private GatewayHost? _host;
    private CockpitSupervisor? _cockpit;

    public GatewayWorker(int port)
    {
        _port = port;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        FileLog.Write($"[GatewayWorker] ExecuteAsync: port={_port}");

        _host = new GatewayHost(_port);
        // /shutdown support so the self-update flow is testable against a dev console gateway.
        _host.OnShutdownRequested = () =>
        {
            FileLog.Write("[GatewayWorker] shutdown requested via /shutdown");
            _ = StopAsync(CancellationToken.None).ContinueWith(_ => Environment.Exit(0));
        };
        await _host.StartAsync();

        // Inert unless CC_COCKPIT_MANAGED=1 is set explicitly; in dev the developer runs the
        // Cockpit themselves (dotnet run) for hot reload/debugging.
        _cockpit = CockpitSupervisor.FromEnvironment();
        _cockpit.Start();

        FileLog.Write($"[GatewayWorker] running on http://127.0.0.1:{_host.Port}");

        // Stay alive until the host signals shutdown (Ctrl+C or ProcessExit).
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        FileLog.Write("[GatewayWorker] StopAsync");
        try
        {
            _cockpit?.Dispose();
            if (_host is not null)
                await _host.StopAsync();
        }
        finally
        {
            await base.StopAsync(cancellationToken);
        }
    }
}
