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
    public static CronRunProblemsDto Problems(
        IReadOnlyDictionary<string, IReadOnlyList<CronRunRecord>> runsByJob,
        IReadOnlyDictionary<string, CronJobDto> jobs,
        TimeZoneInfo zone,
        string? factory,
        bool includeClosed)
    {
        ArgumentNullException.ThrowIfNull(runsByJob);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(zone);
        var wanted = string.IsNullOrWhiteSpace(factory) ? null : factory.Trim();

        var problems = new List<CronRunProblemDto>();
        foreach (var (jobId, runs) in runsByJob)
        {
            jobs.TryGetValue(jobId, out var job);
            if (wanted is not null && !string.Equals(job?.Factory, wanted, StringComparison.OrdinalIgnoreCase))
                continue;

            // Newest first: a problem at index i is cleared by an ok run at any index before it.
            for (var i = 0; i < runs.Count; i++)
            {
                var run = runs[i];
                if (run.Result != CronRunResults.Problem) continue;

                var clearedBy = runs.Take(i).LastOrDefault(r => r.Result == CronRunResults.Ok);
                var dto = ProblemOf(jobId, job, run, zone);
                if (run.ResolvedUtc is { } resolved)
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
            State = includeClosed ? CronRunProblemStates.All : CronRunProblemStates.Open,
            Factory = wanted,
            TimeZone = zone.Id,
            Problems = problems,
        };
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
        return new CronRunProblemDto
        {
            RunId = run.RunId,
            JobId = jobId,
            JobName = job?.Name ?? jobId,
            Factory = job?.Factory,
            Seat = job?.Seat,
            ScheduledUtc = DateTime.SpecifyKind(run.ScheduledUtc, DateTimeKind.Utc),
            StartedUtc = started is { } s ? DateTime.SpecifyKind(s, DateTimeKind.Utc) : null,
            Shift = shift.Name,
            ShiftText = shift.Text,
            Kind = run.Problem ?? "",
            KindText = CronRunProblems.Words(run.Problem),
            Reason = run.ResultReason ?? "",
            RecordedUtc = run.ResultUtc ?? run.FiredUtc,
            SessionId = string.IsNullOrWhiteSpace(run.SessionId) ? null : run.SessionId,
            State = CronRunProblemStates.Open,
        };
    }
}
