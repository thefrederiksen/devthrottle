using CcDirector.Core.Tools;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// The pure roll-up behind the home tools row: pass/fail/not-built tally, and which not-built tools
/// count as "broken" (expected here) vs optional. Drives whether the home alarms (HasProblem).
/// </summary>
public class ToolHealthTests
{
    private static ToolHealthInput Built(string name, bool passed) => new(name, true, true, passed);
    private static ToolHealthInput NotBuilt(string name, bool expected) => new(name, false, expected, false);

    [Fact]
    public void From_TalliesPassFailNotBuiltAndBroken()
    {
        var s = ToolHealthSummary.From(new[]
        {
            Built("a", true), Built("b", true), Built("c", false),
            NotBuilt("optional", expected: false), NotBuilt("broken", expected: true),
        });

        Assert.Equal(2, s.Pass);
        Assert.Equal(1, s.Fail);
        Assert.Equal(2, s.NotBuilt);
        Assert.Equal(1, s.Broken); // only the EXPECTED not-built one is "broken"; the optional one is not
        Assert.Equal(new[] { "c" }, s.Failing);
        Assert.Equal(5, s.Total);
        Assert.True(s.HasProblem);
    }

    [Fact]
    public void From_AllPassWithOptionalNotBuilt_NoProblem()
    {
        var s = ToolHealthSummary.From(new[] { Built("a", true), Built("b", true), NotBuilt("optional", expected: false) });

        Assert.Equal(0, s.Fail);
        Assert.Equal(0, s.Broken);
        Assert.False(s.HasProblem); // an optional not-installed tool is shown but is not a problem
    }

    [Fact]
    public void From_BrokenButNothingFailing_IsStillAProblem()
    {
        var s = ToolHealthSummary.From(new[] { Built("a", true), NotBuilt("broken", expected: true) });

        Assert.Equal(0, s.Fail);
        Assert.Equal(1, s.Broken);
        Assert.True(s.HasProblem); // a broken (expected-but-missing) tool alarms even with no failures
    }
}
