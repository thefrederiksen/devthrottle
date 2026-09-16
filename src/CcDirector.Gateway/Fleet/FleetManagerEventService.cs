using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
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
}

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
/// Directors now report.
///
/// DELIVER ONLY WHEN THE FLEET MANAGER IS WAITING FOR A PROMPT. A delivery happens at the Fleet Manager's own turn
/// end, or - when an event arrives while it is idle - after <see cref="BatchWindow"/>, so a burst becomes one prompt.
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
/// GAP, STATED: a session whose Director has disconnected while its row is still held by the push store is neither
/// alive nor dead to the reconcile; its death is raised when the Director reports again, or when the push store
/// forgets that Director.
/// </summary>
public sealed class FleetManagerEventService : IDisposable
{
    /// <summary>How long an event waits for more before it is delivered to an idle Fleet Manager.</summary>
    public static readonly TimeSpan BatchWindow = TimeSpan.FromSeconds(3);

    /// <summary>How long a stop may wait for its reading before it is delivered with the reason there is none.</summary>
    public static readonly TimeSpan PendingLimit = TimeSpan.FromMinutes(5);

    /// <summary>How long after this Gateway started a Director may stay silent before the owned sessions it last
    /// reported, and that no Director reports now, are counted dead.</summary>
    public static readonly TimeSpan DirectorGrace = TimeSpan.FromMinutes(10);

    private readonly FleetManagerEventStore _store;
    private readonly IFleetManagerEventEnvironment _env;
    private readonly TimeSpan _batchWindow;
    private readonly DateTime _startedAtUtc;
    private readonly CancellationTokenSource _shutdown = new();

    // At most one delivery in flight per Fleet Manager session.
    private readonly ConcurrentDictionary<(TenantId Tenant, string SessionId), byte> _delivering = new();

    // A batch already waiting for this account. An event arriving meanwhile rides on it.
    private readonly ConcurrentDictionary<TenantId, byte> _batchPending = new();

    // Sessions with a stop waiting for its reading in this process, so a reading of any other session costs no
    // database read unless the session is owned.
    private readonly ConcurrentDictionary<(TenantId Tenant, string SessionId), byte> _waitingStops = new();

    // What was last written for each owned session seen alive, so a sighting that changes nothing writes nothing.
    private readonly ConcurrentDictionary<(TenantId Tenant, string SessionId), (string Owner, string Name, string Director)> _noted = new();

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
                // THE FLEET MANAGER'S OWN TURN END: the boundary everything owed to it waits for. The Director makes
                // the idle check at the send, so a pushed state a moment behind this turn end is only a first filter,
                // and a busy answer books one batched retry.
                Track(Task.Run(async () =>
                {
                    var result = await DeliverToAsync(tenant, marked).ConfigureAwait(false);
                    if (result == FleetManagerDeliveryResult.Busy) ScheduleDelivery(tenant);
                }));
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
            var waiting = _waitingStops.ContainsKey((tenant, sid));
            FleetManagerStopSighting? owner = null;
            if (completed.Trigger is TurnVerdictTrigger.TurnEnd or TurnVerdictTrigger.SnoozeExpiry)
                owner = OwnedSighting(tenant, sid, completed.DirectorId, completed.StopObservedAtUtc, isCatchUp: false,
                    _env.MarkedFleetManager(tenant));
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
    /// THE RECONCILE, run at start and on a timer: stops left waiting by an earlier Gateway get their reason, owned
    /// sessions seen alive are remembered, and every owned session this Gateway last knew alive that is now exited,
    /// or absent from a Director that has reported, is counted dead - as is one whose Director has stayed silent for
    /// <see cref="DirectorGrace"/> since this Gateway started. Then anything owed is delivered.
    /// </summary>
    public async Task ReconcileAsync(TenantId tenant)
    {
        if (_disposed) return;
        var now = _env.NowUtc();
        var changed = false;

        if (_restartExpired.TryAdd(tenant, 0))
            changed |= _store.ExpirePendingStops(tenant, _startedAtUtc,
                "the Gateway restarted before the Wingman's reading of this stop was stored").Count > 0;
        changed |= _store.ExpirePendingStops(tenant, now - PendingLimit,
            $"no reading of this stop was stored within {PendingLimit.TotalMinutes:F0} minutes").Count > 0;

        var marked = _env.MarkedFleetManager(tenant);
        if (!string.IsNullOrEmpty(marked))
            foreach (var (directorId, row) in _env.Roster(tenant))
                if (IsOwnedBy(row, marked) && !IsExited(row))
                    NoteAlive(tenant, row, directorId, marked);

        foreach (var owned in _store.AllOwnedAlive(tenant))
        {
            var known = _env.LastKnown(tenant, owned.SessionId);
            string? detail = null;
            var crashed = false;
            if (known is { } k)
            {
                if (!IsExited(k.Session)) continue;
                crashed = k.Session.Crashed;
                detail = crashed ? "it had crashed when the Gateway next looked" : "it had exited when the Gateway next looked";
            }
            else
            {
                var (observation, _) = _env.DirectorFleet(tenant, owned.DirectorId);
                if (observation == FleetObservation.Observed)
                    detail = "it is no longer in its Director's session list; no exit was seen, so whether it crashed is not known";
                else if (now - _startedAtUtc >= DirectorGrace)
                    detail = $"its Director has not reported its sessions in the {DirectorGrace.TotalMinutes:F0} minutes since the "
                             + "Gateway started and no Director reports it; it is presumed gone, and whether it crashed is not known";
            }
            if (detail is null) continue;

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

    private void RecordDeathIfOwned(TenantId tenant, string sid, SessionDto? row, string directorId, bool crashed, string detail)
    {
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

    // ================================================================= delivery

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
        if (!_delivering.TryAdd(key, 0))
        {
            FileLog.Write($"[FleetManagerEventService] deliver to {fleetManagerSessionId}: a delivery is already in flight");
            return FleetManagerDeliveryResult.AlreadyDelivering;
        }

        try
        {
            var marked = _env.MarkedFleetManager(tenant);
            var fm = _env.Roster(tenant).FirstOrDefault(r => SameId(r.Session.SessionId, fleetManagerSessionId));
            if (fm.Session is null || !FleetManagerSessions.IsFleetManager(fm.Session, marked) || IsExited(fm.Session))
            {
                FileLog.Write($"[FleetManagerEventService] deliver to {fleetManagerSessionId}: not the account's live Fleet Manager; events wait");
                return FleetManagerDeliveryResult.NotLive;
            }
            var target = fm.Session.SessionId;

            var owed = _store.Unacknowledged(tenant)
                .Where(e => !e.ReadingPending)
                .Where(e => !SameId(e.DeliveredTo, target))
                .ToList();
            if (owed.Count == 0) return FleetManagerDeliveryResult.NothingOwed;

            // A first filter on the pushed state; the Director makes the check that counts, at the moment it types.
            if (!IsIdle(fm.Session))
            {
                FileLog.Write($"[FleetManagerEventService] deliver to {target}: {owed.Count} owed, " +
                              $"but it is {fm.Session.ActivityState} - waiting for it to wait for a prompt");
                return FleetManagerDeliveryResult.Busy;
            }

            var text = FleetManagerEventPrompt.Build(owed);
            var sent = await _env.SendPromptAsync(tenant, fm.DirectorId, target, text, _shutdown.Token).ConfigureAwait(false);
            switch (sent)
            {
                case FleetManagerPromptSend.Accepted:
                    _store.MarkDelivered(tenant, owed.Select(e => Guid.Parse(e.Id)).ToList(), target, _env.NowUtc());
                    FileLog.Write($"[FleetManagerEventService] delivered {owed.Count} event(s) to {target}: " +
                                  string.Join(", ", owed.Select(e => e.Id)));
                    return FleetManagerDeliveryResult.Delivered;
                case FleetManagerPromptSend.Busy:
                    FileLog.Write($"[FleetManagerEventService] deliver to {target}: the Director found it busy and typed nothing; " +
                                  $"{owed.Count} event(s) wait for its next idle moment");
                    return FleetManagerDeliveryResult.Busy;
                default:
                    FileLog.Write($"[FleetManagerEventService] deliver to {target} FAILED: send={sent}; " +
                                  $"{owed.Count} event(s) stay undelivered until the next trigger");
                    return FleetManagerDeliveryResult.SendFailed;
            }
        }
        finally
        {
            _delivering.TryRemove(key, out _);
        }
    }

    // ================================================================= shared

    private static bool SameId(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsOwnedBy(SessionDto s, string marked)
        => s.IsControlled && SameId(s.ControllerSessionId, marked);

    /// <summary>Idle means waiting for a prompt. A session waiting on a permission question is not idle: typing
    /// into it would answer the question.</summary>
    private static bool IsIdle(SessionDto s)
        => !IsExited(s)
           && (string.Equals(s.ActivityState, "Idle", StringComparison.OrdinalIgnoreCase)
               || string.Equals(s.ActivityState, "WaitingForInput", StringComparison.OrdinalIgnoreCase));

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
    private readonly Func<TenantId, IDisposable>? _enterTenantScope;

    /// <param name="mark">The account's marked Fleet Manager session (the tenant setting).</param>
    /// <param name="enterTenantScope">Enters the account's scope for the send: the tunnel lookup that carries the
    /// prompt is partitioned, and this runs on a background task with no scope of its own.</param>
    public GatewayFleetManagerEventEnvironment(Streaming.PushedSessionStore pushed, TimeSpan stale,
        Func<TenantId, string, Api.SessionVerbClient?> route, Func<TenantId, string?> mark,
        Func<TenantId, IDisposable>? enterTenantScope = null)
    {
        _pushed = pushed ?? throw new ArgumentNullException(nameof(pushed));
        _stale = stale;
        _route = route ?? throw new ArgumentNullException(nameof(route));
        _mark = mark ?? throw new ArgumentNullException(nameof(mark));
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

    public async Task<FleetManagerPromptSend> SendPromptAsync(TenantId tenant, string directorId, string sessionId,
        string text, CancellationToken ct)
    {
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
    /// with a body that does not say it was accepted is not a delivery.
    /// </summary>
    internal static FleetManagerPromptSend Classify(string sessionId, Api.SessionVerbClient.PromptSendOutcome sent)
    {
        switch (sent.Kind)
        {
            case Api.SessionVerbClient.PromptSendKind.Accepted when sent.Body is { Accepted: true } body:
                if (!body.IdleChecked)
                    FileLog.Write($"[GatewayFleetManagerEventEnvironment] sid={sessionId}: the Director typed the events WITHOUT " +
                                  "checking the session was waiting for a prompt - it is older than that check");
                return FleetManagerPromptSend.Accepted;
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
