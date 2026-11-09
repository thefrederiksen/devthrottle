using CcDirector.Gateway.Licenses;
using Xunit;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// THE PACE ON A LICENSE TILE (9 November 2026): "+3.4% an hour" while the weekly use is rising, from the forecast's own
/// weekly pace, and nothing at all when it is not rising or the forecast cannot say - a tile never shows "+0.0%".
/// </summary>
public sealed class LicenseTilePaceTests
{
    [Theory]
    [InlineData(3.4, "+3.4% an hour")]
    [InlineData(3.44, "+3.4% an hour")]
    [InlineData(12.0, "+12% an hour")]
    [InlineData(0.06, "+0.1% an hour")]
    public void PaceText_ARisingWeek_IsSaidPerHour(double perHour, string expected)
    {
        Assert.Equal(expected, LicenseStatusFold.PaceText(perHour));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(0.04)]
    [InlineData(-1.5)]
    public void PaceText_NoRiseOrNotKnown_IsNull(double? perHour)
    {
        Assert.Null(LicenseStatusFold.PaceText(perHour));
    }
}
