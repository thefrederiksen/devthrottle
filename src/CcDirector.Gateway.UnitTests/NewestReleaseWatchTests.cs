using CcDirector.Gateway.Api;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The newest-release watch (fleet maintenance, devthrottle_internal#2020). A read never waits on the network, a
/// failure is reported rather than hidden, and a good answer is not refetched on every poll.
/// </summary>
public sealed class NewestReleaseWatchTests
{
    private DateTime _now = new(2026, 9, 15, 14, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Current_FirstRead_SaysNothingKnownThenServesTheVersion()
    {
        var watch = new NewestReleaseWatch(_ => Task.FromResult("2.1.4"), () => _now);

        var first = watch.Current();
        await watch.WaitForRefreshAsync();

        Assert.Null(first.Version);
        Assert.Null(first.Error);
        Assert.Equal("2.1.4", watch.Current().Version);
    }

    [Fact]
    public async Task Current_FailedRead_ReportsTheErrorAndInventsNoVersion()
    {
        var watch = new NewestReleaseWatch(_ => throw new HttpRequestException("rate limited"), () => _now);

        watch.Current();
        await watch.WaitForRefreshAsync();
        var snapshot = watch.Current();

        Assert.Null(snapshot.Version);
        Assert.Equal("rate limited", snapshot.Error);
    }

    [Fact]
    public async Task Current_UnreadableVersion_IsAnErrorNotAVersion()
    {
        var watch = new NewestReleaseWatch(_ => Task.FromResult("latest"), () => _now);

        watch.Current();
        await watch.WaitForRefreshAsync();
        var snapshot = watch.Current();

        Assert.Null(snapshot.Version);
        Assert.Contains("cannot be read", snapshot.Error);
    }

    [Fact]
    public async Task Current_GoodAnswer_IsNotRefetchedUntilAnHourHasPassed()
    {
        var calls = 0;
        var watch = new NewestReleaseWatch(_ => { calls++; return Task.FromResult("2.1.4"); }, () => _now);

        watch.Current();
        await watch.WaitForRefreshAsync();
        _now = _now.AddMinutes(59);
        watch.Current();
        await watch.WaitForRefreshAsync();
        Assert.Equal(1, calls);

        _now = _now.AddMinutes(2);
        watch.Current();
        await watch.WaitForRefreshAsync();
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Current_FailureAfterAGoodAnswer_KeepsTheVersionAndSaysTheCheckFailed()
    {
        var fail = false;
        var watch = new NewestReleaseWatch(_ => fail ? throw new HttpRequestException("offline") : Task.FromResult("2.1.4"), () => _now);

        watch.Current();
        await watch.WaitForRefreshAsync();
        fail = true;
        _now = _now.AddHours(2);
        watch.Current();
        await watch.WaitForRefreshAsync();
        var snapshot = watch.Current();

        Assert.Equal("2.1.4", snapshot.Version);
        Assert.Equal("offline", snapshot.Error);
    }
}
