using System.Text.Json;
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
/// <param name="VerdictColourOn">Whether this account's readings are shown (false = still a shadow record).</param>
internal sealed record FleetDigestSources(
    Func<TenantId, IReadOnlyList<SessionDto>> FoldedRoster,
    Func<TenantId, string, bool> SessionInAccount,
    Func<TenantId, string, TurnVerdictDto?> LatestVerdict,
    Func<TenantId, bool> VerdictColourOn);

/// <summary>
/// The Fleet Manager's stored news, its standing preferences, and the one digest it reads at the start of
/// every conversation (the Fleet Manager mission, step 3).
///
///   POST   /gateway/fleet-manager/outcomes                 file a Ready, Finding or Decision -> 201
///   GET    /gateway/fleet-manager/outcomes                 ?status=open|answered|all &amp;kind= &amp;count=
///   GET    /gateway/fleet-manager/outcomes/{id}
///   POST   /gateway/fleet-manager/outcomes/{id}/answer     close it with the owner's words -> 200 | 409
///   GET    /gateway/fleet-manager/preferences
///   POST   /gateway/fleet-manager/preferences              -> 201
///   DELETE /gateway/fleet-manager/preferences/{id}
///   GET    /gateway/fleet-manager/digest?session=&lt;id&gt;
///
/// AUTH. These sit under the non-public <c>/gateway/...</c> prefix, so the host-wide middleware demands a
/// device key or a session key before a handler runs, and <see cref="SessionKeyGuard"/> lists each shape a
/// session key may call. Every handler takes the <see cref="HttpContext"/> and resolves the caller's account
/// from it; a request with no bound account is refused, never served the local partition.
///
/// ANOTHER ACCOUNT'S RECORD ANSWERS EXACTLY WHAT AN UNKNOWN ONE ANSWERS. The stores are partitioned, so a
/// foreign id is simply not found - there is no second branch that could tell the two apart.
///
/// WHO FILED AND WHO ANSWERED is read off the credential, never off the body: the calling session's id for a
/// session key, and <c>owner</c> for a person's device.
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
        FleetDigestSources digest)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(digest);

        // Typed as a handler that RETURNS its result: a bare (HttpContext) => Task<IResult> lambda binds to the
        // RequestDelegate overload, which discards the result and answers an empty 200.
        app.MapPost(Prefix + "/outcomes",
            (Func<HttpContext, Task<IResult>>)(ctx => FileOutcomeAsync(ctx, resolveTenant, outcomes)));
        app.MapGet(Prefix + "/outcomes", (HttpContext ctx) => ListOutcomes(ctx, resolveTenant, outcomes));
        app.MapGet(Prefix + "/outcomes/{id}", (HttpContext ctx, string id) => GetOutcome(ctx, id, resolveTenant, outcomes));
        app.MapPost(Prefix + "/outcomes/{id}/answer", (HttpContext ctx, string id)
            => AnswerOutcomeAsync(ctx, id, resolveTenant, outcomes));

        app.MapGet(Prefix + "/preferences", (HttpContext ctx) => ListPreferences(ctx, resolveTenant, preferences));
        app.MapPost(Prefix + "/preferences",
            (Func<HttpContext, Task<IResult>>)(ctx => AddPreferenceAsync(ctx, resolveTenant, preferences)));
        app.MapDelete(Prefix + "/preferences/{id}", (HttpContext ctx, string id)
            => DeletePreference(ctx, id, resolveTenant, preferences));

        app.MapGet(Prefix + "/digest", (HttpContext ctx) => Digest(ctx, resolveTenant, outcomes, preferences, digest));

        FileLog.Write($"[FleetManagerEndpoints] mapped {Prefix}/outcomes, /preferences and /digest");
    }

    // ---- outcomes ----------------------------------------------------------------------------------------

    internal static async Task<IResult> FileOutcomeAsync(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant,
        FleetOutcomeStore store)
    {
        FileLog.Write("[FleetManagerEndpoints] FileOutcome");
        try
        {
            if (resolveTenant(ctx) is not { } tenant) return NoTenant();
            var (body, error) = await ReadBodyAsync<FleetOutcomeFileRequest>(ctx);
            if (error is not null) return error;

            var outcome = store.File(tenant, body!, CallerOf(ctx), DateTime.UtcNow);
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

    internal static IResult ListOutcomes(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant, FleetOutcomeStore store)
    {
        FileLog.Write($"[FleetManagerEndpoints] ListOutcomes: query={ctx.Request.QueryString}");
        try
        {
            if (resolveTenant(ctx) is not { } tenant) return NoTenant();
            var q = ctx.Request.Query;
            var status = q.TryGetValue("status", out var s) ? s.ToString() : FleetOutcomeStore.StatusOpen;
            var kind = q.TryGetValue("kind", out var k) && !string.IsNullOrEmpty(k.ToString()) ? k.ToString() : null;
            var count = FleetOutcomeStore.DefaultCount;
            if (q.TryGetValue("count", out var c) && !int.TryParse(c.ToString(), out count))
                return BadRequest($"count '{c}' is not a whole number between 1 and {FleetOutcomeStore.MaxCount}");

            var rows = store.List(tenant, status, kind, count);
            FileLog.Write($"[FleetManagerEndpoints] ListOutcomes: returned={rows.Count}");
            return Results.Json(new { count = rows.Count, outcomes = rows });
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
        FleetOutcomeStore store)
    {
        FileLog.Write($"[FleetManagerEndpoints] GetOutcome: id={id}");
        try
        {
            if (resolveTenant(ctx) is not { } tenant) return NoTenant();
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
        Func<HttpContext, TenantId?> resolveTenant, FleetOutcomeStore store)
    {
        FileLog.Write($"[FleetManagerEndpoints] AnswerOutcome: id={id}");
        try
        {
            if (resolveTenant(ctx) is not { } tenant) return NoTenant();
            if (!Guid.TryParse(id, out var guid)) return BadId(id);
            var (body, error) = await ReadBodyAsync<FleetOutcomeAnswerRequest>(ctx);
            if (error is not null) return error;

            var result = store.Answer(tenant, guid, body!.Answer, CallerOf(ctx), DateTime.UtcNow);
            FileLog.Write($"[FleetManagerEndpoints] AnswerOutcome: id={id}, status={result.Status}");
            return result.Status switch
            {
                FleetOutcomeAnswerStatus.Answered => Results.Json(result.Outcome),
                FleetOutcomeAnswerStatus.NotFound => OutcomeNotFound(id),
                FleetOutcomeAnswerStatus.AlreadyAnswered => Results.Json(new
                {
                    error = $"outcome {id} was already answered at {result.Outcome!.AnsweredAtUtc:O} by "
                          + $"{result.Outcome.AnsweredBy}; an answer is final and was not changed",
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
        FleetPreferenceStore store)
    {
        FileLog.Write("[FleetManagerEndpoints] ListPreferences");
        try
        {
            if (resolveTenant(ctx) is not { } tenant) return NoTenant();
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
        FleetPreferenceStore store)
    {
        FileLog.Write("[FleetManagerEndpoints] AddPreference");
        try
        {
            if (resolveTenant(ctx) is not { } tenant) return NoTenant();
            var (body, error) = await ReadBodyAsync<FleetPreferenceRequest>(ctx);
            if (error is not null) return error;
            var pref = store.Add(tenant, body!.Text, CallerOf(ctx), DateTime.UtcNow);
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
        FleetPreferenceStore store)
    {
        FileLog.Write($"[FleetManagerEndpoints] DeletePreference: id={id}");
        try
        {
            if (resolveTenant(ctx) is not { } tenant) return NoTenant();
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

    // ---- digest ------------------------------------------------------------------------------------------

    /// <summary>
    /// Everything a Fleet Manager reads at the start of a conversation, in one answer: the account's open
    /// records, the sessions the named session owns (its direct ones - their controlling session is it) with
    /// the Wingman's latest reading of each, the standing preferences, and the counts.
    ///
    /// The named session must be in the caller's account; one that is not answers 404 exactly as an unknown
    /// id does. The caller need not be a Fleet Manager - the answer says whether the named session is one.
    ///
    /// THE SHADOW RULE OF THE TURN VERDICT ROUTE APPLIES HERE TOO. While an account's readings are a shadow
    /// record, a session key is not served them; the digest leaves them out and says so, rather than handing
    /// automation a record the account has not turned on.
    /// </summary>
    internal static IResult Digest(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant,
        FleetOutcomeStore outcomes, FleetPreferenceStore preferences, FleetDigestSources sources)
    {
        var sid = ctx.Request.Query.TryGetValue("session", out var raw) ? raw.ToString().Trim() : "";
        FileLog.Write($"[FleetManagerEndpoints] Digest: session={sid}");
        try
        {
            if (resolveTenant(ctx) is not { } tenant) return NoTenant();
            if (sid.Length == 0)
                return BadRequest("session is required: GET /gateway/fleet-manager/digest?session=<full session id>");
            if (!Guid.TryParse(sid, out var parsed))
                return BadRequest($"session '{sid}' is not a session id; give the full id");
            sid = parsed.ToString();
            if (!sources.SessionInAccount(tenant, sid))
                return Results.Json(new { error = $"no session {sid} in this account" },
                    statusCode: StatusCodes.Status404NotFound);

            var roster = sources.FoldedRoster(tenant);
            var self = roster.FirstOrDefault(s => string.Equals(s.SessionId, sid, StringComparison.OrdinalIgnoreCase));

            var withhold = !sources.VerdictColourOn(tenant) && AuthMiddleware.CallingSession(ctx) is not null;
            var owned = roster
                .Where(s => string.Equals(s.ControllerSessionId, sid, StringComparison.OrdinalIgnoreCase))
                .Where(s => !string.Equals(s.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase) || s.Crashed)
                .OrderBy(s => s.CreatedAt)
                .Select(s => new FleetOwnedSessionDto
                {
                    SessionId = s.SessionId,
                    Name = s.Name ?? "",
                    State = SessionTree.CrewState(s),
                    StateLabel = s.StateLabel ?? "",
                    MissionName = s.MissionName,
                    UncommittedCount = s.UncommittedCount,
                    TurnVerdict = withhold ? null : sources.LatestVerdict(tenant, s.SessionId),
                })
                .ToList();

            var open = outcomes.List(tenant, FleetOutcomeStore.StatusOpen, kind: null, FleetOutcomeStore.MaxCount).ToList();
            var answer = new FleetDigestDto
            {
                SessionId = sid,
                IsFleetManager = FleetManagerSessions.IsFleetManager(self),
                GeneratedAtUtc = DateTime.UtcNow,
                Outcomes = open,
                OwnedSessions = owned,
                Preferences = preferences.List(tenant).ToList(),
                OutcomeCounts = new FleetOutcomeCounts
                {
                    Ready = open.Count(o => o.Kind == FleetOutcomeStore.KindReady),
                    Finding = open.Count(o => o.Kind == FleetOutcomeStore.KindFinding),
                    Decision = open.Count(o => o.Kind == FleetOutcomeStore.KindDecision),
                    Total = open.Count,
                },
                OwnedSessionCounts = new FleetOwnedSessionCounts
                {
                    NeedsYou = owned.Count(o => o.State == SessionTree.CrewStateNeedsYou),
                    Working = owned.Count(o => o.State == SessionTree.CrewStateWorking),
                    Stopped = owned.Count(o => o.State == SessionTree.CrewStateStopped),
                    Total = owned.Count,
                },
                VerdictsWithheld = withhold,
                VerdictsWithheldReason = withhold
                    ? "this account's Wingman readings are still a shadow record, and they are not served to a "
                      + "session key until the account turns the verdict colours on"
                    : null,
            };

            FileLog.Write($"[FleetManagerEndpoints] Digest: session={sid}, isFleetManager={answer.IsFleetManager}, "
                          + $"open={open.Count}, owned={owned.Count}, preferences={answer.Preferences.Count}, withheld={withhold}");
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

    // ---- shared ------------------------------------------------------------------------------------------

    /// <summary>The calling session's id for a session key, <c>owner</c> for anything else the middleware
    /// admitted (a person's device).</summary>
    internal static string CallerOf(HttpContext ctx)
        => AuthMiddleware.CallingSession(ctx)?.SessionId.ToString() ?? FleetOutcomeStore.OwnerCaller;

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
