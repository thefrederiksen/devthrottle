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
public interface IDrainSessionControl
{
    /// <summary>True when a session with this id is still present on this Director. FALSE IS THE
    /// CLOSE CONDITION: marking a session done is a flag and the reap is asynchronous, so a close is only
    /// finished when the session is genuinely absent.</summary>
    /// <param name="sessionId">The session id.</param>
    bool IsPresent(string sessionId);

    /// <summary>Deliver a message to a session as an ordinary prompt, submitted. Returns false when the
    /// session is not there or has exited - never throws for an ordinary absence.</summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="text">The message.</param>
    Task<bool> SendAsync(string sessionId, string text);

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
    public async Task<bool> SendAsync(string sessionId, string text)
    {
        if (!Guid.TryParse(sessionId, out var id)) return false;
        var session = _sessions.GetSession(id);
        if (session is null) return false;

        var result = await SessionCommandExecutor.SendPromptAsync(
            session,
            new Gateway.Contracts.PromptRequest { Text = text, AppendEnter = true },
            SendSource.Framework).ConfigureAwait(false);

        if (result.Status != Gateway.Contracts.DirectorCommandStatus.Ok)
            FileLog.Write($"[DrainSessionControl] SendAsync: session={sessionId} refused: {result.Error}");
        return result.Status == Gateway.Contracts.DirectorCommandStatus.Ok;
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
