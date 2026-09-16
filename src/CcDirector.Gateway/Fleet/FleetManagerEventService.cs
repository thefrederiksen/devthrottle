using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Wingman;

namespace CcDirector.Gateway.Fleet;

/// <summary>How a prompt send ended, as the delivery needs to know it.</summary>
public enum FleetManagerPromptSend
{
    /// <summary>The Director answered Ok.</summary>
    Accepted,

    /// <summary>Nothing was sent: the Director is not connected.</summary>
    NotSent,

    /// <summary>It went out and nothing confirmed it.</summary>
    Unanswered,
}

/// <summary>Everything the Fleet Manager's events need from the Gateway around them, as one seam.</summary>
public interface IFleetManagerEventEnvironment
{
    /// <summary>The account's fresh pushed roster, with every role and owner answer resolved across the whole of it.</summary>
    IReadOnlyList<(string DirectorId, SessionDto Session)> Roster(TenantId tenant);

    /// <summary>The session this account has marked as its Fleet Manager, or null.</summary>
    string? MarkedFleetManager(TenantId tenant);

    /// <summary>Whether the account's Wingman readings are shown (false: still a shadow record).</summary>
    bool VerdictsShown(TenantId tenant);

    /// <summary>Type one prompt into a session and press Enter, through the Gateway's ordinary prompt path.</summary>
    Task<FleetManagerPromptSend> SendPromptAsync(TenantId tenant, string directorId, string sessionId, string text, CancellationToken ct);

    /// <summary>Wait. The service's only clock, so a test can run the batching window instantly.</summary>
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
/// and keeps telling any new Fleet Manager until the event is acknowledged.
///
/// ENQUEUE, ONCE PER HAPPENING. A <c>stop</c> is recorded when a session whose direct live owner is a Fleet
/// Manager reaches a turn end and the Wingman's reading of it has been stored - or the Wingman did not read it for
/// a reason that leaves the Fleet Manager needing to look (the judge switch is off, the account's ceiling was
/// reached), and then the event says why. A stop that JOINED a reading already in flight produces nothing: the
/// stop that started that reading produces the event. A stop that turned back into work, or into an exit, produces
/// nothing either - the session is not stopped. A <c>died</c> is recorded when such a session exits or crashes.
/// The store refuses a second copy of either.
///
/// DELIVER AT THE TURN-END BOUNDARY, NEVER MID-TURN. Nothing here interrupts a Fleet Manager that is working. A
/// delivery happens at the Fleet Manager's own turn end, or - when an event arrives while it is already idle -
/// after <see cref="BatchWindow"/>, so a burst of stops becomes one prompt. Its activity is read again immediately
/// before the prompt is typed. At most one delivery is in flight per Fleet Manager session.
///
/// WHERE. The account's live Fleet Manager session - the one the account has marked - is read off the fresh roster
/// through <see cref="FleetManagerSessions.IsFleetManager"/>. An event goes to the session it was addressed to when that is
/// live; otherwise to the ONE other live Fleet Manager (it was moved or restarted); with none it waits; with more
/// than one it goes nowhere and the log says so - never a guess.
///
/// KEPT UNTIL ACKNOWLEDGED, WITHOUT A LOOP. An event already delivered to a Fleet Manager session is never sent to
/// that same session again - otherwise every turn end would wake it for ever. It is sent again only to a different
/// Fleet Manager session. The digest lists every unacknowledged event, so a Fleet Manager starting a conversation
/// sees them all.
///
/// A FAILED SEND leaves the events undelivered and is logged. It is not retried in a loop: the next trigger - the
/// Fleet Manager's next turn end, or the next event - tries again.
///
/// WHO MAY TYPE. This is one of the Gateway's features that types into a session, alongside the session supervisor
/// and Session Rules. It types into exactly one kind of session - a Fleet Manager the owner started, which exists to
/// be told this - and only at its idle boundary. Its production wiring,
/// <see cref="GatewayFleetManagerEventEnvironment"/>, is named in the guard that lists every direct caller of the
/// prompt send (RulesTypeNothingGuardTests), with that reason.
/// </summary>
public sealed class FleetManagerEventService : IDisposable
{
    /// <summary>How long an event waits for more before it is delivered to an idle Fleet Manager.</summary>
    public static readonly TimeSpan BatchWindow = TimeSpan.FromSeconds(3);

    private readonly FleetManagerEventStore _store;
    private readonly IFleetManagerEventEnvironment _env;
    private readonly TimeSpan _batchWindow;
    private readonly CancellationTokenSource _shutdown = new();

    // At most one delivery in flight per Fleet Manager session.
    private readonly ConcurrentDictionary<(TenantId Tenant, string SessionId), byte> _delivering = new();

    // A batch already waiting for this account. An event arriving meanwhile rides on it.
    private readonly ConcurrentDictionary<TenantId, byte> _batchPending = new();

    // Work started by the fire-and-forget entry points, so a test (and shutdown) can wait for it.
    private readonly ConcurrentDictionary<Task, byte> _running = new();
    private volatile bool _disposed;

    public FleetManagerEventService(FleetManagerEventStore store, IFleetManagerEventEnvironment environment,
        TimeSpan? batchWindow = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _env = environment ?? throw new ArgumentNullException(nameof(environment));
        _batchWindow = batchWindow ?? BatchWindow;
    }

    /// <summary>A turn end was observed. Fire and forget; never throws.</summary>
    /// <param name="verdict">The Wingman's reading this turn end started or joined, or null when there is no Wingman.</param>
    public void OnTurnEnd(TurnEndSignal signal, Task<TurnVerdictOutcome>? verdict)
    {
        if (_disposed || signal is null) return;
        Track(Task.Run(() => HandleTurnEndAsync(signal, verdict)));
    }

    /// <summary>A session's state moved to Exited. Fire and forget; never throws.</summary>
    public void OnSessionExited(TenantId tenant, string sessionId)
    {
        if (_disposed || string.IsNullOrEmpty(sessionId)) return;
        Track(Task.Run(() => HandleExitAsync(tenant, sessionId)));
    }

    /// <summary>Wait for every piece of work started so far to finish. For tests and shutdown.</summary>
    public async Task WhenIdleAsync()
    {
        while (!_running.IsEmpty)
            await Task.WhenAll(_running.Keys.ToArray()).ConfigureAwait(false);
    }

    /// <summary>The awaited form of <see cref="OnTurnEnd"/>.</summary>
    public async Task HandleTurnEndAsync(TurnEndSignal signal, Task<TurnVerdictOutcome>? verdict)
    {
        ArgumentNullException.ThrowIfNull(signal);
        var tenant = signal.Tenant;
        var sid = signal.SessionId;
        try
        {
            var self = Find(_env.Roster(tenant), sid)?.Session;
            if (self is null) return;

            if (FleetManagerSessions.IsFleetManager(self, _env.MarkedFleetManager(tenant)))
            {
                // THE FLEET MANAGER'S OWN TURN END: the boundary everything owed to it waits for. The roster can
                // still be a moment behind the turn end that raised this, so a busy answer books one batched retry.
                var result = await DeliverToAsync(tenant, sid).ConfigureAwait(false);
                if (result == FleetManagerDeliveryResult.Busy) ScheduleDelivery(tenant);
                return;
            }

            if (!self.OwnedByFleetManager || string.IsNullOrEmpty(self.ControllerSessionId)) return;

            var fleetManager = self.ControllerSessionId;
            var name = self.Name ?? "";
            var outcome = verdict is null ? null : await verdict.ConfigureAwait(false);
            var draft = StopDraft(sid, name, fleetManager, outcome, isCatchUp: !signal.IsNewTurn);
            if (draft is null)
            {
                FileLog.Write($"[FleetManagerEventService] turn end sid={sid}: no stop event " +
                              $"(verdict outcome={outcome?.Kind}, cause={outcome?.SkipCause})");
                return;
            }

            if (_store.Enqueue(tenant, draft, _env.NowUtc()) is not null)
                ScheduleDelivery(tenant);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetManagerEventService] turn end FAILED: sid={sid} tenant={tenant.ToLogString()}: " +
                          $"{ex.GetType().FullName}: {ex.Message}");
        }
    }

    /// <summary>The awaited form of <see cref="OnSessionExited"/>.</summary>
    public Task HandleExitAsync(TenantId tenant, string sessionId)
    {
        try
        {
            var self = Find(_env.Roster(tenant), sessionId)?.Session;
            if (self is null || !self.OwnedByFleetManager || string.IsNullOrEmpty(self.ControllerSessionId))
                return Task.CompletedTask;

            var draft = new FleetManagerEventDraft(FleetManagerEventStore.KindDied, sessionId, self.Name ?? "",
                self.ControllerSessionId, Crashed: self.Crashed);
            if (_store.Enqueue(tenant, draft, _env.NowUtc()) is not null)
                ScheduleDelivery(tenant);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetManagerEventService] exit FAILED: sid={sessionId} tenant={tenant.ToLogString()}: " +
                          $"{ex.GetType().FullName}: {ex.Message}");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// The stop event a turn end's reading makes, or null when it makes none. Public for the tests that pin each
    /// outcome's answer.
    /// </summary>
    public static FleetManagerEventDraft? StopDraft(string sessionId, string name, string fleetManager,
        TurnVerdictOutcome? outcome, bool isCatchUp)
    {
        string? reason;
        if (outcome is null)
        {
            reason = "the Wingman is not running on this Gateway";
        }
        else switch (outcome.Kind)
        {
            case TurnVerdictOutcomeKind.Judged:
            case TurnVerdictOutcomeKind.Reused:
            case TurnVerdictOutcomeKind.Failed when outcome.Verdict is not null:
                return new FleetManagerEventDraft(FleetManagerEventStore.KindStop, sessionId, name, fleetManager,
                    Verdict: outcome.Verdict, IsCatchUp: isCatchUp);
            case TurnVerdictOutcomeKind.Failed:
                reason = "the Wingman's reading failed and no record of it was stored";
                break;
            case TurnVerdictOutcomeKind.Cancelled:
                // The session went back to work while it was being read: it is not stopped.
                return null;
            case TurnVerdictOutcomeKind.Skipped:
                reason = outcome.SkipCause switch
                {
                    // Joined a reading already in flight: the stop that started it makes the event.
                    ActivityCauses.AlreadyJudging => null,
                    // Not a stop any more (working again, exited - the died event covers that - or gone), or the
                    // Gateway is shutting down, or the owner changed while it waited.
                    ActivityCauses.WorkingObservation or ActivityCauses.SessionExit or ActivityCauses.SessionNotLive
                        or ActivityCauses.Unknown or ActivityCauses.Held => null,
                    ActivityCauses.JudgeSwitchOff => "this account's Wingman judge switch is off",
                    ActivityCauses.InFlightCap => "this account's limit on readings at once was reached",
                    ActivityCauses.RateLimited => "the Wingman's model provider asked it to wait",
                    ActivityCauses.BrandNew => "the session is brand new and has not finished a turn yet",
                    { } other => $"the Wingman did not read this stop ({other})",
                    null => "the Wingman did not read this stop",
                };
                if (reason is null) return null;
                break;
            default:
                throw new InvalidOperationException($"unhandled verdict outcome {outcome.Kind}");
        }

        return new FleetManagerEventDraft(FleetManagerEventStore.KindStop, sessionId, name, fleetManager,
            NoVerdictReason: reason, IsCatchUp: isCatchUp);
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

    /// <summary>Deliver to every idle live Fleet Manager session that is owed something.</summary>
    public async Task DeliverAllAsync(TenantId tenant)
    {
        var fleetManagers = LiveFleetManagers(_env.Roster(tenant), _env.MarkedFleetManager(tenant));
        var targets = _store.Unacknowledged(tenant)
            .Select(e => TargetOf(e, fleetManagers))
            .Where(t => t is not null)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var target in targets)
            await DeliverToAsync(tenant, target!).ConfigureAwait(false);
    }

    /// <summary>Deliver everything owed to one Fleet Manager session, if it is live and idle.</summary>
    public async Task<FleetManagerDeliveryResult> DeliverToAsync(TenantId tenant, string fleetManagerSessionId)
    {
        var key = (tenant, fleetManagerSessionId);
        if (!_delivering.TryAdd(key, 0))
        {
            FileLog.Write($"[FleetManagerEventService] deliver to {fleetManagerSessionId}: a delivery is already in flight");
            return FleetManagerDeliveryResult.AlreadyDelivering;
        }

        try
        {
            var roster = _env.Roster(tenant);
            var fleetManagers = LiveFleetManagers(roster, _env.MarkedFleetManager(tenant));
            var fm = roster.FirstOrDefault(r => fleetManagers.Contains(r.Session.SessionId)
                && string.Equals(r.Session.SessionId, fleetManagerSessionId, StringComparison.Ordinal));
            if (fm.Session is null)
            {
                FileLog.Write($"[FleetManagerEventService] deliver to {fleetManagerSessionId}: not a live Fleet Manager");
                return FleetManagerDeliveryResult.NotLive;
            }

            var owed = _store.Unacknowledged(tenant)
                .Where(e => string.Equals(TargetOf(e, fleetManagers), fleetManagerSessionId, StringComparison.Ordinal))
                .Where(e => !string.Equals(e.DeliveredTo, fleetManagerSessionId, StringComparison.Ordinal))
                .ToList();
            if (owed.Count == 0) return FleetManagerDeliveryResult.NothingOwed;

            if (!IsIdle(fm.Session))
            {
                FileLog.Write($"[FleetManagerEventService] deliver to {fleetManagerSessionId}: {owed.Count} owed, " +
                              $"but it is {fm.Session.ActivityState} - waiting for its turn end");
                return FleetManagerDeliveryResult.Busy;
            }

            var text = FleetManagerEventPrompt.Build(owed, _env.VerdictsShown(tenant));

            // READ AGAIN IMMEDIATELY BEFORE TYPING: the owner may have started talking to it since.
            var now = Find(_env.Roster(tenant), fleetManagerSessionId);
            if (now is not { } current || !IsIdle(current.Session))
            {
                FileLog.Write($"[FleetManagerEventService] deliver to {fleetManagerSessionId}: no longer idle at the send - waiting");
                return FleetManagerDeliveryResult.Busy;
            }

            var sent = await _env.SendPromptAsync(tenant, current.DirectorId, fleetManagerSessionId, text, _shutdown.Token)
                .ConfigureAwait(false);
            if (sent != FleetManagerPromptSend.Accepted)
            {
                FileLog.Write($"[FleetManagerEventService] deliver to {fleetManagerSessionId} FAILED: send={sent}; " +
                              $"{owed.Count} event(s) stay undelivered until the next trigger");
                return FleetManagerDeliveryResult.SendFailed;
            }

            _store.MarkDelivered(tenant, owed.Select(e => Guid.Parse(e.Id)).ToList(), fleetManagerSessionId, _env.NowUtc());
            FileLog.Write($"[FleetManagerEventService] delivered {owed.Count} event(s) to {fleetManagerSessionId}");
            return FleetManagerDeliveryResult.Delivered;
        }
        finally
        {
            _delivering.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Which live Fleet Manager session an event goes to, or null when it must wait (none live) or must not be
    /// guessed (the one it was addressed to is gone and more than one other is live).
    /// </summary>
    private static string? TargetOf(FleetManagerEventDto e, IReadOnlySet<string> liveFleetManagers)
    {
        if (liveFleetManagers.Contains(e.AddressedTo)) return e.AddressedTo;
        if (liveFleetManagers.Count == 1) return liveFleetManagers.First();
        if (liveFleetManagers.Count > 1)
            FileLog.Write($"[FleetManagerEventService] event {e.Id} NOT DELIVERED: its Fleet Manager {e.AddressedTo} is gone " +
                          $"and {liveFleetManagers.Count} other Fleet Manager sessions are live " +
                          $"({string.Join(", ", liveFleetManagers)}); refusing to guess which one takes it");
        return null;
    }

    private static IReadOnlySet<string> LiveFleetManagers(IReadOnlyList<(string DirectorId, SessionDto Session)> roster,
        string? marked)
        => roster.Select(r => r.Session)
            .Where(s => FleetManagerSessions.IsFleetManager(s, marked) && !IsExited(s))
            .Select(s => s.SessionId)
            .ToHashSet(StringComparer.Ordinal);

    private static (string DirectorId, SessionDto Session)? Find(
        IReadOnlyList<(string DirectorId, SessionDto Session)> roster, string sessionId)
    {
        foreach (var r in roster)
            if (string.Equals(r.Session.SessionId, sessionId, StringComparison.Ordinal))
                return r;
        return null;
    }

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

/// <summary>The production wiring: the push store's fresh roster, the tunnel prompt verb, the account's settings.</summary>
internal sealed class GatewayFleetManagerEventEnvironment : IFleetManagerEventEnvironment
{
    private readonly Streaming.PushedSessionStore _pushed;
    private readonly TimeSpan _stale;
    private readonly Func<TenantId, string, Api.SessionVerbClient?> _route;
    private readonly Func<TenantId, bool> _verdictsShown;
    private readonly Func<TenantId, string?> _mark;
    private readonly Func<TenantId, IDisposable>? _enterTenantScope;

    /// <param name="enterTenantScope">Enters the account's scope for the send: the tunnel lookup that carries the
    /// prompt is partitioned, and this runs on a background task with no scope of its own.</param>
    public GatewayFleetManagerEventEnvironment(Streaming.PushedSessionStore pushed, TimeSpan stale,
        Func<TenantId, string, Api.SessionVerbClient?> route, Func<TenantId, bool> verdictsShown,
        Func<TenantId, string?> mark,
        Func<TenantId, IDisposable>? enterTenantScope = null)
    {
        _mark = mark ?? throw new ArgumentNullException(nameof(mark));
        _enterTenantScope = enterTenantScope;
        _pushed = pushed ?? throw new ArgumentNullException(nameof(pushed));
        _stale = stale;
        _route = route ?? throw new ArgumentNullException(nameof(route));
        _verdictsShown = verdictsShown ?? throw new ArgumentNullException(nameof(verdictsShown));
    }

    public IReadOnlyList<(string DirectorId, SessionDto Session)> Roster(TenantId tenant)
    {
        // The push store hands out copies, so stamping them touches nothing it holds.
        var roster = _pushed.SnapshotFresh(tenant, _stale);
        FleetRoleResolver.Stamp(roster.Select(r => r.Session).Where(s => s is not null).ToList(), _mark(tenant));
        return roster;
    }

    public string? MarkedFleetManager(TenantId tenant) => _mark(tenant);

    public bool VerdictsShown(TenantId tenant) => _verdictsShown(tenant);

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
        // The product wrote this text, not a person: it is marked as not a human turn.
        var request = new PromptRequest { Text = text, AppendEnter = true, WaitForIdle = false, AgentDriven = true };
        var sent = await route.SendPromptAsync(sessionId, request, ct).ConfigureAwait(false);
        switch (sent.Kind)
        {
            case Api.SessionVerbClient.PromptSendKind.Accepted:
                return FleetManagerPromptSend.Accepted;
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
