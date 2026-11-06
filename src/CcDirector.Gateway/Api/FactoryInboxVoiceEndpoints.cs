using System.Collections.Concurrent;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Inbox;
using CcDirector.Gateway.Transcription;
using CcDirector.Gateway.Voice;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// A SPOKEN FACTORY INBOX MESSAGE (Factory Inbox mission, phase 4): the owner's recording, sent through the same durable
/// upload a session's dictation uses, with a factory inbox as the target instead of a session.
///
///   POST /gateway/factory/{factory}/inbox/voice                     Idempotency-Key = the recording's own id
///                                                                   -> 200 { upload_id } | 200 { upload_id, terminal, ... }
///   PUT  /dictation/{uploadId}/chunk/{i}                            the dictation chunk leg, unchanged
///   POST /gateway/factory/{factory}/inbox/voice/{uploadId}/complete { totalChunks, mime, ext, note, sentAtUtc }
///                                                                   -> 201 { message } | 200 { terminal, delivered, ... }
///                                                                      | 409 { missing } | 4xx/5xx { error, code }
///   POST /dictation/{uploadId}/ack                                  the dictation acknowledgement, unchanged
///
/// THE STAGING IS THE DICTATION'S, PER ACCOUNT. The register opens the record in the request's own partition of the
/// dictation staging (the one <see cref="DictationTenantGate"/> hands the chunk leg), so the chunks, the SHA checks, the
/// resume from the missing chunks and the terminal tombstone that answers a repeated complete are all the existing ones.
/// The record names its target as <c>factory-inbox:&lt;factory&gt;</c> where a dictation names a session id: it is never
/// a session, so nothing that locates a session ever finds it, and the Gateway never takes it over as a held delivery -
/// there is no Director between the words and the factory inbox, so there is nothing to hold.
///
/// THE VERBATIM RULE (CLAUDE.md). The recording is transcribed WITHOUT the dictionary stage, so what reaches the store is
/// the speech model's output (after the evidence gate's recorded removals, which the transcription service keeps). The
/// store applies the account's own hand-listed corrections and keeps the raw text beside the result
/// (<see cref="FactoryInboxStore.SendAsync"/>): a spoken message is the raw transcript plus only listed corrections. The
/// client never sends words for a recording, and nothing here edits them.
///
/// ONCE. A complete is single-flighted per account and upload id in this process, and a complete for a record that is
/// already DELIVERED answers what it delivered and logs nothing again - so a retried complete never leaves the message
/// twice.
///
/// WHO MAY: exactly who may type one (<see cref="FactoryInboxEndpoints.RefuseOwnerSend"/>) - the owner's phone or
/// Cockpit, into a registered factory that is not archived and names a Boss. Refused in the same words.
/// </summary>
internal static class FactoryInboxVoiceEndpoints
{
    /// <summary>What a recording's record names as its target, where a dictation names a session id.</summary>
    internal const string TargetPrefix = "factory-inbox:";

    /// <summary>Turn a recording into the speech model's words, with no dictionary stage.</summary>
    internal delegate Task<GatewayTranscriptionResult> TranscribeRaw(TenantId tenant, byte[] audio, string fileName, string mime,
        CancellationToken ct);

    /// <summary>What the routes read. Every function is required, so a wiring line that forgot one fails to compile.</summary>
    /// <param name="OpenUploads">This request's own partition of the dictation staging - the one the chunk leg opens - or
    /// null when no account is bound to the request.</param>
    /// <param name="Wakes">Each factory's wake row (phase 2): why its messages wait, put on the 201 exactly as the typed
    /// route puts it.</param>
    /// <param name="MessageSent">Tells the wake a message was logged (<see cref="FactoryInboxWake.MessageSent"/>), with the
    /// time the owner pressed Send, so a spoken message opens the factory's gather window exactly as a typed one does. The
    /// route never waits for it.</param>
    internal sealed record Parts(
        FactoryInboxStore Store,
        Func<HttpContext, TenantId?> ResolveTenant,
        Func<TenantId, IReadOnlyList<RegisteredFactoryDto>> Factories,
        Func<HttpContext, VoiceUploadStore?> OpenUploads,
        TranscribeRaw Transcribe,
        Func<DateTime> NowUtc,
        FactoryInboxWakeStore Wakes,
        Action<TenantId, string, DateTime> MessageSent);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // One complete at a time per account and upload id, in this process. The terminal tombstone is the durable half:
    // a complete that arrives after the first finished reads DELIVERED and sends nothing.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Completing = new(StringComparer.Ordinal);

    public static void Map(IEndpointRouteBuilder app, Parts parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        FileLog.Write("[FactoryInboxVoiceEndpoints] mapping the spoken factory inbox routes (register, complete)");

        app.MapPost(FactoryInboxEndpoints.OwnerPrefix + "/{factory}/inbox/voice", (string factory, HttpContext ctx) =>
        {
            FileLog.Write($"[FactoryInboxVoiceEndpoints] POST {FactoryInboxEndpoints.OwnerPrefix}/{factory}/inbox/voice: identity={Util.AuthMiddleware.IdentityKind(ctx)}");
            if (Open(parts, ctx, factory, out var opened) is { } refused) return refused;
            var (_, owner, uploads) = opened!;
            var key = ctx.Request.Headers["Idempotency-Key"].ToString();
            if (VoiceUploadStore.NormalizeUploadId(key) is null)
                return FactoryInboxEndpoints.Error(400, "bad_upload_id",
                    "A recording is registered under its own id: send it as the Idempotency-Key header, a GUID.");

            var open = uploads.OpenPending(key, TargetPrefix + owner.Factory.Factory);
            if (open.Before.Refuses)
            {
                FileLog.Write($"[FactoryInboxVoiceEndpoints] register REFUSED: {open.Before.Describe(open.UploadId)}");
                return RecordRefused(open.Before, open.UploadId);
            }
            if (!open.Opened)
            {
                FileLog.Write($"[FactoryInboxVoiceEndpoints] register uploadId={open.UploadId}: already {open.Before.Record!.State}");
                return Terminal(open.UploadId, open.Before.Record!);
            }
            FileLog.Write($"[FactoryInboxVoiceEndpoints] registered uploadId={open.UploadId} factory={owner.Factory.Factory}");
            return Results.Json(new { upload_id = open.UploadId });
        });

        app.MapPost(FactoryInboxEndpoints.OwnerPrefix + "/{factory}/inbox/voice/{uploadId}/complete",
            async (string factory, string uploadId, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[FactoryInboxVoiceEndpoints] POST .../{factory}/inbox/voice/{uploadId}/complete: identity={Util.AuthMiddleware.IdentityKind(ctx)}");
            if (Open(parts, ctx, factory, out var opened) is { } refused) return refused;
            var (tenant, owner, uploads) = opened!;
            if (VoiceUploadStore.NormalizeUploadId(uploadId) is not { } uid)
                return FactoryInboxEndpoints.Error(404, "upload_not_found", $"There is no recording {uploadId}.");

            CompleteBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<CompleteBody>(ctx.Request.Body, JsonOpts, ct);
            }
            catch (JsonException ex)
            {
                return FactoryInboxEndpoints.Error(400, "bad_request_body", $"The body could not be read as JavaScript Object Notation: {ex.Message}");
            }
            if (body is null || body.TotalChunks <= 0)
                return FactoryInboxEndpoints.Error(400, "bad_request_body", "The body must say how many chunks the recording has: {\"totalChunks\": 3}.");
            // The time the owner pressed Send, as the session dictation route requires it: a held recording that goes
            // through the next day is still dated by the press, so its trail, the summary's window and the stuck rule
            // all measure from the moment the owner spoke, never from when transcription finally worked.
            if (body.SentAtUtc is not { Kind: DateTimeKind.Utc } sentAtUtc)
                return FactoryInboxEndpoints.Error(400, "sent_at_required", GatewayDictationEndpoint.SentAtUtcRequired);
            var note = string.IsNullOrWhiteSpace(body.Note) ? FactoryInboxWords.Notes.None : body.Note.Trim().ToLowerInvariant();
            if (!FactoryInboxWords.Notes.All.Contains(note))
                return FactoryInboxEndpoints.Error(400, "bad_note", $"The note must be one of {string.Join(", ", FactoryInboxWords.Notes.All)}, not '{body.Note}'.");

            var gate = Completing.GetOrAdd(tenant.Value + "|" + uid, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                return await CompleteAsync(parts, tenant, owner, uploads, uid, body, note, sentAtUtc, ct);
            }
            finally
            {
                gate.Release();
            }
        });
    }

    private static async Task<IResult> CompleteAsync(Parts parts, TenantId tenant, FactoryInboxEndpoints.OwnerSend owner,
        VoiceUploadStore uploads, string uid, CompleteBody body, string note, DateTime sentAtUtc, CancellationToken ct)
    {
        var read = uploads.Read(uid);
        if (read.Refuses)
        {
            FileLog.Write($"[FactoryInboxVoiceEndpoints] complete REFUSED: {read.Describe(uid)}");
            return RecordRefused(read, uid);
        }
        if (read.Record is null)
            return FactoryInboxEndpoints.Error(404, "upload_not_found", $"There is no recording {uid}. Register it first.");
        if (read.Record.State is DictationDeliveryState.Delivered or DictationDeliveryState.Abandoned)
        {
            FileLog.Write($"[FactoryInboxVoiceEndpoints] complete uploadId={uid}: already {read.Record.State}; nothing sent again");
            return Terminal(uid, read.Record);
        }
        var target = TargetPrefix + owner.Factory.Factory;
        if (!string.Equals(read.Record.SessionId, target, StringComparison.Ordinal))
        {
            FileLog.Write($"[FactoryInboxVoiceEndpoints] complete REFUSED uploadId={uid}: its record names '{read.Record.SessionId}', not '{target}'");
            return FactoryInboxEndpoints.Error(409, "upload_for_another_target",
                $"Recording {uid} was registered for somewhere else, not the inbox of '{owner.Factory.Factory}'.");
        }

        var missing = uploads.MissingChunks(uid, body.TotalChunks);
        if (missing is null)
            return FactoryInboxEndpoints.Error(404, "upload_not_found", $"There is no recording {uid}. Register it first.");
        if (missing.Count > 0)
        {
            FileLog.Write($"[FactoryInboxVoiceEndpoints] complete uploadId={uid}: {missing.Count} chunks missing");
            return Results.Json(new { missing }, statusCode: StatusCodes.Status409Conflict);
        }

        var assembled = await uploads.AssembleAsync(uid, body.TotalChunks, ct);
        if (assembled.Status == "incomplete")
            return Results.Json(new { missing = assembled.Missing }, statusCode: StatusCodes.Status409Conflict);
        if (assembled.Status != "ok" || assembled.Audio is null)
            return FactoryInboxEndpoints.Error(404, "upload_not_found", $"There is no recording {uid}. Register it first.");
        if (assembled.Audio.Length == 0)
        {
            uploads.ResolveEmptyRecording(uid);
            return FactoryInboxEndpoints.Error(422, "empty_recording", "The recording is empty, so nothing was left in the factory inbox. Record it again.");
        }

        var ext = string.IsNullOrWhiteSpace(body.Ext) ? "webm" : body.Ext.Trim().TrimStart('.');
        var mime = string.IsNullOrWhiteSpace(body.Mime) ? "audio/webm" : body.Mime.Trim();
        var result = await parts.Transcribe(tenant, assembled.Audio, "audio." + ext, mime, ct);
        switch (result.Outcome)
        {
            case TranscriptionOutcome.Ok:
                break;
            case TranscriptionOutcome.OutOfCredits:
                FileLog.Write($"[FactoryInboxVoiceEndpoints] complete uploadId={uid}: out of credits ({result.Code}); recording kept");
                return Results.Json(new { error = result.Error ?? "Transcription is not available on this account.", code = result.Code ?? "out_of_credits" },
                    statusCode: StatusCodes.Status402PaymentRequired);
            case TranscriptionOutcome.PermanentError:
                FileLog.Write($"[FactoryInboxVoiceEndpoints] complete uploadId={uid}: permanent ({result.Code}) {result.Error}");
                return FactoryInboxEndpoints.Error(422, result.Code ?? "transcription_permanent",
                    $"The recording cannot be transcribed: {result.Error ?? result.Code}. Nothing was left in the factory inbox.");
            default:
                // A key that is not set, a provider that failed: trying again can work, and the recording stays on the
                // device and on the Gateway until it does.
                FileLog.Write($"[FactoryInboxVoiceEndpoints] complete uploadId={uid}: retryable {result.Outcome} {result.Error}");
                return Results.Json(new { error = $"The recording could not be transcribed just now ({result.Error ?? result.Outcome.ToString()}). It is kept, and sending it will retry by itself.", code = "transcription_failed", retryable = true },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var raw = (result.Text ?? "").Trim();
        if (raw.Length == 0)
        {
            // Nothing was said: a final answer, so a retry does not pay for it again.
            uploads.MarkDelivered(uid, submitted: false, movedOn: false, transcript: "");
            FileLog.Write($"[FactoryInboxVoiceEndpoints] complete uploadId={uid}: nothing was said; nothing left");
            return FactoryInboxEndpoints.Error(422, "message_empty", "Nothing was heard in the recording, so nothing was left in the factory inbox.");
        }

        Data.Entities.FactoryInboxMessageEntity message;
        try
        {
            message = await parts.Store.SendAsync(tenant, new FactoryInboxSendInput(
                owner.Factory.Factory, Text: null, Note: note, Source: FactoryInboxWords.Sources.Spoken, Surface: owner.Surface,
                SentBy: owner.Device.DeviceId, NowUtc: sentAtUtc, RawText: raw), ct);
        }
        catch (FactoryInboxRefusedException ex)
        {
            FileLog.Write($"[FactoryInboxVoiceEndpoints] complete uploadId={uid} refused ({ex.Code}): {ex.Message}");
            return FactoryInboxEndpoints.Error(ex.Status, ex.Code, ex.Message);
        }
        // The tombstone carries the words left, so a repeated complete answers them and sends nothing.
        uploads.MarkDelivered(uid, submitted: true, movedOn: false, transcript: message.Text);
        FileLog.Write($"[FactoryInboxVoiceEndpoints] complete uploadId={uid}: left message {message.Id:D} in {message.Factory}, chars={message.Text.Length}, sentAt={sentAtUtc:O}, completedAt={parts.NowUtc():O}");
        // THE WRITE IS THE EVENT (phase 2), as on the typed route: the wake looks at this factory when the gather window,
        // measured from the Send press, closes.
        parts.MessageSent(tenant, message.Factory, message.SentAtUtc);
        return Results.Json(new { message = FactoryInboxFold.Message(message, null, null,
            FactoryInboxFold.WaitingOf(parts.Wakes.Find(tenant, message.Factory))) }, statusCode: StatusCodes.Status201Created);
    }

    private sealed record Opened(TenantId Tenant, FactoryInboxEndpoints.OwnerSend Owner, VoiceUploadStore Uploads);

    private static IResult? Open(Parts parts, HttpContext ctx, string factory, out Opened? opened)
    {
        opened = null;
        if (parts.ResolveTenant(ctx) is not { } tenant)
            return FactoryInboxEndpoints.Error(403, "no_tenant", "No account is bound to this request.");
        if (FactoryInboxEndpoints.RefuseOwnerSend(ctx, tenant, factory, parts.Factories, out var owner) is { } refused) return refused;
        if (parts.OpenUploads(ctx) is not { } uploads)
            return FactoryInboxEndpoints.Error(403, "no_tenant", "No account is bound to this request.");
        opened = new Opened(tenant, owner!, uploads);
        return null;
    }

    /// <summary>What a recording that already has its final answer says: what was left, or that nothing was.</summary>
    private static IResult Terminal(string uploadId, DictationDeliveryRecord record) => Results.Json(new
    {
        upload_id = uploadId,
        terminal = true,
        delivered = record.State == DictationDeliveryState.Delivered && record.Submitted,
        transcript = record.Transcript,
        line = record.State == DictationDeliveryState.Delivered && record.Submitted
            ? "This recording was already left in the factory inbox."
            : "This recording was already dealt with; nothing was left from it.",
    });

    private static IResult RecordRefused(DictationRecordRead read, string uploadId)
        => Results.Json(new
        {
            error = $"The Gateway cannot read what it recorded about recording {uploadId}, so it will not send it again: {read.Problem ?? read.Kind.ToString()}.",
            code = "recording_record_unreadable",
            record = read.Kind.ToString(),
        }, statusCode: StatusCodes.Status409Conflict);

    private sealed class CompleteBody
    {
        public int TotalChunks { get; set; }
        public string? Mime { get; set; }
        public string? Ext { get; set; }
        public string? Note { get; set; }
        public DateTime? SentAtUtc { get; set; }
    }
}
