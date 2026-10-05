using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Messaging;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// REQUESTS FOR A MESSAGE LINK (issue #3548). A session may message only its own owner and the sessions it started; a
/// link from the owner lets it talk to another. These routes let the session ASK for one when it needs it, instead of
/// the owner having to think of every pair in advance:
///
/// <list type="bullet">
/// <item><c>POST /fleet/link-requests</c> - the calling session asks, with a reason. Only a session asks, and only for
/// itself; the request allows nothing.</item>
/// <item><c>GET /fleet/link-requests</c> - the owner's list: what waits, and what was answered lately.</item>
/// <item><c>POST /fleet/link-requests/{id}/answer</c> - the owner allows it, with an amount, or says no. Allowing sets up
/// an ordinary link through <see cref="FleetMessageLinkEndpoints.CheckPair"/> and
/// <see cref="FleetMessageLinkEndpoints.Apply"/>, the same checks and the same notices as a link set up unasked.</item>
/// </list>
///
/// The list and the answer admit exactly who the link routes admit: the owner's own device, or a raised session on
/// <see cref="RaisedGrant.MessageLinks"/>. The asking session hears every outcome in its inbox.
/// </summary>
public static class FleetMessageLinkRequestEndpoints
{
    public const string Route = "/fleet/link-requests";
    public const string AnswerRoute = "/fleet/link-requests/{id}/answer";

    /// <summary>How far back answered requests are still listed.</summary>
    public const int AnsweredWithinDays = 7;

    public static void Map(IEndpointRouteBuilder app, Func<HttpContext, TenantId?> resolveTenant,
        FleetMessageLinkRequestStore requests, FleetMessageLinkStore links, FleetMessageLinkRecord record,
        Func<TenantId, string, SessionDto?> findSession, Action<TenantId, SessionDto, string> notify, Func<DateTime> nowUtc)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(findSession);
        ArgumentNullException.ThrowIfNull(notify);
        ArgumentNullException.ThrowIfNull(nowUtc);

        // The return types are stated on purpose: an async lambda whose only parameter is HttpContext also fits
        // RequestDelegate, which would throw the IResult away and answer an empty 200 (found in #3549).
        app.MapPost(Route, async Task<IResult> (HttpContext ctx) =>
            await AskAsync(ctx, resolveTenant, requests, links, record, findSession, nowUtc));
        app.MapGet(Route, (HttpContext ctx) => List(ctx, resolveTenant, requests, record, findSession, nowUtc));
        app.MapPost(AnswerRoute, async Task<IResult> (HttpContext ctx, string id) =>
            await AnswerAsync(ctx, id, resolveTenant, requests, links, record, findSession, notify, nowUtc));
        FileLog.Write($"[FleetMessageLinkRequestEndpoints] mapped POST/GET {Route} and POST {AnswerRoute}");
    }

    internal static async Task<IResult> AskAsync(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant,
        FleetMessageLinkRequestStore requests, FleetMessageLinkStore links, FleetMessageLinkRecord record,
        Func<TenantId, string, SessionDto?> findSession, Func<DateTime> nowUtc)
    {
        FileLog.Write("[FleetMessageLinkRequestEndpoints] POST request");
        try
        {
            if (resolveTenant(ctx) is not { } tenant)
                return Refuse(StatusCodes.Status403Forbidden, "no_account", "no account is bound to this request");
            if (AuthMiddleware.CallingSession(ctx) is not { } session)
                return Refuse(StatusCodes.Status403Forbidden, "session_only",
                    "Only a session asks for a message link, for itself. The owner sets one up directly instead.");
            var requesterId = session.SessionId.ToString("D");

            FleetMessageLinkRequestAskRequest? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<FleetMessageLinkRequestAskRequest>(ctx.Request.Body,
                    FleetMessageLinkEndpoints.BodyJsonOptions, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                return Refuse(StatusCodes.Status400BadRequest, "invalid_body", $"The body is not valid JSON: {ex.Message}");
            }
            if (body is null)
                return Refuse(StatusCodes.Status400BadRequest, "invalid_body", "A body is required: { targetSessionId, reason }.");
            if (!Guid.TryParse(body.TargetSessionId, out var t))
                return Refuse(StatusCodes.Status400BadRequest, "invalid_session_id", $"'{body.TargetSessionId}' is not a session id");
            var targetId = t.ToString("D");
            if (targetId == requesterId)
                return Refuse(StatusCodes.Status400BadRequest, "same_session", "A session does not ask to talk to itself.");
            if (string.IsNullOrWhiteSpace(body.Reason))
                return Refuse(StatusCodes.Status400BadRequest, "reason_required",
                    "Say why you need to talk to that session, in one sentence the user can decide on.");

            var target = findSession(tenant, targetId);
            if (target is null)
                return Refuse(StatusCodes.Status404NotFound, "session_not_found", $"No session {targetId} is known in this account.");
            if (FleetManagerSessions.IsGone(target))
                return Refuse(StatusCodes.Status409Conflict, "session_ended", $"Session {targetId} has ended, so there is nobody to talk to.");
            var self = findSession(tenant, requesterId);
            if (FleetManagerSessions.SameId(self?.ControllerSessionId, targetId)
                || FleetManagerSessions.SameId(target.ControllerSessionId, requesterId))
                return Refuse(StatusCodes.Status409Conflict, "already_related",
                    "One of you started the other, so you may already message it. Send the message instead of asking.");
            if (links.FindLive(tenant, requesterId, targetId) is not null)
                return Refuse(StatusCodes.Status409Conflict, "already_linked",
                    "A message link already lets you message that session. Send the message instead of asking.");

            var asked = requests.Ask(tenant, requesterId, targetId, body.Reason, nowUtc());
            if (asked.Refused)
                return Refuse(StatusCodes.Status429TooManyRequests, "too_many_requests",
                    $"You already have {FleetMessageLinkRequestStore.MaxPendingPerSession} requests waiting for the user. "
                    + "Nothing was asked. Wait for an answer, and put what you needed in your report.");
            var request = asked.Request!;
            if (asked.Created) record.Requested(tenant, request);

            FileLog.Write($"[FleetMessageLinkRequestEndpoints] POST request: request={request.RequestId}, from={requesterId}, to={targetId}, created={asked.Created}");
            return Results.Json(new FleetMessageLinkRequestAskResponse
            {
                Request = ToDto(request),
                Created = asked.Created,
                Note = asked.Created
                    ? "Asked. The user decides, and the answer arrives in your inbox. Carry on with your work meanwhile: do "
                      + "not wait for it, and do not ask again."
                    : "You already asked for this, and it is still waiting for the user. Nothing new was asked.",
            }, statusCode: asked.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetMessageLinkRequestEndpoints] POST request FAILED: {ex.Message}");
            throw;
        }
    }

    internal static IResult List(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant,
        FleetMessageLinkRequestStore requests, FleetMessageLinkRecord record, Func<TenantId, string, SessionDto?> findSession,
        Func<DateTime> nowUtc)
    {
        FileLog.Write("[FleetMessageLinkRequestEndpoints] GET requests");
        try
        {
            if (resolveTenant(ctx) is not { } tenant)
                return Refuse(StatusCodes.Status403Forbidden, "no_account", "no account is bound to this request");
            var caller = FleetMessageLinkEndpoints.Caller(ctx, "list the requests for a message link", out var refused);
            if (caller is null) return refused!;

            // A request whose session has ended can never be answered; it ends here, recorded, rather than waiting on
            // the owner's list for ever.
            var now = nowUtc();
            foreach (var waiting in requests.List(tenant, now).Where(r => r.Status == FleetMessageLinkRequestStatuses.Pending))
            {
                var gone = new[] { waiting.RequesterSessionId, waiting.TargetSessionId }
                    .FirstOrDefault(id => findSession(tenant, id) is not { } row || FleetManagerSessions.IsGone(row));
                if (gone is null) continue;
                var by = $"gateway: session {gone} ended";
                if (requests.TryAnswer(tenant, waiting.RequestId, FleetMessageLinkRequestStatuses.Ended, by, now))
                    record.RequestAnswered(tenant, waiting, by, "ended, because a session ended before it was answered");
            }

            var rows = requests.List(tenant, now - TimeSpan.FromDays(AnsweredWithinDays));
            return Results.Json(new FleetMessageLinkRequestListResponse
            {
                Requests = rows.Select(ToDto).ToList(),
                AnsweredWithinDays = AnsweredWithinDays,
            });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetMessageLinkRequestEndpoints] GET requests FAILED: {ex.Message}");
            throw;
        }
    }

    internal static async Task<IResult> AnswerAsync(HttpContext ctx, string id, Func<HttpContext, TenantId?> resolveTenant,
        FleetMessageLinkRequestStore requests, FleetMessageLinkStore links, FleetMessageLinkRecord record,
        Func<TenantId, string, SessionDto?> findSession, Action<TenantId, SessionDto, string> notify, Func<DateTime> nowUtc)
    {
        FileLog.Write($"[FleetMessageLinkRequestEndpoints] POST answer: id={id}");
        try
        {
            if (resolveTenant(ctx) is not { } tenant)
                return Refuse(StatusCodes.Status403Forbidden, "no_account", "no account is bound to this request");
            var caller = FleetMessageLinkEndpoints.Caller(ctx, "answer a request for a message link", out var refused);
            if (caller is null) return refused!;

            FleetMessageLinkRequestAnswerRequest? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<FleetMessageLinkRequestAnswerRequest>(ctx.Request.Body,
                    FleetMessageLinkEndpoints.BodyJsonOptions, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                return Refuse(StatusCodes.Status400BadRequest, "invalid_body", $"The body is not valid JSON: {ex.Message}");
            }
            if (body is null)
                return Refuse(StatusCodes.Status400BadRequest, "invalid_body", "A body is required: { amount } to allow, or { decline: true }.");

            var amount = (body.Amount ?? "").Trim().ToLowerInvariant();
            if (body.Decline == (amount.Length > 0))
                return Refuse(StatusCodes.Status400BadRequest, "invalid_answer",
                    "Answer with an amount to allow it, or with decline: true to say no - one of the two.");
            if (!body.Decline && !FleetMessageLinkAmounts.IsKnown(amount))
                return Refuse(StatusCodes.Status400BadRequest, "invalid_amount",
                    $"amount must be one of {string.Join(", ", FleetMessageLinkAmounts.All)}; '{body.Amount}' was given.");

            var request = requests.Find(tenant, id);
            if (request is null)
                return Refuse(StatusCodes.Status404NotFound, "request_not_found", $"No request {id} is known in this account.");
            if (request.Status != FleetMessageLinkRequestStatuses.Pending)
                return AlreadyAnswered(request);

            var now = nowUtc();
            if (body.Decline)
            {
                if (!requests.TryAnswer(tenant, request.RequestId, FleetMessageLinkRequestStatuses.Declined, caller.Actor, now))
                    return AlreadyAnswered(requests.Find(tenant, request.RequestId)!);
                record.RequestAnswered(tenant, request, caller.Actor, "declined");
                if (findSession(tenant, request.RequesterSessionId) is { } requester && !FleetManagerSessions.IsGone(requester))
                {
                    var target = findSession(tenant, request.TargetSessionId);
                    var who = target is null ? $"session {request.TargetSessionId}"
                        : FleetMessageLinkEndpoints.Describe(request.TargetSessionId, target);
                    notify(tenant, requester,
                        $"The user said no to your request to talk to {who}. Do not ask again for this; put what you "
                        + "needed in your report instead.");
                }
                FileLog.Write($"[FleetMessageLinkRequestEndpoints] POST answer: request={request.RequestId} declined by {caller.Actor}");
                return Results.Json(new FleetMessageLinkRequestAnswerResponse
                {
                    Request = ToDto(requests.Find(tenant, request.RequestId)!),
                });
            }

            // ALLOW: the same rule as a link set up unasked. A pair that fails it is not set up; when a session has
            // ended, the request ends with it, so it leaves the owner's list.
            if (FleetMessageLinkEndpoints.CheckPair(tenant, caller, request.RequesterSessionId, request.TargetSessionId,
                    findSession, out var sender, out var recipient) is { } refusal)
            {
                if (findSession(tenant, request.RequesterSessionId) is not { } r1 || FleetManagerSessions.IsGone(r1)
                    || findSession(tenant, request.TargetSessionId) is not { } r2 || FleetManagerSessions.IsGone(r2))
                {
                    var by = "gateway: a session ended";
                    if (requests.TryAnswer(tenant, request.RequestId, FleetMessageLinkRequestStatuses.Ended, by, now))
                        record.RequestAnswered(tenant, request, by, "ended, because a session ended before it was answered");
                }
                return refusal;
            }

            // The request is claimed BEFORE the link is set up, so of two answers at the same moment only one sets one up.
            if (!requests.TryAnswer(tenant, request.RequestId, FleetMessageLinkRequestStatuses.Allowed, caller.Actor, now, amount))
                return AlreadyAnswered(requests.Find(tenant, request.RequestId)!);
            var setUp = FleetMessageLinkEndpoints.Apply(tenant, caller, request.RequesterSessionId, request.TargetSessionId,
                amount, sender, recipient, links, record, notify, now);
            requests.NoteLink(tenant, request.RequestId, setUp.Link.LinkId);
            record.RequestAnswered(tenant, request, caller.Actor, $"allowed, {amount}, as message link {setUp.Link.LinkId}");

            FileLog.Write($"[FleetMessageLinkRequestEndpoints] POST answer: request={request.RequestId} allowed ({amount}) as link {setUp.Link.LinkId} by {caller.Actor}");
            return Results.Json(new FleetMessageLinkRequestAnswerResponse
            {
                Request = ToDto(requests.Find(tenant, request.RequestId)!),
                Link = FleetMessageLinkEndpoints.ToDto(setUp.Link),
            });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetMessageLinkRequestEndpoints] POST answer FAILED: {ex.Message}");
            throw;
        }
    }

    private static IResult AlreadyAnswered(FleetMessageLinkRequest request)
        => Refuse(StatusCodes.Status409Conflict, "already_answered", request.Status switch
        {
            FleetMessageLinkRequestStatuses.Allowed => "This request was already allowed. Nothing changed.",
            FleetMessageLinkRequestStatuses.Declined => "This request was already declined. Nothing changed.",
            _ => "This request ended because a session ended. Nothing changed.",
        });

    internal static FleetMessageLinkRequestDto ToDto(FleetMessageLinkRequest r) => new()
    {
        RequestId = r.RequestId,
        RequesterSessionId = r.RequesterSessionId,
        TargetSessionId = r.TargetSessionId,
        Reason = r.Reason,
        Status = r.Status,
        AskedAtUtc = r.AskedAtUtc,
        AnsweredBy = r.AnsweredBy,
        AnsweredAtUtc = r.AnsweredAtUtc,
        Amount = r.Amount,
        LinkId = r.LinkId,
    };

    private static IResult Refuse(int status, string code, string sentence)
        => FleetMessageLinkEndpoints.Refuse(status, code, sentence);
}
