using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// THE DAILY LICENSE CHECK AND THE OWNER'S TEST SWITCH (mission "Licenses - Always Ready, Loud When Not"):
/// <list type="bullet">
/// <item><c>GET /gateway/licenses/fire-drill</c> - the daily license check's newest line.</item>
/// <item><c>GET /gateway/licenses/test-usage</c> - whether the 82% test switch is on, and for which machine.</item>
/// <item><c>PUT /gateway/licenses/test-usage</c> - turn it on for one machine. The owner's own.</item>
/// <item><c>DELETE /gateway/licenses/test-usage</c> - turn it off. The owner's own.</item>
/// </list>
/// Every route answers for the caller's own account and no other. None is in the session key guard's list. The drill's
/// old routes that chose a drill machine and ran a live switch out and back are gone (the owner, 7 November 2026).
/// </summary>
public static partial class LicenseEndpoints
{
    public static void MapFireDrill(IEndpointRouteBuilder app, Func<HttpContext, TenantId?> resolveTenant,
        LicenseFireDrillService drills, LicenseTestUsage testUsage, LicenseSnapshotSource snapshots, Func<DateTime> nowUtc)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(drills);
        ArgumentNullException.ThrowIfNull(testUsage);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(nowUtc);

        static IResult? OwnerOnly(HttpContext ctx, string what)
        {
            FleetManagerOwnerDevice.Require(ctx, what,
                $"a session may not {what}; that is the owner's own action, from the Cockpit",
                "LicenseEndpoints", out var refusal);
            return refusal;
        }

        app.MapGet(LicenseFireDrillRoutes.FireDrill, (HttpContext ctx) =>
        {
            FileLog.Write("[LicenseEndpoints] GET fire-drill");
            return resolveTenant(ctx) is { } tenant ? Results.Json(new LicenseFireDrillResponse { Line = drills.ReportLine(tenant) }) : NoAccount();
        });
        app.MapGet(LicenseTestUsageRoutes.TestUsage, (HttpContext ctx) =>
        {
            FileLog.Write("[LicenseEndpoints] GET test-usage");
            return resolveTenant(ctx) is { } tenant ? Results.Json(TestUsageAnswer(tenant, testUsage, snapshots, nowUtc())) : NoAccount();
        });
        app.MapPut(LicenseTestUsageRoutes.TestUsage, async (HttpContext ctx) =>
        {
            const string what = "turn on the license test switch";
            if ((OwnerOnly(ctx, what) ?? NotJson(ctx)) is { } refusedBeforeTheBody)
            {
                await refusedBeforeTheBody.ExecuteAsync(ctx);
                return;
            }
            var body = ParseBody<LicenseTestUsageRequest>(await ReadBodyTextAsync(ctx));
            await TurnOnTestUsage(ctx, c => OwnerOnly(c, what), resolveTenant, testUsage, snapshots, nowUtc, body).ExecuteAsync(ctx);
        });
        app.MapDelete(LicenseTestUsageRoutes.TestUsage, async (HttpContext ctx) =>
        {
            const string what = "turn off the license test switch";
            await TurnOffTestUsage(ctx, c => OwnerOnly(c, what), resolveTenant, testUsage, snapshots, nowUtc).ExecuteAsync(ctx);
        });
        FileLog.Write($"[LicenseEndpoints] mapped GET {LicenseFireDrillRoutes.FireDrill}, GET PUT DELETE {LicenseTestUsageRoutes.TestUsage}");
    }

    internal static LicenseTestUsageResponse TestUsageAnswer(TenantId tenant, LicenseTestUsage testUsage, LicenseSnapshotSource snapshots,
        DateTime now)
    {
        var machine = testUsage.MachineFor(tenant);
        var machines = snapshots.ReadPicture(tenant, now, Array.Empty<LicenseExhaustion>()).Overview.Machines;
        return new LicenseTestUsageResponse
        {
            IsOn = machine is not null,
            MachineId = machine?.ToString("N"),
            Percent = LicenseTestUsage.Percent,
            Text = LicenseTestUsage.Text(machine, machines),
        };
    }

    internal static IResult TurnOnTestUsage(HttpContext ctx, Func<HttpContext, IResult?> ownerOnly, Func<HttpContext, TenantId?> resolveTenant,
        LicenseTestUsage testUsage, LicenseSnapshotSource snapshots, Func<DateTime> nowUtc, LicenseTestUsageRequest? body)
    {
        FileLog.Write("[LicenseEndpoints] PUT test-usage");
        try
        {
            if (ownerOnly(ctx) is { } notTheOwner)
                return notTheOwner;
            if (resolveTenant(ctx) is not { } tenant)
                return NoAccount();
            if (body is null || !Guid.TryParse(body.MachineId ?? "", out var machineId))
                return Refuse(StatusCodes.Status400BadRequest, "invalid_machine",
                    "The body is a JSON object with machineId: a machine's identifier as the Licenses page gives it.");
            var now = nowUtc();
            var machines = snapshots.ReadPicture(tenant, now, Array.Empty<LicenseExhaustion>()).Overview.Machines;
            if (testUsage.TurnOn(tenant, machineId, machines, now) is { } why)
                return Refuse(StatusCodes.Status409Conflict, "unknown_machine", why);
            return Results.Json(TestUsageAnswer(tenant, testUsage, snapshots, now));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseEndpoints] PUT /gateway/licenses/test-usage FAILED: {ex.Message}");
            throw;
        }
    }

    internal static IResult TurnOffTestUsage(HttpContext ctx, Func<HttpContext, IResult?> ownerOnly, Func<HttpContext, TenantId?> resolveTenant,
        LicenseTestUsage testUsage, LicenseSnapshotSource snapshots, Func<DateTime> nowUtc)
    {
        FileLog.Write("[LicenseEndpoints] DELETE test-usage");
        try
        {
            if (ownerOnly(ctx) is { } notTheOwner)
                return notTheOwner;
            if (resolveTenant(ctx) is not { } tenant)
                return NoAccount();
            testUsage.TurnOff(tenant);
            return Results.Json(TestUsageAnswer(tenant, testUsage, snapshots, nowUtc()));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LicenseEndpoints] DELETE /gateway/licenses/test-usage FAILED: {ex.Message}");
            throw;
        }
    }
}
