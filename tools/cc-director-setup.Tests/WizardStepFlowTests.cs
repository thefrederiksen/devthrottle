using CcDirector.Setup.Engine;
using CcDirectorSetup.Services;
using Xunit;

namespace CcDirectorSetup.Tests;

/// <summary>
/// Tests for the wizard's role-aware step ordering. The in-wizard Sign-in step (step 3) applies only to a
/// Gateway install; a Workstation connects to an existing Gateway and signs in there, so the wizard skips
/// step 3 entirely for the Workstation role. These tests pin that the visible-step list, the forward/back
/// navigation, and the SignInApplies predicate all agree on dropping Sign-in for a Workstation while a
/// Gateway keeps the full 7-step flow.
/// </summary>
public sealed class WizardStepFlowTests
{
    [Fact]
    public void VisibleSteps_Gateway_KeepsAllSevenStepsInOrder()
    {
        var steps = WizardStepFlow.VisibleSteps(InstallRole.Gateway);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], steps);
    }

    [Fact]
    public void VisibleSteps_Workstation_DropsSignInAndHasNoGap()
    {
        var steps = WizardStepFlow.VisibleSteps(InstallRole.Workstation);

        // Sign-in (3) is gone and the remaining steps stay in order: 2 flows straight into Privacy (4).
        Assert.Equal([1, 2, 4, 5, 6, 7], steps);
        Assert.DoesNotContain(WizardStepFlow.StepSignIn, steps);
    }

    [Fact]
    public void SignInApplies_OnlyForGateway()
    {
        Assert.True(WizardStepFlow.SignInApplies(InstallRole.Gateway));
        Assert.False(WizardStepFlow.SignInApplies(InstallRole.Workstation));
    }

    [Fact]
    public void NextStep_Workstation_SkipsSignInGoingForward()
    {
        // Leaving Prerequisites (2) lands on Privacy (4), not Sign-in (3).
        Assert.Equal(4, WizardStepFlow.NextStep(2, InstallRole.Workstation));
    }

    [Fact]
    public void NextStep_Gateway_LandsOnSignIn()
    {
        // The Gateway path still shows Sign-in (3) right after Prerequisites (2).
        Assert.Equal(3, WizardStepFlow.NextStep(2, InstallRole.Gateway));
    }

    [Fact]
    public void PrevStep_Workstation_SkipsSignInGoingBack()
    {
        // Back from Privacy (4) lands on Prerequisites (2), not Sign-in (3).
        Assert.Equal(2, WizardStepFlow.PrevStep(4, InstallRole.Workstation));
    }

    [Fact]
    public void PrevStep_Gateway_LandsOnSignIn()
    {
        Assert.Equal(3, WizardStepFlow.PrevStep(4, InstallRole.Gateway));
    }

    [Theory]
    [InlineData(InstallRole.Gateway)]
    [InlineData(InstallRole.Workstation)]
    public void NextStep_WalkingForwardFromWelcome_VisitsExactlyTheVisibleSteps(InstallRole role)
    {
        var visible = WizardStepFlow.VisibleSteps(role);

        // Walk from the first step using NextStep and confirm it reproduces the visible-step sequence.
        var walked = new List<int> { visible[0] };
        var step = visible[0];
        while (step < visible[^1])
        {
            step = WizardStepFlow.NextStep(step, role);
            walked.Add(step);
        }

        Assert.Equal(visible, walked);
    }
}
