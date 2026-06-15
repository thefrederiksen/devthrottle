using System.Linq;
using CcDirector.Core.Home;
using Xunit;

namespace CcDirector.Core.Tests.Home;

/// <summary>
/// Covers the pure readiness logic behind the full-screen home page: how raw service facts
/// (claude on PATH, OpenAI key, cc-* tool count) map to rows, levels, fix-it actions, and the
/// overall ready count. No Avalonia, no I/O.
/// </summary>
public class HomeStatusBuilderTests
{
    private static HomeStatus FullyHealthy() => HomeStatusBuilder.Build(
        claudeFound: true, claudeVersion: "2.1.168",
        keyPresent: true, keyUnavailableMessage: "", keyUsesGateway: true,
        toolsBuilt: 31, toolsTotal: 31, directorVersion: "v0.6.23");

    private static HomeCheck Row(HomeStatus s, string title) => s.Checks.Single(c => c.Title == title);

    [Fact]
    public void Build_AllHealthy_AllReadyAndEveryRowOk()
    {
        var status = FullyHealthy();

        Assert.True(status.AllReady);
        Assert.Equal(4, status.TotalCount);
        Assert.Equal(4, status.ReadyCount);
        Assert.All(status.Checks, c => Assert.Equal(HomeCheckLevel.Ok, c.Level));
        Assert.All(status.Checks, c => Assert.Equal(HomeCheckAction.None, c.Action));
    }

    [Fact]
    public void Build_ClaudeMissing_BadRowRoutesToSettings()
    {
        var status = HomeStatusBuilder.Build(
            claudeFound: false, claudeVersion: null,
            keyPresent: true, keyUnavailableMessage: "", keyUsesGateway: false,
            toolsBuilt: 31, toolsTotal: 31, directorVersion: "v0.6.23");

        var claude = Row(status, "claude CLI");
        Assert.Equal(HomeCheckLevel.Bad, claude.Level);
        Assert.Equal(HomeCheckAction.OpenSettings, claude.Action);
        Assert.False(status.AllReady);
        Assert.Equal(3, status.ReadyCount);
    }

    [Fact]
    public void Build_ClaudeFoundWithoutVersion_OkAndOnPath()
    {
        var status = HomeStatusBuilder.Build(
            claudeFound: true, claudeVersion: null,
            keyPresent: true, keyUnavailableMessage: "", keyUsesGateway: false,
            toolsBuilt: 1, toolsTotal: 1, directorVersion: "v0.6.23");

        var claude = Row(status, "claude CLI");
        Assert.Equal(HomeCheckLevel.Ok, claude.Level);
        Assert.Equal("on PATH", claude.Detail);
    }

    [Fact]
    public void Build_KeyMissing_BadRowKeepsUnavailableMessage()
    {
        const string msg = "OpenAI key is not set. Open Settings > Voice and add your OpenAI API key.";
        var status = HomeStatusBuilder.Build(
            claudeFound: true, claudeVersion: "2.1.168",
            keyPresent: false, keyUnavailableMessage: msg, keyUsesGateway: false,
            toolsBuilt: 31, toolsTotal: 31, directorVersion: "v0.6.23");

        var key = Row(status, "OpenAI key");
        Assert.Equal(HomeCheckLevel.Bad, key.Level);
        Assert.Equal(msg, key.Detail);
        Assert.Equal(HomeCheckAction.OpenSettings, key.Action);
    }

    [Fact]
    public void Build_KeyPresentFromGateway_LabelsVaultSource()
    {
        var status = FullyHealthy();
        Assert.Equal("Set (Gateway vault)", Row(status, "OpenAI key").Detail);
    }

    [Fact]
    public void Build_PartialTools_WarnAndRoutesToTools()
    {
        var status = HomeStatusBuilder.Build(
            claudeFound: true, claudeVersion: "2.1.168",
            keyPresent: true, keyUnavailableMessage: "", keyUsesGateway: true,
            toolsBuilt: 12, toolsTotal: 31, directorVersion: "v0.6.23");

        var tools = Row(status, "cc-* tools");
        Assert.Equal(HomeCheckLevel.Warn, tools.Level);
        Assert.Equal("12 of 31 on PATH", tools.Detail);
        Assert.Equal(HomeCheckAction.OpenTools, tools.Action);
        Assert.False(status.AllReady);
    }

    [Fact]
    public void Build_NoTools_BadAndRoutesToTools()
    {
        var status = HomeStatusBuilder.Build(
            claudeFound: true, claudeVersion: "2.1.168",
            keyPresent: true, keyUnavailableMessage: "", keyUsesGateway: true,
            toolsBuilt: 0, toolsTotal: 31, directorVersion: "v0.6.23");

        var tools = Row(status, "cc-* tools");
        Assert.Equal(HomeCheckLevel.Bad, tools.Level);
        Assert.Equal(HomeCheckAction.OpenTools, tools.Action);
    }

    [Fact]
    public void Build_DirectorRow_AlwaysOkAndShowsVersion()
    {
        var status = FullyHealthy();
        var director = Row(status, "Director");
        Assert.Equal(HomeCheckLevel.Ok, director.Level);
        Assert.Equal("v0.6.23 - running", director.Detail);
        Assert.Equal(HomeCheckAction.None, director.Action);
    }
}
