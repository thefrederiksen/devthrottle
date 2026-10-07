using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Registry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The owner's actions on a factory (Factories screen mission, round 2). Every rule and every sentence is
/// <see cref="FactoryOwnerActions"/>'s; these routes read, call it, and write what it returns.
///
///   POST /gateway/factories/{factory}/waiting/handled-older  body { cutoffUtc, expectedCount } -> 201 | 400 | 403 | 404 | 409
///   POST /gateway/factories/{factory}/archive                body { schedules }                -> 200 | 400 | 403 | 404 | 409
///   POST /gateway/factories/{factory}/restore                body { schedules }                -> 200 | 400 | 403 | 404 | 409
///
/// ONLY THE OWNER'S OWN BROWSER OR PHONE, as Talk (<see cref="FleetManagerOwnerDevice.Require"/>): a session key, a
/// Director's device key and the shared machine token are refused before anything is read. Each body is what the
/// confirm showed, sent back; when the Gateway would now do something else, the answer is 409 and nothing is done.
///
/// Mapped OUTSIDE the factory gate's group, like Talk, so a switch-off refusal can carry a sentence; the switch
/// question is asked here, after the owner check, so the sentence reaches nobody but the owner.
/// </summary>
internal static class FactoryOwnerActionEndpoints
{
    public const string Prefix = FactoriesScreenEndpoints.Prefix + "/{factory}";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <param name="appendRows">Record several activity rows as one write: all of them or none.</param>
    /// <param name="setScheduleEnabled">Switch one schedule of the account on or off; null when it does not exist.</param>
    public static void Map(IEndpointRouteBuilder app, FactoryAgentsSwitch factorySwitch,
        Func<HttpContext, TenantId?> resolveTenant, FactoriesScreenSources sources,
        Func<TenantId, IReadOnlyList<AppendFactoryActivityRequest>, string, IReadOnlyList<FactoryActivityDto>> appendRows,
        Func<TenantId, string, bool, CronJobDto?> setScheduleEnabled)
    {
        ArgumentNullException.ThrowIfNull(factorySwitch);
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(appendRows);
        ArgumentNullException.ThrowIfNull(setScheduleEnabled);

        app.MapPost(Prefix + "/waiting/handled-older", (HttpContext ctx, string factory) =>
            Handle(ctx, factory, "mark old waiting items handled", factorySwitch, resolveTenant, sources, (tenant, f, body, actor) =>
            {
                var now = sources.Activity.NowUtc();
                var input = FactoriesScreenEndpoints.Inputs(sources, tenant, FactoryAgentsFold.WindowLast24h, f.Factory);
                var a = input.Activity;
                var open = FactoryAgentsFold.OpenWaiting(a.WaitingCandidates, FactoryAgentsFold.AllCorrections(a))
                    .Where(r => string.Equals(r.Factory, f.Factory, StringComparison.OrdinalIgnoreCase)).ToList();
                var rows = FactoryOwnerActions.BulkHandledRows(f, open, FactoryAgentsFold.AllCorrections(a), a.WaitingTruncated,
                    body, actor, a.Zone, now);
                var written = appendRows(tenant, rows, actor);
                var marked = written.Count - 1;
                FileLog.Write($"[FactoryOwnerActionEndpoints] handled-older: {f.Factory} marked={marked}, owner row={written[^1].Id}, actor={actor}");
                return Results.Json(new FactoryOwnerActionResultDto
                {
                    Text = $"Marked {Count(marked, "item")} handled. The activity record holds a handled row for each, and one row for what you did.",
                    Marked = marked,
                }, statusCode: StatusCodes.Status201Created);
            }));

        app.MapPost(Prefix + "/archive", (HttpContext ctx, string factory) =>
            Handle(ctx, factory, "archive a factory", factorySwitch, resolveTenant, sources, (tenant, f, body, actor) =>
            {
                if (f.ArchivedAtUtc is not null)
                    throw new FactoryViewValidationException($"{f.Title} is already archived.");
                var now = sources.Activity.NowUtc();
                var plan = FactoryOwnerActions.ArchivePlan(f, sources.Schedules(tenant));
                FactoryOwnerActions.RequireSameSchedules(plan, body, "Archive");
                // The registry first, naming every schedule the archive is about to switch off: should a switch fail
                // part-way, Restore still knows each one it may have to switch back on.
                sources.Registry.Archive(tenant, f.Factory, actor, plan.SwitchIds, now);
                var switched = Switch(tenant, plan, enabled: false, setScheduleEnabled);
                appendRows(tenant, new[] { FactoryOwnerActions.ArchiveRow(f, switched, actor, now) }, actor);
                FileLog.Write($"[FactoryOwnerActionEndpoints] archive: {f.Factory} switched off={string.Join(",", switched.Select(j => j.Id))}, actor={actor}");
                return Results.Json(new FactoryOwnerActionResultDto
                {
                    Text = $"{f.Title} is archived. " + (switched.Count == 0
                        ? "No schedule was switched off."
                        : $"Switched off: {FactoryOwnerActions.Names(switched)}."),
                    SchedulesSwitched = switched.Select(j => j.Id).ToList(),
                });
            }));

        app.MapPost(Prefix + "/restore", (HttpContext ctx, string factory) =>
            Handle(ctx, factory, "restore a factory", factorySwitch, resolveTenant, sources, (tenant, f, body, actor) =>
            {
                if (f.ArchivedAtUtc is null)
                    throw new FactoryViewValidationException($"{f.Title} is not archived.");
                var now = sources.Activity.NowUtc();
                var plan = FactoryOwnerActions.RestorePlan(f, sources.Schedules(tenant));
                FactoryOwnerActions.RequireSameSchedules(plan, body, "Restore");
                // The schedules first, the registry after: should a switch fail, the factory is still archived and
                // still names them, so pressing Restore again finishes the job.
                var switched = Switch(tenant, plan, enabled: true, setScheduleEnabled);
                sources.Registry.Restore(tenant, f.Factory);
                appendRows(tenant, new[] { FactoryOwnerActions.RestoreRow(f, switched, actor, now) }, actor);
                FileLog.Write($"[FactoryOwnerActionEndpoints] restore: {f.Factory} switched on={string.Join(",", switched.Select(j => j.Id))}, actor={actor}");
                return Results.Json(new FactoryOwnerActionResultDto
                {
                    Text = $"{f.Title} is back on the Factories list. " + (switched.Count == 0
                        ? "No schedule was switched back on."
                        : $"Switched back on: {FactoryOwnerActions.Names(switched)}."),
                    SchedulesSwitched = switched.Select(j => j.Id).ToList(),
                });
            }));

        FileLog.Write($"[FactoryOwnerActionEndpoints] mapped {Prefix}/waiting/handled-older, /archive and /restore");
    }

    // Switch each planned schedule; one that vanished since the plan was read is not claimed as switched.
    private static List<CronJobDto> Switch(TenantId tenant, FactoryOwnerActions.SchedulePlan plan, bool enabled,
        Func<TenantId, string, bool, CronJobDto?> setScheduleEnabled)
    {
        var switched = new List<CronJobDto>();
        foreach (var job in plan.Switch)
        {
            var stored = setScheduleEnabled(tenant, job.Id, enabled);
            if (stored is null)
                FileLog.Write($"[FactoryOwnerActionEndpoints] schedule {job.Id} is gone; not switched {(enabled ? "on" : "off")}");
            else
                switched.Add(stored);
        }
        return switched;
    }

    private delegate IResult Act(TenantId tenant, RegisteredFactoryDto factory, FactoryOwnerActionRequest? body, string actor);

    private static async Task<IResult> Handle(HttpContext ctx, string factory, string what, FactoryAgentsSwitch factorySwitch,
        Func<HttpContext, TenantId?> resolveTenant, FactoriesScreenSources sources, Act act)
    {
        FileLog.Write($"[FactoryOwnerActionEndpoints] POST {what}: factory={factory}");
        try
        {
            if (FleetManagerOwnerDevice.Require(ctx, what,
                    $"a session may not {what}; that is the owner's act, from the owner's own phone or browser",
                    nameof(FactoryOwnerActionEndpoints), out var notOwner) is null)
                return notOwner!;
            if (!FactoryAgentsGate.IsOnFor(ctx, factorySwitch, resolveTenant, out var why))
            {
                FileLog.Write($"[FactoryOwnerActionEndpoints] REFUSED: {why}");
                return Refused(StatusCodes.Status404NotFound,
                    "Factory agents are switched off for this account, so nothing was done. The administrator switches them on per account.");
            }
            if (resolveTenant(ctx) is not { } tenant)
                return Refused(StatusCodes.Status403Forbidden, "No account is bound to this request.");
            if (sources.Registry.Find(tenant, factory) is not { } registered)
                return Refused(StatusCodes.Status404NotFound, $"No factory '{factory}' is registered in this account, so nothing was done.");

            FactoryOwnerActionRequest? body = null;
            if (ctx.Request.ContentLength is not 0)
            {
                try
                {
                    body = await JsonSerializer.DeserializeAsync<FactoryOwnerActionRequest>(ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                }
                catch (JsonException ex)
                {
                    FileLog.Write($"[FactoryOwnerActionEndpoints] bad JSON: {ex.Message}");
                    return Refused(StatusCodes.Status400BadRequest, "The request body is not valid JSON, so nothing was done.");
                }
            }

            return act(tenant, registered, body, FactoryAgentsViewEndpoints.OwnerActor(ctx));
        }
        catch (FactoryOwnerActionConflictException ex)
        {
            FileLog.Write($"[FactoryOwnerActionEndpoints] {what} CONFLICT: {ex.Message}");
            return Refused(StatusCodes.Status409Conflict, ex.Message);
        }
        catch (FactoryNotRegisteredException ex)
        {
            FileLog.Write($"[FactoryOwnerActionEndpoints] {what} REFUSED: {ex.Message}");
            return Refused(StatusCodes.Status404NotFound, ex.Message);
        }
        catch (Exception ex) when (ex is FactoryViewValidationException or FactoryActivityValidationException)
        {
            FileLog.Write($"[FactoryOwnerActionEndpoints] {what} REFUSED: {ex.Message}");
            return Refused(StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FactoryOwnerActionEndpoints] {what} FAILED: {ex.Message}");
            throw;
        }
    }

    private static IResult Refused(int status, string error) => Results.Json(new { error }, statusCode: status);

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
