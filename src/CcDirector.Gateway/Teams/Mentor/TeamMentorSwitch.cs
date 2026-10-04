using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Teams.Mentor;

/// <summary>
/// Whether the Mentor's weekly writer runs on this Gateway (devthrottle_internal#2305). Switching on a Mentor that
/// writes about people is the owner's decision, so it is OFF everywhere - hosted included - unless BOTH Teams is
/// released (<see cref="TeamsReleaseSwitch"/>, <c>CC_GATEWAY_TEAMS=1</c>) AND this switch,
/// <c>CC_GATEWAY_TEAM_MENTOR</c>, is exactly <c>1</c>. Unset, empty or anything else is off.
/// </summary>
public static class TeamMentorSwitch
{
    /// <summary>The environment variable that turns the Mentor's weekly writer on.</summary>
    public const string EnvVar = "CC_GATEWAY_TEAM_MENTOR";

    /// <summary>Whether the writer runs, given whether Teams is released, reading this switch from the environment.</summary>
    public static bool IsOn(bool teamsReleased)
    {
        var on = Decide(teamsReleased, Environment.GetEnvironmentVariable(EnvVar));
        FileLog.Write($"[TeamMentorSwitch] IsOn: teams={(teamsReleased ? "released" : "dark")} {EnvVar}={(TeamsReleaseSwitch.Parse(Environment.GetEnvironmentVariable(EnvVar)) ? "1" : "not 1")} - writer {(on ? "ON" : "OFF")}");
        return on;
    }

    /// <summary>The decision from the two inputs. Pure, so it is tested directly.</summary>
    public static bool Decide(bool teamsReleased, string? mentorValue) => teamsReleased && TeamsReleaseSwitch.Parse(mentorValue);
}
