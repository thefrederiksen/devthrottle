using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Requests to a team's Owner and Managers (devthrottle_internal#2308, screen S9).
///
/// <list type="bullet">
/// <item><c>POST /teams/{teamId}/requests</c> with <c>{"text": "..."}</c> - send a request. Every member may.</item>
/// <item><c>GET /teams/{teamId}/requests/mine</c> - the caller's own requests, each with its trail.</item>
/// <item><c>GET /teams/{teamId}/requests</c> - the team's whole Requests list, for the Owner and Managers.</item>
/// <item><c>POST /teams/{teamId}/requests/{requestId}/accept</c>, <c>/decline</c> with <c>{"reason": "..."}</c>,
/// and <c>/done</c> - decide a request, for the Owner and Managers.</item>
/// </list>
///
/// A PERSON'S OWN WORDS NEVER REACH AN AGENT, and the routes make that structural: they answer only a person's own
/// signed-in phone or browser key (<see cref="RequirePerson"/>). A session key is refused - and is refused before that
/// by the session-key guard's default deny - and so is a Director's or a Gateway's device key and the shared machine
/// token, so no agent and no Director can send, read or change a request. The sender is the person that key names,
/// stamped by the server; nothing in a body names anyone.
///
/// Every route is declared to the team gate (<see cref="TeamEndpointRules"/>), which asks the role table before the
/// route runs; the store asks it again. DARK until the owner releases Teams: mapped only when
/// <c>CC_GATEWAY_TEAMS=1</c>, beside the other team routes.
/// </summary>
internal static class TeamRequestEndpoints
{
    /// <summary>The error code on a refusal for a caller that is not a person's own phone or browser.</summary>
    public const string PersonOnlyCode = "person_only";

    /// <summary>What every caller that is not a person's own phone or browser is told.</summary>
    internal const string PersonOnlyRefusal =
        "Requests are written and read by people, from their own signed-in phone or browser. They are never shown to an " +
        "agent or a Director, so this request was refused. Nothing was done.";

    internal sealed record SendRequest(string? Text);

    internal sealed record DecideRequest(string? Reason);

    /// <summary>Maps the six routes.</summary>
    public static void Map(IEndpointRouteBuilder app, TeamRequestStore requests, HostedTenantBoundary boundary, TenantRegistry tenants)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(boundary);
        ArgumentNullException.ThrowIfNull(tenants);

        var root = TeamEndpoints.Path + "/{teamId}/requests";

        app.MapPost(root, (HttpContext ctx, string teamId) => Guarded("POST requests", async () =>
        {
            var caller = Caller(ctx, boundary, tenants);
            if (caller.Denial is not null) return caller.Denial;
            var body = await ReadBody<SendRequest>(ctx, "{\"text\": \"<your request>\"}").ConfigureAwait(false);
            if (body.Denial is not null) return body.Denial;
            return Answer(requests.Send(teamId, caller.Subject!, body.Value?.Text), "send", StatusCodes.Status201Created);
        }));

        app.MapGet(root + "/mine", (HttpContext ctx, string teamId) => Guarded("GET requests/mine", () =>
        {
            var caller = Caller(ctx, boundary, tenants);
            return Task.FromResult(caller.Denial ?? AnswerList(requests.ListMine(teamId, caller.Subject!), "mine"));
        }));

        app.MapGet(root, (HttpContext ctx, string teamId) => Guarded("GET requests", () =>
        {
            var caller = Caller(ctx, boundary, tenants);
            return Task.FromResult(caller.Denial ?? AnswerList(requests.ListForTeam(teamId, caller.Subject!), "team"));
        }));

        app.MapPost(root + "/{requestId}/accept", (HttpContext ctx, string teamId, string requestId) => Guarded("POST requests/accept", () =>
        {
            var caller = Caller(ctx, boundary, tenants);
            return Task.FromResult(caller.Denial
                ?? Answer(requests.Decide(teamId, requestId, caller.Subject!, TeamRequestDecision.Accept, null), "accept", StatusCodes.Status200OK));
        }));

        app.MapPost(root + "/{requestId}/decline", (HttpContext ctx, string teamId, string requestId) => Guarded("POST requests/decline", async () =>
        {
            var caller = Caller(ctx, boundary, tenants);
            if (caller.Denial is not null) return caller.Denial;
            var body = await ReadBody<DecideRequest>(ctx, "{\"reason\": \"<why it is not being done>\"}").ConfigureAwait(false);
            if (body.Denial is not null) return body.Denial;
            return Answer(requests.Decide(teamId, requestId, caller.Subject!, TeamRequestDecision.Decline, body.Value?.Reason), "decline", StatusCodes.Status200OK);
        }));

        app.MapPost(root + "/{requestId}/done", (HttpContext ctx, string teamId, string requestId) => Guarded("POST requests/done", () =>
        {
            var caller = Caller(ctx, boundary, tenants);
            return Task.FromResult(caller.Denial
                ?? Answer(requests.Decide(teamId, requestId, caller.Subject!, TeamRequestDecision.MarkDone, null), "done", StatusCodes.Status200OK));
        }));

        FileLog.Write($"[TeamRequestEndpoints] mapped {root} (POST, GET), {root}/mine, {root}/{{requestId}}/accept|decline|done");
    }

    /// <summary>
    /// Who is asking: a PERSON, on their own signed-in phone or browser, and the account their key names. Anything
    /// else - a session key, a Director's or a Gateway's device key, the shared machine token - is refused before the
    /// account is even looked up.
    /// </summary>
    internal static (string? Subject, IResult? Denial) Caller(HttpContext ctx, HostedTenantBoundary boundary, TenantRegistry tenants)
    {
        if (RequirePerson(ctx) is { } refused)
            return (null, refused);
        return TeamEndpoints.ResolveCaller(ctx, boundary, tenants);
    }

    /// <summary>
    /// Null when the request was made with a person's own phone or browser key; otherwise the 403 answer. Internal so
    /// every credential shape is tested.
    /// </summary>
    internal static IResult? RequirePerson(HttpContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (AuthMiddleware.CallingSession(ctx) is { } session)
        {
            FileLog.Write($"[TeamRequestEndpoints] REFUSED: session {session.SessionId} asked a request route - a request never reaches an agent");
            return PersonOnly();
        }

        var device = ctx.Items.TryGetValue(AuthMiddleware.AuthenticatedDeviceItemKey, out var d) ? d as DeviceCredentialIdentity : null;
        if (device is null || SessionOriginSurfaces.FromDeviceType(device.DeviceType) == SessionOriginSurfaces.Unknown)
        {
            var kind = AuthMiddleware.IdentityKind(ctx);
            FileLog.Write($"[TeamRequestEndpoints] REFUSED: a {(device is null ? kind : $"{kind} ({device.DeviceType})")} credential asked a request route - people only");
            return PersonOnly();
        }

        return null;
    }

    private static IResult PersonOnly() =>
        Results.Json(new { error = PersonOnlyRefusal, code = PersonOnlyCode }, statusCode: StatusCodes.Status403Forbidden);

    /// <summary>A send or decision as HTTP: the request as it now stands, or the refusal with its status.</summary>
    internal static IResult Answer(TeamRequestResult result, string what, int successStatus)
    {
        if (result.Outcome == TeamRequestOutcome.Done)
        {
            FileLog.Write($"[TeamRequestEndpoints] {what}: done");
            return Results.Json(new { request = Describe(result.Request!) }, statusCode: successStatus);
        }

        FileLog.Write($"[TeamRequestEndpoints] {what}: {result.Outcome}");
        return Refusal(result.Outcome, result.Refusal!);
    }

    /// <summary>A list as HTTP.</summary>
    internal static IResult AnswerList(TeamRequestListResult result, string what)
    {
        if (result.Outcome != TeamRequestOutcome.Done)
        {
            FileLog.Write($"[TeamRequestEndpoints] list {what}: {result.Outcome}");
            return Refusal(result.Outcome, result.Refusal!);
        }

        FileLog.Write($"[TeamRequestEndpoints] list {what}: {result.Requests.Count} request(s)");
        return Results.Json(new { count = result.Requests.Count, requests = result.Requests.Select(Describe).ToList() });
    }

    private static IResult Refusal(TeamRequestOutcome outcome, string sentence) => outcome switch
    {
        TeamRequestOutcome.NoSuchTeam or TeamRequestOutcome.NoSuchRequest => Results.NotFound(new { error = sentence }),
        TeamRequestOutcome.Refused => Results.Json(new { error = sentence, code = TeamEndpointGate.RefusalCode }, statusCode: StatusCodes.Status403Forbidden),
        TeamRequestOutcome.Invalid => Results.BadRequest(new { error = sentence }),
        TeamRequestOutcome.Conflict => Results.Conflict(new { error = sentence }),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Not a refusal."),
    };

    /// <summary>One request on the wire. Every verdict is finished here; a screen only renders it.</summary>
    internal static object Describe(TeamRequestView r) => new
    {
        id = r.Id,
        text = r.Text,
        state = r.State,
        stateLabel = r.StateLabel,
        sentBy = r.SentBy,
        isYours = r.IsYours,
        sentAtUtc = r.SentAtUtc,
        updatedAtUtc = r.UpdatedAtUtc,
        trail = r.Trail.Select(s => new
        {
            state = s.State,
            label = s.Label,
            by = s.By,
            atUtc = s.AtUtc,
            reason = s.Reason,
            sentence = s.Sentence,
        }).ToList(),
        canAccept = r.CanAccept,
        canDecline = r.CanDecline,
        canMarkDone = r.CanMarkDone,
    };

    private static async Task<(T? Value, IResult? Denial)> ReadBody<T>(HttpContext ctx, string shape) where T : class
    {
        try
        {
            var value = await ctx.Request.ReadFromJsonAsync<T>(ctx.RequestAborted).ConfigureAwait(false);
            return (value, null);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TeamRequestEndpoints] rejected, the request body is not readable JSON ({ex.GetType().Name})");
            return (null, Results.BadRequest(new { error = $"The request body is not readable JSON. Send {shape}." }));
        }
    }

    private static async Task<IResult> Guarded(string route, Func<Task<IResult>> handle)
    {
        try
        {
            return await handle().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TeamRequestEndpoints] {route} FAILED ({ex.GetType().Name}): {ex.Message}");
            return Results.Json(new { error = "DevThrottle could not handle this request just now because of a fault. Try again shortly." },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
