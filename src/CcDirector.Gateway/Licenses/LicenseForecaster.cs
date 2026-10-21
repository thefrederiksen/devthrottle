using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Licenses;

/// <summary>One past reading of one window: when, how much was used, and when that window resets (null when not
/// known). The reset time is what says two readings are of the same week.</summary>
public sealed record LicenseUsagePoint(DateTime AtUtc, double Utilization, DateTime? ResetsAtUtc = null);

/// <summary>Recent readings of one account, per window, in any order. The store keeps a short series per account.</summary>
public sealed record LicenseAccountHistory(
    string AccountEmail,
    IReadOnlyList<LicenseUsagePoint> FiveHour,
    IReadOnlyList<LicenseUsagePoint> SevenDay);

/// <summary>The forecast for one window of one account.</summary>
/// <param name="Window"><see cref="LicenseWindowNames"/>.</param>
/// <param name="Utilization">The freshest reading, or null when the window is unknown.</param>
/// <param name="ResetsAtUtc">When the window resets, or null when not known.</param>
/// <param name="PercentPerHour">The current rate of use, or null when the history cannot show one.</param>
/// <param name="ExpectedAtThresholdUtc">When the window is expected to reach the threshold - the EARLY end of the
/// forecast; null when it is not expected to before it resets, or when there is no rate.</param>
/// <param name="AveragePercentPerHour">The weekly window only: its use so far over the hours since the week began - the
/// primary pace. Null for the 5-hour window and when it cannot be told.</param>
/// <param name="RecentPercentPerHour">The weekly window only: its pace over at least the last
/// <see cref="LicenseForecaster.RecentPaceSpan"/> of kept readings of this week. Null when there are not that many.</param>
/// <param name="AverageAtThresholdUtc">The weekly window only: when it reaches the threshold at its average pace - the
/// LATE end of the forecast when the recent pace is faster. Null when the average pace does not reach it before the reset.</param>
public sealed record LicenseWindowForecast(
    string Window,
    double? Utilization,
    DateTime? ResetsAtUtc,
    double? PercentPerHour,
    DateTime? ExpectedAtThresholdUtc,
    double? AveragePercentPerHour = null,
    double? RecentPercentPerHour = null,
    DateTime? AverageAtThresholdUtc = null);

/// <summary>The forecast for one account.</summary>
/// <param name="IsStale">The freshest reading is stale, so no time to the threshold is forecast.</param>
/// <param name="ExpectedTiredAtUtc">The earlier of the two windows' expected times, or null.</param>
/// <param name="ExpectedTiredWindow">Which window that is, or null.</param>
public sealed record LicenseAccountForecast(
    string AccountEmail,
    bool IsStale,
    LicenseWindowForecast FiveHour,
    LicenseWindowForecast SevenDay,
    DateTime? ExpectedTiredAtUtc,
    string? ExpectedTiredWindow);

/// <summary>The next switch the forecast expects.</summary>
/// <param name="FromAccountEmail">The account in use expected to reach the threshold first.</param>
/// <param name="ExpectedAtUtc">When.</param>
/// <param name="Window">Which window will reach it.</param>
/// <param name="TargetAccountEmail">The target the engine would choose on today's numbers; null when none
/// would qualify, which is when the owner would get the no-room email.</param>
public sealed record LicenseNextSwitch(string FromAccountEmail, DateTime ExpectedAtUtc, string Window, string? TargetAccountEmail);

/// <summary>The whole forecast for one tenant. Each holding's sign-in expiry verdict is the read model's
/// (<see cref="LicenseSignInExpiry"/>), not repeated here.</summary>
public sealed record LicenseForecast(IReadOnlyList<LicenseAccountForecast> Accounts, LicenseNextSwitch? NextSwitch);

/// <summary>
/// The forecast, as pure code: per account, its reset times and the expected time to the threshold at the
/// current rate; from those, the next switch and its target.
///
/// THE 5-HOUR WINDOW'S rate is the change from the first to the last point of its history SINCE ITS LAST DROP: the
/// rolling window lets old use go, and use from before a drop says nothing about the rate now. Fewer than two points
/// after the drop, or no time between them, means no rate.
///
/// THE WEEKLY WINDOW'S PRIMARY PACE IS ITS AVERAGE THIS WEEK (the usage-by-session mandate, 2026-10-20): its use so far
/// over the hours since the week began (its reset time less seven days). A two-hour rate in memory, reset by any one
/// point of drop, made the forecast jump from "17 hours" to "does not run out"; the average cannot. Its RECENT pace,
/// over at least <see cref="RecentPaceSpan"/> of kept readings of the same week, is the early end of a range: when it is
/// faster, the window is expected at the threshold at the recent pace, and at the average pace at the latest. A week is
/// over only when its reset time changes - never because a number fell. So the forecast never says the weekly window does
/// not run out while its average says it will.
///
/// A rate of zero or less means the window is not expected to reach the threshold. A window expected to reset before it
/// reaches the threshold is not expected to reach it.
///
/// The next switch's target is the engine's own choice (<see cref="LicenseDecisionEngine.NextTarget"/>), chosen on
/// the account's merits and named whenever an account has room, on TODAY'S numbers - the forecast does not project the other accounts forward, so the page labels it as the
/// target if the switch happened now.
/// </summary>
public static class LicenseForecaster
{
    /// <summary>The shortest span the weekly window's recent pace is measured over.</summary>
    public static readonly TimeSpan RecentPaceSpan = TimeSpan.FromHours(6);

    /// <summary>Two readings whose reset times are within this of each other are of the same week.</summary>
    public static readonly TimeSpan SameWeekTolerance = TimeSpan.FromHours(1);

    /// <summary>Forecast every account in <paramref name="snapshot"/>, using <paramref name="histories"/> for the rates.</summary>
    public static LicenseForecast Forecast(LicenseSnapshot snapshot, IReadOnlyList<LicenseAccountHistory> histories)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(histories);
        FileLog.Write($"[LicenseForecaster] Forecast: accounts={snapshot.Accounts.Count}, histories={histories.Count}");

        var historyByAccount = histories.ToDictionary(h => Normalize(h.AccountEmail), StringComparer.Ordinal);
        var now = snapshot.NowUtc;
        var accounts = snapshot.Accounts
            .OrderBy(r => Normalize(r.AccountEmail), StringComparer.Ordinal)
            .Select(reading =>
            {
                historyByAccount.TryGetValue(Normalize(reading.AccountEmail), out var history);
                return ForecastAccount(reading, history, now);
            })
            .ToList();

        LicenseNextSwitch? next = null;
        var inUse = LicenseDecisionEngine.AccountsInUse(snapshot);
        var soonest = accounts
            .Where(a => a.ExpectedTiredAtUtc is not null && inUse.Contains(Normalize(a.AccountEmail)))
            .OrderBy(a => a.ExpectedTiredAtUtc)
            .ThenBy(a => Normalize(a.AccountEmail), StringComparer.Ordinal)
            .FirstOrDefault();
        if (soonest is { ExpectedTiredAtUtc: { } at, ExpectedTiredWindow: { } window })
        {
            var source = Normalize(soonest.AccountEmail);
            var target = LicenseDecisionEngine.NextTarget(snapshot, source);
            next = new LicenseNextSwitch(source, at, window, target);
        }

        FileLog.Write($"[LicenseForecaster] Forecast: nextSwitch={(next is null ? "none" : next.ExpectedAtUtc.ToString("o"))}");
        return new LicenseForecast(accounts, next);
    }

    /// <summary>
    /// The current rate of use in percent per hour from <paramref name="points"/>, or null when it cannot be
    /// shown. Uses only the points after the last drop.
    /// </summary>
    public static double? RatePerHour(IReadOnlyList<LicenseUsagePoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var ordered = points.OrderBy(p => p.AtUtc).ToList();
        // Any fall, however small, is read as a reset: the provider's use only falls at one. If it ever
        // reports rounding noise (50.0 then 49.9), the rate simply restarts from that point.
        var start = 0;
        for (var i = 1; i < ordered.Count; i++)
            if (ordered[i].Utilization < ordered[i - 1].Utilization)
                start = i;
        var run = ordered.Skip(start).ToList();
        if (run.Count < 2)
            return null;
        var hours = (run[^1].AtUtc - run[0].AtUtc).TotalHours;
        if (hours <= 0)
            return null;
        return (run[^1].Utilization - run[0].Utilization) / hours;
    }

    private static LicenseAccountForecast ForecastAccount(LicenseAccountReading reading, LicenseAccountHistory? history, DateTime now)
    {
        var stale = LicenseDecisionEngine.IsReadingStale(reading, now);
        var five = ForecastWindow(LicenseWindowNames.FiveHour, reading.FiveHour, history?.FiveHour, reading.ReadAtUtc, now, stale);
        var week = ForecastWeek(reading.SevenDay, history?.SevenDay, reading.ReadAtUtc, now, stale);

        var first = new[] { five, week }
            .Where(w => w.ExpectedAtThresholdUtc is not null)
            .OrderBy(w => w.ExpectedAtThresholdUtc)
            .FirstOrDefault();
        return new LicenseAccountForecast(Normalize(reading.AccountEmail), stale, five, week,
            first?.ExpectedAtThresholdUtc, first?.Window);
    }

    /// <summary>
    /// The weekly window's average pace this week: its use so far over the hours since the week began. Null when there is
    /// no reset time, it is under an hour into the week, or nothing is used yet.
    /// </summary>
    public static double? AveragePacePerHour(LicenseWindowReading week, DateTime readAtUtc)
    {
        ArgumentNullException.ThrowIfNull(week);
        if (week.ResetsAtUtc is not { } reset || week.Utilization <= 0)
            return null;
        var hoursIn = (readAtUtc - (AsUtc(reset) - LicenseDecisionEngine.SevenDayWindowLength)).TotalHours;
        return hoursIn < 1 ? null : week.Utilization / hoursIn;
    }

    /// <summary>
    /// The weekly window's recent pace, from kept readings in any order: from the latest reading of the week
    /// <paramref name="resetsAtUtc"/> ends, back to the latest reading of the same week at least
    /// <see cref="RecentPaceSpan"/> before it. Null when the week has no reading that old.
    /// </summary>
    public static double? RecentPacePerHour(IReadOnlyList<LicenseUsagePoint> points, DateTime? resetsAtUtc)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (resetsAtUtc is not { } reset)
            return null;
        var week = points
            .Where(p => p.ResetsAtUtc is { } r && (AsUtc(r) - AsUtc(reset)).Duration() <= SameWeekTolerance)
            .OrderBy(p => p.AtUtc)
            .ToList();
        if (week.Count < 2)
            return null;
        var latest = week[^1];
        var from = week.LastOrDefault(p => latest.AtUtc - p.AtUtc >= RecentPaceSpan);
        if (from is null)
            return null;
        return Math.Max(0, (latest.Utilization - from.Utilization) / (latest.AtUtc - from.AtUtc).TotalHours);
    }

    private static LicenseWindowForecast ForecastWeek(LicenseWindowReading? reading, IReadOnlyList<LicenseUsagePoint>? history,
        DateTime? readAt, DateTime now, bool stale)
    {
        if (reading is null)
            return new LicenseWindowForecast(LicenseWindowNames.SevenDay, null, null, null, null);

        var average = readAt is { } read ? AveragePacePerHour(reading, AsUtc(read)) : null;
        var recent = history is null ? null : RecentPacePerHour(history, reading.ResetsAtUtc);
        // The pace that sets the expected time: the faster of the two, so the rate shown and the time shown agree.
        double? primary = (average, recent) switch
        {
            ({ } a, { } r) => Math.Max(a, r),
            _ => average ?? recent,
        };
        if (stale || readAt is not { } readTime)
            return new LicenseWindowForecast(LicenseWindowNames.SevenDay, reading.Utilization, reading.ResetsAtUtc, primary, null, average, recent);

        // Read before the reset: it says nothing about the window now.
        if (reading.ResetsAtUtc is { } passed && passed <= now)
            return new LicenseWindowForecast(LicenseWindowNames.SevenDay, reading.Utilization, reading.ResetsAtUtc, primary, null, average, recent);
        if (LicenseDecisionEngine.IsTired(reading, now))
            return new LicenseWindowForecast(LicenseWindowNames.SevenDay, reading.Utilization, reading.ResetsAtUtc, primary, now, average, recent, now);

        var atAverage = TimeAtThreshold(reading, average, readTime, now);
        var atRecent = TimeAtThreshold(reading, recent, readTime, now);
        DateTime? expected = (atAverage, atRecent) switch
        {
            ({ } a, { } r) => a < r ? a : r,
            ({ } a, null) => a,
            (null, { } r) => r,
            _ => null,
        };
        return new LicenseWindowForecast(LicenseWindowNames.SevenDay, reading.Utilization, reading.ResetsAtUtc, primary, expected,
            average, recent, atAverage);
    }

    // When the window reaches the threshold at the given pace; null for no pace, a pace of nothing, longer than a week
    // (no window lasts longer, and it keeps the date inside the calendar), or a reset that comes first.
    private static DateTime? TimeAtThreshold(LicenseWindowReading reading, double? perHour, DateTime readTime, DateTime now)
    {
        if (perHour is not { } rate || rate <= 0)
            return null;
        var hours = (LicenseDecisionEngine.ThresholdPercent - reading.Utilization) / rate;
        if (double.IsNaN(hours) || hours > LicenseDecisionEngine.SevenDayWindowLength.TotalHours)
            return null;
        var at = readTime.AddHours(hours);
        if (at < now)
            at = now;
        return reading.ResetsAtUtc is { } reset && reset <= at ? null : at;
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value.ToUniversalTime(),
    };

    private static LicenseWindowForecast ForecastWindow(string name, LicenseWindowReading? reading,
        IReadOnlyList<LicenseUsagePoint>? history, DateTime? readAt, DateTime now, bool stale)
    {
        if (reading is null)
            return new LicenseWindowForecast(name, null, null, null, null);

        var rate = history is null ? null : RatePerHour(history);
        if (stale || readAt is not { } readTime)
            return new LicenseWindowForecast(name, reading.Utilization, reading.ResetsAtUtc, rate, null);

        DateTime? expected;
        if (reading.ResetsAtUtc is { } passed && passed <= now)
            // Read before the reset: it says nothing about the window now. The checks below would give null
            // here too, so this line states the intent rather than changing a result.
            expected = null;
        else if (LicenseDecisionEngine.IsTired(reading, now))
            expected = now;
        else if (rate is { } perHour && perHour > 0)
        {
            // No window lasts longer than the weekly one, so a window that needs longer than that to reach the
            // threshold resets first: it is not expected to reach it. Deciding that on the number of hours, before
            // any date is worked out, is also what keeps a rate of almost nothing from asking for a date past the
            // end of the calendar.
            var hours = (LicenseDecisionEngine.ThresholdPercent - reading.Utilization) / perHour;
            if (double.IsNaN(hours) || hours > LicenseDecisionEngine.SevenDayWindowLength.TotalHours)
                expected = null;
            else
            {
                var at = readTime.AddHours(hours);
                if (at < now)
                    at = now;
                expected = reading.ResetsAtUtc is { } reset && reset <= at ? null : at;
            }
        }
        else
            expected = null;

        return new LicenseWindowForecast(name, reading.Utilization, reading.ResetsAtUtc, rate, expected);
    }

    private static string Normalize(string email) => email.Trim().ToLowerInvariant();
}
