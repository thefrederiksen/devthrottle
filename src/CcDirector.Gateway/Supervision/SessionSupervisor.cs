using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Supervision;

/// <summary>
/// Everything the supervisor needs from the Gateway around it, as one seam. Production wires
/// <see cref="GatewaySupervisorEnvironment"/>; the tests wire a fake with no clock, no tunnel and no model,
/// which is what makes a two-hour escalation ladder provable in milliseconds.
/// </summary>
public interface ISupervisorEnvironment
{
    /// <summary>This tenant's supervisor settings.</summary>
    SupervisorSettings Settings(TenantId tenant);

    /// <summary>The session's LIVE screen rows, or null when it cannot be read (Director not connected, read
    /// failed). Null is never treated as a fault - unreadable is not evidence.</summary>
    Task<IReadOnlyList<string>?> ReadScreenRowsAsync(TenantId tenant, string directorId, string sessionId, CancellationToken ct);

    /// <summary>The session's current activity state from the pushed roster snapshot ("Working",
    /// "WaitingForInput", ...), or null when the session is no longer there. A cheap in-memory read: the
    /// supervisor never dials a session to ask whether it is alive.</summary>
    string? ReadActivityState(TenantId tenant, string sessionId);

    /// <summary>True when a menu confidently owns the session's screen. Typing "continue" would answer it,
    /// so the supervisor refuses.</summary>
    Task<bool> IsMenuOnScreenAsync(TenantId tenant, string directorId, string sessionId, CancellationToken ct);

    /// <summary>Send the continuation prompt into the session. Returns whether the send landed.</summary>
    Task<bool> SendContinueAsync(TenantId tenant, string directorId, string sessionId, CancellationToken ct);

    /// <summary>Step 3: ask the cheap model one tight question about an unrecognized terminating error, and
    /// return its raw reply (or null when it could not be asked).</summary>
    Task<string?> AskModelVerdictAsync(TenantId tenant, IReadOnlyList<string> rows, CancellationToken ct);

    /// <summary>Wait. The engine's only clock, so a test can run the whole ladder instantly.</summary>
    Task DelayAsync(TimeSpan delay, CancellationToken ct);

    /// <summary>Append one record to the recovery log.</summary>
    void Record(SupervisorRecord record);

    /// <summary>Raise a hand: the escalated recovery-log record plus the owner email.</summary>
    Task EscalateAsync(SupervisorRecord record, CancellationToken ct);
}

/// <summary>
/// One line of the recovery log (issue #915). Nothing the supervisor does is invisible: every detection,
/// wait, re-send, recovery and escalation writes one of these.
///
/// <paramref name="Detail"/> is a control-flow note - the attempt number, the delay, the outcome, the matched
/// SIGNATURE - and never terminal content, so the log can be read without carrying a customer's screen out of
/// their partition.
/// </summary>
public sealed record SupervisorRecord(
    TenantId Tenant,
    string DirectorId,
    string SessionId,
    string EventType,
    string Cause,
    string Detail);

/// <summary>
/// The session supervisor, phase 1 (issue #915): when a managed session goes idle after a TRANSIENT TRANSPORT
/// fault, wait and resume it automatically instead of freezing until somebody notices.
///
/// WHY IT EXISTS. Overnight on 2026-07-21 a session printed "API Error: Unable to connect to API (ENOTFOUND)"
/// at 06:56 and sat dead until 09:32 - two hours thirty-six minutes lost to a name-resolution blip that
/// cleared itself in seconds. That is the unattended promise failing.
///
/// EVENT-DRIVEN, NOT A POLLER. The only thing that wakes it is a session crossing Working -> idle, which the
/// existing <see cref="TurnEndWatcher"/> already observes. A session that is Working is never evaluated and
/// never touched.
///
/// THE INVARIANT: NON-INTERRUPTIVE BY CONSTRUCTION. Four independent gates stand between an idle session and
/// a keystroke:
///   1. the Working -> idle event (a working session never reaches the engine at all);
///   2. a POSITIVE fault signal on the live screen, inside the last few lines of real content - a clean turn
///      end resolves at step 2 for free, and an old error further up the screen is not a terminating fault;
///   3. a re-read of the session's activity state immediately before every send - if it has gone Working in
///      the meantime, the episode ends as recovered and nothing is sent;
///   4. a menu check before every send - a menu owning the screen escalates instead of being answered.
/// The hand-rolled watcher this replaces violated the invariant by probing sessions to confirm liveness, and
/// interrupted a healthy mission a dozen times in one night. Here that is not discouraged, it is unreachable.
///
/// A CATCH-UP COUNTS TOO. The watcher also fires for a session FIRST SEEN already waiting - a turn that ended
/// before this Gateway started. Those are evaluated deliberately: a session parked on a name-resolution error
/// since 03:00 is exactly the case this feature exists for, and it must not be skipped merely because the
/// Gateway restarted after it died. The fault gate makes that safe - a catch-up with a clean screen is left
/// alone like any other finished turn.
///
/// WHAT IT COSTS. One live-screen read per idle transition, and nothing else on the common path: no timer, no
/// poll, no model call. A clean turn end is resolved by the pure classifier and stops there. The model is
/// reached only for a turn that ended on an error nothing in the table recognizes - a rare minority of a
/// minority.
///
/// AN EPISODE, NOT A TURN. The attempt count and the ceiling belong to the FAULT EPISODE, not to one idle
/// transition. In a real outage a "continue" does produce a brief Working flicker before failing again, so a
/// per-transition counter would reset forever and the ceiling would never fire - which is exactly the
/// infinite blind loop the issue forbids. An episode ends when the session finishes a turn with NO fault on
/// its screen, when it exits, or when the supervisor escalates.
/// </summary>
public sealed class SessionSupervisor : IDisposable
{
    private readonly ISupervisorEnvironment _env;

    // The live recovery loops, keyed by (tenant, session). Presence means "an episode is being worked", so a
    // second turn-end for the same session never stacks a second loop.
    private readonly ConcurrentDictionary<(TenantId Tenant, string SessionId), CancellationTokenSource> _running = new();

    // How many sends this episode has made, per session. Survives the Working flicker a failed continue
    // produces; cleared when the session finishes a turn cleanly, exits, or is escalated.
    private readonly ConcurrentDictionary<(TenantId Tenant, string SessionId), int> _episodeAttempts = new();

    private bool _disposed;

    public SessionSupervisor(ISupervisorEnvironment environment)
        => _env = environment ?? throw new ArgumentNullException(nameof(environment));

    /// <summary>The continuation text sent into a parked session. Deliberately the plainest possible verb -
    /// it resumes the interrupted turn without adding an instruction the agent did not ask for.</summary>
    public const string ContinueText = "continue";

    /// <summary>
    /// A session crossed into idle. Evaluates it and, if a recoverable fault ended the turn, runs the
    /// recovery ladder in the background. Fire-and-forget by design: the turn-end handler must not wait.
    /// </summary>
    public void OnTurnEnd(TurnEndSignal signal)
    {
        if (_disposed || signal is null) return;
        if (!signal.Tenant.IsValid) return;
        if (string.IsNullOrEmpty(signal.SessionId)) return;

        var key = (signal.Tenant, signal.SessionId);
        var cts = new CancellationTokenSource();
        if (!TryClaimEpisode(key, cts))
        {
            // A LIVE episode is already being worked for this session; a second turn-end changes nothing.
            cts.Dispose();
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await SuperviseAsync(signal.Tenant, signal.DirectorId, signal.SessionId, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The session started working while we waited - the ordinary happy ending. Recorded by the
                // waiter itself, so nothing to add here.
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SessionSupervisor] episode FAILED: sid={signal.SessionId} director={signal.DirectorId}: {ex.Message}");
            }
            finally
            {
                // Remove OUR entry specifically: the key is the gate that stops a second loop starting, so
                // it must not be cleared on behalf of a loop somebody else owns.
                _running.TryRemove(new KeyValuePair<(TenantId, string), CancellationTokenSource>(key, cts));
                cts.Dispose();
            }
        });
    }

    /// <summary>
    /// Take the gate that allows one episode per session. A LIVE episode keeps it: a second turn end while one
    /// is being worked changes nothing. A CANCELLED episode gives it up.
    ///
    /// Cancelling does not take an episode out of the table - it leaves only when its task has finished
    /// unwinding, in the finally below. A turn end that lands in that gap is a NEW turn that ended, and it
    /// used to be dropped as "already being worked" by an episode that was never going to send anything. That
    /// gap is widest in exactly the case this class exists for: on a dead connection a turn fails the moment
    /// it starts, so "working" and the next turn end arrive milliseconds apart, and the second fault went
    /// unwatched with the session parked on it.
    ///
    /// Replacing the cancelled entry is safe because the old episode's cleanup removes only its OWN entry.
    /// </summary>
    private bool TryClaimEpisode((TenantId, string) key, CancellationTokenSource cts)
    {
        while (true)
        {
            if (_running.TryAdd(key, cts)) return true;
            if (!_running.TryGetValue(key, out var existing)) continue;   // it left between the two reads
            if (!existing.IsCancellationRequested) return false;           // a live episode owns the session
            if (_running.TryUpdate(key, cts, existing)) return true;       // supersede the cancelled one
        }
    }

    /// <summary>
    /// The session is working again. Cancels any wait in flight - the session recovered, on its own or
    /// because our "continue" landed - so nothing further is sent.
    /// </summary>
    public void OnSessionWorking(TenantId tenant, string sessionId)
    {
        if (_disposed || !tenant.IsValid || string.IsNullOrEmpty(sessionId)) return;
        if (_running.TryGetValue((tenant, sessionId), out var cts))
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { /* the loop already finished and disposed it */ }
        }
    }

    /// <summary>How many sends the current episode for this session has made (0 when there is no episode).
    /// Exposed for tests and diagnosis.</summary>
    internal int EpisodeAttempts(TenantId tenant, string sessionId)
        => _episodeAttempts.TryGetValue((tenant, sessionId), out var n) ? n : 0;

    /// <summary>
    /// The funnel and the ladder for ONE idle session, awaited. This is the test entry point; production
    /// reaches it through <see cref="OnTurnEnd"/>.
    /// </summary>
    internal async Task SuperviseAsync(TenantId tenant, string directorId, string sessionId, CancellationToken ct)
    {
        var settings = _env.Settings(tenant);
        if (!settings.Enabled) return;

        // ---- step 1 and 2: how did the turn end? ---------------------------------------------------------
        var rows = await _env.ReadScreenRowsAsync(tenant, directorId, sessionId, ct).ConfigureAwait(false);
        if (rows is null)
        {
            // Unreadable is not evidence of anything. Say so and stop - never guess a fault into existence.
            FileLog.Write($"[SessionSupervisor] sid={sessionId}: screen unreadable at turn end - no verdict, no action");
            return;
        }

        var fault = TerminatingFaultClassifier.Classify(rows);
        if (fault.Class == SessionFaultClass.None)
        {
            // The majority path: a finished turn is SUPPOSED to wait. Costs one screen read and nothing else,
            // and it ends the episode - the next fault starts counting from one again.
            ClearEpisode(tenant, sessionId);
            return;
        }

        var decidedByModel = false;
        if (fault.Class == SessionFaultClass.Unclassified && settings.ModelFallbackEnabled)
        {
            var reply = await _env.AskModelVerdictAsync(tenant, rows, ct).ConfigureAwait(false);
            var verdict = SupervisorVerdict.Parse(reply);
            var mapped = SupervisorVerdict.Map(verdict);
            decidedByModel = true;
            FileLog.Write($"[SessionSupervisor] sid={sessionId}: model fallback verdict={verdict ?? "(none)"} -> {mapped}");
            if (mapped == SessionFaultClass.None)
            {
                _env.Record(new SupervisorRecord(tenant, directorId, sessionId,
                    ActivityEventTypes.SupervisorFaultDetected, ActivityCauses.Unknown,
                    $"model verdict {verdict}: the turn ended cleanly - no action"));
                ClearEpisode(tenant, sessionId);
                return;
            }
            fault = new SessionFault(mapped, verdict ?? "unparsable-verdict");
        }

        _env.Record(new SupervisorRecord(tenant, directorId, sessionId,
            ActivityEventTypes.SupervisorFaultDetected, fault.LedgerCause,
            $"signature={fault.Signature}{(decidedByModel ? " (model verdict)" : "")}"));

        // ---- the ladder ----------------------------------------------------------------------------------
        var attempt = EpisodeAttempts(tenant, sessionId) + 1;
        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                // The session started working between the last send and this iteration - the ordinary happy
                // ending when a "continue" lands. Record it rather than unwinding silently: a recovery that
                // leaves no line in the recovery log is indistinguishable from one that never happened.
                RecordRecovered(tenant, directorId, sessionId, attempt - 1, "started working after the last continue");
                return;
            }

            var action = SupervisorPlanner.Next(fault.Class, attempt, settings);

            if (action.Kind == SupervisorActionKind.DoNothing)
            {
                ClearEpisode(tenant, sessionId);
                return;
            }

            if (action.Kind == SupervisorActionKind.Escalate)
            {
                await EscalateAsync(tenant, directorId, sessionId, action.Cause,
                    $"{action.Detail} (signature={fault.Signature})", ct).ConfigureAwait(false);
                return;
            }

            _env.Record(new SupervisorRecord(tenant, directorId, sessionId,
                ActivityEventTypes.SupervisorWaiting, action.Cause,
                $"attempt {attempt}: waiting {SupervisorPlanner.Describe(action.Delay)} before sending continue"));

            try
            {
                await _env.DelayAsync(action.Delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The session started working while we waited. That is the whole goal, whichever cause
                // produced it, so the episode ends here as a success.
                RecordRecovered(tenant, directorId, sessionId, attempt, "started working while the supervisor waited");
                return;
            }

            // ---- gate 3: never send into a session that is not still parked ------------------------------
            var state = _env.ReadActivityState(tenant, sessionId);
            if (state is null)
            {
                _env.Record(new SupervisorRecord(tenant, directorId, sessionId,
                    ActivityEventTypes.SupervisorStoodDown, ActivityCauses.SessionNotLive,
                    $"attempt {attempt}: the session is gone - nothing to continue"));
                ClearEpisode(tenant, sessionId);
                return;
            }
            if (IsWorking(state))
            {
                RecordRecovered(tenant, directorId, sessionId, attempt, "already working by the time the wait ended");
                return;
            }
            if (!IsParkedIdle(state))
            {
                // WaitingForPerm, Starting, Exiting: not a turn end the supervisor may act on. A real
                // permission prompt in particular must never be steam-rolled by a typed "continue".
                _env.Record(new SupervisorRecord(tenant, directorId, sessionId,
                    ActivityEventTypes.SupervisorStoodDown, ActivityCauses.Unknown,
                    $"attempt {attempt}: activity state {state} is not a parked turn end - stopping"));
                ClearEpisode(tenant, sessionId);
                return;
            }

            // ---- gate 4: a menu owns the screen -> refuse and raise a hand -------------------------------
            if (await _env.IsMenuOnScreenAsync(tenant, directorId, sessionId, ct).ConfigureAwait(false))
            {
                await EscalateAsync(tenant, directorId, sessionId, ActivityCauses.MenuOwnsScreen,
                    $"attempt {attempt}: a menu owns the screen, so \"{ContinueText}\" would answer it", ct)
                    .ConfigureAwait(false);
                return;
            }

            var sent = await _env.SendContinueAsync(tenant, directorId, sessionId, ct).ConfigureAwait(false);
            _episodeAttempts[(tenant, sessionId)] = attempt;
            _env.Record(new SupervisorRecord(tenant, directorId, sessionId,
                ActivityEventTypes.SupervisorContinueSent, action.Cause,
                $"attempt {attempt}: sent \"{ContinueText}\" - {(sent ? "delivered" : "the send did not land")}"));

            attempt++;
        }
    }

    /// <summary>
    /// The session is working again, so this pass is over. The attempt count is deliberately NOT cleared: a
    /// "continue" that reaches a still-broken network produces exactly this brief Working flicker before
    /// failing again, so clearing here would reset the ladder on every cycle and the ceiling could never
    /// fire - the infinite blind loop the issue forbids. The episode is closed by a turn that ends with NO
    /// fault on the screen, which is the only evidence that the work actually resumed.
    /// </summary>
    private void RecordRecovered(TenantId tenant, string directorId, string sessionId, int attempt, string how)
    {
        var which = attempt > 0 ? $"attempt {attempt}" : "before any continue was sent";
        _env.Record(new SupervisorRecord(tenant, directorId, sessionId,
            ActivityEventTypes.SupervisorRecovered, ActivityCauses.WorkingObservation,
            $"{which}: {how} - the attempt count is kept until a turn ends with no fault"));
    }

    private async Task EscalateAsync(TenantId tenant, string directorId, string sessionId,
        string cause, string detail, CancellationToken ct)
    {
        var record = new SupervisorRecord(tenant, directorId, sessionId,
            ActivityEventTypes.SupervisorEscalated, cause, detail);
        ClearEpisode(tenant, sessionId);
        await _env.EscalateAsync(record, ct).ConfigureAwait(false);
    }

    private void ClearEpisode(TenantId tenant, string sessionId)
        => _episodeAttempts.TryRemove((tenant, sessionId), out _);

    private static bool IsWorking(string state)
        => string.Equals(state, "Working", StringComparison.OrdinalIgnoreCase);

    /// <summary>The two states that mean "a turn ended and the session is waiting" - the same pair the
    /// turn-end watcher fires on and the auto-dismiss sweep acts on.</summary>
    private static bool IsParkedIdle(string state)
        => string.Equals(state, "WaitingForInput", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "Idle", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var cts in _running.Values)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
        _running.Clear();
        _episodeAttempts.Clear();
    }
}
