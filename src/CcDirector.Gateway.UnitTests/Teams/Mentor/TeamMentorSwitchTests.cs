using CcDirector.Gateway.Teams.Mentor;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams.Mentor;

/// <summary>The Mentor's weekly writer runs only with BOTH switches on (devthrottle_internal#2305). One test here
/// clears the process-wide switch variable, so the class runs in the process-environment collection.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class TeamMentorSwitchTests
{
    [Theory]
    [InlineData(true, "1", true)]
    [InlineData(true, " 1 ", true)]
    [InlineData(false, "1", false)]
    [InlineData(true, null, false)]
    [InlineData(true, "", false)]
    [InlineData(true, "true", false)]
    [InlineData(true, "0", false)]
    [InlineData(false, null, false)]
    public void Decide_OnlyTeamsReleasedAndExactlyOne_IsOn(bool teamsReleased, string? mentor, bool on)
    {
        Assert.Equal(on, TeamMentorSwitch.Decide(teamsReleased, mentor));
    }

    [Fact]
    public void IsOn_TheSwitchUnset_IsOff_EvenWithTeamsReleased()
    {
        var saved = Environment.GetEnvironmentVariable(TeamMentorSwitch.EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(TeamMentorSwitch.EnvVar, null);
            Assert.False(TeamMentorSwitch.IsOn(teamsReleased: true));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TeamMentorSwitch.EnvVar, saved);
        }
    }
}
