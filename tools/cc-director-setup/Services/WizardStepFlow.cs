using CcDirector.Setup.Engine;

namespace CcDirectorSetup.Services;

/// <summary>
/// Pure navigation policy for the install wizard's step rail. Separated from the WPF
/// <see cref="MainWindow"/> so the role-aware step ordering is unit-testable without a UI.
///
/// The step ids match MainWindow's wizard step numbers: 1 Welcome, 2 Prerequisites, 3 Sign in,
/// 4 Privacy, 5 Connect, 6 Skills, 7 Install, 8 Complete.
///
/// Two steps are role-aware, and they are exact inverses of each other:
///   - Sign-in (step 3) applies only to a Gateway install. A Workstation signs in through its
///     gateway, not in the installer, so the wizard skips step 3 for the Workstation role (issue #679).
///   - Connect (step 5) is the mandatory gateway-pairing step and applies only to a fresh Workstation
///     install (issue #646). The Gateway role IS the gateway, and an update keeps its existing
///     connection, so both skip step 5.
///
/// Everything else is shown for every install. This type owns the single role-aware ordering so
/// MainWindow has no parallel navigation logic.
/// </summary>
public static class WizardStepFlow
{
    /// <summary>The Sign-in step id. Gateway-only; skipped for a Workstation.</summary>
    public const int StepSignIn = 3;

    /// <summary>The Connect (gateway-pairing) step id. Fresh-Workstation-only; skipped for a Gateway
    /// install and for any update.</summary>
    public const int StepConnect = 5;

    private static readonly int[] AllSteps = [1, 2, StepSignIn, 4, StepConnect, 6, 7, 8];

    /// <summary>True when the in-wizard Sign-in step applies to the given role (Gateway only).</summary>
    public static bool SignInApplies(InstallRole role) => role == InstallRole.Gateway;

    /// <summary>True when the mandatory gateway-pairing Connect step applies: a fresh Workstation
    /// install. A Gateway install and any update skip it (issue #646).</summary>
    public static bool ConnectApplies(InstallRole role, bool isUpdate) =>
        !isUpdate && role == InstallRole.Workstation;

    /// <summary>True when the given step id is hidden for this role / install-kind combination.</summary>
    private static bool IsSkipped(int step, InstallRole role, bool isUpdate) =>
        (step == StepSignIn && !SignInApplies(role)) ||
        (step == StepConnect && !ConnectApplies(role, isUpdate));

    /// <summary>The step ids shown for the given role and install kind, in order. The Sign-in step (3)
    /// is dropped for a Workstation and the Connect step (5) is dropped for a Gateway / any update, so
    /// the rail flows with no gaps.</summary>
    public static List<int> VisibleSteps(InstallRole role, bool isUpdate)
    {
        var steps = new List<int>(AllSteps.Length);
        foreach (var step in AllSteps)
            if (!IsSkipped(step, role, isUpdate))
                steps.Add(step);
        return steps;
    }

    /// <summary>The next visible step after <paramref name="step"/>, skipping any step that does not
    /// apply to this role / install kind.</summary>
    public static int NextStep(int step, InstallRole role, bool isUpdate)
    {
        var next = step + 1;
        while (IsSkipped(next, role, isUpdate))
            next++;
        return next;
    }

    /// <summary>The previous visible step before <paramref name="step"/>, skipping any step that does
    /// not apply to this role / install kind.</summary>
    public static int PrevStep(int step, InstallRole role, bool isUpdate)
    {
        var prev = step - 1;
        while (prev > 0 && IsSkipped(prev, role, isUpdate))
            prev--;
        return prev;
    }
}
