using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
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

    /// <summary>Why it was not delivered: the send's exception message. Null for every other state.</summary>
    public string? Reason { get; set; }

    /// <summary>When the state was written, in UTC.</summary>
    public DateTime At { get; set; }
}

/// <summary>What the record says about one delivery id for one session.</summary>
public sealed record DeliveryLookup(DeliveryState State, string? Reason, DateTime? At);

/// <summary>The answer to <see cref="DeliveryRecord.TryBeginDelivery"/>: either this caller now owns the delivery
/// (<see cref="Began"/>, and <c>Delivering</c> is on disk), or the id was already delivered or being delivered and
/// <see cref="Existing"/> says which - in which case nothing may be typed.</summary>
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
/// THE STATES FOLLOW <c>Session.SubmitTextAsync</c>, which either returns (delivered) or throws (not delivered): there
/// is no third outcome of a send. <c>Delivering</c> is written BEFORE the first character is typed, so a Director that
/// dies while typing leaves <c>Delivering</c> on disk.
///
/// A <c>Delivering</c> ENTRY FOUND AT START-UP STAYS <c>Delivering</c>. It is the honest answer: the words may be in,
/// wholly or partly, and nothing on this side can tell. It never becomes <c>Unknown</c> (which would let a retry type
/// it again) and never becomes <c>NotDelivered</c> (which would too). The recording is kept by the client and shown
/// back to the owner, who can read the conversation and decide; the Director does not guess for him.
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
    private readonly ConcurrentDictionary<Guid, object> _sessionLocks = new();

    public DeliveryRecord(string directory, Func<DateTime>? utcNow = null)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("A directory is required.", nameof(directory));
        _directory = directory;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>The file that holds <paramref name="sessionId"/>'s deliveries.</summary>
    public string FileFor(Guid sessionId) => Path.Combine(_directory, $"{sessionId:D}.jsonl");

    /// <summary>What became of <paramref name="deliveryId"/> for this session. Never seen -&gt; <see cref="DeliveryState.Unknown"/>.
    /// Throws <see cref="DeliveryRecordUnreadableException"/> when the file exists and cannot be read.</summary>
    public DeliveryLookup Read(Guid sessionId, string deliveryId)
    {
        RequireId(deliveryId);
        FileLog.Write($"[DeliveryRecord] Read: session={sessionId}, deliveryId={deliveryId}");
        lock (LockFor(sessionId))
        {
            var lookup = Latest(ReadEntries(sessionId), deliveryId);
            FileLog.Write($"[DeliveryRecord] Read: session={sessionId}, deliveryId={deliveryId}, state={DeliveryStates.Format(lookup.State)}");
            return lookup;
        }
    }

    /// <summary>
    /// The one step before typing: look the id up and, unless it is already delivered or being delivered, write
    /// <see cref="DeliveryState.Delivering"/> - both under this session's lock. <see cref="DeliveryClaim.Began"/> false
    /// means nothing may be typed. An id that is unknown or not delivered begins (a not-delivered one is a real retry).
    /// </summary>
    public DeliveryClaim TryBeginDelivery(Guid sessionId, string deliveryId)
    {
        RequireId(deliveryId);
        FileLog.Write($"[DeliveryRecord] TryBeginDelivery: session={sessionId}, deliveryId={deliveryId}");
        lock (LockFor(sessionId))
        {
            var entries = ReadEntries(sessionId);
            var existing = Latest(entries, deliveryId);
            if (existing.State is DeliveryState.Delivered or DeliveryState.Delivering)
            {
                FileLog.Write($"[DeliveryRecord] TryBeginDelivery: REFUSED session={sessionId}, deliveryId={deliveryId}: already {DeliveryStates.Format(existing.State)} since {existing.At:O}");
                return new DeliveryClaim(false, existing);
            }
            Append(sessionId, entries, deliveryId, DeliveryState.Delivering, null);
            FileLog.Write($"[DeliveryRecord] TryBeginDelivery: began session={sessionId}, deliveryId={deliveryId}, was={DeliveryStates.Format(existing.State)}");
            return new DeliveryClaim(true, existing);
        }
    }

    /// <summary>The send completed: the words reached the session.</summary>
    public void MarkDelivered(Guid sessionId, string deliveryId) => Write(sessionId, deliveryId, DeliveryState.Delivered, null);

    /// <summary>The send threw, or was refused before typing: the words did not reach the session, for <paramref name="reason"/>.</summary>
    public void MarkNotDelivered(Guid sessionId, string deliveryId, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A not-delivered entry must say why.", nameof(reason));
        Write(sessionId, deliveryId, DeliveryState.NotDelivered, reason);
    }

    private void Write(Guid sessionId, string deliveryId, DeliveryState state, string? reason)
    {
        RequireId(deliveryId);
        FileLog.Write($"[DeliveryRecord] Write: session={sessionId}, deliveryId={deliveryId}, state={DeliveryStates.Format(state)}");
        lock (LockFor(sessionId))
        {
            Append(sessionId, ReadEntries(sessionId), deliveryId, state, reason);
        }
    }

    private object LockFor(Guid sessionId) => _sessionLocks.GetOrAdd(sessionId, _ => new object());

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
            // Already checked when the file was read; a word that is not one of the four made the file unreadable.
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
        try
        {
            lines = File.ReadAllLines(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
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
                throw new DeliveryRecordUnreadableException(path, $"line {i + 1} has no id or a state that is not one of the four");
            }
            entries.Add(entry);
        }
        return entries;
    }

    /// <summary>Adds one entry, drops entries older than <see cref="Retention"/>, and replaces the file in one move.</summary>
    private void Append(Guid sessionId, List<DeliveryRecordEntry> entries, string deliveryId, DeliveryState state, string? reason)
    {
        var now = _utcNow();
        var cutoff = now - Retention;
        var kept = entries.Where(e => e.At >= cutoff).ToList();
        kept.Add(new DeliveryRecordEntry { Id = deliveryId, State = DeliveryStates.Format(state), Reason = reason, At = now });

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
    }
}
