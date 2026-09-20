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
/// forces: it never calls <see cref="IDrainSessionControl.StopTurnAsync"/> or
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

/// <summary>
/// The verb this Director can use to stop a session's turn, CHOSEN FROM WHAT THE DRIVER DECLARES.
///
/// It is read from <see cref="DriverCapabilities"/> BEFORE anything is sent, never discovered by sending
/// one verb and catching the refusal. The drivers genuinely disagree: Claude Code and Codex declare both
/// verbs, pi declares only the soft cancel (its Ctrl+C clears the editor and twice QUITS it), Cursor and
/// Copilot declare only the hard interrupt. Sending the same verb to all of them is what left a pi session
/// never told to hand over, working to the limit, and ended with no document (issue #3207).
/// </summary>
public enum DrainStopVerb
{
    /// <summary>The agent declares neither verb, so this Director has no way to stop its turn. Nothing is
    /// sent. It is a fact for the record and for the progress screen, named there, never a silent skip.</summary>
    None = 0,

    /// <summary>The hard interrupt (Ctrl+C for every terminal command line verified so far), declared as
    /// <see cref="DriverCapabilities.Interrupt"/>.</summary>
    Interrupt = 1,

    /// <summary>The soft cancel (Escape), declared as <see cref="DriverCapabilities.Cancel"/>.</summary>
    Escape = 2,
}

/// <summary>
/// What happened when the smart shutdown tried to stop a session's turn: WHICH VERB WAS CHOSEN, and what
/// came of sending it.
///
/// Two facts and not one, for the same reason <see cref="DrainDelivery"/> is not a bool: the row and the
/// record have to say which verb this Director had for that agent, and
/// <see cref="DrainStopVerb.None"/> - the agent declares neither - means nothing was sent at all, which is
/// a different sentence from "it was sent and refused".
/// </summary>
/// <param name="Verb">The verb this session's driver declares, or <see cref="DrainStopVerb.None"/>.</param>
/// <param name="Delivery">What came of sending it; the refusal carries the reason when nothing was sent.</param>
public sealed record DrainTurnStop(DrainStopVerb Verb, DrainDelivery Delivery)
{
    /// <summary>There is no such session here, so there was no turn to stop and no driver to ask.</summary>
    public static readonly DrainTurnStop Gone = new(DrainStopVerb.None, DrainDelivery.Gone);

    /// <summary>The agent declares neither verb. Nothing was sent, and here is why.</summary>
    /// <param name="reason">Which agent it is and what it declares, in plain words.</param>
    public static DrainTurnStop NotDeclared(string reason)
        => new(DrainStopVerb.None, DrainDelivery.Refused(reason));
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
    /// Stop the session's turn, with the verb ITS OWN DRIVER DECLARES: the hard interrupt where the driver
    /// declares <see cref="DriverCapabilities.Interrupt"/>, the soft cancel (Escape) where it declares only
    /// <see cref="DriverCapabilities.Cancel"/>, and nothing at all where it declares neither. Both go
    /// through paths the Director already had. SMART SHUTDOWN ONLY: the older drain never calls this.
    ///
    /// THE VERB IS CHOSEN BEFORE IT IS SENT, never found by sending one and catching what comes back. A
    /// driver with no safe hard interrupt THROWS from its interrupt method, so "try the interrupt and fall
    /// back to Escape" would be an exception used as a decision.
    ///
    /// It answers like <see cref="SendAsync"/>, so "gone" and "could not" stay two facts, and it names the
    /// verb so the row and the record can say which one this Director had.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    Task<DrainTurnStop> StopTurnAsync(string sessionId);

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
/// ONLY). For it this seam carries <see cref="StopTurnAsync"/>, which cuts a turn short with whichever
/// verb that session's driver declares, and <see cref="EndAsync"/>, which ends a session turn or no turn.
/// Both go through paths the Director already had - the interrupt verb, the escape verb and the stop verb
/// of <see cref="SessionCommandExecutor"/> - so there is still exactly one way to interrupt a session, one
/// way to escape one, and one way to stop one.
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
        catch (PromptNotSubmittedException ex)
        {
            // THE SAME FACT, REPORTED A FEW LINES FURTHER ALONG THE SAME SUBMIT PATH. This one means the
            // text reached the composer and nothing ever ran: the Enter was swallowed, or the session is
            // a shell that prints too little for the submit verifier to see a turn start. To a drain that
            // is the same answer as a wedged composer - THIS SESSION CANNOT BE ASKED - and it belongs on
            // that session's row, not at the top of the run.
            //
            // Only its sibling was caught until issue #3235, and the gap was not theoretical: one raw
            // command line session took the words, never started a turn, and the exception came out of
            // the whole smart shutdown. The run stopped at the sixth of seven sessions, the seventh was
            // never asked, every other session was left running, and nothing was handed over.
            FileLog.Write($"[DrainSessionControl] SendAsync: session={sessionId} never started a turn: {ex.Message}");
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
    public async Task<DrainTurnStop> StopTurnAsync(string sessionId)
    {
        if (!Guid.TryParse(sessionId, out var id) || _sessions.GetSession(id) is not { } session)
        {
            FileLog.Write($"[DrainSessionControl] StopTurnAsync: session={sessionId} is not here");
            return DrainTurnStop.Gone;
        }

        // THE VERB IS READ FROM THE DRIVER BEFORE ANYTHING IS SENT. The hard interrupt first where the
        // driver declares it - it is what this Director has always sent, and it stops a turn outright -
        // and the soft cancel where it declares only that. Never "send the interrupt and see what comes
        // back": pi's driver THROWS from its interrupt method by design, so a catch around that throw
        // would be an exception used as a decision, and it is how a pi session was never told to hand
        // over at all (issue #3207).
        var capabilities = session.Driver.Capabilities;
        FileLog.Write(
            $"[DrainSessionControl] StopTurnAsync: session={sessionId}, agent={session.Driver.Kind}, " +
            $"declares={capabilities}");

        if (capabilities.HasFlag(DriverCapabilities.Interrupt))
        {
            var interrupted = await SessionCommandExecutor.InterruptAsync(
                _sessions,
                new Gateway.Contracts.DirectorCommand
                {
                    CommandId = "smart-shutdown-interrupt",
                    Verb = "interrupt",
                    SessionId = sessionId,
                }).ConfigureAwait(false);
            return new DrainTurnStop(
                DrainStopVerb.Interrupt, ReadStopAnswer(sessionId, "interrupt", interrupted));
        }

        if (capabilities.HasFlag(DriverCapabilities.Cancel))
        {
            var escaped = await SessionCommandExecutor.EscapeAsync(
                _sessions,
                new Gateway.Contracts.DirectorCommand
                {
                    CommandId = "smart-shutdown-escape",
                    Verb = "escape",
                    SessionId = sessionId,
                }).ConfigureAwait(false);
            return new DrainTurnStop(DrainStopVerb.Escape, ReadStopAnswer(sessionId, "escape", escaped));
        }

        // NEITHER VERB. Nothing is sent, and the agent is NAMED: a caller handed a bare "could not" has
        // nothing to put on the row or in the record, and a skip with no sentence is the defect itself.
        var reason =
            $"its agent ({session.Driver.Kind}) declares neither a hard interrupt nor a soft cancel, so " +
            "this Director has no verb that stops its turn";
        FileLog.Write($"[DrainSessionControl] StopTurnAsync: session={sessionId} has no stop verb: {reason}");
        return DrainTurnStop.NotDeclared(reason);
    }

    /// <summary>One reading of what a stop verb answered, shared by both verbs so the two cannot drift
    /// apart.</summary>
    /// <param name="sessionId">The session id, for the log.</param>
    /// <param name="verb">Which verb was sent, for the log.</param>
    /// <param name="result">What the Director's own verb handler answered.</param>
    private static DrainDelivery ReadStopAnswer(
        string sessionId, string verb, Gateway.Contracts.DirectorCommandResult result)
    {
        switch (result.Status)
        {
            case Gateway.Contracts.DirectorCommandStatus.Ok:
                FileLog.Write($"[DrainSessionControl] StopTurnAsync: session={sessionId} stopped with {verb}");
                return DrainDelivery.Ok;

            // An id that cannot be parsed and an id that names nothing are the same fact to a caller of
            // this seam, exactly as they are in SendAsync: there is no such session here.
            case Gateway.Contracts.DirectorCommandStatus.BadRequest:
            case Gateway.Contracts.DirectorCommandStatus.NotFound:
                FileLog.Write($"[DrainSessionControl] StopTurnAsync: session={sessionId} is not here: {result.Error}");
                return DrainDelivery.Gone;

            default:
                FileLog.Write($"[DrainSessionControl] StopTurnAsync: session={sessionId} refused the {verb}: {result.Error}");
                return DrainDelivery.Refused(result.Error ?? $"the Director refused the {verb}");
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
