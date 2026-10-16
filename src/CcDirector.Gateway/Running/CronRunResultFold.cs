using System.Globalization;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.Running;

/// <summary>A problem the Gateway records on a run by itself: its kind and the one line why.</summary>
public sealed record GatewayRecordedProblem(string Kind, string Reason);

/// <summary>
/// The pure rules of a scheduled run's result (Factory Control, step 1). No store, no clock, no I/O: every input is
/// handed in, so each rule is tested on its own.
///
/// THE RULES, in the owner's words of 10 October 2026:
///   - every run says how it went: ok, or a problem with one line why;
///   - a run that ends without saying is a problem: "did not report";
///   - a run still going when its shift ends is a problem: "ran past its shift";
///   - a due run that never started is a problem: "did not run";
///   - a problem stays open until the NEXT run of the same schedule reports ok, or someone resolves it with a reason.
/// </summary>
public static class CronRunResultFold
{
    /// <summary>The longest one-line reason a result or a resolve accepts.</summary>
    public const int MaxReasonChars = 500;

    /// <summary>
    /// The problem the Gateway records on a run still waiting on its report, or null when it should keep waiting.
    /// </summary>
    /// <param name="run">A run whose result is still pending.</param>
    /// <param name="ending">How its session ended, from <see cref="CronRunEndingFold.EndingOf"/>.</param>
    /// <param name="endingText">That ending in words, from <see cref="CronRunEndingFold.TextOf"/>.</param>
    /// <param name="shift">The shift the run started in.</param>
    /// <param name="nowUtc">Now.</param>
    public static GatewayRecordedProblem? GatewayProblemFor(CronRunRecord run, string ending, string endingText, WorkShift shift, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(shift);
        // Only a run whose session owes its report can earn one of these. Every other result is named, so a new one
        // cannot slip in as "waiting": a run waiting on its check owes no report yet, and nothing-to-do never will.
        switch (run.Result)
        {
            case CronRunResults.Pending:
                break;
            case CronRunResults.Ok or CronRunResults.Problem or CronRunResults.Untracked
                or CronRunResults.Checking or CronRunResults.NothingToDo:
                return null;
            default:
                throw new InvalidOperationException(
                    $"Run {run.RunId} has the result '{run.Result}', which is not one of the results CronRunResults defines. Add it to CronRunResultFold.GatewayProblemFor.");
        }

        switch (ending)
        {
            case CronRunEndings.StillOpen:
                // Still going: a problem only once its shift is over.
                return nowUtc >= shift.EndUtc
                    ? new GatewayRecordedProblem(CronRunProblems.RanPastShift,
                        $"it had not reported and was still open when its {shift.Text} ended")
                    : null;

            case CronRunEndings.ClosedItself:
            case CronRunEndings.StoppedByYou:
            case CronRunEndings.StoppedBySession:
            case CronRunEndings.ClosedNotRecorded:
            case CronRunEndings.DirectorStopped:
            case CronRunEndings.Interrupted:
                return new GatewayRecordedProblem(CronRunProblems.DidNotReport,
                    $"its session ended without reporting how the run went ({endingText})");

            case CronRunEndings.Unknown:
                // No work-history row for the session: it may be too young to have pushed one, so nothing is concluded
                // before the shift is over. After that, a run that never reported and left no record did not report.
                return nowUtc >= shift.EndUtc
                    ? new GatewayRecordedProblem(CronRunProblems.DidNotReport,
                        $"it did not report by the end of its {shift.Text}, and the Gateway has no record of its session")
                    : null;

            default:
                // NoSession and WorkList runs are never pending: a fire records them as did-not-run or untracked.
                throw new InvalidOperationException($"a pending run cannot have the ending '{ending}' (run {run.RunId})");
        }
    }

    /// <summary>
    /// The refusal for a one-line reason, or null when it may be stored. <paramref name="required"/> says whether an
    /// empty one is refused.
    /// </summary>
    public static string? CheckReason(string? reason, bool required, string what)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return required ? $"{what} needs a one-line reason, and none was given. Nothing was recorded." : null;
        var trimmed = reason.Trim();
        if (trimmed.Contains('\n') || trimmed.Contains('\r'))
            return $"{what} takes ONE line, and the reason given has several. Nothing was recorded.";
        if (trimmed.Length > MaxReasonChars)
            return $"{what} takes one line of at most {MaxReasonChars} characters, and the reason given has {trimmed.Length}. Nothing was recorded.";
        return null;
    }

    /// <summary>
    /// Fold an account's runs into its problems, newest first (the one source the factory page, the 08:00 email and
    /// the Factory Manager read).
    /// </summary>
    /// <param name="runsByJob">Every run of the account, by schedule id, newest first.</param>
    /// <param name="jobs">The account's schedules, by id. A run of a deleted schedule is still listed, under its id.</param>
    /// <param name="zone">The account's time zone, for the shifts.</param>
    /// <param name="factory">Only problems of this factory's schedules, or null for all.</param>
    /// <param name="includeClosed">True to list cleared and resolved problems too.</param>
    /// <param name="asOfUtc">The instant to describe: a problem raised after it is left out, and a clear or resolve
    /// after it has not happened yet. The 16:00 and midnight checks read the state as it stood when their shift
    /// ended.</param>
    /// <param name="definitionTriggerRuns">The SECOND SOURCE (Factory Definition, phase 5b, design 3.5): every
    /// definition trigger of the account with its checks, newest first. A failed check is a <c>check-failed</c>
    /// problem in the same shape (<see cref="TriggerProblems"/>). Command triggers are never handed in: their failures
    /// are not problems.</param>
    public static CronRunProblemsDto Problems(
        IReadOnlyDictionary<string, IReadOnlyList<CronRunRecord>> runsByJob,
        IReadOnlyDictionary<string, CronJobDto> jobs,
        TimeZoneInfo zone,
        string? factory,
        bool includeClosed,
        DateTime asOfUtc,
        IReadOnlyList<(TriggerEntity Trigger, IReadOnlyList<TriggerRunEntity> Runs)> definitionTriggerRuns)
    {
        ArgumentNullException.ThrowIfNull(definitionTriggerRuns);
        ArgumentNullException.ThrowIfNull(runsByJob);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(zone);
        if (asOfUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException($"the as-of instant must be UTC, got {asOfUtc.Kind}", nameof(asOfUtc));
        var wanted = string.IsNullOrWhiteSpace(factory) ? null : factory.Trim();

        var problems = new List<CronRunProblemDto>();
        foreach (var (jobId, runs) in runsByJob)
        {
            jobs.TryGetValue(jobId, out var job);

            // Newest first: a problem at index i is cleared by an ok run at any index before it - and a problem a
            // check raised, also by a run whose check found nothing to do (the check works again: phase 5b).
            for (var i = 0; i < runs.Count; i++)
            {
                var run = runs[i];
                if (!IsProblem(run)) continue;
                if (RaisedAt(run) > asOfUtc) continue;
                // Under the factory the RUN was in, fixed when it was created - not the schedule's factory today.
                if (wanted is not null && !string.Equals(run.Factory, wanted, StringComparison.OrdinalIgnoreCase)) continue;

                var clearedBy = runs.Take(i).LastOrDefault(r => Clears(r, run) && r.ResultUtc is { } at && at <= asOfUtc);
                var dto = ProblemOf(jobId, job, run, zone);
                if (run.ResolvedUtc is { } resolved && resolved <= asOfUtc)
                {
                    dto.State = CronRunProblemStates.Resolved;
                    dto.ClosedUtc = resolved;
                    dto.ResolvedBy = run.ResolvedBy;
                    dto.ResolvedReason = run.ResolvedReason;
                    dto.StateText = $"resolved by {run.ResolvedBy}: {run.ResolvedReason}";
                }
                else if (clearedBy is not null)
                {
                    dto.State = CronRunProblemStates.Cleared;
                    dto.ClosedUtc = clearedBy.ResultUtc;
                    dto.ClearedByRunId = clearedBy.RunId;
                    dto.StateText = clearedBy.Result == CronRunResults.NothingToDo
                        ? "cleared - the next run of this schedule ran its check, which found nothing to do"
                        : "cleared - the next run of this schedule reported ok";
                }
                if (!includeClosed && dto.State != CronRunProblemStates.Open) continue;
                if (dto.State == CronRunProblemStates.Open)
                    dto.OpenForText = OpenFor(asOfUtc - dto.RaisedAtUtc);
                problems.Add(dto);
            }
        }

        foreach (var (trigger, triggerRuns) in definitionTriggerRuns)
        {
            if (!trigger.IsDefinitionCheck)
                throw new ArgumentException($"trigger {trigger.Name} runs a command, and a command trigger's failures are not problems", nameof(definitionTriggerRuns));
            if (wanted is not null && !string.Equals(trigger.Factory, wanted, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var dto in TriggerProblems(trigger, triggerRuns, zone, asOfUtc))
            {
                if (!includeClosed && dto.State != CronRunProblemStates.Open) continue;
                if (dto.State == CronRunProblemStates.Open)
                    dto.OpenForText = OpenFor(asOfUtc - dto.RaisedAtUtc);
                problems.Add(dto);
            }
        }

        problems = problems
            .OrderByDescending(p => p.StartedUtc ?? p.ScheduledUtc)
            .ThenBy(p => p.JobId, StringComparer.Ordinal)
            .ToList();
        return new CronRunProblemsDto
        {
            Count = problems.Count,
            Occurrences = problems.Sum(p => p.Occurrences),
            AsOfUtc = asOfUtc,
            State = includeClosed ? CronRunProblemStates.All : CronRunProblemStates.Open,
            Factory = wanted,
            TimeZone = zone.Id,
            Problems = problems,
        };
    }

    /// <summary>
    /// Whether a run is a problem, naming EVERY result (Factory Definition, phase 5b): a run waiting on its check and a
    /// run whose check found nothing to do are neither pending nor problems, and a result this does not know is an
    /// error rather than a run quietly left off the list.
    /// </summary>
    public static bool IsProblem(CronRunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return run.Result switch
        {
            CronRunResults.Problem => true,
            CronRunResults.Ok or CronRunResults.Pending or CronRunResults.Untracked
                or CronRunResults.Checking or CronRunResults.NothingToDo => false,
            _ => throw new InvalidOperationException(
                $"Run {run.RunId} has the result '{run.Result}', which is not one of the results CronRunResults defines. Add it to CronRunResultFold.IsProblem."),
        };
    }

    /// <summary>
    /// How a failed check leaves the problem list, in one sentence for every surface that explains it - the Open problems
    /// card and the 08:00 email - said only when a failed check is among the problems shown, so a list with none reads
    /// as it always has. True to <see cref="Clears"/> and <see cref="TriggerProblems"/>.
    /// </summary>
    public const string CheckClearsSentence =
        "A failed check of a timed start also clears when a later check finds nothing to do, and a failed check of an " +
        "interval start when its next check does not fail.";

    /// <summary>True when any of <paramref name="problems"/> is one a check raised.</summary>
    public static bool AnyCheckProblem(IEnumerable<CronRunProblemDto> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        return problems.Any(p => CronRunProblems.IsCheckProblem(p.Kind));
    }

    /// <summary>Whether a later run of the same schedule clears <paramref name="problem"/>: an ok run clears any
    /// problem; a run whose check found nothing to do clears a problem a check raised (the check works again).</summary>
    public static bool Clears(CronRunRecord later, CronRunRecord problem)
        => later.Result == CronRunResults.Ok
           || (later.Result == CronRunResults.NothingToDo && CronRunProblems.IsCheckProblem(problem.Problem));

    /// <summary>
    /// A definition trigger's failed checks as problems (Factory Definition, phase 5b, design 3.5), in the shape a run's
    /// problem has. Failures in a row are ONE problem - the first failed check of the run of failures names it, and
    /// <see cref="CronRunProblemDto.Occurrences"/> counts them - so a check that fails every ten minutes for a day is
    /// one line, not a hundred and forty. The problem is cleared by the trigger's next check that did not fail,
    /// whatever it came to; resolved by hand, it stays resolved, and a check failing after the resolve starts a new one.
    /// Only checks recorded by <paramref name="asOfUtc"/> are read, and a clear or resolve after it has not happened yet.
    /// </summary>
    public static IReadOnlyList<CronRunProblemDto> TriggerProblems(TriggerEntity trigger, IReadOnlyList<TriggerRunEntity> runsNewestFirst,
        TimeZoneInfo zone, DateTime asOfUtc)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(runsNewestFirst);
        ArgumentNullException.ThrowIfNull(zone);
        var found = new List<CronRunProblemDto>();
        var oldestFirst = runsNewestFirst.Where(r => AsUtc(r.RecordedUtc) <= asOfUtc).Reverse().ToList();
        List<TriggerRunEntity>? failures = null;
        foreach (var run in oldestFirst)
        {
            if (run.Outcome == TriggerRunOutcome.Failed)
            {
                var head = failures?[0];
                if (head is not null && head.ResolvedUtc is { } resolved && AsUtc(resolved) <= asOfUtc && AsUtc(run.RecordedUtc) > AsUtc(resolved))
                {
                    found.Add(TriggerProblemOf(trigger, failures!, clearedBy: null, zone, asOfUtc));
                    failures = null;
                }
                (failures ??= new List<TriggerRunEntity>()).Add(run);
            }
            else if (failures is not null)
            {
                found.Add(TriggerProblemOf(trigger, failures, clearedBy: run, zone, asOfUtc));
                failures = null;
            }
        }
        if (failures is not null)
            found.Add(TriggerProblemOf(trigger, failures, clearedBy: null, zone, asOfUtc));
        return found;
    }

    private static CronRunProblemDto TriggerProblemOf(TriggerEntity trigger, List<TriggerRunEntity> failures,
        TriggerRunEntity? clearedBy, TimeZoneInfo zone, DateTime asOfUtc)
    {
        var head = failures[0];
        var newest = failures[^1];
        var checkedUtc = AsUtc(head.CheckedUtc);
        var shift = WorkShifts.ShiftOf(checkedUtc, zone);
        var whenLocal = TimeZoneInfo.ConvertTimeFromUtc(checkedUtc, zone).ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture);
        var reason = string.IsNullOrWhiteSpace(newest.Reason) ? "no reason was recorded" : newest.Reason!;
        var dto = new CronRunProblemDto
        {
            RunId = head.Id.ToString("D"),
            JobId = trigger.Id.ToString("D"),
            JobName = trigger.Name,
            Factory = trigger.Factory,
            Seat = trigger.Seat ?? trigger.FactoryAgent,
            ScheduledUtc = checkedUtc,
            StartedUtc = null,
            Shift = shift.Name,
            ShiftText = shift.Text,
            Kind = CronRunProblems.CheckFailed,
            KindText = CronRunProblems.Words(CronRunProblems.CheckFailed),
            Reason = reason,
            Sentence = SentenceOf(CronRunProblems.CheckFailed, failures.Count == 1
                ? reason
                : $"{reason} ({failures.Count.ToString(CultureInfo.InvariantCulture)} checks in a row failed)"),
            WhenText = "Checked " + whenLocal,
            RaisedAtUtc = AsUtc(head.RecordedUtc),
            Occurrences = failures.Count,
            SessionId = null,
            State = CronRunProblemStates.Open,
        };
        if (head.ResolvedUtc is { } resolved && AsUtc(resolved) <= asOfUtc)
        {
            dto.State = CronRunProblemStates.Resolved;
            dto.ClosedUtc = AsUtc(resolved);
            dto.ResolvedBy = head.ResolvedBy;
            dto.ResolvedReason = head.ResolvedReason;
            dto.StateText = $"resolved by {head.ResolvedBy}: {head.ResolvedReason}";
        }
        else if (clearedBy is not null)
        {
            dto.State = CronRunProblemStates.Cleared;
            dto.ClosedUtc = AsUtc(clearedBy.RecordedUtc);
            dto.ClearedByRunId = clearedBy.Id.ToString("D");
            dto.StateText = "cleared - the next check of this trigger did not fail";
        }
        return dto;
    }

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>The infrastructure status of a run that was due and never fired.</summary>
    public const string DidNotRunStatus = "did-not-run";

    /// <summary>A run that was due at <paramref name="dueUtc"/> and never started a session, recorded at <paramref name="nowUtc"/>.</summary>
    public static CronRunRecord DidNotRunRecord(CronJobDto job, DateTime dueUtc, DateTime nowUtc, string reason, int missedNotListed = 0)
    {
        ArgumentNullException.ThrowIfNull(job);
        var record = new CronRunRecord
        {
            ScheduledUtc = DateTime.SpecifyKind(dueUtc, DateTimeKind.Utc),
            FiredUtc = nowUtc,
            Machine = job.Target.Machine,
            TargetDirectorId = "",
            SessionId = null,
            InfraStatus = DidNotRunStatus,
            TaskStatus = CronRunHistoryStore.TaskStatusUnknown,
            Result = CronRunResults.Problem,
            Problem = CronRunProblems.DidNotRun,
            ResultReason = reason,
            ResultUtc = nowUtc,
            MissedNotListed = missedNotListed,
        };
        StampFactoryLink(record, job);
        return record;
    }

    /// <summary>Fix the schedule's factory link on a run recorded with its result, so its owed row can always be written.</summary>
    public static void StampFactoryLink(CronRunRecord record, CronJobDto job)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(job);
        if (!CronRunHistoryStore.IsFactoryLinked(job)) return;
        record.Factory = job.Factory!.Trim();
        record.Seat = job.Seat!.Trim();
        record.JobName = job.Name;
    }

    /// <summary>
    /// Each factory's last day of runs, for the 08:00 email (Factory Control, step 4): runs done and runs that did not
    /// run in <c>[fromUtc, toUtc)</c>, checks that failed in it, seats whose run session is still open now, and open
    /// problems. Listed by factory id: every factory with a schedule, and a factory with only interval starts when one
    /// of their checks failed in the window or a problem of the factory is still open (Factory Definition, phase 5b) -
    /// so a failed interval check is never in the problem list and missing from the table.
    /// </summary>
    /// <param name="runsByJob">Every run of the account, by schedule id, newest first.</param>
    /// <param name="jobs">The account's schedules, by id.</param>
    /// <param name="endingOf">How a run's session ended (<see cref="CronRunEndingFold.EndingOf"/>).</param>
    /// <param name="openProblems">The account's open problems, from <see cref="Problems"/>.</param>
    /// <param name="fromUtc">The start of the window.</param>
    /// <param name="toUtc">The end of the window (exclusive).</param>
    /// <param name="definitionTriggerRuns">Every definition trigger of the account with its checks, as
    /// <see cref="Problems"/> is handed them. Command triggers are never handed in.</param>
    public static IReadOnlyList<CronFactoryDayDto> FactoryDays(
        IReadOnlyDictionary<string, IReadOnlyList<CronRunRecord>> runsByJob,
        IReadOnlyDictionary<string, CronJobDto> jobs,
        Func<CronRunRecord, string> endingOf,
        IReadOnlyList<CronRunProblemDto> openProblems,
        DateTime fromUtc,
        DateTime toUtc,
        IReadOnlyList<(TriggerEntity Trigger, IReadOnlyList<TriggerRunEntity> Runs)> definitionTriggerRuns)
    {
        ArgumentNullException.ThrowIfNull(definitionTriggerRuns);
        ArgumentNullException.ThrowIfNull(runsByJob);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(endingOf);
        ArgumentNullException.ThrowIfNull(openProblems);

        var days = new Dictionary<string, CronFactoryDayDto>(StringComparer.OrdinalIgnoreCase);
        var openSeats = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs.Values)
        {
            if (string.IsNullOrWhiteSpace(job.Factory)) continue;
            var factory = job.Factory.Trim();
            if (!days.TryGetValue(factory, out var day))
            {
                day = new CronFactoryDayDto { Factory = factory };
                days[factory] = day;
                openSeats[factory] = new HashSet<string>(StringComparer.Ordinal);
            }
            if (!runsByJob.TryGetValue(job.Id, out var runs)) continue;

            foreach (var run in runs)
            {
                // EVERY RESULT IS NAMED (Factory Definition, phase 5b): a run whose check found nothing to do is
                // counted as such - never as did not run - and a run still waiting on its check counts as nothing yet.
                switch (run.Result)
                {
                    case CronRunResults.Ok:
                        if (InWindow(run.FiredUtc, fromUtc, toUtc)) day.Done++;
                        break;
                    case CronRunResults.NothingToDo:
                        if (InWindow(run.FiredUtc, fromUtc, toUtc)) day.NothingToDo++;
                        break;
                    case CronRunResults.Problem:
                        // The last row of a long outage also stands for the due times past the row limit.
                        if (run.Problem == CronRunProblems.DidNotRun && InWindow(run.ScheduledUtc, fromUtc, toUtc))
                            day.DidNotRun += 1 + run.MissedNotListed;
                        else if (CronRunProblems.IsCheckProblem(run.Problem) && InWindow(run.FiredUtc, fromUtc, toUtc))
                            day.CheckFailed++;
                        break;
                    case CronRunResults.Pending:
                    case CronRunResults.Untracked:
                    case CronRunResults.Checking:
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Run {run.RunId} has the result '{run.Result}', which is not one of the results CronRunResults defines. Add it to CronRunResultFold.FactoryDays.");
                }
                if (!string.IsNullOrWhiteSpace(run.SessionId) && endingOf(run) == CronRunEndings.StillOpen)
                    openSeats[factory].Add(string.IsNullOrWhiteSpace(job.Seat) ? job.Id : job.Seat);
            }
        }

        // An interval start's failed checks (Factory Definition, phase 5b). Its other checks are not runs of the day:
        // one that found nothing to do every ten minutes would bury the scheduled runs the table is about.
        var withIntervalTrouble = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (trigger, checks) in definitionTriggerRuns)
        {
            if (string.IsNullOrWhiteSpace(trigger.Factory)) continue;
            var factory = trigger.Factory.Trim();
            var failed = checks.Count(c => c.Outcome == TriggerRunOutcome.Failed && InWindow(c.RecordedUtc, fromUtc, toUtc));
            if (failed == 0) continue;
            DayOf(days, factory).CheckFailed += failed;
            withIntervalTrouble.Add(factory);
        }
        foreach (var problem in openProblems)
        {
            if (problem.State != CronRunProblemStates.Open || string.IsNullOrWhiteSpace(problem.Factory)) continue;
            var factory = problem.Factory.Trim();
            // A factory with no schedule is listed for its interval starts' open problems too.
            if (!days.ContainsKey(factory) && definitionTriggerRuns.Any(t => string.Equals(t.Trigger.Factory?.Trim(), factory, StringComparison.OrdinalIgnoreCase)))
                withIntervalTrouble.Add(factory);
            if (days.ContainsKey(factory) || withIntervalTrouble.Contains(factory))
                DayOf(days, factory).OpenProblems++;
        }
        foreach (var (factory, seats) in openSeats)
            days[factory].LeftOpen = seats.Count;

        return days.Values.OrderBy(d => d.Factory, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static CronFactoryDayDto DayOf(Dictionary<string, CronFactoryDayDto> days, string factory)
    {
        if (!days.TryGetValue(factory, out var day))
        {
            day = new CronFactoryDayDto { Factory = factory };
            days[factory] = day;
        }
        return day;
    }

    private static bool InWindow(DateTime utc, DateTime fromUtc, DateTime toUtc)
    {
        var at = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return at >= fromUtc && at < toUtc;
    }

    /// <summary>When the run's shift is counted from: its start, or when it was due if it never started a session.</summary>
    public static DateTime ShiftInstantOf(CronRunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return string.IsNullOrWhiteSpace(run.SessionId) ? run.ScheduledUtc : run.FiredUtc;
    }

    private static CronRunProblemDto ProblemOf(string jobId, CronJobDto? job, CronRunRecord run, TimeZoneInfo zone)
    {
        var shift = WorkShifts.ShiftOf(ShiftInstantOf(run), zone);
        var started = string.IsNullOrWhiteSpace(run.SessionId) ? (DateTime?)null : run.FiredUtc;
        var whenLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(ShiftInstantOf(run), DateTimeKind.Utc), zone)
            .ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture);
        return new CronRunProblemDto
        {
            RunId = run.RunId,
            JobId = jobId,
            JobName = run.JobName ?? job?.Name ?? jobId,
            Factory = run.Factory,
            Seat = run.Seat,
            ScheduledUtc = DateTime.SpecifyKind(run.ScheduledUtc, DateTimeKind.Utc),
            StartedUtc = started is { } s ? DateTime.SpecifyKind(s, DateTimeKind.Utc) : null,
            Shift = shift.Name,
            ShiftText = shift.Text,
            Kind = run.Problem ?? "",
            KindText = CronRunProblems.Words(run.Problem),
            Reason = run.ResultReason ?? "",
            Sentence = SentenceOf(run.Problem, run.ResultReason),
            WhenText = (started is null ? "Due " : "Started ") + whenLocal,
            RaisedAtUtc = RaisedAt(run),
            Occurrences = 1 + run.MissedNotListed,
            SessionId = string.IsNullOrWhiteSpace(run.SessionId) ? null : run.SessionId,
            State = CronRunProblemStates.Open,
        };
    }
    /// <summary>When a run's problem was raised: the instant its result was recorded.</summary>
    public static DateTime RaisedAt(CronRunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return DateTime.SpecifyKind(run.ResultUtc ?? run.FiredUtc, DateTimeKind.Utc);
    }

    /// <summary>A problem as one finished sentence: its kind, then its reason, ending in a full stop.</summary>
    public static string SentenceOf(string? problem, string? reason)
    {
        var kind = CronRunProblems.Words(problem);
        var head = char.ToUpperInvariant(kind[0]) + kind[1..];
        var why = (reason ?? "").Trim();
        if (why.Length == 0) return head + ".";
        return why[^1] is '.' or '!' or '?' ? $"{head}: {why}" : $"{head}: {why}.";
    }

    /// <summary>How long a problem has been open, in words: minutes under an hour, hours under two days, then days.</summary>
    public static string OpenFor(TimeSpan open)
    {
        if (open < TimeSpan.Zero) open = TimeSpan.Zero;
        if (open.TotalMinutes < 1) return "open less than a minute";
        if (open.TotalHours < 1) return Count((int)open.TotalMinutes, "minute");
        if (open.TotalHours < 48) return Count((int)open.TotalHours, "hour");
        return Count((int)open.TotalDays, "day");

        static string Count(int n, string unit) => $"open {n} {unit}{(n == 1 ? "" : "s")}";
    }
}
