using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Voice;

/// <summary>
/// ONCE THE GATEWAY HOLDS THE WORDS, THE GATEWAY DRIVES THE DELIVERY (Voice Delivery mission, phase 5).
///
/// Phase 4 found a recording held as "still delivering" that nothing retried for 7 minutes 44 seconds, because the
/// only retry lived in a Cockpit tab the browser had frozen in the background; when it finally ran the recording was
/// too old and was shown back - never doubled, never delivered. So the moment a complete call is taken over, this store
/// keeps, on the upload's own durable record, everything needed to finish the delivery with no client at all, and the
/// Gateway's driver (<c>HeldDeliveryDriver</c>) finishes it: when the Director's tunnel comes back, on a steady tick,
/// and when the Gateway starts. Nothing here lives only in memory, so a Gateway restart forgets nothing.
///
/// Two kinds of delivery are owned this way: a dictation (its request fields, on the PENDING record as
/// <see cref="DictationOwnedDelivery"/>), and a "Send anyway" of one that was answered "still delivering" (its prompt,
/// in the upload's <c>send-anyway.json</c> as <see cref="SendAnywayDelivery"/> - beside the record rather than on it,
/// because by the time "Send anyway" is pressed the record is resolved or acknowledged, and a resolved record is not
/// something this delivery may rewrite).
/// </summary>
public sealed partial class VoiceUploadStore
{
    private const string SendAnywayFileName = "send-anyway.json";
    private static string SendAnywayPath(string dir) => Path.Combine(dir, SendAnywayFileName);

    /// <summary>
    /// Which of chunks 0..<paramref name="totalChunks"/>-1 are not staged - missing or empty - measured exactly as
    /// <see cref="AssembleAsync"/>'s completeness gate measures them, without reading a byte of audio. Null when the
    /// upload is not staged here at all (never registered, or acknowledged). The complete call asks this BEFORE it
    /// looks the session up, so a recording whose Director is offline at its first complete can still be owned.
    /// </summary>
    public IReadOnlyList<int>? MissingChunks(string uploadId, int totalChunks)
    {
        var uid = NormalizeId(uploadId) ?? throw new InvalidOperationException("invalid upload id");
        if (totalChunks <= 0) throw new InvalidOperationException("totalChunks must be > 0");
        var dir = DirFor(uid);
        if (!Directory.Exists(dir) || IsAcknowledgedDir(dir)) return null;
        var missing = new List<int>();
        for (var i = 0; i < totalChunks; i++)
        {
            var info = new FileInfo(ChunkPath(dir, i));
            if (!info.Exists || info.Length == 0) missing.Add(i);
        }
        return missing;
    }

    /// <summary>
    /// Take a dictation's delivery over: write <paramref name="owned"/> onto its PENDING record and the
    /// <see cref="DeliveryDecisions.GatewayOwnsDelivery"/> line, once, under the upload's gate. A record the Gateway
    /// already owns is left exactly as it is and answered <see cref="DictationOwnership.AlreadyOwned"/>, so a repeated
    /// complete changes nothing. Anything but a readable PENDING record is not taken over
    /// (<see cref="DictationOwnership.NotPending"/>, with the read it was decided on).
    /// </summary>
    public DictationOwnershipOutcome TakeOwnership(string uploadId, DictationOwnedDelivery owned)
    {
        ArgumentNullException.ThrowIfNull(owned);
        var uid = NormalizeId(uploadId) ?? throw new InvalidOperationException("invalid upload id");
        return WithRecordLock(uid, () =>
        {
            var read = Read(uid);
            if (read.Record is not { State: DictationDeliveryState.Pending } record)
                return new DictationOwnershipOutcome(DictationOwnership.NotPending, read);
            if (record.Owned is not null)
                return new DictationOwnershipOutcome(DictationOwnership.AlreadyOwned, read);
            var dir = DirFor(uid);
            WriteRecordMarker(dir, record with { Owned = owned });
            AppendDecisionLine(dir, uid, DeliveryDecisions.GatewayOwnsDelivery, new DeliveryDecisionFacts
            {
                SessionId = owned.SessionId,
                TotalChunks = owned.TotalChunks,
                SentAtUtc = owned.SentAtUtc,
            });
            FileLog.Write($"[VoiceUploadStore] TakeOwnership: uploadId={uid} session={owned.SessionId}; the Gateway drives this delivery now");
            return new DictationOwnershipOutcome(DictationOwnership.Taken, Read(uid));
        });
    }

    /// <summary>
    /// Remember on an owned dictation's PENDING record which Director held its session when an attempt located it
    /// (contract section 9, F4). Written the moment a locate succeeds, so the record - not the in-memory owner
    /// cache, which any roster read may prune - is the durable answer to "which Director is its Director?" when a
    /// later attempt must decide whether the session has provably ended. No-op when the record is not an owned
    /// PENDING one or already remembers this Director.
    /// </summary>
    public bool RememberOwningDirector(string uploadId, string directorId)
    {
        if (string.IsNullOrWhiteSpace(directorId)) return false;
        var uid = NormalizeId(uploadId) ?? throw new InvalidOperationException("invalid upload id");
        return WithRecordLock(uid, () =>
        {
            var read = Read(uid);
            if (read.Record is not { State: DictationDeliveryState.Pending, Owned: { } owned } record)
                return false;
            if (string.Equals(owned.DirectorId, directorId, StringComparison.Ordinal)) return false;
            WriteRecordMarker(DirFor(uid), record with { Owned = owned with { DirectorId = directorId } });
            FileLog.Write($"[VoiceUploadStore] RememberOwningDirector: uploadId={uid} director={directorId}");
            return true;
        });
    }

    /// <summary>
    /// The <c>directorState</c> a held delivery was last answered with: the state on its newest
    /// <see cref="DeliveryDecisions.StillDelivering"/> line, or null when it has none yet. A repeated complete and the
    /// outcome read answer this, so they report the Gateway's own last word rather than drive anything to find one.
    /// </summary>
    public string? LastHeldState(string uploadId)
    {
        var uid = NormalizeId(uploadId);
        if (uid is null) return null;
        return DecisionLinesOf(uid).LastOrDefault(l => l.Decision == DeliveryDecisions.StillDelivering)?.Facts?.State;
    }

    /// <summary>How many times the Gateway's driver has attempted this delivery: its <see cref="DeliveryDecisions.GatewayDrive"/>
    /// lines. Read from the durable log, so the attempt number carries on across a Gateway restart.</summary>
    public int DriveAttempts(string uploadId)
    {
        var uid = NormalizeId(uploadId);
        if (uid is null) return 0;
        return DecisionLinesOf(uid).Count(l => l.Decision == DeliveryDecisions.GatewayDrive);
    }

    /// <summary>
    /// Take a "Send anyway" over once it was answered "still delivering" (Voice Delivery phase 5, contract section 4):
    /// write what must be delivered to the upload's <c>send-anyway.json</c> and the
    /// <see cref="DeliveryDecisions.GatewayOwnsDelivery"/> line with the reason <see cref="DeliveryDecisions.OwnedSendAnyway"/>.
    /// Written only where the upload already has a directory in this partition and its record is not another
    /// tenant's; returns false, writing nothing, otherwise.
    /// </summary>
    public bool TakeSendAnywayOwnership(string uploadId, SendAnywayDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        var uid = NormalizeId(uploadId) ?? throw new InvalidOperationException("invalid upload id");
        return WithRecordLock(uid, () =>
        {
            var dir = DirFor(uid);
            if (!Directory.Exists(dir)) return false;
            var read = ReadRecordFile(RecordPath(dir));
            if (read.Kind == DictationRecordReadKind.Present && !BelongsHere(read.Record!)) return false;
            WriteSendAnyway(dir, delivery);
            AppendDecisionLine(dir, uid, DeliveryDecisions.GatewayOwnsDelivery, new DeliveryDecisionFacts
            {
                SessionId = delivery.SessionId,
                Reason = DeliveryDecisions.OwnedSendAnyway,
                Characters = delivery.Text.Length,
            });
            FileLog.Write($"[VoiceUploadStore] TakeSendAnywayOwnership: uploadId={uid} session={delivery.SessionId} chars={delivery.Text.Length}");
            return true;
        });
    }

    /// <summary>
    /// The "Send anyway" delivery of this upload, when one was taken over and not yet acknowledged away. Null when
    /// there is none. A file that is there but cannot be read THROWS rather than answering null: "I could not read what
    /// I own" is not "I own nothing", and the driver says so in the decision record and leaves it.
    /// </summary>
    public SendAnywayDelivery? ReadSendAnyway(string uploadId)
    {
        var uid = NormalizeId(uploadId);
        if (uid is null) return null;
        return WithRecordLock(uid, () =>
        {
            var dir = DirFor(uid);
            if (!Directory.Exists(dir)) return null;
            var read = ReadRecordFile(RecordPath(dir));
            if (read.Kind == DictationRecordReadKind.Present && !BelongsHere(read.Record!)) return null;
            return ReadSendAnywayFile(SendAnywayPath(dir));
        });
    }

    /// <summary>
    /// Settle this upload's "Send anyway" delivery with its final <paramref name="outcome"/>
    /// (<see cref="SendAnywayOutcomes"/>), keeping the words so the outcome read can hand them back. The words leave
    /// with the acknowledgement, as every kept transcript does. False when there is no held delivery to settle.
    /// </summary>
    public bool ResolveSendAnyway(string uploadId, string outcome, DateTime resolvedAtUtc)
    {
        var uid = NormalizeId(uploadId) ?? throw new InvalidOperationException("invalid upload id");
        return WithRecordLock(uid, () =>
        {
            var dir = DirFor(uid);
            if (!Directory.Exists(dir)) return false;
            var held = ReadSendAnywayFile(SendAnywayPath(dir));
            if (held is not { Outcome: null }) return false;
            WriteSendAnyway(dir, held with { Outcome = outcome, ResolvedAtUtc = resolvedAtUtc });
            FileLog.Write($"[VoiceUploadStore] ResolveSendAnyway: uploadId={uid} outcome={outcome}");
            return true;
        });
    }

    /// <summary>
    /// Forget a SETTLED "Send anyway" delivery so a new press of the same recording starts afresh. A held one is never
    /// forgotten here - it is the Gateway's to finish. Returns true when a settled delivery was removed.
    /// </summary>
    public bool ForgetSettledSendAnyway(string uploadId)
    {
        var uid = NormalizeId(uploadId);
        if (uid is null) return false;
        return WithRecordLock(uid, () =>
        {
            var dir = DirFor(uid);
            var path = SendAnywayPath(dir);
            if (ReadSendAnywayFile(path) is not { Outcome: not null }) return false;
            File.Delete(path);
            FileLog.Write($"[VoiceUploadStore] ForgetSettledSendAnyway: uploadId={uid}; a new Send anyway starts afresh");
            return true;
        });
    }

    /// <summary>
    /// Every delivery in this partition the Gateway owns and has not finished: a PENDING dictation record carrying
    /// <see cref="DictationDeliveryRecord.Owned"/>, and a held "Send anyway". Read from disk, which is what lets a
    /// Gateway that just started pick up every delivery it held before it stopped.
    ///
    /// A delivery that cannot be read is NOT skipped silently and NOT guessed at: it is returned with
    /// <see cref="HeldDelivery.Problem"/> set, so the driver writes that it could not read it and leaves it alone.
    /// </summary>
    public IReadOnlyList<HeldDelivery> HeldDeliveries()
    {
        var found = new List<HeldDelivery>();
        if (!Directory.Exists(_root)) return found;
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!IsCanonicalUploadDirName(name)) continue;
            WithRecordLock(name, () =>
            {
                var read = ReadRecordFile(RecordPath(dir));
                if (read.Kind == DictationRecordReadKind.Present && !BelongsHere(read.Record!)) return 0;
                if (read.Record is { State: DictationDeliveryState.Pending, Owned: { } owned })
                    found.Add(new HeldDelivery(name, HeldDeliveryKind.Dictation, owned.SessionId, null));
                else if (read.Kind is DictationRecordReadKind.Malformed or DictationRecordReadKind.Unreadable)
                    // A record nobody can read may be one this Gateway owns. Named, never driven, never guessed at.
                    found.Add(new HeldDelivery(name, HeldDeliveryKind.Dictation, null, read.Describe(name)));
                if (!File.Exists(SendAnywayPath(dir))) return 0;
                try
                {
                    if (ReadSendAnywayFile(SendAnywayPath(dir)) is { Outcome: null } held)
                        found.Add(new HeldDelivery(name, HeldDeliveryKind.SendAnyway, held.SessionId, null));
                }
                catch (Exception ex)
                {
                    found.Add(new HeldDelivery(name, HeldDeliveryKind.SendAnyway, null, $"send-anyway.json could not be read: {ex.Message}"));
                }
                return 0;
            });
        }
        return found;
    }

    private static SendAnywayDelivery? ReadSendAnywayFile(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        return JsonSerializer.Deserialize<SendAnywayDelivery>(text, RecordJson)
            ?? throw new InvalidOperationException($"{path} holds JSON null, not a Send anyway delivery");
    }

    private static void WriteSendAnyway(string dir, SendAnywayDelivery delivery)
    {
        var path = SendAnywayPath(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(delivery, RecordJson));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>
/// Everything the Gateway needs to finish a dictation's delivery with no client at all (Voice Delivery phase 5,
/// contract section 1): the complete call's own fields, as the first owned complete carried them, plus the delivery
/// surface and credential kind that call was authenticated with. Every later attempt - the driver's, after a tunnel
/// comes back, after a restart - is run from exactly these, so it cannot deliver anything the owner did not send.
/// </summary>
/// <param name="DirectorId">The Director that held the session when an attempt last located it, remembered on the
/// record the moment a locate succeeds. Null until then. It is what lets a LATER attempt prove the session has ENDED
/// (contract section 9, F4): "its Director is connected and fresh and no longer lists the session" needs its Director,
/// and the in-memory owner cache can be pruned by any roster read in between - the record cannot.</param>
public sealed record DictationOwnedDelivery(
    DateTime OwnedAtUtc,
    string SessionId,
    int TotalChunks,
    string? Mime,
    string? Ext,
    string? Before,
    string? After,
    string? Prefix,
    DateTime SentAtUtc,
    string? DeliverySurface,
    string DeliveryIdentityKind,
    double? ClientRecordedMs = null,
    double? ClientDecodedSeconds = null,
    long? ClientSourceBytes = null,
    string? ClientSurface = null,
    string? DirectorId = null);

/// <summary>
/// A "Send anyway" the Gateway took over (contract section 4): the prompt as it was sent, the session, and what the
/// Director is told about who sent it. <see cref="Outcome"/> is null while held, then one of
/// <see cref="SendAnywayOutcomes"/>.
/// </summary>
/// <param name="DirectorId">The Director that held the session when the owner pressed (the press located it - a
/// held "Send anyway" is only ever created by a press that did). Kept so a later driver attempt can prove the
/// session has ENDED without guessing which Director is "its" (contract section 9, F4).</param>
public sealed record SendAnywayDelivery(
    DateTime OwnedAtUtc,
    string SessionId,
    string Text,
    string? Surface,
    SubmissionProvenanceDto? Provenance,
    string? Outcome = null,
    DateTime? ResolvedAtUtc = null,
    string? DirectorId = null);

/// <summary>The final outcomes of a Gateway-driven "Send anyway".</summary>
public static class SendAnywayOutcomes
{
    /// <summary>The words are in: typed by a re-press, refused as already delivered, or the question said delivered.</summary>
    public const string Delivered = "delivered";
    /// <summary>No answer of any kind for more than the limit from the first verified claim: could not confirm it.</summary>
    public const string Unconfirmed = DeliveryDecisions.Unconfirmed;
    /// <summary>The Director said the words are not in, past the limit from the first claim: shown back with "Send anyway".</summary>
    public const string TooOld = DeliveryDecisions.TooOld;
    /// <summary>The session has provably ended (located and exited, or its Director fresh and no longer listing it):
    /// the words are kept and shown back with Dismiss and NO "Send anyway" - there is no session left to send to
    /// (contract section 9, F4).</summary>
    public const string SessionExited = DeliveryDecisions.SessionExited;
}

public enum DictationOwnership { Taken, AlreadyOwned, NotPending }

/// <summary>What <see cref="VoiceUploadStore.TakeOwnership"/> did, and the record as it read.</summary>
public sealed record DictationOwnershipOutcome(DictationOwnership Result, DictationRecordRead Read);

public enum HeldDeliveryKind { Dictation, SendAnyway }

/// <summary>One delivery the Gateway owns and has not finished. <see cref="Problem"/> is set when it could not be
/// read, and then it is reported, never driven.</summary>
public sealed record HeldDelivery(string UploadId, HeldDeliveryKind Kind, string? SessionId, string? Problem);
