using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Licenses;

/// <summary>One past reading of one window: when, and how much was used.</summary>
public sealed record LicenseUsagePoint(DateTime AtUtc, double Utilization);

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
/// <param name="ExpectedAtThresholdUtc">When the window is expected to reach the threshold at that rate; null
/// when it is not expected to before it resets, or when there is no rate.</param>
public sealed record LicenseWindowForecast(
    string Window,
    double? Utilization,
    DateTime? ResetsAtUtc,
    double? PercentPerHour,
    DateTime? ExpectedAtThresholdUtc);

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
/// The rate is the change from the first to the last point of the window's history SINCE ITS LAST DROP: a
/// drop in use means the window reset (or the rolling 5-hour window let old use go), and use from before it
/// says nothing about the rate now. Fewer than two points after the drop, or no time between them, means no
/// rate. A rate of zero or less means the window is not expected to reach the threshold. A window expected to
/// reset before it reaches the threshold is not expected to reach it.
///
/// The next switch's target is the engine's own choice (<see cref="LicenseDecisionEngine.ChooseTarget"/>) on
/// TODAY'S numbers - the forecast does not project the other accounts forward, so the page labels it as the
/// target if the switch happened now.
/// </summary>
public static class LicenseForecaster
{

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
            var target = LicenseDecisionEngine.ChooseTarget(snapshot, source, LicenseDecisionEngine.SwitchableMachines(snapshot, source));
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
        var week = ForecastWindow(LicenseWindowNames.SevenDay, reading.SevenDay, history?.SevenDay, reading.ReadAtUtc, now, stale);

        var first = new[] { five, week }
            .Where(w => w.ExpectedAtThresholdUtc is not null)
            .OrderBy(w => w.ExpectedAtThresholdUtc)
            .FirstOrDefault();
        return new LicenseAccountForecast(Normalize(reading.AccountEmail), stale, five, week,
            first?.ExpectedAtThresholdUtc, first?.Window);
    }

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
            var at = readTime.AddHours((LicenseDecisionEngine.ThresholdPercent - reading.Utilization) / perHour);
            if (at < now)
                at = now;
            expected = reading.ResetsAtUtc is { } reset && reset <= at ? null : at;
        }
        else
            expected = null;

        return new LicenseWindowForecast(name, reading.Utilization, reading.ResetsAtUtc, rate, expected);
    }

    private static string Normalize(string email) => email.Trim().ToLowerInvariant();
}
