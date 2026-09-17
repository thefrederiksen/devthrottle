using System.Text.Json;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Where the Gateway reads the facts the digest needs. Delegates rather than the stores themselves, so the
/// tenant partition and the roster fold stay in the one place that owns each of them.
/// </summary>
/// <param name="FoldedRoster">This account's whole roster, stamped by the same fold the roster route runs.</param>
/// <param name="SessionInAccount">Whether this account has ever been pushed this session id, however stale.</param>
/// <param name="LatestVerdict">The Wingman's latest stored reading of one session in this account.</param>
/// <param name="FormerFleetManagers">Every session this account has ever marked as its Fleet Manager
/// (<see cref="FleetManagerMarkHistory"/>).</param>
internal sealed record FleetDigestSources(
    Func<TenantId, IReadOnlyList<SessionDto>> FoldedRoster,
    Func<TenantId, string, bool> SessionInAccount,
    Func<TenantId, string, TurnVerdictDto?> LatestVerdict,
    Func<TenantId, IReadOnlyList<string>> FormerFleetManagers);

/// <summary>
/// Where the Gateway reads the two facts that decide WHO may call these routes.
/// </summary>
/// <param name="MarkedSessionId">The session this account has marked as its Fleet Manager, or null
/// (the <c>fleet_manager_session_id</c> tenant setting).</param>
/// <param name="LastKnownSession">What this account's Directors last pushed for one session id, however stale, or
/// null when none has pushed it - read for whether a session owns the marked session.</param>
internal sealed record FleetManagerAccess(
    Func<TenantId, string?> MarkedSessionId,
    Func<TenantId, string, SessionDto?> LastKnownSession);

/// <summary>Who is calling a Fleet Manager route, once <see cref="FleetManagerEndpoints.Authorise"/> has allowed it.</summary>
/// <param name="Id">What is recorded as the filer or answerer: the Fleet Manager's session id, or <c>owner</c>.</param>
/// <param name="Role"><see cref="FleetOutcomeStore.RoleOwner"/> or <see cref="FleetOutcomeStore.RoleFleetManager"/>.</param>
internal sealed record FleetManagerCaller(string Id, string Role);

/// <summary>What a caller is asking to do, for the authority check and for the words of a refusal.</summary>
internal enum FleetManagerAction
{
    FileRecord,
    ListRecords,
    ReadRecord,
    AnswerRecord,
    ReadPreferences,
    AddPreference,
    DeletePreference,
    ReadDigest,
    ListEvents,
    AcknowledgeEvents,
}

/// <summary>
/// The Fleet Manager's stored news, its standing preferences, and the one digest it reads at the start of
/// every conversation (the Fleet Manager mission, step 3).
///
///   POST   /gateway/fleet-manager/outcomes                 file a Ready, Finding or Decision -> 201
///   GET    /gateway/fleet-manager/outcomes                 ?status=open|answered|all &amp;kind= &amp;count= &amp;cursor=
///                                                          -> { count, total, hasMore, nextCursor, outcomes }
///   GET    /gateway/fleet-manager/outcomes/{id}
///   POST   /gateway/fleet-manager/outcomes/{id}/answer     close it with the owner's words -> 200 | 409
///   GET    /gateway/fleet-manager/preferences
///   POST   /gateway/fleet-manager/preferences              -> 201
///   DELETE /gateway/fleet-manager/preferences/{id}
///   GET    /gateway/fleet-manager/digest?session=&lt;id&gt;
///   GET    /gateway/fleet-manager/events                   ?status=unacknowledged|all &amp;count= &amp;cursor=   (step 4)
///   POST   /gateway/fleet-manager/events/ack               { ids: [...] } or { all: true }
///
/// AUTH. These sit under the non-public <c>/gateway/...</c> prefix, so the host-wide middleware demands a
/// device key or a session key before a handler runs, and <see cref="SessionKeyGuard"/> lists each shape a
/// session key may reach. Every handler takes the <see cref="HttpContext"/> and resolves the caller's account
/// from it; a request with no bound account is refused, never served the local partition.
///
/// AUTHORITY, BEYOND THE ACCOUNT (<see cref="Authorise"/>). Reaching a route is not being allowed to use it:
///  - A SESSION KEY is allowed only when its session is the account's marked Fleet Manager
///    (<see cref="FleetManagerSessions.IsFleetManager"/>). Every other session of the account - a Worker, the
///    owner's own session, an Architect - is refused, and the refusal says why. It may file, list, read and
///    answer records, read, add and forget preferences, and read its own digest.
///  - THE OWNER, on their own signed-in device (a phone or browser device key - never a Director's key, never
///    a session key), may list, read and answer records, manage preferences, read the digest and list the events.
///    The owner does not file records (a record is the Fleet Manager's news for the owner) and does not
///    acknowledge events (an event is the Fleet Manager's work).
///  - Anything else (a Director's own key, the shared machine token) is refused.
///
/// GAP, STATED: the mark itself is set through <c>PUT /gateway/fleet-manager</c>, which step 2 lets any session
/// key of the account reach. A session can therefore mark ITSELF and then pass this check - a visible, logged
/// act that displaces the real Fleet Manager - until that route is narrowed.
///
/// ANOTHER ACCOUNT'S RECORD ANSWERS EXACTLY WHAT AN UNKNOWN ONE ANSWERS. The stores are partitioned, so a
/// foreign id is simply not found - there is no second branch that could tell the two apart.
///
/// WHO FILED AND WHO ANSWERED is read off the credential, never off the body: the Fleet Manager's session id
/// for its session key, and <c>owner</c> for the owner's device; an answer also records the role that gave it.
/// </summary>
internal static class FleetManagerEndpoints
{
    public const string Prefix = "/gateway/fleet-manager";

    private static readonly JsonSerializerOptions BodyJsonOptions = new(JsonSerializerDefaults.Web);

    public static void Map(
        IEndpointRouteBuilder app,
        Func<HttpContext, TenantId?> resolveTenant,
        FleetOutcomeStore outcomes,
        FleetPreferenceStore preferences,
        FleetDigestSources digest,
        FleetManagerAccess access,
        FleetManagerEventStore events)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(digest);
        ArgumentNullException.ThrowIfNull(access);

        // Typed as a handler that RETURNS its result: a bare (HttpContext) => Task<IResult> lambda binds to the
        // RequestDelegate overload, which discards the result and answers an empty 200.
        app.MapPost(Prefix + "/outcomes",
            (Func<HttpContext, Task<IResult>>)(ctx => FileOutcomeAsync(ctx, resolveTenant, access, outcomes)));
        app.MapGet(Prefix + "/outcomes", (HttpContext ctx) => ListOutcomes(ctx, resolveTenant, access, outcomes));
        app.MapGet(Prefix + "/outcomes/{id}", (HttpContext ctx, string id)
            => GetOutcome(ctx, id, resolveTenant, access, outcomes));
        app.MapPost(Prefix + "/outcomes/{id}/answer", (HttpContext ctx, string id)
            => AnswerOutcomeAsync(ctx, id, resolveTenant, access, outcomes));

        app.MapGet(Prefix + "/preferences", (HttpContext ctx) => ListPreferences(ctx, resolveTenant, access, preferences));
        app.MapPost(Prefix + "/preferences",
            (Func<HttpContext, Task<IResult>>)(ctx => AddPreferenceAsync(ctx, resolveTenant, access, preferences)));
        app.MapDelete(Prefix + "/preferences/{id}", (HttpContext ctx, string id)
            => DeletePreference(ctx, id, resolveTenant, access, preferences));

        app.MapGet(Prefix + "/digest", (HttpContext ctx)
            => Digest(ctx, resolveTenant, access, outcomes, preferences, digest, events));

        app.MapGet(Prefix + "/events", (HttpContext ctx) => ListEvents(ctx, resolveTenant, access, events));
        app.MapPost(Prefix + "/events/ack",
            (Func<HttpContext, Task<IResult>>)(ctx => AcknowledgeEventsAsync(ctx, resolveTenant, access, events)));

        FileLog.Write($"[FleetManagerEndpoints] mapped {Prefix}/outcomes, /preferences, /digest and /events");
    }

    // ---- outcomes ----------------------------------------------------------------------------------------

    internal static async Task<IResult> FileOutcomeAsync(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant,
        FleetManagerAccess access, FleetOutcomeStore store)
    {
        FileLog.Write("[FleetManagerEndpoints] FileOutcome");
        try
        {
            if (resolveTenant(ctx) is not { } tenant) return NoTenant();
            var (caller, refused) = Authorise(ctx, tenant, access, FleetManagerAction.FileRecord);
            if (refused is not null) return refused;
            var (body, error) = await ReadBodyAsync<FleetOutcomeFileRequest>(ctx);
            if (error is not null) return error;

            var outcome = store.File(tenant, body!, caller!.Id, DateTime.UtcNow);
            FileLog.Write($"[FleetManagerEndpoints] FileOutcome: id={outcome.Id}, kind={outcome.Kind}");
            return Results.Json(outcome, statusCode: StatusCodes.Status201Created);
        }
        catch (ArgumentException ex)
        {
            FileLog.Write($"[FleetManagerEndpoints] FileOutcome refused: {ex.Message}");
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetManagerEndpoints] FileOutcome FAILED: {ex.Message}");
            throw;
        }
    }

    internal static IResult ListOutcomes(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant,
        FleetManagerAccess access, FleetOutcomeStore store)
    {
        FileLog.Write($"[FleetManagerEndpoints] ListOutcomes: query={ctx.Request.QueryString}");
        try
        {
            if (resolveTenant(ctx) is not { } tenant) return NoTenant();
            var (_, refused) = Authorise(ctx, tenant, access, FleetManagerAction.ListRecords);
            if (refused is not null) return refused;
            var q = ctx.Request.Query;
            var status = q.TryGetValue("status", out var s) ? s.ToString() : FleetOutcomeStore.StatusOpen;
            var kind = q.TryGetValue("kind", out var k) && !string.IsNullOrEmpty(k.ToString()) ? k.ToString() : null;
            var count = FleetOutcomeStore.DefaultCount;
            if (q.TryGetValue("count", out var c) && !int.TryParse(c.ToString(), out count))
                return BadRequest($"count '{c}' is not a whole number between 1 and {FleetOutcomeStore.MaxCount}");

            var cursor = q.TryGetValue("cursor", out var cur) ? cur.ToString() : null;
            if (cursor is not null && cursor.Trim().Length == 0)
                return BadRequest("cursor is empty; give the nextCursor of the page before, or leave cursor out to start from the newest");

            var page = store.ListPage(tenant, status, kind, count, cursor);
            // The page is capped; the total is counted by the database, and the cursor continues after the last
            // record served, so a reader can always reach every record the filter matches.
            var total = store.Count(tenant, status, kind);
            FileLog.Write($"[FleetManagerEndpoints] ListOutcomes: returned={page.Outcomes.Count}, total={total}, "
                          + $"hasMore={page.NextCursor is not null}");
            return Results.Json(new
            {
                count = page.Outcomes.Count,
                total,
                hasMore = page.NextCursor is not null,
                nextCursor = page.NextCursor,
                outcomes = page.Outcomes,
            });
        }
        catch (ArgumentException ex)
        {
            FileLog.Write($"[FleetManagerEndpoints] ListOutcomes refused: {ex.Message}");
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetManagerEndpoints] ListOutcomes FAILED: {ex.Message}");
            throw;
        }
    }

    internal static IResult GetOutcome(HttpContext ctx, string id, Func<HttpContext, TenantId?> resolveTenant,
        FleetManagerAccess access, FleetOutcomeStore store)
    {
        FileLog.Write($"[FleetManagerEndpoints] GetOutcome: id={id}");
        try
        {
            if (resolveTenant(ctx) is not { } tenant) return NoTenant();
            var (_, refused) = Authorise(ctx, tenant, access, FleetManagerAction.ReadRecord);
            if (refused is not null) return refused;
            if (!Guid.TryParse(id, out var guid)) return BadId(id);
            var outcome = store.Get(tenant, guid);
            return outcome is null ? OutcomeNotFound(id) : Results.Json(outcome);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetManagerEndpoints] GetOutcome FAILED: {ex.Message}");
            throw;
        }
    }

    internal static async Task<IResult> AnswerOutcomeAsync(HttpContext ctx, string id,
        Func<HttpContext, TenantId?> resolveTenant, FleetManagerAccess access, FleetOutcomeStore store)
    {
        FileLog.Write($"[FleetManagerEndpoints] AnswerOutcome: id={id}");
        try
        {
            if (resolveTenant(ctx) is not { } tenant) return NoTenant();
            var (caller, refused) = Authorise(ctx, tenant, access, FleetManagerAction.AnswerRecord);
            if (refused is not null) return refused;
            if (!Guid.TryParse(id, out var guid)) return BadId(id);
            var (body, error) = await ReadBodyAsync<FleetOutcomeAnswerRequest>(ctx);
            if (error is not null) return error;

            var result = store.Answer(tenant, guid, body!.Answer, caller!.Id, caller.Role, DateTime.UtcNow);
            FileLog.Write($"[FleetManagerEndpoints] AnswerOutcome: id={id}, status={result.Status}");
            return result.Status switch
            {
                FleetOutcomeAnswerStatus.Answered => Results.Json(result.Outcome),
                FleetOutcomeAnswerStatus.NotFound => OutcomeNotFound(id),
                FleetOutcomeAnswerStatus.AlreadyAnswered => Results.Json(new
                {
                    code = "already_answered",
                    error = $"outcome {id} was already answered at {result.Outcome!.AnsweredAtUtc:O} by "
                          + $"{result.Outcome.AnsweredBy} ({result.Outcome.AnsweredByRole}); an answer is final and "
                          + "this one was not recorded",
                    outcome = result.Outcome,
                }, statusCode: StatusCodes.Status409Conflict),
                _ => throw new InvalidOperationException($"unhandled answer status {result.Status}"),
            };
        }
        catch (ArgumentException ex)
        {
            FileLog.Write($"[FleetManagerEndpoints] AnswerOutcome refused: {ex.Message}");
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetManagerEndpoints] AnswerOutcome FAILED: {ex.Message}");
            throw;
        }
    }

    // ---- preferences -------------------------------------------------------------------------------------

    internal static IResult ListPreferences(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant,
        FleetManagerAccess access, FleetPreferenceStore store)
    {
        FileLog.Write("[FleetManagerEndpoints] ListPreferences");
        try
        {
            if (resolveTenant(ctx) is not { } tenant) return NoTenant();
            var (_, refused) = Authorise(ctx, tenant, access, FleetManagerAction.ReadPreferences);
            if (refused is not null) return refused;
            var rows = store.List(tenant);
            return Results.Json(new { count = rows.Count, preferences = rows });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetManagerEndpoints] ListPreferences FAILED: {ex.Message}");
            throw;
        }
    }

    internal static async Task<IResult> AddPreferenceAsync(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant,
        FleetManagerAccess access, FleetPreferenceStore store)
    {
        FileLog.Write("[FleetManagerEndpoints] AddPreference");
        try
        {
            if (resolveTenant(ctx) is not { } tenant) return NoTenant();
            var (caller, refused) = Authorise(ctx, tenant, access, FleetManagerAction.AddPreference);
            if (refused is not null) return refused;
            var (body, error) = await ReadBodyAsync<FleetPreferenceRequest>(ctx);
            if (error is not null) return error;
            var pref = store.Add(tenant, body!.Text, caller!.Id, DateTime.UtcNow);
            return Results.Json(pref, statusCode: StatusCodes.Status201Created);
        }
        catch (ArgumentException ex)
        {
            FileLog.Write($"[FleetManagerEndpoints] AddPreference refused: {ex.Message}");
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetManagerEndpoints] AddPreference FAILED: {ex.Message}");
            throw;
        }
    }

    internal static IResult DeletePreference(HttpContext ctx, string id, Func<HttpContext, TenantId?> resolveTenant,
        FleetManagerAccess access, FleetPreferenceStore store)
    {
        FileLog.Write($"[FleetManagerEndpoints] DeletePreference: id={id}");
        try
        {
            if (resolveTenant(ctx) is not { } tenant) return NoTenant();
            var (_, refused) = Authorise(ctx, tenant, access, FleetManagerAction.DeletePreference);
            if (refused is not null) return refused;
            if (!Guid.TryParse(id, out var guid)) return BadId(id);
            return store.Delete(tenant, guid)
                ? Results.Json(new { deleted = true, id = guid.ToString() })
                : Results.Json(new { error = $"no preference {id} in this account; list them with GET {Prefix}/preferences" },
                    statusCode: StatusCodes.Status404NotFound);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetManagerEndpoints] DeletePreference FAILED: {ex.Message}");
            throw;
        }
    }

    // ---- events ------------------------------------------------------------------------------------------

    /// <summary>
    /// The account's events about sessions a Fleet Manager owns, oldest first. Default: the unacknowledged ones.
    /// Readable by the account's Fleet Manager and by the owner on their own device, like the records.
    /// </summary>
    internal static IResult ListEvents(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant,
        FleetManagerAccess access, FleetManagerEventStore store)
    {
        FileLog.Write($"[FleetManagerEndpoints] ListEvents: query={ctx.Request.QueryString}");
        try
        {
            if (resolveTenant(ctx) is not { } tenant) return NoTenant();
            var (_, refused) = Authorise(ctx, tenant, access, FleetManagerAction.ListEvents);
            if (refused is not null) return refused;
            var q = ctx.Request.Query;
            var status = q.TryGetValue("status", out var s) ? s.ToString() : FleetManagerEventStore.StatusUnacknowledged;
            var count = FleetManagerEventStore.DefaultCount;
            if (q.TryGetValue("count", out var c) && !int.TryParse(c.ToString(), out count))
                return BadRequest($"count '{c}' is not a whole number between 1 and {FleetManagerEventStore.MaxCount}");

            var cursor = q.TryGetValue("cursor", out var cur) ? cur.ToString() : null;
            if (cursor is not null && cursor.Trim().Length == 0)
                return BadRequest("cursor is empty; give the nextCursor of the page before, or leave cursor out to start from the first page");

            var page = store.ListPage(tenant, status, count, cursor);
            var total = store.Count(tenant, status);
            FileLog.Write($"[FleetManagerEndpoints] ListEvents: returned={page.Events.Count}, total={total}, "
                          + $"hasMore={page.NextCursor is not null}");
            return Results.Json(new FleetManagerEventListDto
            {
                Count = page.Events.Count,
                Total = total,
                HasMore = page.NextCursor is not null,
                NextCursor = page.NextCursor,
                Events = page.Events.ToList(),
            });
        }
        catch (ArgumentException ex)
        {
            FileLog.Write($"[FleetManagerEndpoints] ListEvents refused: {ex.Message}");
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetManagerEndpoints] ListEvents FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Acknowledge events by id, or every unacknowledged event that was delivered to the calling session. All or
    /// nothing: an id that is not this account's event is refused by name (404) and nothing is changed, and a stop
    /// still waiting for its Wingman reading is refused by name (409, <c>reading_pending</c>) and nothing is changed -
    /// it has not been delivered, and it is acknowledged once it has.
    ///
    /// ONLY THE FLEET MANAGER ACKNOWLEDGES. An acknowledgement says "I have acted on this", and the events are the
    /// Fleet Manager's work: only the account's marked Fleet Manager session key may close them. Every other
    /// session key, and the owner's own device, is refused with a reason. Acknowledging all closes only the events
    /// the Gateway delivered to that session, never one it has not been sent.
    /// </summary>
    internal static async Task<IResult> AcknowledgeEventsAsync(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant,
        FleetManagerAccess access, FleetManagerEventStore store)
    {
        FileLog.Write("[FleetManagerEndpoints] AcknowledgeEvents");
        try
        {
            if (resolveTenant(ctx) is not { } tenant) return NoTenant();
            var (caller, refused) = Authorise(ctx, tenant, access, FleetManagerAction.AcknowledgeEvents);
            if (refused is not null) return refused;
            var (body, error) = await ReadBodyAsync<FleetManagerEventAckRequest>(ctx);
            if (error is not null) return error;

            var ids = new List<Guid>();
            foreach (var raw in body!.Ids ?? new List<string>())
            {
                if (!Guid.TryParse(raw, out var id)) return BadId(raw ?? "");
                ids.Add(id);
            }

            var result = store.Acknowledge(tenant, ids, body.All, caller!.Id, DateTime.UtcNow);
            if (result.Status == FleetManagerEventAckStatus.NotFound)
            {
                var missing = string.Join(", ", result.Missing);
                return Results.Json(new
                {
                    error = $"no event {missing} in this account; nothing was acknowledged. "
                          + $"List them with GET {Prefix}/events?status=all",
                    missing = result.Missing.Select(m => m.ToString()).ToList(),
                }, statusCode: StatusCodes.Status404NotFound);
            }
            if (result.Status == FleetManagerEventAckStatus.ReadingPending)
            {
                var pending = string.Join(", ", result.Pending);
                return Results.Json(new
                {
                    error = $"event {pending} is a stop still waiting for the Wingman's reading; nothing was acknowledged. "
                          + "It is delivered to you when its reading is stored, or with the reason there is none after "
                          + $"{FleetManagerEventStore.PendingLimit.TotalMinutes:F0} minutes - act on it and acknowledge it then",
                    code = "reading_pending",
                    pending = result.Pending.Select(p => p.ToString()).ToList(),
                }, statusCode: StatusCodes.Status409Conflict);
            }

            FileLog.Write($"[FleetManagerEndpoints] AcknowledgeEvents: by={caller.Id}, all={body.All}, "
                          + $"acknowledged={result.Acknowledged.Count}, already={result.AlreadyAcknowledged}");
            return Results.Json(new FleetManagerEventAckDto
            {
                Acknowledged = result.Acknowledged.Count,
                AlreadyAcknowledged = result.AlreadyAcknowledged,
                Ids = result.Acknowledged.Select(id => id.ToString()).ToList(),
            });
        }
        catch (ArgumentException ex)
        {
            FileLog.Write($"[FleetManagerEndpoints] AcknowledgeEvents refused: {ex.Message}");
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetManagerEndpoints] AcknowledgeEvents FAILED: {ex.Message}");
            throw;
        }
    }

    // ---- digest ------------------------------------------------------------------------------------------

    /// <summary>
    /// Everything a Fleet Manager reads at the start of a conversation, in one answer: EVERY open record of the
    /// account, the sessions its Fleet Managers own with the Wingman's latest reading of each, the standing
    /// preferences, and the counts.
    ///
    /// WHO: the account's Fleet Manager, for its own session only, or the owner on their own device for any
    /// session of the account. The named session must be in the caller's account; one that is not answers 404
    /// exactly as an unknown id does.
    ///
    /// NOTHING IS CUT SHORT. The open records are all served, and their counts come from the database, so a reset
    /// Fleet Manager rebuilds the whole of the outstanding work.
    ///
    /// OWNED SESSIONS ARE EVERY FLEET MANAGER'S, NOT ONLY THE CURRENT ONE'S. A new Fleet Manager session is marked
    /// in the old one's place, and the sessions the old one started are still controlled by the old id. So the
    /// owned sessions are those controlled by the current mark OR by any session this account marked before
    /// (<see cref="FleetManagerMarkHistory"/>, which keeps the most recent
    /// <see cref="FleetManagerMarkHistory.MaxRememberedPerAccount"/>), each shown with its owner's id. The list of
    /// Fleet Manager ids names the current mark and an earlier one only while it still controls a live session.
    /// Handing them over to the new Fleet Manager is a later step (hand over); until then the digest shows who
    /// still holds each one.
    ///
    /// THE READINGS ARE SERVED. The Wingman's readings of the sessions a Fleet Manager owns are what it works
    /// from (the design's digest carries them), and only the Fleet Manager itself or the owner may read this
    /// answer - so the colour switch, which decides what the owner's SCREENS show, does not withhold them here.
    /// </summary>
    internal static IResult Digest(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant,
        FleetManagerAccess access, FleetOutcomeStore outcomes, FleetPreferenceStore preferences,
        FleetDigestSources sources, FleetManagerEventStore events)
    {
        var sid = ctx.Request.Query.TryGetValue("session", out var raw) ? raw.ToString().Trim() : "";
        FileLog.Write($"[FleetManagerEndpoints] Digest: session={sid}");
        try
        {
            if (resolveTenant(ctx) is not { } tenant) return NoTenant();
            var (caller, refused) = Authorise(ctx, tenant, access, FleetManagerAction.ReadDigest);
            if (refused is not null) return refused;
            if (sid.Length == 0)
                return BadRequest("session is required: GET /gateway/fleet-manager/digest?session=<full session id>");
            if (!Guid.TryParse(sid, out var parsed))
                return BadRequest($"session '{sid}' is not a session id; give the full id");
            sid = parsed.ToString();
            if (caller!.Role == FleetOutcomeStore.RoleFleetManager
                && !string.Equals(caller.Id, sid, StringComparison.OrdinalIgnoreCase))
                return Refuse($"the Fleet Manager reads its own digest only; ask for session={caller.Id}, not {sid}");
            if (!sources.SessionInAccount(tenant, sid))
                return Results.Json(new { error = $"no session {sid} in this account" },
                    statusCode: StatusCodes.Status404NotFound);

            var roster = sources.FoldedRoster(tenant);
            var self = roster.FirstOrDefault(s => string.Equals(s.SessionId, sid, StringComparison.OrdinalIgnoreCase));
            var marked = access.MarkedSessionId(tenant);

            // In the order the account marked them; a current mark set before the history existed comes last.
            var marks = sources.FormerFleetManagers(tenant).ToList();
            if (!string.IsNullOrEmpty(marked) && !marks.Contains(marked, StringComparer.OrdinalIgnoreCase))
                marks.Add(marked);
            var managerSet = new HashSet<string>(marks, StringComparer.OrdinalIgnoreCase);

            var owned = roster
                .Where(s => !string.IsNullOrEmpty(s.ControllerSessionId) && managerSet.Contains(s.ControllerSessionId))
                .Where(s => !string.Equals(s.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase) || s.Crashed)
                .OrderBy(s => s.CreatedAt)
                .Select(s => new FleetOwnedSessionDto
                {
                    SessionId = s.SessionId,
                    OwnerSessionId = s.ControllerSessionId!,
                    Name = s.Name ?? "",
                    State = SessionTree.CrewState(s),
                    StateLabel = s.StateLabel ?? "",
                    MissionName = s.MissionName,
                    UncommittedCount = s.UncommittedCount,
                    TurnVerdict = sources.LatestVerdict(tenant, s.SessionId),
                })
                .ToList();

            // The current mark always; an earlier one only while it still controls a live session - the only
            // reason the digest names it at all - so the list does not grow with every reset.
            var managers = marks
                .Where(m => string.Equals(m, marked, StringComparison.OrdinalIgnoreCase)
                            || owned.Any(o => string.Equals(o.OwnerSessionId, m, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var open = outcomes.ListOpen(tenant).ToList();
            var counts = outcomes.CountOpen(tenant);
            var answer = new FleetDigestDto
            {
                SessionId = sid,
                IsFleetManager = FleetManagerSessions.IsFleetManager(self, marked),
                FleetManagerSessionId = marked,
                FleetManagerSessionIds = managers,
                GeneratedAtUtc = DateTime.UtcNow,
                Outcomes = open,
                OwnedSessions = owned,
                Preferences = preferences.List(tenant).ToList(),
                OutcomeCounts = counts,
                OwnedSessionCounts = new FleetOwnedSessionCounts
                {
                    NeedsYou = owned.Count(o => o.State == SessionTree.CrewStateNeedsYou),
                    Working = owned.Count(o => o.State == SessionTree.CrewStateWorking),
                    Stopped = owned.Count(o => o.State == SessionTree.CrewStateStopped),
                    Total = owned.Count,
                },
            };
            // The oldest page of what is owed; the rest are reached by the cursor, and the digest says so.
            var eventPage = events.ListPage(tenant, FleetManagerEventStore.StatusUnacknowledged,
                FleetManagerEventStore.MaxCount, cursor: null);
            answer.Events = eventPage.Events.ToList();
            answer.EventsTotal = events.Count(tenant, FleetManagerEventStore.StatusUnacknowledged);
            answer.EventsWaitingForReading = events.CountPending(tenant);
            answer.EventsHasMore = eventPage.NextCursor is not null;
            answer.EventsNextCursor = eventPage.NextCursor;

            FileLog.Write($"[FleetManagerEndpoints] Digest: session={sid}, caller={caller.Role}, "
                          + $"isFleetManager={answer.IsFleetManager}, open={open.Count}, openCounted={counts.Total}, "
                          + $"managers={managers.Count}, owned={owned.Count}, preferences={answer.Preferences.Count}, "
                          + $"events={answer.Events.Count}, eventsTotal={answer.EventsTotal}, eventsHasMore={answer.EventsHasMore}");
            return Results.Json(answer);
        }
        catch (ArgumentException ex)
        {
            FileLog.Write($"[FleetManagerEndpoints] Digest refused: {ex.Message}");
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetManagerEndpoints] Digest FAILED: {ex.Message}");
            throw;
        }
    }

    // ---- authority ---------------------------------------------------------------------------------------

    /// <summary>
    /// Who may do <paramref name="action"/> in <paramref name="tenant"/>: the account's marked Fleet Manager
    /// session, or the owner on their own signed-in device (everything but filing and acknowledging). Everyone else is refused with
    /// a 403 whose words say why. See the AUTHORITY note on this class.
    /// </summary>
    internal static (FleetManagerCaller? Caller, IResult? Refusal) Authorise(
        HttpContext ctx, TenantId tenant, FleetManagerAccess access, FleetManagerAction action)
    {
        var what = Describe(action);

        if (AuthMiddleware.CallingSession(ctx) is { } session)
        {
            var sid = session.SessionId.ToString();
            var marked = access.MarkedSessionId(tenant);
            if (string.IsNullOrEmpty(marked))
                return Deny(action, sid, $"only this account's Fleet Manager session may {what}, and this account has "
                    + "no Fleet Manager marked; the owner marks one with: cc-devthrottle fleet-manager set <session>");
            if (!string.Equals(marked, sid, StringComparison.OrdinalIgnoreCase))
                return Deny(action, sid, $"only this account's Fleet Manager session ({marked}) may {what}; "
                    + $"session {sid} is not it");

            var row = access.LastKnownSession(tenant, sid);
            if (!FleetManagerSessions.IsFleetManager(row, marked))
            {
                var why = row is null
                    ? $"session {sid} is marked as the Fleet Manager but no Director of this account has reported it"
                    : $"session {sid} is marked as the Fleet Manager but is owned by session {row.ControllerSessionId}, "
                      + "and a Fleet Manager answers to the owner only";
                return Deny(action, sid, $"{why}, so it may not {what}");
            }

            FileLog.Write($"[FleetManagerEndpoints] Authorise: {action} allowed for the Fleet Manager {sid}");
            return (new FleetManagerCaller(sid, FleetOutcomeStore.RoleFleetManager), null);
        }

        var deviceType = ctx.Items.TryGetValue(AuthMiddleware.DeviceTypeItemKey, out var dt) ? dt as string : null;
        var ownersDevice = SessionOriginSurfaces.FromDeviceType(deviceType) != SessionOriginSurfaces.Unknown;
        if (ownersDevice)
        {
            if (action == FleetManagerAction.FileRecord)
                return Deny(action, $"device:{deviceType}", "the owner does not file records - a record is the Fleet "
                    + "Manager's news for the owner, filed with the Fleet Manager session's own key");
            if (action == FleetManagerAction.AcknowledgeEvents)
                return Deny(action, $"device:{deviceType}", "the owner does not acknowledge events - an event is the "
                    + "Fleet Manager's work, closed with the Fleet Manager session's own key once it has acted on it");
            FileLog.Write($"[FleetManagerEndpoints] Authorise: {action} allowed for the owner ({deviceType})");
            return (new FleetManagerCaller(FleetOutcomeStore.OwnerCaller, FleetOutcomeStore.RoleOwner), null);
        }

        var kind = AuthMiddleware.IdentityKind(ctx);
        var credential = deviceType is null ? kind : $"{kind} ({deviceType})";
        return Deny(action, credential, $"only this account's Fleet Manager session or the owner on their own signed-in "
            + $"phone or browser may {what}; this request was made with a {credential} credential");
    }

    private static (FleetManagerCaller?, IResult?) Deny(FleetManagerAction action, string who, string why)
    {
        FileLog.Write($"[FleetManagerEndpoints] Authorise: {action} REFUSED for {who}: {why}");
        return (null, Refuse(why));
    }

    private static IResult Refuse(string why)
        => Results.Json(new { code = "not_fleet_manager", error = why }, statusCode: StatusCodes.Status403Forbidden);

    private static string Describe(FleetManagerAction action) => action switch
    {
        FleetManagerAction.FileRecord => "file a record",
        FleetManagerAction.ListRecords => "list the records",
        FleetManagerAction.ReadRecord => "read a record",
        FleetManagerAction.AnswerRecord => "answer a record",
        FleetManagerAction.ReadPreferences => "read the standing preferences",
        FleetManagerAction.AddPreference => "add a standing preference",
        FleetManagerAction.DeletePreference => "forget a standing preference",
        FleetManagerAction.ReadDigest => "read the digest",
        FleetManagerAction.ListEvents => "list the events",
        FleetManagerAction.AcknowledgeEvents => "acknowledge events",
        _ => throw new InvalidOperationException($"unhandled Fleet Manager action {action}"),
    };

    // ---- shared ------------------------------------------------------------------------------------------

    private static async Task<(T? Body, IResult? Error)> ReadBodyAsync<T>(HttpContext ctx) where T : class
    {
        try
        {
            var body = await JsonSerializer.DeserializeAsync<T>(ctx.Request.Body, BodyJsonOptions, ctx.RequestAborted);
            return body is null ? (null, BadRequest("a JSON body is required")) : (body, null);
        }
        catch (JsonException ex)
        {
            return (null, BadRequest($"the body is not valid JSON: {ex.Message}"));
        }
    }

    private static IResult NoTenant()
        => Results.Json(new { error = "no account is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);

    private static IResult BadRequest(string message)
        => Results.Json(new { error = message }, statusCode: StatusCodes.Status400BadRequest);

    private static IResult BadId(string id)
        => BadRequest($"'{id}' is not an id; give the full id");

    private static IResult OutcomeNotFound(string id)
        => Results.Json(new { error = $"no outcome {id} in this account; list them with GET {Prefix}/outcomes?status=all" },
            statusCode: StatusCodes.Status404NotFound);
}
