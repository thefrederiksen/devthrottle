using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Util;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A session may type into a session it owns, and into no other (Parent Control, fix 1). The guard lets the prompt
/// shape through for every session key; this is the rule the route then applies with the target in hand.
/// </summary>
public sealed class OwnedSessionInputTests
{
    private const string Parent = "aaaaaaaa-1111-1111-1111-111111111111";
    private const string Child = "bbbbbbbb-2222-2222-2222-222222222222";
    private const string Stranger = "cccccccc-3333-3333-3333-333333333333";

    private static SessionDto Session(string id, string? owner) => new() { SessionId = id, ControllerSessionId = owner };

    [Fact]
    public void Refusal_TargetOwnedByCaller_IsNull()
        => Assert.Null(OwnedSessionInput.Refusal(Parent, Session(Child, Parent), directorChecksBeforeTyping: true, appendEnter: true));

    [Fact]
    public void Refusal_OwnerIdInAnotherCase_IsStillTheCallersOwn()
        => Assert.Null(OwnedSessionInput.Refusal(Parent.ToUpperInvariant(), Session(Child, Parent), true, true));

    [Fact]
    public void Refusal_TargetTheOwnerRunsDirectly_IsNotYourSession()
        => Assert.Equal(AgentInputRefusal.NotYourSession,
            OwnedSessionInput.Refusal(Parent, Session(Child, owner: null), true, true));

    [Fact]
    public void Refusal_TargetOwnedByAnotherSession_IsNotYourSession()
        => Assert.Equal(AgentInputRefusal.NotYourSession,
            OwnedSessionInput.Refusal(Parent, Session(Child, Stranger), true, true));

    [Fact]
    public void Refusal_Grandchild_IsNotYourSession()
    {
        // The grandchild is owned by the child, not by the caller: only the direct owner may type.
        const string grandchild = "dddddddd-4444-4444-4444-444444444444";
        Assert.Equal(AgentInputRefusal.NotYourSession,
            OwnedSessionInput.Refusal(Parent, Session(grandchild, Child), true, true));
    }

    [Fact]
    public void Refusal_CallerIsTheTarget_IsItself()
        => Assert.Equal(AgentInputRefusal.Itself,
            OwnedSessionInput.Refusal(Parent, Session(Parent, owner: null), true, true));

    [Fact]
    public void Refusal_CallerIsTheTargetAndNamesItselfOwner_IsItself()
        => Assert.Equal(AgentInputRefusal.Itself,
            OwnedSessionInput.Refusal(Parent, Session(Parent, Parent), true, true));

    [Fact]
    public void Refusal_OwnershipWaivedForARaisedCaller_StillRequiresTheCheckingDirector()
    {
        Assert.Null(OwnedSessionInput.Refusal(Parent, Session(Child, Stranger), true, true, ownershipWaived: true));
        Assert.Equal(AgentInputRefusal.DirectorTooOld,
            OwnedSessionInput.Refusal(Parent, Session(Child, Stranger), false, true, ownershipWaived: true));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void ProvesGuardedSend_OnlyAnAcceptanceWithTheCheck_Counts(bool accepted, bool idleChecked, bool expected)
        => Assert.Equal(expected, OwnedSessionInput.ProvesGuardedSend(new PromptResponse { Accepted = accepted, IdleChecked = idleChecked }));

    [Theory]
    [InlineData(int.MaxValue, OwnedSessionInput.MaxWaitMs)]
    [InlineData(5_000, 5_000)]
    [InlineData(-1, 0)]
    public void ApplyTo_TheWaitIsBoundedForAnAgent(int asked, int expected)
    {
        var req = new PromptRequest { Text = "go", WaitForIdle = true, TimeoutMs = asked };

        OwnedSessionInput.ApplyTo(req);

        Assert.Equal(expected, req.TimeoutMs);
    }

    [Fact]
    public void Refusal_DirectorDoesNotCheckBeforeTyping_IsDirectorTooOld()
        => Assert.Equal(AgentInputRefusal.DirectorTooOld,
            OwnedSessionInput.Refusal(Parent, Session(Child, Parent), directorChecksBeforeTyping: false, appendEnter: true));

    [Fact]
    public void Refusal_AskedToLeaveTheTextUnsent_IsNoSubmit()
        => Assert.Equal(AgentInputRefusal.NoSubmit,
            OwnedSessionInput.Refusal(Parent, Session(Child, Parent), true, appendEnter: false));

    [Fact]
    public void Refusal_NotYourSession_IsDecidedBeforeTheDirectorIsConsidered()
        => Assert.Equal(AgentInputRefusal.NotYourSession,
            OwnedSessionInput.Refusal(Parent, Session(Child, Stranger), directorChecksBeforeTyping: false, appendEnter: false));

    [Fact]
    public void ApplyTo_AnyBody_TypesOnlyWhenWaitingAndAlwaysSubmits()
    {
        var req = new PromptRequest { Text = "go", AppendEnter = false, AgentDriven = false, OnlyWhenWaitingForInput = false };

        OwnedSessionInput.ApplyTo(req);

        Assert.True(req.OnlyWhenWaitingForInput);
        Assert.True(req.AppendEnter);
        Assert.True(req.AgentDriven);
        Assert.Equal("go", req.Text);
    }

    [Fact]
    public void DescribeRefusedSend_OwnerDraft_SaysTheOwnersWordsAreNeverTypedOver()
    {
        var said = OwnedSessionInput.DescribeRefusedSend(new PromptResponse
        {
            Accepted = false, RefusedBusy = true, RefusedFor = PromptResponse.RefusedForOwnerDraft, ActivityState = "WaitingForInput",
        });

        Assert.StartsWith("Nothing was typed", said);
        Assert.Contains("not sent them", said);
        Assert.Contains("never typed over", said);
    }

    [Fact]
    public void DescribeRefusedSend_Busy_NamesTheStateItWasIn()
    {
        var said = OwnedSessionInput.DescribeRefusedSend(new PromptResponse
        {
            Accepted = false, RefusedBusy = true, RefusedFor = null, ActivityState = "Working",
        });

        Assert.Contains("not waiting for a prompt", said);
        Assert.Contains("Working", said);
    }
}
