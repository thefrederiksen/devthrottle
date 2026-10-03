using CcDirector.Gateway.Teams;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// Teams merges dark (review finding F1): only <c>CC_GATEWAY_TEAMS=1</c> releases the team routes. Anything else -
/// unset included - keeps them unmapped. Whether the Gateway then maps the routes is proven over real HTTP in the
/// Gateway suite's <c>HostedTeamsDarkTests</c> and <c>HostedTeamEndpointsTests</c>.
/// </summary>
public sealed class TeamsReleaseSwitchTests
{
    [Theory]
    [InlineData("1", true)]
    [InlineData(" 1 ", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("yes", false)]
    public void Parse_EachValue_OnlyOneReleases(string? value, bool released)
    {
        Assert.Equal(released, TeamsReleaseSwitch.Parse(value));
    }
}
