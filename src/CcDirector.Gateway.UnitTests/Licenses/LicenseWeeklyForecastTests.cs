using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Licenses;
using Xunit;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// THE WEEKLY FORECAST (the usage-by-session mandate): this week's average pace is the forecast, the pace over at least
/// the last 6 hours of kept readings is the early end of a range, a week is over only when its reset time changes, and the
/// forecast never says the weekly window does not run out while its average says it will.
///
/// Every account here is 96 hours into a week that resets 72 hours from now.
/// </summary>
public sealed class LicenseWeeklyForecastTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Reset = Now.AddDays(3);

    private const string A = "account-a@example.com";
    private const string B = "account-b@example.com";

    private static LicenseSnapshot Snapshot(double week) =>
        new(Now, new List<LicenseAccountReading>
        {
            new(A, new LicenseWindowReading(10, Now.AddHours(4)), new LicenseWindowReading(week, Reset), Now, true),
        }, new List<LicenseMachineState>
        {
            new("windows", "owner", A, new List<LicenseSpare>(), Now, false, null),
        }, [], []);

    private static LicenseWindowForecast Week(double week, params (double HoursAgo, double Utilization, DateTime Resets)[] kept)
    {
        var history = new LicenseAccountHistory(A, [],
            kept.Select(k => new LicenseUsagePoint(Now.AddHours(-k.HoursAgo), k.Utilization, k.Resets)).ToList());
        return Assert.Single(LicenseForecaster.Forecast(Snapshot(week), [history]).Accounts).SevenDay;
    }

    [Fact]
    public void TheAverageThisWeek_IsTheForecast_WhenThereIsNoRecentPace()
    {
        // 60% over 96 hours is 0.625% an hour: 37 points to go take 59.2 hours, before the reset.
        var week = Week(60);

        Assert.Equal(0.625, week.AveragePercentPerHour!.Value, 6);
        Assert.Null(week.RecentPercentPerHour);
        Assert.Equal(0.625, week.PercentPerHour!.Value, 6);
        Assert.Equal(Now.AddHours(59.2), week.ExpectedAtThresholdUtc!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(week.ExpectedAtThresholdUtc, week.AverageAtThresholdUtc);
    }

    [Fact]
    public void AFasterRecentPace_IsTheEarlyEndOfTheRange_AndTheAverageTheLateEnd()
    {
        // 48% six hours ago and 60% now is 2% an hour: 18.5 hours to 97%; the average still says 59.2.
        var week = Week(60, (6, 48, Reset), (0, 60, Reset));

        Assert.Equal(2.0, week.RecentPercentPerHour!.Value, 6);
        Assert.Equal(2.0, week.PercentPerHour!.Value, 6);
        Assert.Equal(Now.AddHours(18.5), week.ExpectedAtThresholdUtc!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(Now.AddHours(59.2), week.AverageAtThresholdUtc!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void AFlatLastSixHours_NeverSaysItDoesNotRunOut_WhileTheAverageSaysItWill()
    {
        var week = Week(60, (6, 60, Reset), (0, 60, Reset));

        Assert.Equal(0.0, week.RecentPercentPerHour);
        Assert.Equal(Now.AddHours(59.2), week.ExpectedAtThresholdUtc!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ARecentPace_NeedsSixHoursOfReadings()
    {
        var week = Week(60, (5.9, 48, Reset), (0, 60, Reset));

        Assert.Null(week.RecentPercentPerHour);
        Assert.Equal(0.625, week.PercentPerHour!.Value, 6);
    }

    [Fact]
    public void AFallingNumber_DoesNotEndTheWeek_OnlyAChangedResetTimeDoes()
    {
        // A misread 3 hours ago (40%) is inside the same week: the pace runs from the reading 7 hours ago.
        var sameWeek = Week(60, (7, 50, Reset), (3, 40, Reset), (0, 60, Reset));
        Assert.Equal(10.0 / 7, sameWeek.RecentPercentPerHour!.Value, 6);

        // The reading 7 hours ago is of the previous week (its reset time is a week earlier): no 6 hours of this week.
        var newWeek = Week(60, (7, 95, Reset.AddDays(-7)), (3, 50, Reset), (0, 60, Reset));
        Assert.Null(newWeek.RecentPercentPerHour);
    }

    [Fact]
    public void ResetTimesWithinAnHour_AreTheSameWeek()
    {
        var week = Week(60, (6, 48, Reset.AddMinutes(-40)), (0, 60, Reset));

        Assert.Equal(2.0, week.RecentPercentPerHour!.Value, 6);
    }

    [Fact]
    public void AnAverageThatDoesNotReachTheThresholdBeforeTheReset_ExpectsNothing()
    {
        // 20% over 96 hours: 77 points at 0.208 an hour is 370 hours, far past the reset.
        var week = Week(20);

        Assert.Null(week.ExpectedAtThresholdUtc);
        Assert.Null(week.AverageAtThresholdUtc);
        Assert.NotNull(week.AveragePercentPerHour);
    }

    [Fact]
    public void TheHistory_ServesTheWeeklySeriesFromTheKeptReadings_AndListsAnAccountOnlyTheyKnow()
    {
        var tenant = new TenantId("acct-a");
        var kept = new List<LicenseUsageReadingRecord>
        {
            new(B, Now.AddHours(-30), 70, Reset, 10, Now.AddHours(1)),
            new(B, Now.AddHours(-9), 80, Reset, 12, Now.AddHours(1)),
            new(B, Now.AddDays(-9), 5, Reset.AddDays(-7), 1, Now.AddDays(-9)),
        };
        DateTime? askedSince = null;
        var history = new LicenseUsageHistory((t, since) =>
        {
            askedSince = since;
            return kept.Where(r => r.AtUtc >= since).OrderBy(r => r.AtUtc).ToList();
        });

        var read = history.Read(tenant, Now);

        Assert.Equal(Now.AddDays(-8), askedSince);
        var b = Assert.Single(read);
        Assert.Equal(B, b.AccountEmail);
        Assert.Empty(b.FiveHour);
        Assert.Equal(new[] { new LicenseUsagePoint(Now.AddHours(-30), 70, Reset), new LicenseUsagePoint(Now.AddHours(-9), 80, Reset) }, b.SevenDay);
    }
}
