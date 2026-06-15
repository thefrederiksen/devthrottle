using Xunit;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;

namespace CcDirector.Core.Tests.Agents;

public class ClaudeAgentDefaultArgsTests
{
    [Fact]
    public void AgentOptions_DefaultClaudeArgs_DoesNotSkipPermissions()
    {
        var options = new AgentOptions();

        // A bare AgentOptions carries no Claude args until App startup wires the per-tool config
        // into DefaultClaudeArgs (issue #436 changed that configured default to Automatic, but the
        // raw object is still empty - this asserts the unwired invariant).
        Assert.DoesNotContain(AgentToolCatalog.ClaudeSkipPermissionsArg, options.DefaultClaudeArgs);
        Assert.Equal("", options.DefaultClaudeArgs);
    }

    [Fact]
    public void BuildLaunchSpec_NewSession_DefaultArgs_HasNoSkipPermissions()
    {
        var agent = new ClaudeAgent(new AgentOptions());

        var spec = agent.BuildLaunchSpec(userArgs: null, resumeSessionId: null, studioMode: false);

        // The assembled command line is just --session-id <uuid>, never --dangerously-skip-permissions.
        Assert.DoesNotContain(AgentToolCatalog.ClaudeSkipPermissionsArg, spec.Arguments);
        Assert.Contains("--session-id", spec.Arguments);
        Assert.NotNull(spec.PreassignedSessionId);
    }

    [Fact]
    public void BuildLaunchSpec_AutomaticPresetArgs_PropagatesSkipPermissions()
    {
        // When the user opts in to Automatic, the effective args carry skip-permissions
        // and the agent passes them through.
        var options = new AgentOptions
        {
            DefaultClaudeArgs = AgentToolCatalog.ClaudeSkipPermissionsArg,
        };
        var agent = new ClaudeAgent(options);

        var spec = agent.BuildLaunchSpec(userArgs: null, resumeSessionId: null, studioMode: false);

        Assert.Contains(AgentToolCatalog.ClaudeSkipPermissionsArg, spec.Arguments);
    }
}
