using System.Collections.Generic;
using System.Linq;
using CcDirector.Core.Home;
using Xunit;

namespace CcDirector.Core.Tests.Home;

/// <summary>
/// Covers the pure readiness logic behind the full-screen home page: how raw service facts
/// (which agent CLIs are installed, cc-* tool count) map to rows, levels, fix-it actions, and
/// the overall ready count. The Director is CLI-agnostic - any one supported CLI satisfies
/// the requirement. No OpenAI-key or Director row. No Avalonia, no I/O.
/// </summary>
public class HomeStatusBuilderTests
{
    private static AgentCliFact Cli(string name, bool found, string? version = null) =>
        new(name, found, version);

    private static HomeStatus FullyHealthy() => HomeStatusBuilder.Build(
        new[] { Cli("Claude Code", true, "2.1.168"), Cli("Codex", false) },
        toolsBuilt: 31, toolsTotal: 31);

    private static HomeCheck Row(HomeStatus s, string title) => s.Checks.Single(c => c.Title == title);

    [Fact]
    public void Build_AllHealthy_AllReadyAndEveryRowOk()
    {
        var status = FullyHealthy();

        Assert.True(status.AllReady);
        Assert.Equal(2, status.TotalCount);
        Assert.Equal(2, status.ReadyCount);
        Assert.All(status.Checks, c => Assert.Equal(HomeCheckLevel.Ok, c.Level));
        Assert.All(status.Checks, c => Assert.Equal(HomeCheckAction.None, c.Action));
    }

    [Fact]
    public void Build_NoCliInstalled_BadRowRoutesToSettings()
    {
        var status = HomeStatusBuilder.Build(
            new[] { Cli("Claude Code", false), Cli("Codex", false), Cli("Pi", false) },
            toolsBuilt: 31, toolsTotal: 31);

        var clis = Row(status, "Agent CLIs");
        Assert.Equal(HomeCheckLevel.Bad, clis.Level);
        Assert.Equal(HomeCheckAction.OpenSettings, clis.Action);
        Assert.Contains("No agent CLI found", clis.Detail);
        Assert.False(status.AllReady);
        Assert.Equal(1, status.ReadyCount);
    }

    [Fact]
    public void Build_SingleCliInstalled_OkAndListsItWithVersion()
    {
        var status = HomeStatusBuilder.Build(
            new[] { Cli("Codex", true, "0.21.0"), Cli("Claude Code", false) },
            toolsBuilt: 31, toolsTotal: 31);

        var clis = Row(status, "Agent CLIs");
        Assert.Equal(HomeCheckLevel.Ok, clis.Level);
        Assert.Equal("Codex 0.21.0 - on PATH", clis.Detail);
        Assert.Equal(HomeCheckAction.None, clis.Action);
    }

    [Fact]
    public void Build_MultipleClisInstalled_ListsAllInstalled()
    {
        var status = HomeStatusBuilder.Build(
            new[] { Cli("Claude Code", true, "2.1.177"), Cli("Codex", true), Cli("Gemini", false) },
            toolsBuilt: 31, toolsTotal: 31);

        var clis = Row(status, "Agent CLIs");
        Assert.Equal(HomeCheckLevel.Ok, clis.Level);
        Assert.Equal("Claude Code 2.1.177, Codex - on PATH", clis.Detail);
    }

    [Fact]
    public void Build_ClaudeVersionWithProductParenthetical_NotPrintedTwice()
    {
        // Claude reports "2.1.177 (Claude Code)" - the name must not appear twice.
        var status = HomeStatusBuilder.Build(
            new[] { Cli("Claude Code", true, "2.1.177 (Claude Code)") },
            toolsBuilt: 31, toolsTotal: 31);

        Assert.Equal("Claude Code 2.1.177 - on PATH", Row(status, "Agent CLIs").Detail);
    }

    [Fact]
    public void Build_CliFoundWithoutVersion_OkAndNameOnly()
    {
        var status = HomeStatusBuilder.Build(
            new[] { Cli("Claude Code", true) },
            toolsBuilt: 1, toolsTotal: 1);

        var clis = Row(status, "Agent CLIs");
        Assert.Equal(HomeCheckLevel.Ok, clis.Level);
        Assert.Equal("Claude Code - on PATH", clis.Detail);
    }

    [Fact]
    public void Build_PartialTools_WarnAndOffersRepair()
    {
        var status = HomeStatusBuilder.Build(
            new[] { Cli("Claude Code", true, "2.1.168") },
            toolsBuilt: 12, toolsTotal: 31);

        var tools = Row(status, "cc-* tools");
        Assert.Equal(HomeCheckLevel.Warn, tools.Level);
        Assert.Equal("12 of 31 on PATH", tools.Detail);
        Assert.Equal(HomeCheckAction.RepairTools, tools.Action);
        Assert.False(status.AllReady);
    }

    [Fact]
    public void Build_PartialTools_NamesTheMissingToolsAndOffersRepair()
    {
        var status = HomeStatusBuilder.Build(
            new[] { Cli("Claude Code", true, "2.1.168") },
            toolsBuilt: 29, toolsTotal: 32,
            missingTools: new[] { "cc-html", "cc-pdf", "cc-word" });

        var tools = Row(status, "cc-* tools");
        Assert.Equal(HomeCheckLevel.Warn, tools.Level);
        Assert.Equal(HomeCheckAction.RepairTools, tools.Action);
        Assert.Contains("cc-html", tools.Detail);
        Assert.Contains("cc-pdf", tools.Detail);
        Assert.Contains("cc-word", tools.Detail);
    }

    [Fact]
    public void Build_ManyMissingTools_TruncatesTheList()
    {
        var missing = new[] { "cc-a", "cc-b", "cc-c", "cc-d", "cc-e", "cc-f" };
        var status = HomeStatusBuilder.Build(
            new[] { Cli("Claude Code", true, "2.1.168") },
            toolsBuilt: 26, toolsTotal: 32, missingTools: missing);

        var tools = Row(status, "cc-* tools");
        Assert.Contains("+2 more", tools.Detail);
    }

    [Fact]
    public void Build_NoTools_BadAndOffersRepair()
    {
        var status = HomeStatusBuilder.Build(
            new[] { Cli("Claude Code", true, "2.1.168") },
            toolsBuilt: 0, toolsTotal: 31);

        var tools = Row(status, "cc-* tools");
        Assert.Equal(HomeCheckLevel.Bad, tools.Level);
        Assert.Equal(HomeCheckAction.RepairTools, tools.Action);
    }

    [Fact]
    public void Build_NoOpenAiKeyOrDirectorRow()
    {
        var status = FullyHealthy();
        Assert.DoesNotContain(status.Checks, c => c.Title == "OpenAI key");
        Assert.DoesNotContain(status.Checks, c => c.Title == "Director");
    }
}
