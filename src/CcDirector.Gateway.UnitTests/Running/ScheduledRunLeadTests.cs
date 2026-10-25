using CcDirector.ControlApi;
using CcDirector.Core.Agents;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Gateway.Tests.Running;

/// <summary>
/// A SCHEDULED RUN'S PASTED SEED HAS A LEAD TYPED IN FRONT OF IT (issue #3967). The create command chooses the sentence
/// for a schedule's own Claude Code session only; every other first prompt - a person's own above all - is given no lead.
/// </summary>
public sealed class ScheduledRunLeadTests
{
    [Fact]
    public void FirstPromptLead_AScheduleFiringAClaudeCodeSession_IsGivenTheScheduledRunLead()
    {
        Assert.Equal(Session.ScheduledRunLead, SessionCommandExecutor.FirstPromptLead(AgentKind.ClaudeCode, SessionOrigin.Cron));
    }

    /// <summary>A workflow seat and a trigger's session carry the schedule kind too, but are not scheduled runs: the
    /// sentence would not be true of them (review of issue #3967).</summary>
    public static TheoryData<string, string> NotAScheduleFiring => new()
    {
        { SessionOriginKinds.Schedule, SessionOriginSurfaces.Workflow },
        { SessionOriginKinds.Schedule, SessionOriginSurfaces.Trigger },
        { SessionOriginKinds.Schedule, SessionOriginSurfaces.Unknown },
        { SessionOriginKinds.Human, SessionOriginSurfaces.Desktop },
        { SessionOriginKinds.Human, SessionOriginSurfaces.Cron },
        { SessionOriginKinds.Agent, SessionOriginSurfaces.Cron },
        { SessionOriginKinds.Unknown, SessionOriginSurfaces.Unknown },
    };

    [Theory]
    [MemberData(nameof(NotAScheduleFiring))]
    public void FirstPromptLead_AClaudeCodeSessionNotStartedByAScheduleFiring_IsGivenNone(string kind, string surface)
    {
        Assert.Null(SessionCommandExecutor.FirstPromptLead(AgentKind.ClaudeCode, new SessionOrigin(kind, surface)));
    }

    [Theory]
    [InlineData(AgentKind.Codex)]
    [InlineData(AgentKind.Pi)]
    public void FirstPromptLead_AScheduleFiringAnotherAgent_IsGivenNone(AgentKind kind)
    {
        Assert.Null(SessionCommandExecutor.FirstPromptLead(kind, SessionOrigin.Cron));
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

    /// <summary>A leaded paste that did not arrive leaves the lead and the paste in the composer; the mark that sizes the
    /// clear holds both, so a short paste does not leave part of the lead behind (review of issue #3967).</summary>
    [Fact]
    public void HeldWithLead_APasteSentWithALead_HoldsTheLeadThenThePaste()
    {
        Assert.Equal(Session.ScheduledRunLead + " line one\nline two", Session.HeldWithLead(Session.ScheduledRunLead, "line one\nline two"));
    }

    [Fact]
    public void HeldWithLead_AFileReferenceTypedAfterTheLead_IsNotGivenTheLeadTwice()
    {
        var typed = Session.ScheduledRunLead + " @.temp/input_1.txt";

        Assert.Equal(typed, Session.HeldWithLead(Session.ScheduledRunLead, typed));
    }

    [Fact]
    public void HeldWithLead_NoLead_IsTheTextAsTyped()
    {
        Assert.Equal("Run the weekly report.", Session.HeldWithLead(null, "Run the weekly report."));
    }
}
