using CcDirector.Gateway;

namespace CcDirector.GatewayApp;

/// <summary>
/// Process-wide options resolved from the command line in Program.Main before the
/// Avalonia app is constructed. The Avalonia framework instantiates <see cref="App"/>
/// itself, so there is no constructor seam to pass these through - a static holder is
/// the clean way to hand parsed args to the app.
/// </summary>
public static class GatewayAppOptions
{
    /// <summary>Port the in-process gateway listens on. Override with --port N (used for tests).</summary>
    public static int Port { get; set; } = GatewayHost.DefaultPort;

    /// <summary>When true, register the HKCU Run-key autostart entry on startup. --no-autostart disables.</summary>
    public static bool RegisterAutostart { get; set; } = true;

    /// <summary>Parse the supported flags: --port N, --no-autostart.</summary>
    public static void Parse(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out var p))
            {
                Port = p;
                i++;
            }
            else if (args[i] == "--no-autostart")
            {
                RegisterAutostart = false;
            }
        }
    }
}
