using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CcDirector.Gateway;

/// <summary>
/// The settings of a <c>random</c> schedule (issue #3622): about <see cref="PerDay"/> fires a day at random
/// times inside a daily local window, never closer than <see cref="MinGapMinutes"/>, spread by an hourly
/// <see cref="Shape"/> so the times follow a human rhythm rather than a flat spread.
///
/// Stored as text in the job's existing <c>CronExpression</c> column, e.g.
/// <c>window=07:00-01:00 perDay=4 minGap=45 shape=human</c>, so the kind needs no schema change.
/// </summary>
public sealed record RandomScheduleSettings(
    int WindowStartMinute,
    int WindowMinutes,
    int PerDay,
    int MinGapMinutes,
    IReadOnlyList<double> Shape,
    bool ShapeIsHuman)
{
    /// <summary>The window's end as a local minute of the day (0-1439).</summary>
    public int WindowEndMinute => (WindowStartMinute + WindowMinutes) % RandomSchedule.MinutesPerDay;

    /// <summary>The settings written back as the stored text, in the canonical key order.</summary>
    public string ToText()
    {
        var shape = ShapeIsHuman
            ? RandomSchedule.HumanShapeName
            : string.Join(",", Shape.Select(w => w.ToString("0.###", CultureInfo.InvariantCulture)));
        return $"window={RandomSchedule.Hhmm(WindowStartMinute)}-{RandomSchedule.Hhmm(WindowEndMinute)} " +
               $"perDay={PerDay} minGap={MinGapMinutes} shape={shape}";
    }
}

/// <summary>
/// The plan of a <c>random</c> schedule (issue #3622). A day's fire times are a pure function of the job id,
/// the local date the window opens and the settings - nothing random is stored - so a Gateway restart, a store
/// reload or a redeploy gives exactly the same times, and a test can pin a whole week.
///
/// The algorithm is the reference model in devthrottle_internal
/// (<c>docs/marketing/reddit/autonomy/plan_model.py</c>), ported step for step, but on its own seeded generator
/// (SplitMix64) so the numbers never depend on how <see cref="Random"/> behaves in a given .NET version:
///   seed   first 8 bytes of SHA-256("&lt;jobId&gt;|&lt;yyyy-MM-dd&gt;"), big-endian
///   count  perDay + uniform(-perDay/2 .. +perDay/2), clamped to [1, slots / k]
///   slots  the window cut into 15-minute slots, each weighing what the shape gives its hour
///   pick   count slots by weighted draw without replacement; each pick removes every slot closer than
///          k = ceil((minGap + 14) / 15), so the gap holds by construction
///   minute uniform inside each picked slot, drawn in time order after all the picks
/// </summary>
public static class RandomSchedule
{
    public const int MinutesPerDay = 1440;
    public const int SlotMinutes = 15;
    public const string HumanShapeName = "human";

    public const int MinWindowMinutes = 60;
    public const int MinPerDay = 1;
    public const int MaxPerDay = 24;
    public const int MinGap = 10;
    public const int MaxGap = 240;

    /// <summary>The largest custom hourly weight; it keeps the sum of every slot's weight far from overflowing.</summary>
    public const double MaxShapeWeight = 1_000_000;

    // Minutes a spring-forward change can take out of a night: one hour, in every zone with daylight saving we serve.
    private const int DaylightSavingAllowance = 60;

    /// <summary>
    /// The built-in <c>human</c> shape: the owner's own Reddit posting hours (119 comments and posts,
    /// 2026-02-15 to 2026-06-06, America/Toronto, read before any automation), smoothed over three hours with
    /// a floor of 1 so no waking hour is impossible. Index = local hour 00..23.
    /// </summary>
    public static readonly IReadOnlyList<double> HumanShape = new[]
    {
        1, 1, 1, 1, 4.25, 6.5, 4.25, 2.25, 4.75, 9.25, 10.75, 7,
        2.75, 1, 1.5, 7.25, 14.75, 16.5, 12.25, 5.75, 2, 1.5, 1.5, 1,
    };

    private static readonly Regex WindowPattern =
        new(@"^(\d{2}):(\d{2})-(\d{2}):(\d{2})$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Parse and validate the stored settings text. Returns the settings, or one human-readable reason per
    /// failure, suitable for a 400.
    /// </summary>
    public static (RandomScheduleSettings? Settings, string? Error) Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, "cronExpression must hold the random settings, e.g. 'window=07:00-01:00 perDay=4 minGap=45'");

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = token.IndexOf('=');
            if (eq <= 0 || eq == token.Length - 1)
                return (null, $"random setting '{token}' is not key=value");
            var key = token[..eq];
            if (!IsKnownKey(key))
                return (null, $"unknown random setting '{key}'; the settings are window, perDay, minGap and shape");
            if (!values.TryAdd(key, token[(eq + 1)..]))
                return (null, $"random setting '{key}' is given twice");
        }

        if (!values.TryGetValue("window", out var windowText))
            return (null, "window is required for a random schedule, e.g. window=07:00-01:00");
        var window = WindowPattern.Match(windowText);
        if (!window.Success)
            return (null, $"window must be HH:mm-HH:mm, not '{windowText}'");
        var startH = int.Parse(window.Groups[1].Value, CultureInfo.InvariantCulture);
        var startM = int.Parse(window.Groups[2].Value, CultureInfo.InvariantCulture);
        var endH = int.Parse(window.Groups[3].Value, CultureInfo.InvariantCulture);
        var endM = int.Parse(window.Groups[4].Value, CultureInfo.InvariantCulture);
        if (startH > 23 || endH > 23 || startM > 59 || endM > 59)
            return (null, $"window must be HH:mm-HH:mm with real times of day, not '{windowText}'");
        var start = startH * 60 + startM;
        var end = endH * 60 + endM;
        // An end at or before the start crosses midnight; an end equal to the start is a whole day.
        var windowMinutes = end > start ? end - start : end - start + MinutesPerDay;
        if (windowMinutes < MinWindowMinutes)
            return (null, $"window must be at least {MinWindowMinutes} minutes long, not {windowMinutes}");

        if (!values.TryGetValue("perDay", out var perDayText))
            return (null, "perDay is required for a random schedule");
        if (!int.TryParse(perDayText, NumberStyles.None, CultureInfo.InvariantCulture, out var perDay)
            || perDay < MinPerDay || perDay > MaxPerDay)
            return (null, $"perDay must be a whole number from {MinPerDay} to {MaxPerDay}, not '{perDayText}'");

        if (!values.TryGetValue("minGap", out var gapText))
            return (null, "minGap is required for a random schedule");
        if (!int.TryParse(gapText, NumberStyles.None, CultureInfo.InvariantCulture, out var gap)
            || gap < MinGap || gap > MaxGap)
            return (null, $"minGap must be a whole number of minutes from {MinGap} to {MaxGap}, not '{gapText}'");

        IReadOnlyList<double> shape = HumanShape;
        var isHuman = true;
        if (values.TryGetValue("shape", out var shapeText)
            && !string.Equals(shapeText, HumanShapeName, StringComparison.OrdinalIgnoreCase))
        {
            var parts = shapeText.Split(',');
            if (parts.Length != 24)
                return (null, $"shape must be '{HumanShapeName}' or 24 comma-separated hourly weights, not {parts.Length} values");
            var weights = new double[24];
            for (var h = 0; h < 24; h++)
            {
                if (!double.TryParse(parts[h], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var w)
                    || !double.IsFinite(w) || w <= 0 || w > MaxShapeWeight)
                    return (null, $"shape weight for hour {h:00} must be a positive number no larger than {MaxShapeWeight:0}, not '{parts[h]}'");
                weights[h] = w;
            }
            shape = weights;
            isHuman = false;
        }

        // The window must hold twice perDay fires at the gap. That is stricter than the largest day's count
        // (perDay * 1.5) needs, so the weighted draw can never run out of slots - no retry, nothing to fall back to.
        var slots = windowMinutes / SlotMinutes;
        var k = SlotsBetweenPicks(gap);
        if (2 * perDay * (2 * k - 1) > slots)
            return (null, $"window {windowText} is too short for {2 * perDay} fires (twice perDay) at least {gap} minutes apart; " +
                          "widen the window, lower perDay or lower minGap");

        // The gap must hold across days too: the last fire of one window and the first of the next, even on the
        // night a spring-forward change takes an hour out of the time between them.
        if (MinutesPerDay + 1 - slots * SlotMinutes - DaylightSavingAllowance < gap)
            return (null, $"window {windowText} leaves less than minGap ({gap} minutes) between one day's window and the next");

        return (new RandomScheduleSettings(start, windowMinutes, perDay, gap, shape, isHuman), null);
    }

    /// <summary>
    /// The planned fires of the window that opens on <paramref name="windowDate"/>, as local minutes counted
    /// from that date's midnight (so a window crossing midnight yields minutes of 1440 and above), in order.
    ///
    /// Every distance is judged in REAL time, not on the clock face. A slot holding a local minute that does not
    /// exist that day (a spring-forward change) is dead before the draw, so no planned time is ever moved; and a
    /// pick removes every slot whose minutes could come closer than the gap in UTC. On a day with no change this
    /// is exactly the reference model: two slots k apart are at least 15k - 14 minutes apart.
    /// </summary>
    public static IReadOnlyList<int> PlanMinutes(
        string jobId, DateOnly windowDate, RandomScheduleSettings settings, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(zone);

        var rng = new SplitMix64(Seed(jobId, windowDate));
        var slotCount = settings.WindowMinutes / SlotMinutes;
        var weights = new double[slotCount];
        var alive = new bool[slotCount];
        var firstUtc = new DateTime[slotCount];
        var lastUtc = new DateTime[slotCount];
        for (var s = 0; s < slotCount; s++)
        {
            var slotStart = settings.WindowStartMinute + s * SlotMinutes;
            weights[s] = settings.Shape[slotStart / 60 % 24];
            alive[s] = Enumerable.Range(0, SlotMinutes).All(m => !zone.IsInvalidTime(Local(windowDate, slotStart + m)));
            if (!alive[s]) continue;
            // UTC rises minute by minute through a slot whose minutes all exist, so its two ends bound it.
            firstUtc[s] = LocalToUtc(windowDate, slotStart, zone);
            lastUtc[s] = LocalToUtc(windowDate, slotStart + SlotMinutes - 1, zone);
        }

        var k = SlotsBetweenPicks(settings.MinGapMinutes);
        var spread = settings.PerDay / 2;
        var count = settings.PerDay + rng.NextInclusive(-spread, spread);
        count = Math.Max(1, Math.Min(count, slotCount / k));
        var gap = TimeSpan.FromMinutes(settings.MinGapMinutes);

        var picks = new List<int>(count);
        for (var i = 0; i < count; i++)
        {
            var total = 0.0;
            var lastAlive = -1;
            for (var s = 0; s < slotCount; s++)
            {
                if (!alive[s]) continue;
                total += weights[s];
                lastAlive = s;
            }
            if (lastAlive < 0)
                throw new InvalidOperationException(
                    $"random schedule plan ran out of slots for job {jobId} on {windowDate:yyyy-MM-dd}; " +
                    "validation leaves room for every pick and one skipped daylight-saving hour, so either the " +
                    "settings bypassed RandomSchedule.Parse or the zone skipped more than an hour");

            var r = rng.NextDouble() * total;
            var pick = lastAlive;   // rounding can leave r a hair above zero after the last slot
            for (var s = 0; s < slotCount; s++)
            {
                if (!alive[s]) continue;
                r -= weights[s];
                if (r <= 0) { pick = s; break; }
            }
            picks.Add(pick);
            for (var t = 0; t < slotCount; t++)
            {
                if (!alive[t]) continue;
                var tooClose = t == pick
                    || (t > pick && firstUtc[t] - lastUtc[pick] < gap)
                    || (t < pick && firstUtc[pick] - lastUtc[t] < gap);
                if (tooClose) alive[t] = false;
            }
        }

        picks.Sort();
        var minutes = new List<int>(picks.Count);
        foreach (var slot in picks)
            minutes.Add(settings.WindowStartMinute + slot * SlotMinutes + rng.NextBelow(SlotMinutes));
        return minutes;
    }

    /// <summary>
    /// The planned fires of the window that opens on <paramref name="windowDate"/> as UTC instants, in order. No
    /// planned time falls on a local minute that does not exist (see <see cref="PlanMinutes"/>); a time in the
    /// hour a fall-back change repeats uses the earlier of its two instants.
    /// </summary>
    public static IReadOnlyList<DateTime> PlanUtc(
        string jobId, DateOnly windowDate, RandomScheduleSettings settings, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return PlanMinutes(jobId, windowDate, settings, zone).Select(m => LocalToUtc(windowDate, m, zone)).ToList();
    }

    /// <summary>
    /// The earliest planned instant strictly after <paramref name="fromUtc"/>. Scans the window that opened the
    /// local day before (a window crossing midnight), that day, and the next two days; every window holds at
    /// least one fire, so the answer is always found.
    /// </summary>
    public static DateTime NextAfter(
        string jobId, RandomScheduleSettings settings, TimeZoneInfo zone, DateTime fromUtc)
    {
        var from = DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc);
        var localDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(from, zone));
        for (var d = -1; d <= 2; d++)
        {
            foreach (var utc in PlanUtc(jobId, localDay.AddDays(d), settings, zone))
            {
                if (utc > from)
                    return utc;
            }
        }
        throw new InvalidOperationException(
            $"random schedule for job {jobId} has no planned fire within two days of {from:o}; every window holds at least one");
    }

    /// <summary>The words for the schedule: "About 4 times a day at random, 07:00 to 01:00 (at least 45 min apart)".</summary>
    public static string Describe(RandomScheduleSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var times = settings.PerDay == 1 ? "once" : $"{settings.PerDay} times";
        var text = $"About {times} a day at random, {Hhmm(settings.WindowStartMinute)} to {Hhmm(settings.WindowEndMinute)} " +
                   $"(at least {settings.MinGapMinutes} min apart)";
        return settings.ShapeIsHuman ? text : text + " (custom hourly shape)";
    }

    internal static string Hhmm(int minute)
    {
        var m = ((minute % MinutesPerDay) + MinutesPerDay) % MinutesPerDay;
        return $"{m / 60:00}:{m % 60:00}";
    }

    // Slots that must separate two picks for every minute in one to be at least minGap from every minute in the
    // other: two picks k slots apart are at least 15k - 14 minutes apart.
    private static int SlotsBetweenPicks(int minGap) => (minGap + SlotMinutes - 1 + SlotMinutes - 1) / SlotMinutes;

    private static bool IsKnownKey(string key) =>
        key.Equals("window", StringComparison.OrdinalIgnoreCase)
        || key.Equals("perDay", StringComparison.OrdinalIgnoreCase)
        || key.Equals("minGap", StringComparison.OrdinalIgnoreCase)
        || key.Equals("shape", StringComparison.OrdinalIgnoreCase);

    private static ulong Seed(string jobId, DateOnly windowDate)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{jobId}|{windowDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"));
        return BinaryPrimitives.ReadUInt64BigEndian(bytes);
    }

    private static DateTime Local(DateOnly date, int minute) =>
        DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue).AddMinutes(minute), DateTimeKind.Unspecified);

    private static DateTime LocalToUtc(DateOnly date, int minute, TimeZoneInfo zone)
    {
        var local = Local(date, minute);
        if (zone.IsInvalidTime(local))
            throw new InvalidOperationException(
                $"local time {local:yyyy-MM-dd HH:mm} does not exist in {zone.Id}; the plan never picks such a minute");
        if (zone.IsAmbiguousTime(local))
        {
            // The earlier instant is the one with the larger offset from UTC (daylight time, before falling back).
            var offset = zone.GetAmbiguousTimeOffsets(local).Max();
            return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        }
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    /// <summary>SplitMix64: a tiny, fixed, well-mixed generator, so a plan never depends on the runtime's own.</summary>
    private sealed class SplitMix64
    {
        private ulong _state;

        public SplitMix64(ulong seed) => _state = seed;

        public ulong Next()
        {
            unchecked
            {
                _state += 0x9E3779B97F4A7C15UL;
                var z = _state;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        /// <summary>A double in [0, 1) from the top 53 bits.</summary>
        public double NextDouble() => (Next() >> 11) * (1.0 / (1UL << 53));

        /// <summary>A whole number in [0, n), unbiased.</summary>
        public int NextBelow(int n)
        {
            var bound = (ulong)n;
            var threshold = unchecked(0UL - bound) % bound;
            while (true)
            {
                var r = Next();
                if (r >= threshold)
                    return (int)(r % bound);
            }
        }

        /// <summary>A whole number in [low, high], unbiased.</summary>
        public int NextInclusive(int low, int high) => low + NextBelow(high - low + 1);
    }
}
