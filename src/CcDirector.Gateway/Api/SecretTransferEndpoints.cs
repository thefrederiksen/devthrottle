using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Secrets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// THE SECRET HANDOFF ROUTES (issue #2943). A secret moves between two of the owner's machines sealed to the receiving
/// machine's key, so the Gateway relays it and can never read it. This first route lists the machines that can
/// receive one: their names and PUBLIC keys, from the account's connected Directors.
///
/// <list type="bullet">
/// <item><c>GET /gateway/secrets/machines</c> - any caller of the account: a session (cc-secrets inside a session), the
/// owner's phone or browser, or a machine's own credential (cc-secrets in the owner's terminal or window).</item>
/// </list>
/// </summary>
public static class SecretTransferEndpoints
{
    public const string MachinesRoute = "/gateway/secrets/machines";

    public static void Map(IEndpointRouteBuilder app, Func<HttpContext, TenantId?> resolveTenant,
        SecretMachineRegistry machines, Func<TenantId, string, bool> isConnected, Func<DateTime> nowUtc)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentNullException.ThrowIfNull(isConnected);
        ArgumentNullException.ThrowIfNull(nowUtc);

        app.MapGet(MachinesRoute, (HttpContext ctx) => ListMachines(ctx, resolveTenant, machines, isConnected, nowUtc));
        FileLog.Write($"[SecretTransferEndpoints] mapped GET {MachinesRoute}");
    }

    internal static IResult ListMachines(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant,
        SecretMachineRegistry machines, Func<TenantId, string, bool> isConnected, Func<DateTime> nowUtc)
    {
        FileLog.Write("[SecretTransferEndpoints] GET machines");
        try
        {
            if (resolveTenant(ctx) is not { } tenant)
                return Results.Json(new { code = "no_account", error = "no account is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);
            var rows = machines.Machines(tenant, directorId => isConnected(tenant, directorId), nowUtc());
            FileLog.Write($"[SecretTransferEndpoints] GET machines: {rows.Count} machine(s)");
            return Results.Json(new SecretMachineListResponse { Machines = rows });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SecretTransferEndpoints] GET {MachinesRoute} FAILED: {ex.Message}");
            throw;
        }
    }
}
