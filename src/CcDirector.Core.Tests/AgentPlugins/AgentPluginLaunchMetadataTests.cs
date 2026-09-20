using CcDirector.Core.AgentPlugins;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests.AgentPlugins;

/// <summary>
/// THE RESUME FLAG IS CHECKED AGAINST WHAT THE AGENT REALLY BUILDS, for every registered agent.
///
/// <see cref="AgentPluginLaunchMetadata.CanResumeSavedConversation"/> says whether an agent can be started
/// on a saved conversation, and anything that words a screen reads it rather than keeping its own list of
/// agent names. A second list drifts: the way up kept one, and it told the owner that a Copilot or Cursor
/// session's conversation was lost when both drivers pass the id straight through.
///
/// So this is a PRESENCE check over the real drivers and not a list anybody has to remember to update. It
/// builds each agent's launch spec with a known conversation id and asserts the flag equals whether that id
/// really reached the arguments. It fails the day an agent gains or loses resume without its flag moving.
/// </summary>
public sealed class AgentPluginLaunchMetadataTests
{
    /// <summary>A conversation id no argument could hold by accident.</summary>
    private const string KnownConversationId = "11111111-2222-3333-4444-555555555555";

    /// <summary>
    /// IF YOU ARE READING THIS BECAUSE THIS TEST IS FAILING ON AN AGENT YOU JUST ADDED, DO NOT FLIP THE
    /// FLAG TO MATCH THE ARGUMENTS. Widen what the walk looks at instead.
    ///
    /// The walk's proxy for "this agent resumes" is that the conversation id reaches the launch arguments.
    /// For all eight agents today that proxy is exact. It can lie in ONE direction: an agent that carries
    /// the id in its arguments for some OTHER purpose - a log file name, a working folder, a report - is
    /// not resuming anything, and forcing its flag true would word the offer "your conversation comes
    /// back" for a session that arrives blank. That is the one direction the safe-side rule exists to
    /// prevent, which is why it is worth the failing test rather than a quiet true.
    ///
    /// The fix for such an agent is to make the walk read what the driver MEANS rather than what it spells
    /// - for example by naming the argument that carries the id for resuming - and to leave the flag
    /// saying what the agent really does.
    /// </summary>
    [Fact]
    public void Every_registered_agent_resume_flag_matches_what_its_launch_spec_really_builds()
    {
        var plugins = AgentPluginRegistry.All;

        // A registry that answered with nothing would otherwise pass this as a clean run. The built-ins
        // alone are eight, so anything under that is a broken instrument and not a green.
        Assert.True(plugins.Count >= 8, $"the registry answered with only {plugins.Count} agent(s), which is too few to be the real one");

        foreach (var plugin in plugins)
        {
            var spec = plugin.BuildLaunchSpec(new AgentPluginLaunchRequest(
                new AgentOptions(), UserArgs: null, ResumeSessionId: KnownConversationId, StudioMode: false));
            var idReachedTheArguments = spec.Arguments.Contains(KnownConversationId, StringComparison.Ordinal);

            Assert.True(
                plugin.Launch.CanResumeSavedConversation == idReachedTheArguments,
                $"{plugin.DisplayName} declares CanResumeSavedConversation={plugin.Launch.CanResumeSavedConversation}, " +
                $"but the conversation id {(idReachedTheArguments ? "DID" : "did NOT")} reach its launch arguments: " +
                $"\"{spec.Arguments}\"");
        }
    }

    /// <summary>
    /// The four agents that really do resume, named, so the walk above cannot pass by finding that every
    /// agent declares false and no agent builds the id. Read from the drivers on 20 September 2026.
    /// </summary>
    [Theory]
    [InlineData(AgentKind.ClaudeCode, true)]
    [InlineData(AgentKind.Pi, true)]
    [InlineData(AgentKind.Copilot, true)]
    [InlineData(AgentKind.Cursor, true)]
    [InlineData(AgentKind.Codex, false)]
    [InlineData(AgentKind.Gemini, false)]
    [InlineData(AgentKind.Grok, false)]
    [InlineData(AgentKind.OpenCode, false)]
    public void Each_built_in_agent_declares_the_resume_support_its_driver_has(AgentKind kind, bool canResume)
        => Assert.Equal(canResume, AgentPluginRegistry.Get(kind).Launch.CanResumeSavedConversation);
}
