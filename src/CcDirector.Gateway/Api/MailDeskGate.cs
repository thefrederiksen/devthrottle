using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.MailDesk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The one gate in front of every Mail Desk route (the Mail Desk mission, issue #4213, section 5.10). It answers
/// exactly what an unmapped route answers - 404 with no body - for an account Mail Desk is not switched on for, and
/// for a request bound to no account, so an account that is off cannot tell Mail Desk exists.
///
/// Every Mail Desk route is mapped INTO the group this returns, so a route added later is gated by construction
/// rather than by remembering to call a check. A copy of <see cref="FactoryAgentsGate"/>, with no machine switch.
///
/// The gate is SEPARATE from the factory agents gate on purpose: the owner's account has factory agents on, and
/// that must not let Mail Desk through.
/// </summary>
internal static class MailDeskGate
{
    /// <summary>The prefix every Mail Desk route sits under.</summary>
    public const string Prefix = "/gateway/mail-desk";

    /// <summary>A route group whose every endpoint refuses with 404 unless Mail Desk is on for the calling account.</summary>
    public static RouteGroupBuilder Group(IEndpointRouteBuilder app, MailDeskSwitch mailDeskSwitch,
        Func<HttpContext, TenantId?> resolveTenant)
    {
        ArgumentNullException.ThrowIfNull(mailDeskSwitch);
        ArgumentNullException.ThrowIfNull(resolveTenant);

        var group = app.MapGroup(string.Empty);
        group.AddEndpointFilter(async (invocation, next) =>
        {
            var ctx = invocation.HttpContext;
            if (!IsOnFor(ctx, mailDeskSwitch, resolveTenant, out var why))
            {
                FileLog.Write($"[MailDeskGate] REFUSED {ctx.Request.Method} {ctx.Request.Path}: {why} - answering 404");
                return Results.NotFound();
            }
            return await next(invocation);
        });
        return group;
    }

    /// <summary>Whether the calling account may reach Mail Desk. Internal so it is tested directly.</summary>
    internal static bool IsOnFor(HttpContext ctx, MailDeskSwitch mailDeskSwitch,
        Func<HttpContext, TenantId?> resolveTenant, out string why)
    {
        if (resolveTenant(ctx) is not { } tenant)
        {
            why = "no account is bound to this request";
            return false;
        }
        if (!mailDeskSwitch.IsOn(tenant))
        {
            why = $"Mail Desk is off for account {tenant.ToLogString()}";
            return false;
        }
        why = $"on for account {tenant.ToLogString()}";
        return true;
    }
}
