using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Messaging;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// MESSAGE LINKS (issue #3548): the owner lets two sessions that are not owner and worker message each other.
///
///   POST   /fleet/links        set up a link: { senderSessionId, recipientSessionId, amount }
///   GET    /fleet/links        every live link, and every link that stopped in the last 30 days
///   DELETE /fleet/links/{id}   remove a link
///
/// WHO MAY CALL THEM. The owner's own signed-in phone or browser, and a session the owner has RAISED - the Fleet
/// Manager first ("it is a good idea to let the fleet manager allow sessions to talk to each other"). An ordinary
/// session key never reaches them: <see cref="SessionKeyGuard"/> lets a raised key through on
/// <see cref="RaisedGrant.MessageLinks"/> alone, and these handlers check that again. A raised session is refused a
/// link it would itself be part of - it may already message any session, so the only thing such a link could do is
/// outlive the raise. A Director's key and the shared machine token are refused, as they are on raise and lower.
///
/// THE ACCOUNT IS THE CALLER'S. Both sessions are looked for inside the account the caller belongs to, so another
/// account's session answers exactly as an unknown one does.
///
/// EVERY CHANGE IS RECORDED before it is answered (<see cref="FleetMessageLinkRecord"/>), and the sending session is
/// told in its inbox, by a notice from the Gateway, what it may now send.
/// </summary>
internal static class FleetMessageLinkEndpoints
{
    public const string Route = "/fleet/links";
    public const string OneRoute = "/fleet/links/{id}";

    /// <summary>How far back the list shows links that stopped.</summary>
    public const int StoppedWithinDays = 30;

    private const string SessionKeySentence =
        "Only the owner, on their own signed-in phone or browser, or a session the owner has raised, can set up or "
        + "remove a message link. A session never sets up a link for itself.";

    private static readonly JsonSerializerOptions BodyJsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <param name="findSession">The last row any Director of this account reported for one session, live or not; null
    /// when the account has no such session.</param>
    /// <param name="notify">Queues a notice from the Gateway into one session's inbox.</param>
    public static void Map(IEndpointRouteBuilder app, Func<HttpContext, TenantId?> resolveTenant,
        FleetMessageLinkStore links, FleetMessageLinkRecord record, Func<TenantId, string, SessionDto?> findSession,
        Action<TenantId, SessionDto, string> notify, Func<DateTime> nowUtc)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(findSession);
        ArgumentNullException.ThrowIfNull(notify);
        ArgumentNullException.ThrowIfNull(nowUtc);

        // The return type is stated on purpose. An async lambda whose only parameter is HttpContext also fits
        // RequestDelegate, the compiler prefers that overload, and the IResult is then thrown away: every set-up
        // answered an empty 200 OK. Task<IResult> does not fit RequestDelegate, so the result is written.
        app.MapPost(Route, async Task<IResult> (HttpContext ctx) => await CreateAsync(ctx, resolveTenant, links, record, findSession, notify, nowUtc));
        app.MapGet(Route, (HttpContext ctx) => List(ctx, resolveTenant, links, nowUtc));
        app.MapDelete(OneRoute, (HttpContext ctx, string id) => Remove(ctx, id, resolveTenant, links, record, findSession, notify, nowUtc));
        FileLog.Write($"[FleetMessageLinkEndpoints] mapped POST/GET {Route} and DELETE {OneRoute}");
    }

    internal static async Task<IResult> CreateAsync(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant,
        FleetMessageLinkStore links, FleetMessageLinkRecord record, Func<TenantId, string, SessionDto?> findSession,
        Action<TenantId, SessionDto, string> notify, Func<DateTime> nowUtc)
    {
        FileLog.Write("[FleetMessageLinkEndpoints] POST link");
        try
        {
            if (resolveTenant(ctx) is not { } tenant)
                return Refuse(StatusCodes.Status403Forbidden, "no_account", "no account is bound to this request");
            var caller = Caller(ctx, "set up a message link", out var refused);
            if (caller is null) return refused!;

            FleetMessageLinkCreateRequest? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<FleetMessageLinkCreateRequest>(ctx.Request.Body, BodyJsonOptions, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                return Refuse(StatusCodes.Status400BadRequest, "invalid_body", $"The body is not valid JSON: {ex.Message}");
            }
            if (body is null)
                return Refuse(StatusCodes.Status400BadRequest, "invalid_body", "A body is required: { senderSessionId, recipientSessionId, amount }.");

            var amount = (body.Amount ?? "").Trim().ToLowerInvariant();
            if (!FleetMessageLinkAmounts.IsKnown(amount))
                return Refuse(StatusCodes.Status400BadRequest, "invalid_amount",
                    $"amount must be one of {string.Join(", ", FleetMessageLinkAmounts.All)}; '{body.Amount}' was given.");
            if (!Guid.TryParse(body.SenderSessionId, out var s))
                return Refuse(StatusCodes.Status400BadRequest, "invalid_session_id", $"'{body.SenderSessionId}' is not a session id");
            if (!Guid.TryParse(body.RecipientSessionId, out var r))
                return Refuse(StatusCodes.Status400BadRequest, "invalid_session_id", $"'{body.RecipientSessionId}' is not a session id");
            var senderId = s.ToString("D");
            var recipientId = r.ToString("D");
            if (senderId == recipientId)
                return Refuse(StatusCodes.Status400BadRequest, "same_session", "A link joins two different sessions.");

            if (caller.SessionId is { } own && (own == senderId || own == recipientId))
                return Refuse(StatusCodes.Status403Forbidden, "own_link",
                    "A raised session never sets up a link it is part of. It may already message any session of the account.");

            var sender = findSession(tenant, senderId);
            var recipient = findSession(tenant, recipientId);
            foreach (var (id, row) in new[] { (senderId, sender), (recipientId, recipient) })
            {
                if (row is null)
                    return Refuse(StatusCodes.Status404NotFound, "session_not_found",
                        $"No session {id} is known in this account, so no link was set up.");
                if (FleetManagerSessions.IsGone(row))
                    return Refuse(StatusCodes.Status409Conflict, "session_ended",
                        $"Session {id} has ended, so no link was set up. A link ends with either of its sessions.");
            }
            // A session and its own worker may already message each other, so a link between them would never be used,
            // would read "Live" for ever, and would come alive unseen if ownership later changed. Refused, in words.
            if (FleetManagerSessions.SameId(sender!.ControllerSessionId, recipientId)
                || FleetManagerSessions.SameId(recipient!.ControllerSessionId, senderId))
                return Refuse(StatusCodes.Status409Conflict, "already_related",
                    "One of these sessions started the other, so they may already message each other. No link was set up.");

            var setUp = links.SetUp(tenant, senderId, recipientId, amount, caller.Actor, nowUtc());
            foreach (var old in setUp.Replaced)
                record.Stopped(tenant, old, caller.Actor, $"replaced by link {setUp.Link.LinkId}");
            record.SetUp(tenant, setUp.Link, caller.Actor);

            notify(tenant, sender!, SenderNotice(setUp.Link, recipient!));
            if (amount == FleetMessageLinkAmounts.Ongoing)
                notify(tenant, recipient!, RecipientNotice(setUp.Link, sender!));

            FileLog.Write($"[FleetMessageLinkEndpoints] POST link: link={setUp.Link.LinkId}, from={senderId}, to={recipientId}, amount={amount}, by={caller.Actor}");
            return Results.Json(new FleetMessageLinkCreateResponse
            {
                Link = ToDto(setUp.Link),
                Replaced = setUp.Replaced.Select(ToDto).ToList(),
            }, statusCode: StatusCodes.Status201Created);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetMessageLinkEndpoints] POST link FAILED: {ex.Message}");
            throw;
        }
    }

    internal static IResult List(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant, FleetMessageLinkStore links,
        Func<DateTime> nowUtc)
    {
        FileLog.Write("[FleetMessageLinkEndpoints] GET links");
        try
        {
            if (resolveTenant(ctx) is not { } tenant)
                return Refuse(StatusCodes.Status403Forbidden, "no_account", "no account is bound to this request");
            var caller = Caller(ctx, "list the message links", out var refused);
            if (caller is null) return refused!;

            var rows = links.List(tenant, nowUtc() - TimeSpan.FromDays(StoppedWithinDays));
            return Results.Json(new FleetMessageLinkListResponse
            {
                Links = rows.Select(ToDto).ToList(),
                StoppedWithinDays = StoppedWithinDays,
            });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetMessageLinkEndpoints] GET links FAILED: {ex.Message}");
            throw;
        }
    }

    internal static IResult Remove(HttpContext ctx, string id, Func<HttpContext, TenantId?> resolveTenant,
        FleetMessageLinkStore links, FleetMessageLinkRecord record, Func<TenantId, string, SessionDto?> findSession,
        Action<TenantId, SessionDto, string> notify, Func<DateTime> nowUtc)
    {
        FileLog.Write($"[FleetMessageLinkEndpoints] DELETE link: id={id}");
        try
        {
            if (resolveTenant(ctx) is not { } tenant)
                return Refuse(StatusCodes.Status403Forbidden, "no_account", "no account is bound to this request");
            var caller = Caller(ctx, "remove a message link", out var refused);
            if (caller is null) return refused!;

            var before = links.Find(tenant, id);
            if (before is null)
                return Refuse(StatusCodes.Status404NotFound, "link_not_found", $"No message link {id} is known in this account.");
            if (caller.SessionId is { } own && (own == before.SenderSessionId || own == before.RecipientSessionId))
                return Refuse(StatusCodes.Status403Forbidden, "own_link",
                    "A raised session never changes a link it is part of.");

            var result = links.Remove(tenant, before.LinkId, caller.Actor, nowUtc())!;
            var after = result.Link;
            // Recorded and told only when THIS call removed it: a link a message used up a moment before was not removed.
            if (result.Removed)
            {
                record.Stopped(tenant, after, caller.Actor, "removed");
                if (findSession(tenant, after.SenderSessionId) is { } sender && !FleetManagerSessions.IsGone(sender))
                    notify(tenant, sender,
                        $"The message link to session {after.RecipientSessionId} was removed. You may no longer message it "
                        + "unless the user sets up a new link.");
            }
            FileLog.Write($"[FleetMessageLinkEndpoints] DELETE link: link={after.LinkId}, status={after.Status}, by={caller.Actor}");
            return Results.Json(ToDto(after));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetMessageLinkEndpoints] DELETE link FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>Who is calling, in the words the record holds.</summary>
    /// <param name="Actor">The audit actor: the owner's device, or <c>session {id}</c> for a raised session.</param>
    /// <param name="SessionId">The raised session's id, or null when the owner's device is calling.</param>
    internal sealed record LinkCaller(string Actor, string? SessionId);

    /// <summary>
    /// The owner's device, or a raised session let through on <see cref="RaisedGrant.MessageLinks"/>; otherwise null
    /// and <paramref name="refusal"/> is the 403 answer. A second line behind <see cref="SessionKeyGuard"/>, which
    /// refuses an ordinary session key before this runs.
    /// </summary>
    internal static LinkCaller? Caller(HttpContext ctx, string what, out IResult? refusal)
    {
        if (AuthMiddleware.CallingSession(ctx) is { } session)
        {
            if (AuthMiddleware.RaisedGrantOf(ctx) == RaisedGrant.MessageLinks)
            {
                refusal = null;
                var sid = session.SessionId.ToString("D");
                return new LinkCaller($"session {sid}", sid);
            }
            FileLog.Write($"[FleetMessageLinkEndpoints] REFUSED: session {session.SessionId} asked to {what}");
            refusal = Refuse(StatusCodes.Status403Forbidden, "owner_only", SessionKeySentence);
            return null;
        }

        var device = FleetManagerOwnerDevice.Require(ctx, what, SessionKeySentence, nameof(FleetMessageLinkEndpoints), out refusal);
        if (device is null) return null;
        return new LinkCaller(SessionStopFold.ActorFor(null, device.DeviceType, device.DeviceId, credentialAuthenticated: false), null);
    }

    /// <summary>What the sending session is told when a link is set up for it.</summary>
    internal static string SenderNotice(FleetMessageLink link, SessionDto recipient)
    {
        var who = Describe(link.RecipientSessionId, recipient);
        return link.Amount switch
        {
            FleetMessageLinkAmounts.Once =>
                $"The user set up a message link: you may send {who} ONE message, with no reply. Send it with: "
                + $"cc-devthrottle message send {link.RecipientSessionId} \"...\"",
            FleetMessageLinkAmounts.OnceWithReply =>
                $"The user set up a message link: you may send {who} ONE message, and it may reply once. Send it with: "
                + $"cc-devthrottle message send {link.RecipientSessionId} \"...\" --reply-wanted",
            _ =>
                $"The user set up a message link: you and {who} may message each other as much as you need, until the "
                + $"link is removed or either session ends. Send with: cc-devthrottle message send {link.RecipientSessionId} \"...\"",
        };
    }

    /// <summary>What the receiving session is told when an ongoing link is set up - it may send back.</summary>
    internal static string RecipientNotice(FleetMessageLink link, SessionDto sender)
        => $"The user set up a message link: you and {Describe(link.SenderSessionId, sender)} may message each other as "
           + "much as you need, until the link is removed or either session ends. Send with: "
           + $"cc-devthrottle message send {link.SenderSessionId} \"...\"";

    private static string Describe(string sessionId, SessionDto row)
        => string.IsNullOrWhiteSpace(row.Name) ? $"session {sessionId}" : $"session {sessionId} (\"{row.Name}\")";

    /// <summary>The one sentence a client shows for a link, folded here so no client decides what a status means.</summary>
    internal static string Summary(FleetMessageLink link)
    {
        var what = link.Amount switch
        {
            FleetMessageLinkAmounts.Once => "One message, no reply",
            FleetMessageLinkAmounts.OnceWithReply => "One message and a reply",
            _ => "Talk as much as needed, both ways",
        };
        return link.Status switch
        {
            FleetMessageLinkStatuses.Live => $"{what}. Live.",
            FleetMessageLinkStatuses.Used => $"{what}. Used.",
            FleetMessageLinkStatuses.Removed => $"{what}. Removed.",
            _ => $"{what}. Ended with its session.",
        };
    }

    internal static FleetMessageLinkDto ToDto(FleetMessageLink link) => new()
    {
        LinkId = link.LinkId,
        SenderSessionId = link.SenderSessionId,
        RecipientSessionId = link.RecipientSessionId,
        Amount = link.Amount,
        Status = link.Status,
        Summary = Summary(link),
        SetUpBy = link.SetUpBy,
        SetUpAtUtc = link.SetUpAtUtc,
        UsedMessageId = link.UsedMessageId,
        UsedAtUtc = link.UsedAtUtc,
        EndedBy = link.EndedBy,
        EndedAtUtc = link.EndedAtUtc,
    };

    private static IResult Refuse(int status, string code, string sentence)
        => Results.Json(new { code, error = sentence }, statusCode: status);
}
