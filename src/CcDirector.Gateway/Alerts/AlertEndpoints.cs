using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Alerts;

/// <summary>
/// THE ALERTS ROUTES (the Alerts mission, section 5.2): one list of everything that needs the owner, and the two
/// things he does to it here - mark it seen, and clear what no longer needs him.
///
/// <list type="bullet">
/// <item><c>GET /gateway/alerts</c> - <c>{ alerts, needsAnswerCount }</c>.</item>
/// <item><c>POST /gateway/alerts/seen</c> <c>{ ids }</c> - mark those alerts seen at their current version.</item>
/// <item><c>POST /gateway/alerts/{id}/clear</c> - clear one; refused 409 while it still needs an answer.</item>
/// <item><c>POST /gateway/alerts/clear</c> - clear every alert that does not need an answer.</item>
/// </list>
///
/// THE OWNER'S, NOT A SESSION'S. None of these is on <see cref="Util.SessionKeyGuard"/>'s allow list, so a session key
/// is refused all four, the way it is refused the owner's list of message requests: an alert is the owner's view of
/// requests sessions made, and a session never marks or clears the owner's alerts. Answering is not here at all - the
/// detail view uses each feature's own answer route, which keeps every check it has.
///
/// TENANT CONFINEMENT: the account is resolved from the authenticated credential through the same resolver every
/// <c>/gateway/*</c> route uses, and every source and the marks are read inside it, so another account's alert id is a
/// 404 here, not an alert.
/// </summary>
public static class AlertEndpoints
{
    public const string ListRoute = "/gateway/alerts";
    public const string SeenRoute = "/gateway/alerts/seen";
    public const string ClearAllRoute = "/gateway/alerts/clear";
    public const string ClearOneRoute = "/gateway/alerts/{id}/clear";

    private static readonly JsonSerializerOptions BodyJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The most ids one seen call may carry - more than any tray shows.</summary>
    public const int MaxSeenIds = 500;

    public static void Map(IEndpointRouteBuilder app, Func<HttpContext, TenantId?> resolveTenant, AlertsService alerts)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(alerts);

        app.MapGet(ListRoute, (HttpContext ctx) => List(ctx, resolveTenant, alerts));
        // The return types are stated on purpose: an async lambda whose only parameter is HttpContext also fits
        // RequestDelegate, which would throw the IResult away and answer an empty 200.
        app.MapPost(SeenRoute, async Task<IResult> (HttpContext ctx) => await SeenAsync(ctx, resolveTenant, alerts));
        app.MapPost(ClearAllRoute, (HttpContext ctx) => ClearAll(ctx, resolveTenant, alerts));
        app.MapPost(ClearOneRoute, (HttpContext ctx, string id) => ClearOne(ctx, id, resolveTenant, alerts));
        FileLog.Write($"[AlertEndpoints] mapped GET {ListRoute}, POST {SeenRoute}, POST {ClearAllRoute}, POST {ClearOneRoute}");
    }

    internal static IResult List(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant, AlertsService alerts)
    {
        FileLog.Write("[AlertEndpoints] GET alerts");
        try
        {
            if (resolveTenant(ctx) is not { } tenant) return NoAccount();
            return Results.Json(alerts.List(tenant));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AlertEndpoints] GET {ListRoute} FAILED: {ex.Message}");
            throw;
        }
    }

    internal static async Task<IResult> SeenAsync(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant, AlertsService alerts)
    {
        FileLog.Write("[AlertEndpoints] POST seen");
        try
        {
            if (resolveTenant(ctx) is not { } tenant) return NoAccount();
            AlertSeenRequest? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<AlertSeenRequest>(ctx.Request.Body, BodyJsonOptions, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                return Refuse(StatusCodes.Status400BadRequest, "invalid_body", $"The body is not valid JSON: {ex.Message}");
            }
            if (body?.Ids is null)
                return Refuse(StatusCodes.Status400BadRequest, "invalid_body", "A body is required: { ids: [alert ids] }.");
            if (body.Ids.Count > MaxSeenIds)
                return Refuse(StatusCodes.Status400BadRequest, "too_many_ids", $"At most {MaxSeenIds} ids may be marked at once.");
            if (body.Ids.Any(id => !AlertsService.IsWellFormedId(id)))
                return MalformedId(body.Ids.First(id => !AlertsService.IsWellFormedId(id)));
            var marked = alerts.MarkSeen(tenant, body.Ids);
            return Results.Json(new AlertSeenResponse { Marked = marked });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AlertEndpoints] POST {SeenRoute} FAILED: {ex.Message}");
            throw;
        }
    }

    internal static IResult ClearOne(HttpContext ctx, string id, Func<HttpContext, TenantId?> resolveTenant, AlertsService alerts)
    {
        FileLog.Write($"[AlertEndpoints] POST clear: id={id}");
        try
        {
            if (resolveTenant(ctx) is not { } tenant) return NoAccount();
            if (!AlertsService.IsWellFormedId(id)) return MalformedId(id);
            return alerts.Clear(tenant, id) switch
            {
                AlertClearOutcome.Cleared => Results.Json(new AlertClearResponse { Cleared = 1 }),
                AlertClearOutcome.NeedsAnswer => Refuse(StatusCodes.Status409Conflict, "needs_answer", AlertsService.NeedsAnswerSentence),
                AlertClearOutcome.NotFound => Refuse(StatusCodes.Status404NotFound, "alert_not_found", $"No alert {id} is current in this account."),
                var other => throw new InvalidOperationException($"clear answered {other}, which this route does not handle"),
            };
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AlertEndpoints] POST {ClearOneRoute} FAILED: {ex.Message}");
            throw;
        }
    }

    internal static IResult ClearAll(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant, AlertsService alerts)
    {
        FileLog.Write("[AlertEndpoints] POST clear all");
        try
        {
            if (resolveTenant(ctx) is not { } tenant) return NoAccount();
            return Results.Json(new AlertClearResponse { Cleared = alerts.ClearAll(tenant) });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AlertEndpoints] POST {ClearAllRoute} FAILED: {ex.Message}");
            throw;
        }
    }

    private static IResult MalformedId(string? id) => Refuse(StatusCodes.Status400BadRequest, "invalid_alert_id",
        $"'{id}' is not an alert id. An alert id is '<kind>:<id>', where the kind is one of {string.Join(", ", AlertKinds.All)}.");

    private static IResult NoAccount() => Refuse(StatusCodes.Status403Forbidden, "no_account", "no account is bound to this request");

    private static IResult Refuse(int status, string code, string sentence)
        => Results.Json(new { code, error = sentence }, statusCode: status);
}
