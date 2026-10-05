using System;
using System.IO;
using System.Threading.Tasks;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// "The Mentor's writer never runs with either switch off" (devthrottle_internal#2305, review G4), proven where it is
/// DECIDED: a real <see cref="GatewayHost"/>, built the way production builds it, reading <c>CC_GATEWAY_TEAM_MENTOR</c>
/// from the environment. No writer means no sweep and no model call. The control is the switch on, so the first two
/// cannot pass because the host never builds a writer at all. And switched on with no key for the model, the host
/// refuses to start rather than sweep silently for weeks (review H3).
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class TeamMentorSwitchHostTests
{
    [Fact]
    public async Task GatewayHost_TeamsReleased_MentorSwitchUnset_HasNoWriter()
    {
        Assert.Null(await WriterOf(teamsReleased: true, mentorSwitch: null, withKey: true));
    }

    [Fact]
    public async Task GatewayHost_TeamsDark_MentorSwitchSet_HasNoWriter()
    {
        Assert.Null(await WriterOf(teamsReleased: false, mentorSwitch: "1", withKey: true));
    }

    [Fact]
    public async Task GatewayHost_TeamsReleased_MentorSwitchSet_HasAWriter()
    {
        Assert.NotNull(await WriterOf(teamsReleased: true, mentorSwitch: "1", withKey: true));
    }

    [Fact]
    public async Task GatewayHost_MentorSwitchSet_WithNoKeyForItsModel_RefusesToStart_NamingTheKeyAndTheFix()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => WriterOf(teamsReleased: true, mentorSwitch: "1", withKey: false));

        Assert.Contains(TranscriptionEndpointResolver.DevThrottleKeyName, ex.Message);
        Assert.Contains(CcDirector.Gateway.Teams.Mentor.TeamMentorSwitch.EnvVar, ex.Message);
    }

    private static async Task<CcDirector.Gateway.Teams.Mentor.TeamMentorWriter?> WriterOf(bool teamsReleased, string? mentorSwitch, bool withKey)
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-mentor-switch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var vaultPath = Path.Combine(root, "test-vault.json");
        if (withKey)
            new KeyVault(vaultPath).Set(TranscriptionEndpointResolver.DevThrottleKeyName, "test-key-not-real");
        var priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        var priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        var priorMentor = Environment.GetEnvironmentVariable(CcDirector.Gateway.Teams.Mentor.TeamMentorSwitch.EnvVar);
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        Environment.SetEnvironmentVariable(CcDirector.Gateway.Teams.Mentor.TeamMentorSwitch.EnvVar, mentorSwitch);
        GatewayHost? gateway = null;
        try
        {
            gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "test-token", authEnabled: true,
                instancesDirectory: root, keyVaultPath: vaultPath,
                workListsPath: Path.Combine(root, "worklists", "worklists.json"),
                snoozePath: Path.Combine(root, "snooze", "snooze.json"),
                streamMode: true, teamsReleased: teamsReleased);
            await gateway.StartAsync();
            return gateway.TeamMentorWriter;
        }
        finally
        {
            if (gateway is not null)
                await gateway.StopAsync();
            Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", priorHosted);
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", priorRoot);
            Environment.SetEnvironmentVariable(CcDirector.Gateway.Teams.Mentor.TeamMentorSwitch.EnvVar, priorMentor);
            try { if (Directory.Exists(root)) Directory.Delete(root, true); }
            catch (IOException) { /* a file still held by the stopped host; the folder is in temp */ }
        }
    }
}
