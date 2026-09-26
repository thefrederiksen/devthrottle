using CcDirector.Setup.Engine;

namespace CcDirectorSetup.Services;

/// <summary>
/// Every error the wizard shows or dies of reaches DevThrottle (issue #3311), not only the step failures
/// <see cref="EngineInstallRunner"/> already reports. It uses the same public, no-sign-in install report
/// channel and the same per-machine install id, and it attaches the exception and the tail of this run's
/// setup log, so a report can be read without asking the user for files.
///
/// Sending never throws and never blocks the wizard for long: <see cref="InstallFailureReporter"/> already
/// swallows delivery failures into the setup log, and every send here is bounded.
/// </summary>
public static class WizardErrorReport
{
    /// <summary>How long one report may take before the wizard stops waiting for it.</summary>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How many lines of the setup log travel with a report.</summary>
    public const int LogTailLines = 60;

    private static readonly Lazy<InstallFailureReporter> Reporter =
        new(() => new InstallFailureReporter(InstallLayout.Default(), "setup-wizard"));

    /// <summary>Send one error. Returns whether the Gateway accepted it.</summary>
    public static async Task<bool> SendAsync(string component, string step, string message, Exception? ex = null)
    {
        try
        {
            using var cts = new CancellationTokenSource(SendTimeout);
            // Reading the log tail is file input: never on the caller's thread, which is often the window's.
            var diagnostics = await Task.Run(() => Diagnostics(ex)).ConfigureAwait(false);
            var sent = await Reporter.Value.ReportAsync(component, step, message, diagnostics, cts.Token).ConfigureAwait(false);
            SetupLog.Write($"[WizardErrorReport] {component}/{step}: report {(sent ? "sent" : "NOT delivered")}");
            return sent;
        }
        catch (Exception reportEx)
        {
            SetupLog.Write($"[WizardErrorReport] {component}/{step}: report could not be built ({reportEx.GetType().Name}): {reportEx.Message}");
            return false;
        }
    }

    /// <summary>Send one error and wait for it, for a process that is about to die (the crash handlers).</summary>
    public static void SendAndWait(string component, string step, string message, Exception? ex)
    {
        try
        {
            SendAsync(component, step, message, ex).Wait(SendTimeout + TimeSpan.FromSeconds(2));
        }
        catch (Exception waitEx)
        {
            SetupLog.Write($"[WizardErrorReport] {component}/{step}: wait for the report failed: {waitEx.Message}");
        }
    }

    /// <summary>The exception in full, then the last lines of this run's setup log.</summary>
    internal static string Diagnostics(Exception? ex)
    {
        var tail = LaunchdDiagnostics.Tail(SetupLog.Path, LogTailLines) ?? "(the setup log is empty or missing)";
        var head = ex is null ? "" : $"exception:\n{ex}\n";
        return $"{head}setup log (last {LogTailLines} lines of {Path.GetFileName(SetupLog.Path)}):\n{tail}";
    }
}
