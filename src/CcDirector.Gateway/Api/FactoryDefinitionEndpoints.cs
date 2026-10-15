using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Triggers;
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
///   GET  /factory-definitions/{factory}/export             the definition as a folder of files (phase 3, ?version=N);
///                                                          for a factory with NO definition, written from what is live
///   POST /factory-definitions/import                       a folder's files, for the factory its factory.yaml names,
///                                                          checked as a save is, stored as the next draft - NEVER
///                                                          published (phase 3)
///   GET  /factory-definition-schemas                       the JSON Schemas the check enforces (phase 3)
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
/// A PUBLISH AND A RESTORE DRIVE THE FACTORY (phase 4): a person's publish or restore goes through
/// <see cref="FactoryPublishApply"/>, which writes the publish mark, the registry row, the schedules and the triggers in
/// one transaction. A caller may state the published version it saw (<c>replaces</c>) and is refused when another
/// publish got there first. The answer carries what changes against the version it really replaced, and
/// <c>applied</c>: one sentence per step the transaction took. <c>/diff</c> answers what changes between any two
/// versions, and <c>plan</c>: what a publish of the version compared to WOULD do to the live factory now.
///
/// THE STATE A FACTORY'S CHECKS KEEP (phase 5b, design 1.4): <c>/check-state</c> lists every checked start of the
/// published definition, by seat and start number, with the newest state its check left; <c>/check-state/reset</c>
/// appends an empty state for one start - the way out of a stuck state. ONLY A PERSON RESETS, for the reason only a
/// person publishes; a session's reset reaches the route and is refused in words.
/// </summary>
internal static class FactoryDefinitionEndpoints
{
    internal const string Prefix = "/factory-definitions";

    /// <summary>Where a folder is imported. Not under <c>{factory}</c>: the factory is the one factory.yaml names.</summary>
    internal const string ImportRoute = Prefix + "/import";

    /// <summary>Where the schemas are served. Not under <see cref="Prefix"/>, where any one-word path is a factory.</summary>
    internal const string SchemasRoute = "/factory-definition-schemas";

    /// <summary>The noun the shared gate names in its refusal: "this definition belongs to factory ...".</summary>
    private const string What = "definition";

    public static void Map(IEndpointRouteBuilder app,
        Func<HttpContext, TenantId?> resolveTenant,
        FactoryDefinitionStore store,
        Func<string, SessionFactoryLookup> sessionFactoryOf,
        Func<TenantId, IReadOnlyCollection<string>> fleetSkillsOf,
        Func<TenantId, IReadOnlyCollection<string>> connectedMachinesOf,
        Func<TenantId, string, FactoryLiveParts?> liveOf,
        FactoryPublishApply apply,
        Func<DateTime> nowUtc,
        AskMachinesAsync askMachines,
        // Phase 5b: the state each check keeps, and the account's triggers the checked starts are run by. REQUIRED:
        // a wiring line that forgot it must not compile.
        CheckStateParts checkStates)
    {
        ArgumentNullException.ThrowIfNull(checkStates);
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(sessionFactoryOf);
        ArgumentNullException.ThrowIfNull(fleetSkillsOf);
        ArgumentNullException.ThrowIfNull(connectedMachinesOf);
        ArgumentNullException.ThrowIfNull(liveOf);
        ArgumentNullException.ThrowIfNull(nowUtc);
        ArgumentNullException.ThrowIfNull(askMachines);

        var account = new Account(fleetSkillsOf, connectedMachinesOf, askMachines);
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
            Diff(ctx, factory, store, apply, resolveTenant, sessionFactoryOf));
        app.MapPut(factoryRoute + "/draft", (HttpContext ctx, string factory, SaveFactoryDefinitionDraftRequest? body) =>
            SaveDraft(ctx, factory, body, store, resolveTenant, sessionFactoryOf, account, nowUtc));
        app.MapPost(factoryRoute + "/publish", (HttpContext ctx, string factory, PublishFactoryDefinitionRequest? body) =>
            Publish(ctx, factory, body, store, apply, resolveTenant, sessionFactoryOf, account, nowUtc));
        app.MapPost(factoryRoute + "/restore", (HttpContext ctx, string factory, RestoreFactoryDefinitionRequest? body) =>
            Restore(ctx, factory, body, store, apply, resolveTenant, sessionFactoryOf, account, nowUtc));
        app.MapGet(factoryRoute + "/export", (HttpContext ctx, string factory) =>
            Export(ctx, factory, store, resolveTenant, sessionFactoryOf, liveOf));
        app.MapPost(ImportRoute, (HttpContext ctx, ImportFactoryDefinitionRequest? body) =>
            Import(ctx, body, store, resolveTenant, sessionFactoryOf, account, nowUtc));
        app.MapGet(SchemasRoute, (HttpContext ctx) => Schemas(ctx, resolveTenant));
        app.MapGet(factoryRoute + "/check-state", (HttpContext ctx, string factory) =>
            ListCheckState(ctx, factory, store, checkStates, resolveTenant, sessionFactoryOf));
        app.MapPost(factoryRoute + "/check-state/reset", (HttpContext ctx, string factory, ResetFactoryCheckStateRequest? body) =>
            ResetCheckState(ctx, factory, body, store, checkStates, resolveTenant, sessionFactoryOf, nowUtc));

        FileLog.Write($"[FactoryDefinitionEndpoints] mapped {factoryRoute} and its draft, history, versions, diff, publish, restore, export and check-state routes, {ImportRoute} and {SchemasRoute}");
    }

    /// <summary>What the check-state routes read and write: the state store, and the account's triggers.</summary>
    internal sealed record CheckStateParts(FactoryCheckStateStore Store, Func<TenantId, IReadOnlyList<TriggerEntity>> TriggersOf);

    /// <summary>The refusal a session's reset gets: only a person resets a check's state, and what the session can do.</summary>
    public const string OnlyAPersonResets =
        "Only a person resets a check's state. A session of the factory may read it with " +
        "'cc-devthrottle factory check-state show'; ask the owner to reset it.";

    /// <summary>The outcome word of that refusal: its own, so a client never reads a reset refusal as a publish one.</summary>
    public const string OnlyAPersonResetsOutcome = "OnlyAPersonResets";

    /// <summary>GET every checked start of the published definition, with the newest state its check left.</summary>
    internal static IResult ListCheckState(HttpContext ctx, string factory, FactoryDefinitionStore store,
        CheckStateParts checkStates, Func<HttpContext, TenantId?> resolveTenant, Func<string, SessionFactoryLookup> sessionFactoryOf)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        if (!TryCaller(ctx, sessionFactoryOf, factory, $"GET {Prefix}/{factory}/check-state", out _, out var error)) return error!;
        if (!TryFold(factory, out var folded, out error)) return error!;

        var states = CheckStatesOf(tenant, folded, store, checkStates);
        FileLog.Write($"[FactoryDefinitionEndpoints] GET {folded}/check-state: {states.Count} checked start(s)");
        return Results.Json(new FactoryCheckStateListResponse { Factory = folded, Count = states.Count, States = states });
    }

    /// <summary>
    /// POST a person's reset of one start's state: an empty state row marked as a reset, so the check's next run is a
    /// first run. The start must be a checked start of this factory's published definition; anything else is 404 in
    /// words, never a row written for a start that does not exist.
    /// </summary>
    internal static IResult ResetCheckState(HttpContext ctx, string factory, ResetFactoryCheckStateRequest? body,
        FactoryDefinitionStore store, CheckStateParts checkStates, Func<HttpContext, TenantId?> resolveTenant,
        Func<string, SessionFactoryLookup> sessionFactoryOf, Func<DateTime> nowUtc)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        var route = $"POST {Prefix}/{factory}/check-state/reset";
        if (!TryCaller(ctx, sessionFactoryOf, factory, route, out var caller, out var error)) return error!;
        if (caller.Kind != FactoryMemoryAuthorKinds.Person)
        {
            FileLog.Write($"[FactoryDefinitionEndpoints] {route}: REFUSED - a {caller.Kind} may not reset a check's state");
            return Results.Json(new { error = OnlyAPersonResets, outcome = OnlyAPersonResetsOutcome },
                statusCode: StatusCodes.Status403Forbidden);
        }
        if (body is null || string.IsNullOrWhiteSpace(body.OwnerKey)) return Results.BadRequest(new { error = "ownerKey is required" });
        if (!TryFold(factory, out var folded, out error)) return error!;

        var ownerKey = body.OwnerKey.Trim();
        if (!FactoryCheckStateStore.IsOwnerKey(ownerKey))
            return Results.BadRequest(new
            {
                error = $"'{ownerKey}' is not an owner key: it is trigger:<trigger id> or cron:<schedule id>",
                detail = $"'cc-devthrottle factory check-state show {folded}' lists the checked starts and their owner keys.",
            });
        var start = CheckStatesOf(tenant, folded, store, checkStates)
            .FirstOrDefault(s => string.Equals(s.OwnerKey, ownerKey, StringComparison.Ordinal));
        if (start is null)
        {
            FileLog.Write($"[FactoryDefinitionEndpoints] {route}: REFUSED (404) - {ownerKey} is not a checked start of {folded}");
            return Results.Json(new
            {
                error = $"{ownerKey} is not a checked start of {folded}'s published definition, so it has no state to reset",
                detail = $"'cc-devthrottle factory check-state show {folded}' lists the checked starts and their owner keys.",
            }, statusCode: StatusCodes.Status404NotFound);
        }

        var row = checkStates.Store.Reset(tenant, folded, ownerKey, caller.Id, nowUtc());
        FileLog.Write($"[FactoryDefinitionEndpoints] {route}: {folded} {ownerKey} reset by {row.WrittenBy}, sequence={row.Sequence}");
        start.Content = "";
        start.WrittenUtc = DateTime.SpecifyKind(row.WrittenUtc, DateTimeKind.Utc);
        start.WrittenBy = row.WrittenBy;
        start.RunRef = null;
        start.Reset = true;
        return Results.Json(start);
    }

    private static List<FactoryCheckStateDto> CheckStatesOf(TenantId tenant, string folded, FactoryDefinitionStore store,
        CheckStateParts checkStates)
    {
        var published = store.Standing(tenant, folded).Published;
        var definition = published is null ? null
            : FactoryDefinitionCheck.Read(store.Find(tenant, folded, published.Version)!.Files).Definition;
        return FactoryCheckStateFold.List(definition, checkStates.TriggersOf(tenant),
            ownerKey => checkStates.Store.Newest(tenant, folded, ownerKey));
    }

    /// <summary>Asks each machine its question and answers one reply per machine (Factory Definition, phase 5).</summary>
    internal delegate Task<IReadOnlyDictionary<string, MachineReply>> AskMachinesAsync(TenantId tenant,
        IReadOnlyList<MachineQuestion> questions, CancellationToken ct);

    /// <summary>What the whole-definition check needs to know about the account: its fleet skill ids, the machines its
    /// Directors report now, and - for a publish or a restore - what each seat's machine answers when it is asked
    /// (<see cref="FactoryMachineQuestionAsker"/>). All are read at the moment of the write, never cached.</summary>
    internal sealed record Account(
        Func<TenantId, IReadOnlyCollection<string>> FleetSkillsOf,
        Func<TenantId, IReadOnlyCollection<string>> ConnectedMachinesOf,
        AskMachinesAsync AskMachines);

    /// <summary>GET the published definition, with its content.</summary>
    internal static IResult ReadPublished(HttpContext ctx, string factory, FactoryDefinitionStore store,
        Func<HttpContext, TenantId?> resolveTenant, Func<string, SessionFactoryLookup> sessionFactoryOf)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        if (!TryCaller(ctx, sessionFactoryOf, factory, $"GET {Prefix}/{factory}", out _, out var error)) return error!;
        if (!TryFold(factory, out var folded, out error)) return error!;

        var standing = store.Standing(tenant, folded);
        FileLog.Write($"[FactoryDefinitionEndpoints] GET {folded}: published={standing.Published?.Version.ToString() ?? "none"}, newest={standing.Newest?.Version.ToString() ?? "none"}");
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
        FileLog.Write($"[FactoryDefinitionEndpoints] GET {folded}/draft: newest={standing.Newest?.Version.ToString() ?? "none"}");
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
        FileLog.Write($"[FactoryDefinitionEndpoints] GET {folded}/history: limit={limit}, before={before?.ToString() ?? "none"}, versions={page.Versions.Count} of {page.Total}");
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
        var found = store.Find(tenant, folded, wanted);
        FileLog.Write($"[FactoryDefinitionEndpoints] GET {folded}/versions/{wanted}: {(found is null ? "not found" : $"{found.Files.Count} files")}");
        if (found is null) return NoSuchVersion(folded, wanted, standing);
        return Results.Json(ToDto(found, standing));
    }

    /// <summary>
    /// GET what changes from one version to another (<see cref="FactoryDefinitionChanges.Compare"/>): <c>from</c>
    /// defaults to the published version - nothing, before the first publish, so everything is listed as added - and
    /// <c>to</c> to the newest. Every sentence is composed here; the command line and the Cockpit print it as it is.
    /// </summary>
    internal static IResult Diff(HttpContext ctx, string factory, FactoryDefinitionStore store, FactoryPublishApply apply,
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

        // A stored version that no longer passes the format check cannot be compared: say which and where (finding 1).
        if (FirstFormatError(compared.Files) is { } badTo) return DoesNotParse(folded, compared.Summary.Version, badTo);
        if (published is not null && FirstFormatError(published.Files) is { } badFrom)
            return DoesNotParse(folded, published.Summary.Version, badFrom);

        var changes = FactoryDefinitionChanges.Compare(published?.Files, compared.Files);
        var plan = PlanOf(apply, tenant, compared, standing.Published?.Version);
        FileLog.Write($"[FactoryDefinitionEndpoints] {folded}: diff {fromVersion?.ToString() ?? "nothing"} -> {compared.Summary.Version}: {changes.Summary}; plan: {plan.Steps.Count} steps{(plan.Refused is null ? "" : ", refused")}");
        return Results.Json(new FactoryDefinitionDiffResponse
        {
            Factory = folded,
            From = fromVersion,
            To = compared.Summary.Version,
            Changes = changes,
            PublishedVersion = standing.Published?.Version,
            Plan = plan,
        });
    }

    /// <summary>
    /// What a publish of <paramref name="compared"/> WOULD do to the live factory now (design section 6), composed by
    /// the same plan a publish applies. The version already parses (the diff checked it above). A version older than
    /// the published one can only be restored, so its plan is worded as a restore (review of 4006, observation 3): a
    /// restore's copy has the same content, so the steps are the same.
    /// </summary>
    internal static FactoryDefinitionPlanDto PlanOf(FactoryPublishApply apply, TenantId tenant, FactoryDefinitionVersion compared,
        int? publishedVersion)
    {
        var version = compared.Summary.Version;
        var definition = FactoryDefinitionCheck.Read(compared.Files).Definition!;
        var composed = apply.Plan(tenant, definition);
        var restore = publishedVersion is { } published && published > version;
        return new FactoryDefinitionPlanDto
        {
            Version = version,
            Act = restore ? "restore" : "publish",
            Heading = restore
                ? $"Version {version} is older than the published version {publishedVersion}, so it can only be restored. " +
                  $"What restoring version {version} would do to the live factory now:"
                : $"What publishing version {version} would do to the live factory now:",
            Steps = composed.Plan?.Sentences().ToList() ?? new List<string>(),
            Refused = composed.Refusal?.Sentence,
            Findings = composed.Refusal?.Findings.Select(ToDto).ToList() ?? new List<FactoryDefinitionFindingDto>(),
        };
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
        if (NoText(content) is { } noText) return noText;
        return SaveChecked(store, tenant, folded, content, body.ExpectedVersion, caller, body.Note, account, nowUtc,
            "The definition was not saved", created: null);
    }

    /// <summary>
    /// THE ONE CHECKED SAVE, shared by a draft save and an import (phase 3: an import checks a folder EXACTLY as a save
    /// does): the whole-definition check with the account's skills and machines, a 422 with every finding when it finds
    /// an error, and otherwise the store's draft. <paramref name="created"/> is set on an import's answer only.
    /// </summary>
    private static IResult SaveChecked(FactoryDefinitionStore store, TenantId tenant, string folded,
        IReadOnlyDictionary<string, string> content, int expectedVersion, Caller caller, string? note, Account account,
        Func<DateTime> nowUtc, string notStored, bool? created, Func<FactoryDefinitionDto, IResult>? saved = null)
    {
        var check = Check(content, folded, tenant, account);
        if (check.HasErrors)
            return CheckRefused(folded, check, notStored);

        var write = store.SaveDraft(tenant, folded, content, expectedVersion, caller.Kind, caller.Id, note, nowUtc());
        return Answer(write, store, tenant, folded, check, created: write.Ok ? created : null, saved: saved);
    }

    /// <summary>
    /// A path that maps to no text at all - <c>{"factory.yaml": null}</c> - refused with 400 and the path, in the store's
    /// own words, BEFORE the check reads the content (review of 4004: the check threw on it and the request ended as a
    /// 500). Null when every path maps to a text. The first such path in ordinal order is named.
    /// </summary>
    internal static IResult? NoText(IReadOnlyDictionary<string, string> content)
    {
        foreach (var path in content.Keys.OrderBy(p => p, StringComparer.Ordinal))
        {
            if (content[path] is not null) continue;
            FileLog.Write($"[FactoryDefinitionEndpoints] REFUSED (400, NotText) - '{path}' maps to no text");
            return Results.BadRequest(new
            {
                error = FactoryDefinitionStore.NoTextRefusal(path),
                outcome = FactoryDefinitionOutcome.NotText.ToString(),
            });
        }
        return null;
    }

    // ---------- export, import and the schemas (phase 3) ----------

    /// <summary>
    /// GET the definition as a folder of files. With <c>?version=N</c> that version; without, the published version,
    /// or the newest when none is published yet. A stored version is exported EXACTLY as stored, so two exports of one
    /// version are the same bytes. A factory with NO definition is written from what the Gateway holds today (mission
    /// decision A8, <see cref="FactoryDefinitionExport.FromLive"/>), and everything that could not be written is listed.
    /// </summary>
    internal static IResult Export(HttpContext ctx, string factory, FactoryDefinitionStore store,
        Func<HttpContext, TenantId?> resolveTenant, Func<string, SessionFactoryLookup> sessionFactoryOf,
        Func<TenantId, string, FactoryLiveParts?> liveOf)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        if (!TryCaller(ctx, sessionFactoryOf, factory, $"GET {Prefix}/{factory}/export", out _, out var error)) return error!;
        if (!TryFold(factory, out var folded, out error)) return error!;
        int? asked = null;
        if (ctx.Request.Query.TryGetValue("version", out var versionSent))
        {
            if (!TryVersion(versionSent.ToString(), "version", out var v, out error)) return error!;
            asked = v;
        }

        var standing = store.Standing(tenant, folded);
        if (asked is not null || standing.Newest is not null)
        {
            var chosen = asked ?? standing.Published?.Version ?? standing.Newest!.Version;
            var found = store.Find(tenant, folded, chosen);
            if (found is null) return NoSuchVersion(folded, chosen, standing);
            var summary = $"Version {chosen} of the definition of {folded}, {found.Summary.State}, exactly as stored ({found.Files.Count} files).";
            if (asked is null && standing.Published is null)
                summary += " No version is published, so the newest is exported.";
            else if (asked is null && standing.Newest!.Version > chosen)
                summary += $" A newer draft, version {standing.Newest.Version}, is not exported; name its version to export it.";
            FileLog.Write($"[FactoryDefinitionEndpoints] {folded}: export version {chosen} ({found.Summary.State}), files={found.Files.Count}");
            return Results.Json(new FactoryDefinitionExportDto
            {
                Factory = folded,
                Source = "definition",
                Version = chosen,
                State = found.Summary.State,
                Summary = summary,
                Files = new Dictionary<string, string>(found.Files, StringComparer.Ordinal),
            });
        }

        if (liveOf(tenant, folded) is not { } live)
        {
            FileLog.Write($"[FactoryDefinitionEndpoints] {folded}: export REFUSED (404) - no definition and not registered");
            return Results.Json(new
            {
                error = $"{folded} has no definition on the Gateway and is not a registered factory, so there is nothing to export",
                newestVersion = 0,
            }, statusCode: StatusCodes.Status404NotFound);
        }
        var result = FactoryDefinitionExport.FromLive(live);
        var dto = new FactoryDefinitionExportDto
        {
            Factory = folded,
            Source = "live",
            Summary = $"{folded} has no definition on the Gateway, so the folder is written from what it has today: its registry entry, " +
                      $"published briefs, schedules and triggers ({result.Files.Count} files). " +
                      (result.Reported.Count == 0
                          ? "Everything it has is in the folder."
                          : $"{result.Reported.Count} {(result.Reported.Count == 1 ? "thing it has is" : "things it has are")} not in the folder; each is listed with the file it would have gone in."),
            Files = new Dictionary<string, string>(result.Files, StringComparer.Ordinal),
            Reported = result.Reported.Select(x => new FactoryDefinitionExportReportDto { File = x.File, Message = x.Message }).ToList(),
        };
        FileLog.Write($"[FactoryDefinitionEndpoints] {folded}: export from live, files={dto.Files.Count}, reported={dto.Reported.Count}");
        return Results.Json(dto);
    }

    /// <summary>
    /// POST a folder's files as the next DRAFT: checked exactly as a draft save is (<see cref="SaveChecked"/>), by the
    /// same callers, and stored after the newest version - creating the factory's definition when it has none. It never
    /// publishes. The factory is the one factory.yaml names, and nothing else: an import never rewrites a file, so a
    /// copy under a new id is a folder whose factory.yaml names that id (review of 4007, finding 2).
    /// </summary>
    /// <param name="saved">Composes the answer to an import that stored a draft (the Cockpit's Definition tab, phase 6);
    /// every refusal is answered here, the same for both callers. Null answers as this route does.</param>
    internal static IResult Import(HttpContext ctx, ImportFactoryDefinitionRequest? body, FactoryDefinitionStore store,
        Func<HttpContext, TenantId?> resolveTenant, Func<string, SessionFactoryLookup> sessionFactoryOf,
        Account account, Func<DateTime> nowUtc, Func<FactoryDefinitionDto, IResult>? saved = null)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        // A caller that can never write - the shared machine token, a Director - is refused BEFORE its content is read
        // (review of 4007, finding 3): that needs no factory. A session is matched to the factory once factory.yaml
        // has named it, below.
        if (!TryWriter(new Caller(KindOf(ctx), null), $"POST {ImportRoute}", out var notAWriter)) return notAWriter!;
        if (body is null) return NoBody();
        var content = body.Content ?? new Dictionary<string, string>();
        if (NoText(content) is { } noText) return noText;

        var factory = FactoryNamedIn(content);
        if (factory is null)
        {
            // Nothing says which factory: the format check's own findings say why factory.yaml does not.
            var findings = FactoryDefinitionCheck.Read(content).Findings.Where(f => f.IsError).Select(ToDto).ToList();
            if (findings.Count == 0)
                findings.Add(ToDto(new DefinitionFinding(DefinitionSeverity.Error, FactoryDefinitionCheck.FactoryFile, 0,
                    "factory.yaml names no factory, so the import cannot tell which factory the folder is for.")));
            FileLog.Write($"[FactoryDefinitionEndpoints] import REFUSED (422) - no factory named; findings={findings.Count}");
            return Results.Json(new
            {
                error = "The folder was not imported: its factory.yaml does not say which factory it is for. Fix factory.yaml.",
                outcome = "CheckFailed",
                findings,
            }, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var route = $"POST {ImportRoute} ({factory})";
        if (!TryCaller(ctx, sessionFactoryOf, factory, route, out var caller, out var error)) return error!;
        if (!TryWriter(caller, route, out error)) return error!;
        if (!TryFold(factory, out var folded, out error)) return error!;

        // AFTER THE NEWEST VERSION, whatever it is: an import is a whole definition, not an edit of one, and the version
        // it follows stays in the history. A write landing in between is still refused as stale by the store.
        var after = store.Standing(tenant, folded).Newest?.Version ?? 0;
        FileLog.Write($"[FactoryDefinitionEndpoints] {folded}: import of {content.Count} files after version {after} by {caller.Kind}");
        return SaveChecked(store, tenant, folded, content, after, caller, body.Note ?? "imported from a folder", account, nowUtc,
            "The folder was not imported", created: after == 0, saved: saved);
    }

    /// <summary>The factory id factory.yaml names, read with the definition's own parser, or null when it names none.</summary>
    private static string? FactoryNamedIn(IReadOnlyDictionary<string, string> content)
    {
        if (!content.TryGetValue(FactoryDefinitionCheck.FactoryFile, out var text)) return null;
        var (root, _) = DefinitionDocument.ParseYaml(FactoryDefinitionCheck.FactoryFile, text);
        return root is DefinitionMap map && map.Find("factory")?.Value is DefinitionScalar { Kind: DefinitionScalarKind.Text } id
               && !string.IsNullOrWhiteSpace(id.Value)
            ? id.Value
            : null;
    }

    /// <summary>
    /// GET the JSON Schemas the check enforces, one per file type, exactly as embedded: what an editor or an agent
    /// checks a folder's shape against without saving it. The schemas say no more than the check does (the loader
    /// refuses any keyword it does not implement), and the check says more than they do - references, the places a
    /// path may be, the caps - which the note says.
    /// </summary>
    internal static IResult Schemas(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant)
    {
        if (resolveTenant(ctx) is null) return NoAccount();
        var schemas = Enum.GetValues<DefinitionFileKind>().Select(kind => new FactoryDefinitionSchemaDto
        {
            Name = DefinitionSchema.ResourceFileName(kind),
            Applies = kind switch
            {
                DefinitionFileKind.Factory => FactoryDefinitionCheck.FactoryFile,
                DefinitionFileKind.Seat => "seats/<seat>.yaml",
                DefinitionFileKind.Grants => FactoryDefinitionCheck.GrantsFile,
                DefinitionFileKind.Rules => "rules/<name>.json",
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
            },
            Schema = DefinitionSchema.For(kind).Root,
        }).ToList();
        FileLog.Write($"[FactoryDefinitionEndpoints] GET {SchemasRoute}: {schemas.Count} schemas");
        return Results.Json(new FactoryDefinitionSchemasDto
        {
            Note = "Each schema checks the shape of one kind of file. A save or an import also checks what no schema can: " +
                   $"the places a path may be ({DefinitionPaths.PlacesText}), the caps, a factory skill's frontmatter, and that " +
                   "every reference resolves - and answers with every finding by file and line.",
            Schemas = schemas,
        });
    }

    /// <summary>POST a publish: a person makes one version the factory's definition - once the check finds no error in it.</summary>
    internal static async Task<IResult> Publish(HttpContext ctx, string factory, PublishFactoryDefinitionRequest? body,
        FactoryDefinitionStore store, FactoryPublishApply apply, Func<HttpContext, TenantId?> resolveTenant,
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

        return await PublishAsPerson(tenant, folded, body.Version, body.Replaces, caller.Id, store, apply, account, nowUtc(),
            ct: ctx.RequestAborted);
    }

    /// <summary>
    /// A PERSON'S PUBLISH of one definition version, which drives the factory. The one path for it, shared with a
    /// defined factory's brief publish (<see cref="FactoryBriefEndpoints"/>, design section 9), which publishes the whole
    /// definition the same way. <paramref name="published"/> composes the answer to a publish that went through; every
    /// refusal is answered here, the same for both routes. Null answers as this route does.
    /// </summary>
    internal static async Task<IResult> PublishAsPerson(TenantId tenant, string folded, int version, int? replaces, string? personId,
        FactoryDefinitionStore store, FactoryPublishApply apply, Account account, DateTime nowUtc,
        Func<FactoryPublishResult, FactoryDefinitionCheckResult, IResult>? published = null, CancellationToken ct = default)
    {
        // THE STORE'S OWN RULINGS COME FIRST (review of 4004, finding 3): no such version, already published, older
        // than the published one. They say which version is live, and the check's "was not published" must never be
        // said of the version that IS published. They are asked WITHOUT writing (review of 4006, finding 1): a person's
        // publish writes only through the transaction below, which rules again on what it reads.
        if (store.RuleOnPublish(tenant, folded, version, out var wanted) is { } refused)
            return Answer(refused, store, tenant, folded, null);

        var check = await PublishCheckAsync(wanted!.Files, folded, tenant, account, ct);
        if (check.HasErrors)
            return CheckRefused(folded, check, $"Version {version} was not published");

        // THE PUBLISH DRIVES THE FACTORY, in one transaction; what changes is worked out inside it, against the
        // version it really replaces.
        var result = apply.Publish(tenant, folded, version, check.Definition!, wanted.Files, replaces, personId, nowUtc);
        return result.Write.Ok && published is not null ? published(result, check) : Answer(result, store, tenant, folded, check);
    }

    private static string Where(DefinitionFinding f) => FactoryPublishApply.Where(f);

    private static DefinitionFinding? FirstFormatError(IReadOnlyDictionary<string, string> content)
        => FactoryPublishApply.FirstFormatError(content);

    /// <summary>POST a restore: a person publishes a copy of an older version as the newest version - once the check
    /// finds no error in it.</summary>
    internal static async Task<IResult> Restore(HttpContext ctx, string factory, RestoreFactoryDefinitionRequest? body,
        FactoryDefinitionStore store, FactoryPublishApply apply, Func<HttpContext, TenantId?> resolveTenant,
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

        return await RestoreAsPerson(tenant, folded, body.Version, body.ExpectedVersion, body.Replaces, caller.Id, store, apply,
            account, nowUtc(), ct: ctx.RequestAborted);
    }

    /// <summary>
    /// A PERSON'S RESTORE of one older definition version, which drives the factory. Shared with a defined factory's
    /// brief restore exactly as <see cref="PublishAsPerson"/> is shared with its publish.
    /// </summary>
    internal static async Task<IResult> RestoreAsPerson(TenantId tenant, string folded, int version, int expectedVersion, int? replaces,
        string? personId, FactoryDefinitionStore store, FactoryPublishApply apply, Account account, DateTime nowUtc,
        Func<FactoryPublishResult, FactoryDefinitionCheckResult, IResult>? published = null, CancellationToken ct = default)
    {
        // The store's own rulings first, as for a publish and asked without writing: no such version, already the
        // published one, a stale view.
        if (store.RuleOnRestore(tenant, folded, version, expectedVersion, out var wanted) is { } refused)
            return Answer(refused, store, tenant, folded, null);

        var check = await PublishCheckAsync(wanted!.Files, folded, tenant, account, ct);
        if (check.HasErrors)
            return CheckRefused(folded, check, $"Version {version} was not restored");

        // The copy records the version it came from (restoredFrom), so it carries no change note of its own.
        var result = apply.Restore(tenant, folded, version, check.Definition!, wanted.Files, replaces,
            expectedVersion, personId, nowUtc);
        return result.Write.Ok && published is not null ? published(result, check) : Answer(result, store, tenant, folded, check);
    }

    /// <summary>422 for a diff naming a version the format check now refuses: which version, where, and what to do.</summary>
    private static IResult DoesNotParse(string factory, int version, DefinitionFinding problem)
    {
        FileLog.Write($"[FactoryDefinitionEndpoints] {factory}: diff REFUSED (422) - version {version} does not parse at {Where(problem)}");
        return Results.Json(new
        {
            error = $"Version {version} of the definition of {factory} no longer passes the format check at {Where(problem)}, " +
                    "so what changes cannot be shown. Compare other versions with from and to.",
            outcome = "DoesNotParse",
            findings = new[] { ToDto(problem) },
        }, statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    // ---------- the check ----------

    private static FactoryDefinitionCheckResult Check(IReadOnlyDictionary<string, string> content, string factory,
        TenantId tenant, Account account, DefinitionCheckPurpose purpose = DefinitionCheckPurpose.Save)
        => FactoryDefinitionCheck.Check(content, factory, account.FleetSkillsOf(tenant), account.ConnectedMachinesOf(tenant), purpose);

    /// <summary>
    /// THE CHECK A PUBLISH OR A RESTORE RUNS (phase 5, design section 6): ask first, then check. The definition is read
    /// (format only) for the questions its seats raise, each seat's machine is asked them, and the answers go into the
    /// pure check, which words the secret and repository notices from them. A definition that does not parse raises no
    /// question; the check then refuses it on its format.
    /// </summary>
    private static async Task<FactoryDefinitionCheckResult> PublishCheckAsync(IReadOnlyDictionary<string, string> content,
        string factory, TenantId tenant, Account account, CancellationToken ct)
    {
        var questions = FactoryMachineQuestionAsker.QuestionsFor(FactoryDefinitionCheck.Read(content).Definition, content);
        var replies = await account.AskMachines(tenant, questions, ct);
        return FactoryDefinitionCheck.Check(content, factory, account.FleetSkillsOf(tenant), account.ConnectedMachinesOf(tenant),
            DefinitionCheckPurpose.Publish, replies);
    }

    /// <summary>
    /// THE CHECK'S REFUSAL: 422, one sentence, and every finding with its file and line, so a writer can fix each one
    /// where it is. Nothing was stored.
    /// </summary>
    internal static IResult CheckRefused(string factory, FactoryDefinitionCheckResult check, string what)
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

        caller = KindOf(ctx) switch
        {
            FactoryMemoryAuthorKinds.Person => new Caller(FactoryMemoryAuthorKinds.Person, FleetManagerOwnerDevice.Caller(ctx)?.Actor),
            FactoryMemoryAuthorKinds.Session => new Caller(FactoryMemoryAuthorKinds.Session, AuthMiddleware.CallingSession(ctx)!.SessionId.ToString()),
            var other => new Caller(other, null),
        };
        return true;
    }

    /// <summary>What kind of caller this is - a person's device, a session's key, or neither - without asking which
    /// factory it may touch.</summary>
    private static string KindOf(HttpContext ctx)
    {
        var deviceType = ctx.Items.TryGetValue(AuthMiddleware.DeviceTypeItemKey, out var dt) ? dt as string : null;
        if (SessionOriginSurfaces.FromDeviceType(deviceType) != SessionOriginSurfaces.Unknown) return FactoryMemoryAuthorKinds.Person;
        if (AuthMiddleware.CallingSession(ctx) is not null) return FactoryMemoryAuthorKinds.Session;
        return NeitherPersonNorSession;
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

    /// <summary>The answer to a publish or a restore that drove the factory: the write's answer, with what changes and
    /// what the transaction applied on success, and a plan refusal's findings on a refusal.</summary>
    private static IResult Answer(FactoryPublishResult result, FactoryDefinitionStore store, TenantId tenant, string factory,
        FactoryDefinitionCheckResult check)
        => Answer(result.Write, store, tenant, factory, check, result.Changes, result.ChangesNotice,
            applied: result.Applied, refusalFindings: result.RefusalFindings);

    private static IResult Answer(FactoryDefinitionWrite write, FactoryDefinitionStore store, TenantId tenant, string factory,
        FactoryDefinitionCheckResult? check, FactoryDefinitionChangesDto? changes = null, DefinitionFinding? notice = null,
        bool? created = null,
        IReadOnlyList<string>? applied = null, IReadOnlyList<DefinitionFinding>? refusalFindings = null,
        Func<FactoryDefinitionDto, IResult>? saved = null)
    {
        // Read after the write, so the answer says how the factory stands now: which version is newest and which is published.
        var standing = store.Standing(tenant, factory);
        if (write.Ok)
        {
            var dto = ToDto(write.Version!, standing);
            if (check is not null) dto.Findings = check.Findings.Select(ToDto).ToList();
            if (notice is not null) dto.Findings.Add(ToDto(notice));
            dto.Changes = changes;
            dto.Created = created;
            dto.Applied = applied?.ToList();
            return saved is null ? Results.Json(dto) : saved(dto);
        }
        var status = StatusOf(write.Outcome);
        FileLog.Write($"[FactoryDefinitionEndpoints] {factory}: REFUSED ({status}, {write.Outcome}) - {write.Refusal}");
        return Results.Json(new
        {
            error = write.Refusal,
            outcome = write.Outcome.ToString(),
            // THE VERSION THE CALLER NEEDS RIDES THE REFUSAL, without its content: a stale writer reads it with
            // GET .../draft, which is one read it would make anyway.
            current = write.Version is null ? null : ToDto(write.Version, standing),
            // A plan refused by a row rule says which rows, file and line, as the check's refusal does.
            findings = refusalFindings is { Count: > 0 } ? refusalFindings.Select(ToDto).ToList() : null,
        }, statusCode: status);
    }

    /// <summary>The status a refused definition write answers with. Shared with a brief saved into a definition
    /// (<see cref="FactoryBriefEndpoints"/>), so the same refusal never carries two statuses.</summary>
    internal static int StatusOf(FactoryDefinitionOutcome outcome) => outcome switch
    {
        FactoryDefinitionOutcome.OnlyAPersonPublishes => StatusCodes.Status403Forbidden,
        FactoryDefinitionOutcome.NoSuchVersion => StatusCodes.Status404NotFound,
        FactoryDefinitionOutcome.BadFactory or FactoryDefinitionOutcome.Empty or FactoryDefinitionOutcome.TooManyFiles
            or FactoryDefinitionOutcome.BadPath or FactoryDefinitionOutcome.NotText or FactoryDefinitionOutcome.FileTooLarge
            or FactoryDefinitionOutcome.VersionTooLarge or FactoryDefinitionOutcome.ChangeNoteTooLong
            => StatusCodes.Status400BadRequest,
        FactoryDefinitionOutcome.WriteFailed => StatusCodes.Status500InternalServerError,
        FactoryDefinitionOutcome.PlanRefused => StatusCodes.Status422UnprocessableEntity,
        // Stale, too many drafts waiting, already published, older than the published one: "not as things stand".
        _ => StatusCodes.Status409Conflict,
    };

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

    internal static FactoryDefinitionFindingDto ToDto(DefinitionFinding f) => new()
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
