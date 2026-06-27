using CcDirector.Avalonia.Controls;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Tests for the SessionActionBar context gauge presentation logic (issue #799): the three colour
/// bands and the compact label. Pure logic - no Avalonia window is constructed.
/// </summary>
public sealed class ContextGaugeTests
{
    // ---- Band selection: neutral below 70%, amber 70-90%, red above 90% ----

    [Theory]
    [InlineData(0.0)]
    [InlineData(50.0)]
    [InlineData(69.9)]
    public void SelectBand_BelowSeventy_IsNeutral(double pct)
    {
        Assert.Equal(ContextUsageBand.Neutral, ContextGauge.SelectBand(pct));
    }

    [Theory]
    [InlineData(70.0)]   // lower boundary is amber
    [InlineData(80.0)]
    [InlineData(90.0)]   // upper boundary is still amber
    public void SelectBand_SeventyThroughNinety_IsAmber(double pct)
    {
        Assert.Equal(ContextUsageBand.Amber, ContextGauge.SelectBand(pct));
    }

    [Theory]
    [InlineData(90.1)]   // just above ninety is red
    [InlineData(99.0)]
    [InlineData(100.0)]
    public void SelectBand_AboveNinety_IsRed(double pct)
    {
        Assert.Equal(ContextUsageBand.Red, ContextGauge.SelectBand(pct));
    }

    [Fact]
    public void SelectBand_NullPercent_IsNeutral()
    {
        // Window unknown (raw-number fallback) -> neutral bar.
        Assert.Equal(ContextUsageBand.Neutral, ContextGauge.SelectBand(null));
    }

    // ---- Label formatting ----

    [Fact]
    public void FormatLabel_KnownWindow_ShowsUsedWindowAndPercent()
    {
        var usage = new ContextUsageDto { UsedTokens = 42_000, WindowTokens = 200_000, PercentUsed = 21.0 };
        Assert.Equal("ctx 42k / 200k (21%)", ContextGauge.FormatLabel(usage));
    }

    [Fact]
    public void FormatLabel_OneMillionWindow_ShowsMegaToken()
    {
        var usage = new ContextUsageDto { UsedTokens = 250_000, WindowTokens = 1_000_000, PercentUsed = 25.0 };
        Assert.Equal("ctx 250k / 1M (25%)", ContextGauge.FormatLabel(usage));
    }

    [Fact]
    public void FormatLabel_UnknownWindow_RawNumberFallback_NoPercent()
    {
        var usage = new ContextUsageDto { UsedTokens = 12_345, WindowTokens = null, PercentUsed = null };
        Assert.Equal("ctx 12k", ContextGauge.FormatLabel(usage));
    }

    [Theory]
    [InlineData(500, "500")]
    [InlineData(1_000, "1k")]
    [InlineData(42_345, "42k")]
    [InlineData(200_000, "200k")]
    [InlineData(1_000_000, "1M")]
    [InlineData(1_500_000, "1.5M")]
    public void FormatTokens_CompactsLargeNumbers(long tokens, string expected)
    {
        Assert.Equal(expected, ContextGauge.FormatTokens(tokens));
    }
}
