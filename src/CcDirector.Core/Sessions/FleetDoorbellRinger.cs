using System.Collections.Concurrent;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Sessions;

/// <summary>
/// What the ringer needs from a session: its facts, a fresh screen frame on demand, and a way to type the line.
/// <see cref="Session"/> is the product's one implementation (<see cref="SessionDoorbellTarget"/>); tests supply
/// a scripted one so every step of a ring is provable without a terminal.
/// </summary>
public interface IDoorbellTarget
{
    /// <summary>The session's id, for the log and the one-ring-at-a-time rule.</summary>
    Guid Id { get; }

    /// <summary>Which agent runs in the terminal.</summary>
    AgentKind Agent { get; }

    /// <summary>The session has exited or failed.</summary>
    bool Exited { get; }

    /// <summary>The Director's own activity state is Working or Starting, read NOW.</summary>
    bool DirectorSaysWorking { get; }

    /// <summary>The session has a terminal the Director renders.</summary>
    bool HasTerminalGrid { get; }

    /// <summary>An earlier product send left a retained-text mark (see <see cref="DoorbellFacts.ProductMayHaveLeftText"/>).</summary>
    bool ProductMayHaveLeftText { get; }

    /// <summary>One frame of the live screen, taken now.</summary>
    ScreenFrame TakeFrame();

    /// <summary>
    /// Type the doorbell line and press Enter once, as an agent-origin send, through
    /// <see cref="TerminalSubmit.DoorbellSubmitAsync"/>. Throws only when the terminal could not be written.
    /// </summary>
    Task<DoorbellSubmitOutcome> SubmitLineAsync(string line, Func<bool> mayTypeNow, Func<bool> composerShowsLine, Func<bool> turnStarted);
}

/// <summary>The product's <see cref="IDoorbellTarget"/>: a live <see cref="Session"/>.</summary>
public sealed class SessionDoorbellTarget : IDoorbellTarget
{
    private readonly Session _session;

    public SessionDoorbellTarget(Session session) =>
        _session = session ?? throw new ArgumentNullException(nameof(session));

    public Guid Id => _session.Id;
    public AgentKind Agent => _session.AgentKind;
    public bool Exited => _session.Status is SessionStatus.Exited or SessionStatus.Failed
                          || _session.ActivityState == ActivityState.Exited;
    public bool DirectorSaysWorking => _session.ActivityState is ActivityState.Working or ActivityState.Starting;
    public bool HasTerminalGrid => _session.BackendType is SessionBackendType.ConPty;
    public bool ProductMayHaveLeftText => _session.ProductMayHaveLeftComposerText;

    public ScreenFrame TakeFrame()
    {
        var (rows, cursorRow, cursorCol, cursorVisible, _) = _session.SnapshotLiveScreen();
        return new ScreenFrame(rows, cursorRow, cursorCol, cursorVisible);
    }

    public Task<DoorbellSubmitOutcome> SubmitLineAsync(string line, Func<bool> mayTypeNow, Func<bool> composerShowsLine, Func<bool> turnStarted) =>
        _session.SubmitDoorbellLineAsync(line, mayTypeNow, composerShowsLine, turnStarted);
}

/// <summary>
/// THE DOORBELL'S DIRECTOR HALF (the Message Load mission, slice 2). The Gateway sends <c>ring</c>; this looks at
/// the session, runs <see cref="DoorbellSafety.Check"/> on two fresh screen frames, and either types the one
/// doorbell line or says why not.
///
/// THE LAST LOOK IS TAKEN IMMEDIATELY BEFORE THE FIRST BYTE (inspection 4, ruling 1). The two frames the check
/// reads are <see cref="BetweenFrames"/> apart, and a turn can start after the second. So once the check has
/// allowed the ring, a THIRD frame and the Director's activity state are read again, and if the session is now
/// working, has exited, or its screen is no longer the one the check approved, nothing is typed and the answer
/// is <c>deferred, working</c>.
///
/// KNOWN GAP, ACCEPTED (ruling 1): what remains is the interval between that last look and the first byte -
/// no waiting happens in it, only the call into the submit - plus a turn the agent starts on its own inside
/// it (a background task completing). A terminal offers no way to type "only if still idle". The worst case is
/// one short fixed line queued behind the current tool call; it carries no message text, so nothing is lost.
///
/// ONE ENTER, AND <c>rung</c> ONLY WHEN THE SUBMIT WAS SEEN (inspection 4, ruling 2). The line goes through
/// <see cref="TerminalSubmit.DoorbellSubmitAsync"/>, which presses Enter once and never nudges. The submit is
/// verified by the screen (<see cref="DoorbellSafety.ShowsDoorbellSubmitted"/>): the line is out of the composer
/// and either the working marker is up or the transcript shows one more doorbell row than before. Anything less
/// is a deferral, never a ring - see <c>UnverifiedAnswer</c>.
///
/// THE DOORBELL NEVER ERASES (inspection 8, ruling 1). A line whose submit was not verified is left where it is.
/// No screen frame can prove that the composer holds ONLY the product's line - a whitespace character the owner
/// adds can leave the frame unchanged - so anything the product deleted could take an owner character with it.
/// The parked line blocks the next ring (<c>composer-holds-text</c>) until the owner clears or sends it.
///
/// THE LINE IS AGENT-ORIGIN (ruling 15). It is recorded with <see cref="SendSource.Agent"/>, so it does not
/// stamp an owner turn and does not supersede the owner's snooze on this Director.
///
/// ONE RING AT A TIME PER SESSION. A second ring for a session already being rung is deferred rather than
/// queued: two doorbell lines typed back to back would weld into one prompt.
/// </summary>
public static class FleetDoorbellRinger
{
    /// <summary>How long between the two screen frames the check compares.</summary>
    public static readonly TimeSpan BetweenFrames = TimeSpan.FromMilliseconds(120);

    private static readonly ConcurrentDictionary<Guid, byte> Ringing = new();

    /// <summary>
    /// Gather the facts about one session, with two frames taken <paramref name="pause"/> apart.
    ///
    /// AN UNREADABLE SCREEN GETS ONE FRAME. When the session has no terminal grid, or the first frame has no rows,
    /// the facts say so and the check defers <c>screen-unreadable</c> (or <c>exited</c>, which it tests first)
    /// before it ever looks at the frames - so the pause and the second frame could not change the answer. They
    /// are skipped. That is most rings: on one Director 10,980 of 19,805 a day, each frame a snapshot taken
    /// under the session's screen lock. Every readable screen still gets both frames, exactly as before.
    /// </summary>
    public static async Task<DoorbellFacts> GatherFactsAsync(IDoorbellTarget target, Func<Task> pause)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(pause);
        var exited = target.Exited;
        var working = target.DirectorSaysWorking;
        var hasGrid = target.HasTerminalGrid;
        var first = target.TakeFrame();
        var readable = hasGrid && first.Rows.Count > 0;
        if (!readable)
        {
            return new DoorbellFacts(
                target.Agent,
                exited,
                working,
                readable,
                target.ProductMayHaveLeftText,
                [first]);
        }
        await pause().ConfigureAwait(false);
        var second = target.TakeFrame();
        return new DoorbellFacts(
            target.Agent,
            exited,
            working,
            readable,
            target.ProductMayHaveLeftText,
            [first, second]);
    }

    /// <summary>Ring one live session. See <see cref="RingAsync(IDoorbellTarget, int, Func{Task}?)"/>.</summary>
    public static Task<FleetRingResponse> RingAsync(Session session, int unreadCount) =>
        RingAsync(new SessionDoorbellTarget(session), unreadCount);

    /// <summary>
    /// Ring one session: check, look once more, and type the doorbell line when it is safe. Never throws for a
    /// deferral; a terminal that could not be written propagates, because a line that may be half-typed is not a
    /// deferral.
    /// </summary>
    /// <param name="pause">What separates the two checked frames; <see cref="BetweenFrames"/> of real time when null.</param>
    public static async Task<FleetRingResponse> RingAsync(IDoorbellTarget target, int unreadCount, Func<Task>? pause = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        FileLog.Write($"[FleetDoorbellRinger] RingAsync: session={target.Id}, unread={unreadCount}");
        if (!Ringing.TryAdd(target.Id, 0))
            return Deferred(target, FleetRingDeferReasons.Working, "a doorbell is already being typed into this session");
        try
        {
            var facts = await GatherFactsAsync(target, pause ?? (() => Task.Delay(BetweenFrames))).ConfigureAwait(false);
            var verdict = DoorbellSafety.Check(facts);
            if (!verdict.Ring)
                return Deferred(target, verdict.Reason, verdict.Detail);

            var approved = facts.Frames[^1];
            var rowsBefore = DoorbellSafety.CountDoorbellRows(approved);
            var line = FleetDoorbellLine.For(unreadCount);
            (string Reason, string Detail)? changed = null;
            var outcome = await target.SubmitLineAsync(
                line,
                mayTypeNow: () => (changed = ChangedSinceCheck(target, approved)) is null,
                composerShowsLine: () => DoorbellSafety.ComposerHoldsExactly(target.Agent, target.TakeFrame(), line),
                turnStarted: () => DoorbellSafety.ShowsDoorbellSubmitted(target.Agent, target.TakeFrame(), rowsBefore))
                .ConfigureAwait(false);

            switch (outcome)
            {
                case DoorbellSubmitOutcome.NotTyped:
                    var (reason, detail) = changed ?? (FleetRingDeferReasons.ScreenUnreadable, "the session could not be typed into");
                    return Deferred(target, reason, detail);
                case DoorbellSubmitOutcome.Verified:
                    FileLog.Write($"[FleetDoorbellRinger] RUNG: session={target.Id}, unread={unreadCount}, line=\"{line}\"");
                    return new FleetRingResponse { Outcome = FleetRingOutcomes.Rung, Detail = "submitted; the screen shows the turn" };
                default:
                    return UnverifiedAnswer(target);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetDoorbellRinger] RingAsync FAILED: session={target.Id}: {ex.Message}");
            throw;
        }
        finally
        {
            Ringing.TryRemove(target.Id, out _);
        }
    }

    /// <summary>
    /// The last look (ruling 1): read the Director's state and a third frame, and say what changed since the
    /// frame the check approved - or null when nothing did. An exit is reported as <c>exited</c>; every other
    /// change as <c>working</c>, because a screen that moved is a session that is doing something.
    /// </summary>
    internal static (string Reason, string Detail)? ChangedSinceCheck(IDoorbellTarget target, ScreenFrame approved)
    {
        if (target.Exited)
            return (FleetRingDeferReasons.Exited, "the session exited after the check");
        if (target.DirectorSaysWorking)
            return (FleetRingDeferReasons.Working, "the Director's activity state turned working after the check");
        var now = target.TakeFrame();
        if (!SameFrame(approved, now))
            return (FleetRingDeferReasons.Working, "the screen changed after the check");
        return null;
    }

    /// <summary>Two frames are the same when every row and the cursor match exactly.</summary>
    internal static bool SameFrame(ScreenFrame a, ScreenFrame b)
    {
        if (a.CursorRow != b.CursorRow || a.CursorCol != b.CursorCol || a.CursorVisible != b.CursorVisible) return false;
        var ra = a.Rows ?? [];
        var rb = b.Rows ?? [];
        if (ra.Count != rb.Count) return false;
        for (var i = 0; i < ra.Count; i++)
            if (!string.Equals(ra[i], rb[i], StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>
    /// THE SUBMIT WAS NOT VERIFIED (ruling 2). A deferral is never counted as a ring, so the answer is always
    /// <c>deferred</c>, and NOTHING is erased or pressed (inspection 8, ruling 1). The composer is read once, only to
    /// name the reason:
    ///  - Empty: the line left without a turn the screen could see - the interface discarded it, or the turn was
    ///    too quick to catch. <c>not-submitted</c>. The next ring may type a second line, which ruling 8 calls
    ///    harmless.
    ///  - Anything else - the doorbell line, the line with the owner's words, the owner's words alone, or a screen
    ///    that does not read: <c>parked</c>. Whatever is there stays; the Gateway leaves the message due, and the
    ///    next ring is deferred <c>composer-holds-text</c> until the owner clears the composer.
    /// </summary>
    private static FleetRingResponse UnverifiedAnswer(IDoorbellTarget target)
    {
        var frame = target.TakeFrame();
        return DoorbellSafety.ReadComposer(target.Agent, frame) == ComposerReading.Empty
            ? Deferred(target, FleetRingDeferReasons.NotSubmitted,
                "the line left the composer but the screen showed no turn")
            : Deferred(target, FleetRingDeferReasons.Parked,
                "the line was typed but its submit was not verified; it is left in the composer, nothing erased");
    }

    private static FleetRingResponse Deferred(IDoorbellTarget target, string reason, string detail)
    {
        FileLog.Write($"[FleetDoorbellRinger] DEFERRED ({reason}): session={target.Id}: {detail}");
        return new FleetRingResponse { Outcome = FleetRingOutcomes.Deferred, Reason = reason, Detail = detail };
    }
}
