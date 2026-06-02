using CcDirector.Core.Utilities;
using CcDirector.Gateway.Cockpit;
using CcDirector.Setup.Engine;
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

        // Periodic machine-tier auto-update: check for a newer Gateway and, if found, launch the
        // detached self-update helper (stop -> swap -> start -> health -> auto-rollback). A Gateway
        // self-update restarts the service, so the Cockpit picks up its update on relaunch too. Runs
        // alongside the host; failures only log.
        _ = RunUpdateLoopAsync(stoppingToken);

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

    private static async Task RunUpdateLoopAsync(CancellationToken ct)
    {
        var layout = InstallLayout.Default();
        // Let the service settle before the first check; never compete with startup.
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            var cfg = AutoUpdateConfig.Load(layout);
            if (cfg.Enabled && OperatingSystem.IsWindows())
            {
                try
                {
                    var source = new ReleaseSource();
                    var release = await source.FetchLatestAsync(ct);
                    var version = await new GatewayUpdater(layout).CheckStageAndLaunchAsync(release, source, ct);
                    if (version is not null)
                    {
                        FileLog.Write($"[GatewayWorker] launched Gateway self-update to {version}; service will restart");
                        return; // the detached helper will stop/swap/start this service
                    }
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[GatewayWorker] update check failed: {ex.Message}");
                }
            }
            try { await Task.Delay(cfg.Enabled ? cfg.Interval : TimeSpan.FromHours(1), ct); }
            catch (OperationCanceledException) { break; }
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
