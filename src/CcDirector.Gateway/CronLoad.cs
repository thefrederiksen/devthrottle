using System.Globalization;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Running;

namespace CcDirector.Gateway;

/// <summary>
/// The 24-hour load forecast of the Schedule page's load strip (the owner, 2026-10-09: "we also need to figure out
/// the scheduling timeline for the same computer"). Every ACTIVE schedule's fires over the next 24 hours become
/// sessions that stay open for that schedule's own measured run length; each hour then reports the most of them open
/// at the same moment, per machine, against <see cref="DefaultCapacity"/>.
///
/// A pure fold, so the page and <c>cc-devthrottle schedule load</c> read one answer and a test can pin it.
/// </summary>
public static class CronLoad
{
    /// <summary>
    /// How many scheduled sessions one machine is meant to have open at once. There was no such number anywhere
    /// before the load strip; six is the proposal put to the owner in the Schedule page report, and it is one constant
    /// so it can become a setting without touching the fold.
    /// </summary>
    public const int DefaultCapacity = 6;

    /// <summary>
    /// The length assumed for a schedule none of whose runs has ended yet. The bars that hold such a schedule are
    /// estimates, and the forecast says which schedules they are (<see cref="CronMachineLoadDto.EstimatedJobIds"/>).
    /// </summary>
    public static readonly TimeSpan UnmeasuredRunLength = TimeSpan.FromMinutes(30);

    /// <summary>The number of hours forecast.</summary>
    public const int Hours = 24;

    /// <summary>
    /// The forecast for these schedules. Only active ones count (<see cref="CronSchedule.LifecycleOf"/>): a paused,
    /// spent or switched-off schedule starts nothing. <paramref name="runLengths"/> is each schedule's measured typical
    /// run length; a schedule absent from it is assumed to run for <see cref="UnmeasuredRunLength"/>.
    /// </summary>
    public static CronLoadDto Build(IEnumerable<CronJobDto> jobs, IReadOnlyDictionary<string, TimeSpan> runLengths,
        DateTime nowUtc, int capacity)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(runLengths);
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "capacity must be at least 1");
        var now = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);

        var active = jobs.Where(j => CronSchedule.LifecycleOf(j) == CronLifecycle.Active).ToList();
        var machines = active
            .GroupBy(j => j.Target.Machine, StringComparer.OrdinalIgnoreCase)
            .Select(g => MachineLoad(g.Key, g.ToList(), runLengths, now, capacity))
            .OrderByDescending(m => m.Peak)
            .ThenBy(m => m.Machine, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new CronLoadDto { GeneratedUtc = now, Capacity = capacity, Machines = machines };
    }

    private static CronMachineLoadDto MachineLoad(string machine, List<CronJobDto> jobs,
        IReadOnlyDictionary<string, TimeSpan> runLengths, DateTime now, int capacity)
    {
        // A schedule whose stored zone this host does not know cannot be placed in time. It is named rather than
        // failing the whole forecast, the way the list degrades one job's next run rather than every job's.
        var unplaced = jobs.Where(j => CronSchedule.FindZone(j.TimeZoneId) is null).Select(j => j.Id).ToList();
        var placed = jobs.Where(j => !unplaced.Contains(j.Id)).ToList();
        var zoneId = placed.Count == 0
            ? TimeZoneInfo.Utc.Id
            : placed
                .GroupBy(j => j.TimeZoneId, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .First().Key;
        var zone = placed.Count == 0 ? TimeZoneInfo.Utc : CronSchedule.FindZone(zoneId)!;

        var windowStart = HourStart(now, zone);
        var windowEnd = windowStart.AddHours(Hours);

        var runs = new List<(string JobId, DateTime Start, DateTime End)>();
        var estimated = new List<string>();
        foreach (var job in placed)
        {
            var measured = runLengths.TryGetValue(job.Id, out var length);
            if (!measured)
            {
                length = UnmeasuredRunLength;
                estimated.Add(job.Id);
            }
            if (length < TimeSpan.FromMinutes(1))
                length = TimeSpan.FromMinutes(1);
            runs.AddRange(FiresBetween(job, windowStart - length, windowEnd, now)
                .Select(start => (job.Id, start, start + length)));
        }

        var hours = new List<CronLoadHourDto>(Hours);
        for (var i = 0; i < Hours; i++)
        {
            var from = windowStart.AddHours(i);
            var to = from.AddHours(1);
            var inHour = runs.Where(r => r.Start < to && r.End > from).ToList();
            var concurrent = PeakOpen(inHour, from, to);
            hours.Add(new CronLoadHourDto
            {
                StartUtc = from,
                Label = TimeZoneInfo.ConvertTimeFromUtc(from, zone).ToString("HH:mm", CultureInfo.InvariantCulture),
                Concurrent = concurrent,
                Starts = inHour.Count(r => r.Start >= from),
                Over = concurrent > capacity,
                JobIds = inHour.Select(r => r.JobId).Distinct(StringComparer.Ordinal).ToList(),
            });
        }

        var peakHour = hours.OrderByDescending(h => h.Concurrent).ThenBy(h => h.StartUtc).First();
        var quietest = hours.OrderBy(h => h.Concurrent).ThenBy(h => h.Starts).ThenBy(h => h.StartUtc).First();
        var hoursOver = hours.Count(h => h.Over);
        var overText = hoursOver == 0
            ? $"never over {capacity}"
            : $"{hoursOver} {(hoursOver == 1 ? "hour" : "hours")} over {capacity}";

        return new CronMachineLoadDto
        {
            Machine = machine,
            TimeZoneId = zoneId,
            Schedules = jobs.Count,
            Peak = peakHour.Concurrent,
            HoursOver = hoursOver,
            Quietest = quietest,
            Summary = $"peak {peakHour.Concurrent} at {peakHour.Label} - {overText} - quietest {quietest.Label} ({quietest.Concurrent} open)",
            Hours = hours,
            EstimatedJobIds = estimated,
            EstimateNote = estimated.Count switch
            {
                0 => "",
                1 => $"1 schedule has never finished a run, so it is counted at {CronRunEndingFold.Duration(UnmeasuredRunLength)}",
                _ => $"{estimated.Count} schedules have never finished a run, so they are counted at {CronRunEndingFold.Duration(UnmeasuredRunLength)} each",
            },
            UnplacedJobIds = unplaced,
            UnplacedNote = unplaced.Count switch
            {
                0 => "",
                1 => "1 schedule is not counted: its time zone is not known on this host",
                _ => $"{unplaced.Count} schedules are not counted: their time zones are not known on this host",
            },
        };
    }

    /// <summary>The start of the hour <paramref name="now"/> is in, on the clock of <paramref name="zone"/>, in UTC.</summary>
    private static DateTime HourStart(DateTime now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(now, zone);
        var offset = zone.GetUtcOffset(now);
        var localHour = new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0, DateTimeKind.Unspecified);
        return DateTime.SpecifyKind(localHour - offset, DateTimeKind.Utc);
    }

    /// <summary>Every fire of the schedule at or after <paramref name="fromUtc"/> and before <paramref name="toUtc"/>.</summary>
    private static IEnumerable<DateTime> FiresBetween(CronJobDto job, DateTime fromUtc, DateTime toUtc, DateTime now)
    {
        if (CronSchedule.IsOneOff(job.ScheduleKind))
        {
            // An active one-off whose time has passed has not fired yet, and the engine fires it on its next sweep
            // (CronSchedule.LifecycleOf), so its session opens now, not at the time it was due.
            if (CronSchedule.ComputeNextRunUtc(job, now) is { } due)
            {
                var fire = due < now ? now : due;
                if (fire >= fromUtc && fire < toUtc)
                    yield return fire;
            }
            yield break;
        }

        // ComputeNextRunUtc is exclusive of its instant, so start a tick early to keep a fire exactly at fromUtc.
        var cursor = fromUtc.AddTicks(-1);
        // Nothing fires more than once a minute - a cron's finest step is a minute and a random schedule's least gap
        // is ten - so this bound is never what stops the loop; it only proves the loop ends.
        var bound = (int)Math.Ceiling((toUtc - fromUtc).TotalMinutes) + 2;
        for (var i = 0; i < bound; i++)
        {
            var next = CronSchedule.ComputeNextRunUtc(job, cursor);
            if (next is not { } fire || fire >= toUtc)
                yield break;
            if (fire <= cursor)
                yield break;
            if (fire >= fromUtc)
                yield return fire;
            cursor = fire;
        }
    }

    /// <summary>The most of these runs open at one moment inside [from, to). A run ending as another starts does not overlap it.</summary>
    private static int PeakOpen(List<(string JobId, DateTime Start, DateTime End)> runs, DateTime from, DateTime to)
    {
        var events = new List<(DateTime At, int Delta)>(runs.Count * 2);
        foreach (var r in runs)
        {
            events.Add((r.Start < from ? from : r.Start, +1));
            events.Add((r.End > to ? to : r.End, -1));
        }
        events.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.Delta.CompareTo(b.Delta));
        int open = 0, peak = 0;
        foreach (var (_, delta) in events)
        {
            open += delta;
            peak = Math.Max(peak, open);
        }
        return peak;
    }
}
