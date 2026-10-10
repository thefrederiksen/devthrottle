using CcDirector.Core.ErrorReports;
using CcDirector.Setup.Engine;

namespace CcDirector.Setup.Cli;

/// <summary>
/// What `install` reports and says around the install itself (issue #3722).
///
/// Before this, `install` sent a report only when the launcher failed to start. A real Mac signup on
/// 9 Oct 2026 downloaded the setup CLI from the website's copied command, and after that we saw nothing: we
/// could not tell "never ran install" from "install worked and they never opened the app" from "install
/// failed". And a successful install ended at "Install complete" with no word about what to do next, so a
/// person who used the command line never learned to open DevThrottle and sign in.
///
/// Now `install` sends <see cref="StepStart"/> before it begins and <see cref="StepDone"/> or
/// <see cref="StepFailed"/> when it ends, on the same public route and under the same install id as every
/// other installer report; and a successful Workstation install ends with the next step in plain words.
/// </summary>
internal static class InstallProgress
{
    public const string Component = "setup-cli";
    public const string StepStart = "start";
    public const string StepDone = "done";
    public const string StepFailed = "failed";

    public static string StartMessage(InstallRole role, string? tag) =>
        InstallTag.Append($"setup-cli install started ({RoleName(role)}).", tag);

    public static string DoneMessage(InstallRole role, string? tag) =>
        InstallTag.Append($"OK: setup-cli install finished ({RoleName(role)}).", tag);

    public static string FailedMessage(InstallRole role, int exitCode, string? tag) =>
        InstallTag.Append($"setup-cli install ended with exit code {exitCode} ({RoleName(role)}). The failing step is on the person's screen and in the setup log.", tag);

    public static string CrashedMessage(InstallRole role, Exception ex, string? tag) =>
        InstallTag.Append($"setup-cli install stopped on {ex.GetType().Name}: {ex.Message} ({RoleName(role)}).", tag);

    /// <summary>
    /// The lines printed after a successful install: what to do next on this machine. Only a Workstation
    /// install gets them - a Gateway install is signed in with `signin`, which its own output already says.
    /// </summary>
    public static IReadOnlyList<string> NextStep(InstallRole role, bool isMac, bool isWindows)
    {
        if (role != InstallRole.Workstation) return [];
        var where = isMac ? "your Applications folder" : isWindows ? "the Start menu" : "your app menu";
        return
        [
            "",
            "Next step: open DevThrottle from " + where + " and click Sign in and connect.",
            "That connects this machine to your account. Until then it does not show up in DevThrottle.",
        ];
    }

    private static string RoleName(InstallRole role) => role == InstallRole.Gateway ? "gateway" : "workstation";
}
