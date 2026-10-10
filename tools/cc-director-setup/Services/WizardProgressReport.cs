using CcDirector.Setup.Engine;

namespace CcDirectorSetup.Services;

/// <summary>
/// The Windows wizard's reports to DevThrottle: start, done and failed (issue #3722), and every failed step,
/// every error it shows and every crash (issue #3640). This is the wizard published as the Windows
/// <c>cc-director-setup.exe</c>; before #3640 a failed component, an error on the uninstall or finish screen and
/// an unhandled exception stayed in the setup log on the person's machine. Same public route and install id as
/// every other installer (<see cref="InstallFailureReporter"/>), and the same scrubbing.
///
/// A report is a copy for us: it is sent without waiting, and one that cannot be delivered is logged by the
/// reporter and changes nothing on the person's screen. Only the crash report is waited for, briefly, because
/// the process is about to die.
/// </summary>
internal static class WizardProgressReport
{
    public const string Installer = "setup-wizard-win";
    public const string Component = "setup-wizard";

    /// <summary>How many lines of the setup log travel with a failure report.</summary>
    public const int LogTailLines = 60;

    /// <summary>How long a crash report may hold a dying process.</summary>
    public static readonly TimeSpan CrashWait = TimeSpan.FromSeconds(10);

    private static Lazy<InstallFailureReporter> _reporter =
        new(() => new InstallFailureReporter(InstallLayout.Default(), Installer));

    /// <summary>Test seam: send every report through <paramref name="reporter"/> instead of the hosted Gateway.</summary>
    internal static void UseReporterForTests(InstallFailureReporter reporter) => _reporter = new(() => reporter);

    public static void Start(InstallRole role, bool isUpdate) =>
        Send("start", $"Windows setup wizard {(isUpdate ? "update" : "install")} started ({RoleName(role)}).", null, withLog: false);

    public static void Done(InstallRole role, bool isUpdate, string detail) =>
        Send("done", $"OK: Windows setup wizard {(isUpdate ? "update" : "install")} finished ({RoleName(role)}): {detail}", null, withLog: false);

    public static void Failed(InstallRole role, string what, string? diagnostics) =>
        Send("failed", $"Windows setup wizard stopped ({RoleName(role)}): {what}", diagnostics, withLog: true);

    /// <summary>
    /// One report per component the install could not place. The card says "Failed" on screen; this is the
    /// same failure on our side. Returns the reports, so a test can wait for them; the wizard does not.
    /// </summary>
    public static IReadOnlyList<Task<bool>> ComponentsFailed(IEnumerable<ApplyResult> results)
    {
        var sends = new List<Task<bool>>();
        foreach (var r in results.Where(r => r.Status == ApplyStatus.Failed))
        {
            var message = $"Windows setup wizard could not install {r.ComponentId}: {r.Error ?? "no reason was given"}";
            var detail = $"from version: {r.FromVersion ?? "none"}" + Environment.NewLine + $"to version: {r.ToVersion ?? "unknown"}";
            sends.Add(SendFor(r.ComponentId, "place", message, detail, withLog: true));
        }
        return sends;
    }

    /// <summary>An error the wizard shows the person, or a step that failed, with the exception that caused it.</summary>
    public static Task<bool> Error(string step, string message, Exception? ex) =>
        SendFor(Component, step, message, ex?.ToString(), withLog: true);

    /// <summary>
    /// An uninstall that failed. Sent like <see cref="Error"/>, except when the person chose to delete their data
    /// and the folder is gone: the report needs the install id, and reading it would make the folder again
    /// underneath a person who asked for it to be gone. That case is logged with its reason and not sent.
    /// </summary>
    public static Task<bool> UninstallError(string message, Exception? ex)
    {
        var root = InstallLayout.Default().LocalRoot;
        if (!Directory.Exists(root))
        {
            SetupLog.Write($"[WizardProgressReport] uninstall: NOT reported, because the data folder was deleted at the person's request and the report would recreate it: {message}");
            return Task.FromResult(false);
        }
        return Error("uninstall", message, ex);
    }

    /// <summary>The wizard is about to die of <paramref name="ex"/>: send it and wait, at most <see cref="CrashWait"/>.</summary>
    public static void CrashAndWait(string step, Exception? ex)
    {
        var message = $"The Windows setup wizard crashed: {ex?.GetType().Name}: {ex?.Message}";
        try
        {
            if (!SendFor(Component, step, message, ex?.ToString(), withLog: true).Wait(CrashWait))
                SetupLog.Write($"[WizardProgressReport] {step}: the crash report did not finish within {CrashWait.TotalSeconds:0} seconds");
        }
        catch (Exception waitEx)
        {
            // Not reported: this IS the report failing, in a process that is dying; the setup log is all that is left.
            SetupLog.Write($"[WizardProgressReport] {step}: waiting for the crash report FAILED: {waitEx.GetType().Name}: {waitEx.Message}");
        }
    }

    private static void Send(string step, string message, string? diagnostics, bool withLog) =>
        _ = SendFor(Component, step, message, diagnostics, withLog);

    private static Task<bool> SendFor(string component, string step, string message, string? diagnostics, bool withLog)
    {
        SetupLog.Write($"[WizardProgressReport] {component}/{step}: {message}");
        return SendCore(component, step, message, diagnostics, withLog);
    }

    /// <summary>Build the diagnostics off the caller's thread - reading the log tail is file input, and the caller
    /// is usually the window - and send. The reporter logs a failed delivery and never throws for one.</summary>
    private static async Task<bool> SendCore(string component, string step, string message, string? diagnostics, bool withLog)
    {
        var text = withLog ? await Task.Run(() => WithLog(diagnostics)).ConfigureAwait(false) : diagnostics;
        return await _reporter.Value.ReportAsync(component, step, message, text).ConfigureAwait(false);
    }

    /// <summary>The step's own diagnostics, then the last lines of this run's setup log.</summary>
    internal static string WithLog(string? diagnostics)
    {
        string tail;
        try
        {
            tail = LaunchdDiagnostics.Tail(SetupLog.Path, LogTailLines) ?? "(the setup log is empty or missing)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not reported: not on its own - the log is being written at this moment, or is gone with an uninstall. The
            // report this belongs to still goes, and says why it has no log.
            tail = $"(the setup log could not be read: {ex.GetType().Name}: {ex.Message})";
        }
        var head = string.IsNullOrEmpty(diagnostics) ? "" : diagnostics + Environment.NewLine;
        return $"{head}setup log (last {LogTailLines} lines of {Path.GetFileName(SetupLog.Path)}):{Environment.NewLine}{tail}";
    }

    private static string RoleName(InstallRole role) => role == InstallRole.Gateway ? "gateway" : "workstation";
}
