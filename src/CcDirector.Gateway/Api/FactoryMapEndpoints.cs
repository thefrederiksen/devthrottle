using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// A factory publishes its map (issue #3383).
///
///   PUT /gateway/factory/map   body PublishFactoryMapRequest -> 200 PublishFactoryMapResponse | 400
///
/// The factory's tool draws the map from the factory's own files and sends it here; the latest one per factory is
/// kept (<see cref="FactoryMapStore"/>) and the owner sees it on the factory's Map tab. Nobody draws a map in the
/// Cockpit. A session key may call it (SessionKeyGuard) - the factory's own session publishes - and so may the owner.
/// Behind the factory agents switch per account, like every factory route.
/// </summary>
internal static class FactoryMapEndpoints
{
    public const string Route = "/gateway/factory/map";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static void Map(IEndpointRouteBuilder app, FactoryMapStore maps, Func<HttpContext, TenantId?> resolveTenant)
    {
        ArgumentNullException.ThrowIfNull(maps);
        ArgumentNullException.ThrowIfNull(resolveTenant);
        app.MapPut(Route, async (HttpContext ctx) =>
        {
            PublishFactoryMapRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<PublishFactoryMapRequest>(ctx.Request.Body, JsonOpts, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[FactoryMapEndpoints] PUT bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
            if (resolveTenant(ctx) is not { } tenant)
                return Results.Json(new { error = "no account is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);

            var session = AuthMiddleware.CallingSession(ctx);
            var by = session is not null
                ? "session " + session.SessionId
                : "the owner (" + (AuthMiddleware.RegisteringCredential(ctx) ?? AuthMiddleware.IdentityKind(ctx)) + ")";
            try
            {
                var stored = maps.Publish(tenant, req, by, DateTime.UtcNow);
                return Results.Json(new PublishFactoryMapResponse
                {
                    Factory = stored.Map.Factory,
                    Nodes = stored.Map.Nodes.Count,
                    Edges = stored.Map.Edges.Count,
                    PublishedUtc = stored.PublishedUtc,
                });
            }
            catch (FactoryViewValidationException ex)
            {
                FileLog.Write($"[FactoryMapEndpoints] PUT refused: {ex.Message}");
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        FileLog.Write($"[FactoryMapEndpoints] mapped {Route}");
    }
}
