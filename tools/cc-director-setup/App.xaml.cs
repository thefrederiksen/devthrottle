using System.Windows;
using CcDirector.Setup.Engine;
using CcDirectorSetup.Services;

namespace CcDirectorSetup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Route the shared engine's detailed step logs (Director swap, Python tools extract/venv/pip,
        // SHA verify) into the setup log. Without this the engine's log lines are discarded
        // (EngineLog defaults to a no-op), leaving the log blank during the apply phase - exactly
        // where installs stall - so a failed/stuck install can't be diagnosed.
        EngineLog.Sink = SetupLog.Write;
        base.OnStartup(e);
    }
}
