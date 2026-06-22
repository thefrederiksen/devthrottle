using CcDirector.Setup.Engine;

namespace CcDirectorSetup.Services;

/// <summary>
/// Pure navigation policy for the install wizard's step rail. Separated from the WPF
/// <see cref="MainWindow"/> so the role-aware step ordering is unit-testable without a UI.
///
/// The step ids match MainWindow's wizard step numbers: 1 Welcome, 2 Prerequisites, 3 Sign in,
/// 4 Privacy, 5 Skills, 6 Install, 7 Complete. The Sign-in step applies only to a Gateway install -
/// a Workstation connects to an existing Gateway and signs in there - so the wizard skips step 3
/// entirely for the Workstation role.
/// </summary>
public static class WizardStepFlow
{
    /// <summary>The Sign-in step id. Gateway-only; skipped for a Workstation.</summary>
    public const int StepSignIn = 3;

    private static readonly int[] AllSteps = [1, 2, StepSignIn, 4, 5, 6, 7];

    /// <summary>True when the in-wizard Sign-in step applies to the given role (Gateway only).</summary>
    public static bool SignInApplies(InstallRole role) => role == InstallRole.Gateway;

    /// <summary>The step ids shown for the given role, in order. The Sign-in step (3) is dropped for a
    /// Workstation, so the rail flows 2 -> Privacy with no gap.</summary>
    public static List<int> VisibleSteps(InstallRole role)
    {
        var steps = new List<int>(AllSteps);
        if (!SignInApplies(role))
            steps.Remove(StepSignIn);
        return steps;
    }

    /// <summary>The next visible step after <paramref name="step"/>, skipping Sign-in for a Workstation.</summary>
    public static int NextStep(int step, InstallRole role)
    {
        var next = step + 1;
        if (next == StepSignIn && !SignInApplies(role))
            next++;
        return next;
    }

    /// <summary>The previous visible step before <paramref name="step"/>, skipping Sign-in for a Workstation.</summary>
    public static int PrevStep(int step, InstallRole role)
    {
        var prev = step - 1;
        if (prev == StepSignIn && !SignInApplies(role))
            prev--;
        return prev;
    }
}
