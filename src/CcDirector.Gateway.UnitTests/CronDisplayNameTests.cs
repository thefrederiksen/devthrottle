using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The short name a factory schedule shows under its factory's header (the owner's layout A, 2026-10-09), checked
/// against real schedule names from the live list.
/// </summary>
public sealed class CronDisplayNameTests
{
    [Theory]
    [InlineData("ClickFunnels Factory - Builder", "clickfunnels", "ClickFunnels", "Builder")]
    [InlineData("DevThrottle Factory - Mail Desk - 08:00", "devthrottle", "DevThrottle", "Mail Desk - 08:00")]
    [InlineData("CC Factory - Ruth Calder - weekly CFO memo", "cc-factory", "Center Consulting", "Ruth Calder - weekly CFO memo")]
    [InlineData("M-Studio - AI Spend Watch - nightly", "mindzie-web", "M-Studio", "AI Spend Watch - nightly")]
    [InlineData("Money saver - daily cost report", "money-saver", "Money Saver", "daily cost report")]
    [InlineData("mindzie AI Reports - CEO - nightly partner outreach", "mindzie-ai-reports", "M-AI Reports", "CEO - nightly partner outreach")]
    public void ShortName_AFactorySchedule_DropsTheLeadingFactorySegment(string name, string factory, string title, string expected)
    {
        Assert.Equal(expected, CronDisplayName.ShortName(name, factory, title));
    }

    [Theory]
    [InlineData("Website Factory resends (weekdays 10:00)", "website-business", "Website Business")]
    [InlineData("mindzie Web Factory - Usage Watch - daily", "mindzie-web", null)]
    public void ShortName_OnlyStripsAWholeLeadingSegment(string name, string factory, string? title)
    {
        // No separator at all keeps the name; a segment ending in "Factory" is stripped even when the factory is not
        // registered (no title).
        var shortName = CronDisplayName.ShortName(name, factory, title);

        Assert.Equal(name.Contains(" - ") ? "Usage Watch - daily" : name, shortName);
    }

    [Fact]
    public void ShortName_ASegmentThatDoesNotNameTheFactory_IsKept()
    {
        Assert.Equal("Daily Error Triage - mindzieWeb",
            CronDisplayName.ShortName("Daily Error Triage - mindzieWeb", "mindzie-web", "M-Studio"));
    }

    [Fact]
    public void ShortName_AScheduleInNoFactory_KeepsItsFullNameEvenWhenItLooksLikeAFactorys()
    {
        // The name never decides membership: a schedule whose factory field is empty is a plain job.
        Assert.Equal("WarmForward Factory - Nora Hale - morning run",
            CronDisplayName.ShortName("WarmForward Factory - Nora Hale - morning run", null, null));
    }
}
