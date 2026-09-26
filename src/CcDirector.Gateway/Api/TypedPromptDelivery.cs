using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Prompts;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Api;

/// <summary>What one drive of a held typed prompt came to, as the driver needs to know it.</summary>
internal enum TypedDriveResult
{
    /// <summary>It reached its end (delivered, or shown back): stop driving it.</summary>
    Finished,
    /// <summary>Still held: drive it again on a later wake-up.</summary>
    Held,
    /// <summary>It is not held any more (resolved, acknowledged or retired meanwhile), or never was: stop driving it.</summary>
    NotHeld,
    /// <summary>Its record could not be read. Written to the log, never driven, never guessed at.</summary>
    Refused,
}

/// <summary>How the prompt route reads the Director's answer to a typed prompt (contract section 7, T2).</summary>
internal enum TypedSendKind
{
    /// <summary>The Director accepted it and the words are in: 200 with its answer.</summary>
    Delivered,
    /// <summary>The Director answered and typed nothing for a reason of its own (busy, a menu, refused): 200 with its answer, as today.</summary>
    AnsweredNotTyped,
    /// <summary>Known not in: the Director refused before touching the session, or the prompt never left the Gateway: 502, as today.</summary>
    NotIn,
    /// <summary>Not final: the Director said delivering, or gave no answer. Held, 202, and the driver asks.</summary>
    Held,
}

/// <summary>What the prompt route read from the Director's answer to a typed prompt.</summary>
internal sealed record TypedSendReading(TypedSendKind Kind, PromptResponse? Body, string? Error, string? DirectorState,
    TypedPromptDecisionFacts DirectorAnswer, bool NeverLeft = false);

/// <summary>
/// TYPED PROMPTS ARE RESOLVED BY THE GATEWAY (Voice Delivery mission, phase 5, contract section 7). Phase 4 case 2f: a typed
/// prompt was answered 200 "delivering" while the Director had in the end refused it and typed nothing; the outcome was
/// only in the Director's log, and a typed prompt carried no delivery id, so nobody could ask. Now:
///
/// - every prompt the prompt route sends carries a delivery id (<see cref="MintDeliveryId"/>), so the Director records it;
/// - the route reads the answer through <see cref="DeliverySendAndAsk.Read"/>, the one reading of a prompt answer
///   (<see cref="ReadSend"/>), and holds anything not final at once - it never asks inline, which would keep the caller
///   waiting another thirty seconds or more;
/// - the Gateway's driver resolves a held one by ASKING ONLY (<see cref="DriveOnceAsync"/>) - typed text is never re-sent.
///
/// A STARVED DIRECTOR ANSWERS LATE OR NOT AT ALL (phase 4: the prompt verb answered in 32 seconds, the question in 61). So
/// a no-answer is never read as "not in", and <c>unknown</c> - which a starved Director gives while the command is still
/// queued - is held once and shown back only when a later wake-up still hears it. Each question is its own tunnel command
/// with its own id, so a late answer to an earlier question can never be read as the answer to a later one. No timeout is
/// raised to fit a slow Director.
/// </summary>
internal sealed class TypedPromptDelivery
{
    // One attempt of one record at a time, across every wake-up (tunnel back, tick, Gateway start). Keyed by tenant and id.
    private static readonly ConcurrentDictionary<string, byte> Running = new(StringComparer.Ordinal);

    private readonly DirectorRegistry _registry;
    private readonly SessionOwnerCache? _owners;
    private readonly Streaming.PushedSessionStore? _pushedSessions;
    private readonly DirectorCommandRouter.SendDirectorCommandAsync? _sendCommand;
    private readonly TimeSpan _streamStale;

    public TypedPromptDelivery(DirectorRegistry registry, SessionOwnerCache? owners, Streaming.PushedSessionStore? pushedSessions,
        DirectorCommandRouter.SendDirectorCommandAsync? sendCommand, TimeSpan streamStale, TimeProvider clock)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _owners = owners;
        _pushedSessions = pushedSessions;
        _sendCommand = sendCommand;
        _streamStale = streamStale;
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>The clock the five-minute rule is judged by: the real one in production, a test's own otherwise.</summary>
    public TimeProvider Clock { get; }

    /// <summary>A fresh delivery id for a typed prompt: a new GUID, the same <c>N</c> spelling as upload ids.</summary>
    public static string MintDeliveryId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Send a typed prompt (which carries its minted delivery id) and read the answer (<see cref="ReadSend"/>). The typed
    /// prompt route's one send: it needs the three outcomes of <see cref="SessionVerbClient.SendPromptAsync"/> kept apart,
    /// because "unanswered" is held and asked about later while "never left" and "refused" are known not in.
    /// </summary>
    public static async Task<TypedSendReading> SendAsync(SessionVerbClient route, string sid, PromptRequest prompt)
        => ReadSend(await route.SendPromptAsync(sid, prompt));

    /// <summary>
    /// Read the prompt verb's answer to a typed prompt through <see cref="DeliverySendAndAsk.Read"/> - the one reading - and
    /// say what the route answers: delivered, answered-not-typed (today's 200), known not in (today's 502), or held.
    /// </summary>
    public static TypedSendReading ReadSend(SessionVerbClient.PromptSendOutcome sent)
    {
        var (kind, error, refusedDuplicate) = DeliverySendAndAsk.Read(sent);
        var body = sent.Body;
        var answer = new TypedPromptDecisionFacts
        {
            Ok = body?.Accepted ?? false,
            State = body?.DeliveryState is { } state ? DeliveryStates.Format(state) : null,
            RefusedDuplicate = refusedDuplicate,
            Reason = kind is null ? Voice.DeliveryDecisions.AskReasonPromptUnanswered : null,
            Error = kind is null ? error : body is { Accepted: false } ? body.DeliveryStateReason ?? body.Error : null,
        };
        return kind switch
        {
            null => new TypedSendReading(TypedSendKind.Held, null, error, DeliverySendAndAsk.NoAnswerState, answer),
            DeliverySendKind.StillDelivering => new TypedSendReading(TypedSendKind.Held, body, error, DeliveryStates.Delivering, answer),
            DeliverySendKind.Delivered when body is null =>
                new TypedSendReading(TypedSendKind.NotIn, null, "the Director accepted the prompt but answered with no body", null, answer),
            DeliverySendKind.Delivered => new TypedSendReading(TypedSendKind.Delivered, body, null, null, answer),
            DeliverySendKind.NotDelivered when body is not null =>
                new TypedSendReading(TypedSendKind.AnsweredNotTyped, body, error, null, answer),
            DeliverySendKind.NotDelivered => new TypedSendReading(TypedSendKind.NotIn, null, error, null, answer,
                NeverLeft: sent.Kind == SessionVerbClient.PromptSendKind.NeverLeftTheGateway),
            _ => throw new InvalidOperationException($"a prompt answer read as {kind}, which a single send cannot come to"),
        };
    }

    /// <summary>
    /// ONE ATTEMPT AT A HELD TYPED PROMPT, for the Gateway's driver to call on every wake-up (the Director's tunnel back,
    /// the tick, the Gateway starting). It writes <c>gateway-drive</c> with <paramref name="trigger"/> and the attempt
    /// number, then ASKS the Director what became of the delivery id - it never sends the text again - and rules:
    ///
    /// - <c>delivered</c>: resolved delivered, the text deleted;
    /// - <c>delivering</c>: held (the Director's own fifteen-minute watch ends it);
    /// - <c>not-delivered</c>: shown back with the text and "Send anyway";
    /// - <c>unknown</c>: held the first time (the command may still be queued in a starved Director), shown back when a
    ///   later ask - at least one wake-up afterwards - still says <c>unknown</c>;
    /// - no answer of any kind (the session cannot be located, its Director is not connected, is too old to be asked, or is
    ///   silent): held, and past five minutes from the sent time ruled could-not-confirm, with no "Send anyway".
    ///
    /// An attempt already running for this record is left to finish; this call then answers <see cref="TypedDriveResult.Held"/>
    /// and does nothing.
    /// </summary>
    public async Task<TypedDriveResult> DriveOnceAsync(TenantId tenant, TypedPromptStore store, string deliveryId, string trigger)
    {
        if (store.Tenant != tenant)
            throw new ArgumentException($"the typed prompt store is bound to {store.Tenant.ToLogString()}, not {tenant.ToLogString()}", nameof(store));
        var id = TypedPromptStore.NormalizeDeliveryId(deliveryId)
            ?? throw new ArgumentException($"'{deliveryId}' is not a delivery id", nameof(deliveryId));
        var key = $"{tenant.Value}:{id}";
        if (!Running.TryAdd(key, 0))
        {
            FileLog.Write($"[TypedPromptDelivery] DriveOnceAsync: deliveryId={id} already being attempted; this wake-up ({trigger}) does nothing");
            return TypedDriveResult.Held;
        }
        try
        {
            return await DriveUnderFlightAsync(tenant, store, id, trigger);
        }
        finally
        {
            Running.TryRemove(key, out _);
        }
    }

    private async Task<TypedDriveResult> DriveUnderFlightAsync(TenantId tenant, TypedPromptStore store, string id, string trigger)
    {
        var read = store.Read(id);
        if (read.Kind == TypedPromptReadKind.Unreadable)
        {
            FileLog.Write($"[TypedPromptDelivery] deliveryId={id} NOT driven: {read.Problem}; left as it is for an operator");
            store.RecordDecision(id, TypedPromptDecisions.GatewayDriveRefused, new TypedPromptDecisionFacts { Error = read.Problem });
            return TypedDriveResult.Refused;
        }
        var record = store.BeginDrive(id, trigger);
        if (record is null) return TypedDriveResult.NotHeld;
        var sid = record.SessionId;
        // Did it ever leave the Gateway? Read from the decision log alone (contract section 10).
        var mayHaveBeenSent = store.MayHaveBeenSentToDirector(id);
        FileLog.Write($"[TypedPromptDelivery] DriveOnceAsync: deliveryId={id} sid={sid} trigger={trigger} attempt={record.DriveAttempts}");

        var (director, session) = await GatewayEndpoints.LocateSessionAsync(_registry, sid, _pushedSessions, _streamStale, tenant, _owners);
        if (director is null || session is null)
        {
            // NOT LOCATED NOW (contract section 8): its Director is stale, frozen or not connected. That is never "session
            // gone" - the Gateway cannot prove the session ended - so it stays held, waiting for the Director.
            store.RecordDecision(id, TypedPromptDecisions.SessionNotFound,
                new TypedPromptDecisionFacts { SessionId = sid, Reason = "the session could not be located; its Director is not connected" });
            return NoAnswer(store, record, mayHaveBeenSent, TypedPromptDecisions.WaitingForDirector, TypedPromptDecisions.WaitingForDirector);
        }
        if (GatewayDictationEndpoint.IsExited(session))
        {
            // PROVABLY ENDED (contract section 9, F4): located, and exited. Shown back with Dismiss - there is no session
            // left to send to - and the text is kept for that.
            store.ResolveSessionEnded(id, session.Status ?? session.ActivityState);
            return TypedDriveResult.Finished;
        }
        if (!mayHaveBeenSent)
            return await SendNeverSentAsync(store, record, new SessionVerbClient(director, _sendCommand));

        var route = new SessionVerbClient(director, _sendCommand);
        store.RecordDecision(id, TypedPromptDecisions.AskedDirector,
            new TypedPromptDecisionFacts { SessionId = sid, Reason = TypedPromptDecisions.AskReasonGatewayDrive });
        var asked = await route.GetDeliveryStateAsync(sid, id);
        var noAnswer = DeliverySendAndAsk.NoAnswerName(asked.Kind);
        store.RecordDecision(id, TypedPromptDecisions.DeliveryStateAnswer, new TypedPromptDecisionFacts
        {
            SessionId = sid,
            State = asked.Answer is { } a ? DeliveryStates.Format(a.State) : null,
            Reason = noAnswer ?? asked.Answer?.Reason,
            Error = noAnswer is null ? null : asked.Detail,
        });
        if (noAnswer is not null)
            return NoAnswer(store, record, mayHaveBeenSent, noAnswer,
                asked.Kind == SessionVerbClient.DeliveryStateAskKind.NeverLeftTheGateway
                    ? TypedPromptDecisions.WaitingForDirector
                    : DeliverySendAndAsk.NoAnswerState);

        switch (asked.Answer!.State)
        {
            case DeliveryState.Delivered:
                store.ResolveDelivered(id);
                return TypedDriveResult.Finished;
            case DeliveryState.Delivering:
                store.StayHeld(id, DeliveryStates.Delivering, countUnknown: false);
                return TypedDriveResult.Held;
            case DeliveryState.NotDelivered:
                store.ResolveNotDelivered(id, TypedPromptDecisions.ReasonDirectorSaidNotDelivered, DeliveryStates.Format(DeliveryState.NotDelivered));
                return TypedDriveResult.Finished;
            case DeliveryState.Unknown:
                if (record.UnknownAnswers == 0)
                {
                    // The first "unknown": a starved Director may still hold the prompt in its queue, unread. Held.
                    store.StayHeld(id, DeliveryStates.Format(DeliveryState.Unknown), countUnknown: true);
                    return TypedDriveResult.Held;
                }
                store.ResolveNotDelivered(id, TypedPromptDecisions.ReasonUnknownTwice, DeliveryStates.Format(DeliveryState.Unknown));
                return TypedDriveResult.Finished;
            default:
                throw new InvalidOperationException($"the Director answered delivery state {asked.Answer.State}, which this Gateway does not know");
        }
    }

    // SEND ONCE (contract section 10, the Tech Lead's ruling): a typed prompt that never left the Gateway is provably not in,
    // so when its session is reachable again within the age limit from the time it arrived, it is sent - once, under its
    // minted delivery id. The sent-to-director line is written BEFORE the send, so a crash or any later wake-up only asks.
    // Past the limit it is shown back with "Send anyway". Every prompt that DID leave the Gateway stays ask-only.
    private async Task<TypedDriveResult> SendNeverSentAsync(TypedPromptStore store, TypedPromptRecord record, SessionVerbClient route)
    {
        var id = record.DeliveryId;
        if (DeliverySendAndAsk.IsPastConfirmLimit(Clock.GetUtcNow().UtcDateTime, record.SentAtUtc, out _))
        {
            store.ResolveNotDelivered(id, TypedPromptDecisions.ReasonNeverSent, null);
            return TypedDriveResult.Finished;
        }
        var unsent = record.Unsent
            ?? throw new InvalidOperationException($"never-sent typed prompt {id} carries no request fields to send it with");
        if (unsent.MenuGuard)
        {
            // The menu guard reads the live screen one hop before the send, on the prompt route; the driver cannot, and
            // typing blind could confirm whatever a picker has highlighted. So the owner gets it back instead.
            store.ResolveNotDelivered(id, TypedPromptDecisions.ReasonMenuGuardNotAtTheDriver, null);
            return TypedDriveResult.Finished;
        }
        if (!store.MarkSending(id)) return TypedDriveResult.NotHeld;
        var reading = await SendAsync(route, record.SessionId, new PromptRequest
        {
            Text = record.Text ?? throw new InvalidOperationException($"never-sent typed prompt {id} has no text"),
            AppendEnter = unsent.AppendEnter,
            AgentDriven = unsent.AgentDriven,
            Surface = unsent.Surface,
            OnlyWhenWaitingForInput = unsent.OnlyWhenWaitingForInput,
            Provenance = unsent.Provenance,
            DeliveryId = id,
        });
        FileLog.Write($"[TypedPromptDelivery] deliveryId={id}: sent once by the Gateway, answer read as {reading.Kind}");
        if (reading.Kind == TypedSendKind.NotIn && reading.NeverLeft)
        {
            // The tunnel was gone between the locate and the send. Nothing reached the Director, but the log now says it
            // may have, so from here it is only asked about - the one rule, read from the log alone.
            store.MarkNeverLeft(id, reading.Error);
            store.StayHeld(id, TypedPromptDecisions.WaitingForDirector, countUnknown: false);
            return TypedDriveResult.Held;
        }
        store.RecordDecision(id, TypedPromptDecisions.DirectorAnswer, reading.DirectorAnswer with { SessionId = record.SessionId });
        switch (reading.Kind)
        {
            case TypedSendKind.Delivered:
                store.ResolveDelivered(id);
                return TypedDriveResult.Finished;
            case TypedSendKind.AnsweredNotTyped:
                store.ResolveNotDelivered(id, reading.Body?.RefusedFor ?? reading.DirectorAnswer.Error ?? "the Director typed nothing",
                    reading.DirectorAnswer.State);
                return TypedDriveResult.Finished;
            case TypedSendKind.NotIn:
                // Refused before the session was touched: "session has exited", or its Director - connected, answering -
                // no longer lists it. The session ended.
                store.ResolveSessionEnded(id, reading.Error ?? "refused");
                return TypedDriveResult.Finished;
            case TypedSendKind.Held:
                store.StayHeld(id, reading.DirectorState!, countUnknown: false);
                return TypedDriveResult.Held;
            default:
                throw new InvalidOperationException($"unknown typed send reading {reading.Kind}");
        }
    }

    // No answer of any kind: never read as "not in". Held within the age limit from the sent time; past it, could not
    // confirm - unless the prompt never left the Gateway, which is provably not in and is shown back with "Send anyway".
    private TypedDriveResult NoAnswer(TypedPromptStore store, TypedPromptRecord record, bool mayHaveBeenSent, string noAnswerKind,
        string heldState)
    {
        if (DeliverySendAndAsk.IsPastConfirmLimit(Clock.GetUtcNow().UtcDateTime, record.SentAtUtc, out var age))
        {
            if (!mayHaveBeenSent)
            {
                store.ResolveNotDelivered(record.DeliveryId, TypedPromptDecisions.ReasonNeverSent, null);
                return TypedDriveResult.Finished;
            }
            store.ResolveUnconfirmed(record.DeliveryId, age, noAnswerKind);
            FileLog.Write($"[TypedPromptDelivery] deliveryId={record.DeliveryId}: {age.TotalSeconds:0}s since it was sent and no answer " +
                $"({noAnswerKind}); ruled unconfirmed");
            return TypedDriveResult.Finished;
        }
        store.StayHeld(record.DeliveryId, heldState, countUnknown: false);
        return TypedDriveResult.Held;
    }

    /// <summary>
    /// The answer <c>GET /sessions/{sid}/prompts/{deliveryId}/outcome</c> gives for a record - the section 5 body shapes, so
    /// a client reads a typed prompt's outcome exactly as it reads a recording's.
    /// </summary>
    public static IResult OutcomeResult(TypedPromptRecord record) => record.State switch
    {
        TypedPromptState.Held => Results.Json(new { delivering = true, directorState = record.DirectorState, deliveryId = record.DeliveryId },
            statusCode: StatusCodes.Status202Accepted),
        TypedPromptState.Delivered => Results.Json(new
        {
            submitted = true, movedOn = false, transcript = record.Text, deliveryId = record.DeliveryId,
        }),
        TypedPromptState.NotDelivered => Results.Json(new
        {
            submitted = false, movedOn = true, reason = "not-delivered", offerSendAnyway = true,
            transcript = record.Text, deliveryId = record.DeliveryId,
        }),
        TypedPromptState.Unconfirmed => Results.Json(new
        {
            submitted = false, movedOn = true, reason = "unconfirmed", offerSendAnyway = false,
            transcript = record.Text, deliveryId = record.DeliveryId,
        }),
        TypedPromptState.SessionEnded => Results.Json(new
        {
            submitted = false, movedOn = true, reason = TypedPromptDecisions.SessionExited, offerSendAnyway = false,
            transcript = record.Text, deliveryId = record.DeliveryId,
        }),
        _ => throw new InvalidOperationException($"typed prompt state {record.State} has no answer"),
    };
}
