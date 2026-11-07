using System.Text.Json;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.DevReports;
using CcDirector.Gateway.Factory.Inbox;
using CcDirector.Gateway.Inbox;
using CcDirector.Gateway.Messaging;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>Where a session ran, from the Gateway's record of it: what a new session started in its place needs.</summary>
internal sealed record YourInboxSessionPlace(string? DirectorId, string? RepoPath);

/// <summary>
/// THE YOUR INBOX ROUTES (Factory Inbox mission, phase 3).
///
/// The session route - any session, with its own key, as itself:
///
///   POST /sessions/{sid}/inbox                  body {subject, body, reportId?} -> 201 {message}
///                                               what <c>cc-devthrottle inbox send</c> calls
///
/// The owner's routes - the owner's own phone or Cockpit, and nobody else:
///
///   GET  /gateway/inbox?view=unread|inbox|archived&amp;limit=   one view, newest first, with the unread count, the
///                                               filters' words and counts, and the view's empty sentence
///   GET  /gateway/inbox/count                       the unread count for the Inbox entry
///   GET  /gateway/inbox/{id}                        one message, with the replies sent from it
///   POST /gateway/inbox/{id}/state                  body {state: unread|read|archived} -> {message}
///   POST /gateway/inbox/{id}/reply                  body {text, to?} -> 201 {reply, message}, or 409 with offers
///
/// WHO MAY. The inbox is the owner's mailbox, so only his device keys read it, mark it and reply from it. A session key
/// reaching an owner route is refused by <see cref="SessionKeyGuard"/>, which names only the send; and refused HERE as
/// well, so a later edit to the guard cannot open the owner's mail to an agent. The machine token is refused too: it is
/// a machine, not the owner. The send is a session's, and the sender is the session whose key made the call - named in
/// the path and checked against the key - with who it is (number, name, factory, seat) read from the Gateway's own
/// records, never from the body.
///
/// REPLY ROUTING (the owner's decision, mission section 4). A reply to a session's message goes into that session's
/// inbox if it is still running - as a notice from the Gateway, read when it is free, never typed into it mid-turn. If
/// it has ended the reply is NOT sent: the route answers 409 with the sentence and the offers - a new session with the
/// reply as its first prompt, and, when the session was a factory's seat, that factory's Factory inbox. Taking the second
/// offer is the same call with <c>to: "factory"</c>. A reply to a factory's message goes into that factory's Factory
/// inbox, as a typed message from the owner's phone or Cockpit, exactly as if he had left it there himself.
///
/// NOTHING IS DELETED: there is no delete route.
/// </summary>
internal static class YourInboxEndpoints
{
    public const string OwnerPrefix = "/gateway/inbox";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>What the routes read. Every function is required, so a wiring line that forgot one fails to compile.</summary>
    /// <param name="Factories">The account's registered factories, for their titles.</param>
    /// <param name="Naming">A session's number and name, from the live roster or its history row.</param>
    /// <param name="FactorySeatOf">The factory and seat a session was started as, or null for a session in no factory.</param>
    /// <param name="ReportTitle">The title of one of the account's dev reports, or null when the account has no such report.</param>
    /// <param name="Liveness">Whether a session is running, waiting, or has ended.</param>
    /// <param name="PlaceOf">Where a session ran - its Director and its folder - or null when the Gateway has no record.</param>
    /// <param name="DeliverToSession">Write a notice from the Gateway into a session's inbox; its doorbell rings it when it
    /// is free.</param>
    /// <param name="SendToFactory">Leave the owner's message in a factory's Factory inbox (<c>FactoryInboxStore.SendAsync</c>).</param>
    /// <param name="MessageSent">Tells the factory inbox wake a message was left (<c>FactoryInboxWake.MessageSent</c>), exactly
    /// as the owner's own send to a factory does, so a reply from Your inbox is looked at when its gather window closes and
    /// not only at the next heartbeat.</param>
    internal sealed record Parts(
        YourInboxStore Store,
        Func<HttpContext, TenantId?> ResolveTenant,
        Func<TenantId, IReadOnlyList<RegisteredFactoryDto>> Factories,
        Func<TenantId, string, DevReportSessionNaming> Naming,
        Func<TenantId, string, DevReportFactorySeatIdentity?> FactorySeatOf,
        Func<TenantId, Guid, string?> ReportTitle,
        Func<TenantId, string, DevReportSessionLiveness> Liveness,
        Func<TenantId, string, YourInboxSessionPlace?> PlaceOf,
        Func<TenantId, string, string, FleetSendOutcome> DeliverToSession,
        Func<TenantId, FactoryInboxSendInput, CancellationToken, Task<FactoryInboxMessageEntity>> SendToFactory,
        Action<TenantId, string, DateTime> MessageSent,
        Func<DateTime> NowUtc);

    public static void Map(IEndpointRouteBuilder app, Parts parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        FileLog.Write("[YourInboxEndpoints] mapping the Your inbox routes (one session route, five owner routes)");

        // ---------------------------------------------------------------- the session route

        app.MapPost("/sessions/{sid}/inbox", async (string sid, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[YourInboxEndpoints] POST /sessions/{sid}/inbox: identity={AuthMiddleware.IdentityKind(ctx)}");
            if (parts.ResolveTenant(ctx) is not { } tenant) return NoTenant();
            var session = AuthMiddleware.CallingSession(ctx);
            if (session is null)
                return Error(403, "session_key_required", "Only a session sends a message to the owner's inbox, with its own session key.");
            if (!Guid.TryParse(sid, out var named) || named != session.SessionId)
            {
                FileLog.Write($"[YourInboxEndpoints] send REFUSED sid={sid}: caller session {session.SessionId:D}");
                return Error(403, "not_your_session", $"A session sends to the owner's inbox only as itself, and {sid} is not this session.");
            }
            var sessionId = session.SessionId.ToString("D");

            var (body, bad) = await ReadAsync<SendBody>(ctx, ct);
            if (bad is not null) return bad;
            if (body is null) return Error(400, "bad_request_body", "The body must be an object with a \"subject\" and a \"body\".");

            Guid? reportId = null;
            string? reportTitle = null;
            if (!string.IsNullOrWhiteSpace(body.ReportId))
            {
                if (!Guid.TryParse(body.ReportId, out var parsed))
                    return Error(400, "bad_report", $"'{body.ReportId}' is not a dev report id.");
                reportTitle = parts.ReportTitle(tenant, parsed);
                if (reportTitle is null)
                    return Error(404, "report_not_found", $"This account has no dev report {parsed:D}.");
                reportId = parsed;
            }

            // WHO SENT IT is the Gateway's record of the calling session, never a body field.
            var naming = parts.Naming(tenant, sessionId);
            var seat = parts.FactorySeatOf(tenant, sessionId);
            var sender = new YourInboxSender(YourInboxWords.Senders.Session, sessionId, naming.Number, naming.Name, seat?.Factory, seat?.Seat);
            return Refusals(() =>
            {
                var message = parts.Store.Send(tenant, new YourInboxSendInput(body.Subject, body.Body, sender, parts.NowUtc(), reportId, reportTitle));
                return Results.Json(new { message = YourInboxFold.Message(message, Titles(parts, tenant)) }, statusCode: StatusCodes.Status201Created);
            });
        });

        // ---------------------------------------------------------------- the owner's routes

        app.MapGet(OwnerPrefix, (HttpContext ctx) =>
        {
            FileLog.Write($"[YourInboxEndpoints] GET {OwnerPrefix}: identity={AuthMiddleware.IdentityKind(ctx)}");
            if (Owner(parts, ctx, out var tenant, out _) is { } refused) return refused;
            var view = ctx.Request.Query["view"].ToString().Trim().ToLowerInvariant();
            if (view.Length == 0) view = YourInboxWords.Views.Inbox;
            if (!YourInboxWords.Views.All.Contains(view))
                return Error(400, "bad_view", $"view must be one of {string.Join(", ", YourInboxWords.Views.All)}, not '{view}'.");
            var rawLimit = ctx.Request.Query["limit"].ToString();
            var limit = YourInboxStore.DefaultList;
            if (!string.IsNullOrEmpty(rawLimit)
                && (!int.TryParse(rawLimit, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out limit)
                    || limit < 1 || limit > YourInboxStore.MaxList))
                return Error(400, "bad_limit", $"limit must be a whole number from 1 to {YourInboxStore.MaxList}.");
            var (messages, total) = parts.Store.List(tenant, view, limit);
            var unread = parts.Store.UnreadCount(tenant);
            var titles = Titles(parts, tenant);
            // The screen's filters, with their words and counts, and what this view says when it is empty: the Gateway's
            // words, printed as they are (CLAUDE.md rule 7).
            var filters = parts.Store.ViewCounts(tenant)
                .Select(c => new YourInboxFilterDto(c.View, YourInboxWords.ViewLabel(c.View), c.Count)).ToList();
            return Results.Json(new
            {
                view,
                count = total,
                unread,
                unreadLabel = YourInboxWords.CountLabel(unread),
                filters,
                empty = YourInboxWords.EmptyLine(view),
                messages = messages.Select(m => YourInboxFold.Message(m, titles)).ToList(),
            });
        });

        // No entry log line, on purpose: this is the route a phone polls for the Inbox entry's count, and a line per poll
        // would bury the log. A refusal is still answered with its reason, and every write route logs.
        app.MapGet(OwnerPrefix + "/count", (HttpContext ctx) =>
        {
            if (Owner(parts, ctx, out var tenant, out _) is { } refused) return refused;
            var unread = parts.Store.UnreadCount(tenant);
            return Results.Json(new { unread, label = YourInboxWords.CountLabel(unread) });
        });

        app.MapGet(OwnerPrefix + "/{id}", (string id, HttpContext ctx) =>
        {
            FileLog.Write($"[YourInboxEndpoints] GET {OwnerPrefix}/{id}: identity={AuthMiddleware.IdentityKind(ctx)}");
            if (Owner(parts, ctx, out var tenant, out _) is { } refused) return refused;
            if (Find(parts, tenant, id) is not { } message) return NotFound(id);
            return Results.Json(new { message = YourInboxFold.Message(message, Titles(parts, tenant), parts.Store.Replies(tenant, message.Id)) });
        });

        app.MapPost(OwnerPrefix + "/{id}/state", async (string id, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[YourInboxEndpoints] POST {OwnerPrefix}/{id}/state: identity={AuthMiddleware.IdentityKind(ctx)}");
            if (Owner(parts, ctx, out var tenant, out _) is { } refused) return refused;
            if (!Guid.TryParse(id, out var messageId)) return NotFound(id);
            var (body, bad) = await ReadAsync<StateBody>(ctx, ct);
            if (bad is not null) return bad;
            var state = (body?.State ?? "").Trim().ToLowerInvariant();
            return Refusals(() =>
            {
                var message = parts.Store.SetState(tenant, messageId, state, parts.NowUtc());
                if (message is null) return NotFound(id);
                return Results.Json(new { message = YourInboxFold.Message(message, Titles(parts, tenant)) });
            });
        });

        app.MapPost(OwnerPrefix + "/{id}/reply", async (string id, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[YourInboxEndpoints] POST {OwnerPrefix}/{id}/reply: identity={AuthMiddleware.IdentityKind(ctx)}");
            if (Owner(parts, ctx, out var tenant, out var device) is { } refused) return refused;
            if (Find(parts, tenant, id) is not { } message) return NotFound(id);
            var (body, bad) = await ReadAsync<ReplyBody>(ctx, ct);
            if (bad is not null) return bad;
            var to = string.IsNullOrWhiteSpace(body?.To) ? null : body.To.Trim().ToLowerInvariant();
            if (to is not (null or YourInboxWords.ReplyTargets.Factory))
                return Error(400, "bad_reply_target",
                    $"A reply goes where the message came from; the only other place is \"{YourInboxWords.ReplyTargets.Factory}\", not '{body!.To}'.");
            return await RefusalsAsync(() => ReplyAsync(parts, tenant, device!, message, YourInboxStore.CheckReply(body?.Text), to, ct));
        });
    }

    // ---------------------------------------------------------------- reply routing

    private static async Task<IResult> ReplyAsync(Parts parts, TenantId tenant, DeviceCredentialIdentity device, YourInboxMessageEntity message,
        string text, string? to, CancellationToken ct)
    {
        var titles = Titles(parts, tenant);
        if (message.FromKind == YourInboxWords.Senders.Gateway)
            return Error(409, "no_reply_to_gateway", YourInboxWords.NoReplyToGateway);
        if (message.FromKind == YourInboxWords.Senders.Factory || to == YourInboxWords.ReplyTargets.Factory)
        {
            if (message.Factory is null)
                return Error(409, "no_factory_to_reply_into",
                    "This message did not come from a factory or a factory's seat, so there is no Factory inbox to leave the reply in. Nothing was sent.");
            return await ReplyIntoFactoryAsync(parts, tenant, device, message, text, titles, ct);
        }

        var sessionId = message.SessionId!;
        var live = parts.Liveness(tenant, sessionId);
        if (live.Reach == DevReportSessionReach.Ended)
        {
            // THE SESSION HAS ENDED: say so, send nothing, and offer where the reply can go instead - a new session where
            // it ran, and the Factory inbox of the factory it was a seat of, when that factory still takes messages.
            var place = parts.PlaceOf(tenant, sessionId);
            var offers = new List<YourInboxReplyOfferDto>();
            var canStartNew = place is { DirectorId: { Length: > 0 }, RepoPath: { Length: > 0 } };
            if (canStartNew)
                offers.Add(new YourInboxReplyOfferDto("new-session", $"Start a new session in {place!.RepoPath} with this reply",
                    DirectorId: place.DirectorId, RepoPath: place.RepoPath, Prompt: YourInboxFold.ReplyForNewSession(message, text)));
            string? factoryTitle = null;
            if (message.Factory is { } factory && OpenFactory(parts, tenant, factory, out _) is null)
            {
                factoryTitle = titles.TryGetValue(factory, out var t) ? t : factory;
                offers.Add(new YourInboxReplyOfferDto(YourInboxWords.ReplyTargets.Factory, YourInboxFold.LeaveInFactoryLabel(factoryTitle),
                    Factory: factory));
            }
            FileLog.Write($"[YourInboxEndpoints] reply NOT SENT on {message.Id}: session {sessionId} has ended ({live.Why}); offers={offers.Count}");
            return Results.Json(new
            {
                error = YourInboxFold.SessionEndedSentence(message, canStartNew, factoryTitle),
                code = "session_ended",
                offers,
            }, statusCode: StatusCodes.Status409Conflict);
        }

        // RUNNING (waiting or working): into its inbox as a notice from the Gateway, read when it is free. The doorbell rings
        // it; nothing is typed into it mid-turn.
        // THE ORDER IS DELIBERATE, here and in the factory reply below: deliver first, then record. If the record fails, the
        // reply has gone and the owner sees an error and no record - he is told. The other order would leave a record of a
        // reply that never went, which tells him something false. Do not swap them.
        var outcome = parts.DeliverToSession(tenant, sessionId, YourInboxFold.ReplyForSession(message, text));
        if (outcome.Outcome is not (FleetMessageOutcome.Queued or FleetMessageOutcome.DuplicateDropped))
            throw new InvalidOperationException(
                $"A reply from Your inbox to session {sessionId} was refused by the fleet inbox ({outcome.Outcome}: {outcome.Response.Error}); " +
                "a notice from the Gateway is refused only for its text, which this route checked.");
        var reply = parts.Store.AddReply(tenant, message, text, YourInboxWords.ReplyTargets.Session, sessionId, null,
            outcome.Response.MessageId, null, device.DeviceId, parts.NowUtc());
        FileLog.Write($"[YourInboxEndpoints] reply {reply.Id} on {message.Id} went to session {sessionId} as fleet message {outcome.Response.MessageId} ({outcome.Outcome})");
        var after = parts.Store.Get(tenant, message.Id)!;
        return Results.Json(new
        {
            reply = YourInboxFold.Reply(reply, after, titles),
            message = YourInboxFold.Message(after, titles, parts.Store.Replies(tenant, message.Id)),
        }, statusCode: StatusCodes.Status201Created);
    }

    /// <summary>
    /// A REPLY INTO A FACTORY'S FACTORY INBOX: the owner's text, verbatim, left there as a typed message from the device
    /// he replied on - exactly what his own send to that factory writes, so the factory's Secretary files it like any
    /// other. The factory must still take messages: registered, not archived, with a Boss seat.
    /// </summary>
    private static async Task<IResult> ReplyIntoFactoryAsync(Parts parts, TenantId tenant, DeviceCredentialIdentity device,
        YourInboxMessageEntity message, string text, IReadOnlyDictionary<string, string> titles, CancellationToken ct)
    {
        if (OpenFactory(parts, tenant, message.Factory!, out var registered) is { } closed) return closed;
        var surface = SessionOriginSurfaces.FromDeviceType(device.DeviceType);
        if (surface is not (SessionOriginSurfaces.Phone or SessionOriginSurfaces.Cockpit))
            return Error(403, "owner_device_required", "Only the owner's phone or Cockpit leaves a message in a factory inbox.");

        var left = await parts.SendToFactory(tenant, new FactoryInboxSendInput(
            registered!.Factory, text,
            Note: FactoryInboxWords.Notes.None,
            Source: FactoryInboxWords.Sources.Typed,
            Surface: surface == SessionOriginSurfaces.Phone ? FactoryInboxWords.Surfaces.Phone : FactoryInboxWords.Surfaces.Cockpit,
            SentBy: device.DeviceId,
            NowUtc: parts.NowUtc()), ct);
        // The same event as the owner's own send to a factory: the wake looks at the factory when the window closes.
        parts.MessageSent(tenant, left.Factory, left.SentAtUtc);
        var reply = parts.Store.AddReply(tenant, message, text, YourInboxWords.ReplyTargets.Factory, null, registered.Factory,
            null, left.Id, device.DeviceId, parts.NowUtc());
        FileLog.Write($"[YourInboxEndpoints] reply {reply.Id} on {message.Id} left in the Factory inbox of {registered.Factory} as {left.Id}");
        var after = parts.Store.Get(tenant, message.Id)!;
        return Results.Json(new
        {
            reply = YourInboxFold.Reply(reply, after, titles),
            message = YourInboxFold.Message(after, titles, parts.Store.Replies(tenant, message.Id)),
        }, statusCode: StatusCodes.Status201Created);
    }

    /// <summary>The factory, when it still takes messages; otherwise the refusal that says why. Null on success.</summary>
    private static IResult? OpenFactory(Parts parts, TenantId tenant, string factory, out RegisteredFactoryDto? registered)
    {
        registered = parts.Factories(tenant).FirstOrDefault(f => string.Equals(f.Factory, factory, StringComparison.OrdinalIgnoreCase));
        if (registered is null)
            return Error(409, "factory_not_found", $"'{factory}' is no longer a factory of this account, so there is no Factory inbox to leave the reply in. Nothing was sent.");
        if (registered.ArchivedAtUtc is not null)
            return Error(409, "factory_archived", $"{registered.Title} is archived, so it takes no messages. Restore it from the Factories screen first. Nothing was sent.");
        if (string.IsNullOrWhiteSpace(registered.BossSeat))
            return Error(409, "factory_has_no_boss", $"'{registered.Factory}' names no Boss seat, so nobody would read a reply left there. Nothing was sent.");
        return null;
    }

    // ---------------------------------------------------------------- who is calling

    /// <summary>
    /// The owner: a device key of the account - his phone or his Cockpit. A session key and the machine token are refused.
    /// Null on success with the tenant and the device set; otherwise the refusal.
    /// </summary>
    private static IResult? Owner(Parts parts, HttpContext ctx, out TenantId tenant, out DeviceCredentialIdentity? device)
    {
        tenant = default;
        device = null;
        if (parts.ResolveTenant(ctx) is not { } resolved) return NoTenant();
        if (AuthMiddleware.CallingSession(ctx) is { } session)
        {
            FileLog.Write($"[YourInboxEndpoints] owner route REFUSED to session {session.SessionId:D}");
            return Error(403, "session_key_out_of_scope",
                "Your inbox is the owner's mailbox: a session sends to it with cc-devthrottle inbox send, and never reads it.");
        }
        device = AuthMiddleware.AuthenticatedDevice(ctx);
        if (device is null)
            return Error(403, "owner_device_required", "Your inbox is read only from the owner's phone or Cockpit.");
        tenant = resolved;
        return null;
    }

    // ---------------------------------------------------------------- helpers

    private static YourInboxMessageEntity? Find(Parts parts, TenantId tenant, string id)
        => Guid.TryParse(id, out var messageId) ? parts.Store.Get(tenant, messageId) : null;

    private static IReadOnlyDictionary<string, string> Titles(Parts parts, TenantId tenant)
    {
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in parts.Factories(tenant))
            titles[f.Factory] = string.IsNullOrWhiteSpace(f.Title) ? f.Factory : f.Title;
        return titles;
    }

    private static async Task<(T? Body, IResult? Error)> ReadAsync<T>(HttpContext ctx, CancellationToken ct) where T : class
    {
        if (ctx.Request.ContentLength is 0) return (null, null);
        try
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var text = await reader.ReadToEndAsync(ct);
            if (string.IsNullOrWhiteSpace(text)) return (null, null);
            return (JsonSerializer.Deserialize<T>(text, JsonOpts), null);
        }
        catch (JsonException ex)
        {
            return (null, Error(400, "bad_request_body", $"The body could not be read as JavaScript Object Notation: {ex.Message}"));
        }
    }

    private static async Task<IResult> RefusalsAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (YourInboxRefusedException ex)
        {
            FileLog.Write($"[YourInboxEndpoints] refused ({ex.Code}): {ex.Message}");
            return Error(ex.Status, ex.Code, ex.Message);
        }
        catch (FactoryInboxRefusedException ex)
        {
            FileLog.Write($"[YourInboxEndpoints] the Factory inbox refused ({ex.Code}): {ex.Message}");
            return Error(ex.Status, ex.Code, ex.Message);
        }
    }

    private static IResult Refusals(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (YourInboxRefusedException ex)
        {
            FileLog.Write($"[YourInboxEndpoints] refused ({ex.Code}): {ex.Message}");
            return Error(ex.Status, ex.Code, ex.Message);
        }
    }

    private static IResult NotFound(string id) => Error(404, "message_not_found", $"There is no message {id} in your inbox.");

    private static IResult NoTenant() => Error(403, "no_tenant", "No account is bound to this request.");

    private static IResult Error(int status, string code, string error)
        => Results.Json(new { error, code }, statusCode: status);

    private sealed class SendBody
    {
        public string? Subject { get; set; }
        public string? Body { get; set; }
        public string? ReportId { get; set; }
    }

    private sealed class StateBody
    {
        public string? State { get; set; }
    }

    private sealed class ReplyBody
    {
        public string? Text { get; set; }

        /// <summary>Null for where the message came from; "factory" to leave a reply to a factory seat's message in its
        /// factory's Factory inbox - the offer made when that session has ended.</summary>
        public string? To { get; set; }
    }
}
