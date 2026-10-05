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
/// cannot pass because the host never builds a writer at all. Switched on with no key for the model, the host refuses
/// to start rather than sweep silently for weeks (review H3) - but only once the key has had its chance to arrive from
/// the environment (review J3), and never when the Mentor is off (review J5).
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class TeamMentorSwitchHostTests
{
    private const string KeyName = TranscriptionEndpointResolver.DevThrottleKeyName;

    [Fact]
    public async Task GatewayHost_TeamsReleased_MentorSwitchUnset_WithNoKey_StartsWithNoWriter()
    {
        Assert.Null(await WriterOf(hosted: true, teamsReleased: true, mentorSwitch: null, keyInVault: false));
    }

    [Fact]
    public async Task GatewayHost_TeamsDark_MentorSwitchSet_WithNoKey_StartsWithNoWriter()
    {
        Assert.Null(await WriterOf(hosted: true, teamsReleased: false, mentorSwitch: "1", keyInVault: false));
    }

    [Fact]
    public async Task GatewayHost_TeamsReleased_MentorSwitchSet_HasAWriter()
    {
        Assert.NotNull(await WriterOf(hosted: true, teamsReleased: true, mentorSwitch: "1", keyInVault: true));
    }

    [Fact]
    public async Task GatewayHost_MentorSwitchSet_WithNoKeyForItsModel_RefusesToStart_NamingTheKeyAndWhereItComesFrom()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => WriterOf(hosted: true, teamsReleased: true, mentorSwitch: "1", keyInVault: false));

        Assert.Contains(KeyName, ex.Message);
        Assert.Contains(CcDirector.Gateway.Teams.Mentor.TeamMentorSwitch.EnvVar, ex.Message);
        Assert.Contains("hosted Gateway's key vault", ex.Message);
    }

    [Fact]
    public async Task GatewayHost_Personal_MentorSwitchSet_KeyInTheEnvironmentOnly_Starts()
    {
        // Review J3: on a personal Gateway the key is copied from the environment into the vault at start, so a key
        // that is only in the environment is enough.
        Assert.NotNull(await WriterOf(hosted: false, teamsReleased: true, mentorSwitch: "1", keyInVault: false, keyInEnvironment: true));
    }

    private static async Task<CcDirector.Gateway.Teams.Mentor.TeamMentorWriter?> WriterOf(bool hosted, bool teamsReleased,
        string? mentorSwitch, bool keyInVault, bool keyInEnvironment = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-mentor-switch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var vaultPath = Path.Combine(root, "test-vault.json");
        if (keyInVault)
            new KeyVault(vaultPath).Set(KeyName, "test-key-not-real");
        var prior = new[] { "CC_GATEWAY_HOSTED", "CC_DIRECTOR_ROOT", "CC_GATEWAY_NO_TAILSCALE", KeyName,
            CcDirector.Gateway.Teams.Mentor.TeamMentorSwitch.EnvVar };
        var priorValues = Array.ConvertAll(prior, Environment.GetEnvironmentVariable);
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", hosted ? "1" : null);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        // A personal Gateway would otherwise point this machine's real tailnet front door at the test port.
        Environment.SetEnvironmentVariable("CC_GATEWAY_NO_TAILSCALE", "1");
        Environment.SetEnvironmentVariable(KeyName, keyInEnvironment ? "test-key-from-the-environment" : null);
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
            for (var i = 0; i < prior.Length; i++)
                Environment.SetEnvironmentVariable(prior[i], priorValues[i]);
            try { if (Directory.Exists(root)) Directory.Delete(root, true); }
            catch (IOException) { /* a file still held by the stopped host; the folder is in temp */ }
        }
    }
}
