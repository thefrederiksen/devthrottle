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

    /// <summary>Type and submit the doorbell line as an agent-origin send. Throws when the submit failed.</summary>
    Task SendLineAsync(string line);
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

    public Task SendLineAsync(string line) =>
        _session.SendTextAsync(
            line,
            SubmissionProvenance.Typed(SubmissionRoutes.FleetMessage, SubmissionIdentityKinds.Framework),
            SendSource.Agent);
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
/// THE LINE IS AGENT-ORIGIN (ruling 15). It is submitted with <see cref="SendSource.Agent"/>, so it does not
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

    /// <summary>Gather the facts about one session, with two frames taken <paramref name="pause"/> apart.</summary>
    public static async Task<DoorbellFacts> GatherFactsAsync(IDoorbellTarget target, Func<Task> pause)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(pause);
        var exited = target.Exited;
        var working = target.DirectorSaysWorking;
        var hasGrid = target.HasTerminalGrid;
        var first = target.TakeFrame();
        await pause().ConfigureAwait(false);
        var second = target.TakeFrame();
        return new DoorbellFacts(
            target.Agent,
            exited,
            working,
            hasGrid && first.Rows.Count > 0,
            target.ProductMayHaveLeftText,
            [first, second]);
    }

    /// <summary>Ring one live session. See <see cref="RingAsync(IDoorbellTarget, int, Func{Task}?)"/>.</summary>
    public static Task<FleetRingResponse> RingAsync(Session session, int unreadCount) =>
        RingAsync(new SessionDoorbellTarget(session), unreadCount);

    /// <summary>
    /// Ring one session: check, look once more, and type the doorbell line when it is safe. Never throws for a
    /// deferral; a failed submit propagates, because a line that may be half-typed is not a deferral.
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

            var changed = ChangedSinceCheck(target, facts.Frames[^1]);
            if (changed is { } c)
                return Deferred(target, c.Reason, c.Detail);

            var line = FleetDoorbellLine.For(unreadCount);
            try
            {
                await target.SendLineAsync(line).ConfigureAwait(false);
            }
            catch (PromptNotSubmittedException ex) when (ComposerIsEmptyNow(target))
            {
                // THE SUBMIT CHECK CANNOT SEE A SHORT TURN. It calls a submit proven only when the agent prints
                // 2,048 bytes, and an agent that answers the doorbell with one word ("IGNORED") prints less. The
                // end-to-end proof caught it: the check threw, the Gateway was told the ring failed, and rang
                // again - two doorbell lines for one ring. The composer is the better witness for this one
                // line: it is empty now, so the line left it. Counted as rung.
                FileLog.Write($"[FleetDoorbellRinger] RUNG (the byte-count check did not see a turn, the composer is empty): " +
                              $"session={target.Id}: {ex.Message}");
            }
            FileLog.Write($"[FleetDoorbellRinger] RUNG: session={target.Id}, unread={unreadCount}, line=\"{line}\"");
            return new FleetRingResponse { Outcome = FleetRingOutcomes.Rung, Detail = verdict.Detail };
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

    /// <summary>Is the composer empty right now? The line is either submitted (empty) or still parked (text).
    /// A running turn draws an empty composer too, which is the same answer: the line left.</summary>
    private static bool ComposerIsEmptyNow(IDoorbellTarget target) =>
        DoorbellSafety.ReadComposer(target.Agent, target.TakeFrame()) == ComposerReading.Empty;

    private static FleetRingResponse Deferred(IDoorbellTarget target, string reason, string detail)
    {
        FileLog.Write($"[FleetDoorbellRinger] DEFERRED ({reason}): session={target.Id}: {detail}");
        return new FleetRingResponse { Outcome = FleetRingOutcomes.Deferred, Reason = reason, Detail = detail };
    }
}
