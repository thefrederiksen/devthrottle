using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.SessionFilters;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The saved session filters on the caller's account (issue #4009), read and written by the Cockpit's and the phone's
/// Sessions filter panel:
///
///   GET    /gateway/session-filters       -> { filters: [filter], colours: [{ key, label, hex }] }
///   POST   /gateway/session-filters       body { name, colour, repos[], machines[] } -> 201 filter
///   PUT    /gateway/session-filters/{id}  same body -> 200 filter
///   DELETE /gateway/session-filters/{id}  -> 204
///
/// A filter is <c>{ id, name, colour, colourHex, repos[], machines[], createdAtUtc, updatedAtUtc }</c>. The colour's hex
/// is the Gateway's, so every client paints the same colour for the same key. A refusal is
/// <c>{ error: "&lt;sentence&gt;" }</c>: 400 for an invalid filter, 404 for an unknown id, 409 for a name already used and
/// for an account that holds the most filters it may.
///
/// Each route resolves the caller's account with <see cref="GatewayEndpoints.ResolveReadTenant"/> and answers 403 when
/// none resolves, never the Local partition. These routes are not on the session key allow list
/// (<see cref="SessionKeyGuard"/>), so a session cannot read or change its owner's filters: they belong to the person.
/// </summary>
internal static class SavedSessionFilterEndpoints
{
    /// <summary>The route the filters are listed and created on.</summary>
    internal const string Route = "/gateway/session-filters";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Map the four routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <param name="store">The saved filter store.</param>
    /// <param name="boundary">The tenant boundary every request's account is resolved through. Required.</param>
    public static void Map(IEndpointRouteBuilder app, SavedSessionFilterStore store, Tenancy.HostedTenantBoundary boundary)
    {
        FileLog.Write($"[SavedSessionFilterEndpoints] mapping GET/POST {Route} and PUT/DELETE {Route}/{{id}}");

        app.MapGet(Route, (HttpContext ctx) => HandleList(ctx, store, boundary));
        app.MapPost(Route, async (HttpContext ctx) => await HandleCreateAsync(ctx, store, boundary, DateTime.UtcNow));
        app.MapPut(Route + "/{id}", async (HttpContext ctx, string id) => await HandleUpdateAsync(ctx, store, boundary, id, DateTime.UtcNow));
        app.MapDelete(Route + "/{id}", (HttpContext ctx, string id) => HandleDelete(ctx, store, boundary, id));
    }

    internal static IResult HandleList(HttpContext ctx, SavedSessionFilterStore store, Tenancy.HostedTenantBoundary? boundary)
    {
        var t = GatewayEndpoints.ResolveReadTenant(ctx, boundary);
        if (t is null) return TenantRequired();
        var filters = store.List(t.Value);
        return Results.Json(new
        {
            filters = filters.Select(ToWire),
            colours = SavedSessionFilterStore.Colours.Select(c => new { key = c.Key, label = c.Label, hex = c.Hex }),
        });
    }

    internal static async Task<IResult> HandleCreateAsync(HttpContext ctx, SavedSessionFilterStore store, Tenancy.HostedTenantBoundary? boundary, DateTime nowUtc)
    {
        var t = GatewayEndpoints.ResolveReadTenant(ctx, boundary);
        if (t is null) return TenantRequired();
        var (input, bad) = await ReadBodyAsync(ctx);
        if (input is null) return bad!;
        return ToResult(store.Create(t.Value, input, nowUtc), StatusCodes.Status201Created);
    }

    internal static async Task<IResult> HandleUpdateAsync(HttpContext ctx, SavedSessionFilterStore store, Tenancy.HostedTenantBoundary? boundary, string id, DateTime nowUtc)
    {
        var t = GatewayEndpoints.ResolveReadTenant(ctx, boundary);
        if (t is null) return TenantRequired();
        var (input, bad) = await ReadBodyAsync(ctx);
        if (input is null) return bad!;
        return ToResult(store.Update(t.Value, id, input, nowUtc), StatusCodes.Status200OK);
    }

    internal static IResult HandleDelete(HttpContext ctx, SavedSessionFilterStore store, Tenancy.HostedTenantBoundary? boundary, string id)
    {
        var t = GatewayEndpoints.ResolveReadTenant(ctx, boundary);
        if (t is null) return TenantRequired();
        return store.Delete(t.Value, id)
            ? Results.NoContent()
            : Results.Json(new { error = "That saved filter no longer exists." }, statusCode: StatusCodes.Status404NotFound);
    }

    private sealed record FilterBody(string? Name, string? Colour, List<string>? Repos, List<string>? Machines);

    private static async Task<(SavedSessionFilterInput? Input, IResult? Bad)> ReadBodyAsync(HttpContext ctx)
    {
        try
        {
            var body = await JsonSerializer.DeserializeAsync<FilterBody>(ctx.Request.Body, JsonOpts, ctx.RequestAborted);
            if (body is null)
                return (null, Results.BadRequest(new { error = "body { \"name\", \"colour\", \"repos\": [], \"machines\": [] } is required" }));
            return (new SavedSessionFilterInput(body.Name, body.Colour, body.Repos, body.Machines), null);
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[SavedSessionFilterEndpoints] bad JSON: {ex.Message}");
            return (null, Results.BadRequest(new { error = "invalid JSON" }));
        }
    }

    private static IResult ToResult(SavedSessionFilterResult result, int savedStatus) => result.Outcome switch
    {
        SavedSessionFilterOutcome.Saved => Results.Json(ToWire(result.Filter!), statusCode: savedStatus),
        SavedSessionFilterOutcome.NotFound => Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status404NotFound),
        SavedSessionFilterOutcome.NameTaken => Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status409Conflict),
        SavedSessionFilterOutcome.TooMany => Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status409Conflict),
        SavedSessionFilterOutcome.Invalid => Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status400BadRequest),
        _ => throw new InvalidOperationException($"unhandled saved filter outcome {result.Outcome}"),
    };

    private static object ToWire(SavedSessionFilter f) => new
    {
        id = f.Id,
        name = f.Name,
        colour = f.Colour,
        colourHex = f.ColourHex,
        repos = f.Repos,
        machines = f.Machines,
        createdAtUtc = f.CreatedAtUtc,
        updatedAtUtc = f.UpdatedAtUtc,
    };

    private static IResult TenantRequired()
        => Results.Json(new { error = "a tenant could not be resolved for this request" }, statusCode: StatusCodes.Status403Forbidden);
}
