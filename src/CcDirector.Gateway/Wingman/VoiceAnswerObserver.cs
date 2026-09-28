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

    // (tenant, sid) -> the LastOwnerTurnAtUtc this observer last saw for that voice session (null when it had none).
    private readonly ConcurrentDictionary<(TenantId Tenant, string Sid), DateTime?> _lastOwnerTurn = new();

    public VoiceAnswerObserver(WingmanVoiceService voice)
    {
        _voice = voice ?? throw new ArgumentNullException(nameof(voice));
    }

    /// <summary>
    /// Observe one pushed session. Returns what the owner's answer settled, or null when this push is not the owner
    /// answering a voice session (not a voice session, first sight, or no new owner turn).
    /// </summary>
    public VoiceListeningLedger.AnswerOutcome? Observe(TenantId tenant, SessionDto? session)
    {
        var sid = session?.SessionId;
        if (session is null || string.IsNullOrEmpty(sid)) return null;
        var key = (tenant, sid);

        if (!_voice.IsVoiceSession(tenant, sid))
        {
            _lastOwnerTurn.TryRemove(key, out _);
            return null;
        }

        var seen = session.LastOwnerTurnAtUtc;
        if (!_lastOwnerTurn.TryGetValue(key, out var previous))
        {
            _lastOwnerTurn[key] = seen;   // first sight: where the stamp stands now, judged against nothing
            return null;
        }
        var ownerTurnMoved = seen is not null && (previous is null || seen.Value > previous.Value);
        if (!ownerTurnMoved)
        {
            // The session is working and the owner did not start it: the stop it was on has had its next turn, and
            // that turn was not his answer. Retired, so a later owner message is never judged against it (review of
            // step 2). A working push of the owner's OWN turn carries his new stamp and is judged below instead.
            if (IsWorking(session.ActivityState))
                _voice.Listening.RetireUnanswered(tenant, sid, "its next turn began without a message from the owner");
            return null;
        }
        if (!_lastOwnerTurn.TryUpdate(key, seen, previous)) return null;   // a concurrent push already took this turn

        // The Gateway's own prompts - a supervisor's "continue", a Session Rule firing - reach the Director as ordinary
        // prompts and are stamped as the owner's turn. The Gateway recorded that it sent one; this stamp is that one.
        if (_voice.Listening.TakeAutomaticPrompt(tenant, sid))
        {
            FileLog.Write($"[VoiceAnswerObserver] owner-turn stamp moved by the Gateway's own prompt, not the owner: tenant={tenant.ToLogString()} sid={sid}");
            return null;
        }

        FileLog.Write($"[VoiceAnswerObserver] owner drove a turn on a voice session: tenant={tenant.ToLogString()} sid={sid} ownerTurn={seen!.Value:O}");
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
