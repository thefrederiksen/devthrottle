namespace CcDirector.Launcher;

/// <summary>
/// Process-wide options resolved from the command line in Program.Main before the
/// Avalonia app is constructed. A static holder is the clean way to hand parsed args
/// to the app (Avalonia instantiates the App class itself).
/// </summary>
public static class LauncherAppOptions
{
    /// <summary>Default loopback REST port for the launcher API.</summary>
    public const int DefaultPort = 7900;

    /// <summary>Port the in-process REST host listens on. Override with --port N.</summary>
    public static int Port { get; set; } = DefaultPort;

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
