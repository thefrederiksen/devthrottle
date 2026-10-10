using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;

namespace CcDirector.Gateway.Running;

/// <summary>
/// Reads scheduled runs with their endings folded on (<see cref="CronRunEndingFold"/>): one schedule's run table, and
/// every schedule's run record for the Schedule page list. It joins the run history to the session work history,
/// the only two places the facts live, in one query each - the list is polled every few seconds.
/// </summary>
public sealed class CronRunRecordReader
{
    private readonly CronRunHistoryStore _runs;
    private readonly Func<IReadOnlyCollection<string>, IReadOnlyDictionary<string, SessionEndingFact>> _endingsOf;

    /// <param name="runs">The run history.</param>
    /// <param name="endingsOf">How the given sessions ended, from the work history
    /// (<see cref="SessionHistoryStore.EndingsOf"/> in production).</param>
    public CronRunRecordReader(CronRunHistoryStore runs,
        Func<IReadOnlyCollection<string>, IReadOnlyDictionary<string, SessionEndingFact>> endingsOf)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _endingsOf = endingsOf ?? throw new ArgumentNullException(nameof(endingsOf));
    }

    /// <summary>One schedule's runs, newest first, each with its ending.</summary>
    public IReadOnlyList<CronRunRecord> RunsOf(string jobId, DateTime nowUtc)
    {
        var runs = _runs.List(jobId);
        CronRunEndingFold.Stamp(runs, _endingsOf(SessionIdsOf(runs)), nowUtc);
        return runs;
    }

    /// <summary>The run record of each of these schedules, keyed by job id. A schedule that never ran is absent.</summary>
    public IReadOnlyDictionary<string, CronRunRecordSummaryDto> SummariesOf(IReadOnlyCollection<string> jobIds, DateTime nowUtc)
    {
        var byJob = _runs.RecentByJob(jobIds, CronRunEndingFold.SummaryRuns);
        var endings = StampAll(byJob, _endingsOf, nowUtc);
        var result = new Dictionary<string, CronRunRecordSummaryDto>(StringComparer.Ordinal);
        foreach (var (jobId, runs) in byJob)
            result[jobId] = CronRunEndingFold.Summarize(runs, endings, nowUtc);
        return result;
    }

    /// <summary>
    /// Each of these schedules' newest <see cref="CronRunEndingFold.SummaryRuns"/> runs, newest first, each with its
    /// ending - what a factory seat's marker is folded from (<see cref="CronRunEndingFold.SeatMarker"/>). Read in the
    /// account the route resolved EXPLICITLY, never the ambient one, the way the Factories screen reads everything
    /// else. A schedule that never ran is absent.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<CronRunRecord>> RecentRunsOf(CronRunHistoryStore runs,
        SessionHistoryStore history, Core.Tenancy.TenantId tenant, IReadOnlyCollection<string> jobIds, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(history);
        var byJob = runs.RecentByJob(tenant, jobIds, CronRunEndingFold.SummaryRuns);
        StampAll(byJob, ids => history.EndingsOf(tenant, ids), nowUtc);
        FileLog.Write($"[CronRunRecordReader] RecentRunsOf: schedules asked={jobIds.Count}, with runs={byJob.Count}");
        return byJob;
    }

    // Stamp every run with its ending, reading the sessions' endings in one query; returns those endings.
    private static IReadOnlyDictionary<string, SessionEndingFact> StampAll(IReadOnlyDictionary<string, IReadOnlyList<CronRunRecord>> byJob,
        Func<IReadOnlyCollection<string>, IReadOnlyDictionary<string, SessionEndingFact>> endingsOf, DateTime nowUtc)
    {
        var endings = endingsOf(SessionIdsOf(byJob.Values.SelectMany(r => r)));
        foreach (var runs in byJob.Values)
            CronRunEndingFold.Stamp(runs, endings, nowUtc);
        return endings;
    }

    /// <summary>
    /// How long each of these schedules' sessions typically stay open: the middle of its recent runs that have ended,
    /// from the fire to the end of the session, however it ended - a run left open until someone closed it really did
    /// hold a session that long. A schedule none of whose recent runs has ended is absent, and the load forecast says
    /// its length is a guess (<see cref="CronLoad.UnmeasuredRunLength"/>).
    /// </summary>
    public IReadOnlyDictionary<string, TimeSpan> RunLengthsOf(IReadOnlyCollection<string> jobIds)
    {
        // Read deeper than the sample: a schedule that fires every few minutes and holds each session for an hour
        // always has its newest runs open, so the sample is the newest runs that ENDED, found among these.
        var byJob = _runs.RecentByJob(jobIds, RunLengthReadDepth);
        var endings = _endingsOf(SessionIdsOf(byJob.Values.SelectMany(r => r)));
        var result = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        foreach (var (jobId, runs) in byJob)
        {
            var lengths = runs
                .Where(r => r.SessionId is not null)
                .Select(r => endings.TryGetValue(r.SessionId!, out var f) && f.EndedAtUtc is { } e && e >= r.FiredUtc
                    ? (TimeSpan?)(e - r.FiredUtc)
                    : null)
                .Where(l => l is not null)
                .Select(l => l!.Value)
                .Take(CronRunEndingFold.SummaryRuns)
                .OrderBy(l => l)
                .ToList();
            if (lengths.Count > 0)
                result[jobId] = lengths[lengths.Count / 2];
        }
        return result;
    }

    /// <summary>How many of a schedule's newest runs are read to find the newest ones that ended.</summary>
    public const int RunLengthReadDepth = 100;

    private static IReadOnlyCollection<string> SessionIdsOf(IEnumerable<CronRunRecord> runs) =>
        runs.Select(r => r.SessionId).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!)
            .Distinct(StringComparer.Ordinal).ToList();
}
