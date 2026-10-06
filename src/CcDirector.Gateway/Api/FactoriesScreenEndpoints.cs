using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Registry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>What the Factories screen reads beyond what the Factory Agents views already read.</summary>
internal sealed record FactoriesScreenSources(
    FactoryAgentsSources Activity,
    FactoryRegistryStore Registry,
    Func<TenantId, IReadOnlyList<CronJobDto>> Schedules);

/// <summary>
/// The Factories screen (Factories screen mission, phase B): every view folded once by
/// <see cref="FactoriesScreenFold"/> and returned finished (critical rule 7).
///
///   GET /gateway/factories                       -> FactoriesListViewDto   (the Factories tab)
///   GET /gateway/factories/{factory}             -> FactoryPageViewDto     (header, tabs and Overview)
///   GET /gateway/factories/{factory}/seats       -> FactorySeatsViewDto    (the Seats tab)
///
/// Behind the factory agents switch like every factory route, and the OWNER's pages: a session key is refused (the
/// same guard as the Factory Agents pages, and SessionKeyGuard names none of these). The old
/// <c>/gateway/factory-agents/...</c> views stay until the Cockpit has moved off them.
/// </summary>
internal static class FactoriesScreenEndpoints
{
    public const string Prefix = "/gateway/factories";

    public static void Map(IEndpointRouteBuilder app, Func<HttpContext, TenantId?> resolveTenant, FactoriesScreenSources sources)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(sources);

        app.MapGet(Prefix, (HttpContext ctx) =>
            FactoryAgentsViewEndpoints.Owner(ctx, resolveTenant, "GET factories screen list", tenant =>
            {
                var input = Inputs(sources, tenant, FactoryAgentsFold.WindowLast24h, factory: null);
                var dto = FactoriesScreenFold.List(input);
                FileLog.Write($"[FactoriesScreenEndpoints] GET list: factories={dto.Rows.Count}");
                return Results.Json(dto);
            }));

        app.MapGet(Prefix + "/{factory}", (HttpContext ctx, string factory) =>
            FactoryAgentsViewEndpoints.Owner(ctx, resolveTenant, $"GET factory page {factory}", tenant =>
            {
                if (sources.Registry.Find(tenant, factory) is not { } registered) return NotRegistered(factory);
                var input = Inputs(sources, tenant, FactoryAgentsFold.WindowLast7d, registered.Factory);
                var dto = FactoriesScreenFold.Page(registered, input);
                FileLog.Write($"[FactoriesScreenEndpoints] GET page: {registered.Factory} status={dto.StatusWord}, waiting={dto.Waiting.Items.Count}");
                return Results.Json(dto);
            }));

        app.MapGet(Prefix + "/{factory}/seats", (HttpContext ctx, string factory) =>
            FactoryAgentsViewEndpoints.Owner(ctx, resolveTenant, $"GET factory seats {factory}", tenant =>
            {
                if (sources.Registry.Find(tenant, factory) is not { } registered) return NotRegistered(factory);
                var input = Inputs(sources, tenant, FactoryAgentsFold.WindowLast7d, registered.Factory);
                var dto = FactoriesScreenFold.Seats(registered, input);
                FileLog.Write($"[FactoriesScreenEndpoints] GET seats: {registered.Factory} seats={dto.Rows.Count}");
                return Results.Json(dto);
            }));

        FileLog.Write($"[FactoriesScreenEndpoints] mapped {Prefix}, its factory page and its Seats tab");
    }

    /// <summary>Everything a view reads, in one pass. The list reads the last 24 hours (all a status needs); a
    /// factory's page and Seats tab read the last 7 days, so a seat that runs weekly still shows its last run.</summary>
    internal static FactoriesScreenInputs Inputs(FactoriesScreenSources sources, TenantId tenant, string windowKey, string? factory)
    {
        var now = sources.Activity.NowUtc();
        var zone = sources.Activity.TimeZone(tenant);
        var window = FactoryAgentsFold.ResolveWindow(windowKey, null, null, now, windowKey, zone);
        var activity = FactoryAgentsViewEndpoints.Inputs(sources.Activity, tenant, window, factory, now);
        var registry = sources.Registry.List(tenant);
        // The last talk is read whatever its age: "None yet" must mean none, not none this week.
        var talks = factory is null
            ? (IReadOnlyList<FactoryActivityDto>)Array.Empty<FactoryActivityDto>()
            : sources.Activity.Query(tenant, new FactoryRecordQuery(factory, null, FactoryActivityOutcome.Talked, null, null, false, 0, 1)).Rows;
        // Only a factory's page shows a goal number, and only its newest.
        var latest = new Dictionary<string, GoalNumberDto>(StringComparer.Ordinal);
        if (factory is not null && sources.Registry.GoalNumbers(tenant, factory, 1).Latest is { } newest)
            latest[factory] = newest;
        return new FactoriesScreenInputs(registry, activity, sources.Schedules(tenant), latest, talks);
    }

    private static IResult NotRegistered(string factory) =>
        Results.Json(new { error = $"There is no registered factory '{factory}'. A factory is registered with cc-devthrottle factory register." },
            statusCode: StatusCodes.Status404NotFound);
}
