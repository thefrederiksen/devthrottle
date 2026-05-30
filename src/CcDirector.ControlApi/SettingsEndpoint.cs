using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.ControlApi;

/// <summary>
/// Maps the settings REST surface so an external agent (Claude Code) can read and write
/// the Director's <c>config.json</c> programmatically:
///
///   GET /settings        -> the full config.json as a JSON object (schema-agnostic).
///   PUT /settings &lt;obj&gt; -> deep-merges the posted object into config.json and returns
///                           the merged result.
///
/// The body is a partial patch: only the keys you send are changed, every other section is
/// preserved (see <see cref="CcDirectorConfigService.MergePatch"/>). The endpoint is
/// schema-agnostic on purpose - it passes JSON through rather than binding a rigid DTO, so
/// it covers ALL settings (gateway, screenshots, llm, photos, ...) without per-key churn.
///
/// When a PUT touches the <c>gateway</c> block, the Director re-registers with the gateway
/// live via <paramref name="reapplyGatewayAsync"/> - no restart needed. Other sections are
/// read fresh from disk on each use, so they take effect immediately too.
///
/// Loopback-only and subject to the host's auth middleware, exactly like the other routes.
/// </summary>
internal static class SettingsEndpoint
{
    public static void Map(IEndpointRouteBuilder app, Func<Task> reapplyGatewayAsync)
    {
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
    }
}
