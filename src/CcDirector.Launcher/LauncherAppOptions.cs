namespace CcDirector.Launcher;

/// <summary>
/// Process-wide options resolved from the command line in Program.Main before the
/// Avalonia app is constructed. A static holder is the clean way to hand parsed args
/// to the app (Avalonia instantiates the App class itself).
/// </summary>
public static class LauncherAppOptions
{
    /// <summary>When true, register the HKCU Run-key autostart entry on startup. --no-autostart disables.</summary>
    public static bool RegisterAutostart { get; set; } = true;

    /// <summary>
    /// Installed mode (--managed): run the periodic self-update check. Off by default so a dev launch
    /// never self-updates a repo build. The installer launches the shipped launcher with --managed.
    /// </summary>
    public static bool Managed { get; set; }

    /// <summary>The flag the Windows autostart entry carries, so the launcher knows it was started by a sign-in to Windows.</summary>
    public const string AtLoginFlag = "--at-login";

    /// <summary>
    /// Started by the autostart entry at sign-in to Windows (--at-login), not by the installer, a
    /// self-update or a person. Only then does the launcher open a Director that has never signed in
    /// (issue #3503): a launch from the installer would open it mid-wizard with the installer's stale
    /// PATH, and the wizard opens it itself.
    /// </summary>
    public static bool AtLogin { get; set; }

    /// <summary>The arguments equivalent to the current options, for the autostart entry.</summary>
    public static string? AutostartArguments() => AutostartArgumentsFor(Managed, OperatingSystem.IsWindows());

    /// <summary>
    /// <see cref="AutostartArguments"/> for given facts. Only the Windows Run key carries
    /// <see cref="AtLoginFlag"/>: the macOS launch agent is also written by the installer with fixed
    /// arguments, and a launcher that rewrote it would reload the agent under itself.
    /// </summary>
    internal static string? AutostartArgumentsFor(bool managed, bool windows)
    {
        var args = new List<string>();
        if (managed) args.Add("--managed");
        if (windows) args.Add(AtLoginFlag);
        return args.Count == 0 ? null : string.Join(' ', args);
    }

    /// <summary>Parse the supported flags: --no-autostart, --managed, --at-login. Unknown flags are ignored, so an
    /// autostart entry written by an older build (which could carry --port) still starts this one.</summary>
    public static void Parse(string[] args)
    {
        // Every flag starts from its default, so the options always describe THESE arguments and
        // nothing left over from an earlier parse.
        RegisterAutostart = true;
        Managed = false;
        AtLogin = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--no-autostart")
            {
                RegisterAutostart = false;
            }
            else if (args[i] == "--managed")
            {
                Managed = true;
            }
            else if (args[i] == AtLoginFlag)
            {
                AtLogin = true;
            }
        }
    }
}
