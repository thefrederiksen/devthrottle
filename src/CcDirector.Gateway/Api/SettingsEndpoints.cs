using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using CcDirector.AgentBrain;
using CcDirector.Core.Configuration;
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
///   PUT  /gateway/brain/config    body { "agentId": str, "model": str } -> { agentId, tool, model }
///                                  (issue #510; legacy { "tool": str, ... } still accepted)
///   PUT  /gateway/autostart       body { "enabled": bool } -> { supported, enabled }
///   GET  /gateway/transcription-mode -> { mode } ("byo" | "devthrottle") (issue #497)
///   PUT  /gateway/transcription-mode body { "mode": "byo"|"devthrottle" } -> { mode }
///   GET  /gateway/telemetry-consent  -> { enabled } (fleet-wide richer-usage consent, default ON, issue #649)
///   PUT  /gateway/telemetry-consent  body { "enabled": bool } -> { enabled }
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
                // Issue #457: the fleet network addressing mode ("tailscale" | "lan").
                addressingMode = Core.Configuration.AddressingModeConfig.Get().ToConfigString(),
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
                // Issue #531 follow-up: when on, every wingman summary is saved (terminal + response)
                // as training data for improving the wingman.
                wingmanTrainingCapture = Core.Configuration.WingmanTrainingCaptureConfig.Get(),
                // Issue #649: the fleet-wide richer-usage-telemetry consent (opt-out). Default ON.
                // Gates ONLY the richer usage telemetry; the always-on login/startup events are
                // never gated by it.
                telemetryConsent = Core.Configuration.TelemetryConsentConfig.Get(),
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

        // Persist the brain tool + model choice (issue #393). Both are Gateway-level settings in
        // config.json, the same store the existing brain_model uses, so the choice applies fleet-wide
        // without editing any Director. The running brain is unaffected until the next Gateway restart
        // (the supervisor's driver/options are fixed at host construction) - same as brain_model.
        app.MapPut("/gateway/brain/config", async (HttpContext ctx) =>
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<BrainConfigBody>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (body is null)
                    return Results.BadRequest(new { error = "body { \"agentId\": \"<id>\", \"model\": \"<model>\" } is required" });

                // Issue #510: the wingman agent is chosen by its registered-agent id (the same
                // machine list the New Session picker offers), not a hardcoded Claude-only tool
                // name. The legacy "tool" field (an AgentKind name, issue #393) is still accepted
                // so existing callers keep working - it is matched to the first enabled entry of
                // that kind. Either way we resolve to a real registered entry and persist its id,
                // its AgentKind (for the runtime), and the model.
                var agents = LoadMachineAgents();
                Core.Configuration.AgentEntry? entry = null;

                if (!string.IsNullOrWhiteSpace(body.AgentId))
                {
                    var id = body.AgentId.Trim();
                    entry = agents.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal));
                    if (entry is null)
                        return Results.BadRequest(new { error = "agentId must be a registered, enabled agent on this machine" });
                }
                else if (!string.IsNullOrWhiteSpace(body.Tool))
                {
                    if (!Enum.TryParse<Core.Agents.AgentKind>(body.Tool.Trim(), ignoreCase: true, out var tool))
                        return Results.BadRequest(new { error = "tool must be a recognised agent-kind name" });
                    entry = agents.FirstOrDefault(a => a.Type == tool);
                    if (entry is null)
                        return Results.BadRequest(new { error = $"no registered, enabled agent of kind {tool} on this machine" });
                }
                else
                {
                    return Results.BadRequest(new { error = "agentId is required (a registered agent on this machine)" });
                }

                if (string.IsNullOrWhiteSpace(body.Model))
                    return Results.BadRequest(new { error = "model is required (a model alias or id)" });
                var model = body.Model.Trim();

                Core.Configuration.CcDirectorConfigService.MergePatch(
                    new System.Text.Json.Nodes.JsonObject
                    {
                        ["brain_agent_id"] = entry.Id,
                        ["brain_tool"] = entry.Type.ToString(),
                        ["brain_model"] = model,
                    });
                FileLog.Write($"[SettingsEndpoints] brain config set: agentId={entry.Id}, tool={entry.Type}, model={model}");
                return Results.Json(new { agentId = entry.Id, tool = entry.Type.ToString(), model });
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[SettingsEndpoints] PUT /gateway/brain/config bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
        });

        // Read wingman training-data capture state (issue #531 follow-up): when on, every wingman
        // summary saves up to 20,000 chars of the session terminal + the wingman response as a
        // labeled example for improving the wingman.
        app.MapGet("/gateway/wingman/training-capture", () =>
            Results.Json(new { enabled = Core.Configuration.WingmanTrainingCaptureConfig.Get() }));

        // Write the training-data capture toggle. Takes effect immediately (read at capture time) -
        // no restart, unlike wingman_enabled.
        app.MapPut("/gateway/wingman/training-capture", async (HttpContext ctx) =>
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<WingmanBody>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (body is null)
                    return Results.BadRequest(new { error = "body { \"enabled\": true|false } is required" });

                Core.Configuration.CcDirectorConfigService.MergePatch(
                    new System.Text.Json.Nodes.JsonObject { ["wingman_training_capture"] = body.Enabled });
                FileLog.Write($"[SettingsEndpoints] wingman_training_capture set to {body.Enabled}");
                return Results.Json(new { enabled = body.Enabled });
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[SettingsEndpoints] PUT /gateway/wingman/training-capture bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
        });

        // The fleet-wide richer-usage-telemetry consent (issue #649). Lives in its own endpoint class
        // (TelemetryConsentEndpoint) so it can be unit-tested in isolation, but it is part of the one
        // Gateway settings surface and is mapped here alongside the other settings routes. Default ON;
        // gates only the richer usage telemetry - the always-on login/startup events are never gated.
        TelemetryConsentEndpoint.Map(app);

        // Network addressing mode (issue #457): "tailscale" (advertise the Tailscale Serve
        // front door) or "lan" (advertise the machine's real LAN IP). Stored as the top-level
        // config.json key addressing_mode. This is a per-machine setting read at process start;
        // it applies to THIS Gateway host's own Directors on the next restart. Remote Directors
        // read their own machine's config (see the docs note on issue #457).
        app.MapGet("/gateway/addressing-mode", () =>
            Results.Json(new { mode = Core.Configuration.AddressingModeConfig.Get().ToConfigString() }));

        app.MapPut("/gateway/addressing-mode", async (HttpContext ctx) =>
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<AddressingModeBody>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (body is null || string.IsNullOrWhiteSpace(body.Mode))
                    return Results.BadRequest(new { error = "body { \"mode\": \"tailscale\"|\"lan\" } is required" });

                if (!Core.Configuration.AddressingModeExtensions.IsValid(body.Mode))
                    return Results.BadRequest(new { error = "mode must be \"tailscale\" or \"lan\"" });

                var mode = Core.Configuration.AddressingModeExtensions.Parse(body.Mode);
                Core.Configuration.CcDirectorConfigService.MergePatch(
                    new System.Text.Json.Nodes.JsonObject { ["addressing_mode"] = mode.ToConfigString() });
                FileLog.Write($"[SettingsEndpoints] addressing_mode set to {mode.ToConfigString()}");
                return Results.Json(new { mode = mode.ToConfigString() });
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[SettingsEndpoints] PUT /gateway/addressing-mode bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
        });

        // Transcription mode (issue #497, #541): "local" (in-process Whisper.net, the default - no
        // key), "byo" (the user's own OpenAI key -> api.openai.com), or "devthrottle" (a DevThrottle
        // key -> devthrottle.com's managed proxy). Stored as the top-level config.json key
        // transcription_mode, the same store addressing_mode uses. The two remote keys live in the
        // existing vault (OPENAI_API_KEY, DEVTHROTTLE_API_KEY).
        app.MapGet("/gateway/transcription-mode", () =>
            Results.Json(new { mode = Core.Configuration.TranscriptionModeConfig.Get().ToConfigString() }));

        app.MapPut("/gateway/transcription-mode", async (HttpContext ctx) =>
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<TranscriptionModeBody>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (body is null || string.IsNullOrWhiteSpace(body.Mode))
                    return Results.BadRequest(new { error = "body { \"mode\": \"local\"|\"byo\" } is required" });

                if (!Core.Configuration.TranscriptionModeExtensions.IsValid(body.Mode))
                    return Results.BadRequest(new { error = "mode must be \"local\" or \"byo\"" });

                var mode = Core.Configuration.TranscriptionModeExtensions.Parse(body.Mode);

                // DevThrottle-hosted transcription is "coming soon" (issue #541) and must NEVER be
                // written as the active mode - enforced here server-side, not just hidden in the UI.
                if (mode == Core.Configuration.TranscriptionMode.DevThrottle)
                {
                    FileLog.Write("[SettingsEndpoints] PUT /gateway/transcription-mode rejected devthrottle (coming soon)");
                    return Results.BadRequest(new { error = "DevThrottle transcription is coming soon and cannot be selected yet" });
                }

                Core.Configuration.TranscriptionModeConfig.Set(mode);
                FileLog.Write($"[SettingsEndpoints] transcription_mode set to {mode.ToConfigString()}");
                return Results.Json(new { mode = mode.ToConfigString() });
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[SettingsEndpoints] PUT /gateway/transcription-mode bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
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

        // Issue #510: the wingman agent picker is filled from the agents registered on this machine
        // (the same enabled agent.entries the New Session dialog offers), not a hardcoded
        // Claude-only list. The Cockpit selects the saved agent by id; "agentId" is the saved
        // choice (brain_agent_id) so the picker round-trips across a page reload.
        var agents = LoadMachineAgents();
        var savedAgentId = Core.Configuration.CcDirectorConfigService.ReadRaw()["brain_agent_id"] is { } idNode
            && idNode.GetValueKind() == System.Text.Json.JsonValueKind.String
                ? idNode.GetValue<string>()
                : null;

        // Issue #510 (QA bounce, criterion 3): the Model field must round-trip across a page reload
        // exactly as the agent does. We surface the SAVED model (config.json "brain_model" via
        // BrainModelConfig.Get) - the same value the PUT writes - NOT host.BrainModel (the running
        // brain's model, fixed at host construction). Sourcing the GET from the running brain meant a
        // freshly-saved model was persisted to disk yet never shown back on reload. The saved value is
        // what the user chose; the running brain still picks it up on the next Gateway restart (the
        // documented "applies on next restart" contract for the live process is unchanged).
        var savedModel = Core.Configuration.BrainModelConfig.Get();

        return new
        {
            tool = host.BrainTool.ToString(),
            // The agents registered on this machine, in list order (issue #510): the wingman can
            // run as any of them (the driver-level hostability work landed in issue #509).
            agents = agents.Select(a => new { id = a.Id, displayName = a.DisplayName, type = a.Type.ToString() }).ToArray(),
            agentId = savedAgentId,
            model = savedModel,
            sessionId = brain.SessionId,
            pid = brain.ProcessId,
            alive = health.IsAlive,
            started = !IsNotStarted(health.Status),
            status = health.Status,
            detail = BrainDetail(health),
        };
    }

    /// <summary>
    /// The agents registered on THIS machine, filtered to the enabled entries - exactly the set the
    /// New Session dialog offers (issue #510). Uses <see cref="Core.Configuration.AgentEntryStore.LoadEntries"/>
    /// with the same default <see cref="Core.Configuration.AgentOptions"/> the New Session dialog
    /// falls back to, so the picker mirrors that list (including the one-time legacy seed when
    /// agent.entries has never been written) rather than showing an empty dropdown.
    /// </summary>
    private static List<Core.Configuration.AgentEntry> LoadMachineAgents()
    {
        return Core.Configuration.AgentEntryStore.LoadEntries(new Core.Configuration.AgentOptions())
            .Where(e => e.Enabled)
            .ToList();
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

    private sealed record AddressingModeBody(string? Mode);
    private sealed record TranscriptionModeBody(string? Mode);
    private sealed record AutostartBody(bool Enabled);
    private sealed record WingmanBody(bool Enabled);
    private sealed record BrainConfigBody(string? AgentId, string? Tool, string? Model);
}
