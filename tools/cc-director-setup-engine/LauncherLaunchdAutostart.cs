using System.Runtime.Versioning;
using System.Security;
using System.Text;

namespace CcDirector.Setup.Engine;

/// <summary>
/// The CC Launcher's per-user autostart on macOS: a launchd user launch agent at
/// ~/Library/LaunchAgents/com.devthrottle.cc-launcher.plist. The macOS twin of
/// <see cref="LauncherAutostart"/> (the Windows Run key), living in the engine for the
/// same reason: the installer, the uninstaller, and the launcher itself must agree on
/// one label, one property list path, and one command-line format.
///
/// The agent is registered with RunAtLoad (start at login) and KeepAlive with
/// SuccessfulExit=false: launchd resurrects the launcher after a crash or kill, but a
/// CLEAN exit (the tray Quit item, or the POST /shutdown a self-update helper sends)
/// stays exited - otherwise launchd would race the self-update helper by relaunching
/// the old binary the moment it stopped.
///
/// Registration is a two-step: write the property list, then hand it to launchd with
/// "launchctl bootstrap gui/&lt;uid&gt;". Bootstrap also starts the agent immediately
/// (RunAtLoad applies at bootstrap time); when the launcher registers itself at startup
/// this spawns a short-lived duplicate that exits through the single-instance mutex.
/// </summary>
public static class LauncherLaunchdAutostart
{
    /// <summary>The launchd service label (also the property list file name).</summary>
    public const string Label = "com.devthrottle.cc-launcher";

    /// <summary>The user launch-agent property list path: ~/Library/LaunchAgents/{Label}.plist.</summary>
    public static string PlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", Label + ".plist");

    /// <summary>
    /// The full property list for the given executable and arguments. Pure, for tests.
    /// Standard output and error go to logDir so a crash before FileLog starts is not silent.
    /// </summary>
    public static string PlistContent(string exePath, string? arguments, string logDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(logDir);

        var argElements = new StringBuilder();
        argElements.Append($"        <string>{Xml(exePath)}</string>\n");
        foreach (var arg in SplitArguments(arguments))
            argElements.Append($"        <string>{Xml(arg)}</string>\n");

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{Label}</string>
                <key>ProgramArguments</key>
                <array>
            {argElements.ToString().TrimEnd('\n')}
                </array>
                <key>RunAtLoad</key>
                <true/>
                <key>KeepAlive</key>
                <dict>
                    <key>SuccessfulExit</key>
                    <false/>
                </dict>
                <key>ProcessType</key>
                <string>Interactive</string>
                <key>StandardOutPath</key>
                <string>{Xml(Path.Combine(logDir, "launchd-stdout.log"))}</string>
                <key>StandardErrorPath</key>
                <string>{Xml(Path.Combine(logDir, "launchd-stderr.log"))}</string>
            </dict>
            </plist>

            """;
    }

    /// <summary>
    /// Ensure the launch agent is registered and loaded for the given executable and
    /// arguments. Idempotent: returns true if a write or a launchd (re)bootstrap was
    /// performed, false if everything was already correct.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static bool EnsureRegistered(string exePath, string? arguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        var logDir = Path.Combine(InstallLayout.Default().LogsDir, "launcher");
        var desired = PlistContent(exePath, arguments, logDir);
        EngineLog.Write($"[LauncherLaunchdAutostart] EnsureRegistered: exe={exePath}, args={arguments ?? "(none)"}");

        var plistUnchanged = File.Exists(PlistPath)
            && string.Equals(File.ReadAllText(PlistPath), desired, StringComparison.Ordinal);

        if (plistUnchanged && IsLoaded())
        {
            EngineLog.Write("[LauncherLaunchdAutostart] EnsureRegistered: already up to date and loaded");
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
        Directory.CreateDirectory(logDir);
        File.WriteAllText(PlistPath, desired);
        EngineLog.Write($"[LauncherLaunchdAutostart] EnsureRegistered: wrote {PlistPath}");

        // A changed definition must be re-bootstrapped: launchd caches the loaded plist.
        if (IsLoaded())
        {
            var (outExit, outText) = ProcessRunner.Run("/bin/launchctl", $"bootout gui/{UserId()}/{Label}");
            EngineLog.Write($"[LauncherLaunchdAutostart] bootout -> exit={outExit} {Trim(outText)}");
        }

        var (exit, text) = ProcessRunner.Run("/bin/launchctl", $"bootstrap gui/{UserId()} \"{PlistPath}\"");
        if (exit != 0)
            throw new InvalidOperationException(
                $"launchctl bootstrap failed (exit {exit}): {Trim(text)}");

        EngineLog.Write("[LauncherLaunchdAutostart] EnsureRegistered: bootstrapped launch agent");
        return true;
    }

    /// <summary>The registered command line (ProgramArguments joined), or null when the property list does not exist.</summary>
    public static string? Registered()
    {
        if (!File.Exists(PlistPath)) return null;
        var content = File.ReadAllText(PlistPath);
        var strings = new List<string>();
        var start = content.IndexOf("<array>", StringComparison.Ordinal);
        var end = content.IndexOf("</array>", StringComparison.Ordinal);
        if (start < 0 || end < 0) return null;
        var body = content[start..end];
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("<string>", StringComparison.Ordinal) && trimmed.EndsWith("</string>", StringComparison.Ordinal))
                strings.Add(Unxml(trimmed["<string>".Length..^"</string>".Length]));
        }
        return strings.Count == 0 ? null : string.Join(' ', strings);
    }

    /// <summary>True if the launch-agent property list exists.</summary>
    public static bool IsRegistered() => File.Exists(PlistPath);

    /// <summary>
    /// Unload the launch agent from launchd and remove the property list.
    /// Returns true if anything was removed.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static bool Unregister()
    {
        EngineLog.Write("[LauncherLaunchdAutostart] Unregister");
        var existed = File.Exists(PlistPath);

        if (IsLoaded())
        {
            var (exit, text) = ProcessRunner.Run("/bin/launchctl", $"bootout gui/{UserId()}/{Label}");
            EngineLog.Write($"[LauncherLaunchdAutostart] bootout -> exit={exit} {Trim(text)}");
        }

        if (existed)
        {
            File.Delete(PlistPath);
            EngineLog.Write($"[LauncherLaunchdAutostart] Unregister: removed {PlistPath}");
        }
        return existed;
    }

    /// <summary>
    /// Unregister, and say whether the job was actually BOOTED OUT. <see cref="Unregister"/> logged a
    /// nonzero bootout and then reported success, which mattered: a job still loaded keeps its
    /// KeepAlive definition, so launchd restarts the launcher after a stop has already been certified
    /// and the restart binds the port again while files are being deleted.
    /// </summary>
    /// <param name="failure">Why it could not be unregistered, or null on success.</param>
    [SupportedOSPlatform("macos")]
    public static bool UnregisterVerified(out string? failure)
    {
        failure = null;
        EngineLog.Write("[LauncherLaunchdAutostart] UnregisterVerified");
        var existed = File.Exists(PlistPath);

        if (IsLoaded())
        {
            var (exit, text) = ProcessRunner.Run("/bin/launchctl", $"bootout gui/{UserId()}/{Label}");
            EngineLog.Write($"[LauncherLaunchdAutostart] bootout -> exit={exit} {Trim(text)}");
            // Still loaded after asking it to go is the case that used to pass for success.
            if (exit != 0 && IsLoaded())
                failure = $"launchctl bootout failed (exit {exit}): {Trim(text)}";
        }

        try
        {
            if (existed) File.Delete(PlistPath);
        }
        catch (Exception ex)
        {
            failure ??= $"could not delete {PlistPath}: {ex.Message}";
        }

        return failure is null;
    }


    /// <summary>
    /// Stop and restart the launch agent, so a launcher build that has just been REPLACED on disk
    /// becomes the one that is running.
    ///
    /// WITHOUT THIS THE SWAP IS INVISIBLE. The replaced file sits on disk while the old process keeps
    /// running - Unix hands a running process the inode it started from, so it neither notices nor
    /// cares that the path now points somewhere else - and the machine goes on serving the old build
    /// until the next login. The update looks done and has not happened.
    ///
    /// KICKSTART, NOT A STOP FOLLOWED BY A START. KeepAlive is SuccessfulExit=false, so a launcher that
    /// exits CLEANLY is deliberately not respawned: a stop on its own would leave the machine with no
    /// launcher at all until the user logs in again. <c>kickstart -k</c> is the one operation that both
    /// ends the running instance and starts the replacement, performed by the supervisor that owns the
    /// job rather than raced against it.
    ///
    /// IT RETURNS WHETHER THE REQUEST WAS ACCEPTED, NEVER WHETHER THE NEW BUILD IS ANY GOOD. launchd
    /// also throttles respawns to roughly ten seconds, so a check made immediately after this returns
    /// finds nothing and means nothing. The caller's health check is what decides.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static bool Kickstart()
    {
        try
        {
            var (exit, text) = ProcessRunner.Run("/bin/launchctl", $"kickstart -k gui/{UserId()}/{Label}");
            EngineLog.Write($"[LauncherLaunchdAutostart] kickstart -k -> exit={exit} {Trim(text)}");
            return exit == 0;
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[LauncherLaunchdAutostart] kickstart FAILED: {ex.Message}");
            return false;
        }
    }

    /// <summary>Whether launchd currently has the agent loaded in this user's gui domain.</summary>
    [SupportedOSPlatform("macos")]
    public static bool IsLoaded()
    {
        var (exit, _) = ProcessRunner.Run("/bin/launchctl", $"print gui/{UserId()}/{Label}");
        return exit == 0;
    }

    private static string UserId()
    {
        var (exit, output) = ProcessRunner.Run("/usr/bin/id", "-u");
        if (exit != 0 || !int.TryParse(output.Trim(), out var uid))
            throw new InvalidOperationException($"could not resolve the current user id (id -u exit {exit})");
        return uid.ToString();
    }

    /// <summary>Split the stored argument string on whitespace (no quoting in launcher arguments today).</summary>
    private static IEnumerable<string> SplitArguments(string? arguments) =>
        string.IsNullOrWhiteSpace(arguments)
            ? Array.Empty<string>()
            : arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Xml(string value) => SecurityElement.Escape(value);

    private static string Unxml(string value) => value
        .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"")
        .Replace("&apos;", "'").Replace("&amp;", "&");

    private static string Trim(string text) => text.Length > 300 ? text[..300] + "..." : text.Trim();
}
