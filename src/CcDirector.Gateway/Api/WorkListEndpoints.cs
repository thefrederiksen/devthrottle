using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The named work-list REST surface (issue #273, child of #270). A named work list is an ordered
/// list of structured item refs <c>{ source, id, area? }</c> plus a single-consumer claim - the
/// object the product skill writes to, the Cockpit views, and the queue runner drains. These
/// routes live on the Gateway's existing API surface (ASSUMPTION B1), so they inherit the
/// host-wide token middleware and are reachable cross-machine just like the rest of the Gateway.
///
///   POST   /lists                                 body { name }                  -> { name } | 409
///   GET    /lists                                 -> { lists: [ { name, items, consumer } ] }
///   GET    /lists/{name}                          -> { name, items, consumer } | 404
///   POST   /lists/{name}/items                    body { source, id, area? }     -> ok | 404
///   PATCH  /lists/{name}/items                    body [ { source, id, area? } ] -> ok | 404
///   DELETE /lists/{name}/items/{source}/{id}      -> { removed } | 404
///   POST   /lists/{name}/consumer                 body { consumer? }             -> { consumer } | 409 | 404
///   DELETE /lists/{name}/consumer                 -> { released } | 404
/// </summary>
internal static class WorkListEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static void Map(IEndpointRouteBuilder app, WorkListStore store)
    {
        app.MapPost("/lists", async (HttpContext ctx) =>
        {
            CreateListBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<CreateListBody>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[WorkListEndpoints] POST /lists bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }

            if (string.IsNullOrWhiteSpace(body?.Name))
                return Results.BadRequest(new { error = "body { \"name\": \"...\" } is required" });

            return store.Create(body.Name)
                ? Results.Json(new { name = body.Name })
                : Results.Conflict(new { error = "a list with that name already exists", name = body.Name });
        });

        app.MapGet("/lists", () => Results.Json(new { lists = store.ListAll() }));

        app.MapGet("/lists/{name}", (string name) =>
        {
            var list = store.Get(name);
            return list is null
                ? Results.NotFound(new { error = "no such list", name })
                : Results.Json(list);
        });

        app.MapPost("/lists/{name}/items", async (string name, HttpContext ctx) =>
        {
            WorkListItemRef? item;
            try
            {
                item = await JsonSerializer.DeserializeAsync<WorkListItemRef>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[WorkListEndpoints] POST /lists/{name}/items bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }

            if (item is null || string.IsNullOrWhiteSpace(item.Source) || string.IsNullOrWhiteSpace(item.Id))
                return Results.BadRequest(new { error = "body { \"source\": \"...\", \"id\": \"...\", \"area\"?: \"...\" } is required" });

            return store.AppendItem(name, item)
                ? Results.Json(new { name, appended = item })
                : Results.NotFound(new { error = "no such list", name });
        });

        app.MapPatch("/lists/{name}/items", async (string name, HttpContext ctx) =>
        {
            List<WorkListItemRef>? items;
            try
            {
                items = await JsonSerializer.DeserializeAsync<List<WorkListItemRef>>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[WorkListEndpoints] PATCH /lists/{name}/items bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }

            if (items is null)
                return Results.BadRequest(new { error = "body must be a full ordered array of { source, id, area? }" });
            if (items.Any(i => i is null || string.IsNullOrWhiteSpace(i.Source) || string.IsNullOrWhiteSpace(i.Id)))
                return Results.BadRequest(new { error = "every item ref needs a non-empty source and id" });

            return store.Reorder(name, items)
                ? Results.Json(new { name, items })
                : Results.NotFound(new { error = "no such list", name });
        });

        app.MapDelete("/lists/{name}/items/{source}/{id}", (string name, string source, string id) =>
        {
            // The list itself must exist for this to be a 200; a missing list is a 404 (distinct
            // from "list exists but item not found", which is a 200 with removed=false).
            if (store.Get(name) is null)
                return Results.NotFound(new { error = "no such list", name });

            return Results.Json(new { name, source, id, removed = store.RemoveItem(name, source, id) });
        });

        app.MapPost("/lists/{name}/consumer", async (string name, HttpContext ctx) =>
        {
            // The consumer token is optional in the body; the Gateway mints one when omitted so a
            // caller that just wants exclusivity does not have to invent an id.
            CreateConsumerBody? body = null;
            if (ctx.Request.ContentLength is > 0)
                body = await JsonSerializer.DeserializeAsync<CreateConsumerBody>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);

            var token = string.IsNullOrWhiteSpace(body?.Consumer)
                ? Guid.NewGuid().ToString("N")
                : body.Consumer;

            var result = store.Claim(name, token);
            return result switch
            {
                WorkListStore.ClaimResult.Granted => Results.Json(new { name, consumer = token }),
                WorkListStore.ClaimResult.AlreadyClaimed =>
                    Results.Conflict(new { error = "list already has an active consumer", name }),
                WorkListStore.ClaimResult.NoSuchList =>
                    Results.NotFound(new { error = "no such list", name }),
                _ => throw new InvalidOperationException($"unhandled claim result: {result}"),
            };
        });

        app.MapDelete("/lists/{name}/consumer", (string name) =>
            store.Release(name)
                ? Results.Json(new { name, released = true })
                : Results.NotFound(new { error = "no such list", name }));

        FileLog.Write("[WorkListEndpoints] mapped /lists routes");
    }

    private sealed record CreateListBody(string? Name);

    private sealed record CreateConsumerBody(string? Consumer);
}
