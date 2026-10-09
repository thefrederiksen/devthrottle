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
///   POST /gateway/factories/{factory}/failures/{id}/handled -> appends the row that marks a failure handled
///
/// Behind the factory agents switch like every factory route. The list and a factory's page are read by the owner
/// AND by a session of the same account (issue #3685): a factory's boss runs <c>cc-devthrottle factory status</c>
/// and reads the same status word and the same failing and waiting rows the owner sees, so it can act and mark them
/// handled through the activity record. The Seats tab and every write stay the owner's: a session key is refused
/// here and by SessionKeyGuard. The old <c>/gateway/factory-agents/...</c> views stay until the Cockpit has moved
/// off them.
/// </summary>
internal static class FactoriesScreenEndpoints
{
    public const string Prefix = "/gateway/factories";

    public static void Map(IEndpointRouteBuilder app, Func<HttpContext, TenantId?> resolveTenant, FactoriesScreenSources sources)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(sources);

        app.MapGet(Prefix, (HttpContext ctx) =>
            FactoryAgentsViewEndpoints.OwnerOrSession(ctx, resolveTenant, "GET factories screen list", tenant =>
            {
                var input = Inputs(sources, tenant, FactoryAgentsFold.WindowLast24h, factory: null);
                var dto = FactoriesScreenFold.List(input);
                FileLog.Write($"[FactoriesScreenEndpoints] GET list: factories={dto.Rows.Count}");
                return Results.Json(dto);
            }));

        app.MapGet(Prefix + "/{factory}", (HttpContext ctx, string factory) =>
            FactoryAgentsViewEndpoints.OwnerOrSession(ctx, resolveTenant, $"GET factory page {factory}", tenant =>
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

        // The factory's floor (owner decision, 8 October 2026): its production lines, bays, arrows and the owner's desk,
        // laid out here and drawn as it is by the Cockpit.
        app.MapGet(Prefix + "/{factory}/floor", (HttpContext ctx, string factory) =>
            FactoryAgentsViewEndpoints.Owner(ctx, resolveTenant, $"GET factory floor {factory}", tenant =>
            {
                if (sources.Registry.Find(tenant, factory) is not { } registered) return NotRegistered(factory);
                var input = Inputs(sources, tenant, FactoryAgentsFold.WindowLast7d, registered.Factory);
                var dto = FactoryFloorFold.View(registered, FactoriesScreenFold.Seats(registered, input),
                    FactoriesScreenFold.Page(registered, input), sources.Activity.Maps.Find(tenant, registered.Factory));
                FileLog.Write($"[FactoriesScreenEndpoints] GET floor: {registered.Factory} lanes={dto.Lanes.Count}, bays={dto.Bays.Count}, arrows={dto.Arrows.Count}");
                return Results.Json(dto);
            }));

        // "Handled" on a failure (round 2): a NEW row correcting it, as an escalation's "I have handled it" is. The
        // failed row itself is never changed, so the record keeps what went wrong and who said it was over.
        app.MapPost(Prefix + "/{factory}/failures/{id:guid}/handled", (HttpContext ctx, string factory, Guid id) =>
            FactoryAgentsViewEndpoints.Owner(ctx, resolveTenant, $"POST failure handled {factory} {id}", tenant =>
            {
                if (sources.Registry.Find(tenant, factory) is not { } registered) return NotRegistered(factory);
                var a = sources.Activity;
                var (failed, truncated) = FactoryAgentsViewEndpoints.ReadAll(a, tenant,
                    new FactoryRecordQuery(registered.Factory, null, FactoryActivityOutcome.Failed, null, null, false, 0, FactoryAgentsViewEndpoints.PageSize));
                var row = failed.FirstOrDefault(r => r.Id == id);
                if (row is null && truncated)
                    return Results.Json(new { error = $"The record holds more than {FactoryAgentsViewEndpoints.MaxRowsPerRead} failures of this factory, and this one is not among those one read returns. Nothing was written." },
                        statusCode: StatusCodes.Status409Conflict);
                if (row is null)
                    return Results.Json(new { error = $"There is no failure of '{registered.Factory}' with that id." }, statusCode: StatusCodes.Status404NotFound);
                var (corrections, correctionsTruncated) = FactoryAgentsViewEndpoints.Corrections(a, tenant);
                var actor = FactoryAgentsViewEndpoints.OwnerActor(ctx);
                var request = FactoriesScreenFold.FailureHandledRow(registered.Factory, row, corrections, correctionsTruncated, actor, a.NowUtc());
                var written = a.Append(tenant, request, actor);
                FileLog.Write($"[FactoriesScreenEndpoints] POST failure handled: {registered.Factory} failure={id} corrected by row {written.Id}, actor={actor}");
                return Results.Json(written, statusCode: StatusCodes.Status201Created);
            }));

        FileLog.Write($"[FactoriesScreenEndpoints] mapped {Prefix}, its factory page, its Seats tab and a failure's Handled");
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
