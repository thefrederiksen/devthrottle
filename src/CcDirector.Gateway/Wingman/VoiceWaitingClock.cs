using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// Issue #2576: the Gateway-owned per-session clock recording WHEN a voice session last had no
/// playable narration, so every surface can say how long it has been waiting for its voice.
///
/// THE DEFECT THIS EXISTS FOR. A session sat on "Preparing voice" for forty-eight minutes on
/// 11 August and no screen could say so. The needs-you clock beside this one is stamped only when
/// the folded colour is RED (see <c>GatewayEndpoints</c>), and a session waiting for voice is
/// YELLOW - so the one clock the product had was, by construction, never running for exactly the
/// sessions that were stuck. There was no fact anywhere from which "waiting 48 minutes" could be
/// rendered, which is why the owner could see something was wrong and the product could not.
///
/// Deliberately a SECOND clock rather than a widening of <see cref="Briefing.NeedsYouClock"/>.
/// They answer different questions - "how long has this session wanted a human?" and "how long has
/// this session been without its voice?" - and the two episodes start and end at different moments.
/// Merging them would make one number mean whichever the reader assumed.
///
/// In-memory by design (derived state, not the durable record) and re-derived after a Gateway
/// restart on the next waiting refresh. The same entry/hold/clear rule as the needs-you clock:
///   waiting and no timestamp yet -> stamp UtcNow (entry).
///   waiting and one already held -> hold it, so the value never advances mid-episode.
///   not waiting                  -> clear it; audio arriving ENDS the episode, and a later one
///                                   stamps a strictly-later moment.
///
/// Keyed by (tenant, sessionId) exactly as the needs-you clock is, and for the same reason: two
/// accounts can run sessions with the same id, and a bare-sid key would let one tenant's "voice
/// arrived" clear another tenant's entry.
///
/// KNOWN AND NOT ADDRESSED HERE: an entry is dropped only when a refresh reports the session NOT
/// waiting, so a session DELETED while it waits leaves its row behind until the Gateway restarts.
/// <see cref="Briefing.NeedsYouClock"/> has exactly the same shape and the same gap, so this is the
/// existing pattern rather than a new one - which is the argument for matching it, not for calling it
/// correct. A per-session timestamp is small and a Gateway restart clears the lot, so it is a slow
/// leak rather than a live risk; fixing it properly means pruning both clocks against the roster in
/// one place, and that belongs in its own change rather than smuggled into this one. A Forget method
/// was written here first and deleted: nothing called it, and a cleanup entry point with no caller
/// reads as a policy that exists when it does not.
/// </summary>
public sealed class VoiceWaitingClock
{
    private readonly ConcurrentDictionary<(TenantId Tenant, string SessionId), DateTime> _since = new();
    private readonly Func<DateTime> _utcNow;

    /// <param name="utcNow">The clock a new wait starts from. Production passes nothing and reads the wall clock.</param>
    public VoiceWaitingClock(Func<DateTime>? utcNow = null) => _utcNow = utcNow ?? (() => DateTime.UtcNow);

    /// <summary>
    /// What <see cref="Stamp"/> would answer right now, WITHOUT starting or clearing a wait. For a fold that records
    /// what the display push shows and must not move the push's own clock (the Wingman inspector's trace colour): a wait
    /// already running answers its start, one not yet started answers now - the moment Stamp would give it.
    /// </summary>
    public DateTime? Peek(TenantId tenant, string sessionId, bool isWaitingForVoice)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        if (!isWaitingForVoice) return null;
        return _since.TryGetValue((tenant, sessionId), out var since) ? since : _utcNow();
    }

    /// <summary>
    /// Apply the entry/hold/clear rule for one session and return the timestamp to stamp on its
    /// <see cref="Contracts.SessionDto.VoiceWaitingSince"/> (UTC), or null when it is not waiting.
    /// </summary>
    /// <param name="tenant">The tenant that owns the session being stamped.</param>
    /// <param name="sessionId">The session's stable id.</param>
    /// <param name="isWaitingForVoice">Whether this session is a voice session with no playable
    /// audio this refresh, and is not mid-turn. Computed by the caller from the same facts the
    /// display fold reads, so the clock and the words cannot disagree about whether it is waiting.</param>
    public DateTime? Stamp(TenantId tenant, string sessionId, bool isWaitingForVoice)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);

        var key = (tenant, sessionId);
        if (!isWaitingForVoice)
        {
            if (_since.TryRemove(key, out _))
                FileLog.Write($"[VoiceWaitingClock] tenant={tenant.ToLogString()} sid={sessionId}: voice arrived or the turn moved on, cleared VoiceWaitingSince");
            return null;
        }

        var added = false;
        var since = _since.GetOrAdd(key, _ =>
        {
            added = true;
            return _utcNow();
        });
        if (added)
            FileLog.Write($"[VoiceWaitingClock] tenant={tenant.ToLogString()} sid={sessionId}: started waiting for voice, VoiceWaitingSince={since:o}");
        return since;
    }

    /// <summary>
    /// Test-only: a wait that began at <paramref name="since"/>, so a test on a booted host can reach a wait that has
    /// given up (three minutes) without sleeping. Production never calls it.
    /// </summary>
    internal void StartWaitForTest(TenantId tenant, string sessionId, DateTime since)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        _since[(tenant, sessionId)] = since;
    }
}
