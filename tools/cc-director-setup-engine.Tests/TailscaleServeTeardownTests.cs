using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Tailscale Serve teardown (issue #257): removes ONLY the 443 front-door mapping, is a clean
/// no-op when the CLI is absent, and treats an already-off mapping as success (Assumption 5).
/// </summary>
public class TailscaleServeTeardownTests
{
    [Fact]
    public void RemoveFrontDoor_TargetsThe443OffCommand()
    {
        string? seen = null;
        TailscaleServeTeardown.RemoveFrontDoor(args => { seen = args; return (true, 0, ""); });
        Assert.Equal("serve --https=443 off", seen);
    }

    [Fact]
    public void RemoveFrontDoor_CliAbsent_NotAttempted()
    {
        var r = TailscaleServeTeardown.RemoveFrontDoor(_ => (Available: false, ExitCode: -1, Error: ""));
        Assert.False(r.Attempted);
        Assert.False(r.Removed);
        Assert.Null(r.Error);
    }

    [Fact]
    public void RemoveFrontDoor_ExitZero_Removed()
    {
        var r = TailscaleServeTeardown.RemoveFrontDoor(_ => (Available: true, ExitCode: 0, Error: ""));
        Assert.True(r.Attempted);
        Assert.True(r.Removed);
    }

    [Fact]
    public void RemoveFrontDoor_AlreadyOff_CountsAsRemoved()
    {
        var r = TailscaleServeTeardown.RemoveFrontDoor(
            _ => (Available: true, ExitCode: 1, Error: "serve config does not exist"));
        Assert.True(r.Removed);
        Assert.Null(r.Error);
    }

    [Fact]
    public void RemoveFrontDoor_RealFailure_SurfacesError()
    {
        var r = TailscaleServeTeardown.RemoveFrontDoor(
            _ => (Available: true, ExitCode: 1, Error: "permission denied"));
        Assert.False(r.Removed);
        Assert.Equal("permission denied", r.Error);
    }

    [Theory]
    [InlineData("serve config does not exist", true)]
    [InlineData("handler not found", true)]
    [InlineData("permission denied", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAlreadyOff_MatchesBenignMessages(string? error, bool expected)
        => Assert.Equal(expected, TailscaleServeTeardown.IsAlreadyOff(error));
}
