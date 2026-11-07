using CcDirector.Gateway.Licenses;
using Xunit;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// The owner's night, during which a repeating license warning is pushed only once the account is past 90% (mission
/// "Licenses - Always Ready, Loud When Not", step 2): from 22:00 to 07:00 in the account's own time zone.
/// </summary>
public sealed class LicenseOwnerNightTests
{
    [Theory]
    [InlineData(21, 59, false)]
    [InlineData(22, 0, true)]
    [InlineData(3, 0, true)]
    [InlineData(6, 59, true)]
    [InlineData(7, 0, false)]
    [InlineData(12, 0, false)]
    public void IsNight_InUtc_IsTenAtNightToSevenInTheMorning(int hour, int minute, bool night)
    {
        var utc = new DateTime(2026, 11, 6, hour, minute, 0, DateTimeKind.Utc);

        Assert.Equal(night, LicenseOwnerNight.IsNight(TimeZoneInfo.Utc, utc));
    }

    [Fact]
    public void IsNight_ReadsTheAccountsOwnTimeZone_NotUtc()
    {
        // 03:00 UTC is 22:00 the evening before in a zone five hours behind: night there.
        var zone = TimeZoneInfo.CreateCustomTimeZone("five-behind", TimeSpan.FromHours(-5), "five-behind", "five-behind");

        Assert.True(LicenseOwnerNight.IsNight(zone, new DateTime(2026, 11, 6, 3, 0, 0, DateTimeKind.Utc)));
        Assert.False(LicenseOwnerNight.IsNight(zone, new DateTime(2026, 11, 6, 1, 59, 0, DateTimeKind.Utc)));
    }
}
