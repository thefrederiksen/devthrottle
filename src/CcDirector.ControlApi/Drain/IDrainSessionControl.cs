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
}

/// <summary>
/// The real seam, over this Director's own <see cref="SessionManager"/>.
///
/// Note what is NOT here: any way to kill a session DIRECTLY, or to cancel its turn. The strongest thing
/// the drain can do is <see cref="MarkForDeletion"/>, which asks; the Director's own reaper then removes
/// the session after a grace window and only while it is not mid-turn, so a session that keeps working is
/// left alone indefinitely. That is what "never force" means here, and it is enforced by the drain having
/// no stronger verb rather than by a rule saying it must not use one. A seat that cannot reach a clean
/// stop is never flagged at all, keeps running, and the restart does not happen.
///
/// This paragraph used to say the seam held "no way to kill a session". That was wrong: a flagged session
/// IS killed, by the reaper, once it stops working. The distinction that matters is out-of-turn versus
/// after-the-turn, and the earlier wording collapsed the two.
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
}
