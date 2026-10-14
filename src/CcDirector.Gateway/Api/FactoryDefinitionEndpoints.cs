using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// THE GATEWAY CALLS THAT READ AND WRITE A FACTORY'S DEFINITION (Factory Definition mission, phase 1).
///
///   GET  /factory-definitions/{factory}                    the published version, with its content
///   GET  /factory-definitions/{factory}/draft              the newest version, published or not, with its content
///   GET  /factory-definitions/{factory}/history            one page of the versions, newest first, WITHOUT content
///                                                          (?limit=1..100, 50 when not said; ?before=the version to start below)
///   GET  /factory-definitions/{factory}/versions/{version} one version, with its content
///   GET  /factory-definitions/{factory}/diff               what changes (?from=N, default the published version;
///                                                          ?to=M, default the newest)
///   PUT  /factory-definitions/{factory}/draft              save a draft, stating the version last read
///   POST /factory-definitions/{factory}/publish            publish one version - A PERSON ONLY
///   POST /factory-definitions/{factory}/restore            publish a copy of an older version as the newest - A PERSON ONLY
///
/// WHO IS ASKING is read exactly as it is for a seat's brief and a factory's schedules and triggers
/// (<see cref="FactoryNaming.TryAct"/>): a person, or a session of that factory. A session of another factory, or of
/// none, is refused in that gate's own words. A caller that is neither - the account's shared machine token, or a
/// Director - may read and may not write, because every version records who wrote it.
///
/// THE WHOLE-DEFINITION CHECK RUNS BEFORE EVERY WRITE (<see cref="FactoryDefinitionCheck.Check"/>), with the account's
/// fleet skill ids and the machines its Directors report now. Any error refuses with 422 and every finding, file and
/// line, and nothing is stored. Notices never refuse; they ride along on the success. A publish and a restore check
/// again even though the version was checked when it was saved, because the account's skills and machines may have
/// changed since.
///
/// ONLY A PERSON PUBLISHES OR RESTORES. The two routes are reachable by a session key on purpose and refused by the
/// store in its own words, as a brief's are: an agent told "save a draft; the owner publishes it" knows what to do
/// next. A session's publish goes straight to the store, so it is told that rather than what the check thinks of a
/// version it may not publish anyway.
///
/// A PUBLISH AND A RESTORE ANSWER WITH WHAT CHANGES against the version that was published just before, so the person
/// sees what they just did; <c>/diff</c> answers the same between any two versions.
///
/// NOTHING ELSE READS THE DEFINITION YET: these routes are the only reader until phase 4.
/// </summary>
internal static class FactoryDefinitionEndpoints
{
    internal const string Prefix = "/factory-definitions";

    /// <summary>The noun the shared gate names in its refusal: "this definition belongs to factory ...".</summary>
    private const string What = "definition";

    public static void Map(IEndpointRouteBuilder app,
        Func<HttpContext, TenantId?> resolveTenant,
        FactoryDefinitionStore store,
        Func<string, SessionFactoryLookup> sessionFactoryOf,
        Func<TenantId, IReadOnlyCollection<string>> fleetSkillsOf,
        Func<TenantId, IReadOnlyCollection<string>> connectedMachinesOf,
        Func<DateTime> nowUtc)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sessionFactoryOf);
        ArgumentNullException.ThrowIfNull(fleetSkillsOf);
        ArgumentNullException.ThrowIfNull(connectedMachinesOf);
        ArgumentNullException.ThrowIfNull(nowUtc);

        var account = new Account(fleetSkillsOf, connectedMachinesOf);
        const string factoryRoute = Prefix + "/{factory}";
        app.MapGet(factoryRoute, (HttpContext ctx, string factory) =>
            ReadPublished(ctx, factory, store, resolveTenant, sessionFactoryOf));
        app.MapGet(factoryRoute + "/draft", (HttpContext ctx, string factory) =>
            ReadNewest(ctx, factory, store, resolveTenant, sessionFactoryOf));
        app.MapGet(factoryRoute + "/history", (HttpContext ctx, string factory) =>
            History(ctx, factory, store, resolveTenant, sessionFactoryOf));
        app.MapGet(factoryRoute + "/versions/{version}", (HttpContext ctx, string factory, string version) =>
            ReadVersion(ctx, factory, version, store, resolveTenant, sessionFactoryOf));
        app.MapGet(factoryRoute + "/diff", (HttpContext ctx, string factory) =>
            Diff(ctx, factory, store, resolveTenant, sessionFactoryOf));
        app.MapPut(factoryRoute + "/draft", (HttpContext ctx, string factory, SaveFactoryDefinitionDraftRequest? body) =>
            SaveDraft(ctx, factory, body, store, resolveTenant, sessionFactoryOf, account, nowUtc));
        app.MapPost(factoryRoute + "/publish", (HttpContext ctx, string factory, PublishFactoryDefinitionRequest? body) =>
            Publish(ctx, factory, body, store, resolveTenant, sessionFactoryOf, account, nowUtc));
        app.MapPost(factoryRoute + "/restore", (HttpContext ctx, string factory, RestoreFactoryDefinitionRequest? body) =>
            Restore(ctx, factory, body, store, resolveTenant, sessionFactoryOf, account, nowUtc));

        FileLog.Write($"[FactoryDefinitionEndpoints] mapped {factoryRoute} and its draft, history, versions, diff, publish and restore routes");
    }

    /// <summary>What the whole-definition check needs to know about the account: its fleet skill ids and the machines
    /// its Directors report now. Both are read at the moment of the write, never cached.</summary>
    internal sealed record Account(
        Func<TenantId, IReadOnlyCollection<string>> FleetSkillsOf,
        Func<TenantId, IReadOnlyCollection<string>> ConnectedMachinesOf);

    /// <summary>GET the published definition, with its content.</summary>
    internal static IResult ReadPublished(HttpContext ctx, string factory, FactoryDefinitionStore store,
        Func<HttpContext, TenantId?> resolveTenant, Func<string, SessionFactoryLookup> sessionFactoryOf)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        if (!TryCaller(ctx, sessionFactoryOf, factory, $"GET {Prefix}/{factory}", out _, out var error)) return error!;
        if (!TryFold(factory, out var folded, out error)) return error!;

        var standing = store.Standing(tenant, folded);
        if (standing.Published is null)
            return Results.Json(new
            {
                error = standing.Newest is null
                    ? $"{folded} has no definition on the Gateway"
                    : $"{folded} has no published definition yet: its newest version, {standing.Newest.Version}, is a draft",
                detail = standing.Newest is null
                    ? "Save its first draft expecting version 0."
                    : "A person publishes a draft to make it the factory's definition.",
                newestVersion = standing.Newest?.Version ?? 0,
            }, statusCode: StatusCodes.Status404NotFound);
        return Results.Json(ToDto(store.Find(tenant, folded, standing.Published.Version)!, standing));
    }

    /// <summary>GET the newest version, published or not: what an editor starts from, and the version a save states.</summary>
    internal static IResult ReadNewest(HttpContext ctx, string factory, FactoryDefinitionStore store,
        Func<HttpContext, TenantId?> resolveTenant, Func<string, SessionFactoryLookup> sessionFactoryOf)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        if (!TryCaller(ctx, sessionFactoryOf, factory, $"GET {Prefix}/{factory}/draft", out _, out var error)) return error!;
        if (!TryFold(factory, out var folded, out error)) return error!;

        var standing = store.Standing(tenant, folded);
        if (standing.Newest is null)
            return Results.Json(new
            {
                error = $"{folded} has no definition on the Gateway",
                detail = "Save its first draft expecting version 0.",
                newestVersion = 0,
            }, statusCode: StatusCodes.Status404NotFound);
        return Results.Json(ToDto(store.Find(tenant, folded, standing.Newest.Version)!, standing));
    }

    /// <summary>
    /// GET one page of the factory's history, newest first, without any version's content. A <c>limit</c> or a
    /// <c>before</c> that is not one is refused in words rather than quietly replaced: a reader that asked for 500 and
    /// got 100 would believe it had seen them all.
    /// </summary>
    internal static IResult History(HttpContext ctx, string factory, FactoryDefinitionStore store,
        Func<HttpContext, TenantId?> resolveTenant, Func<string, SessionFactoryLookup> sessionFactoryOf)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        if (!TryCaller(ctx, sessionFactoryOf, factory, $"GET {Prefix}/{factory}/history", out _, out var error)) return error!;
        if (!TryFold(factory, out var folded, out error)) return error!;

        var limit = FactoryDefinitionStore.DefaultHistoryPage;
        if (ctx.Request.Query.TryGetValue("limit", out var limitSent)
            && (!int.TryParse(limitSent.ToString(), out limit) || limit < 1 || limit > FactoryDefinitionStore.MaxHistoryPage))
            return Results.BadRequest(new
            {
                error = $"limit is how many versions one page holds: a whole number from 1 to {FactoryDefinitionStore.MaxHistoryPage}, not '{limitSent}'",
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

        var standing = store.Standing(tenant, folded);
        var page = store.History(tenant, folded, before, limit);
        return Results.Json(new FactoryDefinitionHistoryResponse
        {
            Factory = folded,
            PublishedVersion = standing.Published?.Version,
            NewestVersion = standing.Newest?.Version ?? 0,
            Total = page.Total,
            Versions = page.Versions.Select(v => new FactoryDefinitionVersionSummaryDto
            {
                Version = v.Version,
                State = v.State,
                AuthorKind = v.AuthorKind,
                AuthorId = v.AuthorId,
                ChangeNote = v.ChangeNote,
                RestoredFrom = v.RestoredFrom,
                WrittenAtUtc = v.WrittenAtUtc,
                PublishedAtUtc = v.PublishedAtUtc,
                PublishedBy = v.PublishedBy,
                ContentBytes = v.ContentBytes,
                FileCount = v.FileCount,
            }).ToList(),
            NextBefore = page.NextBefore,
        });
    }

    /// <summary>GET one version, with its content: how the history's content is read, one version at a time.</summary>
    internal static IResult ReadVersion(HttpContext ctx, string factory, string version, FactoryDefinitionStore store,
        Func<HttpContext, TenantId?> resolveTenant, Func<string, SessionFactoryLookup> sessionFactoryOf)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        if (!TryCaller(ctx, sessionFactoryOf, factory, $"GET {Prefix}/{factory}/versions/{version}", out _, out var error)) return error!;
        if (!TryFold(factory, out var folded, out error)) return error!;
        if (!TryVersion(version, "version", out var wanted, out error)) return error!;

        var standing = store.Standing(tenant, folded);
        if (store.Find(tenant, folded, wanted) is not { } found) return NoSuchVersion(folded, wanted, standing);
        return Results.Json(ToDto(found, standing));
    }

    /// <summary>
    /// GET what changes from one version to another (<see cref="FactoryDefinitionChanges.Compare"/>): <c>from</c>
    /// defaults to the published version - nothing, before the first publish, so everything is listed as added - and
    /// <c>to</c> to the newest. Every sentence is composed here; the command line and the Cockpit print it as it is.
    /// </summary>
    internal static IResult Diff(HttpContext ctx, string factory, FactoryDefinitionStore store,
        Func<HttpContext, TenantId?> resolveTenant, Func<string, SessionFactoryLookup> sessionFactoryOf)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        if (!TryCaller(ctx, sessionFactoryOf, factory, $"GET {Prefix}/{factory}/diff", out _, out var error)) return error!;
        if (!TryFold(factory, out var folded, out error)) return error!;
        int? from = null, to = null;
        if (ctx.Request.Query.TryGetValue("from", out var fromSent))
        {
            if (!TryVersion(fromSent.ToString(), "version to compare from", out var f, out error)) return error!;
            from = f;
        }
        if (ctx.Request.Query.TryGetValue("to", out var toSent))
        {
            if (!TryVersion(toSent.ToString(), "version to compare to", out var t, out error)) return error!;
            to = t;
        }

        var standing = store.Standing(tenant, folded);
        if (standing.Newest is null) return NoSuchVersion(folded, to ?? 1, standing);
        var compared = store.Find(tenant, folded, to ?? standing.Newest.Version);
        if (compared is null) return NoSuchVersion(folded, to!.Value, standing);
        var fromVersion = from ?? standing.Published?.Version;
        FactoryDefinitionVersion? published = null;
        if (fromVersion is { } fv && (published = store.Find(tenant, folded, fv)) is null)
            return NoSuchVersion(folded, fv, standing);

        var changes = FactoryDefinitionChanges.Compare(published?.Files, compared.Files);
        FileLog.Write($"[FactoryDefinitionEndpoints] {folded}: diff {fromVersion?.ToString() ?? "nothing"} -> {compared.Summary.Version}: {changes.Summary}");
        return Results.Json(new FactoryDefinitionDiffResponse
        {
            Factory = folded,
            From = fromVersion,
            To = compared.Summary.Version,
            Changes = changes,
        });
    }

    /// <summary>PUT a draft: a person, or a session of the factory, saves the next version - once the check finds no error.</summary>
    internal static IResult SaveDraft(HttpContext ctx, string factory, SaveFactoryDefinitionDraftRequest? body,
        FactoryDefinitionStore store, Func<HttpContext, TenantId?> resolveTenant,
        Func<string, SessionFactoryLookup> sessionFactoryOf, Account account, Func<DateTime> nowUtc)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        var route = $"PUT {Prefix}/{factory}/draft";
        if (!TryCaller(ctx, sessionFactoryOf, factory, route, out var caller, out var error)) return error!;
        if (!TryWriter(caller, route, out error)) return error!;
        if (body is null) return NoBody();
        if (!TryFold(factory, out var folded, out error)) return error!;

        var content = body.Content ?? new Dictionary<string, string>();
        var check = Check(content, folded, tenant, account);
        if (check.HasErrors)
            return CheckRefused(folded, check, "The definition was not saved");

        var write = store.SaveDraft(tenant, folded, content, body.ExpectedVersion, caller.Kind, caller.Id, body.Note, nowUtc());
        return Answer(write, store, tenant, folded, check);
    }

    /// <summary>POST a publish: a person makes one version the factory's definition - once the check finds no error in it.</summary>
    internal static IResult Publish(HttpContext ctx, string factory, PublishFactoryDefinitionRequest? body,
        FactoryDefinitionStore store, Func<HttpContext, TenantId?> resolveTenant,
        Func<string, SessionFactoryLookup> sessionFactoryOf, Account account, Func<DateTime> nowUtc)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        var route = $"POST {Prefix}/{factory}/publish";
        if (!TryCaller(ctx, sessionFactoryOf, factory, route, out var caller, out var error)) return error!;
        if (!TryWriter(caller, route, out error)) return error!;
        if (body is null) return NoBody();
        if (!TryFold(factory, out var folded, out error)) return error!;

        // A SESSION'S PUBLISH REACHES THE STORE, which refuses it with the sentence that says what to do instead.
        if (caller.Kind != FactoryMemoryAuthorKinds.Person)
            return Answer(store.Publish(tenant, folded, body.Version, caller.Kind, caller.Id, nowUtc()), store, tenant, folded, null);

        // A version that does not exist is the store's to word; only one that does is checked.
        var wanted = store.Find(tenant, folded, body.Version);
        var check = wanted is null ? null : Check(wanted.Files, folded, tenant, account);
        if (check is { HasErrors: true })
            return CheckRefused(folded, check, $"Version {body.Version} was not published");

        var before = PublishedContent(store, tenant, folded);
        var write = store.Publish(tenant, folded, body.Version, caller.Kind, caller.Id, nowUtc());
        return Answer(write, store, tenant, folded, check, write.Ok ? FactoryDefinitionChanges.Compare(before, wanted!.Files) : null);
    }

    /// <summary>The content of the version published now, or null when none is: what a publish's "what changes" is
    /// measured against, read just before the publish.</summary>
    private static IReadOnlyDictionary<string, string>? PublishedContent(FactoryDefinitionStore store, TenantId tenant, string factory)
        => store.Standing(tenant, factory).Published is { } published ? store.Find(tenant, factory, published.Version)?.Files : null;

    /// <summary>POST a restore: a person publishes a copy of an older version as the newest version - once the check
    /// finds no error in it.</summary>
    internal static IResult Restore(HttpContext ctx, string factory, RestoreFactoryDefinitionRequest? body,
        FactoryDefinitionStore store, Func<HttpContext, TenantId?> resolveTenant,
        Func<string, SessionFactoryLookup> sessionFactoryOf, Account account, Func<DateTime> nowUtc)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        var route = $"POST {Prefix}/{factory}/restore";
        if (!TryCaller(ctx, sessionFactoryOf, factory, route, out var caller, out var error)) return error!;
        if (!TryWriter(caller, route, out error)) return error!;
        if (body is null) return NoBody();
        if (!TryFold(factory, out var folded, out error)) return error!;

        if (caller.Kind != FactoryMemoryAuthorKinds.Person)
            return Answer(store.Restore(tenant, folded, body.Version, caller.Kind, caller.Id, null, nowUtc(), body.ExpectedVersion),
                store, tenant, folded, null);

        var wanted = store.Find(tenant, folded, body.Version);
        var check = wanted is null ? null : Check(wanted.Files, folded, tenant, account);
        if (check is { HasErrors: true })
            return CheckRefused(folded, check, $"Version {body.Version} was not restored");

        var before = PublishedContent(store, tenant, folded);
        // The copy records the version it came from (restoredFrom), so it carries no change note of its own.
        var write = store.Restore(tenant, folded, body.Version, caller.Kind, caller.Id, null, nowUtc(), body.ExpectedVersion);
        return Answer(write, store, tenant, folded, check, write.Ok ? FactoryDefinitionChanges.Compare(before, wanted!.Files) : null);
    }

    // ---------- the check ----------

    private static FactoryDefinitionCheckResult Check(IReadOnlyDictionary<string, string> content, string factory,
        TenantId tenant, Account account)
        => FactoryDefinitionCheck.Check(content, factory, account.FleetSkillsOf(tenant), account.ConnectedMachinesOf(tenant));

    /// <summary>
    /// THE CHECK'S REFUSAL: 422, one sentence, and every finding with its file and line, so a writer can fix each one
    /// where it is. Nothing was stored.
    /// </summary>
    private static IResult CheckRefused(string factory, FactoryDefinitionCheckResult check, string what)
    {
        var errors = check.Findings.Count(f => f.IsError);
        var sentence = $"{what}: the check found {errors} {(errors == 1 ? "error" : "errors")} in the definition of {factory}. " +
                       "Fix each one where it is and try again.";
        FileLog.Write($"[FactoryDefinitionEndpoints] {factory}: REFUSED by the check (422) - {errors} errors");
        return Results.Json(new
        {
            error = sentence,
            outcome = "CheckFailed",
            findings = check.Findings.Select(ToDto).ToList(),
        }, statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    // ---------- who is asking ----------

    /// <summary>The caller kind of a credential that is neither a person's device nor a session's key. Deliberately
    /// NOT one of <see cref="FactoryMemoryAuthorKinds"/>: such a caller may read and can never be the author of a version.</summary>
    private const string NeitherPersonNorSession = "machine";

    /// <summary>Who is asking: a person, a session, or neither, with the identifier a version records.</summary>
    internal readonly record struct Caller(string Kind, string? Id);

    /// <summary>
    /// MAY THIS CALLER TOUCH THIS FACTORY'S DEFINITION, AND WHO IS IT. The gate is <see cref="FactoryNaming.TryAct"/>,
    /// the one a seat's brief and a factory's schedules and triggers already pass, so there is one reading of who is
    /// asking rather than two that could drift.
    /// </summary>
    private static bool TryCaller(HttpContext ctx, Func<string, SessionFactoryLookup> factoryOf, string factory,
        string route, out Caller caller, out IResult? error)
    {
        caller = default;
        if (!FactoryNaming.TryAct(ctx, factoryOf, factory, What, route, out error)) return false;

        var deviceType = ctx.Items.TryGetValue(AuthMiddleware.DeviceTypeItemKey, out var dt) ? dt as string : null;
        if (SessionOriginSurfaces.FromDeviceType(deviceType) != SessionOriginSurfaces.Unknown)
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
        FileLog.Write($"[FactoryDefinitionEndpoints] {route}: REFUSED - neither a person's device nor a session's key");
        error = Results.Json(new
        {
            error = "a factory's definition is written by a person, or by a session of that factory",
            detail = "Every version records who wrote it. This request carried neither a session's key nor a person's device key.",
        }, statusCode: StatusCodes.Status403Forbidden);
        return false;
    }

    /// <summary>The factory in the path, folded to its one spelling - the spelling the check compares factory.yaml with.</summary>
    private static bool TryFold(string factory, out string folded, out IResult? error)
    {
        error = null;
        if (FactoryNames.TryFactory(factory, out folded, out var refusal)) return true;
        error = Results.BadRequest(new { error = refusal, outcome = FactoryDefinitionOutcome.BadFactory.ToString() });
        return false;
    }

    private static bool TryVersion(string sent, string name, out int version, out IResult? error)
    {
        error = null;
        if (int.TryParse(sent, out version) && version >= 1) return true;
        error = Results.BadRequest(new { error = $"a {name} is a whole number from 1, not '{sent}'" });
        return false;
    }

    // ---------- answers ----------

    private static IResult Answer(FactoryDefinitionWrite write, FactoryDefinitionStore store, TenantId tenant, string factory,
        FactoryDefinitionCheckResult? check, FactoryDefinitionChangesDto? changes = null)
    {
        // Read after the write, so the answer says how the factory stands now: which version is newest and which is published.
        var standing = store.Standing(tenant, factory);
        if (write.Ok)
        {
            var dto = ToDto(write.Version!, standing);
            if (check is not null) dto.Findings = check.Findings.Select(ToDto).ToList();
            dto.Changes = changes;
            return Results.Json(dto);
        }
        var status = write.Outcome switch
        {
            FactoryDefinitionOutcome.OnlyAPersonPublishes => StatusCodes.Status403Forbidden,
            FactoryDefinitionOutcome.NoSuchVersion => StatusCodes.Status404NotFound,
            FactoryDefinitionOutcome.BadFactory or FactoryDefinitionOutcome.Empty or FactoryDefinitionOutcome.TooManyFiles
                or FactoryDefinitionOutcome.BadPath or FactoryDefinitionOutcome.NotText or FactoryDefinitionOutcome.FileTooLarge
                or FactoryDefinitionOutcome.VersionTooLarge or FactoryDefinitionOutcome.ChangeNoteTooLong
                => StatusCodes.Status400BadRequest,
            FactoryDefinitionOutcome.WriteFailed => StatusCodes.Status500InternalServerError,
            // Stale, too many drafts waiting, already published, older than the published one: "not as things stand".
            _ => StatusCodes.Status409Conflict,
        };
        FileLog.Write($"[FactoryDefinitionEndpoints] {factory}: REFUSED ({status}, {write.Outcome}) - {write.Refusal}");
        return Results.Json(new
        {
            error = write.Refusal,
            outcome = write.Outcome.ToString(),
            // THE VERSION THE CALLER NEEDS RIDES THE REFUSAL, without its content: a stale writer reads it with
            // GET .../draft, which is one read it would make anyway.
            current = write.Version is null ? null : ToDto(write.Version, standing),
        }, statusCode: status);
    }

    private static IResult NoSuchVersion(string factory, int version, FactoryDefinitionStanding standing) =>
        Results.Json(new
        {
            error = standing.Newest is null
                ? $"{factory} has no definition on the Gateway"
                : $"version {version} of the definition of {factory} does not exist; its newest version is {standing.Newest.Version}",
            newestVersion = standing.Newest?.Version ?? 0,
        }, statusCode: StatusCodes.Status404NotFound);

    private static FactoryDefinitionDto ToDto(FactoryDefinitionVersion found, FactoryDefinitionStanding standing)
    {
        var dto = ToDto(found.Summary, standing);
        dto.Files = new Dictionary<string, string>(found.Files, StringComparer.Ordinal);
        return dto;
    }

    private static FactoryDefinitionDto ToDto(FactoryDefinitionVersionSummary v, FactoryDefinitionStanding standing) => new()
    {
        Factory = v.Factory,
        Version = v.Version,
        State = v.State,
        AuthorKind = v.AuthorKind,
        AuthorId = v.AuthorId,
        ChangeNote = v.ChangeNote,
        RestoredFrom = v.RestoredFrom,
        WrittenAtUtc = v.WrittenAtUtc,
        PublishedAtUtc = v.PublishedAtUtc,
        PublishedBy = v.PublishedBy,
        ContentBytes = v.ContentBytes,
        FileCount = v.FileCount,
        NewestVersion = standing.Newest?.Version ?? v.Version,
        PublishedVersion = standing.Published?.Version,
    };

    private static FactoryDefinitionFindingDto ToDto(DefinitionFinding f) => new()
    {
        Severity = f.Severity,
        File = f.File,
        Line = f.Line,
        Message = f.Message,
    };

    private static IResult NoBody() => Results.BadRequest(new { error = "a body is required" });

    private static IResult NoAccount() =>
        Results.Json(new { error = "no account is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);
}
