using CcDirector.Core.Utilities;
using CcDirector.Gateway.Cockpit;
using Microsoft.Extensions.Hosting;

namespace CcDirector.Gateway;

/// <summary>
/// Hosts the Gateway's Kestrel host and the Cockpit supervisor inside the generic host so the
/// process integrates with the Windows Service Control Manager (start/stop) when launched as the
/// <c>cc-gateway-service</c> Windows service. When run from a console (dev <c>dotnet run</c>), the
/// generic host's console lifetime drives the same Start/Stop, so behavior is identical.
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
        await _host.StartAsync();

        // Supervise the Cockpit UI (production only). Inert unless CC_COCKPIT_MANAGED=1, which the
        // service installer sets; in dev the developer runs the Cockpit themselves.
        _cockpit = CockpitSupervisor.FromEnvironment();
        _cockpit.Start();

        FileLog.Write($"[GatewayWorker] running on http://127.0.0.1:{_host.Port}");

        // Stay alive until the host signals shutdown (SCM stop, Ctrl+C, or ProcessExit).
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
