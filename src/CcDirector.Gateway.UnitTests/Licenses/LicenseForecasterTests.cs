using CcDirector.Gateway.Licenses;
using Xunit;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>The forecast: the rate from recent readings, the expected time to the threshold, and the next switch.</summary>
public sealed class LicenseForecasterTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 3, 0, 0, DateTimeKind.Utc);

    private const string A = "account-a@example.com";
    private const string B = "account-b@example.com";
    private const string C = "account-c@example.com";

    private static LicenseAccountReading Reading(string email, double fiveHour, double week, DateTime? fiveReset = null,
        DateTime? readAt = null, bool active = false) =>
        new(email, new LicenseWindowReading(fiveHour, fiveReset ?? Now.AddHours(4)), new LicenseWindowReading(week, Now.AddDays(3)),
            readAt ?? Now, active);

    private static LicenseMachineState Machine(string name, string active, params string[] spares) =>
        new(name, "owner", active, spares.Select(s => new LicenseSpare(s, null)).ToList(), Now, false, null);

    private static LicenseSnapshot Snapshot(IEnumerable<LicenseAccountReading> accounts, IEnumerable<LicenseMachineState> machines) =>
        new(Now, accounts.ToList(), machines.ToList(), [], []);

    private static List<LicenseUsagePoint> Series(params (int MinutesAgo, double Utilization)[] points) =>
        points.Select(p => new LicenseUsagePoint(Now.AddMinutes(-p.MinutesAgo), p.Utilization)).ToList();

    [Fact]
    public void RatePerHour_RisingSeries_IsFirstToLastOverTheHours()
    {
        Assert.Equal(10.0, LicenseForecaster.RatePerHour(Series((60, 50), (30, 54), (0, 60))));
    }

    [Fact]
    public void RatePerHour_UsesOnlyThePointsAfterTheLastDrop()
    {
        // 90 -> 2 is a reset; the rate is from 2 to 8 over the last half hour.
        Assert.Equal(12.0, LicenseForecaster.RatePerHour(Series((120, 80), (60, 90), (30, 2), (0, 8))));
    }

    [Fact]
    public void RatePerHour_FewerThanTwoPointsOrNoTime_IsNull()
    {
        Assert.Null(LicenseForecaster.RatePerHour(Series((0, 50))));
        Assert.Null(LicenseForecaster.RatePerHour(Series((5, 50), (5, 60))));
        Assert.Null(LicenseForecaster.RatePerHour(Series((60, 90), (0, 10))));
    }

    [Fact]
    public void Forecast_RisingWindow_ExpectsTheThresholdAtTheCurrentRate()
    {
        // 87% now, 10% an hour: 97% in one hour, before the 4-hour reset.
        var snapshot = Snapshot([Reading(A, 87, 30, active: true)], [Machine("windows", A)]);
        var history = new LicenseAccountHistory(A, Series((60, 77), (0, 87)), Series((60, 30), (0, 30)));

        var forecast = LicenseForecaster.Forecast(snapshot, [history]);

        var account = Assert.Single(forecast.Accounts);
        Assert.Equal(Now.AddHours(1), account.FiveHour.ExpectedAtThresholdUtc);
        Assert.Null(account.SevenDay.ExpectedAtThresholdUtc); // flat: never
        Assert.Equal((Now.AddHours(1), LicenseWindowNames.FiveHour), (account.ExpectedTiredAtUtc, account.ExpectedTiredWindow));
        Assert.Equal(Now.AddHours(4), account.FiveHour.ResetsAtUtc);
    }

    [Fact]
    public void Forecast_WindowThatResetsFirst_IsNotExpectedToReachTheThreshold()
    {
        // 87% at 10% an hour reaches 97% in an hour, but the window resets in 30 minutes.
        var snapshot = Snapshot([Reading(A, 87, 30, fiveReset: Now.AddMinutes(30), active: true)], [Machine("windows", A)]);
        var history = new LicenseAccountHistory(A, Series((60, 77), (0, 87)), []);

        var forecast = LicenseForecaster.Forecast(snapshot, [history]);

        Assert.Null(Assert.Single(forecast.Accounts).ExpectedTiredAtUtc);
        Assert.Null(forecast.NextSwitch);
    }

    [Fact]
    public void Forecast_AlreadyAtThreshold_IsExpectedNow()
    {
        var snapshot = Snapshot([Reading(A, 97, 30, active: true)], [Machine("windows", A)]);

        var forecast = LicenseForecaster.Forecast(snapshot, []);

        Assert.Equal(Now, Assert.Single(forecast.Accounts).ExpectedTiredAtUtc);
    }

    [Fact]
    public void Forecast_StaleReading_ForecastsNothing()
    {
        var snapshot = Snapshot([Reading(A, 90, 30, readAt: Now.AddMinutes(-7), active: true)], [Machine("windows", A)]);
        var history = new LicenseAccountHistory(A, Series((60, 80), (7, 90)), []);

        var account = Assert.Single(LicenseForecaster.Forecast(snapshot, [history]).Accounts);

        Assert.True(account.IsStale);
        Assert.Null(account.ExpectedTiredAtUtc);
    }

    [Fact]
    public void Forecast_NextSwitch_IsTheSoonestAccountInUseWithTheEngineTarget()
    {
        // A (in use) reaches 97% in 1 hour, C (in use on the Mac) in 2. B is not in use and would reach it
        // sooner, but a spare reaching the threshold switches nothing. The target for A is the engine's
        // choice on today's numbers: B, the lowest weekly use, held by the Windows machine.
        var snapshot = Snapshot(
            [Reading(A, 87, 40, active: true), Reading(B, 95, 10), Reading(C, 77, 50, active: true)],
            [Machine("windows", A, B, C), Machine("mac", C, A)]);
        var histories = new[]
        {
            new LicenseAccountHistory(A, Series((60, 77), (0, 87)), []),
            new LicenseAccountHistory(B, Series((60, 85), (0, 95)), []),
            new LicenseAccountHistory(C, Series((60, 67), (0, 77)), []),
        };

        var forecast = LicenseForecaster.Forecast(snapshot, histories);

        Assert.NotNull(forecast.NextSwitch);
        var next = forecast.NextSwitch;
        Assert.Equal((A, Now.AddHours(1), LicenseWindowNames.FiveHour, B),
            (next.FromAccountEmail, next.ExpectedAtUtc, next.Window, next.TargetAccountEmail));
    }

    [Fact]
    public void Forecast_NextSwitchWithNoAccountWithRoom_HasNoTarget()
    {
        var snapshot = Snapshot([Reading(A, 87, 40, active: true), Reading(B, 98, 10)], [Machine("windows", A, B)]);
        var history = new LicenseAccountHistory(A, Series((60, 77), (0, 87)), []);

        var next = LicenseForecaster.Forecast(snapshot, [history]).NextSwitch;
        Assert.NotNull(next);

        Assert.Equal(A, next.FromAccountEmail);
        Assert.Null(next.TargetAccountEmail);
    }
}
