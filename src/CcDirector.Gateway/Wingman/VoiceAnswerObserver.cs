using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// DID THE OWNER ANSWER? Voice mode auto-off, step 2 (owner ruling 28 September 2026): a voice session's stop is
/// ANSWERED when its next turn begins with a message from the owner, and only then. Every pushed session passes
/// through here; when a voice session reports the owner drove a new turn, the stop it was on is settled against the
/// listening ledger - heard if its narration was played, unheard if not.
///
/// THE ONE FACT THAT SAYS "THE OWNER": <see cref="SessionDto.LastOwnerTurnAtUtc"/>. The Director stamps it at its input
/// choke points only when the owner submits - a person typing in the terminal, a Cockpit or phone prompt, a dictation
/// arriving - and never for anything else: a fleet message or another agent's prompt (the Gateway marks every
/// session-key caller agent-driven), product text such as a handover or a queue drain, or the agent carrying on by
/// itself, which submits nothing at all. A schedule starts a NEW session and is never the next turn of an existing
/// one. That is the same fact, and the same distinction, the snooze machine already rules on when it decides the
/// owner has come back.
///
/// It is compared only with ITS OWN previous value for the same session, never with a Gateway time: the stamp is the
/// Director's clock, the narration's is the Gateway's, and the two machines' clocks need not agree. The first sight of
/// a session (and the first after a Gateway restart) only records where the stamp stands, so an owner turn the Gateway
/// did not see happen is never judged - the direction that can only leave a stop uncounted.
///
/// Only voice sessions are tracked, which bounds the memory to them; a session that leaves voice mode is dropped on
/// its next push, and one that joins starts from a fresh baseline.
/// </summary>
public sealed class VoiceAnswerObserver
{
    private readonly WingmanVoiceService _voice;

    // (tenant, sid) -> what this observer last saw of that voice session: its owner-turn stamp and its completed-turn
    // count (either null when the Director did not report it), and its activity state.
    private readonly ConcurrentDictionary<(TenantId Tenant, string Sid), Seen> _seen = new();

    private readonly record struct Seen(DateTime? OwnerTurn, int? Turns, string Activity);

    public VoiceAnswerObserver(WingmanVoiceService voice)
    {
        _voice = voice ?? throw new ArgumentNullException(nameof(voice));
    }

    /// <summary>
    /// Observe one pushed session. Returns what the owner's answer settled, or null when this push is not the owner
    /// answering a voice session (not a voice session, first sight, no new owner turn, or a history it cannot read).
    ///
    /// A push can be missed - a dropped delta, a tunnel down for a whole turn - so nothing here depends on SEEING a
    /// transient state. What it reads are the Director's two monotonic facts: the owner-turn stamp and the count of
    /// completed turns. Between two pushes, the owner answered the stop only if his stamp moved and NO turn completed
    /// before his began: none if his turn is still running, exactly one (his own) if it has settled. Anything else - a
    /// turn that completed with no owner stamp, more turns than his, a count that went backwards or is unknown - means
    /// the stop's next turn was not (or cannot be shown to be) his, and the stop is retired unjudged (review of step 2).
    ///
    /// What it cannot see: the agent resuming by itself AND the owner then messaging it, both inside one gap between
    /// pushes, with no turn completing. That needs the owner to have sent a message without playing the narration,
    /// which is the very thing the count measures; only which of two unplayed stops it is charged to can be wrong.
    /// </summary>
    public VoiceListeningLedger.AnswerOutcome? Observe(TenantId tenant, SessionDto? session)
    {
        var sid = session?.SessionId;
        if (session is null || string.IsNullOrEmpty(sid)) return null;
        var key = (tenant, sid);

        if (!_voice.IsVoiceSession(tenant, sid))
        {
            _seen.TryRemove(key, out _);
            return null;
        }

        var now = new Seen(session.LastOwnerTurnAtUtc, session.TurnCount, session.ActivityState ?? "");
        if (!_seen.TryGetValue(key, out var before))
        {
            _seen[key] = now;   // first sight: where the facts stand now, judged against nothing
            return null;
        }
        if (now == before) return null;
        if (!_seen.TryUpdate(key, now, before)) return null;   // a concurrent push already took this change

        var ownerTurnMoved = now.OwnerTurn is not null && (before.OwnerTurn is null || now.OwnerTurn.Value > before.OwnerTurn.Value);
        if (!ownerTurnMoved && now.Turns == before.Turns)
        {
            // Nothing completed and the owner did not start anything, yet the session is working or has changed state -
            // from waiting for input to a permission prompt, say. It has moved on without him: the stop has had its next
            // turn, and it was not his answer. Retired on the FIRST push that shows it, whatever state that push
            // carries, so a missed Working push does not hide it (review of step 2).
            if (IsWorking(now.Activity) || !string.Equals(now.Activity, before.Activity, StringComparison.OrdinalIgnoreCase))
                _voice.Listening.RetireUnanswered(tenant, sid, $"it moved from {before.Activity} to {now.Activity} without a message from the owner");
            return null;
        }
        int? completed = now.Turns is int n && before.Turns is int b && n >= b ? n - b : null;
        var ownTurnOnly = IsWorking(session.ActivityState) ? 0 : 1;

        if (!ownerTurnMoved || completed is null || completed.Value > ownTurnOnly)
        {
            _voice.Listening.RetireUnanswered(tenant, sid, ownerTurnMoved
                ? $"another turn completed around the owner's (completed={completed?.ToString() ?? "unknown"})"
                : "a turn completed without a message from the owner");
            return null;
        }

        FileLog.Write($"[VoiceAnswerObserver] owner drove a turn on a voice session: tenant={tenant.ToLogString()} sid={sid} ownerTurn={now.OwnerTurn!.Value:O}");
        return _voice.Listening.NoteOwnerAnswered(tenant, sid);
    }

    /// <summary>Working or Starting: there is a turn in progress. The same two states the snooze machine calls work.</summary>
    private static bool IsWorking(string? activity) =>
        string.Equals(activity, "Working", StringComparison.OrdinalIgnoreCase)
        || string.Equals(activity, "Starting", StringComparison.OrdinalIgnoreCase);

    /// <summary>Observe a whole pushed snapshot - the reconnect path.</summary>
    public void ObserveSnapshot(TenantId tenant, IReadOnlyList<SessionDto>? sessions)
    {
        if (sessions is null) return;
        foreach (var s in sessions)
            Observe(tenant, s);
    }
}
