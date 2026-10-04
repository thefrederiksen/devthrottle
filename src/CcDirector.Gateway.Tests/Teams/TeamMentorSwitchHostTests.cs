using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// "The Mentor's writer never runs with either switch off" (devthrottle_internal#2305, review G4), proven where it is
/// DECIDED: a real <see cref="GatewayHost"/>, built the way production builds it, reading <c>CC_GATEWAY_TEAM_MENTOR</c>
/// from the environment. No writer means no sweep and no model call. The third case is the switch on, so the first two
/// cannot pass because the host never builds a writer at all.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class TeamMentorSwitchHostTests
{
    [Fact]
    public async Task GatewayHost_TeamsReleased_MentorSwitchUnset_HasNoWriter()
    {
        Assert.Null(await WriterOf(teamsReleased: true, mentorSwitch: null));
    }

    [Fact]
    public async Task GatewayHost_TeamsDark_MentorSwitchSet_HasNoWriter()
    {
        Assert.Null(await WriterOf(teamsReleased: false, mentorSwitch: "1"));
    }

    [Fact]
    public async Task GatewayHost_TeamsReleased_MentorSwitchSet_HasAWriter()
    {
        Assert.NotNull(await WriterOf(teamsReleased: true, mentorSwitch: "1"));
    }

    private static async Task<CcDirector.Gateway.Teams.Mentor.TeamMentorWriter?> WriterOf(bool teamsReleased, string? mentorSwitch)
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-mentor-switch-" + Guid.NewGuid().ToString("N"));
        var priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        var priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        var priorMentor = Environment.GetEnvironmentVariable(CcDirector.Gateway.Teams.Mentor.TeamMentorSwitch.EnvVar);
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        Environment.SetEnvironmentVariable(CcDirector.Gateway.Teams.Mentor.TeamMentorSwitch.EnvVar, mentorSwitch);
        var gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "test-token", authEnabled: true,
            instancesDirectory: root,
            workListsPath: Path.Combine(root, "worklists", "worklists.json"),
            snoozePath: Path.Combine(root, "snooze", "snooze.json"),
            streamMode: true, teamsReleased: teamsReleased);
        try
        {
            await gateway.StartAsync();
            return gateway.TeamMentorWriter;
        }
        finally
        {
            await gateway.StopAsync();
            Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", priorHosted);
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", priorRoot);
            Environment.SetEnvironmentVariable(CcDirector.Gateway.Teams.Mentor.TeamMentorSwitch.EnvVar, priorMentor);
            try { if (Directory.Exists(root)) Directory.Delete(root, true); }
            catch (IOException) { /* a file still held by the stopped host; the folder is in temp */ }
        }
    }
}
