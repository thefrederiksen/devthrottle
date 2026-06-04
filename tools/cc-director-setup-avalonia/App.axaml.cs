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

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
