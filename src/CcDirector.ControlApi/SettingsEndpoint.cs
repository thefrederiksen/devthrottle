using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.Settings;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.ControlApi;

/// <summary>
/// Maps the settings REST surface so an external agent (Claude Code, another agent) can read,
/// write, AND detect settings programmatically - no GUI dialog required:
///
///   GET  /settings                     -> the full config.json as a JSON object.
///   PUT  /settings &lt;obj&gt;               -> deep-merges a partial patch into config.json.
///   POST /settings/detect/gateway      -> scan the tailnet + loopback for a gateway.
///   POST /settings/detect/public-url   -> this Director's advertised URL (MagicDNS name / IP).
///   POST /settings/detect/screenshots  -> the OS screenshots folder.
///   POST /settings/test/gateway {url}  -> probe a gateway URL's /healthz.
///
/// The detect endpoints accept <c>?apply=true</c> to write the detected value into config.json
/// (and live re-register, for gateway changes) in one call; otherwise they only return the
/// detected value and the agent decides whether to PUT it. Detection runs the same
/// <see cref="SettingsDetectionService"/> the Avalonia dialog uses - one implementation.
///
/// Loopback-only and subject to the host's auth middleware, exactly like the other routes.
/// </summary>
internal static class SettingsEndpoint
{
    public sealed record GatewayTestRequest(string? Url);

    public static void Map(IEndpointRouteBuilder app, Func<Task> reapplyGatewayAsync, Func<int> getControlPort)
    {
        var detector = new SettingsDetectionService();

        app.MapGet("/settings", () => Results.Json(CcDirectorConfigService.ReadRaw()));

        app.MapPut("/settings", async (JsonNode? body) =>
        {
            if (body is not JsonObject patch)
                return Results.BadRequest(new { error = "request body must be a JSON object" });

            FileLog.Write($"[SettingsEndpoint] PUT /settings: keys={string.Join(",", patch.Select(kv => kv.Key))}");
            var merged = CcDirectorConfigService.MergePatch(patch);

            // Gateway settings need a live re-register; everything else is read on next use.
            if (patch.ContainsKey("gateway"))
                await reapplyGatewayAsync();

            return Results.Json(merged);
        });

        // ===== Detection (the same logic the Settings dialog buttons run) =====

        app.MapPost("/settings/detect/gateway", async (bool? apply) =>
        {
            FileLog.Write($"[SettingsEndpoint] POST /settings/detect/gateway: apply={apply == true}");
            var result = await detector.DetectGatewayAsync();
            var applied = false;
            if (result.Url is not null && apply == true)
            {
                CcDirectorConfigService.MergePatch(new JsonObject { ["gateway"] = new JsonObject { ["url"] = result.Url } });
                await reapplyGatewayAsync();
                applied = true;
            }
            return Results.Json(new { found = result.Url, scanned = result.Scanned, applied });
        });

        app.MapPost("/settings/detect/public-url", async (bool? apply) =>
        {
            FileLog.Write($"[SettingsEndpoint] POST /settings/detect/public-url: apply={apply == true}");
            var result = await detector.DetectPublicUrlAsync(getControlPort());
            var applied = false;
            if (result.Url is not null && apply == true)
            {
                CcDirectorConfigService.MergePatch(new JsonObject { ["gateway"] = new JsonObject { ["tailnetEndpoint"] = result.Url } });
                await reapplyGatewayAsync();
                applied = true;
            }
            return Results.Json(new { url = result.Url, kind = result.Kind, applied });
        });

        app.MapPost("/settings/detect/screenshots", async (bool? apply) =>
        {
            FileLog.Write($"[SettingsEndpoint] POST /settings/detect/screenshots: apply={apply == true}");
            var result = await detector.DetectScreenshotsAsync();
            var applied = false;
            if (result.Directory is not null && apply == true)
            {
                CcDirectorConfigService.MergePatch(new JsonObject { ["screenshots"] = new JsonObject { ["source_directory"] = result.Directory } });
                applied = true;
            }
            return Results.Json(new { directory = result.Directory, applied });
        });

        app.MapPost("/settings/test/gateway", async (HttpContext ctx, GatewayTestRequest? req) =>
        {
            var url = req?.Url;
            if (string.IsNullOrWhiteSpace(url))
                url = ctx.Request.Query["url"].ToString();
            if (string.IsNullOrWhiteSpace(url))
                return Results.BadRequest(new { error = "url is required (JSON body {\"url\":\"...\"} or ?url=...)" });

            var result = await detector.TestGatewayAsync(url);
            return Results.Json(new { ok = result.Ok, message = result.Message, version = result.Version, directors = result.Directors, sessions = result.Sessions });
        });
    }
}
