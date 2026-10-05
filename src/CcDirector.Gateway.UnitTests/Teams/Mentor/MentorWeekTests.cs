using CcDirector.Gateway.Teams.Mentor;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams.Mentor;

/// <summary>The ISO week the Mentor's page names, cut in the team's own time zone (devthrottle_internal#2305).</summary>
public sealed class MentorWeekTests
{
    [Theory]
    [InlineData("2026-W40", 2026, 40)]
    [InlineData(" 2026-W01 ", 2026, 1)]
    [InlineData("2026-W53", 2026, 53)]
    public void TryParse_AnIsoWeek_Parses(string text, int year, int week)
    {
        Assert.Equal(new MentorWeek(year, week), MentorWeek.TryParse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-40")]
    [InlineData("2026-W4")]
    [InlineData("2026-W00")]
    [InlineData("2027-W53")]   // 2027 has 52 ISO weeks
    [InlineData("2026-w40")]
    public void TryParse_AnythingElse_IsNull(string? text) => Assert.Null(MentorWeek.TryParse(text));

    [Fact]
    public void ToString_IsTheIsoWeek() => Assert.Equal("2026-W05", new MentorWeek(2026, 5).ToString());

    [Fact]
    public void StartAndEnd_AreMondayAndSunday()
    {
        var week = new MentorWeek(2026, 40);

        Assert.Equal(new DateOnly(2026, 9, 28), week.Start);
        Assert.Equal(new DateOnly(2026, 10, 4), week.End);
        Assert.Equal(new MentorWeek(2026, 39), week.Previous);
    }

    [Fact]
    public void Of_ADayAcrossTheYearEnd_IsItsIsoWeek()
    {
        Assert.Equal(new MentorWeek(2026, 53), MentorWeek.Of(new DateOnly(2027, 1, 1)));
    }

    [Fact]
    public void UtcBounds_AreTheTeamsLocalMidnights()
    {
        var copenhagen = TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen");

        var (from, to) = new MentorWeek(2026, 40).UtcBounds(copenhagen);

        Assert.Equal(new DateTime(2026, 9, 27, 22, 0, 0, DateTimeKind.Utc), from);
        Assert.Equal(new DateTime(2026, 10, 4, 22, 0, 0, DateTimeKind.Utc), to);
    }

    [Fact]
    public void UtcBounds_AWeekWithAClockChange_IsSixDaysAndTwentyThreeHoursOrTwentyFive()
    {
        var copenhagen = TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen");

        var (from, to) = new MentorWeek(2026, 43).UtcBounds(copenhagen);   // clocks go back on Sunday 25 October

        Assert.Equal(TimeSpan.FromHours(7 * 24 + 1), to - from);
    }

    [Fact]
    public void LastClosed_IsTheWeekBeforeTheTeamsCurrentWeek()
    {
        var sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
        // Sunday 4 October, 20:00 UTC is already Monday 5 October in Sydney: week 40 has closed there.
        var now = new DateTime(2026, 10, 4, 20, 0, 0, DateTimeKind.Utc);

        Assert.Equal(new MentorWeek(2026, 40), MentorWeek.LastClosed(now, sydney));
        Assert.Equal(new MentorWeek(2026, 39), MentorWeek.LastClosed(now, TimeZoneInfo.Utc));
    }
}
