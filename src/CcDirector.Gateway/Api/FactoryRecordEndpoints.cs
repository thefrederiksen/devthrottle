using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Records;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// THE GATEWAY CALLS THAT READ AND WRITE A FACTORY'S RECORDS (Factory Definition mission, phase 2, section 5.6):
/// its journals, its working documents and its attachments.
///
///   GET  /factory-records/{factory}/journal               entries, newest day first, WITHOUT text
///                                                         (?seat= ?factoryOnly=true ?since=&amp;until=yyyy-MM-dd ?limit=1..200)
///   GET  /factory-records/{factory}/journal/{id}          one entry, with its text
///   POST /factory-records/{factory}/journal               add an entry - append-only, never edited
///   GET  /factory-records/{factory}/documents             the documents as they stand, WITHOUT text
///   GET  /factory-records/{factory}/documents/read        one document (?name=, and ?version= for an older one)
///   GET  /factory-records/{factory}/documents/history     one page of its versions, WITHOUT text (?name= ?before= ?limit=)
///   PUT  /factory-records/{factory}/documents             write one, stating the version last read
///   GET  /factory-records/{factory}/attachments           the files, newest first, WITHOUT bytes (?seat= ?limit=1..1000)
///   GET  /factory-records/{factory}/attachments/{id}      one file, with its bytes
///   POST /factory-records/{factory}/attachments           keep one file
///
/// A DOCUMENT'S NAME TRAVELS IN THE QUERY OR THE BODY, NEVER THE PATH: it is itself a path (<c>outreach/ledger.json</c>),
/// and a route that took it as a segment would split it.
///
/// WHO IS ASKING is read exactly as it is for a factory's briefs, schedules and triggers
/// (<see cref="FactoryNaming.TryAct"/>): a person, or a session of that factory. A session of another factory, or of
/// none, is refused in that gate's own words.
///
/// WHO MAY WRITE. A person, a session of the factory, or the ONE-TIME UPLOAD. The upload runs on a machine and stores
/// the records of every factory folder on it, so it cannot use one session's key (that key reaches one factory) and
/// the command line holds no person's key. It uses the account's machine token (the Delivery Lead's ruling of 14
/// October 2026), within three limits: the token is accepted on the three WRITE routes only - never to read a record;
/// only on a row the upload names with an import key; and only with the name of the machine it runs on
/// (<see cref="UploadMachineHeader"/>), which every row it writes records as its author. So the machine token gains
/// the upload and nothing else. Every row records which of the three wrote it.
/// </summary>
internal static class FactoryRecordEndpoints
{
    internal const string Prefix = "/factory-records";

    /// <summary>The header the one-time upload names its machine in. Every row it writes records that machine.</summary>
    internal const string UploadMachineHeader = "X-DevThrottle-Upload-Machine";

    /// <summary>The longest a machine's name may be, as Windows and the Director's registration both bound it.</summary>
    internal const int MaxMachineLength = 64;

    public static void Map(IEndpointRouteBuilder app,
        Func<HttpContext, TenantId?> resolveTenant,
        FactoryJournalStore journal,
        FactoryDocumentStore documents,
        FactoryAttachmentStore attachments,
        Func<string, SessionFactoryLookup> sessionFactoryOf,
        Func<DateTime> nowUtc)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(sessionFactoryOf);
        ArgumentNullException.ThrowIfNull(nowUtc);

        const string root = Prefix + "/{factory}";
        app.MapGet(root + "/journal", (HttpContext ctx, string factory) =>
            ListJournal(ctx, factory, journal, resolveTenant, sessionFactoryOf));
        app.MapGet(root + "/journal/{id}", (HttpContext ctx, string factory, string id) =>
            ReadJournal(ctx, factory, id, journal, resolveTenant, sessionFactoryOf));
        app.MapPost(root + "/journal", (HttpContext ctx, string factory, AddFactoryJournalEntryRequest? body) =>
            AddJournal(ctx, factory, body, journal, resolveTenant, sessionFactoryOf, nowUtc));

        app.MapGet(root + "/documents", (HttpContext ctx, string factory) =>
            ListDocuments(ctx, factory, documents, resolveTenant, sessionFactoryOf));
        app.MapGet(root + "/documents/read", (HttpContext ctx, string factory) =>
            ReadDocument(ctx, factory, documents, resolveTenant, sessionFactoryOf));
        app.MapGet(root + "/documents/history", (HttpContext ctx, string factory) =>
            DocumentHistory(ctx, factory, documents, resolveTenant, sessionFactoryOf));
        app.MapPut(root + "/documents", (HttpContext ctx, string factory, WriteFactoryDocumentRequest? body) =>
            WriteDocument(ctx, factory, body, documents, resolveTenant, sessionFactoryOf, nowUtc));

        app.MapGet(root + "/attachments", (HttpContext ctx, string factory) =>
            ListAttachments(ctx, factory, attachments, resolveTenant, sessionFactoryOf));
        app.MapGet(root + "/attachments/{id}", (HttpContext ctx, string factory, string id) =>
            ReadAttachment(ctx, factory, id, attachments, resolveTenant, sessionFactoryOf));
        app.MapPost(root + "/attachments", (HttpContext ctx, string factory, AddFactoryAttachmentRequest? body) =>
            AddAttachment(ctx, factory, body, attachments, resolveTenant, sessionFactoryOf, nowUtc));

        FileLog.Write($"[FactoryRecordEndpoints] mapped {root}/journal, /documents and /attachments");
    }

    // ---------- the journal ----------

    internal static IResult ListJournal(HttpContext ctx, string factory, FactoryJournalStore store,
        Func<HttpContext, TenantId?> resolveTenant, Func<string, SessionFactoryLookup> sessionFactoryOf)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        if (!TryReader(ctx, sessionFactoryOf, factory, $"GET {Prefix}/{factory}/journal", out var folded, out var error)) return error!;

        var query = ctx.Request.Query;
        var seat = query["seat"].ToString();
        if (!FactoryRecordNames.TryOptionalSeat(seat, out _, out var seatRefusal))
            return Results.BadRequest(new { error = seatRefusal });
        var factoryOnly = string.Equals(query["factoryOnly"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
        if (factoryOnly && !string.IsNullOrWhiteSpace(seat))
            return Results.BadRequest(new { error = "seat names one seat's journal and factoryOnly the factory's own; send one of them" });
        string? since = null, until = null;
        foreach (var (key, assign) in new (string, Action<string>)[] { ("since", v => since = v), ("until", v => until = v) })
        {
            var sent = query[key].ToString();
            if (sent.Length == 0) continue;
            if (!FactoryRecordNames.TryDate(sent, out var day, out var refusal))
                return Results.BadRequest(new { error = $"{key}: {refusal}" });
            assign(day);
        }
        if (!TryLimit(ctx, FactoryJournalStore.DefaultListPage, FactoryJournalStore.MaxListPage, "entries", out var limit, out error))
            return error!;

        var list = store.List(tenant, folded, seat, factoryOnly, since, until, limit);
        return Results.Json(new FactoryJournalListResponse
        {
            Factory = folded,
            Total = list.Total,
            Entries = list.Entries.Select(e => new FactoryJournalEntryDto
            {
                Id = e.Id,
                Factory = folded,
                Seat = e.Seat,
                Date = e.EntryDate,
                TextBytes = e.TextBytes,
                AttachmentIds = Ids(e.AttachmentIds),
                AuthorKind = e.AuthorKind,
                AuthorId = e.AuthorId,
                WrittenAtUtc = e.WrittenAtUtc,
                Source = e.ImportKey,
            }).ToList(),
        });
    }

    internal static IResult ReadJournal(HttpContext ctx, string factory, string id, FactoryJournalStore store,
        Func<HttpContext, TenantId?> resolveTenant, Func<string, SessionFactoryLookup> sessionFactoryOf)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        if (!TryReader(ctx, sessionFactoryOf, factory, $"GET {Prefix}/{factory}/journal/{id}", out var folded, out var error)) return error!;
        if (!Guid.TryParse(id, out var wanted))
            return Results.BadRequest(new { error = $"a journal entry's id is the id the Gateway gave it, and '{id}' is not one" });
        if (store.Get(tenant, folded, wanted) is not { } entry)
            return Results.NotFound(new { error = $"'{folded}' has no journal entry {wanted}" });
        return Results.Json(ToDto(entry));
    }

    internal static IResult AddJournal(HttpContext ctx, string factory, AddFactoryJournalEntryRequest? body,
        FactoryJournalStore store, Func<HttpContext, TenantId?> resolveTenant,
        Func<string, SessionFactoryLookup> sessionFactoryOf, Func<DateTime> nowUtc)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        var route = $"POST {Prefix}/{factory}/journal";
        if (body is null) return NoBody();
        if (!TryWriter(ctx, sessionFactoryOf, factory, route, body.ImportKey, out var writer, out var error)) return error!;
        var write = store.Append(tenant, writer.Factory, body.Seat, body.Date, body.Text, body.AttachmentIds,
            writer.Kind, writer.Id, nowUtc(), body.ImportKey);
        if (write.Ok) return Written(write.Outcome, entry: ToDto(write.Entry!));
        return Refused(route, write.Outcome, write.Refusal!, current: write.Entry is null ? null : ToDto(write.Entry));
    }

    // ---------- working documents ----------

    internal static IResult ListDocuments(HttpContext ctx, string factory, FactoryDocumentStore store,
        Func<HttpContext, TenantId?> resolveTenant, Func<string, SessionFactoryLookup> sessionFactoryOf)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        if (!TryReader(ctx, sessionFactoryOf, factory, $"GET {Prefix}/{factory}/documents", out var folded, out var error)) return error!;
        return Results.Json(new FactoryDocumentListResponse
        {
            Factory = folded,
            MaxDocumentBytes = FactoryDocumentStore.MaxDocumentBytes,
            Documents = store.List(tenant, folded).Select(s => ToDto(folded, s)).ToList(),
        });
    }

    internal static IResult ReadDocument(HttpContext ctx, string factory, FactoryDocumentStore store,
        Func<HttpContext, TenantId?> resolveTenant, Func<string, SessionFactoryLookup> sessionFactoryOf)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        if (!TryReader(ctx, sessionFactoryOf, factory, $"GET {Prefix}/{factory}/documents/read", out var folded, out var error)) return error!;
        if (!TryDocumentName(ctx, out var name, out error)) return error!;

        var versionSent = ctx.Request.Query["version"].ToString();
        if (versionSent.Length > 0)
        {
            if (!int.TryParse(versionSent, out var version) || version < 1)
                return Results.BadRequest(new { error = $"a version is a whole number from 1, not '{versionSent}'" });
            if (store.Find(tenant, folded, name, version) is not { } found)
                return Results.NotFound(new
                {
                    error = $"'{name}' of {folded} has no version {version}",
                    newestVersion = store.Get(tenant, folded, name)?.Version ?? 0,
                });
            return Results.Json(ToDto(found, withText: true));
        }

        if (store.Get(tenant, folded, name) is not { } newest)
            return Results.NotFound(new
            {
                error = $"'{folded}' has no working document called '{name}'",
                detail = "Write its first version expecting version 0.",
                newestVersion = 0,
            });
        return Results.Json(ToDto(newest, withText: true));
    }

    internal static IResult DocumentHistory(HttpContext ctx, string factory, FactoryDocumentStore store,
        Func<HttpContext, TenantId?> resolveTenant, Func<string, SessionFactoryLookup> sessionFactoryOf)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        if (!TryReader(ctx, sessionFactoryOf, factory, $"GET {Prefix}/{factory}/documents/history", out var folded, out var error)) return error!;
        if (!TryDocumentName(ctx, out var name, out error)) return error!;
        if (!TryLimit(ctx, FactoryDocumentStore.DefaultHistoryPage, FactoryDocumentStore.MaxHistoryPage, "versions", out var limit, out error))
            return error!;
        int? before = null;
        var beforeSent = ctx.Request.Query["before"].ToString();
        if (beforeSent.Length > 0)
        {
            if (!int.TryParse(beforeSent, out var b) || b < 1)
                return Results.BadRequest(new
                {
                    error = $"before is the version a page starts below: a whole number from 1, not '{beforeSent}'",
                    detail = "Send the nextBefore of the page before it, or leave before out for the newest page.",
                });
            before = b;
        }

        var page = store.History(tenant, folded, name, before, limit);
        return Results.Json(new FactoryDocumentHistoryResponse
        {
            Factory = folded,
            Name = name,
            Total = page.Total,
            NextBefore = page.NextBefore,
            Versions = page.Versions.Select(v => ToDto(folded, v)).ToList(),
        });
    }

    internal static IResult WriteDocument(HttpContext ctx, string factory, WriteFactoryDocumentRequest? body,
        FactoryDocumentStore store, Func<HttpContext, TenantId?> resolveTenant,
        Func<string, SessionFactoryLookup> sessionFactoryOf, Func<DateTime> nowUtc)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        var route = $"PUT {Prefix}/{factory}/documents";
        if (body is null) return NoBody();
        if (!TryWriter(ctx, sessionFactoryOf, factory, route, body.ImportKey, out var writer, out var error)) return error!;
        var write = store.Write(tenant, writer.Factory, body.Name ?? "", body.Text, body.ExpectedVersion,
            writer.Kind, writer.Id, nowUtc(), body.ImportKey);
        if (write.Ok) return Written(write.Outcome, document: ToDto(write.Document!, withText: false));
        // THE CURRENT TEXT RIDES A STALE REFUSAL. Without it a refused writer must read, merge and race again; with
        // it, it already holds what it needs to merge.
        return Refused(route, write.Outcome, write.Refusal!,
            current: write.Document is null ? null : ToDto(write.Document, withText: true));
    }

    // ---------- attachments ----------

    internal static IResult ListAttachments(HttpContext ctx, string factory, FactoryAttachmentStore store,
        Func<HttpContext, TenantId?> resolveTenant, Func<string, SessionFactoryLookup> sessionFactoryOf)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        if (!TryReader(ctx, sessionFactoryOf, factory, $"GET {Prefix}/{factory}/attachments", out var folded, out var error)) return error!;
        var seat = ctx.Request.Query["seat"].ToString();
        if (!FactoryRecordNames.TryOptionalSeat(seat, out _, out var seatRefusal))
            return Results.BadRequest(new { error = seatRefusal });
        if (!TryLimit(ctx, FactoryAttachmentStore.DefaultListPage, FactoryAttachmentStore.MaxListPage, "attachments", out var limit, out error))
            return error!;

        var list = store.List(tenant, folded, seat, limit);
        return Results.Json(new FactoryAttachmentListResponse
        {
            Factory = folded,
            Total = list.Total,
            FactoryBytes = list.FactoryBytes,
            MaxAttachmentBytes = FactoryAttachmentStore.MaxAttachmentBytes,
            MaxFactoryBytes = FactoryAttachmentStore.MaxFactoryBytes,
            Attachments = list.Attachments.Select(a => ToDto(a, content: null)).ToList(),
        });
    }

    internal static IResult ReadAttachment(HttpContext ctx, string factory, string id, FactoryAttachmentStore store,
        Func<HttpContext, TenantId?> resolveTenant, Func<string, SessionFactoryLookup> sessionFactoryOf)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        if (!TryReader(ctx, sessionFactoryOf, factory, $"GET {Prefix}/{factory}/attachments/{id}", out var folded, out var error)) return error!;
        if (!Guid.TryParse(id, out var wanted))
            return Results.BadRequest(new { error = $"an attachment's id is the id the Gateway gave it, and '{id}' is not one" });
        if (store.Get(tenant, folded, wanted) is not { } attachment)
            return Results.NotFound(new { error = $"'{folded}' holds no attachment {wanted}" });
        return Results.Json(ToDto(attachment, store.Content(tenant, folded, wanted)));
    }

    internal static IResult AddAttachment(HttpContext ctx, string factory, AddFactoryAttachmentRequest? body,
        FactoryAttachmentStore store, Func<HttpContext, TenantId?> resolveTenant,
        Func<string, SessionFactoryLookup> sessionFactoryOf, Func<DateTime> nowUtc)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        var route = $"POST {Prefix}/{factory}/attachments";
        if (body is null) return NoBody();
        if (!TryWriter(ctx, sessionFactoryOf, factory, route, body.ImportKey, out var writer, out var error)) return error!;
        // Absent is not empty: a file of no bytes is "" and is kept; a body with no field at all is a mistake.
        if (body.ContentBase64 is null)
            return Results.BadRequest(new { error = "an attachment needs its bytes, base64, in contentBase64" });
        byte[] content;
        try
        {
            content = Convert.FromBase64String(body.ContentBase64!);
        }
        catch (FormatException)
        {
            return Results.BadRequest(new { error = "contentBase64 is not base64, so there are no bytes to keep" });
        }
        var write = store.Add(tenant, writer.Factory, body.Seat, body.Name ?? "", body.ContentType, content,
            writer.Kind, writer.Id, nowUtc(), body.ImportKey);
        if (write.Ok) return Written(write.Outcome, attachment: ToDto(write.Attachment!, content: null));
        return Refused(route, write.Outcome, write.Refusal!,
            current: write.Attachment is null ? null : ToDto(write.Attachment, content: null));
    }

    // ---------- who is asking ----------

    /// <summary>Who wrote a record, and the factory's one spelling.</summary>
    internal readonly record struct Writer(string Factory, string Kind, string? Id);

    /// <summary>
    /// MAY THIS CALLER READ THIS FACTORY'S RECORDS. A person, or a session of the factory: the gate is
    /// <see cref="FactoryNaming.TryAct"/>, the one every change to a factory's briefs, schedules and triggers already
    /// passes, so there is one reading of who is asking rather than two that could drift. A credential that is NEITHER
    /// - the account's machine token, a Director - is refused here, where that gate would let it through: the machine
    /// token is allowed the upload's writes and nothing more.
    /// </summary>
    private static bool TryReader(HttpContext ctx, Func<string, SessionFactoryLookup> factoryOf, string factory,
        string route, out string folded, out IResult? error)
    {
        if (!TryFactory(ctx, factoryOf, factory, route, out folded, out error)) return false;
        if (IsPerson(ctx) || AuthMiddleware.CallingSession(ctx) is not null) return true;
        FileLog.Write($"[FactoryRecordEndpoints] {route}: REFUSED a read by a credential that is neither a person nor a session");
        error = Results.Json(new
        {
            error = "a factory's records are read by a person, or by a session of the factory",
            detail = "The account's machine token may only write the rows the one-time upload names; it reads no record.",
        }, statusCode: StatusCodes.Status403Forbidden);
        return false;
    }

    /// <summary>The factory's one spelling, and the gate every factory route passes.</summary>
    private static bool TryFactory(HttpContext ctx, Func<string, SessionFactoryLookup> factoryOf, string factory,
        string route, out string folded, out IResult? error)
    {
        if (!Factory.FactoryNames.TryFactory(factory, out folded, out var refusal))
        {
            error = Results.BadRequest(new { error = refusal, detail = SpawnFactory.OneSpelling });
            return false;
        }
        return FactoryNaming.TryAct(ctx, factoryOf, folded, "record", route, out error);
    }

    private static bool IsPerson(HttpContext ctx)
    {
        var deviceType = ctx.Items.TryGetValue(AuthMiddleware.DeviceTypeItemKey, out var dt) ? dt as string : null;
        return SessionOriginSurfaces.FromDeviceType(deviceType) != SessionOriginSurfaces.Unknown;
    }

    /// <summary>The machine the upload says it runs on, or null when the header is absent or is not a machine's name.</summary>
    private static string? UploadMachine(HttpContext ctx)
    {
        var sent = ctx.Request.Headers[UploadMachineHeader].ToString().Trim();
        if (sent.Length is 0 or > MaxMachineLength) return null;
        return sent.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') ? sent : null;
    }

    /// <summary>
    /// MAY THIS CALLER WRITE, AND WHO IS IT. A person and a session of the factory write as themselves. The account's
    /// machine token writes as the one-time upload, and only rows the upload names (see the class summary). A person
    /// or a session that names a row is refused too: an import key is the upload's word for "I stored this file
    /// before", and anyone else using it could stop the upload from storing the file that key belongs to.
    /// </summary>
    private static bool TryWriter(HttpContext ctx, Func<string, SessionFactoryLookup> factoryOf, string factory,
        string route, string? importKey, out Writer writer, out IResult? error)
    {
        writer = default;
        if (!TryFactory(ctx, factoryOf, factory, route, out var folded, out error)) return false;
        var named = !string.IsNullOrWhiteSpace(importKey);

        string kind;
        string? id;
        if (IsPerson(ctx))
        {
            kind = FactoryRecordAuthorKinds.Person;
            id = FleetManagerOwnerDevice.Caller(ctx)?.Actor;
        }
        else if (AuthMiddleware.CallingSession(ctx) is { } session)
        {
            kind = FactoryRecordAuthorKinds.Session;
            id = session.SessionId.ToString();
        }
        else if (ctx.Items.ContainsKey(AuthMiddleware.AuthenticatedCredentialItemKey) && named)
        {
            kind = FactoryRecordAuthorKinds.Upload;
            id = UploadMachine(ctx);
            if (id is null)
            {
                FileLog.Write($"[FactoryRecordEndpoints] {route}: REFUSED an upload that named no machine");
                error = Results.BadRequest(new
                {
                    error = $"the one-time upload names the machine it runs on in the {UploadMachineHeader} header, and this request did not",
                    detail = $"Every row the upload writes records the machine its file came from: letters, digits, hyphens, " +
                             $"underscores and dots, at most {MaxMachineLength} characters.",
                });
                return false;
            }
        }
        else
        {
            FileLog.Write($"[FactoryRecordEndpoints] {route}: REFUSED - neither a person, a session, nor the upload");
            error = Results.Json(new
            {
                error = "a factory's records are written by a person, by a session of the factory, or by the one-time upload",
                detail = "Every record says who wrote it. This request carried neither a session's key nor a person's " +
                         "device key, and it was not the upload: the machine token writes only the rows the upload names.",
            }, statusCode: StatusCodes.Status403Forbidden);
            return false;
        }

        if (named && kind != FactoryRecordAuthorKinds.Upload)
        {
            FileLog.Write($"[FactoryRecordEndpoints] {route}: REFUSED - a {kind} sent an import key");
            error = Results.BadRequest(new
            {
                error = "only the one-time upload names the rows it stores; leave importKey out",
                detail = "An import key is the upload's record that it stored one file of a machine's records. Written " +
                         "by anyone else it could stop the upload from storing the file it belongs to.",
            });
            return false;
        }

        writer = new Writer(folded, kind, id);
        return true;
    }

    // ---------- answers ----------

    private static IResult Written(FactoryRecordOutcome outcome, FactoryJournalEntryDto? entry = null,
        FactoryDocumentDto? document = null, FactoryAttachmentDto? attachment = null)
        => Results.Json(new FactoryRecordWriteResponse
        {
            Outcome = outcome.ToString(),
            Entry = entry,
            Document = document,
            Attachment = attachment,
        });

    private static IResult Refused(string route, FactoryRecordOutcome outcome, string refusal, object? current)
    {
        var status = outcome switch
        {
            FactoryRecordOutcome.BadFactory or FactoryRecordOutcome.BadSeat or FactoryRecordOutcome.BadName
                or FactoryRecordOutcome.Empty => StatusCodes.Status400BadRequest,
            // An over-cap file is a request too large for this route, whatever the factory holds.
            FactoryRecordOutcome.TooLarge => StatusCodes.Status413PayloadTooLarge,
            FactoryRecordOutcome.NoSuchAttachment => StatusCodes.Status404NotFound,
            // Stale, a full factory, too many attachments, an upload whose file changed: "not as things stand".
            _ => StatusCodes.Status409Conflict,
        };
        FileLog.Write($"[FactoryRecordEndpoints] {route}: REFUSED ({status}, {outcome}) - {refusal}");
        return Results.Json(new { error = refusal, outcome = outcome.ToString(), current }, statusCode: status);
    }

    private static bool TryDocumentName(HttpContext ctx, out string name, out IResult? error)
    {
        error = null;
        if (!FactoryRecordNames.TryName(ctx.Request.Query["name"].ToString(), "document name", out name, out var refusal))
        {
            error = Results.BadRequest(new { error = refusal, detail = "Name the document in the query: ?name=outreach/ledger.json" });
            return false;
        }
        return true;
    }

    /// <summary>A limit that is not one is refused in words rather than quietly replaced, because a reader that asked
    /// for 5000 and got 1000 would believe it had seen them all.</summary>
    private static bool TryLimit(HttpContext ctx, int fallback, int max, string what, out int limit, out IResult? error)
    {
        error = null;
        limit = fallback;
        var sent = ctx.Request.Query["limit"].ToString();
        if (sent.Length == 0) return true;
        if (int.TryParse(sent, out limit) && limit >= 1 && limit <= max) return true;
        error = Results.BadRequest(new { error = $"limit is how many {what} one answer holds: a whole number from 1 to {max}, not '{sent}'" });
        return false;
    }

    private static List<Guid> Ids(string joined)
        => joined.Length == 0 ? new List<Guid>() : joined.Split(',').Select(Guid.Parse).ToList();

    private static FactoryJournalEntryDto ToDto(FactoryJournalEntryEntity e) => new()
    {
        Id = e.Id,
        Factory = e.Factory,
        Seat = e.Seat,
        Date = e.EntryDate,
        Text = e.Text,
        TextBytes = e.TextBytes,
        AttachmentIds = Ids(e.AttachmentIds),
        AuthorKind = e.AuthorKind,
        AuthorId = e.AuthorId,
        WrittenAtUtc = e.WrittenAtUtc,
        Source = e.ImportKey,
    };

    private static FactoryDocumentDto ToDto(FactoryDocumentVersionEntity d, bool withText) => new()
    {
        Factory = d.Factory,
        Name = d.Name,
        Version = d.Version,
        Text = withText ? d.Text : null,
        TextBytes = d.TextBytes,
        AuthorKind = d.AuthorKind,
        AuthorId = d.AuthorId,
        WrittenAtUtc = d.WrittenAtUtc,
    };

    private static FactoryDocumentDto ToDto(string factory, FactoryDocumentSummary s) => new()
    {
        Factory = factory,
        Name = s.Name,
        Version = s.Version,
        TextBytes = s.TextBytes,
        AuthorKind = s.AuthorKind,
        AuthorId = s.AuthorId,
        WrittenAtUtc = s.WrittenAtUtc,
    };

    private static FactoryAttachmentDto ToDto(FactoryAttachmentEntity a, byte[]? content) => new()
    {
        Id = a.Id,
        Factory = a.Factory,
        Seat = a.Seat,
        Name = a.Name,
        ContentType = a.ContentType,
        Bytes = a.Bytes,
        Sha256 = a.Sha256,
        AuthorKind = a.AuthorKind,
        AuthorId = a.AuthorId,
        WrittenAtUtc = a.WrittenAtUtc,
        ContentBase64 = content is null ? null : Convert.ToBase64String(content),
    };

    private static IResult NoBody() => Results.BadRequest(new { error = "a body is required" });

    private static IResult NoAccount() =>
        Results.Json(new { error = "no account is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);
}
