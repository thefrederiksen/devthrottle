using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Prompts;

/// <summary>Where a held typed prompt stands.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TypedPromptState
{
    /// <summary>The Director did not say the words are in, and did not say they are not: the Gateway keeps asking.</summary>
    Held,
    /// <summary>The Director said the words are in. The text has been deleted.</summary>
    Delivered,
    /// <summary>The Director said the words are not in (or never saw them, asked twice): shown back with "Send anyway".</summary>
    NotDelivered,
    /// <summary>No answer of any kind for more than the age limit from the sent time: shown back with no "Send anyway".</summary>
    Unconfirmed,
    /// <summary>The session is known to have ended (located, and exited): shown back with Dismiss and no "Send anyway".</summary>
    SessionEnded,
}

/// <summary>
/// The durable record of one held typed prompt (Voice Delivery mission, phase 5, contract section 7, T3). The text is
/// kept only while it may need to be shown back: <see cref="Text"/> is null once the record resolved delivered, and once
/// the owner acknowledged a shown-back record.
/// </summary>
public sealed record TypedPromptRecord
{
    public string DeliveryId { get; init; } = "";
    public string SessionId { get; init; } = "";
    /// <summary>The partition's tenant, stamped on every write and required to match on every read.</summary>
    public string Tenant { get; init; } = "";
    /// <summary>The text as sent, or null once it is no longer needed (delivered, or a shown-back record acknowledged).</summary>
    public string? Text { get; init; }
    /// <summary>How many characters were sent, kept after the text is deleted.</summary>
    public int Characters { get; init; }
    /// <summary>When the Gateway sent the prompt to the Director: the five-minute rule is measured from here.</summary>
    public DateTime SentAtUtc { get; init; }
    public TypedPromptState State { get; init; }
    /// <summary>The Director's state as the owner is told it while held: <c>delivering</c>, <c>unknown</c> or <c>no-answer</c>.</summary>
    public string DirectorState { get; init; } = "";
    /// <summary>
    /// How many times the Director has answered <c>unknown</c> to the question. The first is held - the command may still
    /// be queued in a starved Director - and a later one, at least one wake-up afterwards, is shown back.
    /// </summary>
    public int UnknownAnswers { get; init; }
    /// <summary>How many times the Gateway's driver has attempted this record.</summary>
    public int DriveAttempts { get; init; }
    /// <summary>
    /// True when the prompt ARRIVED while its session could not be located (a stale or frozen Director, contract section
    /// 8), so it was held rather than answered "gone", with nothing sent. Informational: whether it has been sent since is
    /// read from the decision log alone (<see cref="TypedPromptStore.MayHaveBeenSentToDirector"/>, section 10).
    /// </summary>
    public bool NeverSent { get; init; }
    /// <summary>The request fields to send a never-sent prompt with (contract section 10); null once it has been sent.</summary>
    public TypedPromptUnsentRequest? Unsent { get; init; }
    /// <summary>Why a shown-back record is shown back (<c>not-delivered</c>, <c>unconfirmed</c>), null otherwise.</summary>
    public string? Reason { get; init; }
    /// <summary>The request fields to re-press a CLAIMED record with (the "Send anyway" press's own settled fields);
    /// null when this record was never claimed. The provenance is filled in by
    /// <see cref="TypedPromptStore.RememberClaimProvenance"/> once the prompt route has built it.</summary>
    public TypedPromptUnsentRequest? ClaimRequest { get; init; }
    public DateTime? ResolvedAtUtc { get; init; }
    public DateTime? AcknowledgedAtUtc { get; init; }
}

/// <summary>
/// What the Gateway needs to send a typed prompt that NEVER LEFT it (contract section 10): the request fields the prompt
/// route had settled when the session could not be located. Kept only on a never-sent record; the text is on the record.
/// </summary>
public sealed record TypedPromptUnsentRequest
{
    public bool AppendEnter { get; init; } = true;
    public bool AgentDriven { get; init; }
    public string? Surface { get; init; }
    public bool MenuGuard { get; init; }
    public bool OnlyWhenWaitingForInput { get; init; }
    public CcDirector.Gateway.Contracts.SubmissionProvenanceDto? Provenance { get; init; }
}

/// <summary>What <see cref="TypedPromptStore.Read"/> found for one delivery id.</summary>
public sealed record TypedPromptRead(TypedPromptReadKind Kind, TypedPromptRecord? Record, string? Problem)
{
    public static readonly TypedPromptRead Absent = new(TypedPromptReadKind.Absent, null, null);
}

public enum TypedPromptReadKind
{
    /// <summary>No record for this id in this partition (never held, another account's, or retired).</summary>
    Absent,
    Present,
    /// <summary>A record file exists but cannot be read. Never guessed at, never driven.</summary>
    Unreadable,
}

/// <summary>What <see cref="TypedPromptStore.ClaimSendAnyway"/> decided about a "Send anyway" claim naming a typed prompt.</summary>
public enum TypedPromptClaimKind
{
    /// <summary>The claim is verified and the record is marked: the words go out under the original delivery id.</summary>
    Verified,
    /// <summary>The record exists but may not be pressed: it is already claimed and held, or already resolved another
    /// way. Nothing is sent, nothing is marked; <see cref="TypedPromptClaimResolution.Record"/> is the record's CURRENT
    /// state for the route to answer with.</summary>
    Refused,
    /// <summary>The claim names nothing this account may press (no record here, another session's, or past the claim
    /// window): the words go out as an ordinary prompt with a fresh id, exactly as a dropped recording claim does.</summary>
    Dropped,
}

/// <summary>The answer to <see cref="TypedPromptStore.ClaimSendAnyway"/>: what was decided, a short reason code (written
/// to the decision log), why in words, and on a refusal the record as it stands - the state the caller answers with.</summary>
public sealed record TypedPromptClaimResolution(TypedPromptClaimKind Kind, string Reason, string Why, TypedPromptRecord? Record)
{
    public static TypedPromptClaimResolution Verified() => new(TypedPromptClaimKind.Verified, "verified", "", null);
    public static TypedPromptClaimResolution Refused(string reason, string why, TypedPromptRecord record) => new(TypedPromptClaimKind.Refused, reason, why, record);
    public static TypedPromptClaimResolution Dropped(string reason, string why) => new(TypedPromptClaimKind.Dropped, reason, why, null);
}

/// <summary>
/// The names of the decisions written into a held typed prompt's decision log. The SAME names the dictation log uses where
/// they fit (<see cref="Voice.DeliveryDecisions"/>), so one reader reads both; spelled here because a typed prompt is not
/// an upload and has its own store.
/// </summary>
public static class TypedPromptDecisions
{
    public const string SentToDirector = Voice.DeliveryDecisions.SentToDirector;
    public const string DirectorAnswer = Voice.DeliveryDecisions.DirectorAnswer;
    public const string GatewayDrive = Voice.DeliveryDecisions.GatewayDrive;
    public const string GatewayDriveRefused = Voice.DeliveryDecisions.GatewayDriveRefused;
    public const string AskedDirector = Voice.DeliveryDecisions.AskedDirector;
    public const string DeliveryStateAnswer = Voice.DeliveryDecisions.DeliveryStateAnswer;
    public const string StillDelivering = Voice.DeliveryDecisions.StillDelivering;
    public const string SessionNotFound = Voice.DeliveryDecisions.SessionNotFound;
    public const string Delivered = Voice.DeliveryDecisions.Delivered;
    public const string NotDelivered = "not-delivered";
    public const string Unconfirmed = Voice.DeliveryDecisions.Unconfirmed;
    public const string Acknowledged = Voice.DeliveryDecisions.Acknowledged;
    public const string UnreadableLine = Voice.DeliveryDecisions.UnreadableLine;

    /// <summary>Why the Gateway asked: its driver, on a wake-up, asking what became of a held typed prompt.</summary>
    public const string AskReasonGatewayDrive = "gateway-drive";
    /// <summary>Why the prompt was shown back as not delivered: the Director answered <c>not-delivered</c>.</summary>
    public const string ReasonDirectorSaidNotDelivered = "director-said-not-delivered";
    /// <summary>Why the prompt was shown back as not delivered: the Director answered <c>unknown</c> on two wake-ups.</summary>
    public const string ReasonUnknownTwice = "unknown-twice";
    /// <summary>Why the prompt was shown back as not delivered: it never left the Gateway (its session was not located).</summary>
    public const string ReasonNeverSent = "never-sent";
    /// <summary>The session is known to have ended: located, and exited.</summary>
    public const string SessionExited = Voice.DeliveryDecisions.SessionExited;
    /// <summary>The held state while the session's Director cannot be reached (contract section 2's value).</summary>
    public const string WaitingForDirector = Api.DeliverySendAndAsk.WaitingForDirectorState;
    /// <summary>The held state while the Gateway will press the claim itself (contract section 2's value): a claimed
    /// record is marked with it the moment its claim is verified, before anything is sent.</summary>
    public const string Retrying = Api.DeliverySendAndAsk.RetryingState;
    /// <summary>Why a never-sent prompt was shown back rather than sent: it asked for the menu guard, which reads the live
    /// screen one hop before the send, and only the prompt route can do that.</summary>
    public const string ReasonMenuGuardNotAtTheDriver = "menu-guard-needs-the-prompt-route";
    /// <summary>Why a prompt that went out is known not in: nothing left the Gateway (its Director was not connected).</summary>
    public const string ReasonNeverLeftTheGateway = "never-left-the-gateway";

    /// <summary>The claim of a "Send anyway" was verified: the record is marked and the words go out under its id.
    /// Same line name as the recording claim writes (Voice Delivery phase 5, the Delivery Lead's ruling on review
    /// finding 2 - one claim mechanism for both).</summary>
    public const string ClaimVerified = Voice.DeliveryDecisions.ClaimVerified;
    /// <summary>A "Send anyway" claim was not taken: refused, or dropped. The reason on the line says which.</summary>
    public const string ClaimDropped = Voice.DeliveryDecisions.ClaimDropped;
    /// <summary>The Director's answer to a claimed send, written after every claimed send whatever it came to - the line
    /// a later re-press reads to know it must ask first.</summary>
    public const string ClaimDirectorAnswer = Voice.DeliveryDecisions.ClaimDirectorAnswer;
    /// <summary>Why a claimed press was shown back not delivered again: the claim's own send was known not in.</summary>
    public const string ReasonSendAnywayNotIn = "send-anyway-not-in";

    /// <summary>What woke the driver: the session's Director connected again. Same spelling as the dictation driver's.</summary>
    public const string DriveDirectorConnected = Voice.DeliveryDecisions.DriveDirectorConnected;
    /// <summary>What woke the driver: the steady tick.</summary>
    public const string DriveTick = Voice.DeliveryDecisions.DriveTick;
    /// <summary>What woke the driver: the Gateway started and picked the record up from disk.</summary>
    public const string DriveGatewayStarted = Voice.DeliveryDecisions.DriveGatewayStarted;
}

/// <summary>
/// The small facts written with one decision on a typed prompt. The same shape and JSON spelling as
/// <see cref="Voice.DeliveryDecisionFacts"/>, and like it a closed set of numbers, flags, states and short codes: there is
/// no field for the words, so the words cannot reach the log by accident.
/// </summary>
public sealed record TypedPromptDecisionFacts
{
    private readonly string? _error;

    public string? SessionId { get; init; }
    public string? State { get; init; }
    public string? Reason { get; init; }
    public bool? Ok { get; init; }
    public bool? RefusedDuplicate { get; init; }
    public int? Characters { get; init; }
    /// <summary>When the shown-back record was resolved, on a "Send anyway" claim line: the claim window was measured
    /// from it (the same field the dictation log's claim lines carry).</summary>
    public DateTime? ResolvedAtUtc { get; init; }
    public DateTime? SentAtUtc { get; init; }
    public long? AgeSeconds { get; init; }
    public string? DirectorNoAnswer { get; init; }
    public string? Trigger { get; init; }
    public int? Attempt { get; init; }

    /// <summary>Error text, cut to <see cref="Voice.DeliveryDecisionFacts.MaxErrorLength"/> characters.</summary>
    public string? Error
    {
        get => _error;
        init => _error = value is { Length: > Voice.DeliveryDecisionFacts.MaxErrorLength }
            ? value[..Voice.DeliveryDecisionFacts.MaxErrorLength]
            : value;
    }
}

/// <summary>One line of a typed prompt's decision log.</summary>
public sealed record TypedPromptDecisionLine(DateTime AtUtc, string Decision, TypedPromptDecisionFacts? Facts);

/// <summary>
/// HELD TYPED PROMPTS (Voice Delivery mission, phase 5, contract section 7, T3). Phase 4 case 2f: a typed prompt was
/// answered 200 "delivering" while the Director had in the end refused it and typed nothing - and a typed prompt carried
/// no delivery id, so nobody could ask. Now every typed prompt carries a Gateway-minted delivery id, and one the Director
/// did not answer as delivered is held HERE, so the Gateway's driver can ask what became of it - after a restart too.
///
/// A typed prompt is not an upload, so it has its own small store, laid out like the dictation one: one directory per
/// delivery id holding <c>record.json</c> and <c>decisions.jsonl</c>, in the tenant's own partition (the local tenant
/// keeps the root, every other tenant gets <c>tenants/&lt;id&gt;</c>). Every write is under a per-record gate, and the
/// record is written by temp-and-move so a crash never leaves half a record.
/// </summary>
public sealed class TypedPromptStore
{
    /// <summary>The container directory hosting the non-local partitions, directly under the base root.</summary>
    public const string TenantPartitionDirectoryName = "tenants";

    private const string RecordFileName = "record.json";
    private const string DecisionsFileName = "decisions.jsonl";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    // One gate object per record directory, shared by every store instance in the process (ForTenant makes many).
    private static readonly Dictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _root;
    private readonly string _partitionBase;
    private readonly TenantId _tenant;
    private readonly TimeProvider _clock;

    /// <summary>Stage under an explicit base root, bound to ONE tenant. The tenant is never defaulted.</summary>
    public TypedPromptStore(string root, TenantId tenant, TimeProvider? clock = null)
        : this(PartitionRootFor(root, RequireTenant(tenant)), root, tenant, clock ?? TimeProvider.System) { }

    private TypedPromptStore(string root, string partitionBase, TenantId tenant, TimeProvider clock)
    {
        _root = root;
        _partitionBase = partitionBase;
        _tenant = tenant;
        _clock = clock;
        Directory.CreateDirectory(_root);
    }

    public TenantId Tenant => _tenant;

    public string Root => _root;

    /// <summary>A view of this store bound to ONE tenant: another tenant's delivery id simply does not exist in it.</summary>
    public TypedPromptStore ForTenant(TenantId tenant)
        => new(PartitionRootFor(_partitionBase, RequireTenant(tenant)), _partitionBase, tenant, _clock);

    /// <summary>
    /// Every tenant that has a partition on disk: the local tenant, and each minted account tenant under
    /// <c>tenants/</c>. The Gateway's driver reads this when it starts, so a held prompt is found with nothing in memory.
    /// </summary>
    public IReadOnlyList<TenantId> TenantsWithPartitions()
    {
        var tenants = new List<TenantId> { TenantId.Local };
        var container = Path.Combine(_partitionBase, TenantPartitionDirectoryName);
        if (!Directory.Exists(container)) return tenants;
        foreach (var dir in Directory.GetDirectories(container))
        {
            var name = Path.GetFileName(dir);
            if (IsMintedAccountTenant(name)) tenants.Add(new TenantId(name));
        }
        return tenants;
    }

    /// <summary>A delivery id in the one spelling the Gateway mints (32 lowercase hex digits), or null when it is not one.</summary>
    public static string? NormalizeDeliveryId(string? deliveryId)
        => Guid.TryParse(deliveryId, out var g) ? g.ToString("N") : null;

    /// <summary>
    /// Hold a typed prompt the Director did not answer as delivered, writing the record and its first lines before the
    /// route answers: <c>sent-to-director</c>, the Director's answer, and <c>still-delivering</c>. The text is kept.
    /// </summary>
    public void Hold(string deliveryId, string sessionId, string text, DateTime sentAtUtc, string directorState,
        TypedPromptDecisionFacts directorAnswer)
    {
        var id = RequireId(deliveryId);
        WithGate(id, () =>
        {
            var dir = DirFor(id);
            if (File.Exists(RecordPath(dir)))
                throw new InvalidOperationException($"typed prompt {id} is already held; a delivery id is minted once");
            Directory.CreateDirectory(dir);
            AppendLine(dir, id, TypedPromptDecisions.SentToDirector, new TypedPromptDecisionFacts
            {
                SessionId = sessionId,
                Characters = text.Length,
                SentAtUtc = sentAtUtc,
            });
            AppendLine(dir, id, TypedPromptDecisions.DirectorAnswer, directorAnswer with { SessionId = sessionId });
            WriteRecord(dir, new TypedPromptRecord
            {
                DeliveryId = id,
                SessionId = sessionId,
                Text = text,
                Characters = text.Length,
                SentAtUtc = sentAtUtc,
                State = TypedPromptState.Held,
                DirectorState = directorState,
            });
            AppendLine(dir, id, TypedPromptDecisions.StillDelivering,
                new TypedPromptDecisionFacts { SessionId = sessionId, State = directorState });
        });
        FileLog.Write($"[TypedPromptStore] Hold: deliveryId={id} sid={sessionId} chars={text.Length} directorState={directorState}");
    }

    /// <summary>
    /// Hold a typed prompt that NEVER LEFT the Gateway, because its session could not be located when it arrived (contract
    /// section 8: a stale or frozen Director is a held delivery, never "session gone"). Writes <c>session-not-found</c> and
    /// <c>still-delivering</c> with <c>waiting-for-director</c> before the route answers. The text is kept.
    /// </summary>
    public void HoldNeverSent(string deliveryId, string sessionId, string text, DateTime receivedAtUtc, TypedPromptUnsentRequest unsent)
    {
        var id = RequireId(deliveryId);
        WithGate(id, () =>
        {
            var dir = DirFor(id);
            if (File.Exists(RecordPath(dir)))
                throw new InvalidOperationException($"typed prompt {id} is already held; a delivery id is minted once");
            Directory.CreateDirectory(dir);
            AppendLine(dir, id, TypedPromptDecisions.SessionNotFound, new TypedPromptDecisionFacts
            {
                SessionId = sessionId,
                Characters = text.Length,
                SentAtUtc = receivedAtUtc,
                Reason = "the session could not be located; nothing was sent",
            });
            WriteRecord(dir, new TypedPromptRecord
            {
                DeliveryId = id,
                SessionId = sessionId,
                Text = text,
                Characters = text.Length,
                SentAtUtc = receivedAtUtc,
                State = TypedPromptState.Held,
                DirectorState = TypedPromptDecisions.WaitingForDirector,
                NeverSent = true,
                Unsent = unsent,
            });
            AppendLine(dir, id, TypedPromptDecisions.StillDelivering,
                new TypedPromptDecisionFacts { SessionId = sessionId, State = TypedPromptDecisions.WaitingForDirector });
        });
        FileLog.Write($"[TypedPromptStore] HoldNeverSent: deliveryId={id} sid={sessionId} chars={text.Length}");
    }

    /// <summary>The record for one delivery id in this partition. A record stamped for another tenant reads as absent.</summary>
    public TypedPromptRead Read(string deliveryId)
    {
        var id = NormalizeDeliveryId(deliveryId);
        if (id is null) return TypedPromptRead.Absent;
        return WithGate(id, () => ReadUnlocked(id));
    }

    /// <summary>
    /// Every held record's delivery id and session in this partition - what the driver drives on each wake-up. A record
    /// that cannot be read is listed too, so the driver writes it up rather than silently skipping it.
    /// </summary>
    public IReadOnlyList<(string DeliveryId, string? SessionId)> HeldDeliveries()
    {
        var held = new List<(string, string?)>();
        foreach (var dir in Directory.GetDirectories(_root))
        {
            var id = NormalizeDeliveryId(Path.GetFileName(dir));
            if (id is null || id != Path.GetFileName(dir)) continue;
            var read = Read(id);
            if (read.Kind == TypedPromptReadKind.Unreadable) held.Add((id, null));
            else if (read.Record is { State: TypedPromptState.Held } r) held.Add((id, r.SessionId));
        }
        return held;
    }

    /// <summary>Write one decision line for a record that exists here. False (nothing written) when it does not.</summary>
    public bool RecordDecision(string deliveryId, string decision, TypedPromptDecisionFacts? facts = null)
    {
        var id = RequireId(deliveryId);
        return WithGate(id, () =>
        {
            var dir = DirFor(id);
            if (!Directory.Exists(dir)) return false;
            AppendLine(dir, id, decision, facts);
            return true;
        });
    }

    /// <summary>
    /// Start one driver attempt of a HELD record: count it and write <c>gateway-drive</c> with what woke it. Returns the
    /// record as it stands, or null when it is not held any more (resolved or acknowledged meanwhile).
    /// </summary>
    public TypedPromptRecord? BeginDrive(string deliveryId, string trigger)
    {
        var id = RequireId(deliveryId);
        return WithGate(id, () =>
        {
            var read = ReadUnlocked(id);
            if (read.Record is not { State: TypedPromptState.Held } record) return null;
            var next = record with { DriveAttempts = record.DriveAttempts + 1 };
            var dir = DirFor(id);
            WriteRecord(dir, next);
            AppendLine(dir, id, TypedPromptDecisions.GatewayDrive, new TypedPromptDecisionFacts
            {
                SessionId = record.SessionId,
                Trigger = trigger,
                Attempt = next.DriveAttempts,
            });
            return next;
        });
    }

    /// <summary>
    /// True when this record's decision log says the prompt MAY have reached the Director: it holds a
    /// <c>sent-to-director</c> line, or a line nobody can read and so could be one. This is the ONE test of whether a typed
    /// prompt left the Gateway (contract section 10: the driver decides from that line alone) - a prompt that did is only
    /// ever asked about, one that did not may be sent once. It leans to "may have", because asking is always safe.
    /// </summary>
    public bool MayHaveBeenSentToDirector(string deliveryId)
    {
        var id = RequireId(deliveryId);
        return WithGate(id, () => MayHaveBeenSentUnlocked(id));
    }

    private bool MayHaveBeenSentUnlocked(string id)
        => ReadLines(Path.Combine(DirFor(id), DecisionsFileName))
            .Any(l => l.Decision is TypedPromptDecisions.SentToDirector or TypedPromptDecisions.UnreadableLine);

    /// <summary>
    /// The driver is about to SEND a prompt that never left the Gateway (contract section 10): write <c>sent-to-director</c>
    /// under the record's gate BEFORE the send, so from here on - a crash, an unanswered send, any later wake-up - the
    /// prompt is only asked about and never sent again. Returns false (nothing written) when the record is not held or the
    /// log already says it may have been sent.
    /// </summary>
    public bool MarkSending(string deliveryId)
    {
        var id = RequireId(deliveryId);
        return WithGate(id, () =>
        {
            var read = ReadUnlocked(id);
            if (read.Record is not { State: TypedPromptState.Held } record || MayHaveBeenSentUnlocked(id)) return false;
            var dir = DirFor(id);
            AppendLine(dir, id, TypedPromptDecisions.SentToDirector, new TypedPromptDecisionFacts
            {
                SessionId = record.SessionId,
                Characters = record.Characters,
                SentAtUtc = record.SentAtUtc,
                Reason = TypedPromptDecisions.GatewayDrive,
            });
            WriteRecord(dir, record with { NeverSent = false });
            return true;
        });
    }

    /// <summary>
    /// A send the driver began left nothing behind - the Director's tunnel was gone, so the command never left the Gateway.
    /// Written as the Director's answer with that reason, and held waiting for the Director. The <c>sent-to-director</c>
    /// line stays, so from here the prompt is asked about, never sent again (section 10 decides from that line alone).
    /// </summary>
    public void MarkNeverLeft(string deliveryId, string? error)
    {
        var id = RequireId(deliveryId);
        WithGate(id, () =>
        {
            var read = ReadUnlocked(id);
            if (read.Record is not { State: TypedPromptState.Held } record)
                throw new InvalidOperationException($"typed prompt {id} is not held; its send cannot be marked never-left");
            var dir = DirFor(id);
            AppendLine(dir, id, TypedPromptDecisions.DirectorAnswer, new TypedPromptDecisionFacts
            {
                SessionId = record.SessionId,
                Ok = false,
                Reason = TypedPromptDecisions.ReasonNeverLeftTheGateway,
                Error = error,
            });
            WriteRecord(dir, record with { DirectorState = TypedPromptDecisions.WaitingForDirector });
        });
    }

    /// <summary>
    /// What this record's decision log says about "Send anyway" presses of it (ONE claim mechanism for both kinds of
    /// record, the Delivery Lead's ruling on the phase 5 review, finding 2): whether an earlier claimed send MAY have
    /// reached the Director - the log holds a <see cref="TypedPromptDecisions.ClaimDirectorAnswer"/> line, written
    /// after every claimed send whatever it came to, or a line that cannot be read and so could be one - and when the
    /// FIRST claim naming it was verified (the time of its first <see cref="TypedPromptDecisions.ClaimVerified"/>
    /// line), which the "could not confirm it arrived" limit is measured from. A re-press that sees the first asks
    /// the Director before it sends, exactly as a recording's re-press does. Nothing for an id that is not a delivery
    /// id or has no record here.
    /// </summary>
    public Voice.ClaimSendHistory ReadClaimSends(string deliveryId)
    {
        var id = NormalizeDeliveryId(deliveryId);
        if (id is null) return Voice.ClaimSendHistory.None;
        return WithGate(id, () =>
        {
            var lines = ReadLines(Path.Combine(DirFor(id), DecisionsFileName));
            var mayHaveReached = lines.Any(l =>
                l.Decision is TypedPromptDecisions.ClaimDirectorAnswer or TypedPromptDecisions.UnreadableLine);
            var firstVerified = lines.FirstOrDefault(l => l.Decision == TypedPromptDecisions.ClaimVerified)?.AtUtc;
            return new Voice.ClaimSendHistory(mayHaveReached, firstVerified);
        });
    }

    /// <summary>
    /// DECIDE AND MARK A "SEND ANYWAY" CLAIM OF A TYPED PROMPT, ATOMICALLY, under the record's own gate (the Delivery
    /// Lead's ruling on the phase 5 review, finding 2). A typed "Send anyway" claims the ORIGINAL delivery id through
    /// the same request field a recording's does, but the Director holds that id as <c>not-delivered</c> - a state that
    /// may begin again - so the Director cannot be the one that refuses the second copy. The GATEWAY is:
    ///
    /// - VERIFIED for a record of this partition's tenant (the caller's own account), for <paramref name="sessionId"/>,
    ///   shown back <see cref="TypedPromptState.NotDelivered"/> (known not in), resolved inside the claim window
    ///   (<c>DeliveryRetention.ClaimWindow</c>, the same window a recording claim is measured by). The record is marked
    ///   HELD BEFORE anything is sent: the text as pressed, the press's own request fields, and the
    ///   <see cref="TypedPromptDecisions.ClaimVerified"/> line the claim's time limit is measured from.
    /// - REFUSED for a record that is already held (an earlier claim the Gateway owns, or the delivery it was held for)
    ///   or already resolved another way. Nothing is sent and nothing is marked; the record as it stands is handed back
    ///   for the route to answer with - a second claim of the same id, another tab, a double press, a retry, is refused
    ///   without sending and exactly one press ever reaches the Director.
    /// - DROPPED for a claim that names nothing this account may press: no record here, another session's, or past the
    ///   claim window. The words go out as an ordinary prompt with a fresh id, exactly as a dropped recording claim.
    ///
    /// The decision line is written for every claim that names a record of this partition (never for another
    /// account's, which this partition cannot even see), in the same name the recording claim writes its lines in.
    /// </summary>
    public TypedPromptClaimResolution ClaimSendAnyway(string deliveryId, string sessionId, string text,
        TypedPromptUnsentRequest request, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = NormalizeDeliveryId(deliveryId);
        if (id is null) return TypedPromptClaimResolution.Dropped("not-a-delivery-id", "it is not a delivery id");
        return WithGate(id, () =>
        {
            var read = ReadUnlocked(id);
            if (read.Kind == TypedPromptReadKind.Absent)
                return TypedPromptClaimResolution.Dropped("no-typed-prompt-here",
                    "no typed prompt of the caller's account has this delivery id");
            if (read.Record is not { } record)
            {
                AppendLine(DirFor(id), id, TypedPromptDecisions.ClaimDropped, new TypedPromptDecisionFacts
                {
                    SessionId = sessionId,
                    Reason = "record-not-readable",
                    Error = read.Problem,
                });
                return TypedPromptClaimResolution.Dropped("record-not-readable", $"the record cannot be read: {read.Problem}");
            }

            var (kind, reason, why) = DecideClaim(record, sessionId, nowUtc);
            if (kind != TypedPromptClaimKind.Verified)
            {
                AppendLine(DirFor(id), id, TypedPromptDecisions.ClaimDropped, new TypedPromptDecisionFacts
                {
                    SessionId = sessionId,
                    State = record.State.ToString(),
                    Reason = reason,
                    ResolvedAtUtc = record.ResolvedAtUtc,
                    Characters = text.Length,
                });
                return kind == TypedPromptClaimKind.Refused
                    ? TypedPromptClaimResolution.Refused(reason, why, record)
                    : TypedPromptClaimResolution.Dropped(reason, why);
            }

            // THE MARK, before anything is sent: the record goes back to Held with the words as pressed and the
            // press's own request fields, so the outcome route answers "still delivering" for the ORIGINAL id, the
            // driver re-presses it, and a second claim finds it held and is refused. ResolvedAtUtc is cleared - the
            // claim is not settled - and the claim-verified line's time is the claim's limit from here on.
            WriteRecord(DirFor(id), record with
            {
                State = TypedPromptState.Held,
                Text = text,
                Characters = text.Length,
                DirectorState = TypedPromptDecisions.Retrying,
                UnknownAnswers = 0,
                NeverSent = false,
                Unsent = null,
                Reason = null,
                ResolvedAtUtc = null,
                AcknowledgedAtUtc = null,
                ClaimRequest = request,
            });
            AppendLine(DirFor(id), id, TypedPromptDecisions.ClaimVerified, new TypedPromptDecisionFacts
            {
                SessionId = sessionId,
                State = TypedPromptState.NotDelivered.ToString(),
                Characters = text.Length,
                ResolvedAtUtc = record.ResolvedAtUtc,
            });
            FileLog.Write($"[TypedPromptStore] ClaimSendAnyway: deliveryId={id} sid={sessionId} VERIFIED; " +
                $"the record is held and the Gateway presses the claim");
            return TypedPromptClaimResolution.Verified();
        });
    }

    private static (TypedPromptClaimKind Kind, string Reason, string Why) DecideClaim(TypedPromptRecord record,
        string sessionId, DateTime nowUtc)
    {
        if (!Guid.TryParse(record.SessionId, out var recorded) || !Guid.TryParse(sessionId, out var target) || recorded != target)
            return (TypedPromptClaimKind.Dropped, "another-session",
                $"the typed prompt belongs to session '{record.SessionId}', not this one");
        switch (record.State)
        {
            case TypedPromptState.NotDelivered:
                if (record.ResolvedAtUtc is not { } resolvedAt)
                    return (TypedPromptClaimKind.Dropped, "no-resolution-time",
                        "the shown-back record does not say when it was resolved");
                var age = nowUtc - resolvedAt;
                if (age > Contracts.DeliveryRetention.ClaimWindow)
                    return (TypedPromptClaimKind.Dropped, "outside-claim-window",
                        $"the typed prompt was shown back {age.TotalDays:0.#} days ago, past the " +
                        $"{Contracts.DeliveryRetention.ClaimWindow.TotalDays:0}-day claim window, so the Director may no longer remember it");
                return (TypedPromptClaimKind.Verified, "known-not-in",
                    $"the typed prompt was shown back not-delivered {age.TotalDays:0.#} days ago");
            case TypedPromptState.Held:
                return (TypedPromptClaimKind.Refused, "already-delivering",
                    "the Gateway is already delivering this message; nothing was sent again");
            case TypedPromptState.Delivered:
                return (TypedPromptClaimKind.Refused, "already-delivered",
                    "the words of this message are already in; nothing was sent again");
            case TypedPromptState.Unconfirmed:
                return (TypedPromptClaimKind.Refused, "unconfirmed",
                    "the Gateway could not confirm this message arrived, so it is not offered again");
            case TypedPromptState.SessionEnded:
                return (TypedPromptClaimKind.Refused, "session-ended",
                    "the session has ended, so there is nothing left to send this message to");
            default:
                throw new InvalidOperationException($"a claim of a typed prompt in state {record.State} is not decided");
        }
    }

    /// <summary>
    /// Fill in the provenance of a claimed record's re-press fields, once the prompt route has built it: the claim is
    /// marked before the route builds provenance (the mark must be atomic and come first), so the fields go out first
    /// and this completes them. No-op when the record is not a held claimed one.
    /// </summary>
    public void RememberClaimProvenance(string deliveryId, CcDirector.Gateway.Contracts.SubmissionProvenanceDto? provenance)
    {
        var id = RequireId(deliveryId);
        WithGate(id, () =>
        {
            var read = ReadUnlocked(id);
            if (read.Record is not { State: TypedPromptState.Held, ClaimRequest: { } claim } record) return;
            WriteRecord(DirFor(id), record with { ClaimRequest = claim with { Provenance = provenance } });
        });
    }

    /// <summary>
    /// Settle a CLAIMED record whose decision line the claim mechanism already wrote (<c>unconfirmed</c>, <c>too-old</c>):
    /// the state moves and the reason is set, with NO second line - the claim wrote the one the recording claim writes,
    /// with the age and which kind of no answer. A claim whose words are known not in (the press's own send refused)
    /// settles through <see cref="ResolveNotDelivered"/> instead, which writes its own <c>not-delivered</c> line.
    /// </summary>
    public void SettleClaimResolved(string deliveryId, TypedPromptState state, string reason)
    {
        var id = RequireId(deliveryId);
        WithGate(id, () =>
        {
            var read = ReadUnlocked(id);
            if (read.Record is not { State: TypedPromptState.Held } record)
                throw new InvalidOperationException(
                    $"typed prompt {id} is {read.Record?.State.ToString() ?? read.Kind.ToString()}, not a held claim; it cannot settle as {state}");
            WriteRecord(DirFor(id), record with { State = state, Reason = reason, ResolvedAtUtc = _clock.GetUtcNow().UtcDateTime });
        });
        FileLog.Write($"[TypedPromptStore] claim settled: deliveryId={id} state={state}");
    }

    /// <summary>Keep a held record held, with the Director's state as the owner is told it, and write <c>still-delivering</c>.</summary>
    public void StayHeld(string deliveryId, string directorState, bool countUnknown)
        => Transition(deliveryId, r => r with
        {
            DirectorState = directorState,
            UnknownAnswers = countUnknown ? r.UnknownAnswers + 1 : r.UnknownAnswers,
        }, TypedPromptDecisions.StillDelivering, r => new TypedPromptDecisionFacts { SessionId = r.SessionId, State = directorState });

    /// <summary>Resolve a held record as delivered: the text is deleted, and <c>delivered</c> is written.</summary>
    public void ResolveDelivered(string deliveryId)
        => Transition(deliveryId, r => r with
        {
            State = TypedPromptState.Delivered,
            Text = null,
            ResolvedAtUtc = _clock.GetUtcNow().UtcDateTime,
        }, TypedPromptDecisions.Delivered, r => new TypedPromptDecisionFacts { SessionId = r.SessionId, Characters = r.Characters });

    /// <summary>Show a held record back as not delivered (the text is kept), and write <c>not-delivered</c> with why.</summary>
    public void ResolveNotDelivered(string deliveryId, string why, string? state)
        => Transition(deliveryId, r => r with
        {
            State = TypedPromptState.NotDelivered,
            Reason = "not-delivered",
            ResolvedAtUtc = _clock.GetUtcNow().UtcDateTime,
        }, TypedPromptDecisions.NotDelivered, r => new TypedPromptDecisionFacts { SessionId = r.SessionId, Reason = why, State = state });

    /// <summary>Rule a held record could-not-confirm (the text is kept), and write <c>unconfirmed</c> with its age.</summary>
    public void ResolveUnconfirmed(string deliveryId, TimeSpan age, string noAnswerKind)
        => Transition(deliveryId, r => r with
        {
            State = TypedPromptState.Unconfirmed,
            Reason = "unconfirmed",
            ResolvedAtUtc = _clock.GetUtcNow().UtcDateTime,
        }, TypedPromptDecisions.Unconfirmed, r => new TypedPromptDecisionFacts
        {
            SessionId = r.SessionId,
            AgeSeconds = (long)Math.Floor(age.TotalSeconds),
            DirectorNoAnswer = noAnswerKind,
        });

    /// <summary>Resolve a held record as session-ended (the text is kept, no "Send anyway"), and write <c>session-exited</c>.</summary>
    public void ResolveSessionEnded(string deliveryId, string status)
        => Transition(deliveryId, r => r with
        {
            State = TypedPromptState.SessionEnded,
            Reason = TypedPromptDecisions.SessionExited,
            ResolvedAtUtc = _clock.GetUtcNow().UtcDateTime,
        }, TypedPromptDecisions.SessionExited, r => new TypedPromptDecisionFacts { SessionId = r.SessionId, State = status });

    /// <summary>What an acknowledgement came to.</summary>
    public enum AcknowledgeResult { NotFound, StillHeld, Acknowledged, AlreadyAcknowledged }

    /// <summary>
    /// The owner acknowledged a RESOLVED record: its text is deleted and <c>acknowledged</c> is written, once. A second
    /// acknowledgement changes nothing. A held record is not acknowledged - it is still being found out.
    /// </summary>
    public AcknowledgeResult Acknowledge(string deliveryId)
    {
        var id = NormalizeDeliveryId(deliveryId);
        if (id is null) return AcknowledgeResult.NotFound;
        return WithGate(id, () =>
        {
            var read = ReadUnlocked(id);
            if (read.Kind == TypedPromptReadKind.Unreadable)
                throw new InvalidOperationException($"typed prompt {id} cannot be acknowledged: {read.Problem}");
            if (read.Record is not { } record) return AcknowledgeResult.NotFound;
            if (record.State == TypedPromptState.Held) return AcknowledgeResult.StillHeld;
            if (record.AcknowledgedAtUtc is not null) return AcknowledgeResult.AlreadyAcknowledged;
            var dir = DirFor(id);
            WriteRecord(dir, record with { Text = null, AcknowledgedAtUtc = _clock.GetUtcNow().UtcDateTime });
            AppendLine(dir, id, TypedPromptDecisions.Acknowledged,
                new TypedPromptDecisionFacts { SessionId = record.SessionId, State = record.State.ToString() });
            FileLog.Write($"[TypedPromptStore] Acknowledge: deliveryId={id} state={record.State}; text deleted");
            return AcknowledgeResult.Acknowledged;
        });
    }

    /// <summary>This record's decision log in the order written; empty when there is no record here.</summary>
    public IReadOnlyList<TypedPromptDecisionLine> ReadDecisions(string deliveryId)
    {
        var id = NormalizeDeliveryId(deliveryId);
        if (id is null) return Array.Empty<TypedPromptDecisionLine>();
        return WithGate<IReadOnlyList<TypedPromptDecisionLine>>(id, () =>
        {
            var dir = DirFor(id);
            if (ReadUnlocked(id).Kind == TypedPromptReadKind.Absent) return Array.Empty<TypedPromptDecisionLine>();
            return ReadLines(Path.Combine(dir, DecisionsFileName));
        });
    }

    /// <summary>
    /// Retire records older than <paramref name="maxAge"/> - measured from when they resolved, or from the sent time for
    /// one still held - with their decision logs. The same thirty-day rule as the dictation records: by then the Director
    /// has forgotten the id too, so there is nothing left to ask. Returns how many were retired.
    /// </summary>
    public int SweepOlderThan(TimeSpan maxAge)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var removed = 0;
        foreach (var dir in Directory.GetDirectories(_root))
        {
            var id = Path.GetFileName(dir);
            if (NormalizeDeliveryId(id) != id) continue;
            WithGate(id, () =>
            {
                var read = ReadUnlocked(id);
                if (read.Record is not { } record) return; // unreadable or foreign: never swept blind
                var since = record.ResolvedAtUtc ?? record.SentAtUtc;
                if (now - since <= maxAge) return;
                Directory.Delete(dir, recursive: true);
                removed++;
                FileLog.Write($"[TypedPromptStore] SweepOlderThan: retired deliveryId={id} state={record.State} since={since:O}");
            });
        }
        if (removed > 0)
            FileLog.Write($"[TypedPromptStore] SweepOlderThan: removed={removed} older than {maxAge} (partition={_tenant.ToLogString()})");
        return removed;
    }

    private void Transition(string deliveryId, Func<TypedPromptRecord, TypedPromptRecord> change, string decision,
        Func<TypedPromptRecord, TypedPromptDecisionFacts> facts)
    {
        var id = RequireId(deliveryId);
        WithGate(id, () =>
        {
            var read = ReadUnlocked(id);
            if (read.Record is not { State: TypedPromptState.Held } record)
                throw new InvalidOperationException(
                    $"typed prompt {id} is {read.Record?.State.ToString() ?? read.Kind.ToString()}, not held; '{decision}' was not written");
            var next = change(record);
            var dir = DirFor(id);
            WriteRecord(dir, next);
            AppendLine(dir, id, decision, facts(next));
        });
        FileLog.Write($"[TypedPromptStore] {decision}: deliveryId={id}");
    }

    private TypedPromptRead ReadUnlocked(string id)
    {
        var path = RecordPath(DirFor(id));
        string raw;
        try { raw = File.ReadAllText(path); }
        catch (FileNotFoundException) { return TypedPromptRead.Absent; }
        catch (DirectoryNotFoundException) { return TypedPromptRead.Absent; }
        TypedPromptRecord? record;
        try { record = JsonSerializer.Deserialize<TypedPromptRecord>(raw, Json); }
        catch (JsonException ex)
        {
            return new TypedPromptRead(TypedPromptReadKind.Unreadable, null, $"the record at {path} cannot be read: {ex.Message}");
        }
        if (record is null)
            return new TypedPromptRead(TypedPromptReadKind.Unreadable, null, $"the record at {path} is JSON null");
        if (!string.Equals(record.Tenant, _tenant.Value, StringComparison.Ordinal))
        {
            FileLog.Write($"[TypedPromptStore] Read: deliveryId={id} record belongs to another tenant (partition={_tenant.ToLogString()}); answered absent");
            return TypedPromptRead.Absent;
        }
        return new TypedPromptRead(TypedPromptReadKind.Present, record, null);
    }

    // The one place a record is written, so the one place the tenant is stamped.
    private void WriteRecord(string dir, TypedPromptRecord record)
    {
        var path = RecordPath(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(record with { Tenant = _tenant.Value }, Json));
        File.Move(tmp, path, overwrite: true);
    }

    private void AppendLine(string dir, string id, string decision, TypedPromptDecisionFacts? facts)
    {
        var line = JsonSerializer.Serialize(new TypedPromptDecisionLine(_clock.GetUtcNow().UtcDateTime, decision, facts), Json);
        try
        {
            File.AppendAllText(Path.Combine(dir, DecisionsFileName), line + "\n");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TypedPromptStore] DECISION NOT WRITTEN: deliveryId={id} decision={decision}: {ex.Message}");
            throw;
        }
        FileLog.Write($"[TypedPromptStore] Decision: deliveryId={id} {decision}");
    }

    private static List<TypedPromptDecisionLine> ReadLines(string path)
    {
        string[] raw;
        try { raw = File.ReadAllLines(path); }
        catch (FileNotFoundException) { return new List<TypedPromptDecisionLine>(); }
        catch (DirectoryNotFoundException) { return new List<TypedPromptDecisionLine>(); }
        var lines = new List<TypedPromptDecisionLine>(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(raw[i])) continue;
            try
            {
                lines.Add(JsonSerializer.Deserialize<TypedPromptDecisionLine>(raw[i], Json)
                    ?? throw new JsonException("the line is JSON null"));
            }
            catch (JsonException ex)
            {
                lines.Add(new TypedPromptDecisionLine(DateTime.MinValue, TypedPromptDecisions.UnreadableLine,
                    new TypedPromptDecisionFacts { Error = $"line {i + 1}: {ex.Message}" }));
            }
        }
        return lines;
    }

    private T WithGate<T>(string id, Func<T> body)
    {
        object gate;
        var key = Path.GetFullPath(DirFor(id));
        lock (Gates)
        {
            if (!Gates.TryGetValue(key, out gate!))
            {
                gate = new object();
                Gates[key] = gate;
            }
        }
        lock (gate) return body();
    }

    private void WithGate(string id, Action body) => WithGate<bool>(id, () => { body(); return true; });

    private static string RequireId(string deliveryId)
        => NormalizeDeliveryId(deliveryId)
           ?? throw new ArgumentException($"'{deliveryId}' is not a delivery id", nameof(deliveryId));

    private string DirFor(string id) => Path.Combine(_root, id);

    private static string RecordPath(string dir) => Path.Combine(dir, RecordFileName);

    private static TenantId RequireTenant(TenantId tenant)
        => tenant.IsValid
            ? tenant
            : throw new ArgumentException("A typed prompt partition needs a valid tenant; an unresolved tenant is denied, never defaulted.",
                nameof(tenant));

    // The same rule as the dictation store's partition (VoiceUploadStore.IsMintedAccountTenant): a tenant id becomes a
    // directory name, so only the one canonical lowercase GUID spelling the registry mints is accepted.
    private static bool IsMintedAccountTenant(string value)
        => Guid.TryParseExact(value, "D", out var parsed)
           && string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal);

    private static string PartitionRootFor(string partitionBase, TenantId tenant)
    {
        if (tenant.IsLocal) return partitionBase;
        if (!IsMintedAccountTenant(tenant.Value))
            throw new ArgumentException(
                $"Tenant '{tenant.ToLogString()}' is not a minted account tenant and cannot name a typed prompt partition.",
                nameof(tenant));
        return Path.Combine(partitionBase, TenantPartitionDirectoryName, tenant.Value);
    }
}
