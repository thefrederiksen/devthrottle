using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CcDirector.Core.Utilities;

namespace CcDirector.GatewayApp;

public partial class App : Application
{
    private GatewayTrayController? _controller;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The gateway has no main window: it lives in the tray and must NOT exit when
            // the (nonexistent) last window closes. Only the tray "Quit" item shuts it down.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            try
            {
                _controller = new GatewayTrayController(desktop, GatewayAppOptions.Port);
                _controller.Start();
            }
            catch (Exception ex)
            {
                FileLog.Write($"[App] Controller start FAILED: {ex}");
                throw;
            }

            desktop.ShutdownRequested += (_, _) => _controller?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
