using CcDirector.Gateway.Teams;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>The states of a request and which change is allowed from each (devthrottle_internal#2308).</summary>
public sealed class TeamRequestStatesTests
{
    [Theory]
    [InlineData(TeamRequestStates.Sent, "Sent")]
    [InlineData(TeamRequestStates.Accepted, "Accepted")]
    [InlineData(TeamRequestStates.Declined, "Not doing this")]
    [InlineData(TeamRequestStates.Done, "Done")]
    public void Label_EveryState_IsTheWordsTheScreensShow(string state, string label)
    {
        Assert.Equal(label, TeamRequestStates.Label(state));
    }

    [Fact]
    public void Label_UnknownState_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => TeamRequestStates.Label("lost"));
    }

    [Theory]
    [InlineData(TeamRequestDecision.Accept, TeamRequestStates.Accepted)]
    [InlineData(TeamRequestDecision.Decline, TeamRequestStates.Declined)]
    [InlineData(TeamRequestDecision.MarkDone, TeamRequestStates.Done)]
    public void StateAfter_EveryDecision_IsItsState(TeamRequestDecision decision, string state)
    {
        Assert.Equal(state, TeamRequestStates.StateAfter(decision));
    }

    [Fact]
    public void StateAfter_NotADecision_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TeamRequestStates.StateAfter((TeamRequestDecision)9));
    }

    [Theory]
    [InlineData(TeamRequestStates.Sent, TeamRequestDecision.Accept, true)]
    [InlineData(TeamRequestStates.Sent, TeamRequestDecision.Decline, true)]
    [InlineData(TeamRequestStates.Sent, TeamRequestDecision.MarkDone, true)]
    [InlineData(TeamRequestStates.Accepted, TeamRequestDecision.Accept, false)]
    [InlineData(TeamRequestStates.Accepted, TeamRequestDecision.Decline, true)]
    [InlineData(TeamRequestStates.Accepted, TeamRequestDecision.MarkDone, true)]
    [InlineData(TeamRequestStates.Declined, TeamRequestDecision.Accept, false)]
    [InlineData(TeamRequestStates.Declined, TeamRequestDecision.Decline, false)]
    [InlineData(TeamRequestStates.Declined, TeamRequestDecision.MarkDone, false)]
    [InlineData(TeamRequestStates.Done, TeamRequestDecision.Accept, false)]
    [InlineData(TeamRequestStates.Done, TeamRequestDecision.Decline, false)]
    [InlineData(TeamRequestStates.Done, TeamRequestDecision.MarkDone, false)]
    public void MayMove_EveryStateAndDecision_IsTheRule(string state, TeamRequestDecision decision, bool allowed)
    {
        Assert.Equal(allowed, TeamRequestStates.MayMove(state, decision));
    }

    [Theory]
    [InlineData(TeamRequestStates.Accepted, TeamRequestDecision.Accept)]
    [InlineData(TeamRequestStates.Declined, TeamRequestDecision.Accept)]
    [InlineData(TeamRequestStates.Done, TeamRequestDecision.MarkDone)]
    public void MoveRefusal_EveryRefusedMove_HasASentence(string state, TeamRequestDecision decision)
    {
        Assert.False(string.IsNullOrWhiteSpace(TeamRequestStates.MoveRefusal(state, decision)));
    }

    [Fact]
    public void MoveRefusal_AnAllowedMove_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => TeamRequestStates.MoveRefusal(TeamRequestStates.Sent, TeamRequestDecision.Accept));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("Add a dark mode", true)]
    public void TextRefusal_EmptyOrNot_IsTheRule(string? text, bool usable)
    {
        Assert.Equal(usable, TeamRequestStates.TextRefusal(text) is null);
    }

    [Fact]
    public void TextRefusal_ExactlyTheLimit_IsUsable_OneMoreIsNot()
    {
        Assert.Null(TeamRequestStates.TextRefusal(new string('x', TeamRequestStates.MaxTextLength)));
        Assert.NotNull(TeamRequestStates.TextRefusal(new string('x', TeamRequestStates.MaxTextLength + 1)));
    }

    [Theory]
    [InlineData(TeamRequestDecision.Decline, null, false)]
    [InlineData(TeamRequestDecision.Decline, " ", false)]
    [InlineData(TeamRequestDecision.Decline, "Out of scope", true)]
    [InlineData(TeamRequestDecision.Accept, null, true)]
    [InlineData(TeamRequestDecision.MarkDone, null, true)]
    public void ReasonRefusal_RequiredForNotDoingThisOnly(TeamRequestDecision decision, string? reason, bool usable)
    {
        Assert.Equal(usable, TeamRequestStates.ReasonRefusal(decision, reason) is null);
    }

    [Fact]
    public void ReasonRefusal_PastTheLimit_IsRefused()
    {
        Assert.NotNull(TeamRequestStates.ReasonRefusal(TeamRequestDecision.Decline, new string('x', TeamRequestStates.MaxReasonLength + 1)));
    }
}
