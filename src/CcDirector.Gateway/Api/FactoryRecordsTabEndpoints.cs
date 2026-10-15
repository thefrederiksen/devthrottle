using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Factory.Records;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// THE RECORDS TAB OF A FACTORY'S PAGE (Factory Definition mission, phase 6, sections 5.6 and 5.7): what the owner's
/// screen reads, each view folded once by <see cref="FactoryRecordsTabFold"/> (critical rule 7).
///
///   GET /gateway/factories/{factory}/records                              -> FactoryRecordsTabViewDto
///   GET /gateway/factories/{factory}/records/journal                      -> FactoryRecordsJournalViewDto
///                                                                            (?seat=, none for the factory's own)
///   GET /gateway/factories/{factory}/records/documents/read               -> FactoryRecordsDocumentViewDto (?name= ?version=)
///   GET /gateway/factories/{factory}/records/documents/history            -> FactoryRecordsDocumentHistoryViewDto (?name= ?before=)
///   GET /gateway/factories/{factory}/records/attachments/{id}             -> the file itself, to save
///
/// The owner's, like the Seats tab: a session key is refused here and by SessionKeyGuard. A session reads and writes its
/// factory's records through <c>/factory-records</c> with the command line. Nothing here writes.
///
/// A DOCUMENT'S NAME TRAVELS IN THE QUERY, NEVER THE PATH: it is itself a path (<c>outreach/ledger.json</c>).
/// </summary>
internal static class FactoryRecordsTabEndpoints
{
    public static void Map(IEndpointRouteBuilder app, Func<HttpContext, TenantId?> resolveTenant, FactoriesScreenSources sources,
        FactoryJournalStore journal, FactoryDocumentStore documents, FactoryAttachmentStore attachments, Func<DateTime> nowUtc)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(nowUtc);

        const string route = FactoriesScreenEndpoints.Prefix + "/{factory}/records";

        app.MapGet(route, (HttpContext ctx, string factory) =>
            FactoryAgentsViewEndpoints.Owner(ctx, resolveTenant, $"GET factory records tab {factory}", tenant =>
            {
                if (sources.Registry.Find(tenant, factory) is not { } registered) return NotRegistered(factory);
                var dto = FactoryRecordsTabFold.Tab(registered, journal.Journals(tenant, registered.Factory),
                    documents.List(tenant, registered.Factory),
                    attachments.List(tenant, registered.Factory, seat: null, limit: FactoryRecordsTabFold.AttachmentPage),
                    sources.Activity.TimeZone(tenant), nowUtc());
                FileLog.Write($"[FactoryRecordsTabEndpoints] GET tab: {registered.Factory} journals={dto.JournalGroups.Count}, documents={dto.DocumentRows.Count}, attachments={dto.AttachmentRows.Count}");
                return Results.Json(dto);
            }));

        app.MapGet(route + "/journal", (HttpContext ctx, string factory) =>
            FactoryAgentsViewEndpoints.Owner(ctx, resolveTenant, $"GET factory records journal {factory}", tenant =>
            {
                if (sources.Registry.Find(tenant, factory) is not { } registered) return NotRegistered(factory);
                string? seat = null;
                if (ctx.Request.Query.TryGetValue("seat", out var seatSent) && !string.IsNullOrWhiteSpace(seatSent.ToString()))
                {
                    if (!FactoryRecordNames.TryOptionalSeat(seatSent.ToString(), out seat, out var refusal))
                        return Results.BadRequest(new { error = refusal });
                }
                var page = journal.List(tenant, registered.Factory, seat, factoryOnly: seat is null, limit: FactoryRecordsTabFold.JournalPage);
                var entries = page.Entries.Select(e => journal.Get(tenant, registered.Factory, e.Id))
                    .Where(e => e is not null).Select(e => e!).ToList();
                var dto = FactoryRecordsTabFold.Journal(registered, seat, entries, page.Total,
                    entry => entry.AttachmentIds
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(id => Guid.TryParse(id, out var g) ? attachments.Get(tenant, registered.Factory, g) : null)
                        .Where(a => a is not null).Select(a => a!).ToList(),
                    sources.Activity.TimeZone(tenant), nowUtc());
                FileLog.Write($"[FactoryRecordsTabEndpoints] GET journal: {registered.Factory}/{seat ?? "(factory)"} entries={dto.Entries.Count} of {page.Total}");
                return Results.Json(dto);
            }));

        app.MapGet(route + "/documents/read", (HttpContext ctx, string factory) =>
            FactoryAgentsViewEndpoints.Owner(ctx, resolveTenant, $"GET factory records document {factory}", tenant =>
            {
                if (sources.Registry.Find(tenant, factory) is not { } registered) return NotRegistered(factory);
                var name = ctx.Request.Query["name"].ToString();
                if (string.IsNullOrWhiteSpace(name)) return Results.BadRequest(new { error = "name is the document to read, and it is required" });
                var newest = documents.Get(tenant, registered.Factory, name);
                if (newest is null) return NoDocument(registered.Factory, name);
                var wanted = newest;
                if (ctx.Request.Query.TryGetValue("version", out var versionSent))
                {
                    if (!int.TryParse(versionSent.ToString(), out var v) || v < 1)
                        return Results.BadRequest(new { error = $"a version is a whole number from 1, not '{versionSent}'" });
                    wanted = documents.Find(tenant, registered.Factory, name, v);
                    if (wanted is null)
                        return Results.Json(new { error = $"'{name}' has no version {v}; its newest is version {newest.Version}" },
                            statusCode: StatusCodes.Status404NotFound);
                }
                var dto = FactoryRecordsTabFold.Document(wanted, newest.Version, sources.Activity.TimeZone(tenant), nowUtc());
                FileLog.Write($"[FactoryRecordsTabEndpoints] GET document: {registered.Factory} '{name}' v{wanted.Version} of {newest.Version}");
                return Results.Json(dto);
            }));

        app.MapGet(route + "/documents/history", (HttpContext ctx, string factory) =>
            FactoryAgentsViewEndpoints.Owner(ctx, resolveTenant, $"GET factory records document history {factory}", tenant =>
            {
                if (sources.Registry.Find(tenant, factory) is not { } registered) return NotRegistered(factory);
                var name = ctx.Request.Query["name"].ToString();
                if (string.IsNullOrWhiteSpace(name)) return Results.BadRequest(new { error = "name is the document whose versions to read, and it is required" });
                int? before = null;
                if (ctx.Request.Query.TryGetValue("before", out var beforeSent))
                {
                    if (!int.TryParse(beforeSent.ToString(), out var b) || b < 1)
                        return Results.BadRequest(new { error = $"before is the version a page starts below: a whole number from 1, not '{beforeSent}'" });
                    before = b;
                }
                var newest = documents.Get(tenant, registered.Factory, name);
                if (newest is null) return NoDocument(registered.Factory, name);
                var page = documents.History(tenant, registered.Factory, name, before, FactoryRecordsTabFold.HistoryPage);
                var dto = FactoryRecordsTabFold.DocumentHistory(newest.Name, page, newest.Version, firstPage: before is null,
                    sources.Activity.TimeZone(tenant), nowUtc());
                FileLog.Write($"[FactoryRecordsTabEndpoints] GET document history: {registered.Factory} '{name}' rows={dto.Rows.Count} of {page.Total}");
                return Results.Json(dto);
            }));

        app.MapGet(route + "/attachments/{id:guid}", (HttpContext ctx, string factory, Guid id) =>
            FactoryAgentsViewEndpoints.Owner(ctx, resolveTenant, $"GET factory records attachment {factory}/{id}", tenant =>
            {
                if (sources.Registry.Find(tenant, factory) is not { } registered) return NotRegistered(factory);
                var found = attachments.Get(tenant, registered.Factory, id);
                var bytes = found is null ? null : attachments.Content(tenant, registered.Factory, id);
                if (found is null || bytes is null)
                    return Results.Json(new { error = $"{registered.Factory} has no attachment {id}" }, statusCode: StatusCodes.Status404NotFound);
                FileLog.Write($"[FactoryRecordsTabEndpoints] GET attachment: {registered.Factory} {id} '{found.Name}' bytes={bytes.Length}");
                return Results.File(bytes, found.ContentType, Path.GetFileName(found.Name));
            }));

        FileLog.Write($"[FactoryRecordsTabEndpoints] mapped {route} and its journal, documents and attachments");
    }

    private static IResult NoDocument(string factory, string name) =>
        Results.Json(new { error = $"{factory} has no working document '{name}'" }, statusCode: StatusCodes.Status404NotFound);

    private static IResult NotRegistered(string factory) =>
        Results.Json(new { error = $"There is no factory '{factory}' in this account." }, statusCode: StatusCodes.Status404NotFound);
}
