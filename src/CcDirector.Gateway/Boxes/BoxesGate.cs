using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Boxes;

/// <summary>
/// The one gate in front of every Boxes route (the Factory Boxes mission, phase 1). For an account Boxes is not on for,
/// and for a request bound to no account, it answers exactly what an unmapped route answers - 404 with no body - so an
/// account that is off cannot tell Boxes exists. A copy of <c>Api.MailDeskGate</c>.
///
/// Every Boxes route is mapped INTO the group this returns - the list now, the desktop relay next - so a route added
/// later is gated by construction rather than by remembering to call a check.
/// </summary>
internal static class BoxesGate
{
    /// <summary>A route group whose every endpoint refuses with 404 unless Boxes is on for the calling account.</summary>
    public static RouteGroupBuilder Group(IEndpointRouteBuilder app, BoxesSwitch boxes, Func<HttpContext, TenantId?> resolveTenant)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        ArgumentNullException.ThrowIfNull(resolveTenant);

        var group = app.MapGroup(string.Empty);
        group.AddEndpointFilter(async (invocation, next) =>
        {
            var ctx = invocation.HttpContext;
            if (!IsOnFor(ctx, boxes, resolveTenant, out var why))
            {
                FileLog.Write($"[BoxesGate] REFUSED {ctx.Request.Method} {ctx.Request.Path}: {why} - answering 404");
                return Results.NotFound();
            }
            return await next(invocation);
        });
        return group;
    }

    /// <summary>Whether the calling account may reach Boxes. Internal so it is tested directly.</summary>
    internal static bool IsOnFor(HttpContext ctx, BoxesSwitch boxes, Func<HttpContext, TenantId?> resolveTenant, out string why)
    {
        if (resolveTenant(ctx) is not { } tenant)
        {
            why = "no account is bound to this request";
            return false;
        }
        if (!boxes.IsOn(tenant))
        {
            why = $"Boxes is off for account {tenant.ToLogString()}";
            return false;
        }
        why = $"on for account {tenant.ToLogString()}";
        return true;
    }
}
