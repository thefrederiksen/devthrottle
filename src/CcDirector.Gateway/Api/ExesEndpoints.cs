using System.Diagnostics;
using System.Text.RegularExpressions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Discovery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Maps the <c>/exes</c> management surface: a local-machine developer page that
/// lists the Director executables physically running on the Gateway's own
/// computer (with their sessions nested underneath) and manages the local build
/// "slots" 1-4 produced by <c>scripts/local-build-avalonia.ps1</c>.
///
/// Routes:
///   GET    /exes                         local directors + slot status (JSON)
///   DELETE /exes/slots/{n}               delete local_builds/cc-director{n}.exe
///   POST   /exes/slots/{n}/build-start   build slot n, then launch it
///
/// Killing a running Director reuses the existing <c>DELETE /directors/{id}</c>
/// (graceful shutdown, then force-kill the process tree), so it is not duplicated
/// here. Everything below operates only on the Gateway's own machine - the slot
/// files and processes live on local disk, so these routes are meaningless for a
/// remote Director and the page only ever shows machine-local entries.
/// </summary>
internal static class ExesEndpoints
{
    private static readonly Regex SlotFromExe =
        new(@"cc-director(\d+)\.exe$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static void Map(IEndpointRouteBuilder app, DirectorRegistry registry, DirectorEndpointClient client)
    {
        // ----- list local directors + slot status (JSON; /exes itself is the HTML page) -----
        app.MapGet("/exes/list", async (HttpContext ctx) =>
        {
            FileLog.Write("[ExesEndpoints] GET /exes/list");
            try
            {
                var machine = Environment.MachineName;
                var repoRoot = ResolveRepoRoot();

                var local = registry.ListDirectors()
                    .Where(d => string.Equals(d.MachineName, machine, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(d => d.StartedAt)
                    .ToList();

                // Fan out to each local Director for its sessions, in parallel.
                var directorTasks = local.Select(async d =>
                {
                    var exePath = TryGetExePath(d.Pid);
                    var (sessions, error) = await client.ListSessionsWithStatusAsync(d.ControlEndpoint, false);
                    return new
                    {
                        directorId = d.DirectorId,
                        pid = d.Pid,
                        slot = SlotOf(exePath),
                        exePath = exePath ?? "",
                        controlEndpoint = d.ControlEndpoint,
                        // Routable base URL (loopback locally, public host via Tailscale). The
                        // Director serves its UI at the root, so this IS the Director page.
                        directorUrl = GatewayEndpoints.DeriveDirectorBaseUrl(ctx, d),
                        version = d.Version,
                        startedAt = d.StartedAt,
                        source = d.Source,
                        sessionError = error,
                        sessions = (sessions ?? new()).Select(s => new
                        {
                            sessionId = s.SessionId,
                            name = s.Name,
                            agent = s.Agent,
                            activityState = s.ActivityState,
                            statusColor = s.StatusColor,
                            repoPath = s.RepoPath,
                        }).ToList(),
                    };
                });
                var directors = await Task.WhenAll(directorTasks);

                // Slot status (1-4). A slot is "running" if any local Director's exe
                // resolves to that slot file.
                var runningByPath = directors
                    .Where(d => !string.IsNullOrEmpty(d.exePath))
                    .ToDictionary(d => Path.GetFullPath(d.exePath), d => d, StringComparer.OrdinalIgnoreCase);

                var slots = new List<object>();
                for (int n = 1; n <= 4; n++)
                {
                    var path = repoRoot is null ? null : SlotExePath(repoRoot, n);
                    var exists = path is not null && File.Exists(path);
                    object? running = null;
                    if (exists && runningByPath.TryGetValue(Path.GetFullPath(path!), out var d))
                        running = new { d.pid, d.directorId };

                    slots.Add(new
                    {
                        slot = n,
                        exists,
                        exePath = path ?? "",
                        lastBuiltUtc = exists ? File.GetLastWriteTimeUtc(path!) : (DateTime?)null,
                        sizeBytes = exists ? new FileInfo(path!).Length : 0L,
                        running,
                    });
                }

                return Results.Json(new
                {
                    machineName = machine,
                    repoRoot = repoRoot ?? "",
                    directors,
                    slots,
                });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ExesEndpoints] GET /exes FAILED: {ex.Message}");
                return Results.Problem("failed to enumerate exes: " + ex.Message, statusCode: 500);
            }
        });

        // ----- delete a slot's built exe -----
        app.MapDelete("/exes/slots/{n}", (int n) =>
        {
            FileLog.Write($"[ExesEndpoints] DELETE /exes/slots/{n}");
            try
            {
                if (n < 1 || n > 4)
                    return Results.BadRequest(new { error = "slot must be 1-4" });

                var repoRoot = ResolveRepoRoot();
                if (repoRoot is null)
                    return Results.Problem(RepoNotFoundMessage(), statusCode: 500);

                var path = SlotExePath(repoRoot, n);
                if (!File.Exists(path))
                    return Results.NotFound(new { error = $"slot {n} is not built (no {Path.GetFileName(path)})" });

                // Refuse to delete a slot that is currently running - the file would be
                // locked anyway, and a clear message beats an IO exception.
                var runningPid = RunningPidForExe(registry, path);
                if (runningPid is not null)
                    return Results.Conflict(new { error = $"slot {n} is running (PID {runningPid}). Kill it first." });

                File.Delete(path);
                return Results.Json(new { deleted = true, slot = n });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ExesEndpoints] DELETE /exes/slots/{n} FAILED: {ex.Message}");
                return Results.Problem("delete failed: " + ex.Message, statusCode: 500);
            }
        });

        // ----- build a slot then launch it -----
        app.MapPost("/exes/slots/{n}/build-start", async (int n) =>
        {
            FileLog.Write($"[ExesEndpoints] POST /exes/slots/{n}/build-start");
            try
            {
                if (n < 1 || n > 4)
                    return Results.BadRequest(new { error = "slot must be 1-4" });

                var repoRoot = ResolveRepoRoot();
                if (repoRoot is null)
                    return Results.Problem(RepoNotFoundMessage(), statusCode: 500);

                var script = Path.Combine(repoRoot, "scripts", "local-build-avalonia.ps1");
                var exePath = SlotExePath(repoRoot, n);

                // A running slot locks its exe; the build's copy step would fail. Stop early
                // with a clear message instead of a half-built slot.
                var runningPid = RunningPidForExe(registry, exePath);
                if (runningPid is not null)
                    return Results.Conflict(new { error = $"slot {n} is running (PID {runningPid}). Kill it before rebuilding." });

                var (exit, output) = await RunBuildAsync(repoRoot, script, n);
                if (exit != 0)
                {
                    FileLog.Write($"[ExesEndpoints] build slot {n} FAILED: exit={exit}");
                    return Results.Problem("build failed (exit " + exit + "):\n" + Tail(output, 4000), statusCode: 500);
                }
                if (!File.Exists(exePath))
                    return Results.Problem("build reported success but " + Path.GetFileName(exePath) + " was not produced.", statusCode: 500);

                // Launch the GUI app detached via the shell so it does not inherit this
                // process's console - that keeps any claude.exe sessions it spawns clean.
                var launch = new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = Path.GetDirectoryName(exePath)!,
                    UseShellExecute = true,
                };
                var proc = Process.Start(launch);
                FileLog.Write($"[ExesEndpoints] slot {n} built and launched: pid={proc?.Id}");

                return Results.Json(new
                {
                    built = true,
                    started = true,
                    slot = n,
                    pid = proc?.Id ?? 0,
                    buildTail = Tail(output, 2000),
                });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ExesEndpoints] build-start slot {n} FAILED: {ex.Message}");
                return Results.Problem("build-start failed: " + ex.Message, statusCode: 500);
            }
        });
    }

    private static async Task<(int exit, string output)> RunBuildAsync(string repoRoot, string script, int slot)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add("-Slot");
        psi.ArgumentList.Add(slot.ToString());

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("could not start powershell for build");

        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException("build timed out after 6 minutes");
        }

        var combined = (await stdoutTask) + (await stderrTask);
        return (p.ExitCode, combined);
    }

    /// <summary>Walks up from the running assembly until it finds the repo root
    /// (the directory holding both <c>scripts/local-build-avalonia.ps1</c> and
    /// <c>local_builds/</c>). Returns null when the Gateway is not running from
    /// inside the repo - the page surfaces that truthfully rather than guessing.</summary>
    private static string? ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var script = Path.Combine(dir.FullName, "scripts", "local-build-avalonia.ps1");
            var builds = Path.Combine(dir.FullName, "local_builds");
            if (File.Exists(script) && Directory.Exists(builds))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static string RepoNotFoundMessage() =>
        "repo root not found: the Gateway is not running from inside the cc-director repo, " +
        "so the slot build scripts and local_builds are unavailable. Run the Gateway from a repo build to use slot management.";

    private static string SlotExePath(string repoRoot, int n) =>
        Path.Combine(repoRoot, "local_builds", $"cc-director{n}.exe");

    private static int? SlotOf(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        var m = SlotFromExe.Match(exePath);
        return m.Success ? int.Parse(m.Groups[1].Value) : (int?)null;
    }

    private static string? TryGetExePath(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.MainModule?.FileName;
        }
        catch
        {
            // Process gone, or access denied reading another process's module - the path
            // is simply unknown for this entry.
            return null;
        }
    }

    /// <summary>PID of a local Director whose exe resolves to <paramref name="exePath"/>, or null.</summary>
    private static int? RunningPidForExe(DirectorRegistry registry, string exePath)
    {
        var target = Path.GetFullPath(exePath);
        var machine = Environment.MachineName;
        foreach (var d in registry.ListDirectors())
        {
            if (!string.Equals(d.MachineName, machine, StringComparison.OrdinalIgnoreCase)) continue;
            var p = TryGetExePath(d.Pid);
            if (p is not null && string.Equals(Path.GetFullPath(p), target, StringComparison.OrdinalIgnoreCase))
                return d.Pid;
        }
        return null;
    }

    private static string Tail(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
        return s.Substring(s.Length - max);
    }
}
