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

    private readonly TriggerStore _store;
    private readonly TriggerSessionStarter _startSession;
    private readonly Func<TenantId, string, SessionDto?> _findSession;
    private readonly Func<TenantId, TimeZoneInfo> _timeZone;
    private readonly Func<DateTime> _nowUtc;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    /// <param name="findSession">The last row any Director of the account reported for a session, or null when
    /// the Gateway knows no such session.</param>
    /// <param name="timeZone">The account's time zone, for the time in a started session's name.</param>
    public TriggerService(TriggerStore store, TriggerSessionStarter startSession,
        Func<TenantId, string, SessionDto?> findSession, Func<TenantId, TimeZoneInfo> timeZone, Func<DateTime> nowUtc)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _startSession = startSession ?? throw new ArgumentNullException(nameof(startSession));
        _findSession = findSession ?? throw new ArgumentNullException(nameof(findSession));
        _timeZone = timeZone ?? throw new ArgumentNullException(nameof(timeZone));
        _nowUtc = nowUtc ?? throw new ArgumentNullException(nameof(nowUtc));
    }

    public TriggerStore Store => _store;

    /// <summary>A trigger as the routes serve it, with its status decided now.</summary>
    public TriggerDto ToDto(TriggerEntity t)
    {
        var status = TriggerStatusFold.For(t.CreatedUtc, t.IntervalSeconds, t.LastCheckUtc, t.LastOutcome, t.LastReason, _nowUtc());
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

    /// <summary>"&lt;factory agent&gt; - &lt;trigger name&gt; - &lt;time&gt;", the time in the account's zone.</summary>
    public static string SessionName(TriggerEntity trigger, TimeZoneInfo zone, DateTime nowUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone);
        return $"{trigger.FactoryAgent} - {trigger.Name} - {local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}";
    }
}
