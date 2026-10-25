using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A PERMISSION PROMPT ALWAYS REACHES THE OWNER (the owner's ruling of 24 October 2026: "If I say so, directly, it can
/// ask me for approval. But if I approve, it has to do it."). A supervised session that stops is snoozed to its holder -
/// except at a permission prompt, which only a person may answer: an ask rule exists precisely so that no agent
/// approves it. Parked away from the owner, it is a silent block by another name.
/// </summary>
public sealed class PermissionPromptReachesTheOwnerTests
{
    private static SessionDto HeldWorker(string activityState)
    {
        var worker = new SessionDto
        {
            SessionId = "worker",
            Name = "worker",
            ActivityState = activityState,
            IsControlled = true,
            ControllerSessionId = "supervisor",
        };
        var supervisor = new SessionDto { SessionId = "supervisor", Name = "supervisor", ActivityState = "Working" };
        FleetRoleResolver.Stamp(new List<SessionDto> { worker, supervisor });
        Assert.True(worker.HasLiveSupervisor);
        return worker;
    }

    [Fact]
    public void Classify_ASupervisedSessionAtAPermissionPrompt_NeedsYou_AndIsRed()
    {
        var worker = HeldWorker("WaitingForPerm");

        Assert.Equal(SessionOrdering.TriageBucket.NeedsYou, SessionOrdering.Classify(worker));
        Assert.Equal("red", SessionOrdering.EffectiveColor(worker));
        Assert.NotEqual("Snoozed", SessionOrdering.StateLabel(worker));
    }

    [Fact]
    public void Classify_ASupervisedSessionThatStoppedForAnythingElse_IsStillSnoozedToItsHolder()
    {
        // The control: only the permission prompt leaves the supervised rule; an ordinary stop still reports to the
        // session that holds it.
        var worker = HeldWorker("WaitingForInput");

        Assert.Equal(SessionOrdering.TriageBucket.OnHold, SessionOrdering.Classify(worker));
        Assert.Equal("Snoozed", SessionOrdering.StateLabel(worker));
    }

    [Fact]
    public void Classify_AnUnsupervisedSessionAtAPermissionPrompt_NeedsYou()
    {
        var session = new SessionDto { SessionId = "s", Name = "s", ActivityState = "WaitingForPerm" };

        Assert.Equal(SessionOrdering.TriageBucket.NeedsYou, SessionOrdering.Classify(session));
    }
}
