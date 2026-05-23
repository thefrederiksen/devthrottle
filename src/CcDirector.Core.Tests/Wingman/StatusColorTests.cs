using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

public sealed class StatusColorTests
{
    [Fact]
    public void Null_summary_is_unknown()
    {
        Assert.Equal("unknown", StatusColor.From(null));
    }

    [Theory]
    [InlineData("question")]
    [InlineData("error")]
    [InlineData("permission")]
    [InlineData("QUESTION")]      // case-insensitive
    public void Needs_user_red_inputs_yield_red(string needsUser)
    {
        var s = new TurnSummary { NeedsUser = needsUser };
        Assert.Equal("red", StatusColor.From(s));
    }

    [Fact]
    public void Idle_with_dirty_git_is_yellow()
    {
        var s = new TurnSummary { NeedsUser = "idle" };
        Assert.Equal("yellow", StatusColor.From(s, gitDirty: true));
    }

    [Fact]
    public void Idle_with_clean_git_is_green()
    {
        var s = new TurnSummary { NeedsUser = "idle" };
        Assert.Equal("green", StatusColor.From(s, gitDirty: false));
    }

    [Fact]
    public void No_needs_user_is_green_by_default()
    {
        var s = new TurnSummary { NeedsUser = "no" };
        Assert.Equal("green", StatusColor.From(s));
    }

    [Fact]
    public void Warnings_force_yellow()
    {
        var s = new TurnSummary { NeedsUser = "no" };
        Assert.Equal("yellow", StatusColor.From(s, hasWarnings: true));
    }

    [Fact]
    public void Red_beats_yellow_when_both_apply()
    {
        var s = new TurnSummary { NeedsUser = "error" };
        Assert.Equal("red", StatusColor.From(s, gitDirty: true, hasWarnings: true));
    }
}
