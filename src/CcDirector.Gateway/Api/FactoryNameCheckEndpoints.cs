using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// THE NAME CHECK, <c>GET /gateway/factory/name-check</c> -> <see cref="FactoryNameCheckDto"/>
/// (<c>cc-devthrottle factory name-check</c>): every seat of the account's live factories that says CEO or carries a
/// name that is not a role word. It sits in the factory switch's group (404 with no body when it is off) and is
/// reachable by a session key, so the Fleet Manager can read it and flag what it finds to the owner.
/// </summary>
internal static class FactoryNameCheckEndpoints
{
    public const string Route = "/gateway/factory/name-check";

    public static void Map(RouteGroupBuilder factoryGate, Func<HttpContext, TenantId?> resolveTenant,
        Func<TenantId, IReadOnlyList<RegisteredFactoryDto>> factories)
    {
        ArgumentNullException.ThrowIfNull(factoryGate);
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(factories);

        factoryGate.MapGet(Route, (HttpContext ctx) =>
        {
            if (resolveTenant(ctx) is not { } tenant)
                return Results.Json(new { error = "No account is bound to this request." }, statusCode: StatusCodes.Status403Forbidden);
            return Results.Json(FactoryNameCheck.Check(Live(factories(tenant))));
        });
        FileLog.Write($"[FactoryNameCheckEndpoints] mapped {Route}");
    }

    /// <summary>The account's registered factories that are not archived.</summary>
    internal static IReadOnlyList<RegisteredFactoryDto> Live(IReadOnlyList<RegisteredFactoryDto> all) =>
        all.Where(f => f.ArchivedAtUtc is null).ToList();
}
