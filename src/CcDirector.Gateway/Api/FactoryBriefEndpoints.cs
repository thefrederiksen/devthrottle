using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Briefs;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// THE GATEWAY CALLS THAT READ AND WRITE A SEAT'S BRIEF (Factory Seats mission, phase 2a).
///
///   GET  /factory-briefs/{factory}/{seat}           the published brief - what a talk or a run is given
///   GET  /factory-briefs/{factory}/{seat}/draft     the newest version, published or not - what a save must have read
///   GET  /factory-briefs/{factory}/{seat}/history   one page of the versions, newest first, WITHOUT their text
///                                                   (?limit=1..100, 50 when not said; ?before=the version to start below)
///   GET  /factory-briefs/{factory}/{seat}/versions/{version}   one version, with its text
///   PUT  /factory-briefs/{factory}/{seat}/draft     save a draft, stating the version last read
///   POST /factory-briefs/{factory}/{seat}/publish   publish one version - A PERSON ONLY
///   POST /factory-briefs/{factory}/{seat}/restore   bring an older version back as the newest, published - A PERSON ONLY
///
/// WHO IS ASKING is read exactly as it is for a factory's schedules and triggers (<see cref="FactoryNaming.TryAct"/>):
/// a person, or a session of that factory. A session of another factory, or of none, is refused in that gate's own
/// words, because a brief decides what a session of the factory will be told to do.
///
/// A PERSON, OR A SESSION OF THE FACTORY, MAY SAVE A DRAFT. ONLY A PERSON PUBLISHES (the mission document's P3). The
/// publish and bring back routes are reachable by a session key on purpose and refused by the store in words: the
/// session key guard can only say "not for a session key", and an agent told "save a draft; the owner publishes it"
/// knows what to do next.
///
/// A SESSION'S DRAFTS ARE BOUNDED, AND SO IS EVERY READ (review 1 of phase 2a). Every version is kept for ever, so
/// what is limited is the writing and the reading: a session is refused another draft while
/// <see cref="FactoryBriefStore.MaxWaitingDrafts"/> wait above the published version, and no read here answers with
/// more than one version's text.
///
/// A CALLER THAT IS NEITHER - the account's shared machine token, or a Director - may read, as that gate allows, and
/// may not write: every version records who wrote it, a person or which session, and such a caller is neither.
///
/// A DEFINED FACTORY'S BRIEFS ARE PART OF ITS DEFINITION (Factory Definition, phase 4, design section 9). For a factory
/// with a published definition every route here answers from the definition, and the brief store is not touched: the
/// published brief is the seat's <c>brief:</c> in the published definition, the draft is the one in the newest
/// version, the history is the definition versions in which the seat's brief changed, and a version number is a
/// definition version - every answer says so (<c>versionKind</c>, <c>definition</c>). A save writes a new DEFINITION
/// draft with only that seat's brief replaced, as a text edit, and runs the whole-definition check like any definition
/// save. Publishing or restoring is the whole definition's act, so on a defined factory those two routes send the
/// person to the definition's own publish and restore. A factory with no published definition - none at all, or only
/// drafts - takes every path here exactly as before (goal 14).
/// </summary>
internal static class FactoryBriefEndpoints
{
    internal const string Prefix = "/factory-briefs";

    public static void Map(IEndpointRouteBuilder app,
        Func<HttpContext, TenantId?> resolveTenant,
        FactoryBriefStore store,
        Func<string, SessionFactoryLookup> sessionFactoryOf,
        Func<DateTime> nowUtc,
        Defined defined)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sessionFactoryOf);
        ArgumentNullException.ThrowIfNull(nowUtc);
        ArgumentNullException.ThrowIfNull(defined);

        const string seatRoute = Prefix + "/{factory}/{seat}";
        app.MapGet(seatRoute, (HttpContext ctx, string factory, string seat) =>
            ReadPublished(ctx, factory, seat, store, resolveTenant, sessionFactoryOf, defined));
        app.MapGet(seatRoute + "/draft", (HttpContext ctx, string factory, string seat) =>
            ReadNewest(ctx, factory, seat, store, resolveTenant, sessionFactoryOf, defined));
        app.MapGet(seatRoute + "/history", (HttpContext ctx, string factory, string seat) =>
            History(ctx, factory, seat, store, resolveTenant, sessionFactoryOf, defined));
        app.MapGet(seatRoute + "/versions/{version}", (HttpContext ctx, string factory, string seat, string version) =>
            ReadVersion(ctx, factory, seat, version, store, resolveTenant, sessionFactoryOf, defined));
        app.MapPut(seatRoute + "/draft", (HttpContext ctx, string factory, string seat, SaveFactoryBriefDraftRequest? body) =>
            SaveDraft(ctx, factory, seat, body, store, resolveTenant, sessionFactoryOf, nowUtc, defined));
        app.MapPost(seatRoute + "/publish", (HttpContext ctx, string factory, string seat, PublishFactoryBriefRequest? body) =>
            Publish(ctx, factory, seat, body, store, resolveTenant, sessionFactoryOf, nowUtc, defined));
        app.MapPost(seatRoute + "/restore", (HttpContext ctx, string factory, string seat, BringBackFactoryBriefRequest? body) =>
            BringBack(ctx, factory, seat, body, store, resolveTenant, sessionFactoryOf, nowUtc, defined));

        FileLog.Write($"[FactoryBriefEndpoints] mapped {seatRoute} and its draft, history, versions, publish and restore routes");
    }

    /// <summary>GET the published brief: what a talk or a run of the seat is given.</summary>
    internal static IResult ReadPublished(HttpContext ctx, string factory, string seat, FactoryBriefStore store,
        Func<HttpContext, TenantId?> resolveTenant, Func<string, SessionFactoryLookup> sessionFactoryOf, Defined defined)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        if (!TryCaller(ctx, sessionFactoryOf, factory, $"GET {Prefix}/{factory}/{seat}", out _, out var error)) return error!;
        if (defined.Briefs.IsDefined(tenant, factory))
            return DefinedRead(defined, tenant, factory, seat, d => d.Published!.Version, "published");
        if (store.WhyNotASeat(tenant, factory, seat) is { } notASeat) return Answer(notASeat, store, tenant, factory, seat);

        var standing = store.Standing(tenant, factory, seat);
        if (standing.Published is null)
            return Results.Json(new
            {
                error = standing.Newest is null
                    ? $"'{seat}' of {factory} has no brief on the Gateway"
                    : $"'{seat}' of {factory} has no published brief yet: its newest version, {standing.Newest.Version}, is a draft",
                detail = standing.Newest is null
                    ? "A seat with no brief here is told the path of its brief file, as before. Save a draft to hold its brief on the Gateway."
                    : "A talk or a run is given the published brief. A person publishes the draft.",
                newestVersion = standing.Newest?.Version ?? 0,
            }, statusCode: StatusCodes.Status404NotFound);
        return Results.Json(ToDto(standing.Published, standing));
    }

    /// <summary>GET the newest version, published or not: the text an editor starts from, and the version a save states.</summary>
    internal static IResult ReadNewest(HttpContext ctx, string factory, string seat, FactoryBriefStore store,
        Func<HttpContext, TenantId?> resolveTenant, Func<string, SessionFactoryLookup> sessionFactoryOf, Defined defined)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        if (!TryCaller(ctx, sessionFactoryOf, factory, $"GET {Prefix}/{factory}/{seat}/draft", out _, out var error)) return error!;
        if (defined.Briefs.IsDefined(tenant, factory))
            return DefinedRead(defined, tenant, factory, seat, d => d.Newest!.Version, "newest");
        if (store.WhyNotASeat(tenant, factory, seat) is { } notASeat) return Answer(notASeat, store, tenant, factory, seat);

        var standing = store.Standing(tenant, factory, seat);
        if (standing.Newest is null)
            return Results.Json(new
            {
                error = $"'{seat}' of {factory} has no brief on the Gateway",
                detail = "Save its first draft expecting version 0.",
                newestVersion = 0,
            }, statusCode: StatusCodes.Status404NotFound);
        return Results.Json(ToDto(standing.Newest, standing));
    }

    /// <summary>
    /// GET one page of the seat's history, newest first, without any version's text. <c>limit</c> is how many
    /// versions the page holds and <c>before</c> the version it starts below; a value that is not one is refused in
    /// words rather than quietly replaced, because a reader that asked for 500 and got 100 would believe it had
    /// seen them all.
    /// </summary>
    internal static IResult History(HttpContext ctx, string factory, string seat, FactoryBriefStore store,
        Func<HttpContext, TenantId?> resolveTenant, Func<string, SessionFactoryLookup> sessionFactoryOf, Defined defined)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        if (!TryCaller(ctx, sessionFactoryOf, factory, $"GET {Prefix}/{factory}/{seat}/history", out _, out var error)) return error!;
        var isDefined = defined.Briefs.IsDefined(tenant, factory);
        if (!isDefined && store.WhyNotASeat(tenant, factory, seat) is { } notASeat) return Answer(notASeat, store, tenant, factory, seat);

        var limit = FactoryBriefStore.DefaultHistoryPage;
        if (ctx.Request.Query.TryGetValue("limit", out var limitSent)
            && (!int.TryParse(limitSent.ToString(), out limit) || limit < 1 || limit > FactoryBriefStore.MaxHistoryPage))
            return Results.BadRequest(new
            {
                error = $"limit is how many versions one page holds: a whole number from 1 to {FactoryBriefStore.MaxHistoryPage}, not '{limitSent}'",
                detail = "Read an older page by sending the nextBefore of the page before it as before.",
            });
        int? before = null;
        if (ctx.Request.Query.TryGetValue("before", out var beforeSent))
        {
            if (!int.TryParse(beforeSent.ToString(), out var b) || b < 1)
                return Results.BadRequest(new
                {
                    error = $"before is the version a page starts below: a whole number from 1, not '{beforeSent}'",
                    detail = "Send the nextBefore of the page before it, or leave before out for the newest page.",
                });
            before = b;
        }

        if (isDefined) return DefinedHistory(defined, tenant, factory, seat, before, limit);

        var standing = store.Standing(tenant, factory, seat);
        var page = store.History(tenant, factory, seat, before, limit);
        return Results.Json(new FactoryBriefHistoryResponse
        {
            Factory = standing.Newest?.Factory ?? factory.Trim().ToLowerInvariant(),
            Seat = standing.Newest?.Seat ?? seat.Trim().ToLowerInvariant(),
            PublishedVersion = standing.Published?.Version,
            NewestVersion = standing.Newest?.Version ?? 0,
            Total = page.Total,
            Versions = page.Versions.Select(v => new FactoryBriefVersionSummaryDto
            {
                Version = v.Version,
                State = v.State,
                AuthorKind = v.AuthorKind,
                AuthorId = v.AuthorId,
                WrittenAtUtc = v.WrittenAtUtc,
                PublishedAtUtc = v.PublishedAtUtc,
                PublishedBy = v.PublishedBy,
                TextBytes = v.TextBytes,
            }).ToList(),
            NextBefore = page.NextBefore,
        });
    }

    /// <summary>GET one version of the seat's brief, with its text: how the history's text is read, one at a time.</summary>
    internal static IResult ReadVersion(HttpContext ctx, string factory, string seat, string version, FactoryBriefStore store,
        Func<HttpContext, TenantId?> resolveTenant, Func<string, SessionFactoryLookup> sessionFactoryOf, Defined defined)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        if (!TryCaller(ctx, sessionFactoryOf, factory, $"GET {Prefix}/{factory}/{seat}/versions/{version}", out _, out var error)) return error!;
        var isDefined = defined.Briefs.IsDefined(tenant, factory);
        if (!isDefined && store.WhyNotASeat(tenant, factory, seat) is { } notASeat) return Answer(notASeat, store, tenant, factory, seat);
        if (!int.TryParse(version, out var wanted) || wanted < 1)
            return Results.BadRequest(new { error = $"a version is a whole number from 1, not '{version}'" });
        if (isDefined) return DefinedRead(defined, tenant, factory, seat, _ => wanted, null);

        var standing = store.Standing(tenant, factory, seat);
        if (store.Find(tenant, factory, seat, wanted) is not { } found)
            return Results.Json(new
            {
                error = standing.Newest is null
                    ? $"'{seat}' of {factory} has no brief on the Gateway"
                    : $"version {wanted} of the brief of '{seat}' does not exist; its newest version is {standing.Newest.Version}",
                newestVersion = standing.Newest?.Version ?? 0,
            }, statusCode: StatusCodes.Status404NotFound);
        return Results.Json(ToDto(found, standing));
    }

    /// <summary>PUT a draft: a person, or a session of the factory, saves the next version.</summary>
    internal static IResult SaveDraft(HttpContext ctx, string factory, string seat, SaveFactoryBriefDraftRequest? body,
        FactoryBriefStore store, Func<HttpContext, TenantId?> resolveTenant,
        Func<string, SessionFactoryLookup> sessionFactoryOf, Func<DateTime> nowUtc, Defined defined)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        var route = $"PUT {Prefix}/{factory}/{seat}/draft";
        if (!TryCaller(ctx, sessionFactoryOf, factory, route, out var caller, out var error)) return error!;
        if (!TryWriter(caller, route, out error)) return error!;
        if (body is null) return NoBody();
        if (defined.Briefs.IsDefined(tenant, factory)) return DefinedSave(defined, tenant, factory, seat, body, caller, nowUtc());
        return Answer(store.SaveDraft(tenant, factory, seat, body.Text, body.ExpectedVersion, caller.Kind, caller.Id, nowUtc()),
            store, tenant, factory, seat);
    }

    /// <summary>POST a publish: a person makes one version the brief talks and runs are given.</summary>
    internal static IResult Publish(HttpContext ctx, string factory, string seat, PublishFactoryBriefRequest? body,
        FactoryBriefStore store, Func<HttpContext, TenantId?> resolveTenant,
        Func<string, SessionFactoryLookup> sessionFactoryOf, Func<DateTime> nowUtc, Defined defined)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        var route = $"POST {Prefix}/{factory}/{seat}/publish";
        if (!TryCaller(ctx, sessionFactoryOf, factory, route, out var caller, out var error)) return error!;
        if (!TryWriter(caller, route, out error)) return error!;
        if (body is null) return NoBody();
        if (defined.Briefs.IsDefined(tenant, factory))
            return DefinedPublishOrRestore(defined, tenant, caller, factory, seat, body.Version, null, body.Replaces, nowUtc(), restore: false);
        return Answer(store.Publish(tenant, factory, seat, body.Version, caller.Kind, caller.Id, nowUtc()),
            store, tenant, factory, seat);
    }

    /// <summary>POST a bring back: a person publishes a copy of an older version as the newest version.</summary>
    internal static IResult BringBack(HttpContext ctx, string factory, string seat, BringBackFactoryBriefRequest? body,
        FactoryBriefStore store, Func<HttpContext, TenantId?> resolveTenant,
        Func<string, SessionFactoryLookup> sessionFactoryOf, Func<DateTime> nowUtc, Defined defined)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        var route = $"POST {Prefix}/{factory}/{seat}/restore";
        if (!TryCaller(ctx, sessionFactoryOf, factory, route, out var caller, out var error)) return error!;
        if (!TryWriter(caller, route, out error)) return error!;
        if (body is null) return NoBody();
        if (defined.Briefs.IsDefined(tenant, factory))
            return DefinedPublishOrRestore(defined, tenant, caller, factory, seat, body.Version, body.ExpectedVersion, body.Replaces,
                nowUtc(), restore: true);
        return Answer(store.BringBack(tenant, factory, seat, body.Version, caller.Kind, caller.Id, nowUtc(), body.ExpectedVersion),
            store, tenant, factory, seat);
    }

    // ---------- a defined factory: the brief is part of the definition ----------

    /// <summary>What the routes need for a DEFINED factory: its briefs as the definition holds them, the account's
    /// fleet skills and machines the whole-definition check is run with, and the publish transaction a person's publish
    /// and restore go through. REQUIRED, so a wiring line that forgot it fails to compile rather than answering a
    /// defined factory from the brief store.</summary>
    internal sealed record Defined(FactoryDefinitionBriefs Briefs, FactoryDefinitionEndpoints.Account Account,
        FactoryPublishApply Apply);

    /// <summary>
    /// One read of a defined factory's brief: the seat's brief in the definition version <paramref name="which"/> picks,
    /// or the 404 that says what is missing. <paramref name="what"/> names that version in the sentence ("published",
    /// "newest"), or is null for a version the caller named by number.
    /// </summary>
    private static IResult DefinedRead(Defined defined, TenantId tenant, string factory, string seat,
        Func<FactoryDefinitionStanding, int> which, string? what)
    {
        if (!FactoryNames.TrySeat(seat, out var folded, out var seatRefusal))
            return Results.BadRequest(new { error = seatRefusal, outcome = FactoryBriefOutcome.BadSeat.ToString() });
        var standing = defined.Briefs.Definitions.Standing(tenant, factory);
        var factoryId = standing.Newest!.Factory;
        var version = which(standing);
        if (defined.Briefs.InVersion(tenant, factoryId, folded, version) is not { } brief)
        {
            var missing = defined.Briefs.Definitions.Find(tenant, factoryId, version) is null
                ? $"version {version} of the definition of {factoryId} does not exist; its newest version is {standing.Newest.Version}"
                : FactoryDefinitionBriefs.NotInVersion(factoryId, folded, version, what ?? "named");
            FileLog.Write($"[FactoryBriefEndpoints] {factoryId}/{folded}: defined factory, no brief in definition version {version}");
            return Results.Json(new
            {
                error = missing,
                definition = FactoryBriefPanelFold.DefinitionLine(factoryId),
                newestVersion = standing.Newest.Version,
            }, statusCode: StatusCodes.Status404NotFound);
        }
        FileLog.Write($"[FactoryBriefEndpoints] {factoryId}/{folded}: defined factory, brief from definition version {version}");
        return Results.Json(DefinedDto(brief, standing));
    }

    /// <summary>One page of a defined factory's brief history: the definition versions in which the seat's brief changed.</summary>
    private static IResult DefinedHistory(Defined defined, TenantId tenant, string factory, string seat, int? before, int limit)
    {
        if (!FactoryNames.TrySeat(seat, out var folded, out var seatRefusal))
            return Results.BadRequest(new { error = seatRefusal, outcome = FactoryBriefOutcome.BadSeat.ToString() });
        var standing = defined.Briefs.Definitions.Standing(tenant, factory);
        var factoryId = standing.Newest!.Factory;
        var page = defined.Briefs.History(tenant, factoryId, folded, before, limit);
        return Results.Json(new FactoryBriefHistoryResponse
        {
            Factory = factoryId,
            Seat = folded,
            PublishedVersion = standing.Published?.Version,
            NewestVersion = standing.Newest.Version,
            Total = page.Total,
            Versions = page.Versions.Select(v => new FactoryBriefVersionSummaryDto
            {
                Version = v.Version,
                State = v.State,
                AuthorKind = v.AuthorKind,
                AuthorId = v.AuthorId,
                WrittenAtUtc = v.WrittenAtUtc,
                PublishedAtUtc = v.PublishedAtUtc,
                PublishedBy = v.PublishedBy,
                TextBytes = v.TextBytes,
            }).ToList(),
            NextBefore = page.NextBefore,
            VersionKind = FactoryBriefVersionKinds.Definition,
            Definition = FactoryBriefPanelFold.DefinitionLine(factoryId),
            LiveVersion = page.LiveVersion,
        });
    }

    /// <summary>
    /// SAVE A DEFINED FACTORY'S BRIEF: a new DEFINITION draft that is the newest version with only this seat's
    /// <c>brief:</c> replaced, as a text edit, every other byte of every file kept. <c>expectedVersion</c> is the
    /// definition version the writer read; a stale one is refused by the definition store exactly as a definition save
    /// is, and before the check, which would otherwise judge content the writer never saw. Then the whole-definition
    /// check runs, as for any save, and a definition with an error is not saved.
    /// </summary>
    private static IResult DefinedSave(Defined defined, TenantId tenant, string factory, string seat,
        SaveFactoryBriefDraftRequest body, Caller caller, DateTime nowUtc)
    {
        var draft = defined.Briefs.DraftWith(tenant, factory, seat, body.Text ?? "");
        if (!draft.Ok)
        {
            var status = draft.Outcome switch
            {
                FactoryBriefOutcome.SeatNotRegistered => StatusCodes.Status404NotFound,
                FactoryBriefOutcome.NotEditable => StatusCodes.Status422UnprocessableEntity,
                _ => StatusCodes.Status400BadRequest,
            };
            FileLog.Write($"[FactoryBriefEndpoints] {factory}/{seat}: defined factory, draft REFUSED ({status}, {draft.Outcome}) - {draft.Refusal}");
            return Results.Json(new
            {
                error = draft.Refusal,
                outcome = draft.Outcome.ToString(),
                definition = draft.Newest is null ? null : FactoryBriefPanelFold.DefinitionLine(draft.Newest.Factory),
            }, statusCode: status);
        }

        var newest = draft.Newest!;
        var factoryId = newest.Factory;
        var seatId = draft.Seat!;
        FactoryDefinitionCheckResult? check = null;
        FactoryDefinitionWrite write;
        if (body.ExpectedVersion != newest.Version)
        {
            // THE EDIT WAS MADE ON THE NEWEST VERSION, which is not the one the writer read, so it is never offered to the
            // store: refused here with the store's own stale sentence, the check never run on content the writer did not
            // see (review of 4008, the note on DefinedSave).
            write = FactoryDefinitionStore.StaleWrite(factoryId, body.ExpectedVersion, newest);
        }
        else
        {
            check = FactoryDefinitionCheck.Check(draft.Content!, factoryId, defined.Account.FleetSkillsOf(tenant),
                defined.Account.ConnectedMachinesOf(tenant));
            if (check.HasErrors)
                return FactoryDefinitionEndpoints.CheckRefused(factoryId, check, $"The brief of '{seatId}' was not saved");

            // The store decides staleness again against what is newest NOW, so a writer who lost a race since the edit
            // was made is refused with the newest version - never saved over it.
            write = defined.Briefs.Definitions.SaveDraft(tenant, factoryId, draft.Content, body.ExpectedVersion, caller.Kind,
                caller.Id, $"The brief of '{seatId}' was edited.", nowUtc);
        }
        var standing = defined.Briefs.Definitions.Standing(tenant, factoryId);
        if (write.Ok)
        {
            var saved = defined.Briefs.InVersion(tenant, factoryId, seatId, write.Version!.Version)!;
            var dto = DefinedDto(saved, standing);
            // A save reaches the store only after the check ran (the branch above), so the check is set here.
            dto.Findings = check!.Findings.Select(f => FactoryDefinitionEndpoints.ToDto(f)).ToList();
            FileLog.Write($"[FactoryBriefEndpoints] {factoryId}/{seatId}: defined factory, brief saved as definition version {write.Version.Version} by {caller.Kind}");
            return Results.Json(dto);
        }

        var refusedStatus = FactoryDefinitionEndpoints.StatusOf(write.Outcome);
        FileLog.Write($"[FactoryBriefEndpoints] {factoryId}/{seatId}: defined factory, save REFUSED ({refusedStatus}, {write.Outcome}) - {write.Refusal}");
        // THE VERSION THE CALLER NEEDS RIDES THE REFUSAL, with this seat's brief in it, so an editor shows it beside the
        // writer's own text exactly as it does for a brief.
        var current = write.Version is { } v && defined.Briefs.InVersion(tenant, factoryId, seatId, v.Version) is { } b
            ? DefinedDto(b, standing) : null;
        return Results.Json(new
        {
            error = write.Refusal,
            outcome = write.Outcome.ToString(),
            current,
            definition = FactoryBriefPanelFold.DefinitionLine(factoryId),
        }, statusCode: refusedStatus);
    }

    /// <summary>
    /// A PUBLISH OR A RESTORE OF A DEFINED FACTORY'S BRIEF publishes DEFINITION version <paramref name="version"/>
    /// (design section 9): it is the whole definition that is published, through the phase 4 transaction that drives
    /// the factory, by the same function the definition's own publish and restore call - so every ruling, the check and
    /// every refusal are theirs. A person only, as today: a session is refused with the definition's own sentence. The
    /// version must hold this seat's brief, or the seat's route would publish a definition that has none for it. The
    /// answer is the seat's brief in the version now published, with the line saying the whole definition was
    /// published, what changes and what the transaction applied.
    /// </summary>
    private static IResult DefinedPublishOrRestore(Defined defined, TenantId tenant, Caller caller, string factory,
        string seat, int version, int? expectedVersion, int? replaces, DateTime nowUtc, bool restore)
    {
        var act = restore ? "restore" : "publish";
        if (!FactoryNames.TrySeat(seat, out var folded, out var seatRefusal))
            return Results.BadRequest(new { error = seatRefusal, outcome = FactoryBriefOutcome.BadSeat.ToString() });
        var standing = defined.Briefs.Definitions.Standing(tenant, factory);
        var factoryId = standing.Newest!.Factory;
        if (caller.Kind != FactoryMemoryAuthorKinds.Person)
        {
            FileLog.Write($"[FactoryBriefEndpoints] {factoryId}/{folded}: defined factory, a {act} by a session REFUSED");
            return Results.Json(new
            {
                error = restore ? FactoryDefinitionStore.OnlyAPersonRestores : FactoryDefinitionStore.OnlyAPersonPublishes,
                outcome = FactoryBriefOutcome.OnlyAPersonPublishes.ToString(),
                definition = FactoryBriefPanelFold.DefinitionLine(factoryId),
            }, statusCode: StatusCodes.Status403Forbidden);
        }
        if (defined.Briefs.Definitions.Find(tenant, factoryId, version) is not null
            && defined.Briefs.InVersion(tenant, factoryId, folded, version) is null)
        {
            FileLog.Write($"[FactoryBriefEndpoints] {factoryId}/{folded}: defined factory, a {act} of version {version} REFUSED - no brief for the seat");
            return Results.Json(new
            {
                error = FactoryDefinitionBriefs.NotInVersion(factoryId, folded, version, "named"),
                outcome = FactoryBriefOutcome.SeatNotRegistered.ToString(),
                definition = FactoryBriefPanelFold.DefinitionLine(factoryId),
            }, statusCode: StatusCodes.Status404NotFound);
        }

        FileLog.Write($"[FactoryBriefEndpoints] {factoryId}/{folded}: defined factory, a {act} of definition version {version} by a person");
        IResult Published(FactoryPublishResult result, FactoryDefinitionCheckResult check)
        {
            var now = defined.Briefs.Definitions.Standing(tenant, factoryId);
            var brief = defined.Briefs.InVersion(tenant, factoryId, folded, result.Write.Version!.Version)!;
            var dto = DefinedDto(brief, now);
            dto.Findings = check.Findings.Select(f => FactoryDefinitionEndpoints.ToDto(f)).ToList();
            if (result.ChangesNotice is not null) dto.Findings.Add(FactoryDefinitionEndpoints.ToDto(result.ChangesNotice));
            dto.Changes = result.Changes;
            dto.Applied = result.Applied.ToList();
            FileLog.Write($"[FactoryBriefEndpoints] {factoryId}/{folded}: definition version {brief.Version.Version} published, {dto.Applied.Count} steps applied");
            return Results.Json(dto);
        }
        return restore
            ? FactoryDefinitionEndpoints.RestoreAsPerson(tenant, factoryId, version, expectedVersion ?? 0, replaces, caller.Id,
                defined.Briefs.Definitions, defined.Apply, defined.Account, nowUtc, Published)
            : FactoryDefinitionEndpoints.PublishAsPerson(tenant, factoryId, version, replaces, caller.Id,
                defined.Briefs.Definitions, defined.Apply, defined.Account, nowUtc, Published);
    }

    /// <summary>A defined factory's brief in one definition version, as the brief routes answer it.</summary>
    private static FactoryBriefDto DefinedDto(DefinedSeatBrief brief, FactoryDefinitionStanding standing) => new()
    {
        Factory = brief.Version.Factory,
        Seat = brief.Seat,
        Version = brief.Version.Version,
        Text = brief.Text,
        State = brief.Version.State,
        AuthorKind = brief.Version.AuthorKind,
        AuthorId = brief.Version.AuthorId,
        WrittenAtUtc = brief.Version.WrittenAtUtc,
        PublishedAtUtc = brief.Version.PublishedAtUtc,
        PublishedBy = brief.Version.PublishedBy,
        NewestVersion = standing.Newest?.Version ?? brief.Version.Version,
        PublishedVersion = standing.Published?.Version,
        VersionKind = FactoryBriefVersionKinds.Definition,
        Definition = FactoryBriefPanelFold.DefinitionLine(brief.Version.Factory),
    };

    /// <summary>The caller kind of a credential that is neither a person's device nor a session's key. Deliberately
    /// NOT one of <see cref="FactoryMemoryAuthorKinds"/>: such a caller may read and can never be the author of a version.</summary>
    private const string NeitherPersonNorSession = "machine";

    /// <summary>Who is asking: a person, a session, or neither, with the identifier a version records.</summary>
    internal readonly record struct Caller(string Kind, string? Id);

    /// <summary>
    /// MAY THIS CALLER TOUCH THIS FACTORY'S BRIEFS, AND WHO IS IT. The gate is <see cref="FactoryNaming.TryAct"/>,
    /// the one every change to a factory's schedules and triggers already passes, so there is one reading of who is
    /// asking rather than two that could drift. What is added here is only the name of the caller it let through,
    /// because a version records who wrote it.
    /// </summary>
    private static bool TryCaller(HttpContext ctx, Func<string, SessionFactoryLookup> factoryOf, string factory,
        string route, out Caller caller, out IResult? error)
    {
        caller = default;
        if (!FactoryNaming.TryAct(ctx, factoryOf, factory, "brief", route, out error)) return false;

        var deviceType = ctx.Items.TryGetValue(AuthMiddleware.DeviceTypeItemKey, out var dt) ? dt as string : null;
        if (SessionOriginSurfaces.FromDeviceType(deviceType) != SessionOriginSurfaces.Unknown)
            // WHICH person, as far as this Gateway can tell: the device that authenticated.
            caller = new Caller(FactoryMemoryAuthorKinds.Person, FleetManagerOwnerDevice.Caller(ctx)?.Actor);
        else if (AuthMiddleware.CallingSession(ctx) is { } session)
            caller = new Caller(FactoryMemoryAuthorKinds.Session, session.SessionId.ToString());
        else
            caller = new Caller(NeitherPersonNorSession, null);
        return true;
    }

    /// <summary>A write needs an author: a person or a session. The shared machine token and a Director are neither.</summary>
    private static bool TryWriter(Caller caller, string route, out IResult? error)
    {
        error = null;
        if (caller.Kind is FactoryMemoryAuthorKinds.Person or FactoryMemoryAuthorKinds.Session) return true;
        FileLog.Write($"[FactoryBriefEndpoints] {route}: REFUSED - neither a person's device nor a session's key");
        error = Results.Json(new
        {
            error = "a seat's brief is written by a person, or by a session of its factory",
            detail = "Every version records who wrote it. This request carried neither a session's key nor a person's device key.",
        }, statusCode: StatusCodes.Status403Forbidden);
        return false;
    }

    private static IResult Answer(FactoryBriefWrite write, FactoryBriefStore store, TenantId tenant, string factory, string seat)
    {
        // Read after the write, so the answer says how the seat stands now: which version is newest and which is published.
        var standing = store.Standing(tenant, factory, seat);
        if (write.Ok) return Results.Json(ToDto(write.Brief!, standing));
        var status = write.Outcome switch
        {
            FactoryBriefOutcome.OnlyAPersonPublishes => StatusCodes.Status403Forbidden,
            FactoryBriefOutcome.FactoryNotRegistered or FactoryBriefOutcome.SeatNotRegistered
                or FactoryBriefOutcome.NoSuchVersion => StatusCodes.Status404NotFound,
            FactoryBriefOutcome.BadFactory or FactoryBriefOutcome.BadSeat
                or FactoryBriefOutcome.Empty or FactoryBriefOutcome.TooLarge => StatusCodes.Status400BadRequest,
            // Stale, too many drafts waiting, already published, older than the published one: "not as things stand".
            _ => StatusCodes.Status409Conflict,
        };
        FileLog.Write($"[FactoryBriefEndpoints] {factory}/{seat}: REFUSED ({status}, {write.Outcome}) - {write.Refusal}");
        return Results.Json(new
        {
            error = write.Refusal,
            outcome = write.Outcome.ToString(),
            // THE VERSION THE CALLER NEEDS RIDES THE REFUSAL. Without it a refused writer must read, merge and race
            // again; with it, it already holds what it needs to merge.
            current = write.Brief is null ? null : ToDto(write.Brief, standing),
        }, statusCode: status);
    }

    private static FactoryBriefDto ToDto(FactoryBriefVersionEntity v, FactoryBriefStanding standing) => new()
    {
        Factory = v.Factory,
        Seat = v.Seat,
        Version = v.Version,
        Text = v.Text,
        State = v.State,
        AuthorKind = v.AuthorKind,
        AuthorId = v.AuthorId,
        WrittenAtUtc = v.WrittenAtUtc,
        PublishedAtUtc = v.PublishedAtUtc,
        PublishedBy = v.PublishedBy,
        NewestVersion = standing.Newest?.Version ?? v.Version,
        PublishedVersion = standing.Published?.Version,
    };

    private static IResult NoBody() => Results.BadRequest(new { error = "a body is required" });

    private static IResult NoAccount() =>
        Results.Json(new { error = "no account is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);
}
