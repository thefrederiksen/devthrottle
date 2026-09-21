using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>One read of the factory activity record, in the record store's own terms.</summary>
internal sealed record FactoryRecordQuery(
    string? Factory, string? Agent, string? Outcome, DateTime? FromUtc, DateTime? ToUtc, bool OldestFirst, int Offset, int Limit);

/// <summary>
/// What the Factory Agents views read, as delegates, so the endpoints do not depend on how the record and the
/// triggers are stored - and so a test hands them fixtures.
/// </summary>
internal sealed record FactoryAgentsSources(
    Func<TenantId, FactoryRecordQuery, FactoryActivityPage> Query,
    Func<TenantId, AppendFactoryActivityRequest, string, FactoryActivityDto> Append,
    Func<TenantId, IReadOnlyList<FactoryTriggerFacts>> Triggers,
    Func<TenantId, string, bool, string, bool> SetTriggerPaused,
    Func<TenantId, IReadOnlySet<string>> LiveSessionIds,
    Func<TenantId, TimeZoneInfo> TimeZone,
    Func<DateTime> NowUtc,
    FactoryReportStore Reports);

/// <summary>
/// The Factory Agents area of the Cockpit (Website Business Factory, product track, Screens 1-5): every read is
/// folded once by <see cref="FactoryAgentsFold"/> and returned finished (critical rule 7).
///
///   GET  /gateway/factory-agents/switch                              -> { enabled }            (ALWAYS mapped)
///   GET  /gateway/factory-agents/factories?window=&amp;from=&amp;to=          -> FactoriesViewDto
///   GET  /gateway/factory-agents/factories/{factory}/agents/{agent}   -> FactoryAgentPageDto
///   GET  /gateway/factory-agents/activity?factory=&amp;agent=&amp;outcome=&amp;window=&amp;from=&amp;to= -> FactoryActivityViewDto
///   GET  /gateway/factory-agents/activity.csv?(same)                  -> text/csv, every row
///   GET  /gateway/factory-agents/waiting?factory=                     -> FactoryWaitingViewDto
///   POST /gateway/factory-agents/waiting/{id}/handled                 -> appends the correcting row
///   GET  /gateway/factory-agents/reports?(same)&amp;report=               -> FactoryReportViewDto
///   POST /gateway/factory-agents/reports                              -> keeps the filter as a report
///   POST /gateway/factory-agents/factories/{factory}/pause|resume
///   POST /gateway/factory-agents/factories/{factory}/agents/{agent}/pause|resume
///
/// Only the switch route is mapped while <c>factoryAgents.enabled</c> is off: the rest answer 404. These are the
/// OWNER's pages: a session key is refused (SessionKeyGuard names none of them).
/// </summary>
internal static class FactoryAgentsViewEndpoints
{
    public const string Prefix = "/gateway/factory-agents";

    /// <summary>The most rows one view reads from the record before it says the window was cut.</summary>
    public const int MaxRowsPerRead = 20_000;

    private const int PageSize = 1000;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static void MapSwitch(IEndpointRouteBuilder app, bool enabled)
    {
        app.MapGet(Prefix + "/switch", () => Results.Json(new FactoryAgentsSwitchDto { Enabled = enabled }));
        FileLog.Write($"[FactoryAgentsViewEndpoints] mapped {Prefix}/switch (enabled={enabled})");
    }

    public static void Map(IEndpointRouteBuilder app, Func<HttpContext, TenantId?> resolveTenant, FactoryAgentsSources sources)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(sources);

        app.MapGet(Prefix + "/factories", (HttpContext ctx, string? window, DateTime? from, DateTime? to) =>
            Owner(ctx, resolveTenant, "GET factories", tenant =>
            {
                var now = sources.NowUtc();
                var w = FactoryAgentsFold.ResolveWindow(window, from, to, now, FactoryAgentsFold.WindowLast24h, sources.TimeZone(tenant));
                var dto = FactoryAgentsFold.Factories(Inputs(sources, tenant, w, null, now));
                FileLog.Write($"[FactoryAgentsViewEndpoints] GET factories: window={w.Key}, factories={dto.Factories.Count}, agents={dto.AllAgents.Count}");
                return Results.Json(dto);
            }));

        app.MapGet(Prefix + "/factories/{factory}/agents/{agent}", (HttpContext ctx, string factory, string agent) =>
            Owner(ctx, resolveTenant, $"GET agent {factory}/{agent}", tenant =>
            {
                var now = sources.NowUtc();
                var w = FactoryAgentsFold.ResolveWindow(FactoryAgentsFold.WindowLast7d, null, null, now, FactoryAgentsFold.WindowLast7d);
                var dto = FactoryAgentsFold.AgentPage(factory, agent, Inputs(sources, tenant, w, factory, now));
                FileLog.Write($"[FactoryAgentsViewEndpoints] GET agent: {factory}/{agent} status={dto.StatusWord}, triggers={dto.WokenBy.Count}");
                return Results.Json(dto);
            }));

        app.MapGet(Prefix + "/activity", (HttpContext ctx, string? factory, string? agent, string? outcome,
                string? window, DateTime? from, DateTime? to) =>
            Owner(ctx, resolveTenant, "GET activity", tenant =>
            {
                var now = sources.NowUtc();
                var filter = FactoryAgentsFold.NormaliseFilter(factory, agent, outcome);
                var w = FactoryAgentsFold.ResolveWindow(window, from, to, now, FactoryAgentsFold.WindowLast24h, sources.TimeZone(tenant));
                var dto = FactoryAgentsFold.Activity(Inputs(sources, tenant, w, filter.Factory, now), filter, CsvHref(filter, w));
                FileLog.Write($"[FactoryAgentsViewEndpoints] GET activity: window={w.Key}, lines={dto.Rows.Count}, faults={dto.Faults.Count}");
                return Results.Json(dto);
            }));

        app.MapGet(Prefix + "/activity.csv", (HttpContext ctx, string? factory, string? agent, string? outcome,
                string? window, DateTime? from, DateTime? to) =>
            Owner(ctx, resolveTenant, "GET activity.csv", tenant =>
            {
                var now = sources.NowUtc();
                var filter = FactoryAgentsFold.NormaliseFilter(factory, agent, outcome);
                var w = FactoryAgentsFold.ResolveWindow(window, from, to, now, FactoryAgentsFold.WindowLast24h, sources.TimeZone(tenant));
                var (rows, truncated) = ReadAll(sources, tenant, new FactoryRecordQuery(filter.Factory, filter.Agent, filter.Outcome, w.FromUtc, w.ToUtc, true, 0, PageSize));
                if (truncated)
                    return Results.Json(new { error = $"This window holds more than {MaxRowsPerRead} rows. Export a shorter window, so the file is the whole record for it." },
                        statusCode: StatusCodes.Status400BadRequest);
                var csv = FactoryAgentsFold.Csv(rows, filter);
                FileLog.Write($"[FactoryAgentsViewEndpoints] GET activity.csv: rows={rows.Count}");
                ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"factory-activity-{now:yyyyMMdd-HHmm}.csv\"";
                return Results.Text(csv, "text/csv; charset=utf-8");
            }));

        app.MapGet(Prefix + "/waiting", (HttpContext ctx, string? factory) =>
            Owner(ctx, resolveTenant, "GET waiting", tenant =>
            {
                var now = sources.NowUtc();
                var w = FactoryAgentsFold.ResolveWindow(FactoryAgentsFold.WindowLast24h, null, null, now, FactoryAgentsFold.WindowLast24h);
                var f = string.IsNullOrWhiteSpace(factory) ? null : factory.Trim();
                var dto = FactoryAgentsFold.Waiting(Inputs(sources, tenant, w, f, now), f);
                FileLog.Write($"[FactoryAgentsViewEndpoints] GET waiting: factory={f}, items={dto.Items.Count}");
                return Results.Json(dto);
            }));

        app.MapPost(Prefix + "/waiting/{id:guid}/handled", (HttpContext ctx, Guid id) =>
            Owner(ctx, resolveTenant, $"POST handled {id}", tenant =>
            {
                var now = sources.NowUtc();
                var (escalations, _) = ReadAll(sources, tenant, new FactoryRecordQuery(null, null, FactoryActivityOutcome.Escalated, null, null, false, 0, PageSize));
                var escalation = escalations.FirstOrDefault(r => r.Id == id);
                if (escalation is null)
                    return Results.Json(new { error = "There is no escalation with that id." }, statusCode: StatusCodes.Status404NotFound);
                var corrections = Corrections(sources, tenant);
                var actor = OwnerActor(ctx);
                var request = FactoryAgentsFold.HandledRow(escalation, corrections, actor, now);
                var written = sources.Append(tenant, request, actor);
                FileLog.Write($"[FactoryAgentsViewEndpoints] POST handled: escalation={id} corrected by row {written.Id}, actor={actor}");
                return Results.Json(written, statusCode: StatusCodes.Status201Created);
            }));

        app.MapGet(Prefix + "/reports", (HttpContext ctx, string? factory, string? agent, string? outcome,
                string? window, DateTime? from, DateTime? to, string? report) =>
            Owner(ctx, resolveTenant, "GET reports", tenant =>
            {
                var now = sources.NowUtc();
                string? openedName = null;
                if (!string.IsNullOrWhiteSpace(report))
                {
                    var saved = sources.Reports.Find(tenant, report.Trim());
                    if (saved is null)
                        return Results.Json(new { error = "There is no saved report with that id." }, statusCode: StatusCodes.Status404NotFound);
                    (factory, agent, outcome, window, from, to, openedName) =
                        (saved.Factory, saved.Agent, saved.Outcome, saved.Window, saved.FromUtc, saved.ToUtc, saved.Name);
                }
                var filter = FactoryAgentsFold.NormaliseFilter(factory, agent, outcome);
                var w = FactoryAgentsFold.ResolveWindow(window, from, to, now, FactoryAgentsFold.WindowLast7d, sources.TimeZone(tenant));
                var dto = FactoryAgentsFold.Report(Inputs(sources, tenant, w, filter.Factory, now), filter, CsvHref(filter, w),
                    sources.Reports.List(tenant), openedName);
                FileLog.Write($"[FactoryAgentsViewEndpoints] GET reports: window={w.Key}, rows={dto.Rows.Count}, saved={dto.Saved.Count}, opened={openedName}");
                return Results.Json(dto);
            }));

        app.MapPost(Prefix + "/reports", async (HttpContext ctx) =>
        {
            SaveFactoryReportRequest? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<SaveFactoryReportRequest>(ctx.Request.Body, JsonOpts, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[FactoryAgentsViewEndpoints] POST reports bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
            return Owner(ctx, resolveTenant, "POST reports", tenant =>
            {
                var saved = sources.Reports.Save(tenant, body!, OwnerActor(ctx), sources.NowUtc(), sources.TimeZone(tenant));
                return Results.Json(FactoryAgentsFold.SavedDto(saved, sources.TimeZone(tenant)), statusCode: StatusCodes.Status201Created);
            });
        });

        foreach (var action in new[] { "pause", "resume" })
        {
            var paused = action == "pause";
            app.MapPost(Prefix + "/factories/{factory}/" + action, (HttpContext ctx, string factory) =>
                Owner(ctx, resolveTenant, $"POST {action} factory {factory}", tenant =>
                    SetPaused(sources, tenant, ctx, t => SameId(t.Factory, factory), paused)));

            app.MapPost(Prefix + "/factories/{factory}/agents/{agent}/" + action, (HttpContext ctx, string factory, string agent) =>
                Owner(ctx, resolveTenant, $"POST {action} agent {factory}/{agent}", tenant =>
                    SetPaused(sources, tenant, ctx, t => SameId(t.Factory, factory) && SameId(t.FactoryAgent, agent), paused)));
        }

        FileLog.Write($"[FactoryAgentsViewEndpoints] mapped {Prefix} views, activity, waiting, reports, pause and resume");
    }

    private static IResult SetPaused(FactoryAgentsSources sources, TenantId tenant, HttpContext ctx,
        Func<FactoryTriggerFacts, bool> which, bool paused)
    {
        var triggers = sources.Triggers(tenant).Where(which).ToList();
        if (triggers.Count == 0)
            return Results.Json(new { error = "No trigger wakes this, so there is nothing to pause or resume." },
                statusCode: StatusCodes.Status404NotFound);
        var by = OwnerActor(ctx);
        var changed = triggers.Count(t => t.Paused != paused && sources.SetTriggerPaused(tenant, t.Id, paused, by));
        FileLog.Write($"[FactoryAgentsViewEndpoints] {(paused ? "pause" : "resume")}: triggers={triggers.Count}, changed={changed}, by={by}");
        return Results.Json(new { triggers = triggers.Count, changed });
    }

    // The owner's pages: a request with no account is refused, and so is a session key - a session reads the record
    // through `cc-devthrottle factory activity`, never the owner's screens.
    private static IResult Owner(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant, string what,
        Func<TenantId, IResult> handle)
    {
        FileLog.Write($"[FactoryAgentsViewEndpoints] {what}");
        try
        {
            if (resolveTenant(ctx) is not { } tenant)
                return Results.Json(new { error = "no account is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);
            if (AuthMiddleware.CallingSession(ctx) is not null)
            {
                FileLog.Write($"[FactoryAgentsViewEndpoints] REFUSED: a session key asked for the owner's page ({what})");
                return Results.Json(new { error = "The Factory Agents pages are the owner's. A session reads the record with cc-devthrottle factory activity." },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            return handle(tenant);
        }
        catch (FactoryViewValidationException ex)
        {
            FileLog.Write($"[FactoryAgentsViewEndpoints] {what} REFUSED: {ex.Message}");
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FactoryAgentsViewEndpoints] {what} FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>Everything a fold reads, taken in one pass.</summary>
    internal static FactoryFoldInputs Inputs(FactoryAgentsSources sources, TenantId tenant, FactoryWindow window,
        string? factory, DateTime now)
    {
        var (rows, truncated) = ReadAll(sources, tenant, new FactoryRecordQuery(factory, null, null, window.FromUtc, window.ToUtc, true, 0, PageSize));
        var (asked, t1) = ReadAll(sources, tenant, new FactoryRecordQuery(factory, null, FactoryActivityOutcome.Asked, null, null, true, 0, PageSize));
        var (escalated, t2) = ReadAll(sources, tenant, new FactoryRecordQuery(factory, null, FactoryActivityOutcome.Escalated, null, null, true, 0, PageSize));
        var candidates = asked.Concat(escalated).ToList();
        var corrections = candidates.Count == 0 ? new List<FactoryActivityDto>() : Corrections(sources, tenant);
        return new FactoryFoldInputs(rows, truncated || t1 || t2, candidates, corrections, sources.Triggers(tenant),
            sources.LiveSessionIds(tenant), window, sources.TimeZone(tenant), now);
    }

    // Every row that corrects another. A correction may carry any outcome except the quiet ones a trigger writes on
    // its own, so those two are the only ones not read. There is NO time bound: a caller sets a row's own time, so a
    // correction can carry a time earlier than the row it corrects, and a bound on it would keep a handled item on
    // the list for ever. The rows read are the few non-quiet ones, never the empty checks.
    private static List<FactoryActivityDto> Corrections(FactoryAgentsSources sources, TenantId tenant)
    {
        var all = new List<FactoryActivityDto>();
        foreach (var outcome in FactoryActivityOutcome.All.Where(o => o is not FactoryActivityOutcome.NothingToDo and not FactoryActivityOutcome.Paused))
        {
            var (rows, _) = ReadAll(sources, tenant, new FactoryRecordQuery(null, null, outcome, null, null, true, 0, PageSize));
            all.AddRange(rows.Where(r => r.CorrectsId is not null));
        }
        return all;
    }

    /// <summary>Page through the record up to <see cref="MaxRowsPerRead"/> rows. The second value is true when the
    /// record held more, so a cut list is never presented as complete.</summary>
    internal static (List<FactoryActivityDto> Rows, bool Truncated) ReadAll(FactoryAgentsSources sources, TenantId tenant, FactoryRecordQuery query)
    {
        var rows = new List<FactoryActivityDto>();
        var offset = 0;
        while (true)
        {
            var page = sources.Query(tenant, query with { Offset = offset, Limit = PageSize });
            rows.AddRange(page.Rows);
            if (!page.HasMore) return (rows, false);
            if (rows.Count >= MaxRowsPerRead) return (rows, true);
            if (page.Rows.Count == 0)
                throw new InvalidOperationException("The factory activity record said it had more rows and returned none.");
            offset += page.Rows.Count;
        }
    }

    private static string CsvHref(FactoryFilter filter, FactoryWindow window)
    {
        var q = new List<string> { "window=" + Uri.EscapeDataString(window.Key) };
        if (window.Key == FactoryAgentsFold.WindowCustom)
        {
            q.Add("from=" + Uri.EscapeDataString(window.FromUtc.ToString("o")));
            q.Add("to=" + Uri.EscapeDataString(window.ToUtc.ToString("o")));
        }
        if (filter.Factory is not null) q.Add("factory=" + Uri.EscapeDataString(filter.Factory));
        if (filter.Agent is not null) q.Add("agent=" + Uri.EscapeDataString(filter.Agent));
        if (filter.Outcome is not null) q.Add("outcome=" + Uri.EscapeDataString(filter.Outcome));
        return Prefix + "/activity.csv?" + string.Join("&", q);
    }

    // Who pressed the button, as the record stores it: the owner, and the credential the request came in on.
    private static string OwnerActor(HttpContext ctx) =>
        "owner (" + (AuthMiddleware.RegisteringCredential(ctx) ?? AuthMiddleware.IdentityKind(ctx)) + ")";

    private static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
