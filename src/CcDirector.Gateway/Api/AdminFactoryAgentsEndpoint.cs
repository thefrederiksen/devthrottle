using System.Text.Json.Serialization;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The administrator factory agents switch - the one way to switch the Factory Agents area on or off for ONE
/// account on a Gateway whose machine switch is off (the hosted Gateway, which serves every customer):
///
///   GET  /gateway/admin/factory-agents?account=&lt;id&gt;  -> { account, enabled, machine_wide, decision }
///   POST /gateway/admin/factory-agents  { account, enabled, actor, reason }  -> { outcome, account, enabled }
///
/// Find the account identifier from an email with <c>GET /gateway/admin/accounts?email=...</c>.
///
/// AUTHORIZATION IS THE SAME ADMIN SERVICE TOKEN THE TRIAL, TURN-LOG AND ACCOUNT LOOKUP SURFACES USE, called rather
/// than copied (<see cref="AdminTrialEndpoint.ServiceTokenDenial"/>), so there is one definition of who may act as
/// an administrator here. The account's own settings page cannot write this switch: it is not one of the settings
/// the page offers, so an account cannot switch itself on.
///
/// EVERY WRITE NAMES A PERSON AND A REASON, both required, and the decision is stored with them and the time. The
/// previous decision is written to the Gateway log when it is replaced.
///
/// IT IS A LOOKUP, NOT A DIRECTORY: the read answers about ONE account the caller names, for the reason the account
/// lookup gives - an administrator surface that enumerates the customer base invites being used as one.
///
/// It stores the decision in the account's per-tenant settings, so it needs no new table.
/// </summary>
internal static class AdminFactoryAgentsEndpoint
{
    /// <summary>The route. Exact-match public in <c>AuthMiddleware</c>; the endpoint carries its own gate.</summary>
    public const string Path = "/gateway/admin/factory-agents";

    internal const string OutcomeRecorded = "recorded";
    internal const string OutcomeUnknown = "unknown";

    /// <summary>What the caller sends. Every wire name is spelled out, for the reason <see cref="AdminTurnLogEndpoint"/> gives.</summary>
    internal sealed record SetRequest(
        [property: JsonPropertyName("account")] string? Account,
        [property: JsonPropertyName("enabled")] bool? Enabled,
        [property: JsonPropertyName("actor")] string? Actor,
        [property: JsonPropertyName("reason")] string? Reason);

    public static void Map(IEndpointRouteBuilder app, FactoryAgentsSwitch factorySwitch, TenantRegistry tenants,
        Func<DateTime>? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(factorySwitch);
        ArgumentNullException.ThrowIfNull(tenants);
        var clock = nowUtc ?? (() => DateTime.UtcNow);

        app.MapGet(Path, (HttpContext ctx) =>
        {
            try
            {
                if (AdminTrialEndpoint.ServiceTokenDenial(ctx) is { } gate) return gate;
                return Read(factorySwitch, tenants, ctx.Request.Query["account"].ToString());
            }
            catch (Exception ex)
            {
                FileLog.Write($"[AdminFactoryAgentsEndpoint] GET {Path} FAILED ({ex.GetType().Name}): {ex.Message}");
                return Results.Json(new { outcome = OutcomeUnknown }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.MapPost(Path, async (HttpContext ctx) =>
        {
            try
            {
                // THE GATE COMES FIRST, BEFORE THE BODY IS READ, for the reason the trial surface gives: until it runs
                // the request is an anonymous one off the internet.
                if (AdminTrialEndpoint.ServiceTokenDenial(ctx) is { } gate) return gate;

                SetRequest? body;
                try
                {
                    body = await ctx.Request.ReadFromJsonAsync<SetRequest>(ctx.RequestAborted).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[AdminFactoryAgentsEndpoint] rejected: the request body is not readable JSON ({ex.GetType().Name})");
                    return Results.BadRequest(new { error = "the request body is not readable JSON" });
                }

                return Handle(ctx, body, factorySwitch, tenants, clock());
            }
            catch (Exception ex)
            {
                // UNKNOWN, not a refusal: we do not know whether the decision landed, and an administrator told
                // "denied" would try again.
                FileLog.Write($"[AdminFactoryAgentsEndpoint] POST {Path} FAILED ({ex.GetType().Name}): {ex.Message} - answering UNKNOWN");
                return Results.Json(new { outcome = OutcomeUnknown }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        FileLog.Write($"[AdminFactoryAgentsEndpoint] mapped {Path} (service-token authorized)");
    }

    /// <summary>One account's switch, as the gate will answer it. Internal so it is tested directly.</summary>
    internal static IResult Read(FactoryAgentsSwitch factorySwitch, TenantRegistry tenants, string? account)
    {
        if (string.IsNullOrWhiteSpace(account))
            return Results.BadRequest(new { error = "name one account: ?account=<identifier>. Find it with GET /gateway/admin/accounts?email=..." });
        if (Known(tenants, account) is not { } tenant)
            return Results.NotFound(new { error = UnknownAccount(account) });

        var decision = factorySwitch.Decision(tenant);
        var enabled = factorySwitch.IsOn(tenant);
        FileLog.Write($"[AdminFactoryAgentsEndpoint] GET: account={tenant.ToLogString()} enabled={enabled} machine_wide={factorySwitch.MachineWide} decision={(decision is null ? "none" : decision.Enabled.ToString())}");
        return Results.Json(new
        {
            account = tenant.Value,
            enabled,
            machine_wide = factorySwitch.MachineWide,
            decision,
        });
    }

    /// <summary>Internal so every refusal can be tested directly, without standing a host up per case.</summary>
    internal static IResult Handle(HttpContext ctx, SetRequest? body, FactoryAgentsSwitch factorySwitch,
        TenantRegistry tenants, DateTime nowUtc)
    {
        if (AdminTrialEndpoint.ServiceTokenDenial(ctx) is { } denial) return denial;

        if (body is null)
            return Results.BadRequest(new { error = "a request body is required" });
        // A BLANK ACCOUNT IS NOT "EVERY ACCOUNT". There is no wildcard here at all: the whole-Gateway switch is the
        // machine's config.json, and this route only ever names one account.
        if (string.IsNullOrWhiteSpace(body.Account))
            return Results.BadRequest(new { error = "an account is required. Find it with GET /gateway/admin/accounts?email=..." });
        if (body.Enabled is not { } enabled)
            return Results.BadRequest(new { error = "enabled is required: true to switch factory agents on for this account, false to switch them off" });
        if (string.IsNullOrWhiteSpace(body.Actor))
            return Results.BadRequest(new { error = "an actor is required: a switch decision must record who made it" });
        if (string.IsNullOrWhiteSpace(body.Reason))
            return Results.BadRequest(new { error = "a reason is required: it is where the decision is explained" });
        if (Known(tenants, body.Account) is not { } tenant)
            return Results.BadRequest(new { error = UnknownAccount(body.Account) });

        var decision = factorySwitch.Set(tenant, enabled, body.Actor, body.Reason, nowUtc);
        return Results.Json(new { outcome = OutcomeRecorded, account = tenant.Value, enabled = decision.Enabled });
    }

    // Only an account this Gateway actually has. A mistyped identifier recorded as a decision would name nothing and
    // look like a switch that was thrown.
    private static TenantId? Known(TenantRegistry tenants, string account)
    {
        var match = tenants.ListAll()
            .FirstOrDefault(t => string.Equals(t.TenantId, account.Trim(), StringComparison.OrdinalIgnoreCase));
        return match is null ? null : new TenantId(match.TenantId);
    }

    private static string UnknownAccount(string account)
        => $"no account on this Gateway has the identifier \"{account.Trim()}\". Find it with GET /gateway/admin/accounts?email=...";
}
