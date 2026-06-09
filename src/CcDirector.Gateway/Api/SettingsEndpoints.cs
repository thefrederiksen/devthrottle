using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using CcDirector.AgentBrain;
using CcDirector.Core.Network;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Cockpit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The Gateway settings surface that backs the one Cockpit Settings page
/// (docs/architecture/gateway/SETTINGS_OWNERSHIP.md). One GET assembles the whole snapshot the
/// page renders; two actions mutate Gateway-owned state. Inherits the host-wide token middleware,
/// same as every other endpoint here.
///
///   GET  /gateway/settings        -> { version, state, port, uptimeSeconds, directors, mode,
///                                       cockpit:{port,up,url}, brain:{...}, autostart:{supported,enabled} }
///   POST /gateway/brain/restart   -> { ok, brain:{...} } (restarts the warm brain, issue #184)
///   PUT  /gateway/autostart       body { "enabled": bool } -> { supported, enabled }
/// </summary>
internal static class SettingsEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static void Map(IEndpointRouteBuilder app, GatewayHost host)
    {
        app.MapGet("/gateway/settings", async () =>
        {
            var cockpitPort = CockpitSupervisor.ResolvePort();
            var cockpitUp = await IsLoopbackPortOpenAsync(cockpitPort);
            var up = DateTime.UtcNow - host.StartedAtUtc;

            return Results.Json(new
            {
                version = AppVersion.Full,
                state = "Running",
                port = host.Port,
                uptimeSeconds = (long)up.TotalSeconds,
                directors = host.Registry.ListDirectors().Count,
                mode = host.SettingsHooks?.Mode?.Invoke() ?? "unknown",
                cockpit = new
                {
                    port = cockpitPort,
                    up = cockpitUp,
                    url = TailscaleIdentity.TryGetFrontDoorBaseUrl() is { } b ? b + "/" : null,
                },
                brain = await BrainBlockAsync(host),
                autostart = new
                {
                    supported = host.SettingsHooks?.AutostartEnabled is not null,
                    enabled = host.SettingsHooks?.AutostartEnabled?.Invoke(),
                },
            });
        });

        // Restart the warm brain (issue #184): the one recovery verb, and doubles as a manual
        // start. Mirrors the old tray-window Restart Brain button, now reachable from the Cockpit.
        app.MapPost("/gateway/brain/restart", async () =>
        {
            FileLog.Write("[SettingsEndpoints] POST /gateway/brain/restart");
            try
            {
                await host.Brain.RestartAsync();
                FileLog.Write($"[SettingsEndpoints] brain restart OK: pid={host.Brain.ProcessId}, session={host.Brain.SessionId}");
                return Results.Json(new { ok = true, brain = await BrainBlockAsync(host) });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SettingsEndpoints] brain restart FAILED: {ex.Message}");
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        // Toggle the per-user autostart Run-key. The write itself is GatewayApp-owned (it needs the
        // tray exe path + args), supplied via SettingsHooks; a host with no hook answers unsupported.
        app.MapPut("/gateway/autostart", async (HttpContext ctx) =>
        {
            var set = host.SettingsHooks?.SetAutostart;
            if (set is null)
            {
                FileLog.Write("[SettingsEndpoints] PUT /gateway/autostart: no hook; unsupported on this host");
                return Results.Json(new { supported = false });
            }

            try
            {
                var body = await JsonSerializer.DeserializeAsync<AutostartBody>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (body is null)
                    return Results.BadRequest(new { error = "body { \"enabled\": true|false } is required" });

                var nowEnabled = set(body.Enabled);
                FileLog.Write($"[SettingsEndpoints] autostart requested={body.Enabled}, now={nowEnabled}");
                return Results.Json(new { supported = true, enabled = nowEnabled });
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[SettingsEndpoints] PUT /gateway/autostart bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
        });
    }

    /// <summary>The brain status block - shared by the snapshot GET and the restart POST.</summary>
    private static async Task<object> BrainBlockAsync(GatewayHost host)
    {
        var brain = host.Brain;
        var health = await brain.GetHealthAsync();
        return new
        {
            model = host.BrainModel,
            sessionId = brain.SessionId,
            pid = brain.ProcessId,
            alive = health.IsAlive,
            started = !IsNotStarted(health.Status),
            status = health.Status,
            detail = BrainDetail(health),
        };
    }

    /// <summary>Human-readable one-liner for the brain state. Pure, for tests.</summary>
    public static string BrainDetail(BrainHealth health)
    {
        if (IsNotStarted(health.Status))
            return "not started (spawns on first use)";
        return health.IsAlive
            ? $"alive - {health.ActivityState}, idle {health.IdleSeconds:F0}s, context {health.ContextTokens:N0} tokens"
            : $"DEAD ({health.Status}) - use Restart Brain";
    }

    private static bool IsNotStarted(string status) =>
        string.Equals(status, "NotStarted", StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> IsLoopbackPortOpenAsync(int port)
    {
        try
        {
            using var tcp = new TcpClient();
            var connect = tcp.ConnectAsync(IPAddress.Loopback, port);
            var done = await Task.WhenAny(connect, Task.Delay(500));
            return done == connect && tcp.Connected;
        }
        catch
        {
            return false;
        }
    }

    private sealed record AutostartBody(bool Enabled);
}
