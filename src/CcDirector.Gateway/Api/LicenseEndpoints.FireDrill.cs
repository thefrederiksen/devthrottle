using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// THE DAILY LICENSE FIRE DRILL (mission "Licenses - Always Ready, Loud When Not", step 7):
/// <list type="bullet">
/// <item><c>GET /gateway/licenses/fire-drill</c> - the drill machine and the drill's newest line.</item>
/// <item><c>PUT /gateway/licenses/fire-drill/machine</c> - choose the drill machine, or clear it. The owner's own.</item>
/// <item><c>POST /gateway/licenses/fire-drill/run</c> - run the drill now. The owner's own: it switches a machine.</item>
/// </list>
/// Every route answers for the caller's own account and no other. None is in the session key guard's list.
/// </summary>
public static partial class LicenseEndpoints
{
    public static void MapFireDrill(IEndpointRouteBuilder app, Func<HttpContext, TenantId?> resolveTenant,
        Func<TenantId?> tenantInScope, LicenseFireDrillService drills)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(tenantInScope);
        ArgumentNullException.ThrowIfNull(drills);

        static IResult? OwnerOnly(HttpContext ctx, string what)
        {
            FleetManagerOwnerDevice.Require(ctx, what,
                $"a session may not {what}; that is the owner's own action, from the Licenses page of the Cockpit",
                "LicenseEndpoints", out var refusal);
            return refusal;
        }

        app.MapGet(LicenseFireDrillRoutes.FireDrill, (HttpContext ctx) =>
        {
            FileLog.Write("[LicenseEndpoints] GET fire-drill");
            return resolveTenant(ctx) is { } tenant ? Results.Json(FireDrillAnswer(tenant, drills)) : NoAccount();
        });
        app.MapPut(LicenseFireDrillRoutes.Machine, async (HttpContext ctx) =>
        {
            const string what = "choose the machine of the license fire drill";
            if ((OwnerOnly(ctx, what) ?? NotJson(ctx)) is { } refusedBeforeTheBody)
            {
                await refusedBeforeTheBody.ExecuteAsync(ctx);
                return;
            }
            var body = ParseBody<LicenseFireDrillMachineRequest>(await ReadBodyTextAsync(ctx));
            await (await SetFireDrillMachineAsync(ctx, resolveTenant, drills, body)).ExecuteAsync(ctx);
        });
        app.MapPost(LicenseFireDrillRoutes.Run, async (HttpContext ctx) =>
        {
            const string what = "run the license fire drill";
            if (OwnerOnly(ctx, what) is { } refused)
            {
                await refused.ExecuteAsync(ctx);
                return;
            }
            await (await RunFireDrillAsync(ctx, resolveTenant, tenantInScope, drills)).ExecuteAsync(ctx);
        });
        FileLog.Write($"[LicenseEndpoints] mapped GET {LicenseFireDrillRoutes.FireDrill}, PUT {LicenseFireDrillRoutes.Machine}, POST {LicenseFireDrillRoutes.Run}");
    }

    internal static LicenseFireDrillResponse FireDrillAnswer(TenantId tenant, LicenseFireDrillService drills) => new()
    {
        MachineId = drills.DrillMachine(tenant)?.ToString("N"),
        Line = drills.ReportLine(tenant),
    };

    internal static async Task<IResult> SetFireDrillMachineAsync(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant,
        LicenseFireDrillService drills, LicenseFireDrillMachineRequest? body)
    {
        FileLog.Write("[LicenseEndpoints] PUT fire-drill machine");
        try
        {
            if (resolveTenant(ctx) is not { } tenant)
                return NoAccount();
            if (body is null)
                return Refuse(StatusCodes.Status400BadRequest, "invalid_body", "The body is a JSON object with machineId: a machine's identifier, or null.");
            Guid? machine = null;
            if (body.MachineId is { } text)
            {
                if (!Guid.TryParse(text, out var id))
                    return Refuse(StatusCodes.Status400BadRequest, "invalid_machine", "machineId is a machine's identifier as the Licenses page gives it, or null.");
                machine = id;
            }
            if (await drills.SetDrillMachineAsync(tenant, machine, ctx.RequestAborted) is { } why)
                return Refuse(StatusCodes.Status409Conflict, "unknown_machine", why);
            return Results.Json(FireDrillAnswer(tenant, drills));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseEndpoints] PUT /gateway/licenses/fire-drill/machine FAILED: {ex.Message}");
            throw;
        }
    }

    internal static async Task<IResult> RunFireDrillAsync(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant,
        Func<TenantId?> tenantInScope, LicenseFireDrillService drills)
    {
        FileLog.Write("[LicenseEndpoints] POST fire-drill run");
        try
        {
            if (resolveTenant(ctx) is not { } tenant)
                return NoAccount();
            if (drills.DrillMachine(tenant) is null)
                return Refuse(StatusCodes.Status409Conflict, "no_drill_machine", "No drill machine is chosen, so there is no drill to run.");
            // The drill's switches are SENT in the scope in effect; a scope that is not the caller's would write rows
            // and send nothing, so that is refused before anything is written.
            if (tenantInScope() is not { } scope || scope != tenant)
                throw new InvalidOperationException("The fire drill was run outside the scope of the caller's own account; nothing was written or sent.");
            if (await drills.RunAsync(tenant, ctx.RequestAborted) is null)
                return Refuse(StatusCodes.Status409Conflict, "drill_not_run", "The drill did not run: one is already running, or no machine of this account reports.");
            return Results.Json(FireDrillAnswer(tenant, drills));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseEndpoints] POST /gateway/licenses/fire-drill/run FAILED: {ex.Message}");
            throw;
        }
    }
}
