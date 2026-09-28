using System.Collections.Concurrent;
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
/// counts: a report about a clip a newer stop has replaced is about the past, and says nothing about this stop.
///
/// A narration is identified by the moment it became ready (<see cref="WingmanVoiceService.VoiceReady.AtUtc"/>),
/// which is exactly the <c>generatedAt</c> stamp the phone keys its downloaded clip by - so the phone names a clip in
/// the words it already holds, and the comparison is between two copies of the Gateway's own clock.
///
/// Held in memory. A Gateway restart forgets which narration was played, and forgetting only ever leads to a stop
/// NOT being counted, which is the safe direction: the switch-off this feeds must never fire on a guess.
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

    // tenant -> (sid -> the session's latest narration). Only the latest per session is kept: a newer narration is a
    // newer stop, and an older one that went unplayed and unanswered counts for nothing (the owner's rule).
    private readonly ConcurrentDictionary<TenantId, ConcurrentDictionary<string, NarrationStop>> _stops = new();

    private ConcurrentDictionary<string, NarrationStop> StopsFor(TenantId tenant)
    {
        if (!tenant.IsValid)
            throw new ArgumentException("The listening ledger needs a valid tenant; an unresolved tenant is denied, never defaulted.", nameof(tenant));
        return _stops.GetOrAdd(tenant, _ => new ConcurrentDictionary<string, NarrationStop>(StringComparer.Ordinal));
    }

    /// <summary>A narration became ready for this session: it is now the session's current stop, not yet played.</summary>
    public void NoteNarrationReady(TenantId tenant, string sid, DateTime narrationAtUtc)
    {
        if (string.IsNullOrEmpty(sid)) return;
        StopsFor(tenant)[sid] = new NarrationStop(narrationAtUtc, Played: false);
        FileLog.Write($"[VoiceListeningLedger] narration ready: tenant={tenant.ToLogString()} sid={sid} at={narrationAtUtc:O}");
    }

    /// <summary>
    /// The player started playing the narration made at <paramref name="narrationAtUtc"/>. Returns true when that is
    /// this session's current narration and it is now recorded as played; false when the ledger holds no narration
    /// for the session, or the report names a different one (a newer stop has replaced it).
    /// </summary>
    public bool NotePlayed(TenantId tenant, string sid, DateTime narrationAtUtc)
    {
        if (string.IsNullOrEmpty(sid)) return false;
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
    /// answer to the same stop finds nothing to judge. Who started the turn is not decided here; the caller has
    /// already established it was the owner (see <see cref="VoiceAnswerObserver"/>).
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
        FileLog.Write($"[VoiceListeningLedger] owner answered: tenant={tenant.ToLogString()} sid={sid} narration={stop.NarrationAtUtc:O} outcome={outcome}");
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
}
