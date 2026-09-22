using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Factory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The one gate in front of every factory route: the activity record, the triggers (the owner's AND the Director's
/// half), and the owner's Factory Agents pages. It answers exactly what an unmapped route answers - 404 with no
/// body - for an account the switch is not on for, so an account that is off cannot tell the area exists, and a
/// Director of that account is handed no checks to run.
///
/// Every factory route is mapped INTO the group this returns, so a route added later is gated by construction
/// rather than by remembering to call a check. The switch question itself (<c>/gateway/factory-agents/switch</c>)
/// is NOT behind the gate - it is how the Cockpit learns the answer.
/// </summary>
internal static class FactoryAgentsGate
{
    /// <summary>A route group whose every endpoint refuses with 404 unless the switch is on for the calling account.</summary>
    public static RouteGroupBuilder Group(IEndpointRouteBuilder app, FactoryAgentsSwitch factorySwitch,
        Func<HttpContext, TenantId?> resolveTenant)
    {
        ArgumentNullException.ThrowIfNull(factorySwitch);
        ArgumentNullException.ThrowIfNull(resolveTenant);

        var group = app.MapGroup(string.Empty);
        group.AddEndpointFilter(async (invocation, next) =>
        {
            var ctx = invocation.HttpContext;
            if (!IsOnFor(ctx, factorySwitch, resolveTenant, out var why))
            {
                FileLog.Write($"[FactoryAgentsGate] REFUSED {ctx.Request.Method} {ctx.Request.Path}: {why} - answering 404");
                return Results.NotFound();
            }
            return await next(invocation);
        });
        return group;
    }

    /// <summary>Whether the calling account may reach the factory surface. Internal so it is tested directly.</summary>
    internal static bool IsOnFor(HttpContext ctx, FactoryAgentsSwitch factorySwitch,
        Func<HttpContext, TenantId?> resolveTenant, out string why)
    {
        if (factorySwitch.MachineWide)
        {
            why = "on for this machine";
            return true;
        }
        if (resolveTenant(ctx) is not { } tenant)
        {
            why = "no account is bound to this request";
            return false;
        }
        if (!factorySwitch.IsOn(tenant))
        {
            why = $"factory agents are off for account {tenant.ToLogString()}";
            return false;
        }
        why = $"on for account {tenant.ToLogString()}";
        return true;
    }
}
