using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Gateway relay routes for cross-machine Director lifecycle management via cc-launcher.
///
/// Issue #331: the cc-launcher process on each machine registers with the Gateway
/// (POST /launchers/register) and heartbeats so the Gateway knows it is live. The
/// Gateway then exposes relay routes that forward lifecycle verbs to the target
/// machine's launcher loopback REST API:
///
///   POST /launchers/register                        launcher self-registers
///   POST /launchers/{machine}/heartbeat             launcher heartbeat
///   DELETE /launchers/{machine}                     graceful launcher unregister
///   GET  /launchers                                 list registered launchers
///
///   POST /machines/{machine}/director/restart       relay -> launcher POST /director/restart
///   POST /machines/{machine}/director/start         relay -> launcher POST /director/start
///   POST /machines/{machine}/director/stop          relay -> launcher POST /director/stop
///   POST /machines/{machine}/launch                 relay -> launcher POST /launch
///
/// Relay calls are token-gated (Gateway Bearer) and audit-logged. A slot guard in the
/// relay refuses restart/stop targeting the main Director build or slots 1-4 unless the
/// request carries <c>"confirmProtected": true</c>.
///
/// Cross-machine relay: when the launcher on a remote machine registers, it supplies a
/// <c>networkAddress</c> (tailnet hostname or IP) in its registration payload.  The relay
/// uses that address (plus the registered port) to build the outbound HTTP URL so it can
/// reach launchers on other machines over Tailscale:
///   - Same-machine launcher (networkAddress empty): dials http://127.0.0.1:<port>/
///   - Remote launcher (networkAddress set):         dials http://<networkAddress>:<port>/
/// This enables the Gateway on SOREN_NORTH to relay lifecycle verbs to the launcher on
/// SORENLAPTOP when SORENLAPTOP's launcher registered with its tailnet hostname.
/// </summary>
internal static class MachineEndpoints
{
    /// <summary>
    /// Slots 1-4 and the main slot (0) are protected from unconfirmed restart/stop.
    /// Agents run on slots >= 5 (issue #331 spec: "slots 5+" relay freely; the slot-guard
    /// refuses main + 1-4 without explicit confirm).
    /// </summary>
    private static readonly Regex SlotFromPath =
        new(@"cc-director(\d*)\.exe$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static void Map(IEndpointRouteBuilder app, LauncherRegistry launchers)
    {
        // ===== Launcher self-registration surface =====

        // POST /launchers/register - the launcher POSTs this on startup and after reconnects.
        app.MapPost("/launchers/register", (LauncherRegistrationRequest req) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.MachineName))
                return Results.BadRequest(new { error = "machineName is required" });
            if (req.Port <= 0)
                return Results.BadRequest(new { error = "port must be > 0" });
            if (string.IsNullOrWhiteSpace(req.Token))
                return Results.BadRequest(new { error = "token is required" });

            FileLog.Write($"[MachineEndpoints] POST /launchers/register: machine={req.MachineName}, port={req.Port}, pid={req.Pid}, version={req.Version}");
            var dto = launchers.Upsert(req);
            return Results.Json(dto, statusCode: 201);
        });

        // POST /launchers/{machine}/heartbeat - keep-alive from the launcher every 30 s.
        app.MapPost("/launchers/{machine}/heartbeat", (string machine) =>
        {
            var ok = launchers.Heartbeat(machine);
            if (!ok)
            {
                FileLog.Write($"[MachineEndpoints] POST /launchers/{machine}/heartbeat: unknown -> 410");
                return Results.StatusCode(410);
            }
            FileLog.Write($"[MachineEndpoints] POST /launchers/{machine}/heartbeat: ok");
            return Results.Json(new { ok = true });
        });

        // DELETE /launchers/{machine} - graceful unregister on launcher shutdown.
        app.MapDelete("/launchers/{machine}", (string machine) =>
        {
            launchers.Remove(machine);
            FileLog.Write($"[MachineEndpoints] DELETE /launchers/{machine}: removed");
            return Results.Json(new { ok = true });
        });

        // GET /launchers - list all registered launchers (machine name, port, last-seen).
        app.MapGet("/launchers", () =>
        {
            var list = launchers.ListLaunchers();
            FileLog.Write($"[MachineEndpoints] GET /launchers: count={list.Count}");
            return Results.Json(list);
        });

        // ===== Machine relay surface =====

        // POST /machines/{machine}/director/restart
        app.MapPost("/machines/{machine}/director/restart", async (string machine, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[MachineEndpoints] POST /machines/{machine}/director/restart: caller={ctx.Connection.RemoteIpAddress}");
            return await RelayDirectorLifecycleAsync(machine, "restart", ctx, launchers, ct);
        });

        // POST /machines/{machine}/director/start
        app.MapPost("/machines/{machine}/director/start", async (string machine, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[MachineEndpoints] POST /machines/{machine}/director/start: caller={ctx.Connection.RemoteIpAddress}");
            return await RelayDirectorLifecycleAsync(machine, "start", ctx, launchers, ct);
        });

        // POST /machines/{machine}/director/stop
        app.MapPost("/machines/{machine}/director/stop", async (string machine, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[MachineEndpoints] POST /machines/{machine}/director/stop: caller={ctx.Connection.RemoteIpAddress}");
            return await RelayDirectorLifecycleAsync(machine, "stop", ctx, launchers, ct);
        });

        // POST /machines/{machine}/launch - relay a generic launch request to the launcher.
        app.MapPost("/machines/{machine}/launch", async (string machine, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[MachineEndpoints] POST /machines/{machine}/launch: caller={ctx.Connection.RemoteIpAddress}");

            var (launcher, token, networkAddress, err) = ResolveLauncher(machine, launchers);
            if (err is not null)
            {
                FileLog.Write($"[MachineEndpoints] /machines/{machine}/launch: {err.Value.log}");
                return err.Value.result;
            }

            // Forward the original request body verbatim to the launcher.
            LaunchRelayBody? body = null;
            try { body = await ctx.Request.ReadFromJsonAsync<LaunchRelayBody>(ct); }
            catch { /* treat as null -> launcher will 400 */ }

            using var http = BuildLauncherClient(launcher!.Port, token!, networkAddress!);
            IResult result;
            try
            {
                var response = await http.PostAsJsonAsync("/launch", body, ct);
                var payload = await response.Content.ReadAsStringAsync(ct);
                FileLog.Write($"[MachineEndpoints] /machines/{machine}/launch relay: status={response.StatusCode}");
                result = Results.Json(new RelayResult
                {
                    Machine = machine,
                    Verb = "launch",
                    RelayStatus = (int)response.StatusCode,
                    Payload = payload,
                }, statusCode: (int)response.StatusCode);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[MachineEndpoints] /machines/{machine}/launch relay FAILED: {ex.Message}");
                result = Results.Json(new { error = $"launcher unreachable on {machine}:{launcher!.Port}", detail = ex.Message }, statusCode: 502);
            }
            return result;
        });
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Relay a director lifecycle verb (restart/start/stop) to the target machine's launcher.
    /// Enforces the slot guard for restart/stop verbs.
    /// </summary>
    private static async Task<IResult> RelayDirectorLifecycleAsync(
        string machine, string verb, HttpContext ctx, LauncherRegistry launchers, CancellationToken ct)
    {
        // Parse optional body for confirmProtected flag and target exe path.
        // Use JsonDocument to avoid internal-class reflection issues with System.Text.Json.
        // Do NOT gate on ContentLength - transfer-encoded bodies may have no explicit length.
        string? exePathFromBody = null;
        bool? confirmProtectedFromBody = null;
        try
        {
            ctx.Request.EnableBuffering();
            if (ctx.Request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true
                || ctx.Request.ContentLength is > 0)
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
                if (doc.RootElement.TryGetProperty("exePath", out var ep))
                    exePathFromBody = ep.GetString();
                if (doc.RootElement.TryGetProperty("confirmProtected", out var cp) && cp.ValueKind == JsonValueKind.True)
                    confirmProtectedFromBody = true;
            }
        }
        catch { /* body is optional */ }

        // Slot guard: refuse restart/stop targeting the main build or slots 1-4 without confirm.
        if ((verb == "restart" || verb == "stop") && exePathFromBody is { } exePath)
        {
            var (isProtected, slotDesc) = IsProtectedSlot(exePath);
            if (isProtected && confirmProtectedFromBody != true)
            {
                var reason = $"slot guard: refusing {verb} of protected Director ({slotDesc}) without confirmProtected=true";
                FileLog.Write($"[MachineEndpoints] RELAY_REFUSED machine={machine} verb={verb} reason={reason}");
                return Results.Json(new
                {
                    error = "slot_guard",
                    detail = reason,
                    machine,
                    verb,
                    exePath,
                    hint = "Set confirmProtected=true to override (human-confirmed action only).",
                }, statusCode: 403);
            }
        }

        var (launcher, token, networkAddress, err) = ResolveLauncher(machine, launchers);
        if (err is not null)
        {
            FileLog.Write($"[MachineEndpoints] /machines/{machine}/director/{verb}: {err.Value.log}");
            return err.Value.result;
        }

        var dialHost = string.IsNullOrWhiteSpace(networkAddress) ? "127.0.0.1" : networkAddress;
        using var http = BuildLauncherClient(launcher!.Port, token!, networkAddress!);
        try
        {
            var response = await http.PostAsync($"/director/{verb}", null, ct);
            var payload = await response.Content.ReadAsStringAsync(ct);
            FileLog.Write($"[MachineEndpoints] relay /director/{verb} machine={machine} host={dialHost} -> status={response.StatusCode}");
            return Results.Json(new RelayResult
            {
                Machine = machine,
                Verb = verb,
                RelayStatus = (int)response.StatusCode,
                Payload = payload,
            }, statusCode: (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MachineEndpoints] relay /director/{verb} machine={machine} host={dialHost} FAILED: {ex.Message}");
            return Results.Json(new
            {
                error = $"launcher unreachable on {dialHost}:{launcher!.Port}",
                detail = ex.Message,
            }, statusCode: 502);
        }
    }

    /// <summary>
    /// Resolve the launcher entry for a machine. Returns (dto, token, networkAddress, null) on
    /// success or (null, null, null, (log, result)) on failure.
    /// </summary>
    private static (LauncherDto? launcher, string? token, string? networkAddress, (string log, IResult result)? err)
        ResolveLauncher(string machine, LauncherRegistry launchers)
    {
        var launcher = launchers.Get(machine);
        if (launcher is null)
        {
            return (null, null, null, ($"launcher not registered for machine={machine}",
                Results.Json(new { error = $"no launcher registered for machine '{machine}'", machine }, statusCode: 404)));
        }

        var token = launchers.GetToken(machine);
        if (string.IsNullOrEmpty(token))
        {
            return (null, null, null, ($"launcher token missing for machine={machine}",
                Results.Json(new { error = "launcher token not available" }, statusCode: 500)));
        }

        var networkAddress = launchers.GetNetworkAddress(machine) ?? "";
        return (launcher, token, networkAddress, null);
    }

    /// <summary>
    /// Build a short-lived HttpClient pointed at the launcher's REST API.
    ///
    /// When <paramref name="networkAddress"/> is non-empty the launcher is on a REMOTE
    /// machine: dial http://&lt;networkAddress&gt;:&lt;port&gt;/ over the tailnet.
    /// When <paramref name="networkAddress"/> is empty the launcher is co-located with the
    /// Gateway: dial http://127.0.0.1:&lt;port&gt;/ on loopback.
    /// </summary>
    private static HttpClient BuildLauncherClient(int port, string token, string networkAddress)
    {
        var host = string.IsNullOrWhiteSpace(networkAddress) ? "127.0.0.1" : networkAddress;
        var http = new HttpClient
        {
            BaseAddress = new Uri($"http://{host}:{port}/"),
            Timeout = TimeSpan.FromSeconds(10),
        };
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        return http;
    }

    /// <summary>
    /// Returns (isProtected=true, description) when the exe path refers to the main build
    /// or a protected slot (1-4). Agent slots (5+) are NOT protected.
    /// </summary>
    private static (bool isProtected, string description) IsProtectedSlot(string exePath)
    {
        var m = SlotFromPath.Match(Path.GetFileName(exePath));
        if (!m.Success)
        {
            // Path does not match cc-director*.exe pattern - not a slot we can classify.
            return (false, "unknown");
        }

        var slotStr = m.Groups[1].Value;
        if (string.IsNullOrEmpty(slotStr))
        {
            // cc-director.exe - the main production build.
            return (true, "main build (cc-director.exe)");
        }

        if (int.TryParse(slotStr, out var slot) && slot >= 1 && slot <= 4)
        {
            return (true, $"protected slot cc-director{slot}.exe");
        }

        // Slot 5+ - not protected.
        return (false, $"agent slot {slotStr}");
    }
}

/// <summary>Request body forwarded verbatim to the launcher POST /launch endpoint.</summary>
internal sealed class LaunchRelayBody
{
    public string? Path { get; init; }
    public string? Args { get; init; }
    public string? Cwd { get; init; }
    public bool Headless { get; init; }
}

/// <summary>Response body returned by the Gateway for relay calls.</summary>
internal sealed class RelayResult
{
    public required string Machine { get; init; }
    public required string Verb { get; init; }
    public required int RelayStatus { get; init; }
    public required string Payload { get; init; }
}
