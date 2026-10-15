using CcDirector.Core.Agents;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// A SEAT'S MODEL ON ITS AGENT'S OWN LAUNCH LINE (Factory Definition mission, phase 5, design section 2): each agent's
/// own flag, a model on the default line replaced, the person's explicit arguments untouched when they name a model,
/// and an agent with no model flag refused with the reason.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class AgentLaunchModelTests
{
    [Theory]
    [InlineData(AgentKind.ClaudeCode, "--model claude-opus-5-5")]
    [InlineData(AgentKind.Codex, "-m claude-opus-5-5")]
    [InlineData(AgentKind.Gemini, "--model claude-opus-5-5")]
    [InlineData(AgentKind.Pi, "--model claude-opus-5-5")]
    [InlineData(AgentKind.OpenCode, "--model claude-opus-5-5")]
    [InlineData(AgentKind.Grok, "--model claude-opus-5-5")]
    [InlineData(AgentKind.Copilot, "--model claude-opus-5-5")]
    public void Compose_EachAgent_PutsTheModelOnItsOwnFlag(AgentKind kind, string expected)
    {
        var result = AgentLaunchModel.Compose(kind, "", argumentsAreExplicit: false, "claude-opus-5-5");

        Assert.True(result.Ok);
        Assert.Equal(expected, result.Arguments);
    }

    [Theory]
    [InlineData(AgentKind.ClaudeCode, "--permission-mode auto --model sonnet", "--permission-mode auto --model claude-opus-5-5")]
    [InlineData(AgentKind.ClaudeCode, "--model=sonnet --permission-mode auto", "--permission-mode auto --model claude-opus-5-5")]
    [InlineData(AgentKind.Codex, "--dangerously-bypass-approvals-and-sandbox -m o3", "--dangerously-bypass-approvals-and-sandbox -m claude-opus-5-5")]
    [InlineData(AgentKind.Codex, "--model o3 --full-auto", "--full-auto -m claude-opus-5-5")]
    [InlineData(AgentKind.Grok, "-m grok-4 --always-approve", "--always-approve --model claude-opus-5-5")]
    public void Compose_ADefaultLineThatCarriesAModel_HasItReplaced(AgentKind kind, string defaults, string expected)
    {
        var result = AgentLaunchModel.Compose(kind, defaults, argumentsAreExplicit: false, "claude-opus-5-5");

        Assert.Equal(expected, result.Arguments);
        Assert.False(result.ArgsNamedAModel);
    }

    [Fact]
    public void Compose_ExplicitArgumentsThatNameAModel_AreReturnedUntouched()
    {
        var result = AgentLaunchModel.Compose(AgentKind.ClaudeCode, "--model haiku --verbose", argumentsAreExplicit: true, "claude-opus-5-5");

        Assert.True(result.Ok);
        Assert.Equal("--model haiku --verbose", result.Arguments);
        Assert.True(result.ArgsNamedAModel);
    }

    [Fact]
    public void Compose_ExplicitArgumentsThatNameNoModel_GetTheModelAdded()
    {
        var result = AgentLaunchModel.Compose(AgentKind.ClaudeCode, "--verbose", argumentsAreExplicit: true, "claude-opus-5-5");

        Assert.Equal("--verbose --model claude-opus-5-5", result.Arguments);
    }

    [Theory]
    [InlineData(AgentKind.Cursor)]
    [InlineData(AgentKind.RawCli)]
    public void Compose_AnAgentWithNoModelFlag_IsRefusedWithTheReason(AgentKind kind)
    {
        var result = AgentLaunchModel.Compose(kind, "--force", argumentsAreExplicit: false, "claude-opus-5-5");

        Assert.False(result.Ok);
        Assert.Null(result.Arguments);
        Assert.Equal($"The agent {kind} takes no model on its command line, so the model 'claude-opus-5-5' cannot be applied " +
                     "and no session was started. Remove the model from the seat, or give the seat an agent that takes one.",
            result.Refusal);
    }

    [Fact]
    public void Compose_AModelWithASpace_IsRefused()
    {
        var result = AgentLaunchModel.Compose(AgentKind.ClaudeCode, "", argumentsAreExplicit: false, "opus --dangerously-skip-permissions");

        Assert.False(result.Ok);
        Assert.StartsWith("The model 'opus --dangerously-skip-permissions' has a space or a quote in it", result.Refusal);
    }
}
