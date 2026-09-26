using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Transcription;
using CcDirector.Gateway.Voice;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Api;

/// <summary>What one drive of an owned delivery came to, as the driver needs to know it.</summary>
internal enum DriveResult
{
    /// <summary>The delivery reached its end (delivered, shown back, or left the Gateway's hands): stop driving it.</summary>
    Finished,
    /// <summary>Still held: drive it again later.</summary>
    Held,
    /// <summary>Nothing the Gateway holds is there any more (resolved or acknowledged meanwhile): stop driving it.</summary>
    NotHeld,
    /// <summary>It could not be read. Written to the decision record and the log, never driven, never guessed at.</summary>
    Refused,
}

/// <summary>
/// THE ONE PLACE AN OWNED DELIVERY IS ATTEMPTED (Voice Delivery mission, phase 5), for both of its callers: the complete
/// route when the owner's client first hands a recording over, and the Gateway's driver
/// (<see cref="HeldDeliveryDriver"/>) every time after that. Both run the SAME delivery core
/// (<see cref="GatewayDictationEndpoint.RunCompleteCoreAsync"/>, which already asks first and already applies the
/// five-minute and could-not-confirm rules) through the SAME single-flight, so a driver attempt and a client call never
/// run two attempts of one upload at once. A "Send anyway" the Gateway owns is pressed again through
/// <see cref="ClaimedSendCore"/>, the one piece the prompt route presses it through.
///
/// Every attempt is run from the delivery's DURABLE record - the request fields written when the Gateway took it over -
/// never from anything held in memory, so an attempt after a Gateway restart is exactly the attempt before it.
/// </summary>
internal sealed class DictationDelivery
{
    private readonly DirectorRegistry _registry;
    private readonly SessionOwnerCache? _owners;
    private readonly GatewayTranscriptionService _transcription;
    private readonly TranscribingSessions _transcribingSessions;
    private readonly Streaming.PushedSessionStore? _pushedSessions;
    private readonly DirectorCommandRouter.SendDirectorCommandAsync? _sendCommand;
    private readonly TimeSpan _streamStale;

    public DictationDelivery(DirectorRegistry registry, SessionOwnerCache? owners, GatewayTranscriptionService transcription,
        TranscribingSessions transcribingSessions, Streaming.PushedSessionStore? pushedSessions,
        DirectorCommandRouter.SendDirectorCommandAsync? sendCommand, TimeSpan streamStale, TimeProvider clock)
    {
        _registry = registry;
        _owners = owners;
        _transcription = transcription;
        _transcribingSessions = transcribingSessions;
        _pushedSessions = pushedSessions;
        _sendCommand = sendCommand;
        _streamStale = streamStale;
        Clock = clock;
    }

    /// <summary>The clock every limit is judged by: the real one in production, a test's own otherwise.</summary>
    public TimeProvider Clock { get; }

    /// <summary>
    /// Attempt an owned dictation from its durable request fields, through the single-flight. When an attempt of this
    /// upload is already running, this JOINS it rather than starting a second one. <paramref name="driveTrigger"/> is
    /// null for the client's first complete; for the driver it names what woke it, and the
    /// <see cref="DeliveryDecisions.GatewayDrive"/> line is written only by an attempt this call actually starts.
    /// </summary>
    public Task<DictationOutcome> AttemptAsync(TenantId tenant, VoiceUploadStore store, string uploadId,
        DictationOwnedDelivery owned, string? driveTrigger)
        => GatewayDictationEndpoint.StartOrJoin(tenant, uploadId, owned.SessionId, _transcribingSessions, () =>
        {
            if (driveTrigger is not null)
                store.RecordDecision(uploadId, DeliveryDecisions.GatewayDrive, new DeliveryDecisionFacts
                {
                    SessionId = owned.SessionId,
                    Trigger = driveTrigger,
                    Attempt = store.DriveAttempts(uploadId) + 1,
                });
            return GatewayDictationEndpoint.RunCompleteCoreAsync(uploadId, tenant, RequestFrom(owned), store, _registry, _owners,
                _transcription, _transcribingSessions, owned.DeliverySurface, owned.DeliveryIdentityKind, _pushedSessions,
                _sendCommand, _streamStale, Clock);
        });

    /// <summary>
    /// A repeated complete for a delivery the Gateway already owns (contract section 2): it drives nothing - never sends,
    /// asks or transcribes. An attempt already running is joined and its answer returned; otherwise the record's own
    /// state is answered: the cached outcome when it is resolved, 202 with the last known <c>directorState</c> when held.
    /// </summary>
    public Task<DictationOutcome> AnswerRepeatedCompleteAsync(TenantId tenant, VoiceUploadStore store, string uploadId)
    {
        if (GatewayDictationEndpoint.TryJoin(tenant, uploadId, out var running))
            return running;
        var record = store.Read(uploadId).Record;
        if (record is { State: DictationDeliveryState.Delivered or DictationDeliveryState.Abandoned })
            return Task.FromResult(GatewayDictationEndpoint.TerminalOutcome(record));
        return Task.FromResult(DictationOutcome.StillDelivering(store.LastHeldState(uploadId) ?? DeliverySendAndAsk.RetryingState));
    }

    /// <summary>
    /// The driver's attempt of an owned dictation. Reads the durable record; a record it cannot read is written up and
    /// left (<see cref="DriveResult.Refused"/>); anything not owned and PENDING any more is <see cref="DriveResult.NotHeld"/>.
    /// An attempt already running for this upload (a client's, or an earlier wake's) is left to finish - it is the
    /// attempt, and joining it would drive nothing new.
    /// </summary>
    public async Task<DriveResult> DriveDictationAsync(TenantId tenant, VoiceUploadStore store, string uploadId, string trigger)
    {
        var read = store.Read(uploadId);
        if (read.Refuses)
        {
            WriteRefused(store, uploadId, read.Describe(uploadId));
            return DriveResult.Refused;
        }
        if (read.Record is not { State: DictationDeliveryState.Pending, Owned: { } owned })
            return DriveResult.NotHeld;
        if (GatewayDictationEndpoint.TryJoin(tenant, uploadId, out _))
            return DriveResult.Held;
        var outcome = await AttemptAsync(tenant, store, uploadId, owned, trigger);
        return outcome.IsHeld ? DriveResult.Held : DriveResult.Finished;
    }

    /// <summary>
    /// The driver's press of a "Send anyway" the Gateway owns (contract section 4): the prompt as it was sent, to the
    /// session it was sent to, asking first, through <see cref="ClaimedSendCore"/> - and when its Director is not
    /// connected, held within the limit from the first claim and ruled could-not-confirm past it.
    /// </summary>
    public async Task<DriveResult> DriveSendAnywayAsync(TenantId tenant, VoiceUploadStore store, string uploadId, string trigger)
    {
        SendAnywayDelivery? held;
        try
        {
            held = store.ReadSendAnyway(uploadId);
        }
        catch (Exception ex)
        {
            WriteRefused(store, uploadId, $"its Send anyway delivery could not be read: {ex.Message}");
            return DriveResult.Refused;
        }
        if (held is not { Outcome: null })
            return DriveResult.NotHeld;

        var deliveryId = VoiceUploadStore.NormalizeUploadId(uploadId)
            ?? throw new InvalidOperationException($"upload id '{uploadId}' is not a GUID, yet its Send anyway was read");
        store.RecordDecision(deliveryId, DeliveryDecisions.GatewayDrive, new DeliveryDecisionFacts
        {
            SessionId = held.SessionId,
            Trigger = trigger,
            Attempt = store.DriveAttempts(deliveryId) + 1,
            Reason = DeliveryDecisions.OwnedSendAnyway,
        });

        var (director, session) = await GatewayEndpoints.LocateSessionAsync(
            _registry, held.SessionId, _pushedSessions, _streamStale, tenant, _owners);
        ClaimAttempt attempt;
        if (director is null || session is null)
        {
            store.RecordDecision(deliveryId, DeliveryDecisions.SessionNotFound,
                new DeliveryDecisionFacts { SessionId = held.SessionId, StatusCode = StatusCodes.Status404NotFound });
            attempt = ClaimedSendCore.DirectorNotConnected(store, held.SessionId, deliveryId, Clock);
        }
        else
        {
            attempt = await ClaimedSendCore.AttemptAsync(new SessionVerbClient(director, _sendCommand), held.SessionId,
                new PromptRequest
                {
                    Text = held.Text,
                    AppendEnter = true,
                    Surface = held.Surface ?? "unknown",
                    DeliveryId = deliveryId,
                    Provenance = held.Provenance,
                },
                store, deliveryId, session.ActivityState, Clock, gatewayDriven: true);
        }

        var settled = attempt.Kind switch
        {
            ClaimAttemptKind.Delivered or ClaimAttemptKind.AlreadyDelivered => SendAnywayOutcomes.Delivered,
            ClaimAttemptKind.Unconfirmed => SendAnywayOutcomes.Unconfirmed,
            ClaimAttemptKind.TooOld => SendAnywayOutcomes.TooOld,
            ClaimAttemptKind.Held => null,
            _ => throw new InvalidOperationException($"the Gateway's own Send anyway came to {attempt.Kind}, which only an owner's press can"),
        };
        if (settled is null) return DriveResult.Held;
        store.ResolveSendAnyway(deliveryId, settled, Clock.GetUtcNow().UtcDateTime);
        if (settled == SendAnywayOutcomes.Delivered)
            store.RecordDecision(deliveryId, DeliveryDecisions.Delivered, new DeliveryDecisionFacts
            {
                SessionId = held.SessionId,
                Reason = DeliveryDecisions.OwnedSendAnyway,
                Characters = held.Text.Length,
            });
        FileLog.Write($"[DictationDelivery] Send anyway of upload {deliveryId} settled by the Gateway: {settled}");
        return DriveResult.Finished;
    }

    // "I could not read what I own" is written where it will be read afterwards, and the delivery is left alone.
    private static void WriteRefused(VoiceUploadStore store, string uploadId, string problem)
    {
        FileLog.Write($"[DictationDelivery] upload {uploadId} NOT driven: {problem}; it is left as it is for an operator");
        store.RecordDecision(uploadId, DeliveryDecisions.GatewayDriveRefused, new DeliveryDecisionFacts { Error = problem });
    }

    private static DictationCompleteRequest RequestFrom(DictationOwnedDelivery owned) => new()
    {
        SessionId = owned.SessionId,
        TotalChunks = owned.TotalChunks,
        Mime = owned.Mime,
        Ext = owned.Ext,
        Before = owned.Before,
        After = owned.After,
        Prefix = owned.Prefix,
        SentAtUtc = DateTime.SpecifyKind(owned.SentAtUtc, DateTimeKind.Utc),
        ClientRecordedMs = owned.ClientRecordedMs,
        ClientDecodedSeconds = owned.ClientDecodedSeconds,
        ClientSourceBytes = owned.ClientSourceBytes,
        ClientSurface = owned.ClientSurface,
    };
}
