using CcDirector.Setup.Engine;
using CcDirectorSetup.Services;
using Xunit;

namespace CcDirectorSetup.Tests;

/// <summary>
/// Tests for the wizard's role-aware step ordering. Two steps are role-aware and are exact inverses:
///   - the in-wizard Sign-in step (step 3) applies only to a Gateway install (a Workstation signs in
///     through its gateway, so the wizard skips step 3, issue #679);
///   - the mandatory gateway-pairing Connect step (step 5) applies only to a fresh Workstation install
///     (the Gateway IS the gateway, and an update keeps its connection, so both skip step 5, issue #646).
///
/// These tests pin that the visible-step list, the forward/back navigation, and the SignInApplies /
/// ConnectApplies predicates all agree: a fresh Workstation rail drops Sign-in and includes Connect; a
/// Gateway rail includes Sign-in and excludes Connect; an update never shows Connect for either role.
/// </summary>
public sealed class WizardStepFlowTests
{
    // Full step ids: 1 Welcome, 2 Prerequisites, 3 Sign-in, 4 Privacy, 5 Connect, 6 Skills, 7 Install,
    // 8 Complete.

    [Fact]
    public void VisibleSteps_GatewayFreshInstall_IncludesSignInExcludesConnect()
    {
        var steps = WizardStepFlow.VisibleSteps(InstallRole.Gateway, isUpdate: false);

        // Gateway keeps Sign-in (3) and drops the Connect/pairing step (5), so the rail flows
        // 4 -> Skills (6) with no gap.
        Assert.Equal([1, 2, 3, 4, 6, 7, 8], steps);
        Assert.Contains(WizardStepFlow.StepSignIn, steps);
        Assert.DoesNotContain(WizardStepFlow.StepConnect, steps);
    }

    [Fact]
    public void VisibleSteps_WorkstationFreshInstall_DropsSignInIncludesConnect()
    {
        var steps = WizardStepFlow.VisibleSteps(InstallRole.Workstation, isUpdate: false);

        // Sign-in (3) is gone (2 flows straight into Privacy) and the mandatory Connect/pairing step (5)
        // is present right after Privacy. No gaps either side.
        Assert.Equal([1, 2, 4, 5, 6, 7, 8], steps);
        Assert.DoesNotContain(WizardStepFlow.StepSignIn, steps);
        Assert.Contains(WizardStepFlow.StepConnect, steps);
    }

    [Fact]
    public void VisibleSteps_WorkstationUpdate_DropsBothSignInAndConnect()
    {
        // An update keeps the existing gateway connection, so a Workstation update shows neither the
        // Sign-in step nor the mandatory Connect step.
        var steps = WizardStepFlow.VisibleSteps(InstallRole.Workstation, isUpdate: true);

        Assert.Equal([1, 2, 4, 6, 7, 8], steps);
        Assert.DoesNotContain(WizardStepFlow.StepSignIn, steps);
        Assert.DoesNotContain(WizardStepFlow.StepConnect, steps);
    }

    [Fact]
    public void VisibleSteps_GatewayUpdate_KeepsSignInExcludesConnect()
    {
        var steps = WizardStepFlow.VisibleSteps(InstallRole.Gateway, isUpdate: true);

        Assert.Equal([1, 2, 3, 4, 6, 7, 8], steps);
        Assert.Contains(WizardStepFlow.StepSignIn, steps);
        Assert.DoesNotContain(WizardStepFlow.StepConnect, steps);
    }

    [Fact]
    public void SignInApplies_OnlyForGateway()
    {
        Assert.True(WizardStepFlow.SignInApplies(InstallRole.Gateway));
        Assert.False(WizardStepFlow.SignInApplies(InstallRole.Workstation));
    }

    [Fact]
    public void ConnectApplies_OnlyForFreshWorkstation()
    {
        // The mandatory gateway-pairing step is exactly: a fresh Workstation install.
        Assert.True(WizardStepFlow.ConnectApplies(InstallRole.Workstation, isUpdate: false));
        Assert.False(WizardStepFlow.ConnectApplies(InstallRole.Gateway, isUpdate: false));
        Assert.False(WizardStepFlow.ConnectApplies(InstallRole.Workstation, isUpdate: true));
        Assert.False(WizardStepFlow.ConnectApplies(InstallRole.Gateway, isUpdate: true));
    }

    [Fact]
    public void NextStep_WorkstationFresh_SkipsSignInThenLandsOnConnect()
    {
        // Leaving Prerequisites (2) lands on Privacy (4), not Sign-in (3).
        Assert.Equal(4, WizardStepFlow.NextStep(2, InstallRole.Workstation, isUpdate: false));
        // Leaving Privacy (4) lands on the mandatory Connect/pairing step (5).
        Assert.Equal(5, WizardStepFlow.NextStep(4, InstallRole.Workstation, isUpdate: false));
        // Leaving Connect (5) lands on Skills (6).
        Assert.Equal(6, WizardStepFlow.NextStep(5, InstallRole.Workstation, isUpdate: false));
    }

    [Fact]
    public void NextStep_GatewayFresh_LandsOnSignInThenSkipsConnect()
    {
        // The Gateway path shows Sign-in (3) right after Prerequisites (2).
        Assert.Equal(3, WizardStepFlow.NextStep(2, InstallRole.Gateway, isUpdate: false));
        // Leaving Privacy (4) skips the Connect step (5) and lands on Skills (6).
        Assert.Equal(6, WizardStepFlow.NextStep(4, InstallRole.Gateway, isUpdate: false));
    }

    [Fact]
    public void PrevStep_WorkstationFresh_SkipsSignInGoingBackAndStopsOnConnect()
    {
        // Back from Privacy (4) lands on Prerequisites (2), not Sign-in (3).
        Assert.Equal(2, WizardStepFlow.PrevStep(4, InstallRole.Workstation, isUpdate: false));
        // Back from Skills (6) lands on the Connect step (5).
        Assert.Equal(5, WizardStepFlow.PrevStep(6, InstallRole.Workstation, isUpdate: false));
    }

    [Fact]
    public void PrevStep_GatewayFresh_LandsOnSignInAndSkipsConnect()
    {
        Assert.Equal(3, WizardStepFlow.PrevStep(4, InstallRole.Gateway, isUpdate: false));
        // Back from Skills (6) skips the Connect step (5) and lands on Privacy (4).
        Assert.Equal(4, WizardStepFlow.PrevStep(6, InstallRole.Gateway, isUpdate: false));
    }

    [Theory]
    [InlineData(InstallRole.Gateway, false)]
    [InlineData(InstallRole.Workstation, false)]
    [InlineData(InstallRole.Gateway, true)]
    [InlineData(InstallRole.Workstation, true)]
    public void NextStep_WalkingForwardFromWelcome_VisitsExactlyTheVisibleSteps(InstallRole role, bool isUpdate)
    {
        var visible = WizardStepFlow.VisibleSteps(role, isUpdate);

        // Walk from the first step using NextStep and confirm it reproduces the visible-step sequence.
        var walked = new List<int> { visible[0] };
        var step = visible[0];
        while (step < visible[^1])
        {
            step = WizardStepFlow.NextStep(step, role, isUpdate);
            walked.Add(step);
        }

        Assert.Equal(visible, walked);
    }

    [Theory]
    [InlineData(InstallRole.Gateway, false)]
    [InlineData(InstallRole.Workstation, false)]
    [InlineData(InstallRole.Gateway, true)]
    [InlineData(InstallRole.Workstation, true)]
    public void PrevStep_WalkingBackFromComplete_VisitsExactlyTheVisibleSteps(InstallRole role, bool isUpdate)
    {
        var visible = WizardStepFlow.VisibleSteps(role, isUpdate);

        // Walk backward from the last step using PrevStep and confirm it reproduces the visible-step
        // sequence in reverse.
        var walked = new List<int> { visible[^1] };
        var step = visible[^1];
        while (step > visible[0])
        {
            step = WizardStepFlow.PrevStep(step, role, isUpdate);
            walked.Add(step);
        }
        walked.Reverse();

        Assert.Equal(visible, walked);
    }
}
