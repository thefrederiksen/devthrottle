using CcDirector.Core.Agents;
using Xunit;

namespace CcDirector.Core.Tests.Agents;

public sealed class CliConformanceTests
{
    [Fact]
    public void WarningsFor_VersionThatPassed_ReturnsNoWarning()
    {
        var warnings = CliConformance.WarningsFor(new Dictionary<string, string>
        {
            ["Claude Code"] = "2.1.310 (Claude Code)",
            ["Pi"] = "1.2.0",
        });

        Assert.Empty(warnings);
    }

    [Fact]
    public void WarningsFor_VersionThatNeverPassed_WarnsWithoutCallingItAGate()
    {
        var warnings = CliConformance.WarningsFor(new Dictionary<string, string>
        {
            ["Claude Code"] = "2.1.311 (Claude Code)",
        });

        var warning = Assert.Single(warnings);
        Assert.Contains("has never passed", warning);
        Assert.Contains("session launch remains enabled", warning);
    }

    [Fact]
    public void WarningsFor_CodexWhileItsRunIsParked_Warns()
    {
        var warnings = CliConformance.WarningsFor(new Dictionary<string, string>
        {
            ["Codex"] = "codex-cli 0.163.0",
        });

        Assert.Single(warnings);
    }
}
