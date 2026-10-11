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
    private readonly Func<TenantId, Guid, AppendFactoryActivityRequest, bool, Guid> _writeActivity;
    private readonly Func<DateTime> _nowUtc;

    /// <param name="runs">The run history.</param>
    /// <param name="jobById">A schedule of the ambient account by id, or null.</param>
    /// <param name="allJobs">Every schedule of the ambient account.</param>
    /// <param name="endingsOf">How the given sessions ended (<see cref="SessionHistoryStore.EndingsOf"/>).</param>
    /// <param name="zoneOf">The account's display time zone - the shifts are counted in it.</param>
    /// <param name="writeActivity">Write one factory activity row a run owes - (account, run, row, is it the correcting
    /// row) - in the same write that notes it on the run (<see cref="CronRunHistoryStore.WriteActivityRow"/>); returns
    /// the row's id.</param>
    /// <param name="nowUtc">The clock.</param>
    public CronRunResultService(
        CronRunHistoryStore runs,
        Func<string, CronJobDto?> jobById,
        Func<IReadOnlyList<CronJobDto>> allJobs,
        Func<IReadOnlyCollection<string>, IReadOnlyDictionary<string, SessionEndingFact>> endingsOf,
        Func<TenantId, TimeZoneInfo> zoneOf,
        Func<TenantId, Guid, AppendFactoryActivityRequest, bool, Guid> writeActivity,
        Func<DateTime> nowUtc)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _jobById = jobById ?? throw new ArgumentNullException(nameof(jobById));
        _allJobs = allJobs ?? throw new ArgumentNullException(nameof(allJobs));
        _endingsOf = endingsOf ?? throw new ArgumentNullException(nameof(endingsOf));
        _zoneOf = zoneOf ?? throw new ArgumentNullException(nameof(zoneOf));
        _writeActivity = writeActivity ?? throw new ArgumentNullException(nameof(writeActivity));
        _nowUtc = nowUtc ?? throw new ArgumentNullException(nameof(nowUtc));
    }

    // ---------------------------------------------------------------------------------------------------------
    // The run's own report: cc-devthrottle run result

    /// <summary>What a session's own report adds to a problem the Gateway already recorded on its run.</summary>
    public const string SessionReportMarker = "; the session then reported ";

    /// <summary>
    /// Record how the calling session's scheduled run went. A session no schedule started is refused, never answered
    /// with a silent success.
    ///
    /// A RESULT IS RECORDED ONCE. Sending the same result again answers with the stored one and changes nothing, so a
    /// retried command is harmless; a DIFFERENT second result is refused - a run cannot report a problem and then erase
    /// it with ok (only the next run of the schedule, or a resolve, clears a problem). The one exception is a run the
    /// Gateway already found past its shift: the session's late report is added to that problem, once, and the run
    /// stays a problem. Whether the session closes is read off the STORED result - only an ok run closes.
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
        var name = job?.Name ?? found.JobId;
        var line = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        var runId = Guid.Parse(found.Run.RunId);
        var run = found.Run;

        // At most two passes: a conditional write that finds the run moved on (the sweep recorded it in between) is
        // decided once more against what it found.
        for (var pass = 0; pass < 2; pass++)
        {
            var decision = Decide(run, wanted, line, PastItsShift(tenant, run));
            if (decision.Refusal is not null)
            {
                FileLog.Write($"[CronRunResultService] Report REFUSED: run={runId}: {decision.Refusal}");
                return CronRunResultOutcome<CronRunResultResponse>.Refused(decision.Refusal, 409);
            }
            if (decision.Write is not { } w)
                return Answer(found.JobId, name, run, alreadyRecorded: true);

            var written = _runs.RecordResult(tenant, runId, run.Result, w.ExpectedReason, w.Result, w.Problem, w.Reason, _nowUtc());
            if (written.Run is null)
                throw new InvalidOperationException($"run {runId} vanished while its result was recorded");
            if (written.Applied)
            {
                TryWriteOwedActivity(tenant, runId);
                return Answer(found.JobId, name, written.Run, alreadyRecorded: false);
            }
            run = written.Run;
        }
        throw new InvalidOperationException($"run {runId} changed twice while its result was being recorded");
    }

    private sealed record ResultChange(string Result, string? Problem, string? Reason, string? ExpectedReason);

    private sealed record Decision(ResultChange? Write, string? Refusal);

    // The problem a pending run has earned by now if its shift is over: a session reporting is still running, so a run
    // whose shift has ended ran past it - whether or not the sweep has got there yet. Null while the shift lasts.
    private GatewayRecordedProblem? PastItsShift(TenantId tenant, CronRunRecord run)
    {
        if (run.Result != CronRunResults.Pending) return null;
        var shift = WorkShifts.ShiftOf(CronRunResultFold.ShiftInstantOf(run), _zoneOf(tenant));
        return CronRunResultFold.GatewayProblemFor(run, CronRunEndings.StillOpen, "", shift, _nowUtc());
    }

    private static string ReportWords(string wanted, string? line) =>
        (wanted == CronRunResults.Ok ? "ok" : "a problem") + (line is null ? "" : ": " + line);

    // What a session's report does to a run in the state it is in: write a change, answer with what is stored, or refuse.
    private static Decision Decide(CronRunRecord run, string wanted, string? line, GatewayRecordedProblem? pastShift)
    {
        // A report that comes after the run's shift ended, before the sweep recorded it: the run ran past its shift, and
        // that is what is recorded, with the report added - the same as when the sweep got there first.
        if (pastShift is not null)
            return new Decision(new ResultChange(CronRunResults.Problem, pastShift.Kind,
                Truncate($"{pastShift.Reason}{SessionReportMarker}{ReportWords(wanted, line)}"), null), null);

        if (run.Result is CronRunResults.Pending or CronRunResults.Untracked)
            return new Decision(wanted == CronRunResults.Ok
                ? new ResultChange(CronRunResults.Ok, null, line, null)
                : new ResultChange(CronRunResults.Problem, CronRunProblems.Reported, line, null), null);

        if (run.Result == CronRunResults.Problem && run.Problem == CronRunProblems.RanPastShift
            && !(run.ResultReason ?? "").Contains(SessionReportMarker, StringComparison.Ordinal))
        {
            // The Gateway already recorded that this run went past its shift. That stays true whatever the run says now,
            // so the report is added to the problem, once, rather than replacing it.
            var combined = Truncate($"{run.ResultReason}{SessionReportMarker}{ReportWords(wanted, line)}");
            return new Decision(new ResultChange(CronRunResults.Problem, CronRunProblems.RanPastShift, combined, run.ResultReason ?? ""), null);
        }

        var same = wanted == CronRunResults.Ok
            ? run.Result == CronRunResults.Ok
            : run.Result == CronRunResults.Problem && run.Problem == CronRunProblems.Reported && run.ResultReason == line;
        if (same)
            return new Decision(null, null);

        var stored = run.Result == CronRunResults.Ok
            ? "ok"
            : $"{CronRunProblems.Words(run.Problem)}: {run.ResultReason}";
        return new Decision(null,
            $"Run {run.RunId} already has its result ({stored}), and a result is recorded once. Nothing was changed. " +
            "A problem clears when the next run of the schedule reports ok, or with cc-devthrottle run resolve.");
    }

    private static CronRunResultOutcome<CronRunResultResponse> Answer(string jobId, string name, CronRunRecord stored, bool alreadyRecorded)
    {
        var ok = stored.Result == CronRunResults.Ok;
        var text = (ok, alreadyRecorded) switch
        {
            (true, false) => $"Recorded: the run of {name} went ok. This session now closes itself.",
            (true, true) => $"The run of {name} is already recorded as ok. This session now closes itself.",
            (false, true) => $"The problem on the run of {name} is already recorded. This session stays open for someone to look at.",
            _ when stored.Problem == CronRunProblems.RanPastShift =>
                $"Recorded your report on the run of {name}. It stays a problem - it ran past its shift - so this session stays open for someone to look at.",
            _ => $"Recorded the problem on the run of {name}. This session stays open for someone to look at.",
        };
        return CronRunResultOutcome<CronRunResultResponse>.Ok(new CronRunResultResponse
        {
            Run = stored,
            JobId = jobId,
            JobName = name,
            // Read off what is STORED, never the word asked for: only a run whose result is ok closes.
            CloseSession = ok,
            Text = text,
        });
    }

    // ---------------------------------------------------------------------------------------------------------
    // What the Gateway records by itself

    /// <summary>
    /// Record "did not report" and "ran past its shift" on every run of the account still waiting on its report that
    /// has earned one, then write every factory activity row the account's runs still owe. Called on the cron sweep,
    /// inside the account's scope. Returns how many problems it recorded.
    /// </summary>
    public int Sweep(TenantId tenant)
    {
        var recorded = RecordGatewayProblems(tenant);
        WriteAllOwedActivity(tenant);
        return recorded;
    }

    private int RecordGatewayProblems(TenantId tenant)
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

            // Only while it is STILL pending: a session that reported in the meantime keeps its own result.
            var written = _runs.RecordResult(tenant, Guid.Parse(p.Run.RunId), CronRunResults.Pending, null,
                CronRunResults.Problem, problem.Kind, problem.Reason, now);
            if (!written.Applied)
            {
                FileLog.Write($"[CronRunResultService] Sweep: run={p.Run.RunId} reported before its problem was recorded; left as reported");
                continue;
            }
            FileLog.Write($"[CronRunResultService] Sweep: run={p.Run.RunId}, job={p.JobId}, problem={problem.Kind}");
            recorded++;
        }
        if (recorded > 0)
            FileLog.Write($"[CronRunResultService] Sweep: tenant={tenant.ToLogString()}, pending={pending.Count}, recorded={recorded}");
        return recorded;
    }

    /// <summary>
    /// The engine recorded a run that already carries its result - a fire that started no session, a due run that was
    /// skipped. Its factory activity row is owed from the moment it was recorded; write it now.
    /// </summary>
    public void Announce(TenantId tenant, CronJobDto job, CronRunRecord run)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(run);
        TryWriteOwedActivity(tenant, Guid.Parse(run.RunId));
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
        if (current.State == CronRunProblemStates.Cleared)
            return CronRunResultOutcome<CronRunProblemDto>.Refused(
                $"Run {id}'s problem is already cleared: run {current.ClearedByRunId}, the next run of the same schedule, reported ok.", 409);

        var written = _runs.Resolve(tenant, id, resolvedBy, reason!.Trim(), _nowUtc());
        if (!written.Applied)
        {
            var now = ProblemOf(tenant, found.JobId, id);
            return CronRunResultOutcome<CronRunProblemDto>.Refused(
                $"Run {id}'s problem is already resolved - {now.StateText}.", 409);
        }

        TryWriteOwedActivity(tenant, id);
        return CronRunResultOutcome<CronRunProblemDto>.Ok(ProblemOf(tenant, found.JobId, id));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Reading: the one fold

    /// <summary>The account's problems as of <paramref name="asOfUtc"/> (now when null), through
    /// <see cref="CronRunResultFold.Problems"/>.</summary>
    public CronRunProblemsDto Problems(TenantId tenant, string? factory, bool includeClosed, DateTime? asOfUtc = null)
    {
        var jobs = _allJobs().ToDictionary(j => j.Id, StringComparer.Ordinal);
        var asOf = asOfUtc ?? _nowUtc();
        var dto = CronRunResultFold.Problems(_runs.AllByJob(tenant), jobs, _zoneOf(tenant), factory, includeClosed, asOf);
        FileLog.Write($"[CronRunResultService] Problems: tenant={tenant.ToLogString()}, factory={factory ?? "-"}, state={dto.State}, asOf={asOf:O}, count={dto.Count}");
        return dto;
    }

    /// <summary>
    /// The account's open problems and each factory's last day of runs, read together from ONE read of the run history
    /// (Factory Control, step 4: the factory email). Both come from this fold, so the email's problem list and its one
    /// line per factory can never disagree about the same runs.
    /// </summary>
    /// <param name="tenant">The account. The schedules and the work history are read in the ambient scope.</param>
    /// <param name="fromUtc">The start of the day's window.</param>
    /// <param name="toUtc">The end of the day's window (exclusive).</param>
    public (CronRunProblemsDto Problems, IReadOnlyList<CronFactoryDayDto> Days) ProblemsAndFactoryDays(TenantId tenant, DateTime fromUtc, DateTime toUtc)
    {
        var jobs = _allJobs().ToDictionary(j => j.Id, StringComparer.Ordinal);
        var runsByJob = _runs.AllByJob(tenant);
        var problems = CronRunResultFold.Problems(runsByJob, jobs, _zoneOf(tenant), factory: null, includeClosed: false);

        var sessionIds = runsByJob.Values.SelectMany(r => r).Select(r => r.SessionId)
            .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).Distinct(StringComparer.Ordinal).ToList();
        var endings = sessionIds.Count == 0
            ? new Dictionary<string, SessionEndingFact>(StringComparer.Ordinal)
            : _endingsOf(sessionIds);
        var days = CronRunResultFold.FactoryDays(runsByJob, jobs, run => CronRunEndingFold.EndingOf(run, endings).Ending,
            problems.Problems, fromUtc, toUtc);
        FileLog.Write($"[CronRunResultService] ProblemsAndFactoryDays: tenant={tenant.ToLogString()}, open={problems.Count}, factories={days.Count}");
        return (problems, days);
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
            jobs, _zoneOf(tenant), factory: null, includeClosed: true, _nowUtc());
        return folded.Problems.Single(p => p.RunId == runId.ToString("D"));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Step 6: no silent factories

    private static bool IsFactoryLinked(CronRunRecord run) =>
        !string.IsNullOrWhiteSpace(run.Factory) && !string.IsNullOrWhiteSpace(run.Seat);

    /// <summary>
    /// The factory activity row for a run's result, built from the factory link fixed on the RUN when its result was
    /// recorded - never from the schedule, which may have been changed or deleted since. Null for a run in no factory.
    /// </summary>
    public static AppendFactoryActivityRequest? ActivityFor(string jobId, CronRunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!IsFactoryLinked(run)) return null;
        var name = run.JobName ?? jobId;
        var ok = run.Result == CronRunResults.Ok;
        var what = ok
            ? $"The scheduled run of {name} reported ok" + (string.IsNullOrWhiteSpace(run.ResultReason) ? "." : $": {run.ResultReason}")
            : $"The scheduled run of {name} {CronRunProblems.Words(run.Problem)}: {run.ResultReason}";
        return new AppendFactoryActivityRequest
        {
            Factory = run.Factory,
            FactoryAgent = run.Seat,
            SessionId = string.IsNullOrWhiteSpace(run.SessionId) ? null : run.SessionId,
            What = Truncate(what),
            Outcome = ok ? FactoryActivityOutcome.Done : FactoryActivityOutcome.Failed,
            Subject = name,
            Actor = ActorPrefix + jobId,
            OccurredUtc = run.ResultUtc ?? run.FiredUtc,
        };
    }

    /// <summary>
    /// Write every factory activity row the account's runs still owe. A run that owes one is marked so in the SAME
    /// write that records its result or its resolve, so nothing owed can be forgotten: a row this pass cannot write -
    /// the activity record refused it, the database failed - stays owed, is logged, and is written on the next pass.
    /// </summary>
    public int WriteAllOwedActivity(TenantId tenant)
    {
        var written = 0;
        foreach (var owed in _runs.ActivityDueRuns(tenant))
        {
            try
            {
                WriteOwedActivity(tenant, owed);
                written++;
            }
            catch (Exception ex)
            {
                // Timer boundary, and the debt stays recorded on the run: this is a retry, not a loss.
                FileLog.Write($"[CronRunResultService] WriteAllOwedActivity FAILED: run={owed.Run.RunId}, job={owed.JobId} - still owed, retried on the next sweep: {ex.Message}");
            }
        }
        return written;
    }

    // Write the rows one run owes right now, at the boundary of a report, a resolve or a fire. When it cannot, the run
    // keeps owing them - the write that recorded the result marked it - and the next sweep writes them.
    private void TryWriteOwedActivity(TenantId tenant, Guid runId)
    {
        var owed = _runs.Find(tenant, runId)
            ?? throw new InvalidOperationException($"run {runId} vanished before its factory activity row was written");
        try
        {
            WriteOwedActivity(tenant, owed);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CronRunResultService] factory activity row NOT written yet: run={runId}, job={owed.JobId} - still owed, the next sweep writes it: {ex.Message}");
        }
    }

    // Write the result row and, for a resolved problem, the correcting row this run still owes. Each is written in the
    // same database write that notes it on the run, so a failure at any point leaves the run owing exactly the rows not
    // yet recorded - never a recorded row still marked as owed, which a retry would write twice. Throws when a write fails.
    private void WriteOwedActivity(TenantId tenant, CronRunHistoryStore.JobRun owed)
    {
        var runId = Guid.Parse(owed.Run.RunId);
        if (!IsFactoryLinked(owed.Run))
        {
            _runs.SettleInNoFactory(tenant, runId);
            return;
        }

        var resultRow = owed.ActivityId;
        if (resultRow is null && owed.Run.Result is CronRunResults.Ok or CronRunResults.Problem)
        {
            resultRow = _writeActivity(tenant, runId, ActivityFor(owed.JobId, owed.Run)!, false);
            FileLog.Write($"[CronRunResultService] activity row written: run={runId}, factory={owed.Run.Factory}, row={resultRow}");
        }

        if (owed.Run.ResolvedUtc is { } resolvedUtc && owed.ResolveActivityId is null && resultRow is { } failedRow)
        {
            var correction = _writeActivity(tenant, runId, new AppendFactoryActivityRequest
            {
                Factory = owed.Run.Factory,
                FactoryAgent = owed.Run.Seat,
                What = Truncate($"Resolved by {owed.Run.ResolvedBy}: {owed.Run.ResolvedReason}"),
                Outcome = FactoryActivityOutcome.Done,
                Subject = owed.Run.JobName ?? owed.JobId,
                Actor = owed.Run.ResolvedBy,
                CorrectsId = failedRow,
                OccurredUtc = resolvedUtc,
            }, true);
            FileLog.Write($"[CronRunResultService] correcting row written: run={runId}, factory={owed.Run.Factory}, row={correction}");
        }
    }

    private static string Truncate(string text) =>
        text.Length <= CronRunResultFold.MaxReasonChars ? text : text[..(CronRunResultFold.MaxReasonChars - 3)] + "...";
}
