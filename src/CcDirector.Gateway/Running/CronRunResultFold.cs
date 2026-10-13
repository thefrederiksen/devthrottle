using System.Globalization;
using CcDirector.Gateway.Contracts;

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
        if (run.Result != CronRunResults.Pending) return null;

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
    public static CronRunProblemsDto Problems(
        IReadOnlyDictionary<string, IReadOnlyList<CronRunRecord>> runsByJob,
        IReadOnlyDictionary<string, CronJobDto> jobs,
        TimeZoneInfo zone,
        string? factory,
        bool includeClosed,
        DateTime asOfUtc)
    {
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

            // Newest first: a problem at index i is cleared by an ok run at any index before it.
            for (var i = 0; i < runs.Count; i++)
            {
                var run = runs[i];
                if (run.Result != CronRunResults.Problem) continue;
                if (RaisedAt(run) > asOfUtc) continue;
                // Under the factory the RUN was in, fixed when it was created - not the schedule's factory today.
                if (wanted is not null && !string.Equals(run.Factory, wanted, StringComparison.OrdinalIgnoreCase)) continue;

                var clearedBy = runs.Take(i).LastOrDefault(r => r.Result == CronRunResults.Ok && r.ResultUtc is { } at && at <= asOfUtc);
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
                    dto.StateText = "cleared - the next run of this schedule reported ok";
                }
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
