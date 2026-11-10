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
/// <item><c>POST /teams/{teamId}/requests/{requestId}/forward</c> with <c>{"factory": "...", "note": "..."}</c> - the
/// Owner's "Forward to the factory" (Team implementation piece 4.3): the words, quoted and marked as the sender's, are
/// left in one of THIS team's factory inboxes in the Owner's name (<see cref="TeamRequestForwarding"/>). The Owner alone;
/// the note is optional.</item>
/// </list>
///
/// THE OWNER'S INBOX. What a contributor writes lands here and only here: the list the Owner's inbox shows is
/// <c>GET /teams/{teamId}/requests</c>, labelled with the team and the sender, and it carries the factories the Owner may
/// forward to. Nothing on this page is read by a session, a Director, a factory or a Boss; the forward is the one way out.
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
    public const string PersonOnlyCode = PersonOnlyCaller.Code;

    /// <summary>What every caller that is not a person's own phone or browser is told.</summary>
    internal const string PersonOnlyRefusal =
        "Requests are written and read by people, from their own signed-in phone or browser. They are never shown to an " +
        "agent or a Director, so this request was refused. Nothing was done.";

    internal sealed record SendRequest(string? Text);

    internal sealed record DecideRequest(string? Reason);

    internal sealed record ForwardRequest(string? Factory, string? Note);

    /// <summary>The words of the forward control on the Owner's inbox item. All the Gateway's (rule 7).</summary>
    internal const string ForwardLabel = "Forward to the factory";
    internal const string ForwardNoteLabel = "Your note above their words (optional)";
    internal const string ForwardNoteHint =
        "The factory gets their words quoted and marked as theirs - who wrote them, on which team, and when - in your name.";
    internal const string NotSeenByAgents = "Not seen by any agent until you forward it.";
    /// <summary>Maps the six routes.</summary>
    public static void Map(IEndpointRouteBuilder app, TeamRequestStore requests, TeamRequestForwarding forwarding, TeamRegistry teams,
        HostedTenantBoundary boundary, TenantRegistry tenants)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(forwarding);
        ArgumentNullException.ThrowIfNull(teams);
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
            return Task.FromResult(caller.Denial ?? AnswerTeamList(requests.ListForTeam(teamId, caller.Subject!), teamId, caller.Subject!, forwarding, teams));
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

        app.MapPost(root + "/{requestId}/forward", (HttpContext ctx, string teamId, string requestId) => Guarded("POST requests/forward", async () =>
        {
            var caller = Caller(ctx, boundary, tenants);
            if (caller.Denial is not null) return caller.Denial;
            var body = await ReadBody<ForwardRequest>(ctx, "{\"factory\": \"<the factory>\", \"note\": \"<optional note>\"}").ConfigureAwait(false);
            if (body.Denial is not null) return body.Denial;
            var result = await forwarding.ForwardAsync(teamId, requestId, caller.Subject!, OwnerDevice(ctx), body.Value?.Factory, body.Value?.Note,
                ctx.RequestAborted).ConfigureAwait(false);
            return Answer(result, "forward", StatusCodes.Status200OK, ForwardTitles(forwarding, teamId, caller.Subject!));
        }));

        FileLog.Write($"[TeamRequestEndpoints] mapped {root} (POST, GET), {root}/mine, {root}/{{requestId}}/accept|decline|done|forward");
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
    /// Null when the request was made with a person's own phone or browser key; otherwise the 403 answer. The rule is the
    /// one person-only rule (<see cref="PersonOnlyCaller.Require"/>); only the sentence is the Requests routes'.
    /// </summary>
    internal static IResult? RequirePerson(HttpContext ctx) =>
        PersonOnlyCaller.Require(ctx, "a request route", PersonOnlyRefusal);

    /// <summary>The Owner's own phone or Cockpit, as the factory inbox records a sender. The caller has already passed
    /// <see cref="RequirePerson"/>, so the key is a person's phone or browser key.</summary>
    internal static TeamForwardDevice OwnerDevice(HttpContext ctx)
    {
        var device = AuthMiddleware.AuthenticatedDevice(ctx)
            ?? throw new InvalidOperationException("A forward ran for a caller with no device key, after the person-only check let it in.");
        var surface = SessionOriginSurfaces.FromDeviceType(device.DeviceType) switch
        {
            SessionOriginSurfaces.Phone => Factory.Inbox.FactoryInboxWords.Surfaces.Phone,
            SessionOriginSurfaces.Cockpit => Factory.Inbox.FactoryInboxWords.Surfaces.Cockpit,
            var other => throw new InvalidOperationException($"A forward ran from a {other} key, after the person-only check let it in."),
        };
        return new TeamForwardDevice(surface, device.DeviceId);
    }

    /// <summary>The titles of the factories the caller may forward to, by name - what a forwarded step shows the Owner.</summary>
    private static IReadOnlyDictionary<string, string> ForwardTitles(TeamRequestForwarding forwarding, string teamId, string caller) =>
        forwarding.Targets(teamId, caller).Factories.ToDictionary(f => f.Factory, f => f.Name, StringComparer.Ordinal);

    /// <summary>
    /// The team's whole list as HTTP, for the Owner's inbox and the Owner and Managers' Requests page: every request with
    /// the team's label, who sent it, and - for the Owner - the factories to forward to and the words of the control.
    /// </summary>
    internal static IResult AnswerTeamList(TeamRequestListResult result, string teamId, string caller, TeamRequestForwarding forwarding, TeamRegistry teams)
    {
        if (result.Outcome != TeamRequestOutcome.Done)
        {
            FileLog.Write($"[TeamRequestEndpoints] list team: {result.Outcome}");
            return Refusal(result.Outcome, result.Refusal!);
        }

        var teamLabel = teams.ListTeamsFor(caller).First(t => string.Equals(t.TeamId, teamId, StringComparison.Ordinal)).Name;
        var targets = forwarding.Targets(teamId, caller);
        var titles = targets.Factories.ToDictionary(f => f.Factory, f => f.Name, StringComparer.Ordinal);
        FileLog.Write($"[TeamRequestEndpoints] list team: {result.Requests.Count} request(s), {targets.Factories.Count} factory target(s)");
        return Results.Json(new
        {
            teamId,
            teamLabel,
            count = result.Requests.Count,
            requests = result.Requests.Select(r => Describe(r, titles)).ToList(),
            forward = new
            {
                label = ForwardLabel,
                noteLabel = ForwardNoteLabel,
                hint = ForwardNoteHint,
                factories = targets.Factories.Select(f => new { factory = f.Factory, name = f.Name }).ToList(),
                emptyText = targets.EmptyText,
            },
        });
    }

    /// <summary>A send or decision as HTTP: the request as it now stands, or the refusal with its status.</summary>
    internal static IResult Answer(TeamRequestResult result, string what, int successStatus, IReadOnlyDictionary<string, string>? titles = null)
    {
        if (result.Outcome == TeamRequestOutcome.Done)
        {
            FileLog.Write($"[TeamRequestEndpoints] {what}: done");
            return Results.Json(new { request = Describe(result.Request!, titles) }, statusCode: successStatus);
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
        return Results.Json(new { count = result.Requests.Count, requests = result.Requests.Select(r => Describe(r)).ToList() });
    }

    private static IResult Refusal(TeamRequestOutcome outcome, string sentence) => outcome switch
    {
        TeamRequestOutcome.NoSuchTeam or TeamRequestOutcome.NoSuchRequest => Results.NotFound(new { error = sentence }),
        TeamRequestOutcome.Refused => Results.Json(new { error = sentence, code = TeamEndpointGate.RefusalCode }, statusCode: StatusCodes.Status403Forbidden),
        TeamRequestOutcome.Invalid => Results.BadRequest(new { error = sentence }),
        TeamRequestOutcome.Conflict => Results.Conflict(new { error = sentence }),
        TeamRequestOutcome.Unavailable => Results.Json(new { error = sentence }, statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Not a refusal."),
    };

    /// <summary>One request on the wire. Every verdict is finished here; a screen only renders it. <paramref name="titles"/>
    /// names the factories the reader may forward to; a forwarded step shows its factory's title only to that reader.</summary>
    internal static object Describe(TeamRequestView r, IReadOnlyDictionary<string, string>? titles = null) => new
    {
        id = r.Id,
        text = r.Text,
        state = r.State,
        stateLabel = r.StateLabel,
        sentBy = r.SentBy,
        fromLabel = FromLabel(r),
        privacyNote = r.CanForward ? NotSeenByAgents : null,
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
            forwardedTo = s.ForwardedTo is null ? null : titles?.GetValueOrDefault(s.ForwardedTo) ?? s.ForwardedTo,
        }).ToList(),
        canAccept = r.CanAccept,
        canDecline = r.CanDecline,
        canMarkDone = r.CanMarkDone,
        canForward = r.CanForward,
    };

    /// <summary>Who wrote it, as a list row says it: "From you", or the sender and - when they are a contributor - that
    /// they are one, because a contributor's words are the ones that may never reach an agent unread.</summary>
    internal static string FromLabel(TeamRequestView r) =>
        r.IsYours ? "From you"
        : r.SenderRole == TeamRole.Collaborator ? $"From {r.SentBy}, a contributor"
        : $"From {r.SentBy}";

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
