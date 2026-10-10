using CcDirector.Setup.Engine;

namespace CcDirectorSetup.Services;

/// <summary>
/// The Windows wizard's start, done and failed reports (issue #3722). Before this the Windows wizard sent no
/// install report at all, so a Windows install that stopped part-way, or finished and was never opened, left
/// no trace. Same public route and install id as every other installer (<see cref="InstallFailureReporter"/>).
///
/// A report is a copy for us: it is sent without waiting, and one that cannot be delivered is logged by the
/// reporter and changes nothing on the person's screen.
/// </summary>
internal static class WizardProgressReport
{
    public const string Installer = "setup-wizard-win";
    public const string Component = "setup-wizard";

    private static readonly Lazy<InstallFailureReporter> Reporter =
        new(() => new InstallFailureReporter(InstallLayout.Default(), Installer));

    public static void Start(InstallRole role, bool isUpdate) =>
        Send("start", $"Windows setup wizard {(isUpdate ? "update" : "install")} started ({RoleName(role)}).", null);

    public static void Done(InstallRole role, bool isUpdate, string detail) =>
        Send("done", $"OK: Windows setup wizard {(isUpdate ? "update" : "install")} finished ({RoleName(role)}): {detail}", null);

    public static void Failed(InstallRole role, string what, string? diagnostics) =>
        Send("failed", $"Windows setup wizard stopped ({RoleName(role)}): {what}", diagnostics);

    private static void Send(string step, string message, string? diagnostics)
    {
        SetupLog.Write($"[WizardProgressReport] {step}: {message}");
        _ = Reporter.Value.ReportAsync(Component, step, message, diagnostics);
    }

    private static string RoleName(InstallRole role) => role == InstallRole.Gateway ? "gateway" : "workstation";
}
