using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Records;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// THE KIT A DIRECTOR WRITES BEFORE A SEAT'S AGENT STARTS (Factory Definition, phase 5, design section 4).
///
///   GET /factory-definitions/{factory}/versions/{version}/kit?seat=&lt;id&gt;&amp;kind=seat|rules-only
///
/// The Gateway composes the kit (<see cref="FactoryKitComposer"/>) and the Director writes it, word for word (rule 7).
///
/// A DIRECTOR'S OWN CREDENTIAL ONLY, as the memory download is (<see cref="FactoryMemoryEndpoints"/>). The Director
/// builds the kit before the agent starts, and at that moment the session has no key of its own yet. A person's device
/// and a session's key are refused here in words; a session's key is refused before this by
/// <see cref="SessionKeyGuard"/>, which does not list this route, so a seat cannot read another seat's kit.
///
/// A KIT IS NEVER BUILT FROM A DRAFT. A version that is not published is refused, and so is a seat the version does
/// not hold: the Director stops the start with the refusal as its reason.
/// </summary>
internal static class FactoryKitEndpoints
{
    internal const string Route = FactoryDefinitionEndpoints.Prefix + "/{factory}/versions/{version}/kit";

    /// <summary>The refusal for any caller that is not a Director.</summary>
    internal const string OnlyADirector =
        "a seat's kit is downloaded by the Director that starts the seat, with that Director's own credential";

    /// <summary>The longest attachment list a kit carries; the rest are counted, not listed.</summary>
    internal const int AttachmentsListed = FactoryAttachmentStore.DefaultListPage;

    /// <summary>The stores a kit is built from.</summary>
    internal sealed record Stores(
        FactoryDefinitionStore Definitions,
        FactoryJournalStore Journal,
        FactoryDocumentStore Documents,
        FactoryAttachmentStore Attachments);

    public static void Map(IEndpointRouteBuilder app, Func<HttpContext, TenantId?> resolveTenant, Stores stores,
        Func<DateTime> nowUtc)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(stores);
        ArgumentNullException.ThrowIfNull(nowUtc);
        app.MapGet(Route, (HttpContext ctx, string factory, string version) =>
            Kit(ctx, factory, version, stores, resolveTenant, nowUtc));
        FileLog.Write($"[FactoryKitEndpoints] mapped {Route}");
    }

    /// <summary>GET a seat's kit, composed from one published version.</summary>
    internal static IResult Kit(HttpContext ctx, string factory, string version, Stores stores,
        Func<HttpContext, TenantId?> resolveTenant, Func<DateTime> nowUtc)
    {
        if (resolveTenant(ctx) is not { } tenant)
            return Results.Json(new { error = "no account is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);

        // A Director only: a VERIFIED credential that is neither a session's key nor a person's device.
        if (!ctx.Items.ContainsKey(AuthMiddleware.AuthenticatedCredentialItemKey) || !WorkspaceEndpoints.IsDirectorCredential(ctx))
        {
            FileLog.Write($"[FactoryKitEndpoints] REFUSED a kit of '{factory}' to a caller that is not a Director");
            return Results.Json(new
            {
                error = OnlyADirector,
                detail = "A person reads a definition with GET /factory-definitions/{factory}; a session reads its own kit in the folder CC_FACTORY_KIT_DIR names.",
            }, statusCode: StatusCodes.Status403Forbidden);
        }

        if (!FactoryNames.TryFactory(factory, out var folded, out var badFactory))
            return Results.BadRequest(new { error = badFactory, outcome = FactoryDefinitionOutcome.BadFactory.ToString() });
        if (!int.TryParse(version, out var wanted) || wanted < 1)
            return Results.BadRequest(new { error = $"a version is a whole number from 1, not '{version}'" });

        var seat = ctx.Request.Query["seat"].ToString().Trim();
        if (seat.Length == 0)
            return Results.BadRequest(new { error = "name the seat the kit is for: pass ?seat=<id>" });
        var kind = ctx.Request.Query["kind"].ToString().Trim();
        if (!FactoryKits.IsKnown(kind))
            return Results.BadRequest(new
            {
                error = $"a kit is '{FactoryKits.Seat}' or '{FactoryKits.RulesOnly}', not '{kind}': pass ?kind=seat for a seat start or ?kind=rules-only for a child of a seat",
            });

        FileLog.Write($"[FactoryKitEndpoints] Kit: factory={folded}, version={wanted}, seat={seat}, kind={kind}");
        var found = stores.Definitions.Find(tenant, folded, wanted);
        if (found is null)
        {
            var standing = stores.Definitions.Standing(tenant, folded);
            return Refused(StatusCodes.Status404NotFound, standing.Newest is null
                ? $"{folded} has no definition on the Gateway, so no kit was built"
                : $"version {wanted} of the definition of {folded} does not exist, so no kit was built; its newest version is {standing.Newest.Version}");
        }
        if (found.Summary.State != FactoryDefinitionStates.Published)
            return Refused(StatusCodes.Status409Conflict,
                $"version {wanted} of the definition of {folded} is a draft, and a kit is built only from a published version, so no kit was built. The owner publishes it first.");

        var read = FactoryDefinitionCheck.Read(found.Files);
        if (read.Definition is not { } definition)
        {
            FileLog.Write($"[FactoryKitEndpoints] Kit FAILED: published version {wanted} of {folded} does not read: {read.Findings.FirstOrDefault()?.Message}");
            return Refused(StatusCodes.Status500InternalServerError,
                $"version {wanted} of the definition of {folded} is published but could not be read, so no kit was built: {read.Findings.FirstOrDefault()?.Message}");
        }
        if (!definition.Seats.Any(s => string.Equals(s.Seat, seat, StringComparison.Ordinal)))
            return Refused(StatusCodes.Status404NotFound,
                $"version {wanted} of the definition of {folded} has no seat '{seat}', so no kit was built. Its seats are: {string.Join(", ", definition.Seats.Select(s => s.Seat))}.");

        // The records only a seat kit carries. A child (rules-only) reads none of them.
        var journal = new List<FactoryKitJournalEntry>();
        var journalTotal = 0;
        IReadOnlyList<FactoryDocumentSummary> documents = Array.Empty<FactoryDocumentSummary>();
        var attachments = new List<FactoryKitAttachment>();
        var attachmentsTotal = 0;
        if (kind == FactoryKits.Seat)
        {
            var page = stores.Journal.List(tenant, folded, seat, limit: FactoryKitComposer.MaxJournalEntries);
            journalTotal = page.Total;
            foreach (var summary in page.Entries)
                if (stores.Journal.Get(tenant, folded, summary.Id) is { } entry)
                    journal.Add(new FactoryKitJournalEntry(entry.Id, entry.EntryDate, entry.Text, entry.AuthorKind, entry.AuthorId, entry.WrittenAtUtc));
            documents = stores.Documents.List(tenant, folded);
            var kept = stores.Attachments.List(tenant, folded, limit: AttachmentsListed);
            attachmentsTotal = kept.Total;
            attachments.AddRange(kept.Attachments.Select(a => new FactoryKitAttachment(a.Id, a.Name, a.Seat, a.Bytes, a.WrittenAtUtc)));
        }

        var files = FactoryKitComposer.Compose(new FactoryKitSource(folded, wanted, found.Files, definition, seat, kind, nowUtc(),
            journal, journalTotal, documents, attachments, attachmentsTotal));
        FileLog.Write($"[FactoryKitEndpoints] Kit: factory={folded}, version={wanted}, seat={seat}, kind={kind}, files={files.Count}");
        return Results.Json(new FactoryKitResponse
        {
            Factory = folded,
            Seat = seat,
            Version = wanted,
            Kind = kind,
            Files = files.ToList(),
        });
    }

    private static IResult Refused(int status, string error)
    {
        FileLog.Write($"[FactoryKitEndpoints] REFUSED ({status}): {error}");
        return Results.Json(new { error }, statusCode: status);
    }
}
