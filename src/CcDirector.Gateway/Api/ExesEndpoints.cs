using System.Diagnostics;
using System.Text.RegularExpressions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tenancy;
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
///   GET    /exes/list                    local directors + slot status (JSON)
///   DELETE /exes/slots/{n}               delete local_builds/cc-director{n}.exe
///   POST   /exes/slots/{n}/build-start   build slot n, then launch it
///
/// Killing a running Director reuses the existing <c>DELETE /directors/{id}</c>
/// (graceful shutdown, then force-kill the process tree), so it is not duplicated
/// here. Everything below operates only on the Gateway's own machine - the slot
/// files and processes live on local disk, so these routes are meaningless for a
/// remote Director and the page only ever shows machine-local entries.
///
/// DENIED IN WHOLE ON HOSTED. This surface is a machine-local developer/launcher CONTROL PLANE for the
/// Gateway's OWN host box, and it carries no tenant. GET /exes/list substitutes <see cref="TenantId.Local"/>
/// and enumerates the OS processes running on the shared host; DELETE /exes/slots/{n} deletes a
/// PROCESS-GLOBAL slot executable off the host's disk; POST /exes/slots/{n}/build-start shells out to a
/// PowerShell build and LAUNCHES a process on the shared Gateway host. OS-gating is not tenant isolation: on a
/// Windows hosted deployment the whole surface maps behind only the host-wide auth gate, which admits any
/// enrolled device key from any account - so one authenticated tenant could read the host's process roster,
/// delete a slot another tenant's build expects, or start a build that launches a shared process on the host.
/// None of these has a per-tenant meaning on shared infrastructure, so the WHOLE group is refused on hosted
/// rather than any single route being guarded - the read, the deletion and the process launch are all equally
/// wrong here, and a route-by-route guard rots (the next /exes route added would be open by default).
///
/// HOW THE DENY IS EXPRESSED - THE SHARED REFUSAL PRIMITIVE, NOT A BESPOKE CHECK. The group is denied through
/// <see cref="HostedRouteDeny.ExclusiveGroup"/>, the ONE hosted-refusal boundary every deny family on this
/// Gateway adopts (the recording-ingest group in <see cref="RecordingEndpoints"/> and the key-vault group in
/// <see cref="VaultEndpoints"/> are the reference adoptions). On hosted the handlers are NEVER MAPPED; one
/// verb-less catch-all refusal claims everything under <c>/exes</c> (plus a root refusal at the prefix
/// itself), so EVERY request shape meets the refusal - a valid body, a malformed body, a wrong media type, a
/// verb the group never mapped, and a route added LATER. The exclusivity claim is CHECKED at startup by
/// <see cref="HostedRefusalRouteSpace.ValidateBeforeStart"/>: the Gateway refuses to start if any live route
/// serves beneath <c>/exes</c> (the single-page-app fallback is a global <c>{*path}</c>, not a route under
/// this prefix, so it does not compete).
///
/// SELF-HOST IS COMPLETELY UNCHANGED - the owner's single-owner dev box lists its Directors, deletes its slots
/// and builds them exactly as before, and that is the control. Off hosted the primitive maps the real handlers
/// on the group exactly as an unguarded builder would and creates no refusal at all.
///
/// UN-DENY DEBT: there is nothing to partition here - the surface controls the Gateway's OWN host machine (its
/// OS processes, its <c>local_builds</c> slot files, its build script) and has no per-tenant meaning on shared
/// infrastructure. It exists only for a single-owner self-host dev box, so on hosted it stays denied; this is
/// recorded on the payload's unDenyInstruction so it travels with the deny.
/// </summary>
internal static class ExesEndpoints
{
    private static readonly Regex SlotFromExe =
        new(@"cc-director(\d+)\.exe$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The exclusive prefix the exe/slot developer surface owns outright on hosted.</summary>
    internal const string Prefix = "/exes";

    /// <summary>The single error string the hosted refusal serves. Held here so a test can assert against the
    /// exact string that is served rather than a copy that could drift.</summary>
    internal const string RefusalMessage = "the developer exe and slot management surface is not available on the hosted gateway";

    /// <summary>
    /// The hosted refusal payload for the whole exe/slot group. Validated on construction, so a blank field
    /// fails the Gateway at startup rather than serving a refusal a caller cannot act on. 404 rather than 403:
    /// on hosted this surface does not exist as a concept, so "not here" is the truthful answer; 403 would
    /// imply some credential could reach it, and none can.
    /// </summary>
    private static HostedDenial Denial() => new(
        family: "exes-slots",
        message: RefusalMessage,
        reason: "the /exes surface is a machine-local developer/launcher control plane for the Gateway's own host: " +
                "it enumerates the OS processes running on the box (substituting TenantId.Local), deletes process-global " +
                "slot executables off local disk, and shells out to a PowerShell build that launches a process on the " +
                "shared host - none of which carries a tenant, so behind the host-wide auth gate one authenticated tenant " +
                "could read the host roster, delete a slot another tenant's build expects, or launch a shared process",
        unDenyInstruction: "do NOT lift this deny by partitioning: this surface controls the Gateway's OWN host machine " +
                "(its OS processes, its local_builds slot files, its build script) and has no per-tenant meaning on shared " +
                "infrastructure - it exists only for a single-owner self-host dev box, so it must stay denied on hosted",
        statusCode: StatusCodes.Status404NotFound);

    /// <summary>
    /// Maps the exe/slot developer routes and RETURNS the denied group they were mapped through.
    ///
    /// The routes are mapped through the group HANDLE (<see cref="HostedDenyGroup"/>), never through the
    /// ungrouped builder: the handle is obtainable only from <see cref="HostedRouteDeny.ExclusiveGroup"/>, so
    /// a route mapped around the refusal is not expressible in <see cref="MapRoutes"/> without changing its
    /// signature. On hosted the handle DISCARDS each handler (the exclusive catch-all already refuses the
    /// path); off hosted it maps each handler as an unguarded builder would.
    /// </summary>
    public static HostedDenyGroup Map(IEndpointRouteBuilder outer, bool hosted, DirectorRegistry registry,
        PushedSessionStore? pushedSessions = null, TimeSpan? streamStaleAfter = null,
        Snooze.SnoozeRegistry? snoozeRegistry = null)
    {
        FileLog.Write($"[ExesEndpoints] mapping {Prefix} developer exe/slot routes; hosted={hosted} - on hosted the whole group is refused via the shared refusal primitive");

        var group = HostedRouteDeny.ExclusiveGroup(outer, Prefix, Denial(), hosted);
        MapRoutes(group, registry, pushedSessions, streamStaleAfter, snoozeRegistry);
        return group;
    }

    /// <summary>
    /// Every /exes route, mapped RELATIVE to the <see cref="Prefix"/> so the full paths are
    /// <c>/exes/list</c>, <c>/exes/slots/{n}</c> and <c>/exes/slots/{n}/build-start</c> exactly as before.
    /// Takes the denied GROUP HANDLE and nothing else: the ungrouped route builder is deliberately out of
    /// scope here so no route can be mapped around the hosted refusal.
    /// </summary>
    private static void MapRoutes(HostedDenyGroup app, DirectorRegistry registry,
        PushedSessionStore? pushedSessions, TimeSpan? streamStaleAfter,
        Snooze.SnoozeRegistry? snoozeRegistry)
    {
        // Gateway Cleanup Phase 2 (PR E, Group C): under streamMode the roster lives in the push store; resolve
        // the same freshness window the /sessions roster uses so a stream-connected Director's sessions are read
        // from the store instead of pulled over HTTP.
        var streamStale = streamStaleAfter ?? TimeSpan.FromSeconds(Core.Configuration.GatewayConfig.DefaultStreamStaleAfterSeconds);

        // ----- list local directors + slot status (JSON; /exes itself is the SPA page) -----
        app.MapGet("/list", (HttpContext ctx) =>
        {
            FileLog.Write("[ExesEndpoints] GET /exes/list");
            try
            {
                var machine = Environment.MachineName;
                var repoRoot = ResolveRepoRoot();

                // Defect 6: this page used to fold each session on its own, straight out of the push store,
                // with NO fleet pass - so it answered differently from every other screen. SessionRole is
                // resolved ONLY by the fleet pass (the Director never sends it), so it was null here, the
                // Worker red-suppression could not fire, and a live Worker showed RED on this page while the
                // roster showed it receded. The expired-snooze override was missing for the same reason:
                // "Snoozed" here, "Needs you" there.
                //
                // The universe is the WHOLE fleet, not the local Directors this page lists: a local Worker's
                // Manager can be on another machine, and asking "is my controller alive?" of the local
                // machine only would re-create defect 13 on a new page.
                // Hosted Multi-Tenancy: /exes/list is a LOCAL dev diagnostics page, not the cockpit read path;
                // it serves Local. Correct on self-host; on hosted the Local partition is empty so the page
                // degrades to no sessions rather than reading another tenant's fleet (never a leak).
                var byDirector = GatewayEndpoints.FleetByDirector(registry, pushedSessions, streamStale, TenantId.Local);
                var fleet = byDirector.Values.SelectMany(x => x).ToList();

                // needsYouStampFor is NOT passed: the needs-you clock is entry/exit and belongs to the roster
                // read. A dev page must not drive it.
                GatewayEndpoints.StampFleetRolesAndFold(fleet, fleet, needsYouStampFor: null, snoozeRegistry: snoozeRegistry);

                var local = registry.ListDirectors(TenantId.Local)
                    .Where(d => string.Equals(d.MachineName, machine, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(d => d.StartedAt)
                    .ToList();

                // Post-cut: each local Director's sessions come ONLY from the pushed store. A Director with no
                // fresh push is not connected to the tunnel and is surfaced with an error.
                var directors = local.Select(d =>
                {
                    var exePath = TryGetExePath(d.Pid);
                    IReadOnlyList<SessionDto>? sessions =
                        byDirector.TryGetValue(d.DirectorId, out var cached) ? cached : null;
                    string? error = sessions is null ? "director not connected to the tunnel" : null;
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
                        sessions = (sessions ?? new List<SessionDto>()).Select(s => new
                        {
                            sessionId = s.SessionId,
                            name = s.Name,
                            agent = s.Agent,
                            activityState = s.ActivityState,
                            statusColor = s.StatusColor,
                            // Issue #1177 (Phase 2.3): render the Gateway fold so the Exes page shows the same
                            // effectiveColor + stateLabel as every other client, instead of the raw Director
                            // color (which is now just blue/red/gray after the Director overlay fold was retired).
                            // Defect 6: READ from the fleet pass above - never recomputed here. A second call to
                            // the fold is a second answer, which is the whole defect.
                            effectiveColor = s.EffectiveColor,
                            stateLabel = s.StateLabel,
                            // The "Snooze ended" badge must ride /exes/list too (the mission requires the
                            // cleared value on every roster). The fold above stamps it; the projection dropped
                            // it. Same verbatim boolean every other surface renders.
                            snoozeExpired = s.SnoozeExpired,
                            repoPath = s.RepoPath,
                        }).ToList(),
                    };
                }).ToList();

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
        app.MapDelete("/slots/{n}", (int n) =>
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
        app.MapPost("/slots/{n}/build-start", async (int n) =>
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
        foreach (var d in registry.ListDirectors(TenantId.Local))
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
