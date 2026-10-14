using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Briefs;
using CcDirector.Gateway.Factory.Registry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>What the Factories screen reads beyond what the Factory Agents views already read.</summary>
/// <param name="ScheduleRuns">The newest runs of these schedules in this account, each with its ending stamped
/// (<see cref="Running.CronRunRecordReader.RecentRunsOf"/>), at this instant - what each seat's "closes itself"
/// marker is folded from.</param>
/// <param name="Problems">One factory's open problems, from the one problems fold (<c>GET /cron/problems</c>) - what
/// the factory page's Open problems card lays out (Factory Control, step 1b).</param>
/// <param name="Emails">The SENT factory emails that covered one factory, newest first (<c>GET /gateway/factory-emails</c>)
/// - what the factory page's kept reports card lays out.</param>
internal sealed record FactoriesScreenSources(
    FactoryAgentsSources Activity,
    FactoryRegistryStore Registry,
    Func<TenantId, IReadOnlyList<CronJobDto>> Schedules,
    Func<TenantId, IReadOnlyCollection<string>, DateTime, IReadOnlyDictionary<string, IReadOnlyList<CronRunRecord>>> ScheduleRuns,
    Func<TenantId, string, CronRunProblemsDto> Problems,
    Func<TenantId, string, FactoryEmailsDto> Emails);

/// <summary>
/// The Factories screen (Factories screen mission, phase B): every view folded once by
/// <see cref="FactoriesScreenFold"/> and returned finished (critical rule 7).
///
///   GET /gateway/factories                       -> FactoriesListViewDto   (the Factories tab)
///   GET /gateway/factories/{factory}             -> FactoryPageViewDto     (header, tabs and Overview)
///   GET /gateway/factories/{factory}/seats       -> FactorySeatsViewDto    (the Seats tab)
///   GET /gateway/factories/{factory}/seats/{seat} -> FactorySeatPageViewDto (one seat's page; <see cref="MapSeatPage"/>)
///   GET /gateway/factories/{factory}/seats/{seat}/brief/editor  -> FactoryBriefEditorViewDto  (what the brief's editor starts from)
///   GET /gateway/factories/{factory}/seats/{seat}/brief/history -> FactoryBriefHistoryViewDto (one page; ?before=the version to start below)
///   GET /gateway/factories/{factory}/seats/{seat}/brief/versions/{version} -> FactoryBriefVersionViewDto (one version, with its text)
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
                dto.Problems = FactoryProblemsFold.Card(registered, sources.Problems(tenant, registered.Factory));
                dto.Emails = FactoryEmailsFold.Card(registered, sources.Emails(tenant, registered.Factory),
                    input.Activity.Zone, input.Activity.NowUtc);
                FileLog.Write($"[FactoriesScreenEndpoints] GET page: {registered.Factory} status={dto.StatusWord}, waiting={dto.Waiting.Items.Count}, problems={dto.Problems.Items.Count}, emails={dto.Emails.Items.Count}");
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

    /// <summary>
    /// One seat's page (Factory Seats mission, phase 1): every seat has one, with or without a schedule, folded by
    /// <see cref="FactorySeatPageFold"/>. The owner's, like the Seats tab it is opened from.
    ///
    /// And what its brief panel reads (phase 2b): the editor's starting text, one page of the history and one
    /// version, each folded by <see cref="FactoryBriefPanelFold"/>. These are READS for the owner's screen. Saving,
    /// publishing and bringing back go to the brief's own routes (<see cref="FactoryBriefEndpoints"/>), where who is
    /// asking is checked and every refusal is worded.
    /// </summary>
    /// <param name="memoryNoteCount">How many notes one factory's memory holds now. REQUIRED, so a wiring line that
    /// forgot it fails to compile rather than showing every factory an empty memory.</param>
    /// <param name="briefs">The store holding every seat's brief. REQUIRED for the same reason: a page wired without
    /// it would show every seat's brief as a file.</param>
    public static void MapSeatPage(IEndpointRouteBuilder app, Func<HttpContext, TenantId?> resolveTenant,
        FactoriesScreenSources sources, Func<TenantId, string, int> memoryNoteCount, FactoryBriefStore briefs)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(memoryNoteCount);
        ArgumentNullException.ThrowIfNull(briefs);

        const string seatRoute = Prefix + "/{factory}/seats/{seat}";

        app.MapGet(seatRoute, (HttpContext ctx, string factory, string seat) =>
            FactoryAgentsViewEndpoints.Owner(ctx, resolveTenant, $"GET factory seat {factory}/{seat}", tenant =>
            {
                if (FindSeat(sources, tenant, factory, seat, out var registered, out var found) is { } refusal) return refusal;
                var input = Inputs(sources, tenant, FactoryAgentsFold.WindowLast7d, registered.Factory);
                // The page's last talk is this seat's own, whatever its age - not the factory's newest.
                var talks = sources.Activity.Query(tenant,
                    new FactoryRecordQuery(registered.Factory, found.Id, FactoryActivityOutcome.Talked, null, null, false, 0, 1)).Rows;
                var dto = FactorySeatPageFold.Page(registered, found, input with { Talks = talks },
                    memoryNoteCount(tenant, registered.Factory), briefs.Standing(tenant, registered.Factory, found.Id));
                FileLog.Write($"[FactoriesScreenEndpoints] GET seat: {registered.Factory}/{found.Id} starts={dto.StartsWord}, schedules={dto.Starts.Schedules.Count}, triggers={dto.Starts.Triggers.Count}, brief={dto.Brief.State}");
                return Results.Json(dto);
            }));

        app.MapGet(seatRoute + "/brief/editor", (HttpContext ctx, string factory, string seat) =>
            FactoryAgentsViewEndpoints.Owner(ctx, resolveTenant, $"GET factory seat brief editor {factory}/{seat}", tenant =>
            {
                if (FindSeat(sources, tenant, factory, seat, out var registered, out var found) is { } refusal) return refusal;
                var standing = briefs.Standing(tenant, registered.Factory, found.Id);
                var dto = FactoryBriefPanelFold.Editor(registered, found, standing,
                    sources.Activity.TimeZone(tenant), sources.Activity.NowUtc());
                FileLog.Write($"[FactoriesScreenEndpoints] GET brief editor: {registered.Factory}/{found.Id} startsFrom={dto.ExpectedVersion}");
                return Results.Json(dto);
            }));

        app.MapGet(seatRoute + "/brief/history", (HttpContext ctx, string factory, string seat) =>
            FactoryAgentsViewEndpoints.Owner(ctx, resolveTenant, $"GET factory seat brief history {factory}/{seat}", tenant =>
            {
                if (FindSeat(sources, tenant, factory, seat, out var registered, out var found) is { } refusal) return refusal;
                int? before = null;
                if (ctx.Request.Query.TryGetValue("before", out var beforeSent))
                {
                    // Refused in words, never quietly replaced: a page that started somewhere else than asked would
                    // read as the page that was asked for.
                    if (!int.TryParse(beforeSent.ToString(), out var b) || b < 1)
                        return Results.BadRequest(new
                        {
                            error = $"before is the version a page of the history starts below: a whole number from 1, not '{beforeSent}'",
                        });
                    before = b;
                }
                var standing = briefs.Standing(tenant, registered.Factory, found.Id);
                var page = briefs.History(tenant, registered.Factory, found.Id, before, FactoryBriefPanelFold.HistoryPage);
                var dto = FactoryBriefPanelFold.History(registered, found, standing, page, firstPage: before is null,
                    sources.Activity.TimeZone(tenant), sources.Activity.NowUtc());
                FileLog.Write($"[FactoriesScreenEndpoints] GET brief history: {registered.Factory}/{found.Id} before={before?.ToString() ?? "(newest)"}, rows={dto.Rows.Count}, total={page.Total}, next={dto.NextBefore?.ToString() ?? "(none)"}");
                return Results.Json(dto);
            }));

        app.MapGet(seatRoute + "/brief/versions/{version}", (HttpContext ctx, string factory, string seat, string version) =>
            FactoryAgentsViewEndpoints.Owner(ctx, resolveTenant, $"GET factory seat brief version {factory}/{seat}/{version}", tenant =>
            {
                if (FindSeat(sources, tenant, factory, seat, out var registered, out var found) is { } refusal) return refusal;
                if (!int.TryParse(version, out var wanted) || wanted < 1)
                    return Results.BadRequest(new { error = $"a version is a whole number from 1, not '{version}'" });
                var standing = briefs.Standing(tenant, registered.Factory, found.Id);
                if (briefs.Find(tenant, registered.Factory, found.Id, wanted) is not { } one)
                {
                    FileLog.Write($"[FactoriesScreenEndpoints] GET brief version: {registered.Factory}/{found.Id} has no version {wanted}");
                    return Results.Json(new
                    {
                        error = standing.Newest is null
                            ? $"'{found.Id}' of {registered.Factory} has no brief on the Gateway"
                            : $"version {wanted} of the brief of '{found.Id}' does not exist; its newest version is {standing.Newest.Version}",
                    }, statusCode: StatusCodes.Status404NotFound);
                }
                FileLog.Write($"[FactoriesScreenEndpoints] GET brief version: {registered.Factory}/{found.Id} version={wanted}, state={one.State}, bytes={one.TextBytes}");
                return Results.Json(FactoryBriefPanelFold.Version(one, standing, sources.Activity.TimeZone(tenant), sources.Activity.NowUtc()));
            }));

        FileLog.Write($"[FactoriesScreenEndpoints] mapped {seatRoute} and its brief editor, history and versions");
    }

    /// <summary>
    /// The registered factory and its seat a seat route names, or the 404 that says what is missing. Only a
    /// registered seat has a page, and so only a registered seat has a brief panel.
    /// </summary>
    private static IResult? FindSeat(FactoriesScreenSources sources, TenantId tenant, string factory, string seat,
        out RegisteredFactoryDto registered, out RegisteredFactorySeatDto found)
    {
        registered = null!;
        found = null!;
        if (sources.Registry.Find(tenant, factory) is not { } f) return NotRegistered(factory);
        registered = f;
        var wanted = seat.Trim();
        if (f.Seats.FirstOrDefault(s => string.Equals(s.Id, wanted, StringComparison.OrdinalIgnoreCase)) is not { } one)
        {
            FileLog.Write($"[FactoriesScreenEndpoints] GET seat: {f.Factory} has no seat '{wanted}'");
            return Results.Json(new
            {
                error = $"{f.Title} has no seat '{wanted}'. Its seats are: {string.Join(", ", f.Seats.Select(s => s.Id))}. " +
                        "Only a registered seat has a page; what any other session wrote is on the factory's Activity tab.",
            }, statusCode: StatusCodes.Status404NotFound);
        }
        found = one;
        return null;
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
        // The runs of the seats in view - every factory's for the list, one factory's for its page - for the markers.
        var seatSchedules = registry
            .Where(f => factory is null || string.Equals(f.Factory, factory, StringComparison.Ordinal))
            .SelectMany(f => f.Seats).SelectMany(s => s.Schedules)
            .Distinct(StringComparer.Ordinal).ToList();
        var runs = sources.ScheduleRuns(tenant, seatSchedules, now);
        return new FactoriesScreenInputs(registry, activity, sources.Schedules(tenant), latest, talks, runs);
    }

    private static IResult NotRegistered(string factory) =>
        Results.Json(new { error = $"There is no registered factory '{factory}'. A factory is registered with cc-devthrottle factory register." },
            statusCode: StatusCodes.Status404NotFound);
}
