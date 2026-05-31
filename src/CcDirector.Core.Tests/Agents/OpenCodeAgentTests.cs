using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests.Agents;

public class OpenCodeAgentTests
{
    private static OpenCodeAgent CreateAgent(AgentOptions? options = null) =>
        new OpenCodeAgent(options ?? new AgentOptions());

    [Fact]
    public void Constructor_NullOptions_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new OpenCodeAgent(null!));
    }

    [Fact]
    public void Kind_IsOpenCode()
    {
        var agent = CreateAgent();

        Assert.Equal(AgentKind.OpenCode, agent.Kind);
    }

    [Fact]
    public void ExecutablePath_UsesOpenCodePathFromOptions()
    {
        var options = new AgentOptions { OpenCodePath = "custom-opencode" };
        var agent = CreateAgent(options);

        Assert.Equal("custom-opencode", agent.ExecutablePath);
    }

    [Fact]
    public void ExecutablePath_Default_IsOpencode()
    {
        var agent = CreateAgent();

        Assert.Equal("opencode", agent.ExecutablePath);
    }

    [Fact]
    public void SupportsPreassignedSessionId_IsFalse()
    {
        var agent = CreateAgent();

        Assert.False(agent.SupportsPreassignedSessionId);
    }

    [Fact]
    public void SupportsStudioMode_IsFalse()
    {
        var agent = CreateAgent();

        Assert.False(agent.SupportsStudioMode);
    }

    [Fact]
    public void BuildLaunchSpec_NoResume_DoesNotPreassignSessionId()
    {
        var agent = CreateAgent();

        var spec = agent.BuildLaunchSpec(userArgs: null, resumeSessionId: null, studioMode: false);

        Assert.NotNull(spec);
        Assert.Null(spec.PreassignedSessionId);
    }

    [Fact]
    public void BuildLaunchSpec_IgnoresResume_StillNoPreassignedSessionId()
    {
        var agent = CreateAgent();

        var spec = agent.BuildLaunchSpec(userArgs: "--flag", resumeSessionId: "abc-123", studioMode: true);

        Assert.NotNull(spec);
        Assert.Null(spec.PreassignedSessionId);
    }
}
