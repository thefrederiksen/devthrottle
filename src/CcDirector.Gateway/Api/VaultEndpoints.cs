using System.Text.Json;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The central key-vault REST surface (docs/architecture/gateway/GATEWAY_KEY_VAULT.md).
/// Keys are set once here - from the Cockpit Keys page - and Directors GET them on demand.
/// Auth is the Gateway's host-wide token middleware, so these routes inherit it; the tailnet
/// plus that token is the trust boundary. Values leave the Gateway only via the single-key
/// GET a Director calls; the list route exposes names only, never values.
///
///   GET    /vault/keys           -> { "names": [...] }            (names only)
///   GET    /vault/keys/{name}    -> { "name", "value" } | 404
///   PUT    /vault/keys/{name}    body { "value": "..." } -> { "name", "set": true }
///   DELETE /vault/keys/{name}    -> { "name", "deleted": bool }
/// </summary>
internal static class VaultEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static void Map(IEndpointRouteBuilder app, KeyVault vault)
    {
        app.MapGet("/vault/keys", () => Results.Json(new { names = vault.ListNames() }));

        app.MapGet("/vault/keys/{name}", (string name) =>
        {
            var value = vault.Get(name);
            return value is null
                ? Results.NotFound(new { error = "no such key", name })
                : Results.Json(new { name, value });
        });

        app.MapPut("/vault/keys/{name}", async (string name, HttpContext ctx) =>
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<VaultKeyBody>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (body?.Value is null)
                    return Results.BadRequest(new { error = "body { \"value\": \"...\" } is required" });

                vault.Set(name, body.Value);
                return Results.Json(new { name, set = true });
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[VaultEndpoints] PUT {name} bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
        });

        app.MapDelete("/vault/keys/{name}", (string name) =>
            Results.Json(new { name, deleted = vault.Delete(name) }));
    }

    private sealed record VaultKeyBody(string? Value);
}
