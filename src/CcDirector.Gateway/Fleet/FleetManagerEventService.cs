using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Wingman;

namespace CcDirector.Gateway.Fleet;

/// <summary>How a prompt send ended, as the delivery needs to know it.</summary>
public enum FleetManagerPromptSend
{
    /// <summary>The Director typed it.</summary>
    Accepted,

    /// <summary>The Director refused it: the session was not waiting for a prompt at the moment it would have typed.
    /// Nothing was typed.</summary>
    Busy,

    /// <summary>Nothing was sent: the Director is not connected.</summary>
    NotSent,

    /// <summary>It went out and nothing confirmed it was typed.</summary>
    Unanswered,

    /// <summary>Refused: the Director has not said it checks the session is waiting for a prompt before typing (it is
    /// older than that check), so nothing is sent to it - or it answered without saying it checked. Not a delivery.</summary>
    DirectorTooOld,

    /// <summary>The Director refused it: the owner has typed text into the session and not sent it. Nothing was typed.</summary>
    OwnerDraft,

    /// <summary>The Director refused it: the session's terminal submits a whole turn in one call that cannot be taken
    /// back, so a guarded prompt is never sent to it. Nothing was sent.</summary>
    OneCallSubmit,
}

/// <summary>Everything the Fleet Manager's events need from the Gateway around them, as one seam.</summary>
public interface IFleetManagerEventEnvironment
{
    /// <summary>The session this account has marked as its Fleet Manager, or null.</summary>
    string? MarkedFleetManager(TenantId tenant);

    /// <summary>What this account's Directors last pushed for one session, however stale, with the Director that
    /// pushed it - or null when no Director of the account holds it. One session's row, not the roster.</summary>
    (string DirectorId, SessionDto Session)? LastKnown(TenantId tenant, string sessionId);

    /// <summary>The account's fresh pushed roster, with every role and owner answer resolved across the whole of it.</summary>
    IReadOnlyList<(string DirectorId, SessionDto Session)> Roster(TenantId tenant);

    /// <summary>Whether one Director is connected and has said what it runs, and what that is.</summary>
    (FleetObservation Observation, IReadOnlyList<SessionDto> Sessions) DirectorFleet(TenantId tenant, string directorId);

    /// <summary>Whether this Director is CONFIRMED gone: it said goodbye (an orderly shutdown, which ends its sessions)
    /// and has not come back. A Director that is merely disconnected or silent, for however long, is not.</summary>
    bool DirectorShutDown(TenantId tenant, string directorId);

    /// <summary>Type one prompt into a session and press Enter, through the Gateway's ordinary prompt path - only if
    /// the Director finds the session waiting for a prompt at that moment.</summary>
    Task<FleetManagerPromptSend> SendPromptAsync(TenantId tenant, string directorId, string sessionId, string text, CancellationToken ct);

    /// <summary>Wait. The service's only clock for waiting, so a test can run the batching window instantly.</summary>
    Task DelayAsync(TimeSpan delay, CancellationToken ct);

    DateTime NowUtc();
}

/// <summary>How one delivery attempt to one Fleet Manager session ended.</summary>
public enum FleetManagerDeliveryResult
{
    Delivered,
    NothingOwed,
    Busy,
    NotLive,
    AlreadyDelivering,
    SendFailed,
    DirectorTooOld,
    OwnerDraft,
    OneCallSubmit,

    /// <summary>Held on the Gateway: the Fleet Manager's turn is not known to be finished without a question for the
    /// owner (no reading of its latest turn end yet, or the reading says it asked). Nothing was sent.</summary>
    TurnNotFinished,
}

/// <summary>
/// What the Gateway knows of the Fleet Manager's own latest turn end, and the Wingman's latest reading of it. In
/// process only: after a restart the turn-end watcher's first sighting of a waiting session raises a catch-up turn
/// end, which starts this again, and until then nothing is typed.
/// </summary>
internal sealed record FleetManagerTurn(
    DateTime? LatestTurnEndUtc,
    bool WorkingSinceTurnEnd,
    DateTime? ReadingStopUtc,
    TurnVerdictDto? Verdict,
    string? NoReadingReason);

/// <summary>
/// THE GATEWAY TELLS THE FLEET MANAGER WHEN A SESSION IT OWNS STOPS OR DIES (the Fleet Manager mission, step 4),
/// and keeps telling it until the event is acknowledged.
///
/// OWNED means: the session is controlled by the session the ACCOUNT has marked as its Fleet Manager. What is known
/// is used - the session's last pushed row however stale, or the owned session this Gateway last saw alive - never
/// only the fresh roster, so a session that has just left the roster is still somebody's.
///
/// A STOP IS STORED THE MOMENT IT IS SEEN (<see cref="OnTurnEnd"/>), on the caller's thread, before any reading and
/// before any roster lookup. It waits for its reading: <see cref="OnReadingCompleted"/> is raised by the Wingman for
/// EVERY reading that ends, whatever started it (a turn end or a snooze expiry), and attaches the reading - or the
/// reason there is none - to the waiting stop. A reading with no stop waiting is stored as a stop of its own unless
/// it is one already held, so each stop is told once. A stop that turned back into work before it was read is
/// withdrawn; one whose reading never reports back is delivered with the reason after <see cref="PendingLimit"/>.
///
/// A DEATH is stored when an owned session is seen to exit (<see cref="OnSessionExited"/>), when it is removed from
/// its Director's list (<see cref="OnSessionRemoved"/>), and by <see cref="ReconcileAsync"/>, which compares the
/// owned sessions this Gateway last knew alive - kept in the database, so a restart forgets none - with what the
/// Directors now report. A death is recorded ONLY on evidence: the session is reported exited, a connected Director
/// that has reported leaves it out, or its Director is confirmed gone (it said goodbye). ABSENCE IS NOT DEATH: a
/// session whose Director is disconnected or silent - after a Gateway restart, for any length of time - is not
/// counted dead, because a death is final and a partition is not. And no death is recorded while another Director
/// reports the session alive.
///
/// DELIVER ONLY WHEN THE FLEET MANAGER'S TURN IS FINISHED AND IT IS NOT ASKING THE OWNER ANYTHING (the Architect's
/// ruling on inspection round 2, finding 3). The Director reports a Claude Code session that finished its turn with
/// nothing asked as WaitingForInput - the same state as one that asked the owner a question, because the terminal
/// detector deliberately does not tell the two apart, and nothing in production ever assigns Idle. So WaitingForInput
/// alone is never enough: the Wingman's reading of the Fleet Manager's LATEST turn end must say it did not ask
/// (finished or continues-alone, with high confidence). A reading that says it needs the owner, a reading still being
/// formed, a reading of an older turn end, a failed reading, or none at all holds the events, and the Gateway writes
/// the reason (<see cref="DeliveryNote"/>). The reading's completion is itself a delivery trigger. Idle is accepted
/// as it stands. A delivery happens then, or - when an event arrives while the Fleet Manager is already in such a
/// state - after <see cref="BatchWindow"/>, so a burst becomes one prompt.
/// The prompt is sent with <see cref="PromptRequest.OnlyWhenWaitingForInput"/>: the DIRECTOR checks the session's
/// state at the moment it types and refuses otherwise, so a turn the owner has just started is never typed into. A
/// refused send leaves the events undelivered for the Fleet Manager's next idle moment.
///
/// AT LEAST ONCE. The prompt is typed before the delivery is saved, so a Gateway that stops between the two, or a
/// send that landed but was never confirmed, sends the same event again. Every event carries its id, and the Fleet
/// Manager ignores an id it has already handled. An event whose delivery WAS saved is not sent to that same session
/// again - otherwise every turn end would wake it for ever; a new Fleet Manager session is sent everything still open.
///
/// WHO MAY TYPE. This is one of the Gateway's features that types into a session, alongside the session supervisor
/// and Session Rules. It types into exactly one session - the account's marked Fleet Manager, which exists to be told
/// this - and only while that session is waiting for a prompt. Its production wiring,
/// <see cref="GatewayFleetManagerEventEnvironment"/>, is named in the guard that lists every direct caller of the
/// prompt send (RulesTypeNothingGuardTests), with that reason.
///
/// NOT BUILT HERE: pull request and report events (a later part of phase 1).
///
/// GAP, STATED: a session whose Director disconnects and never comes back, without saying goodbye, is never counted
/// dead - not after the push store forgets that Director, and not after any timeout. Its death is raised when the
/// Director reports again without it. That is the Architect's ruling: a missed death is better than a false one.
/// </summary>
public sealed class FleetManagerEventService : IDisposable
{
    private readonly ConcurrentDictionary<TenantId, (string SessionId, string Text)> _deliveryNotes = new();
    /// <summary>How long an event waits for more before it is delivered to an idle Fleet Manager.</summary>
    public static readonly TimeSpan BatchWindow = TimeSpan.FromSeconds(3);

    /// <summary>How long a stop may wait for its reading before it is delivered with the reason there is none
    /// (<see cref="FleetManagerEventStore.PendingLimit"/>).</summary>
    public static readonly TimeSpan PendingLimit = FleetManagerEventStore.PendingLimit;

    private readonly FleetManagerEventStore _store;
    private readonly IFleetManagerEventEnvironment _env;
    private readonly TimeSpan _batchWindow;
    private readonly DateTime _startedAtUtc;
    private readonly CancellationTokenSource _shutdown = new();

    // At most one delivery in flight per Fleet Manager session, and the ones asked for again while it ran. A pass that
    // typed nothing runs once more rather than drop the request; one that delivered does not, because the Fleet Manager
    // is now busy with that prompt and what was stored meanwhile waits for its next idle moment. Both under the lock.
    private readonly object _deliveryGate = new();
    private readonly HashSet<(TenantId Tenant, string SessionId)> _delivering = new();
    private readonly HashSet<(TenantId Tenant, string SessionId)> _deliverAgain = new();

    // A batch already waiting for this account. An event arriving meanwhile rides on it.
    private readonly ConcurrentDictionary<TenantId, byte> _batchPending = new();

    // An owned session no Director reports, already logged as not counted dead - so the timer does not repeat it.
    private readonly ConcurrentDictionary<(TenantId Tenant, string SessionId), byte> _unreportedLogged = new();

    // Sessions with a stop waiting for its reading in this process, so a reading of any other session costs no
    // database read unless the session is owned.
    private readonly ConcurrentDictionary<(TenantId Tenant, string SessionId), byte> _waitingStops = new();

    // What was last written for each owned session seen alive, so a sighting that changes nothing writes nothing.
    private readonly ConcurrentDictionary<(TenantId Tenant, string SessionId), (string Owner, string Name, string Director)> _noted = new();

    // The Fleet Manager's own latest turn end and the Wingman's reading of it, per Fleet Manager session.
    private readonly ConcurrentDictionary<(TenantId Tenant, string SessionId), FleetManagerTurn> _fleetManagerTurns = new();

    // Accounts whose stops left waiting by an earlier process have been given their reason.
    private readonly ConcurrentDictionary<TenantId, byte> _restartExpired = new();

    // Work started by the fire-and-forget entry points, so a test (and shutdown) can wait for it.
    private readonly ConcurrentDictionary<Task, byte> _running = new();
    private volatile bool _disposed;

    public FleetManagerEventService(FleetManagerEventStore store, IFleetManagerEventEnvironment environment,
        TimeSpan? batchWindow = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _env = environment ?? throw new ArgumentNullException(nameof(environment));
        _batchWindow = batchWindow ?? BatchWindow;
        _startedAtUtc = _env.NowUtc();
    }

    // ================================================================= what the Gateway observes

    /// <summary>
    /// A turn end was observed. The stop of an owned session is STORED HERE, synchronously, before this returns; the
    /// Fleet Manager's own turn end books a delivery. Never throws.
    /// </summary>
    /// <param name="wingmanRunning">False when this Gateway has no Wingman to read the stop: it is stored with that
    /// reason at once.</param>
    public void OnTurnEnd(TurnEndSignal signal, bool wingmanRunning)
    {
        if (_disposed || signal is null) return;
        var tenant = signal.Tenant;
        var sid = signal.SessionId;
        try
        {
            var marked = _env.MarkedFleetManager(tenant);
            if (!string.IsNullOrEmpty(marked) && SameId(sid, marked))
            {
                // THE FLEET MANAGER'S OWN TURN END. It opens a new turn whose reading is not in yet, so everything
                // owed waits for that reading (OnReadingCompleted), which is the delivery trigger. With no Wingman on
                // this Gateway there will be no reading, and the reason is recorded now. The attempt below writes the
                // reason the events are held, and delivers at once only to an Idle Fleet Manager.
                var key = (tenant, marked.ToLowerInvariant());
                var at = signal.ObservedAtUtc;
                var opened = wingmanRunning
                    ? new FleetManagerTurn(at, false, null, null, null)
                    : new FleetManagerTurn(at, false, at, null, NoWingmanReason);
                // An older turn end arriving late changes nothing.
                _fleetManagerTurns.AddOrUpdate(key, opened,
                    (_, t) => t.LatestTurnEndUtc is { } seen && seen > at ? t : opened);
                FileLog.Write($"[FleetManagerEventService] Fleet Manager {sid} turn end at {signal.ObservedAtUtc:O}: " +
                              $"events wait for the Wingman's reading of it (wingmanRunning={wingmanRunning})");
                TrackDelivery(tenant, marked);
                return;
            }

            var owner = OwnedSighting(tenant, sid, signal.DirectorId, signal.ObservedAtUtc, !signal.IsNewTurn, marked);
            if (owner is null) return;

            var stored = _store.RecordStop(tenant, owner, _env.NowUtc());
            if (stored is not null) _waitingStops[(tenant, sid)] = 0;
            if (!wingmanRunning)
            {
                ApplyReading(tenant, sid, verdict: null, "the Wingman is not running on this Gateway", owner: null);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetManagerEventService] turn end FAILED: sid={sid} tenant={tenant.ToLogString()}: " +
                          $"{ex.GetType().FullName}: {ex.Message}");
        }
    }

    /// <summary>A session was seen working: remember it alive if it is owned. Never throws.</summary>
    public void OnSessionWorking(TenantId tenant, string sessionId, string directorId)
    {
        if (_disposed || string.IsNullOrEmpty(sessionId)) return;
        try
        {
            // The Fleet Manager working again: the reading of its last turn end no longer describes it, even before
            // the next turn end is seen - so a waiting state pushed ahead of that turn end is not taken as finished.
            var fmKey = (tenant, sessionId.ToLowerInvariant());
            if (_fleetManagerTurns.TryGetValue(fmKey, out var turn) && !turn.WorkingSinceTurnEnd)
                _fleetManagerTurns[fmKey] = turn with { WorkingSinceTurnEnd = true };
            OwnedSighting(tenant, sessionId, directorId, _env.NowUtc(), isCatchUp: false, _env.MarkedFleetManager(tenant));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetManagerEventService] working FAILED: sid={sessionId} tenant={tenant.ToLogString()}: " +
                          $"{ex.GetType().FullName}: {ex.Message}");
        }
    }

    /// <summary>
    /// The Wingman finished a reading - of any session, whatever started it. Attaches it to the stop waiting for it,
    /// or stores it as a stop of an owned session when none is waiting. Never throws.
    /// </summary>
    public void OnReadingCompleted(TurnVerdictReadingCompleted completed)
    {
        if (_disposed || completed is null) return;
        var tenant = completed.Tenant;
        var sid = completed.SessionId;
        try
        {
            var marked = _env.MarkedFleetManager(tenant);
            if (!string.IsNullOrEmpty(marked) && SameId(sid, marked))
            {
                if (RecordFleetManagerReading(tenant, marked, completed)) TrackDelivery(tenant, marked);
                return;
            }

            var waiting = _waitingStops.ContainsKey((tenant, sid));
            FleetManagerStopSighting? owner = null;
            if (completed.Trigger is TurnVerdictTrigger.TurnEnd or TurnVerdictTrigger.SnoozeExpiry)
                owner = OwnedSighting(tenant, sid, completed.DirectorId, completed.StopObservedAtUtc, isCatchUp: false,
                    marked);
            if (!waiting && owner is null) return;

            var outcome = completed.Outcome;
            switch (outcome.Kind)
            {
                case TurnVerdictOutcomeKind.Judged:
                case TurnVerdictOutcomeKind.Reused:
                case TurnVerdictOutcomeKind.Failed when outcome.Verdict is not null:
                    ApplyReading(tenant, sid, outcome.Verdict, null, owner);
                    return;
                case TurnVerdictOutcomeKind.Failed:
                    ApplyReading(tenant, sid, null, "the Wingman's reading failed and no record of it was stored", owner);
                    return;
                case TurnVerdictOutcomeKind.Cancelled:
                    Withdraw(tenant, sid, "it went back to work while it was being read");
                    return;
                case TurnVerdictOutcomeKind.Skipped:
                    switch (outcome.SkipCause)
                    {
                        case ActivityCauses.WorkingObservation:
                            Withdraw(tenant, sid, "it was working again when the Wingman looked");
                            return;
                        case ActivityCauses.SessionExit:
                            // Not a stop: the death is its own event.
                            Withdraw(tenant, sid, "it had exited when the Wingman looked");
                            return;
                        case ActivityCauses.Unknown:
                            // The Gateway is stopping: the stop stays waiting, and the next start gives it its reason.
                            return;
                        case ActivityCauses.AlreadyJudging:
                            // Never the end of a flight; the flight it joined reports.
                            return;
                    }
                    ApplyReading(tenant, sid, null, SkipReason(outcome.SkipCause), owner);
                    return;
                default:
                    throw new InvalidOperationException($"unhandled verdict outcome {outcome.Kind}");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetManagerEventService] reading completed FAILED: sid={sid} tenant={tenant.ToLogString()}: " +
                          $"{ex.GetType().FullName}: {ex.Message}");
        }
    }

    /// <summary>A session's state moved to Exited. Stores the death of an owned session. Never throws.</summary>
    public void OnSessionExited(TenantId tenant, string sessionId, string directorId)
    {
        if (_disposed || string.IsNullOrEmpty(sessionId)) return;
        try
        {
            var known = _env.LastKnown(tenant, sessionId);
            var crashed = known?.Session.Crashed == true;
            RecordDeathIfOwned(tenant, sessionId, known?.Session, directorId, crashed,
                crashed ? "it crashed" : "it exited");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetManagerEventService] exit FAILED: sid={sessionId} tenant={tenant.ToLogString()}: " +
                          $"{ex.GetType().FullName}: {ex.Message}");
        }
    }

    /// <summary>A session was removed from its Director's list. Stores the death of an owned session. Never throws.</summary>
    public void OnSessionRemoved(TenantId tenant, string sessionId, string directorId)
    {
        if (_disposed || string.IsNullOrEmpty(sessionId)) return;
        try
        {
            RecordDeathIfOwned(tenant, sessionId, row: null, directorId, crashed: false,
                "its Director removed it from its session list; no exit was seen first, so whether it crashed is not known");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetManagerEventService] removal FAILED: sid={sessionId} tenant={tenant.ToLogString()}: " +
                          $"{ex.GetType().FullName}: {ex.Message}");
        }
    }

    /// <summary>
    /// A session's owner was changed by a hand over (step 8). <paramref name="row"/> is the session as its Director
    /// reported it after the change. Owned by the account's Fleet Manager now: it is remembered alive, so its stops and
    /// its death are the Fleet Manager's from this moment. Owned by anyone else, or nobody: what was kept of it is
    /// forgotten, and a stop still waiting for its reading is withdrawn, so its stops and its death go to its new owner
    /// and no longer to the Fleet Manager. Never throws.
    /// </summary>
    public void OnOwnerChanged(TenantId tenant, string directorId, SessionDto row)
    {
        if (_disposed || row is null || string.IsNullOrEmpty(row.SessionId)) return;
        var sid = row.SessionId;
        try
        {
            var marked = _env.MarkedFleetManager(tenant);
            if (!string.IsNullOrEmpty(marked) && IsOwnedBy(row, marked) && !IsExited(row))
            {
                FileLog.Write($"[FleetManagerEventService] owner changed: sid={sid} is now owned by the Fleet Manager {marked}");
                NoteAlive(tenant, row, directorId, marked);
                return;
            }
            FileLog.Write($"[FleetManagerEventService] owner changed: sid={sid} is owned by {row.ControllerSessionId ?? "the owner"} " +
                          "now; its stops and its death are no longer the Fleet Manager's");
            Forget(tenant, sid, $"its owner is {row.ControllerSessionId ?? "the owner"} now");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetManagerEventService] owner changed FAILED: sid={sid} tenant={tenant.ToLogString()}: " +
                          $"{ex.GetType().FullName}: {ex.Message}");
        }
    }

    /// <summary>
    /// THE RECONCILE, run at start and on a timer: stops left waiting by an earlier Gateway get their reason, owned
    /// sessions seen alive are remembered, and every owned session this Gateway last knew alive is counted dead ONLY
    /// when it is reported exited, when a connected Director that has reported its sessions leaves it out, or when
    /// its Director said goodbye - and never while another Director reports it alive. A session whose Director is
    /// disconnected or silent is left alone, however long. Then anything owed is delivered.
    /// </summary>
    public async Task ReconcileAsync(TenantId tenant)
    {
        if (_disposed) return;
        var now = _env.NowUtc();
        var changed = false;

        if (_restartExpired.TryAdd(tenant, 0))
            changed |= _store.ExpirePendingStops(tenant, _startedAtUtc,
                "the Gateway restarted before the Wingman's reading of this stop was stored").Count > 0;
        changed |= _store.ExpirePendingStops(tenant, now - PendingLimit, FleetManagerEventStore.PendingLimitReason).Count > 0;

        var marked = _env.MarkedFleetManager(tenant);
        if (!string.IsNullOrEmpty(marked))
            foreach (var (directorId, row) in _env.Roster(tenant))
                if (IsOwnedBy(row, marked) && !IsExited(row))
                    NoteAlive(tenant, row, directorId, marked);

        foreach (var owned in _store.AllOwnedAlive(tenant))
        {
            var known = _env.LastKnown(tenant, owned.SessionId);
            // HANDED OVER ELSEWHERE (step 8): the session is running and its row names another owner than the one this
            // was kept for - handed back to the owner, or to someone else. Its end is not that Fleet Manager's news.
            if (known is { } running && !IsExited(running.Session)
                && !(running.Session.IsControlled && SameId(running.Session.ControllerSessionId, owned.FleetManagerSessionId)))
            {
                Forget(tenant, owned.SessionId,
                    $"its row now names {running.Session.ControllerSessionId ?? "no owner"}, not {owned.FleetManagerSessionId}");
                continue;
            }
            string? detail = null;
            var crashed = false;
            var directorId = known?.DirectorId ?? owned.DirectorId;
            if (known is { } k && IsExited(k.Session))
            {
                crashed = k.Session.Crashed;
                detail = crashed ? "it had crashed when the Gateway next looked" : "it had exited when the Gateway next looked";
            }
            else
            {
                var (observation, sessions) = _env.DirectorFleet(tenant, directorId);
                if (observation == FleetObservation.Observed
                    && !sessions.Any(s => SameId(s.SessionId, owned.SessionId)))
                    detail = "it is no longer in its Director's session list; no exit was seen, so whether it crashed is not known";
                else if (observation != FleetObservation.Observed && _env.DirectorShutDown(tenant, directorId))
                    detail = "its Director shut down (it said goodbye) and has not come back; whether the session crashed first is not known";
                else if (known is null && observation != FleetObservation.Observed && _unreportedLogged.TryAdd((tenant, owned.SessionId), 0))
                    FileLog.Write($"[FleetManagerEventService] reconcile: sid={owned.SessionId} is not reported by any Director and its " +
                                  $"Director {directorId} is {observation}; NOT counted dead - absence is not death");
            }
            if (detail is null) continue;
            if (ReportedAliveElsewhere(tenant, owned.SessionId, directorId) is { } elsewhere)
            {
                FileLog.Write($"[FleetManagerEventService] reconcile: sid={owned.SessionId} NOT counted dead ({detail}): " +
                              $"Director {elsewhere} reports it alive");
                continue;
            }
            _unreportedLogged.TryRemove((tenant, owned.SessionId), out _);

            var death = _store.RecordDeath(tenant, new FleetManagerDeath(owned.SessionId, owned.SessionName,
                owned.FleetManagerSessionId, owned.DirectorId, crashed, detail), now);
            _waitingStops.TryRemove((tenant, owned.SessionId), out _);
            changed |= death is not null;
        }

        if (changed) ScheduleDelivery(tenant);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>Wait for every piece of work started so far to finish. For tests and shutdown.</summary>
    public async Task WhenIdleAsync()
    {
        while (!_running.IsEmpty)
            await Task.WhenAll(_running.Keys.ToArray()).ConfigureAwait(false);
    }

    // ================================================================= recording

    /// <summary>
    /// Who owns this session, from what is known, and remembers it alive: null when the account's marked Fleet
    /// Manager does not own it. The last pushed row is read first, however stale; with no row, the owned session
    /// this Gateway last saw alive answers, and the log says the row is missing.
    /// </summary>
    private FleetManagerStopSighting? OwnedSighting(TenantId tenant, string sid, string? directorId,
        DateTime observedAt, bool isCatchUp, string? marked)
    {
        var known = _env.LastKnown(tenant, sid);
        if (known is { } k)
        {
            if (string.IsNullOrEmpty(marked) || !IsOwnedBy(k.Session, marked)) return null;
            if (!IsExited(k.Session)) NoteAlive(tenant, k.Session, k.DirectorId, marked);
            return new FleetManagerStopSighting(sid, k.Session.Name ?? "", marked, k.DirectorId, observedAt, isCatchUp);
        }

        var tracked = _store.OwnedAlive(tenant, sid);
        if (tracked is null) return null;
        FileLog.Write($"[FleetManagerEventService] sid={sid}: no Director holds its row any more; using what was last " +
                      $"known of it (owner {tracked.FleetManagerSessionId}, seen alive {tracked.LastSeenAliveUtc:O})");
        return new FleetManagerStopSighting(sid, tracked.SessionName, tracked.FleetManagerSessionId,
            directorId ?? tracked.DirectorId, observedAt, isCatchUp);
    }

    private void NoteAlive(TenantId tenant, SessionDto row, string directorId, string owner)
    {
        var key = (tenant, row.SessionId);
        var value = (owner, row.Name ?? "", directorId);
        if (_noted.TryGetValue(key, out var last) && last == value) return;
        _store.NoteOwnedAlive(tenant, new FleetManagerOwnedSession(row.SessionId, owner, row.Name ?? "", directorId,
            _env.NowUtc()), _env.NowUtc());
        _noted[key] = value;
    }

    private void Forget(TenantId tenant, string sid, string why)
    {
        _store.ForgetOwnedAlive(tenant, sid, why);
        _noted.TryRemove((tenant, sid), out _);
        Withdraw(tenant, sid, $"its owner changed before its reading arrived ({why})");
    }

    private void ApplyReading(TenantId tenant, string sid, TurnVerdictDto? verdict, string? reason,
        FleetManagerStopSighting? owner)
    {
        var (result, _) = _store.AttachReading(tenant, sid, verdict, reason, owner, _env.NowUtc());
        _waitingStops.TryRemove((tenant, sid), out _);
        if (result != FleetManagerReadingResult.AlreadyHeld) ScheduleDelivery(tenant);
    }

    private void Withdraw(TenantId tenant, string sid, string why)
    {
        var removed = _store.WithdrawPendingStops(tenant, sid);
        _waitingStops.TryRemove((tenant, sid), out _);
        if (removed > 0)
            FileLog.Write($"[FleetManagerEventService] sid={sid}: the stop was withdrawn, not delivered - {why}");
    }

    /// <summary>The Director other than <paramref name="exceptDirectorId"/> that reports this session alive in the fresh
    /// roster, or null.</summary>
    private string? ReportedAliveElsewhere(TenantId tenant, string sid, string exceptDirectorId)
    {
        foreach (var (directorId, row) in _env.Roster(tenant))
            if (SameId(row.SessionId, sid) && !SameId(directorId, exceptDirectorId) && !IsExited(row))
                return directorId;
        return null;
    }

    private void RecordDeathIfOwned(TenantId tenant, string sid, SessionDto? row, string directorId, bool crashed, string detail)
    {
        // A SESSION ANOTHER DIRECTOR RUNS IS NOT DEAD. A moved session's old Director can report the removal after the
        // new one has reported it alive; a death is final, so it is not recorded on that.
        if (ReportedAliveElsewhere(tenant, sid, directorId) is { } elsewhere)
        {
            FileLog.Write($"[FleetManagerEventService] sid={sid}: NOT counted dead ({detail}, reported by Director {directorId}): " +
                          $"Director {elsewhere} reports it alive");
            return;
        }
        var marked = _env.MarkedFleetManager(tenant);
        FleetManagerDeath? death = null;
        if (row is not null && !string.IsNullOrEmpty(marked) && IsOwnedBy(row, marked))
        {
            death = new FleetManagerDeath(sid, row.Name ?? "", marked, directorId, crashed, detail);
        }
        else if (_store.OwnedAlive(tenant, sid) is { } tracked)
        {
            var missing = row is null ? "; its last row is gone, so its name is the one last seen" : "";
            death = new FleetManagerDeath(sid, tracked.SessionName, tracked.FleetManagerSessionId, directorId, crashed,
                detail + missing);
        }
        if (death is null) return;

        _waitingStops.TryRemove((tenant, sid), out _);
        if (_store.RecordDeath(tenant, death, _env.NowUtc()) is not null)
            ScheduleDelivery(tenant);
    }

    private static string SkipReason(string? cause) => cause switch
    {
        ActivityCauses.JudgeSwitchOff => "this account's Wingman judge switch is off",
        ActivityCauses.InFlightCap => "this account's limit on readings at once was reached",
        ActivityCauses.RateLimited => "the Wingman's model provider asked it to wait",
        ActivityCauses.BrandNew => "the session is brand new and has not finished a turn yet",
        ActivityCauses.SessionNotLive => "the Wingman could not see the session: its Director's report was not current",
        ActivityCauses.Held => "the Wingman did not read it: another session holds it now",
        { } other => $"the Wingman did not read this stop ({other})",
        null => "the Wingman did not read this stop",
    };

    // ================================================================= the Fleet Manager's own turn

    private const string NoWingmanReason = "the Wingman is not running on this Gateway";

    /// <summary>
    /// Keep the Wingman's reading of the Fleet Manager's own stop, when it is a reading of the latest turn end this
    /// Gateway saw. True when something was kept, so a delivery is worth attempting.
    /// </summary>
    private bool RecordFleetManagerReading(TenantId tenant, string marked, TurnVerdictReadingCompleted completed)
    {
        var outcome = completed.Outcome;
        TurnVerdictDto? verdict = null;
        string? reason = null;
        switch (outcome.Kind)
        {
            case TurnVerdictOutcomeKind.Judged:
            case TurnVerdictOutcomeKind.Reused:
            case TurnVerdictOutcomeKind.Failed when outcome.Verdict is not null:
                verdict = outcome.Verdict;
                break;
            case TurnVerdictOutcomeKind.Failed:
                reason = "the Wingman's reading of its turn failed and no record of it was stored";
                break;
            case TurnVerdictOutcomeKind.Cancelled:
                return false;
            case TurnVerdictOutcomeKind.Skipped:
                switch (outcome.SkipCause)
                {
                    case ActivityCauses.WorkingObservation:
                    case ActivityCauses.SessionExit:
                    case ActivityCauses.Unknown:
                    case ActivityCauses.AlreadyJudging:
                        return false;
                }
                reason = SkipReason(outcome.SkipCause);
                break;
            default:
                throw new InvalidOperationException($"unhandled verdict outcome {outcome.Kind}");
        }

        var key = (tenant, marked.ToLowerInvariant());
        var stop = completed.StopObservedAtUtc;
        var kept = false;
        _fleetManagerTurns.AddOrUpdate(key,
            _ =>
            {
                kept = true;
                return new FleetManagerTurn(stop, false, stop, verdict, reason);
            },
            (_, t) =>
            {
                if (t.LatestTurnEndUtc is { } latest && stop < latest) return t;
                if (t.ReadingStopUtc is { } had && stop < had) return t;
                kept = true;
                return t with
                {
                    LatestTurnEndUtc = t.LatestTurnEndUtc is { } l && l > stop ? l : stop,
                    ReadingStopUtc = stop,
                    Verdict = verdict,
                    NoReadingReason = reason,
                };
            });
        FileLog.Write($"[FleetManagerEventService] Fleet Manager {completed.SessionId} reading of the stop at {stop:O}: " +
                      $"{(kept ? "kept" : "ignored, it is older than the latest turn end")} " +
                      $"(verdict={verdict?.Verdict ?? "none"}, failed={verdict?.Failed}, reason={reason ?? "none"})");
        return kept;
    }

    /// <summary>
    /// Null when the Fleet Manager's turn is finished and it is not asking the owner anything - Idle, or
    /// WaitingForInput whose latest reading, of its latest turn end, is an accepted high-confidence finished or
    /// continues-alone. Otherwise the sentence saying why its events are held.
    /// </summary>
    internal string? WhyTurnIsNotFinished(TenantId tenant, SessionDto s)
    {
        if (IsExited(s)) return "The Fleet Manager has exited.";
        if (string.Equals(s.ActivityState, "Idle", StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.Equals(s.ActivityState, "WaitingForInput", StringComparison.OrdinalIgnoreCase))
            return $"The Fleet Manager is not waiting for a prompt (it is {s.ActivityState}).";

        if (!_fleetManagerTurns.TryGetValue((tenant, s.SessionId.ToLowerInvariant()), out var turn)
            || turn.LatestTurnEndUtc is null)
            return "This Gateway has not seen the Fleet Manager's turn end yet, so it cannot tell whether it is asking you something.";
        if (turn.WorkingSinceTurnEnd)
            return "The Fleet Manager went back to work after its last turn end; its next turn end has not been seen yet.";
        if (turn.NoReadingReason is { } why && turn.ReadingStopUtc >= turn.LatestTurnEndUtc)
            return $"The Fleet Manager's latest turn has no Wingman reading ({why}), so it cannot be told whether it is asking you something.";
        if (turn.ReadingStopUtc is null || turn.ReadingStopUtc < turn.LatestTurnEndUtc || turn.Verdict is null)
            return "The Wingman is still reading the Fleet Manager's latest turn; events are sent once it says the Fleet Manager is not asking you anything.";
        var v = turn.Verdict;
        if (v.Failed)
            return $"The Wingman's reading of the Fleet Manager's latest turn failed ({v.FailureReason ?? "no reason was given"}), so it cannot be told whether it is asking you something.";
        if (string.Equals(v.Verdict, TurnVerdictVocabulary.NeededYou, StringComparison.Ordinal))
            return "The Fleet Manager is waiting for your answer; events are sent after its next turn that asks you nothing.";
        if (!TurnVerdictVocabulary.IsCalm(v.Verdict) || !string.Equals(v.Confidence, "high", StringComparison.Ordinal))
            return $"The Wingman could not say the Fleet Manager's latest turn asks you nothing (it read it as {v.Verdict}, " +
                   $"{(string.IsNullOrEmpty(v.Confidence) ? "no" : v.Confidence)} confidence); events wait for a turn that does.";
        return null;
    }

    private void TrackDelivery(TenantId tenant, string fleetManagerSessionId)
    {
        Track(Task.Run(async () =>
        {
            var result = await DeliverToAsync(tenant, fleetManagerSessionId).ConfigureAwait(false);
            if (result == FleetManagerDeliveryResult.Busy) ScheduleDelivery(tenant);
        }));
    }

    // ================================================================= delivery

    /// <summary>An event was stored by someone else (an owner's answer, a moved mark): book a delivery for the account's
    /// Fleet Manager, which happens only while it is waiting for a prompt. Never throws.</summary>
    public void OnEventQueued(TenantId tenant)
    {
        if (_disposed) return;
        FileLog.Write($"[FleetManagerEventService] OnEventQueued: tenant={tenant.ToLogString()}");
        ScheduleDelivery(tenant);
    }

    /// <summary>Book one batched delivery for this account, unless one is already waiting.</summary>
    private void ScheduleDelivery(TenantId tenant)
    {
        if (_disposed || !_batchPending.TryAdd(tenant, 0)) return;
        Track(Task.Run(async () =>
        {
            try
            {
                try
                {
                    await _env.DelayAsync(_batchWindow, _shutdown.Token).ConfigureAwait(false);
                }
                finally
                {
                    // Released BEFORE the events are read, so an event stored after the read books a batch of its own.
                    _batchPending.TryRemove(tenant, out _);
                }
                await DeliverAllAsync(tenant).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                // Shutting down: the events stay stored and undelivered.
            }
            catch (Exception ex)
            {
                FileLog.Write($"[FleetManagerEventService] batched delivery FAILED: tenant={tenant.ToLogString()}: " +
                              $"{ex.GetType().FullName}: {ex.Message}");
            }
        }));
    }

    /// <summary>
    /// Why this account's events are not being delivered to its Fleet Manager right now, as a sentence the Gateway
    /// writes and a page shows as it is - or null when nothing holds them back. Set by a delivery the Director refused
    /// for a reason the owner can act on, and cleared by the next delivery attempt that is not refused for it. It
    /// belongs to the Fleet Manager session it was written for: once another session is marked, there is none.
    /// </summary>
    public string? DeliveryNote(TenantId tenant)
    {
        if (!_deliveryNotes.TryGetValue(tenant, out var note)) return null;
        return SameId(note.SessionId, _env.MarkedFleetManager(tenant)) ? note.Text : null;
    }

    private void NoteDelivery(TenantId tenant, string sessionId, FleetManagerDeliveryResult result, int waiting,
        string? heldBecause)
    {
        var text = result switch
        {
            FleetManagerDeliveryResult.TurnNotFinished => $"{heldBecause} {Capitalised(Waiting(waiting))}.",
            FleetManagerDeliveryResult.OwnerDraft => DeliveryNoteOwnerDraft(waiting),
            FleetManagerDeliveryResult.OneCallSubmit => DeliveryNoteOneCallSubmit(waiting),
            _ => null,
        };
        if (text is null)
        {
            if (_deliveryNotes.TryRemove(tenant, out _))
                FileLog.Write($"[FleetManagerEventService] delivery note cleared: tenant={tenant.ToLogString()}, result={result}");
            return;
        }
        _deliveryNotes[tenant] = (sessionId, text);
        FileLog.Write($"[FleetManagerEventService] delivery note set: tenant={tenant.ToLogString()}, session={sessionId}, " +
                      $"result={result}, waiting={waiting}, noteLength={text.Length}");
    }

    private static string Capitalised(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    private static string Waiting(int n) => n == 1 ? "1 event is waiting" : $"{n} events are waiting";

    internal static string DeliveryNoteOwnerDraft(int waiting) =>
        $"The Fleet Manager has your unsent text; {Waiting(waiting)}. " +
        (waiting == 1 ? "It is" : "They are") + " sent after you send your text.";

    internal static string DeliveryNoteOneCallSubmit(int waiting) =>
        "The Fleet Manager runs in a session whose terminal cannot take a send back, so no events are typed into it; " +
        $"{Waiting(waiting)}. Run the Fleet Manager in a terminal session to receive them.";

    /// <summary>Deliver what is owed to the account's marked Fleet Manager, if it is live and waiting for a prompt.</summary>
    public async Task DeliverAllAsync(TenantId tenant)
    {
        var marked = _env.MarkedFleetManager(tenant);
        if (string.IsNullOrEmpty(marked))
        {
            FileLog.Write($"[FleetManagerEventService] deliver: tenant={tenant.ToLogString()} has no Fleet Manager marked; events wait");
            return;
        }
        await DeliverToAsync(tenant, marked).ConfigureAwait(false);
    }

    /// <summary>Deliver everything owed to one Fleet Manager session, if it is the account's live marked Fleet Manager.</summary>
    public async Task<FleetManagerDeliveryResult> DeliverToAsync(TenantId tenant, string fleetManagerSessionId)
    {
        var key = (tenant, fleetManagerSessionId.ToLowerInvariant());
        lock (_deliveryGate)
        {
            if (!_delivering.Add(key))
            {
                // A TRIGGER DURING A DELIVERY IS NOT DROPPED. The one in flight may have read what was owed before this
                // trigger's event was stored, or found the turn not yet finished; unless it delivers, it runs again.
                _deliverAgain.Add(key);
                FileLog.Write($"[FleetManagerEventService] deliver to {fleetManagerSessionId}: a delivery is already in flight; " +
                              "it runs again when it ends unless it delivers");
                return FleetManagerDeliveryResult.AlreadyDelivering;
            }
        }

        var released = false;
        try
        {
            while (true)
            {
                var (result, waiting, heldBecause) = await DeliverOnceAsync(tenant, fleetManagerSessionId).ConfigureAwait(false);
                NoteDelivery(tenant, fleetManagerSessionId, result, waiting, heldBecause);
                bool again;
                lock (_deliveryGate)
                {
                    again = _deliverAgain.Remove(key);
                    if (!again || result == FleetManagerDeliveryResult.Delivered)
                    {
                        _delivering.Remove(key);
                        released = true;
                    }
                }
                if (released)
                {
                    if (again)
                        FileLog.Write($"[FleetManagerEventService] deliver to {fleetManagerSessionId}: asked for again while it " +
                                      "delivered; what is still owed waits for its next idle moment");
                    return result;
                }
                FileLog.Write($"[FleetManagerEventService] deliver to {fleetManagerSessionId}: asked for again while it ran " +
                              $"(result={result}); running again");
            }
        }
        finally
        {
            if (!released)
            {
                lock (_deliveryGate)
                {
                    _delivering.Remove(key);
                    _deliverAgain.Remove(key);
                }
            }
        }
    }

    /// <summary>One delivery attempt, and how many events were owed when it was made.</summary>
    private async Task<(FleetManagerDeliveryResult Result, int Waiting, string? HeldBecause)> DeliverOnceAsync(TenantId tenant, string fleetManagerSessionId)
    {
        var marked = _env.MarkedFleetManager(tenant);
        var fm = _env.Roster(tenant).FirstOrDefault(r => SameId(r.Session.SessionId, fleetManagerSessionId));
        if (fm.Session is null || !FleetManagerSessions.IsFleetManager(fm.Session, marked) || IsExited(fm.Session))
        {
            FileLog.Write($"[FleetManagerEventService] deliver to {fleetManagerSessionId}: not the account's live Fleet Manager; events wait");
            return (FleetManagerDeliveryResult.NotLive, 0, null);
        }
        var target = fm.Session.SessionId;

        // Asked of the database, so however many events wait before them, the oldest owed are found. A batch
        // larger than one prompt carries leaves the rest for the next idle moment, and the prompt says so.
        var found = _store.Owed(tenant, target, FleetManagerEventStore.MaxDeliveryBatch);
        var owed = found.Events;
        if (owed.Count == 0) return (FleetManagerDeliveryResult.NothingOwed, 0, null);
        var waiting = owed.Count + found.MoreOwed;

        // THE GATEWAY'S CHECK: its turn is finished and it is not asking the owner anything. The Director keeps its
        // own checks at the moment it types (waiting for a prompt, no unsent owner text, the input held for the send).
        if (!string.Equals(fm.Session.ActivityState, "Idle", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fm.Session.ActivityState, "WaitingForInput", StringComparison.OrdinalIgnoreCase))
        {
            FileLog.Write($"[FleetManagerEventService] deliver to {target}: {owed.Count} owed, " +
                          $"but it is {fm.Session.ActivityState} - waiting for its turn to end");
            return (FleetManagerDeliveryResult.Busy, waiting, null);
        }
        if (WhyTurnIsNotFinished(tenant, fm.Session) is { } held)
        {
            FileLog.Write($"[FleetManagerEventService] deliver to {target}: {owed.Count} owed, HELD: its turn is not finished " +
                          $"(state={fm.Session.ActivityState}, reasonLength={held.Length})");
            return (FleetManagerDeliveryResult.TurnNotFinished, waiting, held);
        }

        var text = FleetManagerEventPrompt.Build(owed, found.MoreOwed);
        var sent = await _env.SendPromptAsync(tenant, fm.DirectorId, target, text, _shutdown.Token).ConfigureAwait(false);
        switch (sent)
        {
            case FleetManagerPromptSend.Accepted:
                _store.MarkDelivered(tenant, owed.Select(e => Guid.Parse(e.Id)).ToList(), target, _env.NowUtc());
                FileLog.Write($"[FleetManagerEventService] delivered {owed.Count} event(s) to {target}, " +
                              $"{found.MoreOwed} more owed: " + string.Join(", ", owed.Select(e => e.Id)));
                return (FleetManagerDeliveryResult.Delivered, waiting, null);
            case FleetManagerPromptSend.Busy:
                FileLog.Write($"[FleetManagerEventService] deliver to {target}: the Director found it busy and typed nothing; " +
                              $"{owed.Count} event(s) wait for its next idle moment");
                return (FleetManagerDeliveryResult.Busy, waiting, null);
            case FleetManagerPromptSend.OwnerDraft:
                FileLog.Write($"[FleetManagerEventService] deliver to {target}: the owner has unsent text in it, so the Director " +
                              $"typed nothing; {owed.Count} event(s) wait until the owner sends their text");
                return (FleetManagerDeliveryResult.OwnerDraft, waiting, null);
            case FleetManagerPromptSend.OneCallSubmit:
                FileLog.Write($"[FleetManagerEventService] deliver to {target} REFUSED: its terminal submits in one call that " +
                              $"cannot be taken back, so no events are typed into it; {owed.Count} event(s) stay undelivered");
                return (FleetManagerDeliveryResult.OneCallSubmit, waiting, null);
            case FleetManagerPromptSend.DirectorTooOld:
                FileLog.Write($"[FleetManagerEventService] deliver to {target} REFUSED: its Director {fm.DirectorId} does not check " +
                              $"the session is waiting before it types, so no events are typed into it; {owed.Count} event(s) " +
                              "stay undelivered until the Fleet Manager runs on a Director that does");
                return (FleetManagerDeliveryResult.DirectorTooOld, waiting, null);
            default:
                FileLog.Write($"[FleetManagerEventService] deliver to {target} FAILED: send={sent}; " +
                              $"{owed.Count} event(s) stay undelivered until the next trigger");
                return (FleetManagerDeliveryResult.SendFailed, waiting, null);
        }
    }

    // ================================================================= shared

    private static bool SameId(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsOwnedBy(SessionDto s, string marked)
        => s.IsControlled && SameId(s.ControllerSessionId, marked);

    private static bool IsExited(SessionDto s)
        => s.Crashed || string.Equals(s.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase);

    private void Track(Task task)
    {
        _running[task] = 0;
        _ = task.ContinueWith(t =>
        {
            _running.TryRemove(t, out _);
            if (t.Exception is not null)
                FileLog.Write($"[FleetManagerEventService] work FAULTED: {t.Exception.GetBaseException().Message}");
        }, TaskScheduler.Default);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}

/// <summary>The production wiring: the push store, the tunnel prompt verb, the account's mark.</summary>
internal sealed class GatewayFleetManagerEventEnvironment : IFleetManagerEventEnvironment
{
    private readonly Streaming.PushedSessionStore _pushed;
    private readonly TimeSpan _stale;
    private readonly Func<TenantId, string, Api.SessionVerbClient?> _route;
    private readonly Func<TenantId, string?> _mark;
    private readonly Func<TenantId, string, bool> _checksIdleBeforeTyping;
    private readonly Func<TenantId, string, bool> _directorShutDown;
    private readonly Func<TenantId, IDisposable>? _enterTenantScope;

    /// <param name="mark">The account's marked Fleet Manager session (the tenant setting).</param>
    /// <param name="checksIdleBeforeTyping">Whether a Director said, on its Hello, that it honours
    /// <see cref="PromptRequest.OnlyWhenWaitingForInput"/>. Nothing is sent to one that did not.</param>
    /// <param name="directorShutDown">Whether a Director said goodbye and has not come back (the registry's stop stamp,
    /// which the next Hello clears).</param>
    /// <param name="enterTenantScope">Enters the account's scope for the send: the tunnel lookup that carries the
    /// prompt is partitioned, and this runs on a background task with no scope of its own.</param>
    public GatewayFleetManagerEventEnvironment(Streaming.PushedSessionStore pushed, TimeSpan stale,
        Func<TenantId, string, Api.SessionVerbClient?> route, Func<TenantId, string?> mark,
        Func<TenantId, string, bool> checksIdleBeforeTyping,
        Func<TenantId, string, bool> directorShutDown,
        Func<TenantId, IDisposable>? enterTenantScope = null)
    {
        _pushed = pushed ?? throw new ArgumentNullException(nameof(pushed));
        _stale = stale;
        _route = route ?? throw new ArgumentNullException(nameof(route));
        _mark = mark ?? throw new ArgumentNullException(nameof(mark));
        _checksIdleBeforeTyping = checksIdleBeforeTyping ?? throw new ArgumentNullException(nameof(checksIdleBeforeTyping));
        _directorShutDown = directorShutDown ?? throw new ArgumentNullException(nameof(directorShutDown));
        _enterTenantScope = enterTenantScope;
    }

    public string? MarkedFleetManager(TenantId tenant) => _mark(tenant);

    public (string DirectorId, SessionDto Session)? LastKnown(TenantId tenant, string sessionId)
        => _pushed.TryGetLastKnownSession(tenant, sessionId);

    public IReadOnlyList<(string DirectorId, SessionDto Session)> Roster(TenantId tenant)
    {
        // The push store hands out copies, so stamping them touches nothing it holds.
        var roster = _pushed.SnapshotFresh(tenant, _stale);
        FleetRoleResolver.Stamp(roster.Select(r => r.Session).Where(s => s is not null).ToList(), _mark(tenant));
        return roster;
    }

    public (FleetObservation Observation, IReadOnlyList<SessionDto> Sessions) DirectorFleet(TenantId tenant, string directorId)
        => _pushed.ConnectedFleet(tenant, directorId);

    public bool DirectorShutDown(TenantId tenant, string directorId) => _directorShutDown(tenant, directorId);

    public async Task<FleetManagerPromptSend> SendPromptAsync(TenantId tenant, string directorId, string sessionId,
        string text, CancellationToken ct)
    {
        // AN OLDER DIRECTOR GETS NOTHING TYPED INTO IT. It would ignore the request to check first and type whatever the
        // session is doing, and no answer it gives afterwards can take the keystrokes back.
        if (!_checksIdleBeforeTyping(tenant, directorId))
        {
            FileLog.Write($"[GatewayFleetManagerEventEnvironment] NOT sent sid={sessionId}: director {directorId} has not said it " +
                          "checks the session is waiting for a prompt before typing (it is older than that check)");
            return FleetManagerPromptSend.DirectorTooOld;
        }

        using var scope = _enterTenantScope?.Invoke(tenant);
        var route = _route(tenant, directorId);
        if (route is null)
        {
            FileLog.Write($"[GatewayFleetManagerEventEnvironment] NOT sent sid={sessionId}: director {directorId} is not connected");
            return FleetManagerPromptSend.NotSent;
        }
        // The product wrote this text, not a person: it is marked as not a human turn. The Director types it only if
        // the session is waiting for a prompt at that moment.
        var request = new PromptRequest
        {
            Text = text, AppendEnter = true, WaitForIdle = false, AgentDriven = true, OnlyWhenWaitingForInput = true,
        };
        var sent = await route.SendPromptAsync(sessionId, request, ct).ConfigureAwait(false);
        return Classify(sessionId, sent);
    }

    /// <summary>
    /// What a send did, read from the Director's own answer - as the answer route's tunnel channel reads it. An Ok
    /// with a body that does not say it was accepted, AND that the idle check was made, is not a delivery.
    /// </summary>
    internal static FleetManagerPromptSend Classify(string sessionId, Api.SessionVerbClient.PromptSendOutcome sent)
    {
        switch (sent.Kind)
        {
            case Api.SessionVerbClient.PromptSendKind.Accepted when sent.Body is { Accepted: true, IdleChecked: true }:
                return FleetManagerPromptSend.Accepted;
            case Api.SessionVerbClient.PromptSendKind.Accepted when sent.Body is { Accepted: true }:
                // A receipt without the check is refused, not delivered: the events stay owed.
                FileLog.Write($"[GatewayFleetManagerEventEnvironment] REFUSED receipt sid={sessionId}: the Director answered that it " +
                              "typed WITHOUT saying it checked the session was waiting for a prompt; not counted as delivered");
                return FleetManagerPromptSend.DirectorTooOld;
            case Api.SessionVerbClient.PromptSendKind.Accepted
                when sent.Body is { RefusedBusy: true, RefusedFor: PromptResponse.RefusedForOwnerDraft }:
                FileLog.Write($"[GatewayFleetManagerEventEnvironment] sid={sessionId}: refused by the Director - the owner has unsent text in it");
                return FleetManagerPromptSend.OwnerDraft;
            case Api.SessionVerbClient.PromptSendKind.Accepted
                when sent.Body is { RefusedBusy: true, RefusedFor: PromptResponse.RefusedForOneCallSubmit }:
                FileLog.Write($"[GatewayFleetManagerEventEnvironment] sid={sessionId}: refused by the Director - {sent.Body.Error}");
                return FleetManagerPromptSend.OneCallSubmit;
            case Api.SessionVerbClient.PromptSendKind.Accepted when sent.Body is { RefusedBusy: true } refused:
                FileLog.Write($"[GatewayFleetManagerEventEnvironment] sid={sessionId}: refused by the Director - it is {refused.ActivityState}");
                return FleetManagerPromptSend.Busy;
            case Api.SessionVerbClient.PromptSendKind.Accepted:
                FileLog.Write($"[GatewayFleetManagerEventEnvironment] send NOT CONFIRMED sid={sessionId}: " +
                              (sent.Body?.Error ?? "the Director answered without accepting the prompt"));
                return FleetManagerPromptSend.Unanswered;
            case Api.SessionVerbClient.PromptSendKind.NeverLeftTheGateway:
                FileLog.Write($"[GatewayFleetManagerEventEnvironment] NOT sent sid={sessionId}: {sent.Detail}");
                return FleetManagerPromptSend.NotSent;
            default:
                FileLog.Write($"[GatewayFleetManagerEventEnvironment] send UNANSWERED sid={sessionId}: {sent.Detail}");
                return FleetManagerPromptSend.Unanswered;
        }
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);

    public DateTime NowUtc() => DateTime.UtcNow;
}
