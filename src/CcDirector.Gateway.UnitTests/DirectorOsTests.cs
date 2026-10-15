using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.UnitTests;

/// <summary>The one spelling of a Director's operating system, written by the Director and read by the Gateway.</summary>
public sealed class DirectorOsTests
{
    [Theory]
    [InlineData("windows", "windows")]
    [InlineData(" Linux ", "linux")]
    [InlineData("MACOS", "macos")]
    public void Normalize_KnownSystem_ReturnsItsOneSpelling(string reported, string expected)
        => Assert.Equal(expected, DirectorOs.Normalize(reported));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("darwin")]
    [InlineData("freebsd")]
    public void Normalize_EmptyOrUnlisted_ReturnsNull(string? reported)
        => Assert.Null(DirectorOs.Normalize(reported));

    [Fact]
    public void Current_OnThisMachine_IsOneOfTheListedSystems()
        => Assert.NotNull(DirectorOs.Normalize(DirectorOs.Current()));

    [Theory]
    [InlineData("windows", "Windows")]
    [InlineData("linux", "Linux")]
    [InlineData("macos", "Mac")]
    [InlineData(null, "Unknown")]
    public void DisplayName_EachSystem_IsThePageSpelling(string? os, string expected)
        => Assert.Equal(expected, DirectorOs.DisplayName(os));
}
