using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CcDirector.Avalonia;
using CcDirector.Core.Configuration;

// Headless render proof for issue #651: bring up the REAL Director SettingsDialog and capture the
// Account tab - the read-only Gateway connection + signed-in identity panel - to a PNG. The dialog's
// LoadAsync reads config.json (pointed at the local stub Gateway by the proof script) and the Account
// panel reads GET /account/status from that stub, so the captured identity line is the live value.
//
// Runs in-process with Avalonia.Headless + Skia drawing, so it needs no interactive desktop (this agent
// runs in a non-interactive session where GDI screen capture is unavailable).

var outPath = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "settings-account-live.png");

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

// The real SettingsDialog reads AgentOptions off Application.Current (App.Options) when it loads the
// agent list. Seed a default AgentOptions on the App so LoadAsync completes and the tabs become visible;
// the Account panel itself reads only config.json + the Gateway, which the proof script has wired.
var app = (App)Application.Current!;
typeof(App).GetProperty("Options")!.SetValue(app, new AgentOptions());

int exitCode = 0;
Dispatcher.UIThread.Invoke(() =>
{
    try
    {
        var dialog = new SettingsDialog();
        dialog.Width = 1040;
        dialog.Height = 1010;
        dialog.Show();

        // Pump the dispatcher so the Loaded handler (LoadAsync -> config read + /account/status fetch)
        // runs and the identity line updates. Headless time is virtual, so advance it in slices and let
        // the real background HTTP call to the stub complete.
        for (int i = 0; i < 60; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(100);
            Dispatcher.UIThread.RunJobs();
        }

        var frame = dialog.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("CaptureRenderedFrame returned null");
        frame.Save(outPath);
        Console.WriteLine($"[render-harness] saved {outPath}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[render-harness] FAILED: {ex}");
        exitCode = 1;
    }
});

return exitCode;
