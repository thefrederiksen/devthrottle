using System.Diagnostics;
using System.Net.Http;
using CcDirector.Core.Utilities;
using CcDirector.Setup.Engine;

namespace CcDirector.Launcher;

/// <summary>
/// Supervises the installed CC Director tray app.
///
/// Resolves the Director exe via <see cref="InstallLayout"/> (the installed path at
/// %LOCALAPPDATA%/cc-director/app/cc-director.exe). Provides start / stop / restart
/// operations with FileLog audit trail.
///
/// Stop strategy: POST /shutdown to the Director's Control API (graceful). The Director
/// discovers its own port via the instances json files; we probe for a running instance
/// by reading the instance registration files in the known directory.
/// </summary>
public sealed class DirectorSupervisor
{
    private readonly InstallLayout _layout;
    private readonly HttpClient _http;

    public DirectorSupervisor() : this(InstallLayout.Default()) { }

    public DirectorSupervisor(InstallLayout layout)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    /// <summary>
    /// The installed Director exe path (per InstallLayout). This is what the supervisor
    /// manages. To launch arbitrary slot builds, use LaunchService directly with an
    /// explicit path.
    /// </summary>
    public string DirectorExePath => _layout.PathFor(ComponentRegistry.Director);

    /// <summary>Whether the installed Director exe exists on disk.</summary>
    public bool DirectorExeExists => File.Exists(DirectorExePath);

    /// <summary>
    /// Whether the installed Director appears to be running (a process with the resolved
    /// exe path exists). Best-effort: does not prove the Control API is healthy.
    /// </summary>
    public bool IsRunning => FindDirectorProcess() is not null;

    /// <summary>
    /// Start the installed Director if it is not already running.
    /// Uses UseShellExecute=true for clean parentage (no ConPty inheritance).
    /// </summary>
    public void Start()
    {
        FileLog.Write($"[DirectorSupervisor] Start: exe={DirectorExePath}");

        if (!File.Exists(DirectorExePath))
            throw new FileNotFoundException($"Director exe not found: {DirectorExePath}", DirectorExePath);

        if (FindDirectorProcess() is { } running)
        {
            FileLog.Write($"[DirectorSupervisor] Start: Director already running (pid={running.Id}); skipping");
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = DirectorExePath,
            WorkingDirectory = Path.GetDirectoryName(DirectorExePath) ?? "",
            UseShellExecute = true,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Process.Start returned null for: {DirectorExePath}");

        FileLog.Write($"[DirectorSupervisor] Start: launched Director pid={proc.Id}");
    }

    /// <summary>
    /// Stop the running Director gracefully via POST /shutdown to its Control API.
    /// Falls back to process kill only when the Control API is unreachable.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        FileLog.Write("[DirectorSupervisor] StopAsync");

        var proc = FindDirectorProcess();
        if (proc is null)
        {
            FileLog.Write("[DirectorSupervisor] StopAsync: Director not running");
            return;
        }

        // Try graceful shutdown via Control API.
        var port = FindDirectorPort();
        if (port > 0)
        {
            try
            {
                FileLog.Write($"[DirectorSupervisor] StopAsync: POST http://127.0.0.1:{port}/shutdown");
                using var resp = await _http.PostAsync($"http://127.0.0.1:{port}/shutdown", content: null, ct);
                FileLog.Write($"[DirectorSupervisor] StopAsync: /shutdown -> {(int)resp.StatusCode}");
                // Wait for the process to exit after the graceful shutdown.
                await WaitForExitAsync(proc, TimeSpan.FromSeconds(10), ct);
                return;
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DirectorSupervisor] StopAsync: /shutdown failed ({ex.Message}); falling back to process stop");
            }
        }

        // Fallback: stop the process directly.
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: false);
                FileLog.Write($"[DirectorSupervisor] StopAsync: killed pid={proc.Id}");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorSupervisor] StopAsync: kill failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Restart the Director: stop gracefully, wait, then start fresh.
    /// A staged update is applied automatically by the Director on the next startup.
    /// </summary>
    public async Task RestartAsync(CancellationToken ct = default)
    {
        FileLog.Write("[DirectorSupervisor] RestartAsync");
        await StopAsync(ct);
        // Brief pause to let file locks release before relaunching.
        await Task.Delay(500, ct);
        Start();
        FileLog.Write("[DirectorSupervisor] RestartAsync: Director restarted");
    }

    /// <summary>Find a running Director process whose image path matches the installed exe.</summary>
    private Process? FindDirectorProcess()
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName("cc-director"))
            {
                try
                {
                    var exePath = proc.MainModule?.FileName ?? "";
                    if (string.Equals(exePath, DirectorExePath, StringComparison.OrdinalIgnoreCase))
                        return proc;
                }
                catch
                {
                    // MainModule access may fail for elevated processes; skip.
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorSupervisor] FindDirectorProcess error: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Find the Director's Control API port by reading instance registration files.
    /// Returns 0 if not found.
    /// </summary>
    private int FindDirectorPort()
    {
        try
        {
            var instancesDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "cc-director", "instances");

            if (!Directory.Exists(instancesDir)) return 0;

            foreach (var file in Directory.GetFiles(instancesDir, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    // Parse port field from the instance JSON: {"port":7879,...}
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("port", out var portEl) && portEl.TryGetInt32(out var p))
                        return p;
                }
                catch
                {
                    // Skip malformed files.
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorSupervisor] FindDirectorPort error: {ex.Message}");
        }
        return 0;
    }

    private static async Task WaitForExitAsync(Process proc, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            FileLog.Write("[DirectorSupervisor] WaitForExitAsync: timed out waiting for Director to exit");
        }
    }
}
