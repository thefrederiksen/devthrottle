using System.Diagnostics;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Cockpit;

/// <summary>
/// Keeps the Cockpit web app (a separate Kestrel process) running alongside the always-on
/// Gateway. In production the Gateway service launches the published Cockpit exe and restarts
/// it whenever it exits, so the UI is always available - without the Cockpit ever being a
/// service itself.
///
/// Dev mode: when <c>CC_COCKPIT_MANAGED</c> is not "1" (the default), this is inert. The
/// developer runs the Cockpit themselves (<c>dotnet run</c>) for hot reload/debugging, and the
/// Gateway does not launch one or fight for its port.
///
/// The Cockpit stays its OWN process: restarting the Cockpit never bounces the Gateway. On
/// Gateway shutdown the managed child is stopped; a fresh Gateway start re-launches it, first
/// clearing any orphan left by a prior hard crash so the port never double-binds.
/// </summary>
public sealed class CockpitSupervisor : IDisposable
{
    // Canonical Cockpit location (master spec: docs/install/INSTALLATION.md):
    // %ProgramFiles%\CC Director\cockpit\cc-director-cockpit.exe. CC_COCKPIT_EXE overrides it.
    private static readonly string DefaultExe = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "CC Director", "cockpit", "cc-director-cockpit.exe");
    private const string CockpitProcessName = "cc-director-cockpit";
    private const int DefaultPort = 7470;

    private static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HealthyRunThreshold = TimeSpan.FromSeconds(10);

    private readonly bool _enabled;
    private readonly string _exePath;
    private readonly int _port;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Process? _child;
    private bool _disposed;

    public CockpitSupervisor(bool enabled, string exePath, int port)
    {
        _enabled = enabled;
        _exePath = exePath;
        _port = port;
    }

    /// <summary>
    /// Read config from the environment. <c>CC_COCKPIT_MANAGED=1</c> turns supervision ON (set by
    /// the service installer); anything else - including unset, the dev default - leaves it OFF.
    /// <c>CC_COCKPIT_EXE</c> overrides the Cockpit exe path; <c>CC_COCKPIT_PORT</c> the port.
    /// </summary>
    public static CockpitSupervisor FromEnvironment()
    {
        var managed = Environment.GetEnvironmentVariable("CC_COCKPIT_MANAGED");
        var enabled = managed == "1" || string.Equals(managed, "true", StringComparison.OrdinalIgnoreCase);

        var exe = Environment.GetEnvironmentVariable("CC_COCKPIT_EXE");
        if (string.IsNullOrWhiteSpace(exe)) exe = DefaultExe;

        var port = DefaultPort;
        var portEnv = Environment.GetEnvironmentVariable("CC_COCKPIT_PORT");
        if (!string.IsNullOrWhiteSpace(portEnv) && int.TryParse(portEnv, out var p) && p is > 0 and < 65536)
            port = p;

        return new CockpitSupervisor(enabled, exe, port);
    }

    public void Start()
    {
        if (!_enabled)
        {
            FileLog.Write("[CockpitSupervisor] disabled (CC_COCKPIT_MANAGED != 1); Cockpit is dev-controlled");
            return;
        }
        if (!File.Exists(_exePath))
        {
            FileLog.Write($"[CockpitSupervisor] enabled but Cockpit exe not found at {_exePath}; not launching");
            return;
        }
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => SuperviseAsync(_cts.Token));
        FileLog.Write($"[CockpitSupervisor] Start: managing {_exePath} on port {_port}");
    }

    private async Task SuperviseAsync(CancellationToken ct)
    {
        var backoff = BaseBackoff;

        while (!ct.IsCancellationRequested)
        {
            KillOrphans();   // clear a Cockpit left by a prior hard crash so the port is free

            Process proc;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _exePath,
                    WorkingDirectory = Path.GetDirectoryName(_exePath) ?? Environment.CurrentDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                // The published exe has no launchSettings, so pin its URL here.
                psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{_port}";
                proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[CockpitSupervisor] launch failed: {ex.Message}; retry in {backoff.TotalSeconds:F0}s");
                if (!await DelayAsync(backoff, ct)) break;
                backoff = Min(backoff + backoff, MaxBackoff);
                continue;
            }

            _child = proc;
            var launchedAt = DateTime.UtcNow;
            FileLog.Write($"[CockpitSupervisor] launched Cockpit pid={proc.Id} on port {_port}");

            try { await proc.WaitForExitAsync(ct); }
            catch (OperationCanceledException) { break; }
            if (ct.IsCancellationRequested) break;

            var ranFor = DateTime.UtcNow - launchedAt;
            FileLog.Write($"[CockpitSupervisor] Cockpit exited code={SafeExitCode(proc)} after {ranFor.TotalSeconds:F0}s; restarting");
            // A healthy run resets the backoff; a fast crash grows it to avoid a tight loop.
            backoff = ranFor > HealthyRunThreshold ? BaseBackoff : Min(backoff + backoff, MaxBackoff);
            if (!await DelayAsync(backoff, ct)) break;
        }

        StopChild();
    }

    private static void KillOrphans()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(CockpitProcessName))
            {
                try { p.Kill(entireProcessTree: true); p.WaitForExit(2000); }
                catch { /* best effort */ }
                finally { p.Dispose(); }
            }
        }
        catch (Exception ex) { FileLog.Write($"[CockpitSupervisor] KillOrphans error: {ex.Message}"); }
    }

    private void StopChild()
    {
        var p = _child;
        _child = null;
        if (p is null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
        finally { p.Dispose(); }
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return -1; }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static async Task<bool> DelayAsync(TimeSpan d, CancellationToken ct)
    {
        try { await Task.Delay(d, ct); return true; }
        catch (OperationCanceledException) { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts?.Cancel(); } catch { /* already cancelled */ }
        try { _loop?.Wait(TimeSpan.FromSeconds(5)); } catch { /* loop unwinding */ }
        StopChild();
        _cts?.Dispose();
    }
}
