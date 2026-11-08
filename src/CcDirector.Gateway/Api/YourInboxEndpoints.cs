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
/// The session routes - any session, with its own key, as itself. The account is the one the session's key is bound to -
/// the account its Director is signed in as - and nothing in the request can name another:
///
///   POST /sessions/{sid}/inbox                  body {subject, body, reportId?} -> 201 {message}
///                                               what <c>cc-devthrottle inbox send</c> calls
///   GET  /sessions/{sid}/inbox?view=unread|inbox|archived&amp;limit=   the account's inbox, one view (inbox list)
///   GET  /sessions/{sid}/inbox/{id}             one message with its replies (inbox read); does NOT mark it read
///   POST /sessions/{sid}/inbox/{id}/state       body {state: unread|read|archived|restore} -> {message}
///                                               (inbox mark, inbox delete = archive, inbox restore)
///   POST /sessions/{sid}/inbox/{id}/reply       body {text, to?} -> 201 {reply, message}, or 409 with offers (inbox reply)
///   POST /sessions/{sid}/factories/{factory}/inbox   body {text} -> 201 {message}  (factory inbox send)
///                                               mapped on the factory gate's group (<see cref="FactoryAgentsGate"/>), so
///                                               an account without factories is answered 404, as on every factory route
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
/// WHO MAY (the owner's decisions of 7 November 2026). "An agent should be able to do what it can do with regular
/// email. Read, delete, and also send, to factory mailboxes or its own." So a session reads, marks, archives, restores
/// and replies to the account's inbox - through ITS OWN routes under /sessions/{sid}, never through the owner's. The
/// owner's routes under /gateway/inbox stay his device keys' alone: a session key reaching one is refused by
/// <see cref="SessionKeyGuard"/> and refused HERE as well, so a later edit to the guard cannot make an agent's act look
/// like the owner's. The machine token is refused everywhere: it is a machine, not the owner and not a session.
///
/// AN AGENT IS ALWAYS NAMED. The session is the one whose key made the call - named in the path and checked against the
/// key - with who it is (number, name, factory, seat) read from the Gateway's own records, never from the body. Every
/// act it takes on a message (read, mark, archive, restore, reply) is recorded with that session and the time, and the
/// message shows it in a line folded here (<see cref="YourInboxWords.AgentLine"/>). Reading does NOT mark a message read;
/// an agent marks read or unread only when it is told to. "Delete" is archive (the owner, 7 November 2026: "The message
/// leaves your inbox, and you can still find it, and undo it, under Archived"). A reply an agent writes, and a message it
/// leaves in a factory, is shown as from that agent, never as from the owner. A factory's own seats may not leave a
/// message in their own factory's inbox: that would loop the factory into itself.
///
/// REPLY ROUTING (the owner's decision, mission section 4). A reply to a session's message goes into that session's
/// inbox if it is still running - as a notice from the Gateway, read when it is free, never typed into it mid-turn. If
/// it has ended the reply is NOT sent: the route answers 409 with the sentence and the offers - a new session with the
/// reply as its first prompt, and, when the session was a factory's seat, that factory's Factory inbox. Taking the second
/// offer is the same call with <c>to: "factory"</c>. A reply to a factory's message goes into that factory's Factory
/// inbox, as a typed message from the owner's phone or Cockpit, exactly as if he had left it there himself.
///
/// NOTHING IS DELETED: there is no delete route, for the owner or for an agent.
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
    /// <param name="SendAsAgent">An AGENT's reply to a session: an ordinary fleet message FROM that agent (sender, then
    /// recipient, then text), judged by the normal message policy - who may write to whom, and how often - with the agent
    /// recorded as its sender. Never a Gateway notice: a notice waives the policy and names no sender, which would let any
    /// session key write into any running session unattributed.</param>
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
        Func<TenantId, string, string, string, FleetSendOutcome> SendAsAgent,
        Func<TenantId, FactoryInboxSendInput, CancellationToken, Task<FactoryInboxMessageEntity>> SendToFactory,
        Action<TenantId, string, DateTime> MessageSent,
        Func<DateTime> NowUtc);

    /// <param name="app">Where the inbox routes are mapped.</param>
    /// <param name="factoryGate">The factory gate's group (<see cref="FactoryAgentsGate.Group"/>). The one route that
    /// leaves a message in a factory is mapped on it, so an account without factories is refused there exactly as on
    /// every other factory route.</param>
    /// <param name="parts">What the routes read.</param>
    public static void Map(IEndpointRouteBuilder app, RouteGroupBuilder factoryGate, Parts parts)
    {
        ArgumentNullException.ThrowIfNull(factoryGate);
        ArgumentNullException.ThrowIfNull(parts);
        FileLog.Write("[YourInboxEndpoints] mapping the Your inbox routes (six session routes, five owner routes)");

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

        // ---------------------------------------------------------------- an agent's routes (7 November 2026)

        app.MapGet("/sessions/{sid}/inbox", (string sid, HttpContext ctx) =>
        {
            FileLog.Write($"[YourInboxEndpoints] GET /sessions/{sid}/inbox: identity={AuthMiddleware.IdentityKind(ctx)}");
            if (Agent(parts, ctx, sid, out var tenant, out _) is { } refused) return refused;
            return ListView(parts, ctx, tenant);
        });

        app.MapGet("/sessions/{sid}/inbox/{id}", (string sid, string id, HttpContext ctx) =>
        {
            FileLog.Write($"[YourInboxEndpoints] GET /sessions/{sid}/inbox/{id}: identity={AuthMiddleware.IdentityKind(ctx)}");
            if (Agent(parts, ctx, sid, out var tenant, out var agent) is { } refused) return refused;
            if (Find(parts, tenant, id) is not { } message) return NotFound(id);
            // READING IS RECORDED, AND IT DOES NOT MARK THE MESSAGE READ: the owner has still not read it.
            parts.Store.RecordAgentRead(tenant, message.Id, agent!, parts.NowUtc());
            var folded = FoldOne(parts, tenant, message.Id);
            return Results.Json(new
            {
                message = folded,
                replyGoesTo = folded.ReplyTo?.Label ?? YourInboxWords.NoReplyLine,
                note = YourInboxWords.AgentReadNote,
            });
        });

        app.MapPost("/sessions/{sid}/inbox/{id}/state", async (string sid, string id, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[YourInboxEndpoints] POST /sessions/{sid}/inbox/{id}/state: identity={AuthMiddleware.IdentityKind(ctx)}");
            if (Agent(parts, ctx, sid, out var tenant, out var agent) is { } refused) return refused;
            if (Find(parts, tenant, id) is not { } message) return NotFound(id);
            var (body, bad) = await ReadAsync<StateBody>(ctx, ct);
            if (bad is not null) return bad;
            var asked = (body?.State ?? "").Trim().ToLowerInvariant();
            // RESTORE is the undo of an agent's delete: the message goes back to the inbox as it was (the store decides how,
            // from what it holds). Every other word is a state.
            return Refusals(() =>
            {
                var change = asked == YourInboxWords.AgentRestore
                    ? parts.Store.RestoreByAgent(tenant, message.Id, agent!, parts.NowUtc())
                    : parts.Store.SetStateByAgent(tenant, message.Id, asked, agent!, parts.NowUtc());
                if (change is null) return NotFound(id);
                return Results.Json(new
                {
                    message = FoldOne(parts, tenant, change.Value.Message.Id, withReplies: false),
                    done = YourInboxWords.AgentDone(change.Value.Act, change.Value.Message.State),
                });
            });
        });

        app.MapPost("/sessions/{sid}/inbox/{id}/reply", async (string sid, string id, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[YourInboxEndpoints] POST /sessions/{sid}/inbox/{id}/reply: identity={AuthMiddleware.IdentityKind(ctx)}");
            if (Agent(parts, ctx, sid, out var tenant, out var agent) is { } refused) return refused;
            if (Find(parts, tenant, id) is not { } message) return NotFound(id);
            var (body, bad) = await ReadAsync<ReplyBody>(ctx, ct);
            if (bad is not null) return bad;
            var to = string.IsNullOrWhiteSpace(body?.To) ? null : body.To.Trim().ToLowerInvariant();
            if (to is not (null or YourInboxWords.ReplyTargets.Factory))
                return Error(400, "bad_reply_target",
                    $"A reply goes where the message came from; the only other place is \"{YourInboxWords.ReplyTargets.Factory}\", not '{body!.To}'.");
            return await RefusalsAsync(() => ReplyAsync(parts, tenant, new Replier(null, agent), message, YourInboxStore.CheckReply(body?.Text), to, ct));
        });

        // A factory route, so it is mapped behind the factory gate: an account without factories gets the gate's 404.
        factoryGate.MapPost("/sessions/{sid}/factories/{factory}/inbox", async (string sid, string factory, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[YourInboxEndpoints] POST /sessions/{sid}/factories/{factory}/inbox: identity={AuthMiddleware.IdentityKind(ctx)}");
            if (Agent(parts, ctx, sid, out var tenant, out var agent) is { } refused) return refused;
            var (body, bad) = await ReadAsync<FactorySendBody>(ctx, ct);
            if (bad is not null) return bad;
            if (body is null) return Error(400, "bad_request_body", "The body must be an object with a \"text\".");
            if (OpenFactory(parts, tenant, factory, out var registered, "message") is { } closed) return closed;
            if (OwnSeatRefusal(parts, tenant, agent!, registered!) is { } loop) return loop;
            return await RefusalsAsync(async () =>
            {
                var left = await LeaveInFactoryAsync(parts, tenant, agent!, registered!, body.Text, ct);
                return Results.Json(new { message = FactoryInboxFold.Message(left, null) }, statusCode: StatusCodes.Status201Created);
            });
        });

        // ---------------------------------------------------------------- the owner's routes

        app.MapGet(OwnerPrefix, (HttpContext ctx) =>
        {
            FileLog.Write($"[YourInboxEndpoints] GET {OwnerPrefix}: identity={AuthMiddleware.IdentityKind(ctx)}");
            if (Owner(parts, ctx, out var tenant, out _) is { } refused) return refused;
            return ListView(parts, ctx, tenant);
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
            return Results.Json(new { message = FoldOne(parts, tenant, message.Id) });
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
                return Results.Json(new { message = FoldOne(parts, tenant, message.Id, withReplies: false) });
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
            return await RefusalsAsync(() => ReplyAsync(parts, tenant, new Replier(device, null), message, YourInboxStore.CheckReply(body?.Text), to, ct));
        });
    }

    // ---------------------------------------------------------------- reply routing

    /// <summary>Who is replying: the owner, from his phone or Cockpit (<see cref="Device"/>), or an agent
    /// (<see cref="Agent"/>). Exactly one is set. The routing is the same for both; what differs is who the reply is shown
    /// as from, and that only the owner's reply marks the message read.</summary>
    private sealed record Replier(DeviceCredentialIdentity? Device, YourInboxAgent? Agent)
    {
        public string SentBy => Device?.DeviceId ?? Agent!.SessionId;
    }

    private static async Task<IResult> ReplyAsync(Parts parts, TenantId tenant, Replier by, YourInboxMessageEntity message,
        string text, string? to, CancellationToken ct)
    {
        var titles = Titles(parts, tenant);
        if (message.FromKind == YourInboxWords.Senders.Gateway)
            return Error(409, "no_reply_to_gateway", YourInboxWords.NoReplyToGateway(message.Seat));
        if (message.FromKind == YourInboxWords.Senders.Factory || to == YourInboxWords.ReplyTargets.Factory)
        {
            if (message.Factory is null)
                return Error(409, "no_factory_to_reply_into",
                    "This message did not come from a factory or a factory's seat, so there is no Factory inbox to leave the reply in. Nothing was sent.");
            return await ReplyIntoFactoryAsync(parts, tenant, by, message, text, titles, ct);
        }

        var sessionId = message.SessionId!;
        // An agent does not answer its own message: the reply would land in its own inbox, a loop with no reader.
        if (by.Agent is { } self && string.Equals(self.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            return Error(409, "reply_to_yourself",
                "This message is one this session sent, so a reply to it would come back to this session. Nothing was sent.");
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
                    DirectorId: place.DirectorId, RepoPath: place.RepoPath, Prompt: YourInboxFold.ReplyForNewSession(message, text, by.Agent)));
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
        FleetSendOutcome outcome;
        if (by.Agent is { } agent)
        {
            // AN AGENT'S REPLY IS AN ORDINARY MESSAGE FROM THAT AGENT, held to the normal policy: who may write to whom, and
            // how often. A refusal is the answer, in the policy's words, and nothing is recorded.
            outcome = parts.SendAsAgent(tenant, agent.SessionId, sessionId, YourInboxFold.AgentReplyForSession(message, text, agent));
            if (outcome.Outcome is not (FleetMessageOutcome.Queued or FleetMessageOutcome.DuplicateDropped))
            {
                FileLog.Write($"[YourInboxEndpoints] agent reply REFUSED on {message.Id}: session {agent.SessionId} to {sessionId}: {outcome.Outcome}");
                return Error(outcome.StatusCode, "reply_refused",
                    $"{outcome.Response.Error} Nothing was sent. {YourInboxWords.AgentReplyRefusedInstead}");
            }
        }
        else
        {
            outcome = parts.DeliverToSession(tenant, sessionId, YourInboxFold.ReplyForSession(message, text));
            if (outcome.Outcome is not (FleetMessageOutcome.Queued or FleetMessageOutcome.DuplicateDropped))
                throw new InvalidOperationException(
                    $"A reply from Your inbox to session {sessionId} was refused by the fleet inbox ({outcome.Outcome}: {outcome.Response.Error}); " +
                    "a notice from the Gateway is refused only for its text, which this route checked.");
        }
        var reply = parts.Store.AddReply(tenant, message, text, YourInboxWords.ReplyTargets.Session, sessionId, null,
            outcome.Response.MessageId, null, by.SentBy, parts.NowUtc(), by.Agent);
        FileLog.Write($"[YourInboxEndpoints] reply {reply.Id} on {message.Id} went to session {sessionId} as fleet message {outcome.Response.MessageId} ({outcome.Outcome}); by {(by.Agent is null ? "the owner" : "session " + by.Agent.SessionId)}");
        return Replied(parts, tenant, reply, message.Id);
    }

    /// <summary>
    /// A REPLY INTO A FACTORY'S FACTORY INBOX: the text, verbatim, left there as a typed message. From the owner it is from
    /// the device he replied on - exactly what his own send to that factory writes; from an agent it is from that agent,
    /// named - exactly what <c>factory inbox send</c> writes - and never from the owner. The factory's Secretary files it
    /// like any other. The factory must still take messages: registered, not archived, with a Boss seat.
    /// </summary>
    private static async Task<IResult> ReplyIntoFactoryAsync(Parts parts, TenantId tenant, Replier by,
        YourInboxMessageEntity message, string text, IReadOnlyDictionary<string, string> titles, CancellationToken ct)
    {
        if (OpenFactory(parts, tenant, message.Factory!, out var registered) is { } closed) return closed;
        FactoryInboxMessageEntity left;
        if (by.Agent is { } agent)
        {
            if (OwnSeatRefusal(parts, tenant, agent, registered!) is { } loop) return loop;
            left = await LeaveInFactoryAsync(parts, tenant, agent, registered!, text, ct);
        }
        else
        {
            var device = by.Device!;
            var surface = SessionOriginSurfaces.FromDeviceType(device.DeviceType);
            if (surface is not (SessionOriginSurfaces.Phone or SessionOriginSurfaces.Cockpit))
                return Error(403, "owner_device_required", "Only the owner's phone or Cockpit leaves a message in a factory inbox.");

            left = await parts.SendToFactory(tenant, new FactoryInboxSendInput(
                registered!.Factory, text,
                Note: FactoryInboxWords.Notes.None,
                Source: FactoryInboxWords.Sources.Typed,
                Surface: surface == SessionOriginSurfaces.Phone ? FactoryInboxWords.Surfaces.Phone : FactoryInboxWords.Surfaces.Cockpit,
                SentBy: device.DeviceId,
                NowUtc: parts.NowUtc()), ct);
            // The same event as the owner's own send to a factory: the wake looks at the factory when the window closes.
            parts.MessageSent(tenant, left.Factory, left.SentAtUtc);
        }
        var reply = parts.Store.AddReply(tenant, message, text, YourInboxWords.ReplyTargets.Factory, null, registered!.Factory,
            null, left.Id, by.SentBy, parts.NowUtc(), by.Agent);
        FileLog.Write($"[YourInboxEndpoints] reply {reply.Id} on {message.Id} left in the Factory inbox of {registered.Factory} as {left.Id}");
        return Replied(parts, tenant, reply, message.Id);
    }

    private static IResult Replied(Parts parts, TenantId tenant, YourInboxReplyEntity reply, Guid messageId)
    {
        var titles = Titles(parts, tenant);
        var after = parts.Store.Get(tenant, messageId)!;
        var acts = parts.Store.AgentActions(tenant, [messageId]).GetValueOrDefault(messageId) ?? [];
        var replyAct = acts.FirstOrDefault(a => a.ReplyId == reply.Id);
        return Results.Json(new
        {
            reply = YourInboxFold.Reply(reply, after, titles, replyAct),
            message = YourInboxFold.Message(after, titles, parts.Store.Replies(tenant, messageId), acts),
            done = replyAct is null ? null : YourInboxWords.AgentReplyDone,
        }, statusCode: StatusCodes.Status201Created);
    }

    /// <summary>
    /// AN AGENT LEAVES A MESSAGE IN A FACTORY'S FACTORY INBOX: typed, from that session (surface <c>session</c>, its id as
    /// the sender, its number and name recorded), so the thread shows it as from that agent and never from the owner. The
    /// factory's Secretary and Boss handle it like any message, and the wake is told exactly as for the owner's send.
    /// </summary>
    private static async Task<FactoryInboxMessageEntity> LeaveInFactoryAsync(Parts parts, TenantId tenant, YourInboxAgent agent,
        RegisteredFactoryDto registered, string? text, CancellationToken ct)
    {
        var left = await parts.SendToFactory(tenant, new FactoryInboxSendInput(
            registered.Factory, text,
            Note: FactoryInboxWords.Notes.None,
            Source: FactoryInboxWords.Sources.Typed,
            Surface: FactoryInboxWords.Surfaces.Session,
            SentBy: agent.SessionId,
            NowUtc: parts.NowUtc(),
            SessionNumber: agent.SessionNumber,
            SessionName: agent.SessionName), ct);
        parts.MessageSent(tenant, left.Factory, left.SentAtUtc);
        FileLog.Write($"[YourInboxEndpoints] session {agent.SessionId} left {left.Id} in the Factory inbox of {registered.Factory}");
        return left;
    }

    /// <summary>
    /// NO LOOPS (the design the owner was shown, 7 November 2026): a session that is a seat of a factory - its Secretary, its
    /// Boss, or any other seat it was started as - may not leave a message in that same factory's inbox, because the
    /// factory would then be writing work to itself. Null when the session may.
    /// </summary>
    private static IResult? OwnSeatRefusal(Parts parts, TenantId tenant, YourInboxAgent agent, RegisteredFactoryDto registered)
    {
        var seat = parts.FactorySeatOf(tenant, agent.SessionId);
        if (seat is null || !string.Equals(seat.Factory, registered.Factory, StringComparison.OrdinalIgnoreCase)) return null;
        FileLog.Write($"[YourInboxEndpoints] factory send REFUSED: session {agent.SessionId} is the {seat.Seat} seat of {registered.Factory}");
        return Error(403, "own_factory",
            $"This session is the {seat.Seat} seat of {registered.Title}, and a factory's own seats do not leave messages in its own " +
            "Factory inbox: the factory would be writing work to itself. Nothing was sent.");
    }

    /// <summary>The factory, when it still takes messages; otherwise the refusal that says why. Null on success.
    /// <paramref name="what"/> is what would have been left there: "reply" or "message".</summary>
    private static IResult? OpenFactory(Parts parts, TenantId tenant, string factory, out RegisteredFactoryDto? registered, string what = "reply")
    {
        registered = parts.Factories(tenant).FirstOrDefault(f => string.Equals(f.Factory, factory, StringComparison.OrdinalIgnoreCase));
        if (registered is null)
            return Error(409, "factory_not_found", $"'{factory}' is not a factory of this account, so there is no Factory inbox to leave the {what} in. Nothing was sent.");
        if (registered.ArchivedAtUtc is not null)
            return Error(409, "factory_archived", $"{registered.Title} is archived, so it takes no messages. Restore it from the Factories screen first. Nothing was sent.");
        if (string.IsNullOrWhiteSpace(registered.BossSeat))
            return Error(409, "factory_has_no_boss", $"'{registered.Factory}' names no Boss seat, so nobody would read a {what} left there. Nothing was sent.");
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
                "These are the owner's own inbox routes. A session uses the inbox through its own routes, as itself: cc-devthrottle inbox list, read, mark, delete, restore, reply and send.");
        }
        device = AuthMiddleware.AuthenticatedDevice(ctx);
        if (device is null)
            return Error(403, "owner_device_required", "Your inbox is read only from the owner's phone or Cockpit.");
        tenant = resolved;
        return null;
    }

    /// <summary>
    /// An agent: a session key, calling as the session named in the path. The account is the one the key is bound to; a
    /// request whose resolved account is not the key's own is refused, so nothing in the request can name another account.
    /// Who the agent is - number and name - is read from the Gateway's records. Null on success with the tenant and the
    /// agent set; otherwise the refusal.
    /// </summary>
    private static IResult? Agent(Parts parts, HttpContext ctx, string sid, out TenantId tenant, out YourInboxAgent? agent)
    {
        tenant = default;
        agent = null;
        if (parts.ResolveTenant(ctx) is not { } resolved) return NoTenant();
        var session = AuthMiddleware.CallingSession(ctx);
        if (session is null)
            return Error(403, "session_key_required",
                "This is a session's route to the account's inbox, called with that session's own key. The owner reads his inbox from his phone or Cockpit.");
        if (!Guid.TryParse(sid, out var named) || named != session.SessionId)
        {
            FileLog.Write($"[YourInboxEndpoints] agent route REFUSED sid={sid}: caller session {session.SessionId:D}");
            return Error(403, "not_your_session", $"A session uses the inbox only as itself, and {sid} is not this session.");
        }
        if (resolved != session.Tenant)
        {
            FileLog.Write($"[YourInboxEndpoints] agent route REFUSED: session {session.SessionId:D} is bound to another account than the request's");
            return Error(403, "not_your_account", "A session reads only the inbox of the account its key is bound to.");
        }
        var sessionId = session.SessionId.ToString("D");
        var naming = parts.Naming(resolved, sessionId);
        tenant = resolved;
        agent = new YourInboxAgent(sessionId, naming.Number, naming.Name);
        return null;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// One view of the inbox, newest first, with the unread count, the filters' words and counts, and the view's empty
    /// sentence - the same answer to the owner and to an agent, every word the Gateway's (CLAUDE.md rule 7).
    /// </summary>
    private static IResult ListView(Parts parts, HttpContext ctx, TenantId tenant)
    {
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
        var acts = parts.Store.AgentActions(tenant, messages.Select(m => m.Id).ToList());
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
            messages = messages.Select(m => YourInboxFold.Message(m, titles, null, acts.GetValueOrDefault(m.Id))).ToList(),
        });
    }

    /// <summary>One message as it stands now, folded with what agents did to it and, when read on its own, its replies.</summary>
    private static YourInboxMessageDto FoldOne(Parts parts, TenantId tenant, Guid id, bool withReplies = true)
    {
        var message = parts.Store.Get(tenant, id)
            ?? throw new InvalidOperationException($"Your inbox message {id} was found a moment ago and is gone; nothing deletes a message.");
        var acts = parts.Store.AgentActions(tenant, [id]).GetValueOrDefault(id);
        return YourInboxFold.Message(message, Titles(parts, tenant), withReplies ? parts.Store.Replies(tenant, id) : null, acts);
    }

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

    private sealed class FactorySendBody
    {
        public string? Text { get; set; }
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
