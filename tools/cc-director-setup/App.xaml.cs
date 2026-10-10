using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CcDirector.Setup.Engine;
using CcDirectorSetup.Services;

namespace CcDirectorSetup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Render the wizard purely in software (issue #1730). On some Windows machines the WPF
        // hardware-composition path (Direct3D 9Ex handed to the Desktop Window Manager) never presents
        // a single frame: the window opens, every step object is built and the setup log runs clean,
        // but the whole client area stays blank white - only the OS-drawn title bar and Close button
        // appear. It was reproduced on Windows 11 (SORENLAPTOP) on the update path with no exception in
        // the setup log or the Windows event log, while the Director - which uses Avalonia's separate
        // GPU path - painted fine on the same desktop, confirming the fault is WPF-specific, not the GPU.
        // The wizard is short-lived and visually simple, so software rendering costs nothing perceptible
        // and it must paint on any GPU or driver. This MUST be set before the first window is created
        // (base.OnStartup builds the StartupUri window), so it runs first.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        SetupLog.Write("[App] OnStartup: forced WPF software rendering (RenderMode.SoftwareOnly) for #1730");

        // Route the shared engine's detailed step logs (Director swap, Python tools extract/venv/pip,
        // SHA verify) into the setup log. Without this the engine's log lines are discarded
        // (EngineLog defaults to a no-op), leaving the log blank during the apply phase - exactly
        // where installs stall - so a failed/stuck install can't be diagnosed.
        EngineLog.Sink = SetupLog.Write;

        // A crash reaches DevThrottle, not only this machine's setup log (issue #3640). An error on the window's
        // thread is shown, reported and the wizard carries on, so the person can still read the screen and open
        // the log; an error anywhere else ends the process, so its report is waited for, briefly, first.
        DispatcherUnhandledException += (_, args) =>
        {
            SetupLog.Write($"[App] UNHANDLED UI-THREAD EXCEPTION: {args.Exception}");
            _ = WizardProgressReport.Error("ui-thread", $"The Windows setup wizard hit an error: {args.Exception.GetType().Name}: {args.Exception.Message}", args.Exception);
            MessageBox.Show($"The setup wizard hit an error and reported it to DevThrottle:\n\n{args.Exception.Message}\n\nThe log of this run is {SetupLog.Path}",
                "DevThrottle Setup", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            SetupLog.Write($"[App] UNHANDLED EXCEPTION (terminating={args.IsTerminating}): {args.ExceptionObject}");
            WizardProgressReport.CrashAndWait("crash", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            SetupLog.Write($"[App] UNOBSERVED TASK: {args.Exception}");
            _ = WizardProgressReport.Error("unobserved-task", $"A background task in the Windows setup wizard failed: {args.Exception.GetBaseException().Message}", args.Exception);
            args.SetObserved();
        };

        // Windows started us from the copy inside the install root (the Uninstall button in
        // Settings > Apps runs the UninstallString we registered there). Uninstalling from inside
        // the tree we are about to delete would hold a file open in it, so hand the job to a copy
        // in the temp directory and get out of the way. No window is created on this path.
        if (OperatingSystem.IsWindows()
            && LaunchedToUninstall(e.Args)
            && UninstallRegistration.ShouldRelaunchFromTemp(InstallLayout.Default())
            && UninstallRegistration.RelaunchFromTemp())
        {
            SetupLog.Write("[App] handed the uninstall to a temp copy; exiting so the install root is unlocked");
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    /// <summary>True when the process was started with the uninstall switch (/uninstall, -uninstall).</summary>
    private static bool LaunchedToUninstall(string[] args) =>
        args.Any(a => string.Equals(a.TrimStart('-', '/'), "uninstall", StringComparison.OrdinalIgnoreCase));
}
