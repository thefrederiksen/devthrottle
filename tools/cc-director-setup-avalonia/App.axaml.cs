using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CcDirector.Setup.Engine;
using CcDirectorSetup.Services;

namespace CcDirectorSetup;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Route the shared engine's detailed step logs (Director placement, Python tools
        // extract/venv/pip, SHA verify) into the setup log. EngineLog defaults to a no-op, so
        // without this the log is blank during the apply phase - exactly where installs stall.
        EngineLog.Sink = SetupLog.Write;

        // An exception on the window's thread: log it, report it, and say so on screen instead of letting
        // the wizard vanish. The install step is left where it stopped, with Retry offered (#3311).
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            SetupLog.Write($"[App] UI-thread exception: {e.Exception}");
            _ = WizardErrorReport.SendAsync("wizard", "ui-thread", $"The setup wizard hit an error: {e.Exception.GetType().Name}: {e.Exception.Message}", e.Exception);
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow window })
                window.ShowUnexpectedError(e.Exception);
            e.Handled = true;
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
