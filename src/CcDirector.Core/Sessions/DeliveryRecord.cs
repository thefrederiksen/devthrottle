using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Sessions;

/// <summary>One line of a session's delivery record: a delivery id moved to a state at a time.</summary>
public sealed class DeliveryRecordEntry
{
    /// <summary>The delivery id - the recording's upload id the Gateway sent with the prompt.</summary>
    public string Id { get; set; } = "";

    /// <summary>The state it moved to, as its wire word (<see cref="DeliveryStates"/>).</summary>
    public string State { get; set; } = "";

    /// <summary>Why it was not delivered, or why it could not be confirmed. Null for every other state.</summary>
    public string? Reason { get; set; }

    /// <summary>When the state was written, in UTC.</summary>
    public DateTime At { get; set; }

    /// <summary>The process id of the Director that wrote this <c>delivering</c> line (issue #3487). Written on
    /// <c>delivering</c> lines only; null on every other state, and on a <c>delivering</c> line written by a Director older
    /// than the field.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OwnerProcessId { get; set; }

    /// <summary>When that process started, in UTC - so a later process that reuses the id is not taken for the writer.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? OwnerStartedAt { get; set; }

    /// <summary>The steps the send took, in order and in full, each with the milliseconds since the send began
    /// (<see cref="Input.SendTrail"/>): what the composer held, the keys pressed, what it held after, the retries, and the
    /// verdict. Written on a send's final line - delivered, not delivered or unconfirmed - and null on every other line,
    /// and on a line written by a Director older than the field (the Prompt Delivery mission, 8 October 2026).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Steps { get; set; }
}

/// <summary>The Director process that began a delivery: its process id and when it started, in UTC. The pair names one
/// process for the life of the machine; the id alone does not, because the operating system reuses it.</summary>
public sealed record DeliveryOwner(int ProcessId, DateTime StartedAtUtc)
{
    /// <summary>How far apart two readings of one process's start time may be and still name the same process. The
    /// operating system reports the start time to a fraction of a second, and on Linux derives it from the boot time; a
    /// different process given the same id inside this window is not a real case.</summary>
    public static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    private static readonly Lazy<DeliveryOwner> CurrentInstance = new(() =>
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        return new DeliveryOwner(self.Id, self.StartTime.ToUniversalTime());
    });

    /// <summary>This process.</summary>
    public static DeliveryOwner Current => CurrentInstance.Value;

    /// <summary>True when <paramref name="startedAtUtc"/> is this owner's start time, within <see cref="StartTimeTolerance"/>.</summary>
    public bool StartedAt(DateTime startedAtUtc) => (StartedAtUtc - startedAtUtc).Duration() <= StartTimeTolerance;
}

/// <summary>What asking the operating system about a delivery's owner established. THREE answers: a process that could not
/// be read is neither alive nor gone, and only <see cref="Gone"/> lets a <c>delivering</c> entry be settled.</summary>
public enum DeliveryOwnerLiveness
{
    /// <summary>That process is running: its send may still be typing, or its late watch still watching.</summary>
    Alive,

    /// <summary>The operating system says no such process is running: no such id, an exited one, or the id now belongs to
    /// a process that started at another time.</summary>
    Gone,

    /// <summary>The question could not be answered. Never read as gone.</summary>
    Unreadable,
}

/// <summary>What <see cref="DeliveryRecord.SettleOrphanedDeliveries"/> found and did, for the start-up log.</summary>
/// <param name="Settled">Delivering entries whose owner is gone, now unconfirmed.</param>
/// <param name="KeptLiveOwner">Delivering entries left alone because the process that began them is running.</param>
/// <param name="KeptUnprovable">Delivering entries left alone because their owner could not be shown to be gone: a line with
/// no owner (written by a Director older than the field), or an owner the operating system could not be asked about.</param>
/// <param name="UnreadableFiles">Session files that could not be read, with why; their deliveries are refused as before.</param>
public sealed record DeliverySettleReport(
    int Settled, int KeptLiveOwner, int KeptUnprovable, IReadOnlyList<string> UnreadableFiles);

/// <summary>What the record says about one delivery id for one session.</summary>
public sealed record DeliveryLookup(DeliveryState State, string? Reason, DateTime? At);

/// <summary>The answer to <see cref="DeliveryRecord.TryBeginDelivery"/>: either this caller now owns the delivery
/// (<see cref="Began"/>, and <c>Delivering</c> is on disk), or the id was already delivered, being delivered, or could not
/// be confirmed, and <see cref="Existing"/> says which - in which case nothing may be typed.</summary>
public sealed record DeliveryClaim(bool Began, DeliveryLookup Existing);

/// <summary>
/// A session's delivery record could not be read. The delivery is refused rather than treated as never seen:
/// reading an unreadable record as "unknown" would let a retry type words that may already be in - the same way
/// issue #2745 re-delivered speech when an unreadable dictation record was read as "no record".
/// </summary>
public sealed class DeliveryRecordUnreadableException : Exception
{
    public string FilePath { get; }

    public DeliveryRecordUnreadableException(string filePath, string detail, Exception? inner = null)
        : base($"The delivery record {filePath} cannot be read ({detail}), so whether this delivery already reached the " +
               "session is not known. Nothing was typed. Move the file aside only once you have checked what it says.", inner)
    {
        FilePath = filePath;
    }
}

/// <summary>
/// THE DIRECTOR'S DURABLE RECORD OF DELIVERIES (Voice Delivery mission, phase 1). The Director is the one process
/// that knows whether a delivery happened, so it remembers, per session and per delivery id, whether the words are
/// being typed (<see cref="DeliveryState.Delivering"/>), went in (<see cref="DeliveryState.Delivered"/>) or did not
/// (<see cref="DeliveryState.NotDelivered"/>, with the reason) - and a second copy of an id that is delivered or
/// being delivered is refused without typing anything. On 25 September 2026 a spoken prompt reached the agent twice
/// because nothing remembered the first copy.
///
/// THE STATES FOLLOW THE SEND (<c>TextSendOutcome</c>): a send that throws is not delivered; one that returns confirmed is
/// delivered; one that returns still delivering (phase 3: the words left the composer of a working agent and are not yet
/// in its records, or the send outlived the prompt verb's answer budget) stays <c>Delivering</c> while the Director
/// watches the agent's records, then becomes <c>Delivered</c> when they show it, or <c>Unconfirmed</c> with
/// <see cref="NeverInAgentRecordsReason"/> when the watch ends without it. Nothing stays <c>Delivering</c> forever
/// (round 2c): with no records to watch, or after the watch fails, it stays <c>Delivering</c> to the same limit and then
/// becomes <c>Unconfirmed</c> with <see cref="NoRecordsToWatchReason"/> or <see cref="RecordsWatchFailedReason"/>; a
/// session that ends first makes it <c>NotDelivered</c> at once, with <see cref="SessionEndedReason"/>.
///
/// UNCONFIRMED IS NOT NOT-DELIVERED (issue #3484, the owner's ruling of 29 September 2026). Every one of those
/// watch endings comes after the words LEFT THE COMPOSER, so the agent may hold them: a watch that ends without proof
/// either way proves nothing. <c>NotDelivered</c> is kept for words that provably never arrived, because it lets the id
/// begin again and the Gateway offers "Send anyway" for it; <c>Unconfirmed</c> refuses a second copy exactly as
/// <c>Delivered</c> does, and the Gateway answers it with no "Send anyway". <c>Delivering</c> is written BEFORE the first
/// character is typed, so a Director that dies while typing leaves <c>Delivering</c> on disk - which the next start-up
/// settles, below.
///
/// A <c>Delivering</c> ENTRY WHOSE DIRECTOR STOPPED BECOMES <c>Unconfirmed</c> AT START-UP (issue #3487, the owner's
/// ruling of 29 September 2026, replacing the earlier ruling that it stays <c>Delivering</c>). The late watch that would
/// have settled it died with the process, so left alone it would be held forever. The words may be in, wholly or partly,
/// and nothing on this side can tell - which is exactly what <c>Unconfirmed</c> says: the id never begins again, and the
/// Gateway shows the owner his words with no "Send anyway". It still never becomes <c>Unknown</c> or <c>NotDelivered</c>,
/// either of which would let a retry type it a second time. See <see cref="SettleOrphanedDeliveries"/>.
///
/// WHO OWNS A <c>Delivering</c> ENTRY: every <c>delivering</c> line carries the process that wrote it
/// (<see cref="DeliveryOwner"/>: process id and start time). A send and its late watch run in that same process and write
/// the final state while it runs, so while that process runs the entry is owned by a live send, and once it has gone
/// nothing will ever settle the entry. That is why only an entry whose owner the operating system says is GONE is
/// settled: one this process wrote, one another live Director on the same state files wrote, one whose owner cannot be
/// read, and one written by a Director older than the stamp are all left as they are.
///
/// ON DISK: one file per session, <c>&lt;sessionId&gt;.jsonl</c>, one JSON line per state change, the last line for an id
/// wins. Every write rewrites the whole file to a temporary file, flushes it, and moves it over the old one, so a crash
/// mid-write leaves either the old file or the new one - never a torn line that would make every earlier entry
/// unreadable. The files are small (see <see cref="Retention"/>), so a rewrite per state change costs nothing that
/// matters beside typing a prompt.
///
/// ONE STEP PER SESSION: the lookup and the write of <c>Delivering</c> happen under one lock per session, so two
/// concurrent sends of the same id cannot both see "not delivered" and both type. The session's own send gate is not
/// used for this on purpose: it is held for the whole send (up to minutes on a busy agent), and a second copy must
/// be answered at once, not queued behind the first.
/// </summary>
public sealed class DeliveryRecord
{
    /// <summary>
    /// How long an entry is kept. Entries older than this are dropped whenever the session's file is written.
    /// NOT CHOSEN HERE: it is <see cref="DeliveryRetention.DirectorRetention"/> - the Gateway's claim window plus a
    /// margin - because the record must outlive every window in which the Gateway can still send a recording with
    /// its delivery id. A shorter record would answer "unknown" to a "Send anyway" the Gateway still honours, and
    /// the words would be typed twice. It was seven days until review finding 3 found it inside the Gateway's
    /// thirty. A busy session's file then holds a few thousand small lines, rewritten once per delivery.
    /// </summary>
    public static readonly TimeSpan Retention = DeliveryRetention.DirectorRetention;

    /// <summary>Where the Director keeps it: beside its crash journal, in this instance's own Director config.</summary>
    public static string DefaultDirectory => Path.Combine(CcStorage.ToolConfig("director"), "delivery-records");

    private static readonly Lazy<DeliveryRecord> SharedInstance = new(() => new DeliveryRecord(DefaultDirectory));

    /// <summary>The one record this Director process uses. One instance, so its per-session locks are the only ones.</summary>
    public static DeliveryRecord Shared => SharedInstance.Value;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _directory;
    private readonly Func<DateTime> _utcNow;
    private readonly DeliveryOwner _owner;
    private readonly Func<DeliveryOwner, DeliveryOwnerLiveness> _ownerLiveness;
    private readonly ConcurrentDictionary<Guid, object> _sessionLocks = new();

    /// <param name="owner">The process this record writes <c>delivering</c> lines as. Null in the Director - this process;
    /// a made-up one in tests, to stand in for a Director that has since stopped.</param>
    /// <param name="ownerLiveness">How another owner's liveness is asked. Null in the Director - the operating system
    /// (<see cref="AskTheOperatingSystem"/>).</param>
    public DeliveryRecord(string directory, Func<DateTime>? utcNow = null, DeliveryOwner? owner = null,
        Func<DeliveryOwner, DeliveryOwnerLiveness>? ownerLiveness = null)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("A directory is required.", nameof(directory));
        _directory = directory;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _owner = owner ?? DeliveryOwner.Current;
        _ownerLiveness = ownerLiveness ?? AskTheOperatingSystem;
    }

    /// <summary>The file that holds <paramref name="sessionId"/>'s deliveries.</summary>
    public string FileFor(Guid sessionId) => Path.Combine(_directory, $"{sessionId:D}.jsonl");

    /// <summary>What became of <paramref name="deliveryId"/> for this session. Never seen -&gt; <see cref="DeliveryState.Unknown"/>.
    /// Throws <see cref="DeliveryRecordUnreadableException"/> when the file exists and cannot be read.</summary>
    public DeliveryLookup Read(Guid sessionId, string deliveryId)
    {
        RequireId(deliveryId);
        FileLog.Write($"[DeliveryRecord] Read: session={sessionId}, deliveryId={deliveryId}");
        return UnderSessionLock(sessionId, "a read", () =>
        {
            var lookup = Latest(ReadEntries(sessionId), deliveryId);
            FileLog.Write($"[DeliveryRecord] Read: session={sessionId}, deliveryId={deliveryId}, state={DeliveryStates.Format(lookup.State)}");
            return lookup;
        });
    }

    /// <summary>
    /// The one step before typing: look the id up and, unless it is already delivered, being delivered, or could not be
    /// confirmed, write <see cref="DeliveryState.Delivering"/> - both under this session's lock.
    /// <see cref="DeliveryClaim.Began"/> false means nothing may be typed. An id that is unknown or not delivered begins (a
    /// not-delivered one is a real retry); an unconfirmed one never does, because its words may already be in.
    /// </summary>
    public DeliveryClaim TryBeginDelivery(Guid sessionId, string deliveryId)
    {
        RequireId(deliveryId);
        FileLog.Write($"[DeliveryRecord] TryBeginDelivery: session={sessionId}, deliveryId={deliveryId}");
        return UnderSessionLock(sessionId, "the claim before typing", () =>
        {
            var entries = ReadEntries(sessionId);
            var existing = Latest(entries, deliveryId);
            if (existing.State is DeliveryState.Delivered or DeliveryState.Delivering or DeliveryState.Unconfirmed)
            {
                FileLog.Write($"[DeliveryRecord] TryBeginDelivery: REFUSED session={sessionId}, deliveryId={deliveryId}: already {DeliveryStates.Format(existing.State)} since {existing.At:O}");
                return new DeliveryClaim(false, existing);
            }
            Append(sessionId, entries, deliveryId, DeliveryState.Delivering, null);
            FileLog.Write($"[DeliveryRecord] TryBeginDelivery: began session={sessionId}, deliveryId={deliveryId}, was={DeliveryStates.Format(existing.State)}");
            return new DeliveryClaim(true, existing);
        });
    }

    /// <summary>
    /// The reason written when a send that was still delivering never showed in the agent's records before the Director's
    /// late watch ended (the Delivery Lead's ruling, round 2b: nothing stays delivering forever): "never appeared in the
    /// agent's records within 15 minutes". The words left the composer, so this is written as UNCONFIRMED (issue #3484):
    /// the Gateway shows the owner his words with no "Send anyway", and nothing types them a second time.
    /// </summary>
    public static string NeverInAgentRecordsReason(TimeSpan watch)
    {
        if (watch <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(watch), watch, "A records watch has a positive limit.");
        var within = watch.TotalMinutes >= 1 && watch.TotalMinutes == Math.Floor(watch.TotalMinutes)
            ? $"{watch.TotalMinutes:F0} minute{(watch.TotalMinutes == 1 ? "" : "s")}"
            : $"{watch.TotalSeconds:0.#} second{(watch.TotalSeconds == 1 ? "" : "s")}";
        return $"{NeverInAgentRecordsPrefix}{within}";
    }

    /// <summary>The reason written when a still-delivering send had no records to watch - the agent keeps none the Director
    /// can read for it - and the late watch's limit ended (the Tech Lead's ruling, round 2c, case 1).</summary>
    public const string NoRecordsToWatchReason = "could not be confirmed: the Director had no records to watch for this agent";

    /// <summary>The reason written when reading the records failed during the late watch and its limit then ended (round 2c,
    /// case 2), naming <paramref name="failure"/>.</summary>
    public static string RecordsWatchFailedReason(string failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failure);
        return $"{RecordsWatchFailedPrefix}{failure}";
    }

    /// <summary>
    /// The start of the two built reasons a late watch writes when it ended WITHOUT PROOF EITHER WAY:
    /// <see cref="RecordsWatchFailedReason"/> and <see cref="NeverInAgentRecordsReason"/>. The third,
    /// <see cref="NoRecordsToWatchReason"/>, is matched whole. A Director released before issue #3484 wrote these endings as
    /// not-delivered with exactly these words, so they are what tells a Gateway that such a not-delivered proves nothing
    /// (<see cref="IsAnUnprovenEnding"/>). Each prefix is specific to the watch on purpose: a send that throws stores its
    /// exception message verbatim as the reason, and a shorter prefix such as "could not be confirmed: " could match an
    /// exception's own words and take "Send anyway" away from a send that provably never arrived (review of pull request 3486).
    /// </summary>
    public static readonly IReadOnlyList<string> UnprovenEndingPrefixes = new[]
    {
        RecordsWatchFailedPrefix,
        NeverInAgentRecordsPrefix,
    };

    private const string RecordsWatchFailedPrefix = "could not be confirmed: the records watch failed: ";
    private const string NeverInAgentRecordsPrefix = "never appeared in the agent's records within ";

    /// <summary>True when <paramref name="reason"/> is one a late watch writes when it ended without proof either way:
    /// <see cref="NoRecordsToWatchReason"/> exactly, or one of <see cref="UnprovenEndingPrefixes"/> at the start - never
    /// a contains-match, which would catch any reason that happens to quote the words.</summary>
    public static bool IsAnUnprovenEnding(string? reason)
        => reason is not null
           && (string.Equals(reason, NoRecordsToWatchReason, StringComparison.Ordinal)
               || UnprovenEndingPrefixes.Any(p => reason.StartsWith(p, StringComparison.Ordinal)));

    /// <summary>The reason written at start-up for a delivery the Director was typing when it stopped (issue #3487): the
    /// entry was left <c>delivering</c> by a process that is gone, so nothing would ever settle it otherwise.</summary>
    public const string DirectorStoppedWhileTypingReason =
        "could not be confirmed: the Director stopped while it was being typed";

    /// <summary>
    /// THE START-UP SETTLEMENT (issue #3487). Every session file is read, and every id whose latest line is
    /// <c>delivering</c> is settled by its owner:
    /// - owned by this process: left alone - a send in this process began it and will write its end;
    /// - owned by another process the operating system says is running: left alone - a second Director on the same state
    ///   files, whose send or late watch is still live;
    /// - owned by a process the operating system says is gone: written <c>Unconfirmed</c> with
    ///   <see cref="DirectorStoppedWhileTypingReason"/>;
    /// - no owner on the line (a Director older than the stamp), or an owner that cannot be read: left alone, and counted,
    ///   because nothing shows the writer has gone.
    /// Each session is settled under its own lock, so a send that begins in this process at the same moment is never
    /// overwritten. A file that cannot be read is named in the report and left untouched; every other file is still settled.
    /// </summary>
    public DeliverySettleReport SettleOrphanedDeliveries()
    {
        FileLog.Write($"[DeliveryRecord] SettleOrphanedDeliveries: directory={_directory}, this process={_owner.ProcessId} started {_owner.StartedAtUtc:O}");
        var settled = 0;
        var keptLive = 0;
        var keptUnprovable = 0;
        var unreadable = new List<string>();
        if (!Directory.Exists(_directory))
        {
            FileLog.Write("[DeliveryRecord] SettleOrphanedDeliveries: no delivery records yet; nothing to settle");
            return new DeliverySettleReport(0, 0, 0, unreadable);
        }

        foreach (var path in Directory.GetFiles(_directory, "*.jsonl").OrderBy(p => p, StringComparer.Ordinal))
        {
            if (!Guid.TryParse(Path.GetFileNameWithoutExtension(path), out var sessionId))
            {
                FileLog.Write($"[DeliveryRecord] SettleOrphanedDeliveries: {path} is not named for a session; not read");
                unreadable.Add($"{path}: not named for a session");
                continue;
            }
            try
            {
                var counts = UnderSessionLock(sessionId, "the start-up settlement", () => SettleSession(sessionId));
                settled += counts.Settled;
                keptLive += counts.KeptLive;
                keptUnprovable += counts.KeptUnprovable;
            }
            catch (DeliveryRecordUnreadableException ex)
            {
                unreadable.Add(ex.Message);
            }
        }

        FileLog.Write($"[DeliveryRecord] SettleOrphanedDeliveries: settled={settled}, keptLiveOwner={keptLive}, " +
                      $"keptUnprovable={keptUnprovable}, unreadableFiles={unreadable.Count}");
        return new DeliverySettleReport(settled, keptLive, keptUnprovable, unreadable);
    }

    private (int Settled, int KeptLive, int KeptUnprovable) SettleSession(Guid sessionId)
    {
        var entries = ReadEntries(sessionId);
        var delivering = entries
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .Select(g => g.Last())
            .Where(e => DeliveryStates.TryParse(e.State, out var state) && state == DeliveryState.Delivering)
            .ToList();
        int settled = 0, keptLive = 0, keptUnprovable = 0;
        foreach (var entry in delivering)
        {
            var writer = entry.OwnerProcessId is { } pid && entry.OwnerStartedAt is { } started
                ? new DeliveryOwner(pid, started)
                : null;
            if (writer is null)
            {
                FileLog.Write($"[DeliveryRecord] SettleOrphanedDeliveries: KEPT session={sessionId}, deliveryId={entry.Id}: " +
                              $"delivering since {entry.At:O} with no owner on the line (a Director older than the stamp), " +
                              "so nothing shows its writer has gone");
                keptUnprovable++;
                continue;
            }
            var liveness = writer.ProcessId == _owner.ProcessId && _owner.StartedAt(writer.StartedAtUtc)
                ? DeliveryOwnerLiveness.Alive
                : _ownerLiveness(writer);
            switch (liveness)
            {
                case DeliveryOwnerLiveness.Alive:
                    FileLog.Write($"[DeliveryRecord] SettleOrphanedDeliveries: KEPT session={sessionId}, deliveryId={entry.Id}: " +
                                  $"its owner, process {writer.ProcessId} started {writer.StartedAtUtc:O}, is running");
                    keptLive++;
                    break;
                case DeliveryOwnerLiveness.Gone:
                    entries = Append(sessionId, entries, entry.Id, DeliveryState.Unconfirmed, DirectorStoppedWhileTypingReason);
                    FileLog.Write($"[DeliveryRecord] SettleOrphanedDeliveries: SETTLED session={sessionId}, deliveryId={entry.Id}: " +
                                  $"delivering since {entry.At:O}; its owner, process {writer.ProcessId} started " +
                                  $"{writer.StartedAtUtc:O}, is gone; now unconfirmed");
                    settled++;
                    break;
                default:
                    FileLog.Write($"[DeliveryRecord] SettleOrphanedDeliveries: KEPT session={sessionId}, deliveryId={entry.Id}: " +
                                  $"its owner, process {writer.ProcessId}, could not be read, so it is not known to have gone");
                    keptUnprovable++;
                    break;
            }
        }
        return (settled, keptLive, keptUnprovable);
    }

    /// <summary>The operating system's answer about <paramref name="owner"/>: <see cref="DeliveryOwnerLiveness.Gone"/> when
    /// no process has its id, the process has exited, or the process with its id started at another time (the id was
    /// reused); <see cref="DeliveryOwnerLiveness.Unreadable"/> when the question itself failed.</summary>
    public static DeliveryOwnerLiveness AskTheOperatingSystem(DeliveryOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.ProcessId <= 0) return DeliveryOwnerLiveness.Gone;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(owner.ProcessId);
            if (process.HasExited) return DeliveryOwnerLiveness.Gone;
            return owner.StartedAt(process.StartTime.ToUniversalTime()) ? DeliveryOwnerLiveness.Alive : DeliveryOwnerLiveness.Gone;
        }
        catch (ArgumentException)
        {
            return DeliveryOwnerLiveness.Gone;   // no such process: the operating system looked and said so
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Includes a process that exits between the lookup and the read - but that is not proven, so it is reported
            // unreadable rather than guessed gone.
            FileLog.Write($"[DeliveryRecord] AskTheOperatingSystem: could not read process {owner.ProcessId} " +
                          $"({ex.GetType().Name}: {ex.Message}); unreadable, not gone");
            return DeliveryOwnerLiveness.Unreadable;
        }
    }

    /// <summary>The reason written, at once, when the session ended while a send was still delivering (round 2c, case 3). A
    /// retry into an ended session cannot double anything.</summary>
    public const string SessionEndedReason = "the session ended before the words appeared in its records";

    /// <summary>The send completed: the words reached the session.</summary>
    public void MarkDelivered(Guid sessionId, string deliveryId, IReadOnlyList<string>? steps = null) =>
        Write(sessionId, deliveryId, DeliveryState.Delivered, null, steps);

    /// <summary>The send threw, or was refused before typing: the words did not reach the session, for <paramref name="reason"/>.</summary>
    public void MarkNotDelivered(Guid sessionId, string deliveryId, string reason, IReadOnlyList<string>? steps = null)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A not-delivered entry must say why.", nameof(reason));
        Write(sessionId, deliveryId, DeliveryState.NotDelivered, reason, steps);
    }

    /// <summary>The words left the composer and the late watch ended without proof either way, for <paramref name="reason"/>
    /// (issue #3484). The id never begins again.</summary>
    public void MarkUnconfirmed(Guid sessionId, string deliveryId, string reason, IReadOnlyList<string>? steps = null)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("An unconfirmed entry must say why.", nameof(reason));
        Write(sessionId, deliveryId, DeliveryState.Unconfirmed, reason, steps);
    }

    private void Write(Guid sessionId, string deliveryId, DeliveryState state, string? reason, IReadOnlyList<string>? steps)
    {
        RequireId(deliveryId);
        FileLog.Write($"[DeliveryRecord] Write: session={sessionId}, deliveryId={deliveryId}, state={DeliveryStates.Format(state)}, steps={steps?.Count ?? 0}");
        UnderSessionLock(sessionId, $"writing {DeliveryStates.Format(state)}", () =>
        {
            Append(sessionId, ReadEntries(sessionId), deliveryId, state, reason, steps);
            return true;
        });
    }

    /// <summary>Test seam: the per-session lock, so a test can hold it the way a long write would.</summary>
    internal object SessionLockForTests(Guid sessionId) => LockFor(sessionId);

    /// <summary>Test seam: runs just before the session's file is read. Null in the Director.</summary>
    internal Action? BeforeFileReadForTests { get; set; }

    private object LockFor(Guid sessionId) => _sessionLocks.GetOrAdd(sessionId, _ => new object());

    /// <summary>How long taking a session's lock file may take before it is reported as a record that cannot be used. Its
    /// holder keeps it only for one small read or rewrite, so a wait this long means something is wrong, not busy.</summary>
    internal static readonly TimeSpan LockFileGiveUp = TimeSpan.FromSeconds(60);

    /// <summary>The lock file for <paramref name="sessionId"/>: beside its record, named so no <c>*.jsonl</c> sweep reads it.</summary>
    public string LockFileFor(Guid sessionId) => Path.Combine(_directory, $"{sessionId:D}.lock");

    /// <summary>
    /// Runs <paramref name="body"/> under this session's lock. A wait for the lock that runs past a few seconds says what
    /// it waits for and, when it gets the lock, how long it waited (the phase 3 lines, <see cref="Drivers.SendWaitNotice"/>)
    /// - so a slow answer from the delivery-state verb can be told apart from a slow file or a starved process (phase 6).
    ///
    /// TWO LOCKS, BECAUSE TWO DIRECTORS CAN SHARE THESE FILES (review of pull request 3491). The in-process lock orders
    /// this process's own callers; the lock file (<see cref="LockFileFor"/>, opened with no sharing) orders this process
    /// against any other Director on the same state files. Every read-then-rewrite goes through here, so without the
    /// second lock one Director could rewrite a session's file from a snapshot taken before another Director wrote a
    /// claim, and erase that claim - after which the same words could begin, and be typed, again. The operating system
    /// drops the lock file's lock when its holder exits, so a Director that dies holding it blocks nobody.
    /// </summary>
    private T UnderSessionLock<T>(Guid sessionId, string doing, Func<T> body)
    {
        return UnderProcessLock(sessionId, doing, () =>
        {
            using var acrossProcesses = TakeLockFile(sessionId, doing);
            return body();
        });
    }

    private FileStream TakeLockFile(Guid sessionId, string doing)
    {
        Directory.CreateDirectory(_directory);
        var path = LockFileFor(sessionId);
        Drivers.SendWaitNotice? notice = null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var held = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                notice?.End("got the lock file");
                return held;
            }
            catch (UnauthorizedAccessException ex)
            {
                FileLog.Write($"[DeliveryRecord] TakeLockFile FAILED: {path}: {ex.Message}");
                throw new DeliveryRecordUnreadableException(path, $"its lock file cannot be opened: {ex.Message}", ex);
            }
            catch (IOException ex)
            {
                // Another Director holds it for its own read or rewrite. It is kept only for that, so the wait is short.
                if (clock.Elapsed >= LockFileGiveUp)
                {
                    notice?.End($"GAVE UP: {ex.Message}");
                    FileLog.Write($"[DeliveryRecord] TakeLockFile FAILED: {path}: not free after {LockFileGiveUp.TotalSeconds:0}s: {ex.Message}");
                    throw new DeliveryRecordUnreadableException(path,
                        $"its lock file was not free after {LockFileGiveUp.TotalSeconds:0} seconds ({ex.Message})", ex);
                }
                notice ??= new Drivers.SendWaitNotice("DeliveryRecord",
                    $"the delivery record lock file {path} (session {sessionId}), for {doing}",
                    $"{LockFileGiveUp.TotalSeconds:0}s - another Director holds it for one read or rewrite");
                notice.Check();
                Thread.Sleep(20);
            }
        }
    }

    private T UnderProcessLock<T>(Guid sessionId, string doing, Func<T> body)
    {
        var gate = LockFor(sessionId);
        if (!Monitor.TryEnter(gate))
        {
            var notice = new Drivers.SendWaitNotice("DeliveryRecord",
                $"the delivery record lock for session {sessionId}, for {doing}", "none - it waits for the record's current reader or writer");
            if (!Monitor.TryEnter(gate, Drivers.SendWaitNotice.NoticeAfter))
            {
                notice.Check();
                Monitor.Enter(gate);
            }
            notice.End("got the lock");
        }
        try
        {
            return body();
        }
        finally
        {
            Monitor.Exit(gate);
        }
    }

    private static void RequireId(string deliveryId)
    {
        if (string.IsNullOrWhiteSpace(deliveryId)) throw new ArgumentException("A delivery id is required.", nameof(deliveryId));
    }

    private static DeliveryLookup Latest(List<DeliveryRecordEntry> entries, string deliveryId)
    {
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var entry = entries[i];
            if (!string.Equals(entry.Id, deliveryId, StringComparison.Ordinal)) continue;
            // Already checked when the file was read; a word that is not one of the five made the file unreadable.
            DeliveryStates.TryParse(entry.State, out var state);
            return new DeliveryLookup(state, entry.Reason, entry.At);
        }
        return new DeliveryLookup(DeliveryState.Unknown, null, null);
    }

    /// <summary>Every entry in the session's file, oldest first. No file -&gt; none. A file that exists and cannot be read,
    /// or holds a line that is not a well-formed entry, throws: it is never read as empty.</summary>
    private List<DeliveryRecordEntry> ReadEntries(Guid sessionId)
    {
        var path = FileFor(sessionId);
        if (!File.Exists(path)) return new List<DeliveryRecordEntry>();

        string[] lines;
        var notice = new Drivers.SendWaitNotice("DeliveryRecord",
            $"the file system to read the delivery record {path} (session {sessionId})", "none - one small file");
        try
        {
            BeforeFileReadForTests?.Invoke();
            lines = File.ReadAllLines(path, Encoding.UTF8);
            notice.End($"read {lines.Length} lines");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The wait names its end on the failure path too (phase 6 review, Pi finding 2): a slow read that then
            // throws is the exact path where the diagnosis is hardest, and a WAITING line without its WAIT ENDED
            // there breaks the pair the phase 3 lines exist for.
            notice.End($"FAILED: {ex.Message}");
            FileLog.Write($"[DeliveryRecord] ReadEntries FAILED: {path}: {ex.Message}");
            throw new DeliveryRecordUnreadableException(path, ex.Message, ex);
        }

        var entries = new List<DeliveryRecordEntry>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) continue;
            DeliveryRecordEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<DeliveryRecordEntry>(lines[i], JsonOptions);
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[DeliveryRecord] ReadEntries FAILED: {path} line {i + 1}: {ex.Message}");
                throw new DeliveryRecordUnreadableException(path, $"line {i + 1} is not an entry: {ex.Message}", ex);
            }
            if (entry is null || string.IsNullOrWhiteSpace(entry.Id) || !DeliveryStates.TryParse(entry.State, out _))
            {
                FileLog.Write($"[DeliveryRecord] ReadEntries FAILED: {path} line {i + 1} has no id or an unknown state");
                throw new DeliveryRecordUnreadableException(path, $"line {i + 1} has no id or a state that is not one of the five");
            }
            entries.Add(entry);
        }
        return entries;
    }

    /// <summary>Adds one entry, drops entries older than <see cref="Retention"/>, and replaces the file in one move. A
    /// <c>delivering</c> entry carries this record's owner. Returns the entries now on disk.</summary>
    private List<DeliveryRecordEntry> Append(Guid sessionId, List<DeliveryRecordEntry> entries, string deliveryId, DeliveryState state, string? reason,
        IReadOnlyList<string>? steps = null)
    {
        var now = _utcNow();
        var cutoff = now - Retention;
        var kept = entries.Where(e => e.At >= cutoff).ToList();
        var delivering = state == DeliveryState.Delivering;
        kept.Add(new DeliveryRecordEntry
        {
            Id = deliveryId,
            State = DeliveryStates.Format(state),
            Reason = reason,
            At = now,
            OwnerProcessId = delivering ? _owner.ProcessId : null,
            OwnerStartedAt = delivering ? _owner.StartedAtUtc : null,
            Steps = steps is { Count: > 0 } ? steps.ToList() : null,
        });

        Directory.CreateDirectory(_directory);
        var path = FileFor(sessionId);
        var temporary = path + ".tmp";
        var text = new StringBuilder();
        foreach (var entry in kept)
            text.Append(JsonSerializer.Serialize(entry, JsonOptions)).Append('\n');
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text.ToString());
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
        var dropped = entries.Count - (kept.Count - 1);
        FileLog.Write($"[DeliveryRecord] wrote session={sessionId}, deliveryId={deliveryId}, state={DeliveryStates.Format(state)}, entries={kept.Count}" +
                      (dropped > 0 ? $", dropped {dropped} older than {Retention.TotalDays:0} days" : ""));
        return kept;
    }
}
