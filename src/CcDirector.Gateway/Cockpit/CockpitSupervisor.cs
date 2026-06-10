using System.Diagnostics;
using System.Net.Http;
using CcDirector.Core.Utilities;
using CcDirector.Setup.Engine;

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
    // Canonical Cockpit location (master spec: docs/install/INSTALLATION.md): the per-user
    // install layout's cockpit dir (%LOCALAPPDATA%\cc-director\cockpit\cc-director-cockpit.exe).
    // CC_COCKPIT_EXE overrides it.
    private static readonly string DefaultExe =
        CcDirector.Setup.Engine.InstallLayout.Default().PathFor(CcDirector.Setup.Engine.ComponentRegistry.Cockpit);
    private const string CockpitProcessName = "cc-director-cockpit";

    /// <summary>Canonical Cockpit port. Overridable with <c>CC_COCKPIT_PORT</c> via <see cref="ResolvePort"/>.</summary>
    public const int DefaultPort = 7470;

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

        return new CockpitSupervisor(enabled, exe, ResolvePort());
    }

    /// <summary>
    /// The Cockpit's port: <c>CC_COCKPIT_PORT</c> when set to a valid value, else
    /// <see cref="DefaultPort"/>. Shared by the supervisor (which launches the Cockpit on it)
    /// and the Gateway (which provisions the Tailscale Serve mapping and advertises the URL for
    /// it), so all three agree on one number.
    /// </summary>
    public static int ResolvePort()
    {
        var portEnv = Environment.GetEnvironmentVariable("CC_COCKPIT_PORT");
        if (!string.IsNullOrWhiteSpace(portEnv) && int.TryParse(portEnv, out var p) && p is > 0 and < 65536)
            return p;
        return DefaultPort;
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
        // Apply any available Cockpit update ONCE, before the first launch (the child is not running, so
        // its files are not locked). This is the silent "apply at next restart" path (D5): the Cockpit
        // moves to the latest release whenever the Gateway service (re)starts. Never blocks startup on a
        // slow/unreachable GitHub - it is time-boxed and all failures just fall through to launching the
        // build already on disk.
        await TryAutoUpdateAsync(ct);

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
                    // Capture the child's stdout/stderr (issue #199): without this the Cockpit's
                    // console output (startup errors, unhandled exceptions printed before its own
                    // file sink is up) went to an invisible console and was lost. We mirror each
                    // line into the Gateway's FileLog tagged with the child pid.
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                // The published exe has no launchSettings, so pin its URL here.
                psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{_port}";
                proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
                // BeginXxxReadLine drives the OutputDataReceived/ErrorDataReceived events on a
                // thread-pool thread, so reading the child's streams never blocks the supervise loop.
                proc.OutputDataReceived += (_, e) => LogChildLine(proc.Id, "out", e.Data);
                proc.ErrorDataReceived += (_, e) => LogChildLine(proc.Id, "err", e.Data);
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
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

    /// <summary>
    /// Time-boxed Cockpit auto-update: fetch the latest release and extract a newer Cockpit if one is
    /// available (refresh-only, pin-aware). Called once before the first launch. All failures (offline,
    /// slow GitHub, etc.) are swallowed so they can never block the Cockpit from starting; the build
    /// already on disk launches as usual. Opt out with CC_COCKPIT_AUTOUPDATE=0 (full config is phase 5).
    /// </summary>
    private static async Task TryAutoUpdateAsync(CancellationToken ct)
    {
        if (Environment.GetEnvironmentVariable("CC_COCKPIT_AUTOUPDATE") == "0")
        {
            FileLog.Write("[CockpitSupervisor] Cockpit auto-update disabled (CC_COCKPIT_AUTOUPDATE=0)");
            return;
        }
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15)); // never delay Cockpit startup on a slow/unreachable GitHub
            var source = new ReleaseSource(new HttpClient { Timeout = TimeSpan.FromSeconds(12) });
            var release = await source.FetchLatestAsync(cts.Token);
            var newVersion = await new CockpitUpdater().ApplyAsync(release, source, cts.Token);
            if (newVersion is not null)
                FileLog.Write($"[CockpitSupervisor] auto-updated Cockpit to {newVersion} before launch");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CockpitSupervisor] Cockpit update check skipped: {ex.Message}");
        }
    }

    /// <summary>
    /// Mirror one line of the managed Cockpit child's stdout/stderr into the Gateway FileLog,
    /// tagged with the child pid and stream (issue #199). A null payload is the end-of-stream
    /// signal the redirected-read events raise on exit - skip it.
    /// </summary>
    private static void LogChildLine(int pid, string stream, string? data)
    {
        if (data is null) return;
        FileLog.Write($"[CockpitSupervisor] cockpit pid={pid} {stream}: {data}");
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
