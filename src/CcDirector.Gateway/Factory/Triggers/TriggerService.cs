using System.Collections.Concurrent;
using System.Globalization;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Fleet;

namespace CcDirector.Gateway.Factory.Triggers;

/// <summary>Why a check report was not recorded.</summary>
public enum TriggerReportRefusal
{
    None,
    /// <summary>No trigger has that id or name in this account.</summary>
    NoSuchTrigger,
    /// <summary>Another Director on the machine holds this trigger's check.</summary>
    HeldByAnotherDirector,
    /// <summary>A check from a definition, reported by a Director that never said on Hello that it runs them.</summary>
    CannotRunDefinitionChecks,
}

/// <summary>The answer to one check report: the run row it wrote, or why it wrote none.</summary>
/// <remarks>A check that begins a session start has no row yet: <see cref="Run"/> is null and
/// <see cref="Starting"/> is the start, which writes the row when it returns.</remarks>
public sealed record TriggerReportResult(TriggerReportRefusal Refusal, TriggerRunEntity? Run, string? Error)
{
    /// <summary>The session start this check began, running on the Gateway's lifetime; it ends with the check's run
    /// row, or null when none could be written. Null when the check did not begin a start.</summary>
    public Task<TriggerRunEntity?>? Starting { get; init; }

    /// <summary>What the check counted, when it began a start.</summary>
    public int? StartingCount { get; init; }

    public static TriggerReportResult Refused(TriggerReportRefusal refusal, string error) => new(refusal, null, error);

    public static TriggerReportResult StartingIn(Task<TriggerRunEntity?> starting, int count)
        => new(TriggerReportRefusal.None, null, null) { Starting = starting, StartingCount = count };
}

/// <summary>
/// Starts a session for a trigger on its machine - production wraps
/// <c>MachineSessionSpawner.SpawnOnMachineWithOutcomeAsync</c>, the same path schedules use. Returns the new
/// session's id, or the reason none started and whether that failure left the outcome unknown.
/// </summary>
public delegate Task<TriggerStartAttempt> TriggerSessionStarter(
    string machine, NewSessionRequest request, CancellationToken ct);

/// <summary>What one session start came to: its id, or the reason none was returned. <see cref="OutcomeUnknown"/>
/// is true when the Gateway cannot know whether the Director created the session (it stopped waiting, or the tunnel
/// dropped mid-command) - the Director may have created it anyway.</summary>
public sealed record TriggerStartAttempt(string? SessionId, string? Error, bool OutcomeUnknown)
{
    public static TriggerStartAttempt Started(string sessionId) => new(sessionId, null, false);
    public static TriggerStartAttempt Failed(string error) => new(null, error, false);
    public static TriggerStartAttempt Unknown(string error) => new(null, error, true);
}

/// <summary>
/// THE GATEWAY DECIDES AND STARTS (the Website Business Factory mission, product track). A Director runs a
/// trigger's check and reports what the process produced; this reads it under the check contract, decides, starts
/// the session when there is work, and records one run row whatever happened:
///
///  - the check broke its contract                          -> <c>failed</c>, with the reason
///  - it counted nothing                                     -> <c>nothing-to-do</c>
///  - it counted work, and the trigger is paused             -> <c>paused</c>
///  - it counted work, and this trigger's last session lives -> <c>skipped-running</c>
///  - it counted work, and nothing stands in the way         -> take the lock, answer at once, start one session
///                                                              off the request; when the start returns,
///                                                              <c>started</c> with its id, or <c>failed</c>
///
/// EVERY CHECK IS ALSO A FACTORY ACTIVITY ROW. Beside its own run history, each recorded check appends one row to
/// the append-only factory activity record: the trigger's factory and factory agent, the actor
/// <c>trigger:&lt;trigger id&gt;</c>, the outcome in the record's words (<c>skipped-running</c> is the record's
/// <c>skipped</c>), and one plain sentence. The Cockpit groups those rows by outcome and actor, never by the sentence.
///
/// ONE AT A TIME, UNTIL THE SESSION HAS ENDED. The schedule's overlap guard is held only while a session is being
/// STARTED. This lock is the started session itself: while the session this trigger last started is still alive in
/// the Gateway's session list, no second one starts - and it is taken BEFORE the start begins, so a start still in
/// flight holds it too. Deciding and taking it happen under a per-trigger lock, so two reports arriving together
/// cannot both start one.
///
/// A START WHOSE OUTCOME IS UNKNOWN KEEPS THE LOCK. When the Gateway stops waiting for the Director's answer, or the
/// tunnel drops mid-command, the Director may have created the session anyway (seen live: a first start that spent
/// 27 seconds installing skills outlived the Gateway's 30-second wait, and releasing the lock started a second
/// session). So that start writes a <c>failed</c> row saying the outcome is unknown and the lock is held, and the
/// trigger reads RED "start outcome unknown". At each later check, the session is looked for by the start's unique
/// name: found, it is adopted - a new <c>started</c> row with its id, and the lock is that session; not found within
/// <see cref="StartGrace"/> of the start, the lock lapses with a row saying so. A DEFINITE failure releases at once.
/// </summary>
public sealed class TriggerService
{
    /// <summary>How long a session just started counts as alive before any Director has reported it. A start
    /// returns before the session shows in the roster, and "not in the list yet" must not read as "ended".</summary>
    public static readonly TimeSpan StartGrace = TimeSpan.FromMinutes(5);

    /// <summary>The actor prefix of every factory activity row a trigger writes: <c>trigger:&lt;id&gt;</c>.</summary>
    public const string ActorPrefix = "trigger:";

    private readonly TriggerStore _store;
    private readonly FactoryActivityRecord _activity;
    private readonly TriggerSessionStarter _startSession;
    private readonly Func<TenantId, string, SessionDto?> _findSession;
    private readonly Func<TenantId, string, SessionDto?> _findSessionByName;
    private readonly Func<TenantId, TimeZoneInfo> _timeZone;
    private readonly Func<DateTime> _nowUtc;
    private readonly CancellationToken _startLifetime;
    private readonly Func<TenantId, string, bool> _directorRunsDefinitionChecks;
    private readonly Func<TenantId, string, bool> _machineRunsDefinitionChecks;
    private readonly Func<TenantId, string, PublishedFactoryDefinition?> _definitionOf;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    // The triggers whose start is running right now on this Gateway. A lock with no session that is NOT in here is a
    // start that ended with its outcome unknown (or one a stopped Gateway left behind): the check looks for its session.
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();

    /// <param name="findSession">The last row any Director of the account reported for a session, or null when
    /// the Gateway knows no such session.</param>
    /// <param name="findSessionByName">The last row any Director of the account reported for a session a trigger
    /// started with this exact name, or null - how a start whose outcome was unknown finds its session.</param>
    /// <param name="timeZone">The account's time zone, for the time in a started session's name.</param>
    /// <param name="startLifetime">The Gateway's own lifetime, which every session start runs on - cancelled only when
    /// the Gateway stops, never by the request that asked for the start.</param>
    /// <param name="directorRunsDefinitionChecks">Whether this account's Director (by id) said on Hello that it runs
    /// checks from a factory's definition. A definition trigger is handed to, claimed by and reported from such a
    /// Director only (Factory Definition, phase 5b, design 2.6).</param>
    /// <param name="machineRunsDefinitionChecks">Whether any connected Director of this account on the machine (by
    /// name) said so - when none has, a definition trigger's status says so instead of going silent.</param>
    /// <param name="definitionOf">The published definition of a factory (by id) in the account, or null when none is
    /// published; throws <see cref="InvalidOperationException"/> with the reason when the published version cannot be
    /// read (<see cref="FactorySeatStart.ReadPublished"/>). A definition trigger's session is built from it (phase 5b,
    /// brief D item 8); a command trigger never reads it.</param>
    public TriggerService(TriggerStore store, FactoryActivityRecord activity, TriggerSessionStarter startSession,
        Func<TenantId, string, SessionDto?> findSession, Func<TenantId, string, SessionDto?> findSessionByName,
        Func<TenantId, TimeZoneInfo> timeZone, Func<DateTime> nowUtc, CancellationToken startLifetime,
        Func<TenantId, string, bool> directorRunsDefinitionChecks, Func<TenantId, string, bool> machineRunsDefinitionChecks,
        Func<TenantId, string, PublishedFactoryDefinition?> definitionOf)
    {
        _startLifetime = startLifetime;
        _definitionOf = definitionOf ?? throw new ArgumentNullException(nameof(definitionOf));
        _directorRunsDefinitionChecks = directorRunsDefinitionChecks ?? throw new ArgumentNullException(nameof(directorRunsDefinitionChecks));
        _machineRunsDefinitionChecks = machineRunsDefinitionChecks ?? throw new ArgumentNullException(nameof(machineRunsDefinitionChecks));
        _findSessionByName = findSessionByName ?? throw new ArgumentNullException(nameof(findSessionByName));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _startSession = startSession ?? throw new ArgumentNullException(nameof(startSession));
        _findSession = findSession ?? throw new ArgumentNullException(nameof(findSession));
        _timeZone = timeZone ?? throw new ArgumentNullException(nameof(timeZone));
        _nowUtc = nowUtc ?? throw new ArgumentNullException(nameof(nowUtc));
    }

    public TriggerStore Store => _store;

    /// <summary>A trigger as the routes serve it, with its status decided now.</summary>
    public TriggerDto ToDto(TriggerEntity t)
    {
        var status = TriggerStatusFold.For(t.CreatedUtc, t.IntervalSeconds, t.LastCheckUtc, t.LastOutcome, t.LastReason,
            t.LastSessionId, t.LastStartedUtc is { } s ? DateTime.SpecifyKind(s, DateTimeKind.Utc) : null, _nowUtc(),
            startOutcomeUnknown: IsHeldByUnknownStart(t),
            noDefinitionDirectorOn: t.IsDefinitionCheck && !_machineRunsDefinitionChecks(new TenantId(t.TenantId), t.Machine)
                ? t.Machine
                : null);
        return new TriggerDto
        {
            Id = t.Id.ToString("D"),
            Name = t.Name,
            Factory = t.Factory,
            FactoryAgent = t.FactoryAgent,
            Machine = t.Machine,
            RepoPath = t.RepoPath,
            CheckCommand = t.CheckCommand,
            IntervalSeconds = t.IntervalSeconds,
            Prompt = t.Prompt,
            Paused = t.Paused,
            CreatedBy = t.CreatedBy,
            CreatedUtc = DateTime.SpecifyKind(t.CreatedUtc, DateTimeKind.Utc),
            Seat = t.Seat,
            DefinitionVersion = t.DefinitionVersion,
            CheckPath = t.CheckPath,
            LastSessionId = t.LastSessionId,
            LastCheckUtc = t.LastCheckUtc is { } c ? DateTime.SpecifyKind(c, DateTimeKind.Utc) : null,
            LastOutcome = t.LastOutcome,
            Status = status.Kind,
            StatusText = status.Text,
        };
    }

    public static TriggerRunDto ToDto(TriggerRunEntity r) => new()
    {
        Id = r.Id.ToString("D"),
        TriggerId = r.TriggerId.ToString("D"),
        CheckedUtc = DateTime.SpecifyKind(r.CheckedUtc, DateTimeKind.Utc),
        RecordedUtc = DateTime.SpecifyKind(r.RecordedUtc, DateTimeKind.Utc),
        Outcome = r.Outcome,
        Count = r.Count,
        SessionId = r.SessionId,
        Reason = r.Reason,
        DirectorId = r.DirectorId,
    };

    /// <summary>
    /// The checks this Director must run: the triggers on its machine it holds, or takes over. A COMMAND trigger is
    /// handed exactly as it always was. A DEFINITION trigger (Factory Definition, phase 5b, design 2.1) is handed with
    /// its kind, factory, version, check path and declared secrets, and the newest state its check left - and with no
    /// command and no folder, because its check is downloaded from the published version and never runs in a checkout.
    /// A definition trigger is handed ONLY to a Director that said on Hello that it runs them (design 2.6): an older
    /// Director is handed none and claims none, so it never runs one and never writes a run row for one.
    /// </summary>
    public IReadOnlyList<TriggerAssignmentDto> AssignmentsFor(TenantId tenant, string directorId, string machine)
    {
        FileLog.Write($"[TriggerService] AssignmentsFor: tenant={tenant.ToLogString()}, director={directorId}, machine={machine}");
        var runsDefinitionChecks = _directorRunsDefinitionChecks(tenant, directorId);
        return _store.ClaimForDirector(tenant, directorId, machine, _nowUtc(), runsDefinitionChecks)
            .Select(t => t.IsDefinitionCheck ? DefinitionAssignment(tenant, t) : new TriggerAssignmentDto
            {
                Id = t.Id.ToString("D"),
                Name = t.Name,
                CheckCommand = t.CheckCommand,
                RepoPath = t.RepoPath,
                IntervalSeconds = t.IntervalSeconds,
                TimeoutSeconds = TriggerDefinition.TimeoutSecondsFor(t.IntervalSeconds),
            })
            .ToList();
    }

    private TriggerAssignmentDto DefinitionAssignment(TenantId tenant, TriggerEntity t) => new()
    {
        Id = t.Id.ToString("D"),
        Name = t.Name,
        CheckCommand = "",
        RepoPath = "",
        IntervalSeconds = t.IntervalSeconds,
        TimeoutSeconds = TriggerDefinition.TimeoutSecondsFor(t.IntervalSeconds),
        Kind = TriggerKinds.Definition,
        Seat = t.Seat,
        Factory = t.Factory,
        DefinitionVersion = t.DefinitionVersion,
        CheckPath = t.CheckPath,
        Secrets = SecretsOf(t),
        DueRunId = null,
        State = _store.CheckStates.StateFor(tenant, t.Factory, FactoryCheckStateStore.TriggerOwner(t.Id)),
    };

    /// <summary>The cc-secrets entry names a definition trigger's check declares, in declared order.</summary>
    internal static List<string> SecretsOf(TriggerEntity t)
        => string.IsNullOrEmpty(t.SecretsJson)
            ? new List<string>()
            : System.Text.Json.JsonSerializer.Deserialize<List<string>>(t.SecretsJson)
              ?? throw new InvalidOperationException($"trigger {t.Name} holds secrets that are not a JSON array: {t.SecretsJson}");

    /// <summary>
    /// Resume a trigger on the owner's word. When it was paused, this also releases the one-at-a-time lock (see
    /// <see cref="TriggerStore.Resume"/>) and records the release as one factory activity row: <c>allowed</c>, the
    /// caller as actor, and the session the lock had been waiting on. Taken under the same per-trigger lock as a check
    /// report, so a release never lands in the middle of a decision. Null when there is no such trigger.
    /// </summary>
    public async Task<TriggerEntity?> ResumeAsync(TenantId tenant, string triggerIdOrName, string releasedBy, CancellationToken ct)
    {
        FileLog.Write($"[TriggerService] ResumeAsync: tenant={tenant.ToLogString()}, trigger={triggerIdOrName}, by={releasedBy}");
        var found = _store.Find(tenant, triggerIdOrName);
        if (found is null) return null;

        var gate = GateFor(tenant, found.Id);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (trigger, released) = _store.Resume(tenant, found.Id.ToString("D"));
            if (trigger is null) return null;
            if (released is not null)
                _activity.Append(tenant, ReleaseActivityFor(trigger, released, releasedBy, _nowUtc()), callingActor: null);
            return trigger;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TriggerService] ResumeAsync FAILED: trigger={triggerIdOrName}, {ex.Message}");
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The factory activity row for a released lock: who released it, and which session it waited on.</summary>
    internal static AppendFactoryActivityRequest ReleaseActivityFor(TriggerEntity trigger, string releasedSessionId,
        string releasedBy, DateTime nowUtc) => new()
    {
        Factory = trigger.Factory,
        FactoryAgent = trigger.FactoryAgent,
        SessionId = releasedSessionId,
        What = Sentence($"{releasedBy} resumed {trigger.Name} and released its lock, which had been waiting on session " +
                        $"{releasedSessionId}; the next check that counts work starts a new session"),
        Outcome = FactoryActivityOutcome.Allowed,
        Subject = trigger.Name,
        Actor = releasedBy,
        OccurredUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc),
    };

    /// <summary>
    /// Read one check report, decide, and record the run - or, when there is work and nothing stands in the way, take
    /// the one-at-a-time lock and begin the session start WITHOUT waiting for it.
    ///
    /// A START NEVER RUNS ON THE REPORT'S REQUEST. A real session start takes 10 to 16 seconds and the Director gives
    /// its report 10 (found live on 2026-09-21: every start was cut off mid-create by the Director's own timeout, the
    /// row said failed, the lock was never set, and every interval started another session). So the lock is taken
    /// first (<see cref="TriggerStore.BeginStart"/>), the Director is answered at once, and the start runs on the
    /// Gateway's own lifetime, never <paramref name="ct"/>. When it returns it writes the check's one run row:
    /// <c>started</c> with the session id, or <c>failed</c>, which releases the lock and turns the trigger red. A check
    /// that arrives while the start is still running reads the lock and records <c>skipped-running</c>.
    /// The result's <see cref="TriggerReportResult.Starting"/> carries that start; the route does not wait on it.
    /// </summary>
    public async Task<TriggerReportResult> ReportCheckAsync(
        TenantId tenant, string directorId, string triggerIdOrName, TriggerCheckReport report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        FileLog.Write($"[TriggerService] ReportCheckAsync: tenant={tenant.ToLogString()}, director={directorId}, trigger={triggerIdOrName}");

        var found = _store.Find(tenant, triggerIdOrName);
        if (found is null)
            return TriggerReportResult.Refused(TriggerReportRefusal.NoSuchTrigger,
                $"no trigger '{triggerIdOrName}' in this account");
        if (found.IsDefinitionCheck && !_directorRunsDefinitionChecks(tenant, directorId))
            return TriggerReportResult.Refused(TriggerReportRefusal.CannotRunDefinitionChecks,
                $"trigger '{found.Name}' runs a check from the definition of {found.Factory}, and Director {directorId} has not said it runs those; this report was not recorded");

        var gate = GateFor(tenant, found.Id);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        PendingStart pending;
        try
        {
            // Read again under the lock: a report that waited here must see the lock the one before it took.
            var trigger = _store.Find(tenant, found.Id.ToString("D"));
            if (trigger is null)
                return TriggerReportResult.Refused(TriggerReportRefusal.NoSuchTrigger,
                    $"no trigger '{triggerIdOrName}' in this account");

            var now = _nowUtc();
            if (TriggerStore.IsHeldByAnother(trigger, directorId, now))
                return TriggerReportResult.Refused(TriggerReportRefusal.HeldByAnotherDirector,
                    $"Director {trigger.ClaimedByDirectorId} runs the check of trigger '{trigger.Name}'; this report was not recorded");

            // A lock with no session and no start running is a start whose outcome was not known: adopt its session
            // if the Director has reported it, or let the lock lapse once the grace is over - before deciding.
            trigger = ResolveUnknownStart(tenant, trigger, directorId, now);
            if (trigger is null)
                return TriggerReportResult.Refused(TriggerReportRefusal.NoSuchTrigger,
                    $"no trigger '{triggerIdOrName}' in this account");

            var decision = Decide(tenant, trigger, directorId, report, now);
            if (decision.Start is null)
            {
                var recorded = _store.RecordCheck(tenant, trigger.Id, directorId, report.CheckedAtUtc, decision.Outcome!,
                    decision.Count, decision.SessionId, decision.Reason, _nowUtc());
                if (recorded is null)
                    return TriggerReportResult.Refused(TriggerReportRefusal.NoSuchTrigger,
                        $"trigger '{trigger.Name}' was deleted while its check was being recorded");

                _activity.Append(tenant, ActivityFor(trigger, recorded.Run), callingActor: null);
                // Of the outcomes that start nothing, only nothing-to-do keeps the state: paused and skipped-running
                // counted work that no session took, so the next check must see that work again.
                if (decision.Outcome == TriggerRunOutcome.NothingToDo)
                    KeepState(tenant, trigger, directorId, report.State, recorded.Run.Id.ToString("D"));
                FileLog.Write($"[TriggerService] ReportCheckAsync: trigger={trigger.Name}, outcome={decision.Outcome}, count={decision.Count?.ToString(CultureInfo.InvariantCulture) ?? "none"}, session={decision.SessionId ?? "none"}, reason={decision.Reason ?? "none"}");
                return new TriggerReportResult(TriggerReportRefusal.None, recorded.Run, null);
            }

            if (!_store.BeginStart(tenant, trigger.Id, directorId, now, decision.Start.Name!))
                return TriggerReportResult.Refused(TriggerReportRefusal.NoSuchTrigger,
                    $"trigger '{trigger.Name}' was deleted while its check was being recorded");
            _inFlight[trigger.Id] = 0;
            // The run row of a check that begins a start is written when the start returns, and its state is kept
            // then, only when a session started (see FinishStartAsync) - so it travels with the start.
            pending = new PendingStart(tenant, trigger, directorId, report.CheckedAtUtc, decision.Count!.Value, decision.Start,
                report.State);
            FileLog.Write($"[TriggerService] ReportCheckAsync: trigger={trigger.Name}, count={pending.Count.ToString(CultureInfo.InvariantCulture)}, lock taken, starting session '{pending.Request.Name}' off the request");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TriggerService] ReportCheckAsync FAILED: trigger={triggerIdOrName}, {ex.Message}");
            throw;
        }
        finally
        {
            gate.Release();
        }

        // Begun after this report's hold on the per-trigger lock is released, and on the Gateway's lifetime - never
        // the request's - so the Director going away cannot cancel a start that is already under way.
        var starting = Task.Run(() => FinishStartAsync(pending), CancellationToken.None);
        return TriggerReportResult.StartingIn(starting, pending.Count);
    }

    private SemaphoreSlim GateFor(TenantId tenant, Guid triggerId)
        => _locks.GetOrAdd(KeyFor(tenant, triggerId), _ => new SemaphoreSlim(1, 1));

    private static string KeyFor(TenantId tenant, Guid triggerId) => $"{tenant.Value}/{triggerId:D}";

    /// <summary>True when nothing on this Gateway holds the trigger: its decision lock is free and no start of it is
    /// running. What a test reads to prove a failure inside a decision or a start left no lock behind.</summary>
    internal bool HoldsNoLock(TenantId tenant, Guid triggerId)
        => GateFor(tenant, triggerId).CurrentCount == 1 && !_inFlight.ContainsKey(triggerId);

    /// <summary>True while the lock is held by a start whose outcome is not known: a start time with no session, and
    /// no start of this trigger running on this Gateway.</summary>
    private bool IsHeldByUnknownStart(TriggerEntity t)
        => string.IsNullOrEmpty(t.LastSessionId) && t.LastStartedUtc is not null && !_inFlight.ContainsKey(t.Id);

    /// <summary>
    /// Settle a start whose outcome was not known, under the per-trigger lock. It applies only to a lock with no
    /// session while no start of this trigger runs here. The session is looked for by the exact name the start gave
    /// it, stored with the lock (<see cref="TriggerEntity.LastStartName"/>) - never worked out again from the trigger's
    /// name, factory agent or the account's time zone, which the owner can change meanwhile. Found, it is adopted - a
    /// new <c>started</c> row with its id makes it the lock; not found and the start grace is over, the lock lapses
    /// with a <c>failed</c> row saying so.
    /// Inside the grace with no session yet, nothing changes and the lock keeps holding. Returns the trigger as it now
    /// stands, or null when it no longer exists.
    /// </summary>
    private TriggerEntity? ResolveUnknownStart(TenantId tenant, TriggerEntity trigger, string directorId, DateTime now)
    {
        if (!IsHeldByUnknownStart(trigger))
            return trigger;

        var startedUtc = DateTime.SpecifyKind(trigger.LastStartedUtc!.Value, DateTimeKind.Utc);
        // Every lock BeginStart takes carries its name; a lock without one has no name to look for, and lapses.
        var name = trigger.LastStartName;
        var found = string.IsNullOrEmpty(name) ? null : _findSessionByName(tenant, name);
        TriggerCheckRecorded? recorded;
        string what;
        if (found is not null && !string.IsNullOrEmpty(found.SessionId))
        {
            recorded = _store.AdoptStart(tenant, trigger.Id, directorId, found.SessionId, now);
            what = $"Found session {found.SessionId}, named {name}, from the start of {trigger.Name} whose outcome was not known, and took it as the lock";
        }
        else if (now - startedUtc >= StartGrace)
        {
            var reason = TriggerStatusFold.StartFailedPrefix +
                         $"no session named '{name ?? "(no name was recorded)"}' showed up within {(int)StartGrace.TotalMinutes} minutes of a start whose outcome was not known; the lock is released";
            recorded = _store.LapseStart(tenant, trigger.Id, directorId, reason, now);
            what = $"{trigger.Name}: {reason}";
        }
        else
        {
            return trigger;
        }

        if (recorded is null) return null;
        _activity.Append(tenant, ActivityFor(trigger, recorded.Run, what), callingActor: null);
        FileLog.Write($"[TriggerService] ResolveUnknownStart: trigger={trigger.Name}, outcome={recorded.Run.Outcome}, session={recorded.Run.SessionId ?? "none"}, name={name}");
        return recorded.Trigger;
    }

    private sealed record PendingStart(TenantId Tenant, TriggerEntity Trigger, string DirectorId, DateTime CheckedUtc,
        int Count, NewSessionRequest Request, string? State);

    /// <summary>What a check came to: an outcome recorded now, or a session to start.</summary>
    private sealed record Decision(string? Outcome, int? Count, string? SessionId, string? Reason, NewSessionRequest? Start);

    /// <summary>
    /// A DEFINITION CHECK'S STATE IS KEPT ONLY WHEN THE WORK IT SAW WAS DEALT WITH (design 1.4): on nothing-to-do, and
    /// on a session that started. Never on a failed check - half-written state must never become the truth - and never
    /// on paused, skipped-running, or a start that failed or whose outcome is unknown: the work the check counted was
    /// not taken, so its next run must see it again. Called under the trigger's lock, so a later check's state can
    /// never be overwritten by an earlier one's. A command trigger has none; an absent state file writes nothing.
    /// </summary>
    private void KeepState(TenantId tenant, TriggerEntity trigger, string directorId, string? state, string runRef)
    {
        if (!trigger.IsDefinitionCheck || state is null) return;
        _store.CheckStates.RecordIfChanged(tenant, trigger.Factory, FactoryCheckStateStore.TriggerOwner(trigger.Id),
            state, directorId, runRef, _nowUtc());
    }

    /// <summary>
    /// Run one start begun by <see cref="ReportCheckAsync"/> and write the check's one run row. Nothing awaits this in
    /// production, so it is an entry point: every failure ends as a logged failed row, never an unobserved exception.
    /// Returns the row, or null when none could be written.
    /// </summary>
    private async Task<TriggerRunEntity?> FinishStartAsync(PendingStart p)
    {
        var name = p.Trigger.Name;
        TriggerStartAttempt attempt;
        try
        {
            attempt = await _startSession(p.Trigger.Machine, p.Request, _startLifetime).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TriggerService] FinishStartAsync: trigger={name}, the start threw {ex.GetType().Name}: {ex.Message}");
            // Cut off by the Gateway stopping, the create may already be on its way: that outcome is unknown too.
            attempt = ex is OperationCanceledException && _startLifetime.IsCancellationRequested
                ? TriggerStartAttempt.Unknown("the Gateway stopped while the session was being started.")
                : TriggerStartAttempt.Failed(ex.Message);
        }

        var key = p.Trigger.Id;
        try
        {
            var gate = GateFor(p.Tenant, p.Trigger.Id);
            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                var started = !string.IsNullOrEmpty(attempt.SessionId);
                var outcome = started ? TriggerRunOutcome.Started : TriggerRunOutcome.Failed;
                var error = attempt.Error ?? "the machine did not return a session";
                var reason = started ? null
                    : attempt.OutcomeUnknown
                        ? TriggerStatusFold.StartUnknownPrefix + error +
                          $" The lock is held until a session named '{p.Request.Name}' shows up, or for {(int)StartGrace.TotalMinutes} minutes."
                        : TriggerStatusFold.StartFailedPrefix + error;
                var recorded = _store.RecordStartResult(p.Tenant, p.Trigger.Id, p.DirectorId, p.CheckedUtc, outcome,
                    p.Count, started ? attempt.SessionId : null, reason, _nowUtc(), holdLock: attempt.OutcomeUnknown);
                if (recorded is null)
                {
                    FileLog.Write($"[TriggerService] FinishStartAsync: trigger={name} was deleted while its session was starting; session={attempt.SessionId ?? "none"} is not recorded");
                    return null;
                }

                _activity.Append(p.Tenant, ActivityFor(p.Trigger, recorded.Run), callingActor: null);
                if (started)
                    KeepState(p.Tenant, p.Trigger, p.DirectorId, p.State, recorded.Run.Id.ToString("D"));
                FileLog.Write($"[TriggerService] FinishStartAsync: trigger={name}, outcome={outcome}, outcomeUnknown={attempt.OutcomeUnknown}, count={p.Count.ToString(CultureInfo.InvariantCulture)}, session={attempt.SessionId ?? "none"}, reason={reason ?? "none"}");
                return recorded.Run;
            }
            finally
            {
                _inFlight.TryRemove(key, out _);
                gate.Release();
            }
        }
        catch (Exception ex)
        {
            _inFlight.TryRemove(key, out _);
            FileLog.Write($"[TriggerService] FinishStartAsync FAILED to record: trigger={name}, session={attempt.SessionId ?? "none"}, {ex.Message}");
            return null;
        }
    }

    private Decision Decide(TenantId tenant, TriggerEntity trigger, string directorId, TriggerCheckReport report, DateTime now)
    {
        var reading = TriggerCheckContract.Read(report, trigger.IsDefinitionCheck);
        if (reading.Failed)
            return new Decision(TriggerRunOutcome.Failed, null, null, reading.FailureReason, null);

        var count = reading.Count!.Value;
        if (count == 0)
            return new Decision(TriggerRunOutcome.NothingToDo, 0, null, null, null);

        if (trigger.Paused)
            return new Decision(TriggerRunOutcome.Paused, count, null, null, null);

        if (IsLastSessionAlive(tenant, trigger, now))
            return new Decision(TriggerRunOutcome.SkippedRunning, count, trigger.LastSessionId, null, null);

        var name = SessionName(trigger, _timeZone(tenant), now);
        if (trigger.IsDefinitionCheck)
        {
            // A definition trigger's prompt also carries what its check found (design 3.2).
            var prompt = TriggerDefinition.PromptFor(trigger.Prompt, count, reading.Context);
            var (defined, refusal) = DefinedStart(tenant, trigger, name, prompt, directorId);
            return refusal is null
                ? new Decision(null, count, null, null, defined)
                : new Decision(TriggerRunOutcome.Failed, count, null, TriggerStatusFold.StartFailedPrefix + refusal, null);
        }

        // A command trigger's create is built from its own row, exactly as it always was.
        return new Decision(null, count, null, null, new NewSessionRequest
        {
            RepoPath = trigger.RepoPath,
            Agent = "ClaudeCode",
            Name = name,
            PrePrompt = TriggerDefinition.PromptFor(trigger.Prompt, count),
            // The Director that ran the check is on the trigger's machine and was alive a moment ago; starting the
            // session there names ONE Director instead of leaving it to whichever the machine resolves to first.
            Director = directorId,
            // Like a schedule's seed run: a session that finishes with nothing needing a person closes itself,
            // which is also what ends the one-at-a-time lock.
            AutoDismiss = true,
            Origin = SessionOriginKinds.Schedule,
            OriginSurface = SessionOriginSurfaces.Trigger,
            // THE SESSION IS BORN INTO THE TRIGGER'S FACTORY (Factory Memory mission, phase 1). This is one of the
            // two ways membership begins - the other is a person naming a factory by hand - and it is the way the
            // Website Factory's Sender gets it. The Gateway sets it here from the trigger row rather than letting
            // the create body carry it, which is why naming a trigger's factory is itself limited to a person or a
            // session already in that factory: without that limit, writing a trigger would BE joining a factory.
            Factory = trigger.Factory,
        });
    }

    /// <summary>
    /// A DEFINITION TRIGGER'S SESSION IS A SEAT START (Factory Definition, phase 5b, brief D item 8): built by
    /// <see cref="FactorySeatStart.Build"/> from the factory's PUBLISHED version - the seat's agent, model, repository by
    /// name or its home, and a seat kit, never the trigger's folder, which a factory a publish registered does not have.
    /// The trigger's own settings are kept: the Director that ran the check, the prompt with what it counted, closing
    /// itself when done, and the trigger origin. A factory with no published version, one that cannot be read, or a
    /// version without the trigger's seat starts nothing, and the reason is returned instead.
    /// </summary>
    private (NewSessionRequest? Request, string? Refusal) DefinedStart(TenantId tenant, TriggerEntity trigger, string name,
        string prompt, string directorId)
    {
        PublishedFactoryDefinition? published;
        try
        {
            published = _definitionOf(tenant, trigger.Factory);
        }
        catch (InvalidOperationException ex)
        {
            FileLog.Write($"[TriggerService] DefinedStart REFUSED: trigger={trigger.Name}: {ex.Message}");
            return (null, ex.Message);
        }
        if (published is null)
        {
            var none = $"'{trigger.Factory}' has no published definition, so its trigger '{trigger.Name}' cannot start its seat. Publish the definition again.";
            FileLog.Write($"[TriggerService] DefinedStart REFUSED: trigger={trigger.Name}: {none}");
            return (null, none);
        }
        if (published.Seat(trigger.Seat) is not { } seat)
        {
            var notHeld = FactorySeatStart.SeatNotInVersion(published, trigger.Seat ?? "");
            FileLog.Write($"[TriggerService] DefinedStart REFUSED: trigger={trigger.Name}: {notHeld}");
            return (null, notHeld);
        }

        var request = FactorySeatStart.Build(published, seat, name, prompt).Request;
        request.Director = directorId;
        request.AutoDismiss = true;
        request.Origin = SessionOriginKinds.Schedule;
        request.OriginSurface = SessionOriginSurfaces.Trigger;
        FileLog.Write($"[TriggerService] DefinedStart: trigger={trigger.Name}, {trigger.Factory}/{seat.Seat} v{published.Version}, director={directorId}");
        return (request, null);
    }

    /// <summary>
    /// Is the session this trigger last started still alive? Alive when the Gateway's session list has it and it
    /// has not exited or crashed. A session the list does not have is alive only inside <see cref="StartGrace"/>
    /// of its start (no Director has reported it yet); after that, a session the Gateway no longer knows has ended.
    /// A start still in flight - a start time and no session yet (<see cref="TriggerStore.BeginStart"/>) - is alive
    /// inside the same grace: the start writes its session or releases the lock long before, and only a Gateway that
    /// stopped mid-start leaves one behind, which then lapses rather than holding for good.
    /// </summary>
    internal bool IsLastSessionAlive(TenantId tenant, TriggerEntity trigger, DateTime now)
    {
        if (string.IsNullOrEmpty(trigger.LastSessionId))
            return trigger.LastStartedUtc is { } pendingSince
                   && now - DateTime.SpecifyKind(pendingSince, DateTimeKind.Utc) < StartGrace;
        var row = _findSession(tenant, trigger.LastSessionId);
        if (row is not null) return !FleetManagerSessions.IsGone(row);
        return trigger.LastStartedUtc is { } started
               && now - DateTime.SpecifyKind(started, DateTimeKind.Utc) < StartGrace;
    }

    /// <summary>
    /// The factory activity row for one recorded check: who (the trigger), what it came to in the record's outcome
    /// words, and one plain sentence. The sentence is for a person to read; nothing groups or decides on it.
    /// </summary>
    internal static AppendFactoryActivityRequest ActivityFor(TriggerEntity trigger, TriggerRunEntity run, string? whatOverride = null)
    {
        var name = trigger.Name;
        var counted = run.Count is { } c ? $"counted {c.ToString(CultureInfo.InvariantCulture)}" : "counted nothing";
        var (outcome, what) = run.Outcome switch
        {
            TriggerRunOutcome.NothingToDo => (FactoryActivityOutcome.NothingToDo,
                $"Checked {name} - nothing to do"),
            TriggerRunOutcome.Started => (FactoryActivityOutcome.Started,
                $"Checked {name} - {counted}, started session {run.SessionId}"),
            TriggerRunOutcome.Paused => (FactoryActivityOutcome.Paused,
                $"Checked {name} - {counted}, but the trigger is paused, so nothing started"),
            TriggerRunOutcome.SkippedRunning => (FactoryActivityOutcome.Skipped, string.IsNullOrEmpty(run.SessionId)
                ? $"Checked {name} - {counted}, but its session is still being started, so nothing started"
                : $"Checked {name} - {counted}, but its last session {run.SessionId} is still running, so nothing started"),
            TriggerRunOutcome.Failed => (FactoryActivityOutcome.Failed, FailedSentence(name, run)),
            _ => throw new InvalidOperationException($"Trigger run outcome '{run.Outcome}' has no factory activity outcome."),
        };

        return new AppendFactoryActivityRequest
        {
            Factory = trigger.Factory,
            FactoryAgent = trigger.FactoryAgent,
            SessionId = run.SessionId,
            What = Sentence(whatOverride ?? what),
            Outcome = outcome,
            Subject = name,
            Actor = ActorPrefix + trigger.Id.ToString("D"),
            OccurredUtc = DateTime.SpecifyKind(run.CheckedUtc, DateTimeKind.Utc),
        };
    }

    private static string FailedSentence(string name, TriggerRunEntity run)
    {
        var reason = string.IsNullOrWhiteSpace(run.Reason) ? "no reason was recorded" : run.Reason;
        return reason.StartsWith(TriggerStatusFold.StartFailedPrefix, StringComparison.Ordinal)
               || reason.StartsWith(TriggerStatusFold.StartUnknownPrefix, StringComparison.Ordinal)
            ? $"Checked {name} - counted {run.Count?.ToString(CultureInfo.InvariantCulture) ?? "work"}, but {reason}"
            : $"Checked {name} - the check failed: {reason}";
    }

    /// <summary>The record takes at most <see cref="FactoryActivityRecord.MaxWhatChars"/>; a long failure reason is
    /// cut, and the full reason stays in the trigger's own run history.</summary>
    private static string Sentence(string what)
        => what.Length <= FactoryActivityRecord.MaxWhatChars ? what : what[..(FactoryActivityRecord.MaxWhatChars - 3)] + "...";

    /// <summary>"&lt;factory agent&gt; - &lt;trigger name&gt; - &lt;time&gt;", the time in the account's zone.</summary>
    public static string SessionName(TriggerEntity trigger, TimeZoneInfo zone, DateTime nowUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone);
        return $"{trigger.FactoryAgent} - {trigger.Name} - {local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}";
    }
}
