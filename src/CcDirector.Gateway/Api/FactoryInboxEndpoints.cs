using System.Text.Json;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Inbox;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// THE FACTORY INBOX ROUTES (Factory Inbox mission, phase 1).
///
/// The owner's routes - the owner's own phone or browser, and for the four reads also a session of the account:
///
///   POST /gateway/factory/{factory}/inbox                  body {text, note, source, rawText?, corrections?} -> 201 {message}
///   GET  /gateway/factory/inbox                            every factory with a Boss: waiting count and newest message
///   GET  /gateway/factory/{factory}/inbox?limit=           one factory's thread, newest first, each message with its trail
///   GET  /gateway/factory/{factory}/inbox/summary?since=   what came in since a time and the decision on each (no agent)
///   GET  /gateway/factory/{factory}/todo                   the factory's to-do list
///   POST /gateway/factory/{factory}/inbox/dealt-with       the owner dealt with what the stuck alert reports (issue 4746)
///
/// The session routes - the factory is read from the CALLER'S recorded session history, never from the request:
///
///   GET  /sessions/{sid}/factory-inbox                     the Secretary's messages to file
///   POST /sessions/{sid}/factory-inbox/receive             body {ids?, merges?, links?}: claim and mark received
///   POST /sessions/{sid}/factory-inbox/bundle              body {ids?}: hand one bundle to the Boss
///   GET  /sessions/{sid}/factory-inbox/bundle              the Boss reads its factory's open bundles
///   POST /sessions/{sid}/factory-inbox/{messageId}/decide  body {kind, session?, why?}: the Boss decides one message
///
/// WHO MAY. The send is the owner's and nobody else's: a session key is refused here as well as by SessionKeyGuard, so a
/// later edit to the guard is not enough to let an agent leave a message in the owner's name; the machine token is
/// refused too. The Secretary routes need a session started as its factory's Secretary
/// (<see cref="FactoryInboxSeats.Secretary"/>); the Boss routes need a session started as its factory's Boss seat - a
/// child that inherited the seat's rules is neither. Delegated is accepted only with a session whose recorded parent is
/// the deciding Boss: "never does it itself" is checkable.
///
/// Refused in words: an unknown, archived or Boss-less factory, at the send.
/// </summary>
internal static class FactoryInboxEndpoints
{
    public const string OwnerPrefix = "/gateway/factory";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>What the routes read. Every function is required, so a wiring line that forgot one fails to compile.</summary>
    /// <param name="Factories">The account's registered factories.</param>
    /// <param name="FactoryOf">Which factory and seat a session was started as, from its history row.</param>
    /// <param name="ParentOf">The session that started a session, from its history row; null when the Gateway has no history
    /// row for the session yet (a session seconds old), which is a different answer from a row with no parent.</param>
    /// <param name="IsEnded">Whether the Gateway has recorded a session as ended.</param>
    /// <param name="Wakes">Each factory's wake row (phase 2): why its messages wait, shown in place of their state.</param>
    /// <param name="MessageSent">Tells the wake a message was logged (<see cref="FactoryInboxWake.MessageSent"/>). It looks
    /// at the factory when the gather window closes; the route never waits for it.</param>
    /// <param name="Held">The spoken recordings still held PENDING for a factory's inbox in the account's dictation
    /// staging (phase 4 review, finding 2), shown on the thread so a held message is never invisible.</param>
    /// <param name="BundleHanded">Tells the wake a Secretary handed a bundle (<see cref="FactoryInboxWake.BundleHanded"/>),
    /// so the Boss is rung or started at once; the route never waits for it.</param>
    internal sealed record Parts(
        FactoryInboxStore Store,
        Func<HttpContext, TenantId?> ResolveTenant,
        Func<TenantId, IReadOnlyList<RegisteredFactoryDto>> Factories,
        Func<TenantId, string, SessionFactoryLookup> FactoryOf,
        Func<TenantId, string, FactoryInboxRecordedParent?> ParentOf,
        Func<TenantId, string, bool> IsEnded,
        Func<DateTime> NowUtc,
        FactoryInboxWakeStore Wakes,
        Action<TenantId, string, DateTime> MessageSent,
        Action<TenantId, string> BundleHanded,
        Func<TenantId, string, IReadOnlyList<Voice.PendingUpload>> Held);

    public static void Map(IEndpointRouteBuilder app, Parts parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        FileLog.Write("[FactoryInboxEndpoints] mapping the factory inbox routes (six owner routes, five session routes)");

        // ---------------------------------------------------------------- owner routes

        app.MapPost(OwnerPrefix + "/{factory}/inbox", async (string factory, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[FactoryInboxEndpoints] POST {OwnerPrefix}/{factory}/inbox: identity={AuthMiddleware.IdentityKind(ctx)}");
            if (parts.ResolveTenant(ctx) is not { } tenant) return NoTenant();
            if (RefuseOwnerSend(ctx, tenant, factory, parts.Factories, out var owner) is { } refusedSend) return refusedSend;
            var (registered, device, surface) = owner!;

            SendBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<SendBody>(ctx.Request.Body, JsonOpts, ct);
            }
            catch (JsonException ex)
            {
                return Error(400, "bad_request_body", $"The body could not be read as JavaScript Object Notation: {ex.Message}");
            }
            if (body is null) return Error(400, "bad_request_body", "The body must be an object with a \"text\".");
            // THE CORRECTIONS ARE THE GATEWAY'S (the verbatim rule): a client never says which words were corrected. The
            // field is refused by name rather than ignored, so a client that sends one learns it was not used.
            if (body.Corrections is not null)
                return Error(400, "corrections_not_accepted",
                    "A message carries no corrections. For a spoken message send only rawText; the Gateway applies your listed corrections to it.");

            return await RefusalsAsync(async () =>
            {
                var message = await parts.Store.SendAsync(tenant, new FactoryInboxSendInput(
                    registered.Factory, body.Text,
                    Note: string.IsNullOrWhiteSpace(body.Note) ? FactoryInboxWords.Notes.None : body.Note.Trim().ToLowerInvariant(),
                    Source: string.IsNullOrWhiteSpace(body.Source) ? FactoryInboxWords.Sources.Typed : body.Source.Trim().ToLowerInvariant(),
                    Surface: surface,
                    SentBy: device.DeviceId,
                    NowUtc: parts.NowUtc(),
                    RawText: body.RawText), ct);
                // THE WRITE IS THE EVENT (phase 2): the wake looks at this factory when the gather window closes.
                parts.MessageSent(tenant, message.Factory, message.SentAtUtc);
                return Results.Json(new { message = FactoryInboxFold.Message(message, null, null, WaitingOf(parts, tenant, message.Factory)) },
                    statusCode: StatusCodes.Status201Created);
            });
        });

        app.MapGet(OwnerPrefix + "/inbox", (HttpContext ctx) =>
        {
            FileLog.Write($"[FactoryInboxEndpoints] GET {OwnerPrefix}/inbox: identity={AuthMiddleware.IdentityKind(ctx)}");
            if (parts.ResolveTenant(ctx) is not { } tenant) return NoTenant();
            if (RefuseMachineToken(ctx) is { } refused) return refused;
            var counts = parts.Store.CountsByFactory(tenant);
            var lastIds = counts.Values.Select(c => c.Last.Id).ToList();
            var decisions = parts.Store.DecisionsOf(tenant, lastIds);
            var handed = parts.Store.HandedTimes(tenant, counts.Values.Where(c => c.Last.BundleId is not null).Select(c => c.Last.BundleId!.Value).ToList());
            var factories = parts.Factories(tenant)
                .Where(FactoryInboxReach.TakesMessages)
                .OrderBy(f => f.Title, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Factory, StringComparer.Ordinal)
                .Select(f =>
                {
                    var c = counts.TryGetValue(f.Factory, out var found) ? found : null;
                    var lastDecision = c is not null && decisions.TryGetValue(c.Last.Id, out var ld) ? ld : null;
                    var waitingLabels = c is null ? null : WaitingOf(parts, tenant, f.Factory);
                    return new
                    {
                        factory = f.Factory,
                        title = f.Title,
                        // The row's words and tone (phase 4): the screens print these and decide nothing.
                        row = FactoryInboxFold.Row(c?.Waiting ?? 0, c?.Last, lastDecision, waitingLabels),

                        waiting = c?.Waiting ?? 0,
                        lastMessage = c is null ? null : FactoryInboxFold.Message(c.Last,
                            decisions.TryGetValue(c.Last.Id, out var d) ? d : null, HandedOf(handed, c.Last), waitingLabels),
                    };
                })
                .ToList();
            return Results.Json(new { count = factories.Count, factories });
        });

        app.MapGet(OwnerPrefix + "/{factory}/inbox", (string factory, HttpContext ctx) =>
        {
            FileLog.Write($"[FactoryInboxEndpoints] GET {OwnerPrefix}/{factory}/inbox: identity={AuthMiddleware.IdentityKind(ctx)}");
            if (parts.ResolveTenant(ctx) is not { } tenant) return NoTenant();
            if (RefuseMachineToken(ctx) is { } refused) return refused;
            if (Registered(parts, tenant, factory, out var registered) is { } missing) return missing;
            var rawLimit = ctx.Request.Query["limit"].ToString();
            var limit = FactoryInboxStore.DefaultThread;
            if (!string.IsNullOrEmpty(rawLimit)
                && (!int.TryParse(rawLimit, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out limit)
                    || limit < 1 || limit > FactoryInboxStore.MaxThread))
                return Error(400, "bad_limit", $"limit must be a whole number from 1 to {FactoryInboxStore.MaxThread}.");
            var (messages, total) = parts.Store.Thread(tenant, registered!.Factory, limit);
            // The held recordings come from the account's dictation staging. When a record there cannot be read, whether
            // a spoken message is held is not known, and the thread says so in words rather than showing "nothing held"
            // or answering a bare 500 with no body (observation 1 of the 4690 review).
            IReadOnlyList<Voice.PendingUpload> heldUploads;
            try { heldUploads = parts.Held(tenant, registered.Factory); }
            catch (InvalidOperationException ex)
            {
                FileLog.Write($"[FactoryInboxEndpoints] GET thread factory={registered.Factory}: held recordings unknown: {ex.Message}");
                return Error(503, "held_recordings_unknown",
                    "The Gateway could not read its stored recordings, so it cannot say whether a spoken message is held for this factory. Try again shortly.");
            }
            return Results.Json(new
            {
                factory = registered.Factory,
                title = registered.Title,
                count = total,
                messages = Fold(parts, tenant, messages),
                // Every spoken message the Gateway still holds for this factory, in its words (finding 2): a recording
                // whose transcription failed for now is on the thread until it is left, never only on one device.
                held = FactoryInboxFold.Held(heldUploads),
            });
        });

        app.MapGet(OwnerPrefix + "/{factory}/inbox/summary", (string factory, HttpContext ctx) =>
        {
            FileLog.Write($"[FactoryInboxEndpoints] GET {OwnerPrefix}/{factory}/inbox/summary: identity={AuthMiddleware.IdentityKind(ctx)}");
            if (parts.ResolveTenant(ctx) is not { } tenant) return NoTenant();
            if (RefuseMachineToken(ctx) is { } refused) return refused;
            if (Registered(parts, tenant, factory, out var registered) is { } missing) return missing;
            var now = parts.NowUtc();
            var since = now.AddDays(-1);
            var rawSince = ctx.Request.Query["since"].ToString();
            if (!string.IsNullOrEmpty(rawSince))
            {
                if (!DateTime.TryParse(rawSince, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out since))
                    return Error(400, "bad_since", $"since must be a date and time, such as 2026-11-04T08:00:00Z, not '{rawSince}'.");
                since = DateTime.SpecifyKind(since, DateTimeKind.Utc);
                if (since > now) return Error(400, "bad_since", "since is in the future, so there is nothing to summarise.");
            }
            return Results.Json(parts.Store.Summary(tenant, registered!.Factory, since, now));
        });

        app.MapGet(OwnerPrefix + "/{factory}/todo", (string factory, HttpContext ctx) =>
        {
            FileLog.Write($"[FactoryInboxEndpoints] GET {OwnerPrefix}/{factory}/todo: identity={AuthMiddleware.IdentityKind(ctx)}");
            if (parts.ResolveTenant(ctx) is not { } tenant) return NoTenant();
            if (RefuseMachineToken(ctx) is { } refused) return refused;
            if (Registered(parts, tenant, factory, out var registered) is { } missing) return missing;
            var items = parts.Store.Todos(tenant, registered!.Factory).Select(Todo).ToList();
            return Results.Json(new { factory = registered.Factory, count = items.Count, items });
        });

        // THE OWNER DEALT WITH IT (issue 4746): the "Dealt with it" control on the stuck alert, in the Cockpit and on the
        // phone. The owner's own device and nobody else - a session key or the machine token never ends the owner's alert
        // for him. What is marked is the store's ruling (the stuck alert's own three rules); the answer says how many.
        app.MapPost(OwnerPrefix + "/{factory}/inbox/dealt-with", (string factory, HttpContext ctx) =>
        {
            FileLog.Write($"[FactoryInboxEndpoints] POST {OwnerPrefix}/{factory}/inbox/dealt-with: identity={AuthMiddleware.IdentityKind(ctx)}");
            if (parts.ResolveTenant(ctx) is not { } tenant) return NoTenant();
            if (AuthMiddleware.CallingSession(ctx) is { } session)
            {
                FileLog.Write($"[FactoryInboxEndpoints] dealt-with REFUSED to session {session.SessionId:D}");
                return Error(403, "session_key_out_of_scope", "A session may not end the owner's alert; only the owner's phone or Cockpit does.");
            }
            var device = AuthMiddleware.AuthenticatedDevice(ctx);
            if (device is null || SessionOriginSurfaces.FromDeviceType(device.DeviceType) == SessionOriginSurfaces.Unknown)
                return Error(403, "owner_device_required", "Only the owner's phone or Cockpit marks a factory inbox as dealt with.");
            // NOT ONLY A REGISTERED FACTORY (issue 4746 review, finding 1). Deleting a factory leaves its inbox rows, and
            // the stuck alert is raised from those rows alone, so a deleted factory's alert must be clearable too. The
            // registry only supplies the stored spelling when it has one; the store refuses when nothing is stuck.
            if (!Factory.FactoryNames.TryFactory(factory, out var folded, out var badName))
                return Error(400, "bad_factory", badName ?? $"'{factory}' is not one spelling of a factory id.");
            var name = parts.Factories(tenant).FirstOrDefault(f => string.Equals(f.Factory, folded, StringComparison.OrdinalIgnoreCase))?.Factory
                       ?? folded;
            return Refusals(() =>
            {
                var marked = parts.Store.DealtWithByOwner(tenant, name, device.DeviceId,
                    sid => parts.IsEnded(tenant, sid), parts.NowUtc());
                return Results.Json(new { factory = name, marked });
            });
        });

        // ---------------------------------------------------------------- session routes

        app.MapGet("/sessions/{sid}/factory-inbox", (string sid, HttpContext ctx) =>
        {
            FileLog.Write($"[FactoryInboxEndpoints] GET /sessions/{sid}/factory-inbox: identity={AuthMiddleware.IdentityKind(ctx)}");
            if (Seat(parts, ctx, sid, SeatKind.Secretary, out var caller) is { } refused) return refused;
            var ended = EndedClaimants(parts, caller!);
            var messages = parts.Store.ToFile(caller!.Tenant, caller.Factory, caller.SessionId, ended);
            return Results.Json(new { factory = caller.Factory, count = messages.Count, messages = Fold(parts, caller.Tenant, messages) });
        });

        app.MapPost("/sessions/{sid}/factory-inbox/receive", async (string sid, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[FactoryInboxEndpoints] POST /sessions/{sid}/factory-inbox/receive: identity={AuthMiddleware.IdentityKind(ctx)}");
            if (Seat(parts, ctx, sid, SeatKind.Secretary, out var caller) is { } refused) return refused;
            var (body, bad) = await ReadAsync<ReceiveBody>(ctx, ct);
            if (bad is not null) return bad;
            if (Ids(body?.Ids, out var ids) is { } badIds) return badIds;
            var merges = new List<FactoryInboxMerge>();
            foreach (var m in body?.Merges ?? [])
            {
                if (!Guid.TryParse(m.Id, out var id) || !Guid.TryParse(m.Into, out var into))
                    return Error(400, "bad_merge", "Each merge names two message ids: {\"id\": \"...\", \"into\": \"...\"}.");
                merges.Add(new FactoryInboxMerge(id, into));
            }
            var links = new List<FactoryInboxLink>();
            foreach (var l in body?.Links ?? [])
            {
                if (!Guid.TryParse(l.Id, out var id))
                    return Error(400, "bad_link", "Each link names a message id, a kind and a reference: {\"id\": \"...\", \"kind\": \"run\", \"ref\": \"...\"}.");
                links.Add(new FactoryInboxLink(id, (l.Kind ?? "").Trim().ToLowerInvariant(), l.Ref ?? ""));
            }
            var ended = EndedClaimants(parts, caller!);
            return Refusals(() =>
            {
                var received = parts.Store.Receive(caller!.Tenant, caller.Factory, caller.SessionId, ids, ended, merges, links, parts.NowUtc());
                return Results.Json(new { factory = caller.Factory, count = received.Count, received = Fold(parts, caller.Tenant, received) });
            });
        });

        app.MapPost("/sessions/{sid}/factory-inbox/bundle", async (string sid, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[FactoryInboxEndpoints] POST /sessions/{sid}/factory-inbox/bundle: identity={AuthMiddleware.IdentityKind(ctx)}");
            if (Seat(parts, ctx, sid, SeatKind.Secretary, out var caller) is { } refused) return refused;
            var (body, bad) = await ReadAsync<BundleBody>(ctx, ct);
            if (bad is not null) return bad;
            if (Ids(body?.Ids, out var ids) is { } badIds) return badIds;
            return Refusals(() =>
            {
                var (bundle, messages) = parts.Store.HandBundle(caller!.Tenant, caller.Factory, caller.SessionId, ids, parts.NowUtc());
                // The Boss is rung, or started, now (phase 2).
                parts.BundleHanded(caller.Tenant, caller.Factory);
                return Results.Json(new { bundle = Bundle(bundle, messages, new Dictionary<Guid, FactoryInboxDecisionEntity>()) },
                    statusCode: StatusCodes.Status201Created);
            });
        });

        app.MapGet("/sessions/{sid}/factory-inbox/bundle", (string sid, HttpContext ctx) =>
        {
            FileLog.Write($"[FactoryInboxEndpoints] GET /sessions/{sid}/factory-inbox/bundle: identity={AuthMiddleware.IdentityKind(ctx)}");
            if (Seat(parts, ctx, sid, SeatKind.Boss, out var caller) is { } refused) return refused;
            var open = parts.Store.OpenBundles(caller!.Tenant, caller.Factory, caller.SessionId, parts.NowUtc());
            var decisions = parts.Store.DecisionsOf(caller.Tenant, open.SelectMany(o => o.Messages).Select(m => m.Id).ToList());
            var bundles = open.Select(o => Bundle(o.Bundle, o.Messages, decisions)).ToList();
            return Results.Json(new { factory = caller.Factory, count = bundles.Count, bundles });
        });

        app.MapPost("/sessions/{sid}/factory-inbox/{messageId}/decide", async (string sid, string messageId, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[FactoryInboxEndpoints] POST /sessions/{sid}/factory-inbox/{messageId}/decide: identity={AuthMiddleware.IdentityKind(ctx)}");
            if (Seat(parts, ctx, sid, SeatKind.Boss, out var caller) is { } refused) return refused;
            if (!Guid.TryParse(messageId, out var id))
                return Error(404, "message_not_found", $"There is no message {messageId} in this factory's inbox.");
            var (body, bad) = await ReadAsync<DecideBody>(ctx, ct);
            if (bad is not null) return bad;
            if (body is null) return Error(400, "bad_request_body", "The body must be an object with a \"kind\".");
            var kind = (body.Kind ?? "").Trim().ToLowerInvariant();
            var delegated = string.IsNullOrWhiteSpace(body.Session) ? null : body.Session.Trim();

            if (kind == FactoryInboxWords.Decisions.Delegated && delegated is not null)
            {
                // "NEVER DOES IT ITSELF" IS CHECKABLE: delegated names a session this Boss started, by the session's own
                // recorded parent - never a session it merely names.
                if (!Guid.TryParse(delegated, out var delegatedId))
                    return Error(400, "bad_session", $"'{delegated}' is not a session id.");
                delegated = delegatedId.ToString("D");
                var recorded = parts.ParentOf(caller!.Tenant, delegated);
                // No history row yet is a different answer from "someone else's": the session may be seconds old and not on
                // the Director's next push yet. Telling the Boss to start another would start a second session for one job.
                if (recorded is null)
                {
                    FileLog.Write($"[FactoryInboxEndpoints] decide REFUSED for now: the Gateway has no record of {delegated} yet");
                    return Error(409, "delegated_session_not_yet_known",
                        $"The Gateway has no record of session {delegated} yet. If you just started it, try again in a moment; " +
                        "do not start another.");
                }
                var parent = recorded.ParentSessionId;
                if (!Guid.TryParse(parent, out var parentId) || parentId.ToString("D") != caller.SessionId)
                {
                    FileLog.Write($"[FactoryInboxEndpoints] decide REFUSED: {delegated} was not started by Boss {caller.SessionId} (parent {parent ?? "(none)"})");
                    return Error(403, "not_started_by_this_boss",
                        $"Session {delegated} was not started by this Boss, so the message is not delegated to it. " +
                        "Start a session to do it (cc-devthrottle factory seat start, or session spawn), then decide with that session's id.");
                }
            }
            return Refusals(() =>
            {
                var decided = parts.Store.Decide(caller!.Tenant, caller.Factory, caller.SessionId, id, kind, delegated, body.Why, parts.NowUtc());
                var handed = parts.Store.HandedTimes(caller.Tenant, decided.Message.BundleId is { } b ? [b] : []);
                return Results.Json(new
                {
                    message = FactoryInboxFold.Message(decided.Message, decided.Decision, HandedOf(handed, decided.Message), WaitingOf(parts, caller.Tenant, caller.Factory)),
                    todo = decided.Todo is null ? null : Todo(decided.Todo),
                });
            });
        });
    }

    // ---------------------------------------------------------------- who is calling

    private enum SeatKind { Secretary, Boss }

    private sealed record Caller(TenantId Tenant, string SessionId, string Factory);

    /// <summary>
    /// The calling session, when it is the named session, in a registered factory, and started as that factory's
    /// Secretary or Boss. Everything is read from the session's own recorded history and the registry - never from the
    /// request. Null on success with <paramref name="caller"/> set; otherwise the refusal.
    /// </summary>
    private static IResult? Seat(Parts parts, HttpContext ctx, string sid, SeatKind kind, out Caller? caller)
    {
        caller = null;
        if (parts.ResolveTenant(ctx) is not { } tenant) return NoTenant();
        var session = AuthMiddleware.CallingSession(ctx);
        if (session is null)
            return Error(403, "session_key_required", "Only a session may call this route, with its own session key.");
        if (!Guid.TryParse(sid, out var named) || named != session.SessionId)
        {
            FileLog.Write($"[FactoryInboxEndpoints] REFUSED sid={sid}: caller session {session.SessionId:D}");
            return Error(403, "not_your_session", $"A session may use the factory inbox only as itself, and {sid} is not this session.");
        }
        var sessionId = session.SessionId.ToString("D");
        var lookup = parts.FactoryOf(tenant, sessionId);
        if (!lookup.IsKnown)
            return Error(409, "session_not_yet_known", "The Gateway does not know this session yet. Try again in a moment.");
        if (!lookup.IsInAFactory)
            return Error(403, "not_in_a_factory", "This session is in no factory, so it has no factory inbox.");
        var registered = parts.Factories(tenant).FirstOrDefault(f => string.Equals(f.Factory, lookup.Factory, StringComparison.OrdinalIgnoreCase));
        if (registered is null)
            return Error(404, "factory_not_found", $"'{lookup.Factory}' is not a factory of this account.");

        var seat = lookup.SeatIsInherited ? null : lookup.Seat;
        // THE WAKE'S RECORD (phase 2): the session the wake started as this factory's Secretary or Boss - the only way an
        // UNDEFINED factory's seat is known, since it carries no seat - as a trigger remembers the session it started.
        var woken = parts.Wakes.Find(tenant, registered.Factory);
        var recordedSecretary = woken?.SecretarySessionId is { } s && string.Equals(s, sessionId, StringComparison.OrdinalIgnoreCase);
        var recordedBoss = woken?.BossSessionId is { } b && string.Equals(b, sessionId, StringComparison.OrdinalIgnoreCase);
        if (kind == SeatKind.Secretary && !recordedSecretary
            && !string.Equals(seat, FactoryInboxSeats.Secretary, StringComparison.OrdinalIgnoreCase))
        {
            FileLog.Write($"[FactoryInboxEndpoints] REFUSED {sessionId}: seat '{lookup.Seat}' (kit {lookup.Kit ?? "none"}) is not the Secretary of {registered.Factory}");
            return Error(403, "not_the_secretary",
                $"Only the Secretary of '{registered.Factory}' files its inbox, and this session was not started as its Secretary.");
        }
        if (kind == SeatKind.Boss && !recordedBoss
            && (string.IsNullOrWhiteSpace(registered.BossSeat) || !string.Equals(seat, registered.BossSeat.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            FileLog.Write($"[FactoryInboxEndpoints] REFUSED {sessionId}: seat '{lookup.Seat}' (kit {lookup.Kit ?? "none"}) is not the Boss of {registered.Factory}");
            return Error(403, "not_the_boss",
                $"Only the Boss of '{registered.Factory}', started as its Boss, reads its bundles and decides them; this session is not.");
        }
        caller = new Caller(tenant, sessionId, registered.Factory);
        return null;
    }

    /// <summary>The sessions holding a claim on this factory's messages that the Gateway has recorded as ended.</summary>
    private static IReadOnlyCollection<string> EndedClaimants(Parts parts, Caller caller)
    {
        var ended = parts.Store.OtherClaimants(caller.Tenant, caller.Factory, caller.SessionId)
            .Where(s => parts.IsEnded(caller.Tenant, s))
            .ToList();
        if (ended.Count > 0)
            FileLog.Write($"[FactoryInboxEndpoints] {caller.Factory}: claims held by ended sessions may be taken: {string.Join(", ", ended)}");
        return ended;
    }

    /// <summary>Who leaves a message, and where it goes, once the owner's send has passed every check.</summary>
    internal sealed record OwnerSend(RegisteredFactoryDto Factory, DeviceCredentialIdentity Device, string Surface);

    /// <summary>
    /// THE OWNER'S SEND CHECKS, ONCE: the typed send and the spoken one (<see cref="FactoryInboxVoiceEndpoints"/>) refuse in
    /// the same words. A session key and the machine token are refused; only the owner's phone or Cockpit leaves a message,
    /// and only in a registered factory that is not archived and names a Boss seat. Null on success with
    /// <paramref name="owner"/> set; otherwise the refusal.
    /// </summary>
    internal static IResult? RefuseOwnerSend(HttpContext ctx, TenantId tenant, string factory,
        Func<TenantId, IReadOnlyList<RegisteredFactoryDto>> factories, out OwnerSend? owner)
    {
        owner = null;
        var device = AuthMiddleware.AuthenticatedDevice(ctx);
        if (AuthMiddleware.CallingSession(ctx) is { } session)
        {
            FileLog.Write($"[FactoryInboxEndpoints] send REFUSED to session {session.SessionId:D}");
            return Error(403, "session_key_out_of_scope",
                "a session key may not leave a message in a factory inbox; only the owner's phone or Cockpit leaves one.");
        }
        var surface = SessionOriginSurfaces.FromDeviceType(device?.DeviceType);
        if (device is null || surface == SessionOriginSurfaces.Unknown)
            return Error(403, "owner_device_required", "Only the owner's phone or Cockpit leaves a message in a factory inbox.");

        if (Registered(factories, tenant, factory, out var registered) is { } missing) return missing;
        if (registered!.ArchivedAtUtc is not null)
            return Error(409, "factory_archived", $"{registered.Title} is archived, so it takes no messages. Restore it from the Factories screen first.");
        if (string.IsNullOrWhiteSpace(registered.BossSeat))
            return Error(409, "factory_has_no_boss", $"'{registered.Factory}' names no Boss seat, so nobody would read a message left there.");
        owner = new OwnerSend(registered, device,
            surface == SessionOriginSurfaces.Phone ? FactoryInboxWords.Surfaces.Phone : FactoryInboxWords.Surfaces.Cockpit);
        return null;
    }

    private static IResult? Registered(Parts parts, TenantId tenant, string factory, out RegisteredFactoryDto? registered)
        => Registered(parts.Factories, tenant, factory, out registered);

    private static IResult? Registered(Func<TenantId, IReadOnlyList<RegisteredFactoryDto>> factories, TenantId tenant, string factory,
        out RegisteredFactoryDto? registered)
    {
        registered = null;
        if (!Factory.FactoryNames.TryFactory(factory, out var folded, out var badName))
            return Error(400, "bad_factory", badName ?? $"'{factory}' is not one spelling of a factory id.");
        registered = factories(tenant).FirstOrDefault(f => string.Equals(f.Factory, folded, StringComparison.OrdinalIgnoreCase));
        return registered is null
            ? Error(404, "factory_not_found", $"'{folded}' is not a factory of this account.")
            : null;
    }

    private static IResult? RefuseMachineToken(HttpContext ctx)
    {
        if (AuthMiddleware.CallingSession(ctx) is not null || AuthMiddleware.AuthenticatedDevice(ctx) is not null) return null;
        return Error(403, "owner_or_session_required", "The factory inbox is read by the owner's phone or Cockpit, or by a session of the account.");
    }

    // ---------------------------------------------------------------- folding and bodies

    private static List<FactoryInboxMessageDto> Fold(Parts parts, TenantId tenant, IReadOnlyList<FactoryInboxMessageEntity> messages)
    {
        var decisions = parts.Store.DecisionsOf(tenant, messages.Select(m => m.Id).ToList());
        var handed = parts.Store.HandedTimes(tenant, messages.Where(m => m.BundleId is not null).Select(m => m.BundleId!.Value).Distinct().ToList());
        var waiting = messages.Select(m => m.Factory).Distinct(StringComparer.Ordinal)
            .ToDictionary(f => f, f => WaitingOf(parts, tenant, f), StringComparer.Ordinal);
        return messages.Select(m => FactoryInboxFold.Message(m, decisions.TryGetValue(m.Id, out var d) ? d : null, HandedOf(handed, m),
            waiting[m.Factory])).ToList();
    }

    /// <summary>Why one factory's messages wait, from its wake row, or null when nothing waits.</summary>
    private static FactoryInboxWaitingLabels? WaitingOf(Parts parts, TenantId tenant, string factory)
        => FactoryInboxFold.WaitingOf(parts.Wakes.Find(tenant, factory));

    private static DateTime? HandedOf(IReadOnlyDictionary<Guid, DateTime> handed, FactoryInboxMessageEntity m)
        => m.BundleId is { } b && handed.TryGetValue(b, out var at) ? at : null;

    private static object Bundle(FactoryInboxBundleEntity bundle, IReadOnlyList<FactoryInboxMessageEntity> messages,
        IReadOnlyDictionary<Guid, FactoryInboxDecisionEntity> decisions)
        => new
        {
            id = bundle.Id.ToString("D"),
            factory = bundle.Factory,
            handedBySessionId = bundle.HandedBySessionId,
            handedAtUtc = bundle.HandedAtUtc,
            count = messages.Count,
            items = messages.Select(m => FactoryInboxFold.Message(m, decisions.TryGetValue(m.Id, out var d) ? d : null, bundle.HandedAtUtc)).ToList(),
        };

    private static FactoryTodoDto Todo(FactoryTodoEntity t)
        => new(t.Id.ToString("D"), t.Text, t.FromMessageId?.ToString("D"), t.AddedBySessionId, t.AddedAtUtc);

    private static IResult? Ids(List<string>? raw, out List<Guid>? ids)
    {
        ids = null;
        if (raw is null) return null;
        ids = new List<Guid>();
        foreach (var r in raw)
        {
            if (!Guid.TryParse(r, out var id)) return Error(400, "bad_id", $"'{r}' is not a message id.");
            ids.Add(id);
        }
        return null;
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
        catch (FactoryInboxRefusedException ex)
        {
            FileLog.Write($"[FactoryInboxEndpoints] refused ({ex.Code}): {ex.Message}");
            return Error(ex.Status, ex.Code, ex.Message);
        }
    }

    private static IResult Refusals(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (FactoryInboxRefusedException ex)
        {
            FileLog.Write($"[FactoryInboxEndpoints] refused ({ex.Code}): {ex.Message}");
            return Error(ex.Status, ex.Code, ex.Message);
        }
    }

    private static IResult NoTenant() => Error(403, "no_tenant", "No account is bound to this request.");

    internal static IResult Error(int status, string code, string error)
        => Results.Json(new { error, code }, statusCode: status);

    private sealed class SendBody
    {
        public string? Text { get; set; }
        public string? Note { get; set; }
        public string? Source { get; set; }
        public string? RawText { get; set; }
        /// <summary>Read only to be refused: a client never sends corrections.</summary>
        public JsonElement? Corrections { get; set; }
    }

    private sealed class ReceiveBody
    {
        public List<string>? Ids { get; set; }
        public List<MergeBody>? Merges { get; set; }
        public List<LinkBody>? Links { get; set; }
    }

    private sealed class MergeBody
    {
        public string? Id { get; set; }
        public string? Into { get; set; }
    }

    private sealed class LinkBody
    {
        public string? Id { get; set; }
        public string? Kind { get; set; }
        public string? Ref { get; set; }
    }

    private sealed class BundleBody
    {
        public List<string>? Ids { get; set; }
    }

    private sealed class DecideBody
    {
        public string? Kind { get; set; }
        public string? Session { get; set; }
        public string? Why { get; set; }
    }
}

/// <summary>What the Gateway's history row records about who started a session. The record itself being null means there is
/// no history row yet.</summary>
internal sealed record FactoryInboxRecordedParent(string? ParentSessionId);
