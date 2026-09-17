using System.Text.Json;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Reports;
using CcDirector.Gateway.Util;
using CcDirector.Gateway.Wingman;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The one stop handler, reachable from outside the route table that owns it. <see cref="GatewayEndpoints"/> sets
/// <see cref="Stop"/> when it maps <c>POST /sessions/{sid}/stop</c>, so the walkthrough's close runs THAT handler -
/// the same fold, the same audit row, the same answer - rather than a second stop.
/// </summary>
internal sealed class SessionStopDoor
{
    /// <summary>(request, session id, reason, cancellation) to the stop route's own answer. Null until mapped.</summary>
    public Func<HttpContext, string, string, CancellationToken, Task<IResult>>? Stop { get; set; }
}

/// <summary>Where the walkthrough routes read their facts. Delegates, so the tenant partition, the roster fold and the
/// stores stay in the one place that owns each of them.</summary>
internal sealed record FleetManagerWalkthroughSources(
    Func<TenantId, IReadOnlyList<SessionDto>> LiveRoster,
    Func<TenantId, string?> MarkedSessionId,
    Func<TenantId, string, SessionDto?> LastKnownSession,
    Func<TenantId, string, TurnVerdictDto?> LatestVerdict,
    Func<TenantId, string, TurnVerdictLocated?> FindVerdict,
    Func<TenantId, IReadOnlyList<StoredRepoState>> Repositories,
    Func<TenantId, int> SnoozeMinutes,
    Func<TenantId, TimeZoneInfo> TimeZone,
    Func<DateTime> NowUtc,
    SessionStopDoor StopDoor);

/// <summary>
/// "Take me through them" (the Fleet Manager mission, step 7):
///
///   GET  /gateway/fleet-manager/walkthrough?round=&lt;id&gt;,&lt;id&gt;   the round, folded (FleetManagerWalkthroughFold)
///   POST /gateway/fleet-manager/walkthrough/{id}/answered         record the options the session just took
///   POST /gateway/fleet-manager/walkthrough/{id}/snoozed          record that the owner snoozed the session
///   POST /gateway/fleet-manager/walkthrough/{id}/close            decide close again, stop, then record it
///
/// THE OWNER'S, ONLY. Each route answers only the owner on their own signed-in phone or browser: a session key is
/// refused here and by <see cref="SessionKeyGuard"/>, and so is a Director's key. The records these routes answer are
/// answered as the owner, so nothing but the owner's own device may reach them.
///
/// ACT ON THE SESSION FIRST, THEN RECORD WHAT HAPPENED. Step 6's card answers the record first and then tells the Fleet
/// Manager, because the Fleet Manager can be told again and the record is the truth. Here the act is on the SESSION,
/// and the session's routes refuse often and on purpose - the answer route refuses a screen that has changed, the
/// stop route a computer that is not connected. A record is final, so it must only carry what the session actually
/// took: the answer route marks a verdict answered only when the Director confirmed the write, and the "answered"
/// route here records nothing unless that mark is there; the close route stops first and records only a stop that
/// happened. So a refused answer or a refused stop leaves the record open and says nothing to the Fleet Manager.
/// </summary>
internal static class FleetManagerWalkthroughEndpoints
{
    public const string WalkthroughRoute = FleetManagerEndpoints.Prefix + "/walkthrough";

    private static readonly JsonSerializerOptions BodyJsonOptions = new(JsonSerializerDefaults.Web);

    public static void Map(IEndpointRouteBuilder app, Func<HttpContext, TenantId?> resolveTenant,
        FleetOutcomeStore outcomes, FleetManagerWalkthroughSources sources)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(sources);

        app.MapGet(WalkthroughRoute, (HttpContext ctx) => Read(ctx, resolveTenant, outcomes, sources));
        app.MapPost(WalkthroughRoute + "/{id}/answered", (HttpContext ctx, string id)
            => AnsweredAsync(ctx, id, resolveTenant, outcomes, sources));
        app.MapPost(WalkthroughRoute + "/{id}/snoozed", (HttpContext ctx, string id)
            => Snoozed(ctx, id, resolveTenant, outcomes, sources));
        app.MapPost(WalkthroughRoute + "/{id}/close", (HttpContext ctx, string id, CancellationToken ct)
            => CloseAsync(ctx, id, resolveTenant, outcomes, sources, ct));
        FileLog.Write($"[FleetManagerWalkthroughEndpoints] mapped {WalkthroughRoute} and its answered, snoozed and close verbs");
    }

    // ---- the read ------------------------------------------------------------------------------------------

    internal static IResult Read(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant,
        FleetOutcomeStore outcomes, FleetManagerWalkthroughSources sources)
    {
        FileLog.Write($"[FleetManagerWalkthroughEndpoints] GET walkthrough: query={ctx.Request.QueryString}");
        try
        {
            if (resolveTenant(ctx) is not { } tenant) return NoTenant();
            if (OwnerOnly(ctx, "read the walkthrough") is { } refused) return refused;

            IReadOnlyList<Guid>? round = null;
            if (ctx.Request.Query.TryGetValue("round", out var raw))
            {
                var ids = new List<Guid>();
                foreach (var part in raw.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!Guid.TryParse(part, out var g))
                        return BadRequest($"round '{part}' is not a record id; send back the roundIds the walkthrough gave you, or leave round out to start a new round");
                    ids.Add(g);
                }
                if (ids.Count > FleetManagerWalkthroughFold.MaxRoundItems)
                    return BadRequest($"round names {ids.Count} records; a round holds at most {FleetManagerWalkthroughFold.MaxRoundItems}");
                round = ids;
            }

            var dto = FleetManagerWalkthroughFold.Fold(Inputs(tenant, outcomes, sources, round));
            FileLog.Write($"[FleetManagerWalkthroughEndpoints] GET walkthrough: round={dto.Items.Count}, open={dto.OpenCount}, "
                          + $"given={(round is null ? "new" : round.Count.ToString())}, notInRound={dto.NotInRound ?? "none"}");
            return Results.Json(dto);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetManagerWalkthroughEndpoints] GET walkthrough FAILED: {ex.Message}");
            throw;
        }
    }

    internal static FleetManagerWalkthroughInputs Inputs(TenantId tenant, FleetOutcomeStore outcomes,
        FleetManagerWalkthroughSources sources, IReadOnlyList<Guid>? round)
    {
        var marked = sources.MarkedSessionId(tenant);
        return new FleetManagerWalkthroughInputs(
            marked,
            string.IsNullOrWhiteSpace(marked) ? null : sources.LastKnownSession(tenant, marked.Trim()),
            sources.LiveRoster(tenant),
            sid => sources.LastKnownSession(tenant, sid),
            // Every open record, never a first page, exactly as the page reads them.
            outcomes.ListOpen(tenant),
            round,
            id => outcomes.Get(tenant, id),
            sid => sources.LatestVerdict(tenant, sid),
            sources.Repositories(tenant),
            sources.SnoozeMinutes(tenant),
            sources.TimeZone(tenant),
            sources.NowUtc());
    }

    // ---- answered ------------------------------------------------------------------------------------------

    /// <summary>
    /// Record, as the owner's answer to the record, the options the session has just taken. The words are the options'
    /// own keys, joined in the order they were picked - the Gateway reads them off the stored verdict, the client sends
    /// only positions. Nothing is recorded unless the answer route marked that verdict answered (the Director confirmed
    /// the write), and the verdict must be about the record's own session.
    /// </summary>
    internal static async Task<IResult> AnsweredAsync(HttpContext ctx, string id, Func<HttpContext, TenantId?> resolveTenant,
        FleetOutcomeStore outcomes, FleetManagerWalkthroughSources sources)
    {
        FileLog.Write($"[FleetManagerWalkthroughEndpoints] answered: id={id}");
        try
        {
            if (resolveTenant(ctx) is not { } tenant) return NoTenant();
            if (OwnerOnly(ctx, "record a walkthrough answer") is { } refused) return refused;
            if (!Guid.TryParse(id, out var guid)) return BadId(id);
            var (body, error) = await ReadBodyAsync<FleetWalkthroughAnsweredRequest>(ctx);
            if (error is not null) return error;

            var record = outcomes.Get(tenant, guid);
            if (record is null) return RecordNotFound(id);
            if (string.IsNullOrWhiteSpace(body!.VerdictId))
                return BadRequest("verdictId is required: the verdict whose options the session took");
            if (body.OptionIndexes is null)
                return BadRequest("optionIndexes is required: the positions the session took, or an empty list for a confirmed typed reply");

            var located = sources.FindVerdict(tenant, body.VerdictId.Trim());
            if (located is null)
                return Results.Json(new { error = $"no verdict {body.VerdictId} in this account, so nothing was recorded" },
                    statusCode: StatusCodes.Status404NotFound);
            if (!string.Equals(located.SessionId, record.SessionId, StringComparison.OrdinalIgnoreCase))
                return Conflict("verdict_other_session",
                    $"verdict {body.VerdictId} is about another session than this record, so nothing was recorded");
            if (located.AnsweredAtUtc is null)
                return Conflict("not_answered",
                    "the session has not taken an answer to that verdict, so nothing was recorded; answer it first");

            var options = located.Verdict.Options;
            var indexes = body.OptionIndexes;
            if (indexes.Distinct().Count() != indexes.Count)
                return BadRequest("optionIndexes names an option more than once");
            var outOfRange = indexes.Where(i => i < 0 || i >= options.Count).ToList();
            if (outOfRange.Count > 0)
                return BadRequest($"optionIndexes {string.Join(", ", outOfRange)} {(outOfRange.Count == 1 ? "is" : "are")} not "
                                  + $"an option of that verdict, which has {options.Count}");
            string words;
            if (indexes.Count == 0)
            {
                if (options.Count > 0)
                    return BadRequest("optionIndexes is empty, and an empty list is only the confirm of a typed reply; that verdict has options");
                words = "Sent the reply typed on the screen.";
            }
            else
            {
                words = string.Join(", ", indexes.Select(i => options[i].Key));
            }

            var result = outcomes.Answer(tenant, guid, words, FleetOutcomeStore.OwnerCaller, FleetOutcomeStore.RoleOwner,
                sources.NowUtc());
            FileLog.Write($"[FleetManagerWalkthroughEndpoints] answered: id={id}, verdict={body.VerdictId}, status={result.Status}");
            return AnswerResult(id, result);
        }
        catch (ArgumentException ex)
        {
            FileLog.Write($"[FleetManagerWalkthroughEndpoints] answered refused: {ex.Message}");
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetManagerWalkthroughEndpoints] answered FAILED: {ex.Message}");
            throw;
        }
    }

    // ---- snoozed -------------------------------------------------------------------------------------------

    /// <summary>
    /// Record on the record that the owner snoozed its session, once the snooze route has taken it. A snooze is not an
    /// answer - the question is still open - so the record stays open and carries the note, which the Fleet Manager's
    /// digest serves. The note is written only when the Gateway sees the session snoozed now.
    /// </summary>
    internal static IResult Snoozed(HttpContext ctx, string id, Func<HttpContext, TenantId?> resolveTenant,
        FleetOutcomeStore outcomes, FleetManagerWalkthroughSources sources)
    {
        FileLog.Write($"[FleetManagerWalkthroughEndpoints] snoozed: id={id}");
        try
        {
            if (resolveTenant(ctx) is not { } tenant) return NoTenant();
            if (OwnerOnly(ctx, "record a snooze") is { } refused) return refused;
            if (!Guid.TryParse(id, out var guid)) return BadId(id);
            var record = outcomes.Get(tenant, guid);
            if (record is null) return RecordNotFound(id);
            if (string.IsNullOrWhiteSpace(record.SessionId))
                return Conflict("no_session", "this record is about no session, so there is no snooze to record");

            var row = sources.LiveRoster(tenant)
                .FirstOrDefault(s => string.Equals(s.SessionId, record.SessionId, StringComparison.OrdinalIgnoreCase));
            var facts = new FleetWalkthroughSessionFacts(record.SessionId, row, row, null);
            if (!facts.Snoozed)
                return Conflict("not_snoozed",
                    $"session {record.SessionId} is not snoozed, so nothing was recorded; snooze it first");

            var tz = sources.TimeZone(tenant);
            var now = sources.NowUtc();
            var note = row!.SnoozeUntil is { } until
                ? $"The owner snoozed the session from the walkthrough, until {FleetManagerPlacementFold.FormatWhen(until, tz, now)}."
                : "The owner snoozed the session from the walkthrough; the snooze starts when it stops working.";
            var result = outcomes.NoteOwnerAction(tenant, guid, note, now);
            FileLog.Write($"[FleetManagerWalkthroughEndpoints] snoozed: id={id}, status={result.Status}");
            return result.Status switch
            {
                FleetOutcomeUpdateStatus.Updated => Results.Json(result.Outcome),
                FleetOutcomeUpdateStatus.NotFound => RecordNotFound(id),
                FleetOutcomeUpdateStatus.AlreadyAnswered => Conflict("already_answered",
                    $"outcome {id} is already answered, so the snooze was not recorded on it"),
                _ => throw new InvalidOperationException($"unhandled update status {result.Status}"),
            };
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetManagerWalkthroughEndpoints] snoozed FAILED: {ex.Message}");
            throw;
        }
    }

    // ---- close ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Close the record's session: decide again with <see cref="FleetManagerCloseRule"/> - a hidden button is not a
    /// refusal - then run the ONE stop handler, then answer the record with <see cref="FleetManagerWalkthroughFold.CloseWords"/>
    /// only when the stop answer says a session was stopped. A refused close is 409 <c>close_refused</c> with the rule's
    /// sentence, and nothing is stopped or recorded.
    /// </summary>
    internal static async Task<IResult> CloseAsync(HttpContext ctx, string id, Func<HttpContext, TenantId?> resolveTenant,
        FleetOutcomeStore outcomes, FleetManagerWalkthroughSources sources, CancellationToken ct)
    {
        FileLog.Write($"[FleetManagerWalkthroughEndpoints] close: id={id}");
        try
        {
            if (resolveTenant(ctx) is not { } tenant) return NoTenant();
            if (OwnerOnly(ctx, "close a session from the walkthrough") is { } refused) return refused;
            if (!Guid.TryParse(id, out var guid)) return BadId(id);
            var record = outcomes.Get(tenant, guid);
            if (record is null) return RecordNotFound(id);
            if (record.Status != FleetOutcomeStore.StatusOpen)
                return Conflict("already_answered", $"outcome {id} is already answered, so nothing was closed");
            if (string.IsNullOrWhiteSpace(record.SessionId))
                return Conflict("no_session", "this record is about no session, so there is nothing to close");

            var inputs = Inputs(tenant, outcomes, sources, round: Array.Empty<Guid>());
            var fleetManager = FleetManagerSessions.IsFleetManager(inputs.MarkedSession, inputs.MarkedSessionId?.Trim())
                ? inputs.MarkedSessionId!.Trim()
                : null;
            var fresh = inputs.LiveRoster.FirstOrDefault(s => string.Equals(s.SessionId, record.SessionId, StringComparison.OrdinalIgnoreCase));
            var facts = new FleetWalkthroughSessionFacts(record.SessionId, fresh,
                fresh ?? inputs.LastKnownSession(record.SessionId), inputs.LatestVerdict(record.SessionId));
            var decision = FleetManagerWalkthroughFold.Decide(facts, fleetManager, inputs);
            if (!decision.Allowed)
            {
                FileLog.Write($"[FleetManagerWalkthroughEndpoints] close REFUSED: id={id}, session={record.SessionId}: {decision.Refusal}");
                return Conflict("close_refused", decision.Refusal ?? "Close is not allowed for this session.");
            }

            var stop = sources.StopDoor.Stop
                ?? throw new InvalidOperationException("the stop route is not mapped, so the walkthrough cannot close a session");
            var stopResult = await stop(ctx, record.SessionId, FleetManagerWalkthroughFold.StopReason, ct);
            var status = (stopResult as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status200OK;
            if (status < 200 || status >= 300 || (stopResult as IValueHttpResult)?.Value is not SessionStopResponse answer)
            {
                FileLog.Write($"[FleetManagerWalkthroughEndpoints] close: id={id}, the stop answered {status}; the record is left open");
                return stopResult;
            }

            var response = new FleetWalkthroughCloseResponse { Stop = answer };
            var stopped = answer.Verdict is SessionStopVerdict.Stopped or SessionStopVerdict.AlreadyStopped
                or SessionStopVerdict.StoppedNotDescribed;
            if (!stopped)
            {
                response.RecordError = $"The stop answered \"{answer.Headline}\", so the record was left open.";
                FileLog.Write($"[FleetManagerWalkthroughEndpoints] close: id={id}, stop verdict={answer.Verdict}; record left open");
                return Results.Json(response);
            }

            var result = outcomes.Answer(tenant, guid, FleetManagerWalkthroughFold.CloseWords,
                FleetOutcomeStore.OwnerCaller, FleetOutcomeStore.RoleOwner, sources.NowUtc());
            response.Outcome = result.Outcome;
            if (result.Status != FleetOutcomeAnswerStatus.Answered)
                response.RecordError = result.Status == FleetOutcomeAnswerStatus.AlreadyAnswered
                    ? $"The session was closed, but the record had already been answered at {result.Outcome!.AnsweredAtUtc:O} "
                      + $"by {result.Outcome.AnsweredBy}, so the close was not recorded on it."
                    : "The session was closed, but the record is no longer in this account, so the close was not recorded.";
            FileLog.Write($"[FleetManagerWalkthroughEndpoints] close: id={id}, stop verdict={answer.Verdict}, record={result.Status}");
            return Results.Json(response);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetManagerWalkthroughEndpoints] close FAILED: {ex.Message}");
            throw;
        }
    }

    // ---- shared --------------------------------------------------------------------------------------------

    /// <summary>The owner on their own signed-in phone or browser, and nobody else (<see cref="FleetManagerOwnerDevice"/>).</summary>
    internal static IResult? OwnerOnly(HttpContext ctx, string what)
    {
        FleetManagerOwnerDevice.Require(ctx, what,
            "The walkthrough is the owner's. A session reads GET /gateway/fleet-manager/digest.",
            nameof(FleetManagerWalkthroughEndpoints), out var refusal);
        return refusal;
    }

    private static IResult AnswerResult(string id, FleetOutcomeAnswerResult result)
        => result.Status switch
        {
            FleetOutcomeAnswerStatus.Answered => Results.Json(result.Outcome),
            FleetOutcomeAnswerStatus.NotFound => RecordNotFound(id),
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

    private static IResult Conflict(string code, string message)
        => Results.Json(new { code, error = message }, statusCode: StatusCodes.Status409Conflict);

    private static IResult BadId(string id) => BadRequest($"'{id}' is not an id; give the full id");

    private static IResult RecordNotFound(string id)
        => Results.Json(new { error = $"no outcome {id} in this account" }, statusCode: StatusCodes.Status404NotFound);
}
