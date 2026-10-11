using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Email;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The factory email's switch and its kept reports (Factory Control, step 4):
///
///   GET /gateway/factory-email                     -> FactoryEmailSwitchDto   - is this account sent the factory email
///   PUT /gateway/factory-email  { enabled: bool }  -> FactoryEmailSwitchDto   - switch it on or off
///   GET /gateway/factory-email/reports?kind=&amp;limit=&amp;html=true -> FactoryEmailReportsDto - the kept emails, newest first
///
/// The email ships OFF. A PUT whose body has no boolean <c>enabled</c> is refused, never read as either answer: one guess
/// mails an account that did not ask, the other silences one that did.
/// </summary>
internal static class FactoryEmailEndpoints
{
    public const string SwitchRoute = "/gateway/factory-email";
    public const string ReportsRoute = "/gateway/factory-email/reports";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Map(IEndpointRouteBuilder app, Func<HttpContext, TenantId?> resolveTenant,
        Func<TenantId, bool> isEnabled, Action<TenantId, bool> setEnabled, FactoryEmailStore store)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(isEnabled);
        ArgumentNullException.ThrowIfNull(setEnabled);
        ArgumentNullException.ThrowIfNull(store);

        app.MapGet(SwitchRoute, (HttpContext ctx) => GetSwitch(ctx, resolveTenant, isEnabled));
        app.MapPut(SwitchRoute, async (HttpContext ctx) => await PutSwitchAsync(ctx, resolveTenant, setEnabled));
        app.MapGet(ReportsRoute, (HttpContext ctx, string? kind, int? limit, bool? html) =>
            ListReports(ctx, kind, limit, html, resolveTenant, store));

        FileLog.Write($"[FactoryEmailEndpoints] mapped {SwitchRoute} (GET, PUT), {ReportsRoute}");
    }

    /// <summary>GET /gateway/factory-email: is this account sent the factory email.</summary>
    internal static IResult GetSwitch(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant, Func<TenantId, bool> isEnabled)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount(SwitchRoute);
        return Results.Json(new FactoryEmailSwitchDto { Enabled = isEnabled(tenant) });
    }

    /// <summary>PUT /gateway/factory-email: switch it on or off. A body without a boolean "enabled" is refused.</summary>
    internal static async Task<IResult> PutSwitchAsync(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant,
        Action<TenantId, bool> setEnabled)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount(SwitchRoute);
        try
        {
            var body = await JsonSerializer.DeserializeAsync<SwitchBody>(ctx.Request.Body, Json, ctx.RequestAborted);
            if (body?.Enabled is null)
                return Results.Json(new { error = "body { \"enabled\": true|false } is required" }, statusCode: StatusCodes.Status400BadRequest);
            setEnabled(tenant, body.Enabled.Value);
            FileLog.Write($"[FactoryEmailEndpoints] factory email set to {body.Enabled.Value} for tenant={tenant.ToLogString()}");
            return Results.Json(new FactoryEmailSwitchDto { Enabled = body.Enabled.Value });
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[FactoryEmailEndpoints] PUT {SwitchRoute} bad JSON: {ex.Message}");
            return Results.Json(new { error = "invalid JSON" }, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>GET /gateway/factory-email/reports: the account's kept factory emails, newest first.</summary>
    internal static IResult ListReports(HttpContext ctx, string? kind, int? limit, bool? html,
        Func<HttpContext, TenantId?> resolveTenant, FactoryEmailStore store)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount(ReportsRoute);
        var wanted = string.IsNullOrWhiteSpace(kind) ? null : kind.Trim().ToLowerInvariant();
        if (wanted is not (null or FactoryEmailKinds.Morning or FactoryEmailKinds.Check))
            return Results.Json(new { error = $"'{kind}' is not a factory email kind. Use '{FactoryEmailKinds.Morning}' or '{FactoryEmailKinds.Check}'." },
                statusCode: StatusCodes.Status400BadRequest);
        if (limit is < 1 or > FactoryEmailStore.MaxList)
            return Results.Json(new { error = $"limit must be between 1 and {FactoryEmailStore.MaxList}." },
                statusCode: StatusCodes.Status400BadRequest);
        var list = store.List(tenant, wanted, limit ?? 20, html ?? false);
        FileLog.Write($"[FactoryEmailEndpoints] GET {ReportsRoute}: tenant={tenant.ToLogString()}, kind={wanted ?? "-"}, count={list.Count}");
        return Results.Json(list);
    }

    private static IResult NoAccount(string route)
    {
        FileLog.Write($"[FactoryEmailEndpoints] {route}: REFUSED - no account for the caller");
        return Results.Json(new { error = "no account for this caller" }, statusCode: StatusCodes.Status403Forbidden);
    }

    private sealed record SwitchBody(bool? Enabled);
}
