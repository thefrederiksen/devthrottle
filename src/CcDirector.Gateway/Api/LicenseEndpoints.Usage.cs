using System.Globalization;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// WHAT IS USING THE LICENSE (the usage-by-session mandate of 2026-10-20):
/// <list type="bullet">
/// <item><c>GET /gateway/licenses/usage</c> - which sessions use one Claude account, how fast and how much so far, in
/// percent of its weekly limit. A read: in the session key guard's list, so an agent - the Fleet Manager first - can
/// answer "what is using it?" without the owner.</item>
/// <item><c>PUT /gateway/licenses/accounts/price</c> - set or clear what an account's subscription costs a month. A
/// write, and the owner's own, like the renewal date.</item>
/// </list>
/// </summary>
public static partial class LicenseEndpoints
{
    /// <summary>The highest monthly price accepted, in dollars: far above any plan, and a ceiling on a typing slip.</summary>
    public const double MaxMonthlyPriceUsd = 100_000;

    public static void MapUsage(IEndpointRouteBuilder app, Func<HttpContext, TenantId?> resolveTenant,
        SessionUsageService usage, LicenseStore store, Func<DateTime> nowUtc)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(nowUtc);

        static IResult? OwnerOnly(HttpContext ctx, string what)
        {
            FleetManagerOwnerDevice.Require(ctx, what,
                $"a session may not {what}; that is the owner's own action, from the Licenses page of the Cockpit",
                "LicenseEndpoints", out var refusal);
            return refusal;
        }

        app.MapGet(LicenseUsageRoutes.Usage, (HttpContext ctx) => Usage(ctx, resolveTenant, usage, nowUtc));
        app.MapPut(LicenseUsageRoutes.AccountPrice, async (HttpContext ctx) =>
        {
            const string what = "set what a subscription costs";
            if ((OwnerOnly(ctx, what) ?? NotJson(ctx)) is { } refusedBeforeTheBody)
            {
                await refusedBeforeTheBody.ExecuteAsync(ctx);
                return;
            }
            var text = await ReadBodyTextAsync(ctx);
            await SetMonthlyPrice(ctx, c => OwnerOnly(c, what), resolveTenant, store, ParseBody<LicensePriceRequest>(text), PriceGiven(text)).ExecuteAsync(ctx);
        });
        FileLog.Write($"[LicenseEndpoints] mapped GET {LicenseUsageRoutes.Usage}, PUT {LicenseUsageRoutes.AccountPrice}");
    }

    internal static IResult Usage(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant, SessionUsageService usage, Func<DateTime> nowUtc)
    {
        FileLog.Write("[LicenseEndpoints] GET usage");
        try
        {
            if (resolveTenant(ctx) is not { } tenant)
                return NoAccount();
            var account = ctx.Request.Query["account"].ToString();
            if (account.Length > 0 && NotAnAccountEmail(account))
                return InvalidAccount("account");
            if (!TryFlag(ctx.Request.Query["closed"].ToString(), out var includeGone))
                return Refuse(StatusCodes.Status400BadRequest, "invalid_closed", "closed is true or false.");
            var response = usage.Read(tenant, account.Length == 0 ? null : account, includeGone, nowUtc());
            if (response is null)
                return Refuse(StatusCodes.Status404NotFound, "unknown_account", "No machine of this DevThrottle account holds that Claude account.");
            FileLog.Write($"[LicenseEndpoints] GET usage: tenant={tenant.ToLogString()}, sessions={response.Sessions.Count}");
            return Results.Json(response);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseEndpoints] GET /gateway/licenses/usage FAILED: {ex.Message}");
            throw;
        }
    }

    internal static IResult SetMonthlyPrice(HttpContext ctx, Func<HttpContext, IResult?> ownerOnly,
        Func<HttpContext, TenantId?> resolveTenant, LicenseStore store, LicensePriceRequest? body, bool priceGiven)
    {
        FileLog.Write("[LicenseEndpoints] PUT monthly price");
        try
        {
            if (ownerOnly(ctx) is { } notTheOwner)
                return notTheOwner;
            if (resolveTenant(ctx) is not { } tenant)
                return NoAccount();
            if (body is null || !priceGiven)
                return Refuse(StatusCodes.Status400BadRequest, "invalid_body",
                    "The body is a JSON object with accountEmail and monthlyPriceUsd. monthlyPriceUsd must be given: a number, or null to clear it.");
            if (string.IsNullOrWhiteSpace(body.AccountEmail))
                return Refuse(StatusCodes.Status400BadRequest, "account_required", "accountEmail is required.");
            if (NotAnAccountEmail(body.AccountEmail))
                return InvalidAccount("accountEmail");
            if (body.MonthlyPriceUsd is { } usd && (double.IsNaN(usd) || usd < 0 || usd > MaxMonthlyPriceUsd))
                return Refuse(StatusCodes.Status400BadRequest, "invalid_price",
                    $"monthlyPriceUsd is a number of dollars from 0 to {MaxMonthlyPriceUsd.ToString(CultureInfo.InvariantCulture)}, or null to clear it.");

            long? cents = body.MonthlyPriceUsd is { } dollars ? (long)Math.Round(dollars * 100, MidpointRounding.AwayFromZero) : null;
            if (!store.SetMonthlyPrice(tenant, body.AccountEmail, cents))
                return Refuse(StatusCodes.Status404NotFound, "unknown_account", "No machine of this DevThrottle account holds that Claude account.");
            FileLog.Write($"[LicenseEndpoints] PUT monthly price: tenant={tenant.ToLogString()}, set={(cents is null ? "cleared" : "a price")}");
            return Results.Json(new LicensePriceResponse
            {
                AccountEmail = LicenseStore.NormalizeEmail(body.AccountEmail),
                MonthlyPriceUsd = cents / 100.0,
            });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseEndpoints] PUT /gateway/licenses/accounts/price FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>Whether the body NAMED <c>monthlyPriceUsd</c>: the typed body alone cannot tell it left out from null.</summary>
    internal static bool PriceGiven(string json)
    {
        if (ParseBody<LicensePriceRequest>(json) is null)
            return false;
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject()
            .Any(p => string.Equals(p.Name, "monthlyPriceUsd", StringComparison.OrdinalIgnoreCase));
    }

    // An empty value is "not given", which is false; otherwise exactly true or false, whatever the capitals.
    private static bool TryFlag(string value, out bool flag)
    {
        flag = false;
        if (value.Length == 0)
            return true;
        return bool.TryParse(value, out flag);
    }
}
