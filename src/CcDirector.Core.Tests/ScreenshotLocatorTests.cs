using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Core.Tests;

public class ScreenshotLocatorTests
{
    [Fact]
    public void ParseMacScreencaptureLocation_TildePath_ExpandsToHome()
    {
        var result = ScreenshotLocator.ParseMacScreencaptureLocation("~/Desktop/Shots\n", "/Users/testuser");

        Assert.Equal("/Users/testuser/Desktop/Shots", result);
    }

    [Fact]
    public void ParseMacScreencaptureLocation_BareTilde_IsHome()
    {
        Assert.Equal("/Users/testuser", ScreenshotLocator.ParseMacScreencaptureLocation("~", "/Users/testuser"));
    }

    [Fact]
    public void ParseMacScreencaptureLocation_AbsolutePath_ReturnedAsIs()
    {
        var result = ScreenshotLocator.ParseMacScreencaptureLocation("/Users/testuser/Pictures/Caps\n", "/Users/testuser");

        Assert.Equal("/Users/testuser/Pictures/Caps", result);
    }

    [Fact]
    public void ParseMacScreencaptureLocation_Quoted_Trimmed()
    {
        var result = ScreenshotLocator.ParseMacScreencaptureLocation("\"~/Desktop\"", "/Users/testuser");

        Assert.Equal("/Users/testuser/Desktop", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n")]
    public void ParseMacScreencaptureLocation_EmptyOrUnset_ReturnsNull(string? stdout)
    {
        Assert.Null(ScreenshotLocator.ParseMacScreencaptureLocation(stdout, "/Users/testuser"));
    }

    [Fact]
    public void Detect_OnThisPlatform_DoesNotThrow()
    {
        // Smoke: detection must never throw regardless of environment; it returns a path or null.
        var ex = Record.Exception(() => ScreenshotLocator.Detect());

        Assert.Null(ex);
    }
}
