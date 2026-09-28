using System.Collections.Concurrent;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// IS THE OWNER LISTENING? The account's record of which narration each voice session is on, and whether that
/// narration was PLAYED (voice mode auto-off, owner ruling 28 September 2026).
///
/// Voice mode decides narration spend, and nothing ever switched it off: the owner turns voice on in the car, goes
/// back to the desktop, and every stop keeps paying for narration and speech nobody hears. The owner's rule is that a
/// stop is handled WITHOUT VOICE when he answers the session and its narration was never played, and that five of
/// those in a row switch voice mode off. This ledger holds the one half of that only a player can report - was it
/// played - keyed to the one narration it was about.
///
/// A PLAY IS A FACT THE PHONE REPORTS; A DOWNLOAD IS NOT A PLAY. The phone pre-downloads every clip the moment it is
/// ready (client-core voice/clips.ts), so the audio route being read says nothing about whether anyone listened. The
/// player reports a play by naming the clip it started, and only a report naming the session's CURRENT narration
/// marks that stop heard: a report about a clip a newer stop has replaced is about the past, and says nothing about
/// this stop.
///
/// A narration is identified by the moment it became ready (<see cref="WingmanVoiceService.VoiceReady.AtUtc"/>),
/// which is exactly the <c>generatedAt</c> stamp the phone keys its downloaded clip by - so the phone names a clip in
/// the words it already holds, and the comparison is between two copies of the Gateway's own clock.
///
/// Which narration each session is on is held in memory. A Gateway restart forgets which narration was played, and
/// forgetting only ever leads to a stop NOT being counted, which is the safe direction: the switch-off this feeds must
/// never fire on a guess.
///
/// THE COUNT (step 3). ONE count for the whole account: every stop handled without voice adds one, and any narration
/// played, on any session, sets it back to zero - only a play, never a dictation, a reply or an explain press. It is
/// persisted to <c>voice-listening.json</c> in the account's voice partition, beside <c>voice-sessions.json</c>, so a
/// Gateway restart does not reset it. A count file that cannot be read starts the account at zero and says so in the
/// log - the direction that can only delay a switch-off, never cause one.
/// </summary>
public sealed class VoiceListeningLedger
{
    /// <summary>One voice session's latest narration: when it became ready, and whether it has been played.</summary>
    public readonly record struct NarrationStop(DateTime NarrationAtUtc, bool Played);

    /// <summary>What the owner answering a voice session settled about the stop it was on.</summary>
    public enum AnswerOutcome
    {
        /// <summary>No narration was on record for the stop, so there was nothing to hear and nothing is judged.</summary>
        NoNarration,
        /// <summary>The narration was played before the owner answered: the stop was handled WITH voice.</summary>
        Heard,
        /// <summary>The narration was never played and the owner answered anyway: the stop was handled WITHOUT voice.</summary>
        Unheard,
    }

    /// <summary>The persisted part of one account's listening record.</summary>
    private sealed class AccountRecord
    {
        /// <summary>Stops handled without voice since a narration was last played.</summary>
        public int UnheardInARow { get; set; }
    }

    private const string FileName = "voice-listening.json";

    // tenant -> (sid -> the session's latest narration). Only the latest per session is kept: a newer narration is a
    // newer stop, and an older one that went unplayed and unanswered counts for nothing (the owner's rule).
    private readonly ConcurrentDictionary<TenantId, ConcurrentDictionary<string, NarrationStop>> _stops = new();

    // tenant -> its persisted record, loaded from disk on first use. Every read-modify-write of a record holds its lock.
    private readonly ConcurrentDictionary<TenantId, AccountRecord> _accounts = new();

    // The directory holding one account's voice state (the voice service's tenant partition). Null keeps the record in
    // memory only.
    private readonly Func<TenantId, string>? _stateDirectory;

    /// <param name="stateDirectory">The directory one account's record is persisted in. The voice service passes its
    /// tenant partition, so the count sits beside that account's voice-sessions.json. Null keeps it in memory.</param>
    public VoiceListeningLedger(Func<TenantId, string>? stateDirectory = null)
    {
        _stateDirectory = stateDirectory;
    }

    private ConcurrentDictionary<string, NarrationStop> StopsFor(TenantId tenant)
    {
        RequireValid(tenant);
        return _stops.GetOrAdd(tenant, _ => new ConcurrentDictionary<string, NarrationStop>(StringComparer.Ordinal));
    }

    private static void RequireValid(TenantId tenant)
    {
        if (!tenant.IsValid)
            throw new ArgumentException("The listening ledger needs a valid tenant; an unresolved tenant is denied, never defaulted.", nameof(tenant));
    }

    /// <summary>A narration became ready for this session: it is now the session's current stop, not yet played.</summary>
    public void NoteNarrationReady(TenantId tenant, string sid, DateTime narrationAtUtc)
    {
        if (string.IsNullOrEmpty(sid)) return;
        StopsFor(tenant)[sid] = new NarrationStop(narrationAtUtc, Played: false);
        FileLog.Write($"[VoiceListeningLedger] narration ready: tenant={tenant.ToLogString()} sid={sid} at={narrationAtUtc:O}");
    }

    /// <summary>
    /// The player started playing the narration made at <paramref name="narrationAtUtc"/>. Any play sets the account's
    /// unheard count back to zero. Returns true when that is this session's current narration and it is now recorded
    /// as played; false when the ledger holds no narration for the session, or the report names a different one (a
    /// newer stop has replaced it).
    /// </summary>
    public bool NotePlayed(TenantId tenant, string sid, DateTime narrationAtUtc)
    {
        if (string.IsNullOrEmpty(sid)) return false;
        // ANY narration played sets the count back to zero - including one a newer stop has since replaced, or one from
        // before a restart. The owner listened; that is the whole question the count asks.
        ResetUnheard(tenant, $"a narration was played on {sid}");
        var stops = StopsFor(tenant);
        while (stops.TryGetValue(sid, out var current))
        {
            if (current.NarrationAtUtc != narrationAtUtc)
            {
                FileLog.Write($"[VoiceListeningLedger] play report for a narration that is not current: tenant={tenant.ToLogString()} sid={sid} reported={narrationAtUtc:O} current={current.NarrationAtUtc:O}");
                return false;
            }
            if (current.Played) return true;
            if (stops.TryUpdate(sid, current with { Played = true }, current))
            {
                FileLog.Write($"[VoiceListeningLedger] narration played: tenant={tenant.ToLogString()} sid={sid} at={narrationAtUtc:O}");
                return true;
            }
        }
        FileLog.Write($"[VoiceListeningLedger] play report for a session with no narration on record: tenant={tenant.ToLogString()} sid={sid} reported={narrationAtUtc:O}");
        return false;
    }

    /// <summary>
    /// The owner answered this session - its next turn began with a message from him. That settles the stop it was
    /// on, exactly once: the narration on record is taken off the ledger and judged heard or unheard, and a second
    /// answer to the same stop finds nothing to judge. An unheard stop adds one to the account's count. Who started
    /// the turn is not decided here; the caller has already established it was the owner (see
    /// <see cref="VoiceAnswerObserver"/>).
    /// </summary>
    public AnswerOutcome NoteOwnerAnswered(TenantId tenant, string sid)
    {
        if (string.IsNullOrEmpty(sid)) return AnswerOutcome.NoNarration;
        if (!StopsFor(tenant).TryRemove(sid, out var stop))
        {
            FileLog.Write($"[VoiceListeningLedger] owner answered with no narration on record: tenant={tenant.ToLogString()} sid={sid}");
            return AnswerOutcome.NoNarration;
        }
        var outcome = stop.Played ? AnswerOutcome.Heard : AnswerOutcome.Unheard;
        var count = outcome == AnswerOutcome.Unheard ? AddUnheard(tenant) : UnheardInARow(tenant);
        FileLog.Write($"[VoiceListeningLedger] owner answered: tenant={tenant.ToLogString()} sid={sid} narration={stop.NarrationAtUtc:O} outcome={outcome} unheardInARow={count}");
        return outcome;
    }

    /// <summary>
    /// The stop's next turn began and the owner did not start it - the agent carried on by itself, or another agent or
    /// the product woke it. That stop was not answered, and it never will be: an unplayed, unanswered narration counts
    /// for nothing (the owner's rule), so it is retired, and a later owner message is not judged against it.
    /// </summary>
    public void RetireUnanswered(TenantId tenant, string sid, string why)
    {
        if (string.IsNullOrEmpty(sid)) return;
        if (StopsFor(tenant).TryRemove(sid, out var stop))
            FileLog.Write($"[VoiceListeningLedger] stop retired unanswered ({why}): tenant={tenant.ToLogString()} sid={sid} narration={stop.NarrationAtUtc:O} played={stop.Played}");
    }

    private static void RequireValid(TenantId tenant)
    {
        if (!tenant.IsValid)
            throw new ArgumentException("The listening ledger needs a valid tenant; an unresolved tenant is denied, never defaulted.", nameof(tenant));
    }

    /// <summary>The session is no longer a voice session: its narration is about nothing anyone asked to hear.</summary>
    public void Forget(TenantId tenant, string sid)
    {
        if (string.IsNullOrEmpty(sid)) return;
        StopsFor(tenant).TryRemove(sid, out _);
    }

    /// <summary>The session's current narration, or null when none is on record.</summary>
    public NarrationStop? StopFor(TenantId tenant, string sid) =>
        StopsFor(tenant).TryGetValue(sid, out var stop) ? stop : null;

    /// <summary>How many stops the owner has handled without voice since a narration was last played.</summary>
    public int UnheardInARow(TenantId tenant)
    {
        var record = RecordFor(tenant);
        lock (record) return record.UnheardInARow;
    }

    private int AddUnheard(TenantId tenant)
    {
        var record = RecordFor(tenant);
        lock (record)
        {
            record.UnheardInARow++;
            Save(tenant, record);
            return record.UnheardInARow;
        }
    }

    /// <summary>Set the count back to zero. No write when it already is zero.</summary>
    private void ResetUnheard(TenantId tenant, string why)
    {
        var record = RecordFor(tenant);
        lock (record)
        {
            if (record.UnheardInARow == 0) return;
            FileLog.Write($"[VoiceListeningLedger] unheard count reset from {record.UnheardInARow}: tenant={tenant.ToLogString()} ({why})");
            record.UnheardInARow = 0;
            Save(tenant, record);
        }
    }

    private AccountRecord RecordFor(TenantId tenant)
    {
        RequireValid(tenant);
        return _accounts.GetOrAdd(tenant, Load);
    }

    private AccountRecord Load(TenantId tenant)
    {
        if (_stateDirectory is null) return new AccountRecord();
        var path = Path.Combine(_stateDirectory(tenant), FileName);
        if (!File.Exists(path)) return new AccountRecord();
        try
        {
            var record = JsonSerializer.Deserialize<AccountRecord>(File.ReadAllText(path))
                ?? throw new InvalidDataException("the file holds no record");
            FileLog.Write($"[VoiceListeningLedger] loaded: tenant={tenant.ToLogString()} unheardInARow={record.UnheardInARow}");
            return record;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            FileLog.Write($"[VoiceListeningLedger] load FAILED, the account starts at zero: tenant={tenant.ToLogString()}: {ex.GetType().Name}: {Redact(ex.Message, tenant)}");
            return new AccountRecord();
        }
    }

    private void Save(TenantId tenant, AccountRecord record)
    {
        if (_stateDirectory is null) return;
        try
        {
            var dir = _stateDirectory(tenant);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, FileName), JsonSerializer.Serialize(record));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLog.Write($"[VoiceListeningLedger] save FAILED: tenant={tenant.ToLogString()}: {ex.GetType().Name}: {Redact(ex.Message, tenant)}");
        }
    }

    /// <summary>The partition directory IS the tenant id, so a path in an exception message carries it; log the hashed
    /// form, as the voice service does.</summary>
    private static string Redact(string text, TenantId tenant)
        => string.IsNullOrEmpty(text) || !tenant.IsValid
            ? text
            : text.Replace(tenant.Value, tenant.ToLogString(), StringComparison.Ordinal);
}
