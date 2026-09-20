using CcDirector.Core.Drivers;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.ControlApi.Drain;

/// <summary>
/// Everything the drain does TO a live session on this Director, behind one seam.
///
/// It is a seam and not a direct <see cref="SessionManager"/> call for one reason: a drain closes every
/// session on a Director, so a test that drove the real thing would have to start eighteen agent
/// processes to prove the leaf-first order. The order, the poll-until-absent and the never-force rule are
/// the parts that carry the risk, and they are exactly the parts a seam makes testable.
///
/// TWO CALLERS, AND THEY DO NOT HOLD THE SAME VERBS. The older drain (<see cref="DirectorDrain"/>) never
/// forces: it never calls <see cref="IDrainSessionControl.InterruptAsync"/> or
/// <see cref="IDrainSessionControl.EndAsync"/>, and a test runs it on the rig and asserts exactly that.
/// Those two verbs are here for the smart shutdown only, which has a time limit the owner chose and ends
/// what is still present when it is reached.
/// </summary>
/// <summary>
/// What happened when the drain tried to say something to a session.
///
/// THE POINT OF THE TYPE IS THAT DID-NOT-LAND IS A PRESENCE. A drain that only knows "no answer yet"
/// has to conclude from silence, and ninety minutes of silence is an absence-shaped check in its purest
/// form: it cannot tell a seat that is thinking from a seat that never heard the question. The delivery
/// path already knows the difference at the moment of the attempt - a wedged composer refuses the text
/// and says so - and this carries that answer back instead of flattening it into a bool.
/// </summary>
/// <param name="Delivered">The words reached the agent.</param>
/// <param name="SessionGone">There is no such session here any more. Not a fault - one less thing to
/// wait for.</param>
/// <param name="Reason">Why it did not land, in the words the delivery path used. Null when it did.</param>
public sealed record DrainDelivery(bool Delivered, bool SessionGone, string? Reason)
{
    /// <summary>It landed.</summary>
    public static readonly DrainDelivery Ok = new(true, false, null);

    /// <summary>The session is not here.</summary>
    public static readonly DrainDelivery Gone = new(false, true, "the session is no longer on this Director");

    /// <summary>It did not land, and here is what the delivery path said.</summary>
    /// <param name="reason">The delivery path's own words.</param>
    public static DrainDelivery Refused(string reason) => new(false, false, reason);
}

/// <summary>
/// What happened when the smart shutdown ended a session.
///
/// Three facts and not a bool, for the same reason as <see cref="DrainDelivery"/>: "it is gone because
/// I ended it", "it was already gone" and "it would not go" lead to three different rows in the record,
/// and the last one is a session still running that the record must not call closed.
/// </summary>
/// <param name="Ended">This call ended the session and it is no longer on this Director.</param>
/// <param name="SessionGone">There was no such session here when the call was made. Not a fault.</param>
/// <param name="Reason">Why it is still here, in the words the stop path used. Null when it was ended.</param>
public sealed record DrainEnd(bool Ended, bool SessionGone, string? Reason)
{
    /// <summary>It was ended and is gone.</summary>
    public static readonly DrainEnd Ok = new(true, false, null);

    /// <summary>The session was not here.</summary>
    public static readonly DrainEnd Gone = new(false, true, "the session is no longer on this Director");

    /// <summary>It is still here, and here is what the stop path said.</summary>
    /// <param name="reason">The stop path's own words.</param>
    public static DrainEnd Refused(string reason) => new(false, false, reason);
}

public interface IDrainSessionControl
{
    /// <summary>True when a session with this id is still present on this Director. FALSE IS THE
    /// CLOSE CONDITION: marking a session done is a flag and the reap is asynchronous, so a close is only
    /// finished when the session is genuinely absent.
    ///
    /// The caller must only ask this about an id it has already checked is well formed. A malformed id
    /// cannot be looked up, so the honest answer would be neither true nor false - and the drain writes a
    /// CLOSE TIME on false. It rejects such ids before they ever reach here rather than letting "I could
    /// not look it up" arrive as "it is gone".</summary>
    /// <param name="sessionId">The session id.</param>
    bool IsPresent(string sessionId);

    /// <summary>
    /// Whether this seam can look this id up AT ALL - not whether a session with it exists.
    ///
    /// It is a separate question because <see cref="IsPresent"/> has only two answers and the drain writes
    /// a CLOSE TIME on false. An id it cannot even parse would come back false, and "I could not look it
    /// up" would land in the record as "verified gone" for a session nobody ever found. The seam is what
    /// knows what it can address, so the seam is where the question belongs.
    /// </summary>
    /// <param name="sessionId">The candidate session id.</param>
    bool CanDrive(string? sessionId);

    /// <summary>
    /// Every session id present on this Director RIGHT NOW.
    ///
    /// A drain captures its roster once, and a Director can gain a session afterwards - one spawned by a
    /// session that had not yet been told to stop, or by anything else that can create one. A seat that is
    /// not in the capture is in no document, in no sweep and in no record, and the restart would destroy
    /// it silently. So the drain compares this against its seats before saying a restart may proceed.
    /// </summary>
    IReadOnlyList<string> LiveSessionIds();

    /// <summary>
    /// Deliver a message to a session as an ordinary prompt, submitted.
    ///
    /// It answers with WHY rather than with a bool, because the two failures mean opposite things to a
    /// drain: a session that has gone is one less thing to wait for, and a session that is alive and
    /// cannot take the words is one the drain will otherwise wait ninety minutes to call silent when it
    /// was never spoken to.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="text">The message.</param>
    Task<DrainDelivery> SendAsync(string sessionId, string text);

    /// <summary>Rename a session, which is how a drained seat is marked on every screen while it waits to
    /// be reaped.</summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="name">The new name.</param>
    bool Rename(string sessionId, string name);

    /// <summary>
    /// Flag a session for deletion. This is a REQUEST, not a kill: it records the fact, and the Director's
    /// own deletion reaper removes the session later, after a grace window and ONLY while the session is
    /// not mid-turn. A session that keeps working is left alone by the reaper on every sweep, for as long
    /// as it keeps working.
    ///
    /// So the session does end up killed - by the reaper, once it has stopped. What never happens is a
    /// session being cut off DURING a turn, which is what "never force" means and is the whole reason a
    /// blocked seat is never flagged at all.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="reason">Why, recorded on the session.</param>
    bool MarkForDeletion(string sessionId, string reason);

    /// <summary>
    /// Whether the session is in the middle of a turn RIGHT NOW. SMART SHUTDOWN ONLY: the older drain
    /// never asks, because it never interrupts.
    ///
    /// It is the same question the Director's own deletion reaper asks before it removes a flagged
    /// session, answered from the same fact, so "mid-turn" means one thing on this Director. False for a
    /// session that is not here: a session that has gone is not working.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    bool IsMidTurn(string sessionId);

    /// <summary>
    /// Interrupt the session's turn, through the Director's existing interrupt path. SMART SHUTDOWN ONLY:
    /// the older drain never calls this.
    ///
    /// It answers like <see cref="SendAsync"/>, so "gone" and "could not" stay two facts. "Could not" is a
    /// real answer here and not an error to work around: an agent whose command line has no safe hard
    /// interrupt refuses, and the reason it gives comes back verbatim.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    Task<DrainDelivery> InterruptAsync(string sessionId);

    /// <summary>
    /// End the session NOW, turn or no turn, through the Director's existing way of stopping a session.
    /// SMART SHUTDOWN ONLY: the older drain never calls this, and <see cref="MarkForDeletion"/> remains
    /// the strongest thing it does.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="reason">Why, for the log.</param>
    Task<DrainEnd> EndAsync(string sessionId, string reason);

    /// <summary>
    /// Take back a <see cref="MarkForDeletion"/> that the reaper has not acted on yet, through the
    /// session's existing way of cancelling a pending deletion. SMART SHUTDOWN ONLY, and only for "Cancel
    /// and keep working": a session that handed over and was flagged is still open until the reaper
    /// removes it, and a cancel that left the flag standing would watch it be closed a minute after the
    /// owner was told the restart is off. The older drain never calls this.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <returns>True when the session is here and is no longer flagged. False when it is not here any
    /// more: the reaper got to it first, and it is then one of the sessions to bring back.</returns>
    bool CancelDeletion(string sessionId);
}

/// <summary>
/// The real seam, over this Director's own <see cref="SessionManager"/>.
///
/// IT HOLDS TWO KINDS OF VERB, FOR TWO CALLERS.
///
/// The older drain (<see cref="DirectorDrain"/>) never forces. The strongest thing it does is
/// <see cref="MarkForDeletion"/>, which asks; the Director's own reaper then removes the session after a
/// grace window and only while it is not mid-turn, so a session that keeps working is left alone
/// indefinitely. A seat that cannot reach a clean stop is never flagged at all, keeps running, and the
/// restart does not happen. A flagged session IS killed in the end, by the reaper, once it stops working:
/// the distinction that matters is during-the-turn against after-the-turn.
///
/// The smart shutdown does force, because the owner gave it a time limit (mission document "Smart
/// Director Restart", section 5.3 item 5, which replaces the never-force rule FOR THE SMART SHUTDOWN
/// ONLY). For it this seam carries <see cref="InterruptAsync"/>, which cuts a turn short, and
/// <see cref="EndAsync"/>, which ends a session turn or no turn. Both go through paths the Director
/// already had - the interrupt verb and the stop verb of <see cref="SessionCommandExecutor"/> - so there
/// is still exactly one way to interrupt a session and one way to stop one.
///
/// This comment used to say the seam held no way to kill a session directly or to cancel its turn, and
/// that never-force was enforced by the drain having no stronger verb. That stopped being true when the
/// two verbs were added. What is true now: the older drain never CALLS them, and that is held by a test
/// (DirectorDrainTests, Drain_OnTheOlderPath_NeverInterruptsAndNeverEndsASession), which runs the older
/// drain on the rig and asserts neither verb was called - not by this sentence.
/// </summary>
public sealed class SessionManagerDrainControl : IDrainSessionControl
{
    private readonly SessionManager _sessions;

    /// <summary>Create the seam over a Director's session manager.</summary>
    /// <param name="sessions">The Director's live sessions.</param>
    public SessionManagerDrainControl(SessionManager sessions)
        => _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));

    /// <inheritdoc />
    public bool IsPresent(string sessionId)
        => Guid.TryParse(sessionId, out var id) && _sessions.GetSession(id) is not null;

    /// <inheritdoc />
    public bool CanDrive(string? sessionId)
        => !string.IsNullOrWhiteSpace(sessionId) && Guid.TryParse(sessionId, out _);

    /// <inheritdoc />
    public IReadOnlyList<string> LiveSessionIds()
        => _sessions.ListSessions().Select(s => s.Id.ToString()).ToList();

    /// <inheritdoc />
    public async Task<DrainDelivery> SendAsync(string sessionId, string text)
    {
        if (!Guid.TryParse(sessionId, out var id)) return DrainDelivery.Gone;
        var session = _sessions.GetSession(id);
        if (session is null) return DrainDelivery.Gone;

        try
        {
            var result = await SessionCommandExecutor.SendPromptAsync(
                session,
                new Gateway.Contracts.PromptRequest { Text = text, AppendEnter = true },
                SendSource.Framework).ConfigureAwait(false);

            if (result.Status == Gateway.Contracts.DirectorCommandStatus.Ok) return DrainDelivery.Ok;

            FileLog.Write($"[DrainSessionControl] SendAsync: session={sessionId} refused: {result.Error}");
            return DrainDelivery.Refused(result.Error ?? "the Director refused the prompt");
        }
        catch (ComposerNotAcceptingInputException ex)
        {
            // A WEDGED SEAT. The submit protocol types the text, watches for the composer to echo it, and
            // THROWS when it never does - which is the honest answer and the reason this method returns a
            // reason rather than a bool. Letting it propagate would have taken the whole drain down at the
            // first wedged session, unhandled, leaving a capture on the Gateway reading "draining" for
            // ever. That is what the seam's own contract promised would not happen, and it is what would
            // have happened.
            FileLog.Write($"[DrainSessionControl] SendAsync: session={sessionId} wedged: {ex.Message}");
            return DrainDelivery.Refused(ex.Message);
        }
    }

    /// <inheritdoc />
    public bool Rename(string sessionId, string name)
    {
        if (!Guid.TryParse(sessionId, out var id)) return false;
        var session = _sessions.GetSession(id);
        if (session is null) return false;
        session.CustomName = name;
        session.IsAutoNamed = false;
        FileLog.Write($"[DrainSessionControl] Rename: session={sessionId} -> {name}");
        return true;
    }

    /// <inheritdoc />
    public bool MarkForDeletion(string sessionId, string reason)
    {
        if (!Guid.TryParse(sessionId, out var id)) return false;
        var session = _sessions.GetSession(id);
        if (session is null) return false;
        session.MarkForDeletion(reason);
        return true;
    }

    /// <inheritdoc />
    public bool CancelDeletion(string sessionId)
    {
        if (!Guid.TryParse(sessionId, out var id) || _sessions.GetSession(id) is not { } session)
        {
            FileLog.Write($"[DrainSessionControl] CancelDeletion: session={sessionId} is not here");
            return false;
        }
        session.CancelDeletion();
        FileLog.Write($"[DrainSessionControl] CancelDeletion: session={sessionId} is no longer flagged");
        return true;
    }

    /// <inheritdoc />
    public bool IsMidTurn(string sessionId)
    {
        // ActivityState.Working, and only that, is what the deletion reaper treats as a turn it must not
        // cut off (SessionManager.ReapPendingDeletions). The same fact is used here so the two agree.
        var midTurn = Guid.TryParse(sessionId, out var id)
                      && _sessions.GetSession(id) is { ActivityState: ActivityState.Working };
        FileLog.Write($"[DrainSessionControl] IsMidTurn: session={sessionId}, midTurn={midTurn}");
        return midTurn;
    }

    /// <inheritdoc />
    public async Task<DrainDelivery> InterruptAsync(string sessionId)
    {
        FileLog.Write($"[DrainSessionControl] InterruptAsync: session={sessionId}");

        var result = await SessionCommandExecutor.InterruptAsync(
            _sessions,
            new Gateway.Contracts.DirectorCommand
            {
                CommandId = "smart-shutdown-interrupt",
                Verb = "interrupt",
                SessionId = sessionId,
            }).ConfigureAwait(false);

        switch (result.Status)
        {
            case Gateway.Contracts.DirectorCommandStatus.Ok:
                FileLog.Write($"[DrainSessionControl] InterruptAsync: session={sessionId} interrupted");
                return DrainDelivery.Ok;

            // An id that cannot be parsed and an id that names nothing are the same fact to a caller of
            // this seam, exactly as they are in SendAsync: there is no such session here.
            case Gateway.Contracts.DirectorCommandStatus.BadRequest:
            case Gateway.Contracts.DirectorCommandStatus.NotFound:
                FileLog.Write($"[DrainSessionControl] InterruptAsync: session={sessionId} is not here: {result.Error}");
                return DrainDelivery.Gone;

            default:
                FileLog.Write($"[DrainSessionControl] InterruptAsync: session={sessionId} refused: {result.Error}");
                return DrainDelivery.Refused(result.Error ?? "the Director refused the interrupt");
        }
    }

    /// <inheritdoc />
    public async Task<DrainEnd> EndAsync(string sessionId, string reason)
    {
        FileLog.Write($"[DrainSessionControl] EndAsync: session={sessionId}, reason={reason}");

        // Asked BEFORE the stop, because the stop verb answers "already stopped" as a success for a
        // session with no row (it has to: a stop must never fail because there is nothing left to stop),
        // and this seam promises to keep "I ended it" and "it was not here" apart.
        if (!Guid.TryParse(sessionId, out var id) || _sessions.GetSession(id) is null)
        {
            FileLog.Write($"[DrainSessionControl] EndAsync: session={sessionId} is not here");
            return DrainEnd.Gone;
        }

        var result = await SessionCommandExecutor.KillAsync(
            _sessions,
            new Gateway.Contracts.DirectorCommand
            {
                CommandId = "smart-shutdown-end",
                Verb = "kill",
                SessionId = sessionId,
            }).ConfigureAwait(false);

        if (result.Status != Gateway.Contracts.DirectorCommandStatus.Ok)
        {
            FileLog.Write($"[DrainSessionControl] EndAsync: session={sessionId} FAILED: {result.Error}");
            return DrainEnd.Refused(result.Error ?? "the Director could not stop the session");
        }

        // The stop verb succeeds while KEEPING the row in one case: the session ran in a pooled worktree
        // that would not be taken back. That session is still on this Director, so it was not ended, and
        // the reason the stop gave is the reason handed back.
        if (_sessions.GetSession(id) is { } kept)
        {
            var why = kept.PooledWorktreeHeldReason is { Length: > 0 } held
                ? $"its pooled worktree is held: {held}"
                : "the stop succeeded and the session is still on this Director";
            FileLog.Write($"[DrainSessionControl] EndAsync: session={sessionId} still present: {why}");
            return DrainEnd.Refused(why);
        }

        FileLog.Write($"[DrainSessionControl] EndAsync: session={sessionId} ended");
        return DrainEnd.Ok;
    }
}
