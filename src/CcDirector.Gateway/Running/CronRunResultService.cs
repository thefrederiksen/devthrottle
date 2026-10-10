using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;

namespace CcDirector.Gateway.Running;

/// <summary>What a report or a resolve came to: the answer, or the refusal and its status code.</summary>
public sealed record CronRunResultOutcome<T>(T? Answer, string? Refusal, int StatusCode) where T : class
{
    public static CronRunResultOutcome<T> Ok(T answer) => new(answer, null, 200);
    public static CronRunResultOutcome<T> Refused(string why, int statusCode) => new(null, why, statusCode);
}

/// <summary>
/// EVERY RUN SAYS HOW IT WENT (Factory Control, step 1 and step 6). Records the result a scheduled run's own session
/// reports, the problems the Gateway records by itself, and clearing a problem by hand; reads every problem through
/// the one fold, <see cref="CronRunResultFold.Problems"/>.
///
/// NO SILENT FACTORIES (step 6). Whenever a result is recorded on a run of a schedule linked to a factory, a factory
/// activity row is written for it - "done" for ok, "failed" for a problem - so a factory is never silent on its
/// dashboard whatever its prompts say. The row's subject is the schedule's name and its seat is the schedule's seat,
/// so the factory screen's own clearing rule clears a failed row when the next run of the same schedule reports ok.
/// Resolving a problem by hand writes the correcting row that marks its failed row handled.
///
/// The account is always EXPLICIT for the run history and the activity record. The schedules and the session work
/// history are read in the ambient scope, which the route and the cron sweep have already entered for that account.
/// </summary>
public sealed class CronRunResultService
{
    /// <summary>The actor a Gateway-recorded row names: the schedule, as the trigger service names its trigger.</summary>
    public const string ActorPrefix = "schedule:";

    /// <summary>Who resolved a problem when a person did it.</summary>
    public const string ResolvedByYou = "you";

    private readonly CronRunHistoryStore _runs;
    private readonly Func<string, CronJobDto?> _jobById;
    private readonly Func<IReadOnlyList<CronJobDto>> _allJobs;
    private readonly Func<IReadOnlyCollection<string>, IReadOnlyDictionary<string, SessionEndingFact>> _endingsOf;
    private readonly Func<TenantId, TimeZoneInfo> _zoneOf;
    private readonly Func<TenantId, AppendFactoryActivityRequest, Guid> _appendActivity;
    private readonly Func<DateTime> _nowUtc;

    /// <param name="runs">The run history.</param>
    /// <param name="jobById">A schedule of the ambient account by id, or null.</param>
    /// <param name="allJobs">Every schedule of the ambient account.</param>
    /// <param name="endingsOf">How the given sessions ended (<see cref="SessionHistoryStore.EndingsOf"/>).</param>
    /// <param name="zoneOf">The account's display time zone - the shifts are counted in it.</param>
    /// <param name="appendActivity">Append one factory activity row for the account; returns the row's id.</param>
    /// <param name="nowUtc">The clock.</param>
    public CronRunResultService(
        CronRunHistoryStore runs,
        Func<string, CronJobDto?> jobById,
        Func<IReadOnlyList<CronJobDto>> allJobs,
        Func<IReadOnlyCollection<string>, IReadOnlyDictionary<string, SessionEndingFact>> endingsOf,
        Func<TenantId, TimeZoneInfo> zoneOf,
        Func<TenantId, AppendFactoryActivityRequest, Guid> appendActivity,
        Func<DateTime> nowUtc)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _jobById = jobById ?? throw new ArgumentNullException(nameof(jobById));
        _allJobs = allJobs ?? throw new ArgumentNullException(nameof(allJobs));
        _endingsOf = endingsOf ?? throw new ArgumentNullException(nameof(endingsOf));
        _zoneOf = zoneOf ?? throw new ArgumentNullException(nameof(zoneOf));
        _appendActivity = appendActivity ?? throw new ArgumentNullException(nameof(appendActivity));
        _nowUtc = nowUtc ?? throw new ArgumentNullException(nameof(nowUtc));
    }

    // ---------------------------------------------------------------------------------------------------------
    // The run's own report: cc-devthrottle run result

    /// <summary>
    /// Record how the calling session's scheduled run went. A session no schedule started is refused, never answered
    /// with a silent success. After ok the answer tells the session to close itself; after a problem it stays open.
    /// </summary>
    public CronRunResultOutcome<CronRunResultResponse> Report(TenantId tenant, string? callingSessionId, string? result, string? reason)
    {
        FileLog.Write($"[CronRunResultService] Report: session={callingSessionId}, result={result}");
        if (string.IsNullOrWhiteSpace(callingSessionId))
            return CronRunResultOutcome<CronRunResultResponse>.Refused(
                "Only a scheduled run's own session can report its result, and this request carries no session key. Run it from inside the run's session.",
                403);

        var wanted = (result ?? "").Trim().ToLowerInvariant();
        if (wanted is not (CronRunResults.Ok or CronRunResults.Problem))
            return CronRunResultOutcome<CronRunResultResponse>.Refused(
                $"'{result}' is not a result. Report 'ok', or 'problem' with a one-line reason.", 400);

        var refusal = CronRunResultFold.CheckReason(reason, required: wanted == CronRunResults.Problem,
            wanted == CronRunResults.Problem ? "A problem" : "An ok result");
        if (refusal is not null)
            return CronRunResultOutcome<CronRunResultResponse>.Refused(refusal, 400);

        var found = _runs.FindBySession(tenant, callingSessionId);
        if (found is null)
        {
            FileLog.Write($"[CronRunResultService] Report REFUSED: session {callingSessionId} is not a scheduled run");
            return CronRunResultOutcome<CronRunResultResponse>.Refused(
                $"Session {callingSessionId} was not started by a schedule, so it has no run to report on. Only a scheduled run's session reports its result. Nothing was recorded.",
                404);
        }

        var job = _jobById(found.JobId);
        var now = _nowUtc();
        var line = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        var run = found.Run;
        CronRunRecord stored;
        string text;
        var name = job?.Name ?? found.JobId;

        if (run.Result == CronRunResults.Problem && run.Problem == CronRunProblems.RanPastShift)
        {
            // The Gateway already recorded that this run went past its shift. That stays true whatever the run says
            // now, so the report is added to the problem rather than replacing it.
            var said = wanted == CronRunResults.Ok ? "ok" : "a problem";
            var combined = Truncate($"{run.ResultReason}; it then reported {said}{(line is null ? "" : ": " + line)}");
            stored = _runs.RecordResult(tenant, Guid.Parse(run.RunId), CronRunResults.Problem, CronRunProblems.RanPastShift, combined, now)
                ?? throw new InvalidOperationException($"run {run.RunId} vanished while its result was recorded");
            text = $"Recorded your report on the run of {name}. It stays a problem: it ran past its shift before it reported.";
        }
        else if (wanted == CronRunResults.Ok)
        {
            stored = _runs.RecordResult(tenant, Guid.Parse(run.RunId), CronRunResults.Ok, null, line, now)
                ?? throw new InvalidOperationException($"run {run.RunId} vanished while its result was recorded");
            text = $"Recorded: the run of {name} went ok. This session now closes itself.";
        }
        else
        {
            stored = _runs.RecordResult(tenant, Guid.Parse(run.RunId), CronRunResults.Problem, CronRunProblems.Reported, line, now)
                ?? throw new InvalidOperationException($"run {run.RunId} vanished while its result was recorded");
            text = $"Recorded the problem on the run of {name}. This session stays open for someone to look at.";
        }

        WriteActivity(tenant, job, stored);
        return CronRunResultOutcome<CronRunResultResponse>.Ok(new CronRunResultResponse
        {
            Run = stored,
            JobId = found.JobId,
            JobName = name,
            CloseSession = wanted == CronRunResults.Ok,
            Text = text,
        });
    }

    // ---------------------------------------------------------------------------------------------------------
    // What the Gateway records by itself

    /// <summary>
    /// Record "did not report" and "ran past its shift" on every run of the account still waiting on its report that
    /// has earned one. Called on the cron sweep, inside the account's scope. Returns how many problems it recorded.
    /// </summary>
    public int Sweep(TenantId tenant)
    {
        var pending = _runs.PendingRuns(tenant);
        if (pending.Count == 0) return 0;

        var now = _nowUtc();
        var zone = _zoneOf(tenant);
        var sessionIds = pending.Select(p => p.Run.SessionId).Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!).Distinct(StringComparer.Ordinal).ToList();
        var endings = _endingsOf(sessionIds);
        var recorded = 0;
        foreach (var p in pending)
        {
            var (ending, endedUtc) = CronRunEndingFold.EndingOf(p.Run, endings);
            var shift = WorkShifts.ShiftOf(CronRunResultFold.ShiftInstantOf(p.Run), zone);
            var problem = CronRunResultFold.GatewayProblemFor(p.Run, ending,
                CronRunEndingFold.TextOf(ending, p.Run.FiredUtc, endedUtc, now), shift, now);
            if (problem is null) continue;

            var stored = _runs.RecordResult(tenant, Guid.Parse(p.Run.RunId), CronRunResults.Problem, problem.Kind, problem.Reason, now)
                ?? throw new InvalidOperationException($"run {p.Run.RunId} vanished while its problem was recorded");
            FileLog.Write($"[CronRunResultService] Sweep: run={p.Run.RunId}, job={p.JobId}, problem={problem.Kind}");
            WriteActivity(tenant, _jobById(p.JobId), stored);
            recorded++;
        }
        if (recorded > 0)
            FileLog.Write($"[CronRunResultService] Sweep: tenant={tenant.ToLogString()}, pending={pending.Count}, recorded={recorded}");
        return recorded;
    }

    /// <summary>
    /// The engine recorded a run that already carries its result - a fire that never started a session, a due run
    /// that was skipped. Write its factory activity row.
    /// </summary>
    public void Announce(TenantId tenant, CronJobDto job, CronRunRecord run)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(run);
        if (run.Result is not (CronRunResults.Ok or CronRunResults.Problem)) return;
        WriteActivity(tenant, job, run);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Clearing by hand: cc-devthrottle run resolve

    /// <summary>
    /// Resolve a run's problem with a one-line reason, keeping the reason and who resolved it. An empty reason is
    /// refused. <paramref name="resolvedBy"/> is "you" for a person, otherwise the session that resolved it.
    /// </summary>
    public CronRunResultOutcome<CronRunProblemDto> Resolve(TenantId tenant, string? runId, string resolvedBy, string? reason)
    {
        FileLog.Write($"[CronRunResultService] Resolve: run={runId}, by={resolvedBy}");
        var refusal = CronRunResultFold.CheckReason(reason, required: true, "Resolving a problem");
        if (refusal is not null)
            return CronRunResultOutcome<CronRunProblemDto>.Refused(refusal, 400);
        if (!Guid.TryParse((runId ?? "").Trim(), out var id))
            return CronRunResultOutcome<CronRunProblemDto>.Refused(
                $"'{runId}' is not a run id. Copy the run id from cc-devthrottle run problems.", 400);

        var found = _runs.Find(tenant, id);
        if (found is null)
            return CronRunResultOutcome<CronRunProblemDto>.Refused(
                $"There is no run {id} in this account. Copy the run id from cc-devthrottle run problems.", 404);
        if (found.Run.Result != CronRunResults.Problem)
            return CronRunResultOutcome<CronRunProblemDto>.Refused(
                $"Run {id} has no problem to resolve: its result is '{found.Run.Result}'.", 409);

        var current = ProblemOf(tenant, found.JobId, id);
        if (current.State == CronRunProblemStates.Resolved)
            return CronRunResultOutcome<CronRunProblemDto>.Refused(
                $"Run {id}'s problem is already resolved - {current.StateText}.", 409);
        if (current.State == CronRunProblemStates.Cleared)
            return CronRunResultOutcome<CronRunProblemDto>.Refused(
                $"Run {id}'s problem is already cleared: run {current.ClearedByRunId}, the next run of the same schedule, reported ok.", 409);

        var line = reason!.Trim();
        _ = _runs.Resolve(tenant, id, resolvedBy, line, _nowUtc())
            ?? throw new InvalidOperationException($"run {id} vanished while it was resolved");

        var job = _jobById(found.JobId);
        if (found.ProblemActivityId is { } failedRow && IsFactoryLinked(job))
            AppendActivitySafely(tenant, new AppendFactoryActivityRequest
            {
                Factory = job!.Factory,
                FactoryAgent = job.Seat,
                What = Truncate($"Resolved by {resolvedBy}: {line}"),
                Outcome = FactoryActivityOutcome.Done,
                Subject = job.Name,
                Actor = resolvedBy,
                CorrectsId = failedRow,
                OccurredUtc = _nowUtc(),
            }, $"resolve of run {id}");

        return CronRunResultOutcome<CronRunProblemDto>.Ok(ProblemOf(tenant, found.JobId, id));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Reading: the one fold

    /// <summary>The account's problems, through <see cref="CronRunResultFold.Problems"/>.</summary>
    public CronRunProblemsDto Problems(TenantId tenant, string? factory, bool includeClosed)
    {
        var jobs = _allJobs().ToDictionary(j => j.Id, StringComparer.Ordinal);
        var dto = CronRunResultFold.Problems(_runs.AllByJob(tenant), jobs, _zoneOf(tenant), factory, includeClosed);
        FileLog.Write($"[CronRunResultService] Problems: tenant={tenant.ToLogString()}, factory={factory ?? "-"}, state={dto.State}, count={dto.Count}");
        return dto;
    }

    private CronRunProblemDto ProblemOf(TenantId tenant, string jobId, Guid runId)
    {
        var all = _runs.AllByJob(tenant);
        var runs = all.TryGetValue(jobId, out var r) ? r : Array.Empty<CronRunRecord>();
        var job = _jobById(jobId);
        var jobs = job is null
            ? new Dictionary<string, CronJobDto>(StringComparer.Ordinal)
            : new Dictionary<string, CronJobDto>(StringComparer.Ordinal) { [jobId] = job };
        var folded = CronRunResultFold.Problems(new Dictionary<string, IReadOnlyList<CronRunRecord>>(StringComparer.Ordinal) { [jobId] = runs },
            jobs, _zoneOf(tenant), factory: null, includeClosed: true);
        return folded.Problems.Single(p => p.RunId == runId.ToString("D"));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Step 6: no silent factories

    private static bool IsFactoryLinked(CronJobDto? job) =>
        job is not null && !string.IsNullOrWhiteSpace(job.Factory) && !string.IsNullOrWhiteSpace(job.Seat);

    /// <summary>The factory activity row for a run's result, or null for a schedule in no factory.</summary>
    public static AppendFactoryActivityRequest? ActivityFor(CronJobDto? job, CronRunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!IsFactoryLinked(job)) return null;
        var ok = run.Result == CronRunResults.Ok;
        var what = ok
            ? $"The scheduled run of {job!.Name} reported ok" + (string.IsNullOrWhiteSpace(run.ResultReason) ? "." : $": {run.ResultReason}")
            : $"The scheduled run of {job!.Name} {CronRunProblems.Words(run.Problem)}: {run.ResultReason}";
        return new AppendFactoryActivityRequest
        {
            Factory = job.Factory,
            FactoryAgent = job.Seat,
            SessionId = string.IsNullOrWhiteSpace(run.SessionId) ? null : run.SessionId,
            What = Truncate(what),
            Outcome = ok ? FactoryActivityOutcome.Done : FactoryActivityOutcome.Failed,
            Subject = job.Name,
            Actor = ActorPrefix + job.Id,
            OccurredUtc = run.ResultUtc ?? run.FiredUtc,
        };
    }

    private void WriteActivity(TenantId tenant, CronJobDto? job, CronRunRecord run)
    {
        var row = ActivityFor(job, run);
        if (row is null) return;
        var id = AppendActivitySafely(tenant, row, $"result of run {run.RunId}");
        if (id is { } rowId && run.Result == CronRunResults.Problem)
            _runs.SetProblemActivity(tenant, Guid.Parse(run.RunId), rowId);
    }

    // The result is already recorded when its activity row is written, and it is the record of truth: a refused or
    // failed row is LOGGED rather than failing the report, so a run is never told its report failed when it did not.
    private Guid? AppendActivitySafely(TenantId tenant, AppendFactoryActivityRequest row, string what)
    {
        try
        {
            var id = _appendActivity(tenant, row);
            FileLog.Write($"[CronRunResultService] activity row written: {what}, factory={row.Factory}, outcome={row.Outcome}, row={id}");
            return id;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CronRunResultService] activity row FAILED: {what}, factory={row.Factory}: {ex.Message}");
            return null;
        }
    }

    private static string Truncate(string text) =>
        text.Length <= CronRunResultFold.MaxReasonChars ? text : text[..(CronRunResultFold.MaxReasonChars - 3)] + "...";
}
