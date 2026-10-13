using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Briefs;
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
///   GET  /factory-briefs/{factory}/{seat}/history   every version, newest first
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
/// A CALLER THAT IS NEITHER - the account's shared machine token, or a Director - may read, as that gate allows, and
/// may not write: every version records who wrote it, a person or which session, and such a caller is neither.
/// </summary>
internal static class FactoryBriefEndpoints
{
    internal const string Prefix = "/factory-briefs";

    public static void Map(IEndpointRouteBuilder app,
        Func<HttpContext, TenantId?> resolveTenant,
        FactoryBriefStore store,
        Func<string, SessionFactoryLookup> sessionFactoryOf,
        Func<DateTime> nowUtc)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sessionFactoryOf);
        ArgumentNullException.ThrowIfNull(nowUtc);

        const string seatRoute = Prefix + "/{factory}/{seat}";
        app.MapGet(seatRoute, (HttpContext ctx, string factory, string seat) =>
            ReadPublished(ctx, factory, seat, store, resolveTenant, sessionFactoryOf));
        app.MapGet(seatRoute + "/draft", (HttpContext ctx, string factory, string seat) =>
            ReadNewest(ctx, factory, seat, store, resolveTenant, sessionFactoryOf));
        app.MapGet(seatRoute + "/history", (HttpContext ctx, string factory, string seat) =>
            History(ctx, factory, seat, store, resolveTenant, sessionFactoryOf));
        app.MapPut(seatRoute + "/draft", (HttpContext ctx, string factory, string seat, SaveFactoryBriefDraftRequest? body) =>
            SaveDraft(ctx, factory, seat, body, store, resolveTenant, sessionFactoryOf, nowUtc));
        app.MapPost(seatRoute + "/publish", (HttpContext ctx, string factory, string seat, PublishFactoryBriefRequest? body) =>
            Publish(ctx, factory, seat, body, store, resolveTenant, sessionFactoryOf, nowUtc));
        app.MapPost(seatRoute + "/restore", (HttpContext ctx, string factory, string seat, BringBackFactoryBriefRequest? body) =>
            BringBack(ctx, factory, seat, body, store, resolveTenant, sessionFactoryOf, nowUtc));

        FileLog.Write($"[FactoryBriefEndpoints] mapped {seatRoute} and its draft, history, publish and restore routes");
    }

    /// <summary>GET the published brief: what a talk or a run of the seat is given.</summary>
    internal static IResult ReadPublished(HttpContext ctx, string factory, string seat, FactoryBriefStore store,
        Func<HttpContext, TenantId?> resolveTenant, Func<string, SessionFactoryLookup> sessionFactoryOf)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        if (!TryCaller(ctx, sessionFactoryOf, factory, $"GET {Prefix}/{factory}/{seat}", out _, out var error)) return error!;
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
        Func<HttpContext, TenantId?> resolveTenant, Func<string, SessionFactoryLookup> sessionFactoryOf)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        if (!TryCaller(ctx, sessionFactoryOf, factory, $"GET {Prefix}/{factory}/{seat}/draft", out _, out var error)) return error!;
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

    /// <summary>GET every version of the seat's brief, newest first.</summary>
    internal static IResult History(HttpContext ctx, string factory, string seat, FactoryBriefStore store,
        Func<HttpContext, TenantId?> resolveTenant, Func<string, SessionFactoryLookup> sessionFactoryOf)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        if (!TryCaller(ctx, sessionFactoryOf, factory, $"GET {Prefix}/{factory}/{seat}/history", out _, out var error)) return error!;
        if (store.WhyNotASeat(tenant, factory, seat) is { } notASeat) return Answer(notASeat, store, tenant, factory, seat);

        var standing = store.Standing(tenant, factory, seat);
        var versions = store.History(tenant, factory, seat);
        return Results.Json(new FactoryBriefHistoryResponse
        {
            Factory = versions.FirstOrDefault()?.Factory ?? factory.Trim().ToLowerInvariant(),
            Seat = versions.FirstOrDefault()?.Seat ?? seat.Trim().ToLowerInvariant(),
            PublishedVersion = standing.Published?.Version,
            Versions = versions.Select(v => ToDto(v, standing)).ToList(),
        });
    }

    /// <summary>PUT a draft: a person, or a session of the factory, saves the next version.</summary>
    internal static IResult SaveDraft(HttpContext ctx, string factory, string seat, SaveFactoryBriefDraftRequest? body,
        FactoryBriefStore store, Func<HttpContext, TenantId?> resolveTenant,
        Func<string, SessionFactoryLookup> sessionFactoryOf, Func<DateTime> nowUtc)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        var route = $"PUT {Prefix}/{factory}/{seat}/draft";
        if (!TryCaller(ctx, sessionFactoryOf, factory, route, out var caller, out var error)) return error!;
        if (!TryWriter(caller, route, out error)) return error!;
        if (body is null) return NoBody();
        return Answer(store.SaveDraft(tenant, factory, seat, body.Text, body.ExpectedVersion, caller.Kind, caller.Id, nowUtc()),
            store, tenant, factory, seat);
    }

    /// <summary>POST a publish: a person makes one version the brief talks and runs are given.</summary>
    internal static IResult Publish(HttpContext ctx, string factory, string seat, PublishFactoryBriefRequest? body,
        FactoryBriefStore store, Func<HttpContext, TenantId?> resolveTenant,
        Func<string, SessionFactoryLookup> sessionFactoryOf, Func<DateTime> nowUtc)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        var route = $"POST {Prefix}/{factory}/{seat}/publish";
        if (!TryCaller(ctx, sessionFactoryOf, factory, route, out var caller, out var error)) return error!;
        if (!TryWriter(caller, route, out error)) return error!;
        if (body is null) return NoBody();
        return Answer(store.Publish(tenant, factory, seat, body.Version, caller.Kind, caller.Id, nowUtc()),
            store, tenant, factory, seat);
    }

    /// <summary>POST a bring back: a person publishes a copy of an older version as the newest version.</summary>
    internal static IResult BringBack(HttpContext ctx, string factory, string seat, BringBackFactoryBriefRequest? body,
        FactoryBriefStore store, Func<HttpContext, TenantId?> resolveTenant,
        Func<string, SessionFactoryLookup> sessionFactoryOf, Func<DateTime> nowUtc)
    {
        if (resolveTenant(ctx) is not { } tenant) return NoAccount();
        var route = $"POST {Prefix}/{factory}/{seat}/restore";
        if (!TryCaller(ctx, sessionFactoryOf, factory, route, out var caller, out var error)) return error!;
        if (!TryWriter(caller, route, out error)) return error!;
        if (body is null) return NoBody();
        return Answer(store.BringBack(tenant, factory, seat, body.Version, caller.Kind, caller.Id, nowUtc(), body.ExpectedVersion),
            store, tenant, factory, seat);
    }

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
            // Stale, already published, older than the published one: "not as things stand".
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
