using System.Collections.Concurrent;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Sessions;

/// <summary>
/// THE DOORBELL'S DIRECTOR HALF (the Message Load mission, slice 2). The Gateway sends <c>ring</c>; this looks at
/// the session, runs <see cref="DoorbellSafety.Check"/> on two fresh screen frames, and either types the one
/// doorbell line or says why not.
///
/// THE LINE IS AGENT-ORIGIN (ruling 15). It is submitted with <see cref="SendSource.Agent"/>, so it does not
/// stamp an owner turn and does not supersede the owner's snooze on this Director. It does make the session
/// work, and the Gateway's own law ends an ARMED snooze on any work (17 July 2026) - that is the Gateway's
/// ruling, not this class's, and it is written up in the mission handoff.
///
/// ONE RING AT A TIME PER SESSION. A second ring for a session already being rung is deferred rather than
/// queued: two doorbell lines typed back to back would weld into one prompt.
/// </summary>
public static class FleetDoorbellRinger
{
    /// <summary>How long between the two screen frames the check compares.</summary>
    public static readonly TimeSpan BetweenFrames = TimeSpan.FromMilliseconds(120);

    private static readonly ConcurrentDictionary<Guid, byte> Ringing = new();

    /// <summary>Gather the facts about one session, with two frames <paramref name="betweenFrames"/> apart.</summary>
    public static async Task<DoorbellFacts> GatherFactsAsync(Session session, TimeSpan? betweenFrames = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var exited = session.Status is SessionStatus.Exited or SessionStatus.Failed
                     || session.ActivityState == ActivityState.Exited;
        var working = session.ActivityState is ActivityState.Working or ActivityState.Starting;
        var hasGrid = session.BackendType is SessionBackendType.ConPty;
        var first = Frame(session);
        await Task.Delay(betweenFrames ?? BetweenFrames).ConfigureAwait(false);
        var second = Frame(session);
        return new DoorbellFacts(
            session.AgentKind,
            exited,
            working,
            hasGrid && first.Rows.Count > 0,
            session.ProductMayHaveLeftComposerText,
            [first, second]);
    }

    private static ScreenFrame Frame(Session session)
    {
        var (rows, cursorRow, cursorCol, cursorVisible, _) = session.SnapshotLiveScreen();
        return new ScreenFrame(rows, cursorRow, cursorCol, cursorVisible);
    }

    /// <summary>
    /// Ring one session: check, and type the doorbell line when it is safe. Never throws for a deferral; a failed
    /// submit propagates, because a line that may be half-typed is not a deferral.
    /// </summary>
    public static async Task<FleetRingResponse> RingAsync(Session session, int unreadCount, TimeSpan? betweenFrames = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        FileLog.Write($"[FleetDoorbellRinger] RingAsync: session={session.Id}, unread={unreadCount}");
        if (!Ringing.TryAdd(session.Id, 0))
            return Deferred(session, FleetRingDeferReasons.Working, "a doorbell is already being typed into this session");
        try
        {
            var facts = await GatherFactsAsync(session, betweenFrames).ConfigureAwait(false);
            var verdict = DoorbellSafety.Check(facts);
            if (!verdict.Ring)
                return Deferred(session, verdict.Reason, verdict.Detail);

            var line = FleetDoorbellLine.For(unreadCount);
            try
            {
                await session.SendTextAsync(
                    line,
                    SubmissionProvenance.Typed(SubmissionRoutes.FleetMessage, SubmissionIdentityKinds.Framework),
                    SendSource.Agent).ConfigureAwait(false);
            }
            catch (PromptNotSubmittedException ex) when (ComposerIsEmptyNow(session))
            {
                // THE SUBMIT CHECK CANNOT SEE A SHORT TURN. It calls a submit proven only when the agent prints
                // 2,048 bytes, and an agent that answers the doorbell with one word ("IGNORED") prints less. The
                // end-to-end proof caught it: the check threw, the Gateway was told the ring failed, and rang
                // again - two doorbell lines for one ring. The composer is the better witness for this one
                // line: it is empty now, so the line left it. Counted as rung.
                FileLog.Write($"[FleetDoorbellRinger] RUNG (the byte-count check did not see a turn, the composer is empty): " +
                              $"session={session.Id}: {ex.Message}");
            }
            FileLog.Write($"[FleetDoorbellRinger] RUNG: session={session.Id}, unread={unreadCount}, line=\"{line}\"");
            return new FleetRingResponse { Outcome = FleetRingOutcomes.Rung, Detail = verdict.Detail };
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetDoorbellRinger] RingAsync FAILED: session={session.Id}: {ex.Message}");
            throw;
        }
        finally
        {
            Ringing.TryRemove(session.Id, out _);
        }
    }

    /// <summary>Is the composer empty right now? The line is either submitted (empty) or still parked (text).
    /// A running turn draws an empty composer too, which is the same answer: the line left.</summary>
    private static bool ComposerIsEmptyNow(Session session) =>
        DoorbellSafety.ReadComposer(session.AgentKind, Frame(session)) == ComposerReading.Empty;

    private static FleetRingResponse Deferred(Session session, string reason, string detail)
    {
        FileLog.Write($"[FleetDoorbellRinger] DEFERRED ({reason}): session={session.Id}: {detail}");
        return new FleetRingResponse { Outcome = FleetRingOutcomes.Deferred, Reason = reason, Detail = detail };
    }
}
