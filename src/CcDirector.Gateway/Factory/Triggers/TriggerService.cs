using System.Collections.Concurrent;
using System.Globalization;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
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
}

/// <summary>The answer to one check report: the run row it wrote, or why it wrote none.</summary>
public sealed record TriggerReportResult(TriggerReportRefusal Refusal, TriggerRunEntity? Run, string? Error)
{
    public static TriggerReportResult Refused(TriggerReportRefusal refusal, string error) => new(refusal, null, error);
}

/// <summary>
/// Starts a session for a trigger on its machine - production wraps <c>MachineSessionSpawner.SpawnOnMachineAsync</c>,
/// the same method schedules use. Returns the new session's id, or the reason none started.
/// </summary>
public delegate Task<(string? sessionId, string? error)> TriggerSessionStarter(
    string machine, NewSessionRequest request, CancellationToken ct);

/// <summary>
/// THE GATEWAY DECIDES AND STARTS (the Website Business Factory mission, product track). A Director runs a
/// trigger's check and reports what the process produced; this reads it under the check contract, decides, starts
/// the session when there is work, and records one run row whatever happened:
///
///  - the check broke its contract                          -> <c>failed</c>, with the reason
///  - it counted nothing                                     -> <c>nothing-to-do</c>
///  - it counted work, and the trigger is paused             -> <c>paused</c>
///  - it counted work, and this trigger's last session lives -> <c>skipped-running</c>
///  - it counted work, and nothing stands in the way         -> start one session; <c>started</c> with its id,
///                                                              or <c>failed</c> when it could not be started
///
/// EVERY CHECK IS ALSO A FACTORY ACTIVITY ROW. Beside its own run history, each recorded check appends one row to
/// the append-only factory activity record: the trigger's factory and factory agent, the actor
/// <c>trigger:&lt;trigger id&gt;</c>, the outcome in the record's words (<c>skipped-running</c> is the record's
/// <c>skipped</c>), and one plain sentence. The Cockpit groups those rows by outcome and actor, never by the sentence.
///
/// ONE AT A TIME, UNTIL THE SESSION HAS ENDED. The schedule's overlap guard is held only while a session is being
/// STARTED. This lock is the started session itself: while the session this trigger last started is still alive in
/// the Gateway's session list, no second one starts. Deciding and starting also happen under a per-trigger lock, so
/// two reports arriving together cannot both start one.
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
    private readonly Func<TenantId, TimeZoneInfo> _timeZone;
    private readonly Func<DateTime> _nowUtc;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    /// <param name="findSession">The last row any Director of the account reported for a session, or null when
    /// the Gateway knows no such session.</param>
    /// <param name="timeZone">The account's time zone, for the time in a started session's name.</param>
    public TriggerService(TriggerStore store, FactoryActivityRecord activity, TriggerSessionStarter startSession,
        Func<TenantId, string, SessionDto?> findSession, Func<TenantId, TimeZoneInfo> timeZone, Func<DateTime> nowUtc)
    {
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
            t.LastSessionId, t.LastStartedUtc is { } s ? DateTime.SpecifyKind(s, DateTimeKind.Utc) : null, _nowUtc());
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

    /// <summary>The checks this Director must run: the triggers on its machine it holds, or takes over.</summary>
    public IReadOnlyList<TriggerAssignmentDto> AssignmentsFor(TenantId tenant, string directorId, string machine)
    {
        FileLog.Write($"[TriggerService] AssignmentsFor: tenant={tenant.ToLogString()}, director={directorId}, machine={machine}");
        return _store.ClaimForDirector(tenant, directorId, machine, _nowUtc())
            .Select(t => new TriggerAssignmentDto
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

        var gate = _locks.GetOrAdd($"{tenant.Value}/{found.Id:D}", _ => new SemaphoreSlim(1, 1));
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

    /// <summary>Read one check report, decide, start a session when there is work, and record the run.</summary>
    public async Task<TriggerReportResult> ReportCheckAsync(
        TenantId tenant, string directorId, string triggerIdOrName, TriggerCheckReport report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        FileLog.Write($"[TriggerService] ReportCheckAsync: tenant={tenant.ToLogString()}, director={directorId}, trigger={triggerIdOrName}");

        var found = _store.Find(tenant, triggerIdOrName);
        if (found is null)
            return TriggerReportResult.Refused(TriggerReportRefusal.NoSuchTrigger,
                $"no trigger '{triggerIdOrName}' in this account");

        var gate = _locks.GetOrAdd($"{tenant.Value}/{found.Id:D}", _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Read again under the lock: a report that waited here must see the session the one before it started.
            var trigger = _store.Find(tenant, found.Id.ToString("D"));
            if (trigger is null)
                return TriggerReportResult.Refused(TriggerReportRefusal.NoSuchTrigger,
                    $"no trigger '{triggerIdOrName}' in this account");

            var now = _nowUtc();
            if (TriggerStore.IsHeldByAnother(trigger, directorId, now))
                return TriggerReportResult.Refused(TriggerReportRefusal.HeldByAnotherDirector,
                    $"Director {trigger.ClaimedByDirectorId} runs the check of trigger '{trigger.Name}'; this report was not recorded");

            var (outcome, count, sessionId, reason) = await DecideAsync(tenant, trigger, directorId, report, now, ct)
                .ConfigureAwait(false);

            var recorded = _store.RecordCheck(tenant, trigger.Id, directorId, report.CheckedAtUtc, outcome, count,
                sessionId, reason, _nowUtc());
            if (recorded is null)
                return TriggerReportResult.Refused(TriggerReportRefusal.NoSuchTrigger,
                    $"trigger '{trigger.Name}' was deleted while its check was being recorded");

            _activity.Append(tenant, ActivityFor(trigger, recorded.Run), callingActor: null);

            FileLog.Write($"[TriggerService] ReportCheckAsync: trigger={trigger.Name}, outcome={outcome}, count={count?.ToString(CultureInfo.InvariantCulture) ?? "none"}, session={sessionId ?? "none"}, reason={reason ?? "none"}");
            return new TriggerReportResult(TriggerReportRefusal.None, recorded.Run, null);
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
    }

    private async Task<(string outcome, int? count, string? sessionId, string? reason)> DecideAsync(
        TenantId tenant, TriggerEntity trigger, string directorId, TriggerCheckReport report, DateTime now,
        CancellationToken ct)
    {
        var reading = TriggerCheckContract.Read(report);
        if (reading.Failed)
            return (TriggerRunOutcome.Failed, null, null, reading.FailureReason);

        var count = reading.Count!.Value;
        if (count == 0)
            return (TriggerRunOutcome.NothingToDo, 0, null, null);

        if (trigger.Paused)
            return (TriggerRunOutcome.Paused, count, null, null);

        if (IsLastSessionAlive(tenant, trigger, now))
            return (TriggerRunOutcome.SkippedRunning, count, trigger.LastSessionId, null);

        var request = new NewSessionRequest
        {
            RepoPath = trigger.RepoPath,
            Agent = "ClaudeCode",
            Name = SessionName(trigger, _timeZone(tenant), now),
            PrePrompt = TriggerDefinition.PromptFor(trigger.Prompt, count),
            // The Director that ran the check is on the trigger's machine and was alive a moment ago; starting the
            // session there names ONE Director instead of leaving it to whichever the machine resolves to first.
            Director = directorId,
            // Like a schedule's seed run: a session that finishes with nothing needing a person closes itself,
            // which is also what ends the one-at-a-time lock.
            AutoDismiss = true,
            Origin = SessionOriginKinds.Schedule,
            OriginSurface = SessionOriginSurfaces.Trigger,
        };

        var (sessionId, error) = await _startSession(trigger.Machine, request, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(sessionId))
            return (TriggerRunOutcome.Failed, count, null,
                TriggerStatusFold.StartFailedPrefix + (error ?? "the machine did not return a session"));

        return (TriggerRunOutcome.Started, count, sessionId, null);
    }

    /// <summary>
    /// Is the session this trigger last started still alive? Alive when the Gateway's session list has it and it
    /// has not exited or crashed. A session the list does not have is alive only inside <see cref="StartGrace"/>
    /// of its start (no Director has reported it yet); after that, a session the Gateway no longer knows has ended.
    /// </summary>
    internal bool IsLastSessionAlive(TenantId tenant, TriggerEntity trigger, DateTime now)
    {
        if (string.IsNullOrEmpty(trigger.LastSessionId)) return false;
        var row = _findSession(tenant, trigger.LastSessionId);
        if (row is not null) return !FleetManagerSessions.IsGone(row);
        return trigger.LastStartedUtc is { } started
               && now - DateTime.SpecifyKind(started, DateTimeKind.Utc) < StartGrace;
    }

    /// <summary>
    /// The factory activity row for one recorded check: who (the trigger), what it came to in the record's outcome
    /// words, and one plain sentence. The sentence is for a person to read; nothing groups or decides on it.
    /// </summary>
    internal static AppendFactoryActivityRequest ActivityFor(TriggerEntity trigger, TriggerRunEntity run)
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
            TriggerRunOutcome.SkippedRunning => (FactoryActivityOutcome.Skipped,
                $"Checked {name} - {counted}, but its last session {run.SessionId} is still running, so nothing started"),
            TriggerRunOutcome.Failed => (FactoryActivityOutcome.Failed, FailedSentence(name, run)),
            _ => throw new InvalidOperationException($"Trigger run outcome '{run.Outcome}' has no factory activity outcome."),
        };

        return new AppendFactoryActivityRequest
        {
            Factory = trigger.Factory,
            FactoryAgent = trigger.FactoryAgent,
            SessionId = run.SessionId,
            What = Sentence(what),
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
