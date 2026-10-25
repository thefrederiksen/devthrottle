using CcDirector.ControlApi;
using CcDirector.Core.Agents;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Gateway.Tests.Running;

/// <summary>
/// A SCHEDULED RUN'S PASTED SEED HAS A LEAD TYPED IN FRONT OF IT (issue #3967). The create command chooses the sentence
/// for a schedule's Claude Code session only; every other first prompt - a person's own above all - is given no lead.
/// </summary>
public sealed class ScheduledRunLeadTests
{
    [Fact]
    public void FirstPromptLead_AScheduledClaudeCodeSession_IsGivenTheScheduledRunLead()
    {
        Assert.Equal(Session.ScheduledRunLead, SessionCommandExecutor.FirstPromptLead(AgentKind.ClaudeCode, SessionOriginKinds.Schedule));
    }

    [Theory]
    [InlineData(SessionOriginKinds.Human)]
    [InlineData(SessionOriginKinds.Agent)]
    [InlineData(SessionOriginKinds.Unknown)]
    [InlineData(null)]
    public void FirstPromptLead_AClaudeCodeSessionNotStartedByASchedule_IsGivenNone(string? origin)
    {
        Assert.Null(SessionCommandExecutor.FirstPromptLead(AgentKind.ClaudeCode, origin));
    }

    [Theory]
    [InlineData(AgentKind.Codex)]
    [InlineData(AgentKind.Pi)]
    public void FirstPromptLead_AScheduledSessionOfAnotherAgent_IsGivenNone(AgentKind kind)
    {
        Assert.Null(SessionCommandExecutor.FirstPromptLead(kind, SessionOriginKinds.Schedule));
    }

    /// <summary>The lead is typed key by key in front of a paste: one short line of words, no control characters.</summary>
    [Fact]
    public void ScheduledRunLead_IsOneShortLineOfWords()
    {
        Assert.DoesNotContain('\n', Session.ScheduledRunLead);
        Assert.DoesNotContain('\t', Session.ScheduledRunLead);
        Assert.True(Session.ScheduledRunLead.Length <= 120, $"the lead is {Session.ScheduledRunLead.Length} characters");
        Assert.False(Session.ScheduledRunLead.StartsWith('/') || Session.ScheduledRunLead.StartsWith('@') || Session.ScheduledRunLead.StartsWith('!'));
    }
}
