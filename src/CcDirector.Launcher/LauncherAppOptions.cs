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

    /// <summary>
    /// Installed mode (--managed): run the periodic self-update check. Off by default so a dev launch
    /// never self-updates a repo build. The installer launches the shipped launcher with --managed.
    /// </summary>
    public static bool Managed { get; set; }

    /// <summary>The arguments equivalent to the current options, for the autostart Run key.</summary>
    public static string? AutostartArguments() => Managed ? "--managed" : null;

    /// <summary>Parse the supported flags: --port N, --no-autostart, --managed.</summary>
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
            else if (args[i] == "--managed")
            {
                Managed = true;
            }
        }
    }
}
