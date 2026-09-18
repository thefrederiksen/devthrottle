using System.Runtime.Versioning;

namespace CcDirector.Setup.Engine;

/// <summary>
/// The CC Launcher's per-user autostart on Linux: a <c>systemd --user</c> service unit at
/// ~/.config/systemd/user/devthrottle-launcher.service. The Linux twin of
/// <see cref="LauncherAutostart"/> (the Windows Run key) and <see cref="LauncherLaunchdAutostart"/>
/// (the macOS launch agent), and the direct counterpart of <see cref="GatewaySystemdAutostart"/>,
/// which does the same job for the Gateway.
///
/// It lives in the engine for the same reason its siblings do: the installer, the uninstaller, and the
/// launcher itself must agree on one unit name, one file path, and one command-line format.
///
/// WHY THIS FILE EXISTS AT ALL. Until it did, a Linux machine had no autostart mechanism of any kind -
/// <c>LauncherCore.RegisterAutostartSafe</c> fell out of the bottom of an
/// <c>IsWindows || IsMacOS</c> condition and recorded "not registered" as a normal outcome. So no
/// launcher ever ran on Linux; and because the launcher is what installs a staged Director update
/// (issue #1033), a Linux Director downloaded every release and installed none of them, silently and
/// for ever. This is the same defect macOS had when the launcher's update job sat behind an
/// <c>IsWindows</c> gate, one platform over.
///
/// Per-user (<c>--user</c>), never a system service: the launcher only does useful work while the user
/// is logged in - the whole fleet is logon-bound - exactly as the Windows side reasons about HKCU and
/// the macOS side about the gui domain. <c>WantedBy=default.target</c> starts it with the user session;
/// <c>Restart=on-failure</c> resurrects it after a crash but leaves a CLEAN exit (the shutdown
/// lifecycle signal a self-update raises) exited - the systemd analogue of launchd's
/// <c>KeepAlive SuccessfulExit=false</c>. <c>Restart=always</c> would fight every deliberate stop.
///
/// THE LAUNCHER RUNS HEADLESS ON LINUX, and that is why this unit needs no graphical session and names
/// no display. The launcher is a tray application on Windows and macOS; on Linux there is no dependable
/// tray (a GNOME desktop has not drawn one for years without an extension), and neither of the
/// launcher's two jobs - staging its own update, installing the Director's - needs a window. See
/// <c>Program.Main</c>, which routes Linux to the headless core directly.
///
/// SUPERVISION CHANGES HOW THE LAUNCHER IS REPLACED. Once this unit exists, something on Linux DOES
/// restart the launcher, so the swap order for a launcher update moves from stop-then-place-then-start
/// to place-then-restart. See <see cref="LauncherSwapOrder"/>, and <see cref="Restart"/> below.
/// </summary>
public static class LauncherSystemdAutostart
{
    /// <summary>The systemd unit name (also the unit file name).</summary>
    public const string UnitName = "devthrottle-launcher.service";

    /// <summary>The per-user unit file path: ~/.config/systemd/user/devthrottle-launcher.service.</summary>
    public static string UnitPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "systemd", "user", UnitName);

    /// <summary>
    /// The full unit file for the given executable and arguments. Pure, for tests. The ExecStart command
    /// is double-quoted so a path containing spaces stays one token.
    /// </summary>
    public static string UnitContent(string exePath, string? arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        var execStart = string.IsNullOrWhiteSpace(arguments)
            ? $"\"{exePath}\""
            : $"\"{exePath}\" {arguments}";

        return $"""
            [Unit]
            Description=DevThrottle Launcher
            After=network.target

            [Service]
            Type=simple
            ExecStart={execStart}
            Restart=on-failure

            [Install]
            WantedBy=default.target

            """;
    }

    /// <summary>
    /// Ensure the user unit is written and enabled (started now and at login) for the given executable
    /// and arguments. Idempotent: returns true if a write or a systemd (re)enable was performed, false
    /// if everything was already correct.
    /// </summary>
    [SupportedOSPlatform("linux")]
    public static bool EnsureRegistered(string exePath, string? arguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        var desired = UnitContent(exePath, arguments);
        EngineLog.Write($"[LauncherSystemdAutostart] EnsureRegistered: exe={exePath}, args={arguments ?? "(none)"}");

        var unitUnchanged = File.Exists(UnitPath)
            && string.Equals(File.ReadAllText(UnitPath), desired, StringComparison.Ordinal);

        if (unitUnchanged && IsEnabled())
        {
            EngineLog.Write("[LauncherSystemdAutostart] EnsureRegistered: already up to date and enabled");
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(UnitPath)!);
        File.WriteAllText(UnitPath, desired);
        EngineLog.Write($"[LauncherSystemdAutostart] EnsureRegistered: wrote {UnitPath}");

        // systemd caches units, so a changed definition must be reloaded before enable.
        var (reloadExit, reloadText) = ProcessRunner.Run("systemctl", "--user daemon-reload");
        EngineLog.Write($"[LauncherSystemdAutostart] daemon-reload -> exit={reloadExit} {Trim(reloadText)}");

        var (exit, text) = ProcessRunner.Run("systemctl", $"--user enable --now {UnitName}");
        if (exit != 0)
            throw new InvalidOperationException(
                $"systemctl --user enable --now {UnitName} failed (exit {exit}): {Trim(text)}");

        EngineLog.Write("[LauncherSystemdAutostart] EnsureRegistered: enabled user unit");
        return true;
    }

    /// <summary>The registered ExecStart command line, or null when the unit file does not exist.</summary>
    public static string? Registered()
    {
        if (!File.Exists(UnitPath)) return null;
        foreach (var line in File.ReadAllLines(UnitPath))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("ExecStart=", StringComparison.Ordinal))
                return trimmed["ExecStart=".Length..].Trim();
        }
        return null;
    }

    /// <summary>True if the user unit file exists.</summary>
    public static bool IsRegistered() => File.Exists(UnitPath);

    /// <summary>Whether systemd currently reports the user unit as enabled.</summary>
    [SupportedOSPlatform("linux")]
    public static bool IsEnabled()
    {
        var (exit, _) = ProcessRunner.Run("systemctl", $"--user is-enabled {UnitName}");
        return exit == 0;
    }

    /// <summary>Whether systemd currently reports the user unit as active (running).</summary>
    [SupportedOSPlatform("linux")]
    public static bool IsActive()
    {
        var (exit, _) = ProcessRunner.Run("systemctl", $"--user is-active {UnitName}");
        return exit == 0;
    }

    /// <summary>
    /// Disable and stop the user unit, then remove its unit file, and SAY SO WHEN IT DID NOT WORK.
    ///
    /// The verified form is the one the uninstaller must use, for the reason its macOS twin documents:
    /// a unit that is still loaded keeps its <c>Restart=on-failure</c> definition, so systemd can bring
    /// the launcher back after the uninstaller's stop has already been certified - and the restarted
    /// process then runs while the files around it are being deleted. Reporting a failed disable as
    /// success is how that becomes invisible.
    /// </summary>
    [SupportedOSPlatform("linux")]
    public static bool UnregisterVerified(out string? failure)
    {
        failure = null;
        EngineLog.Write("[LauncherSystemdAutostart] UnregisterVerified");
        var existed = File.Exists(UnitPath);

        if (existed)
        {
            var (exit, text) = ProcessRunner.Run("systemctl", $"--user disable --now {UnitName}");
            EngineLog.Write($"[LauncherSystemdAutostart] disable --now -> exit={exit} {Trim(text)}");
            // Still enabled after being asked to go is the case that must not pass for success.
            if (exit != 0 && IsEnabled())
                failure = $"systemctl --user disable --now {UnitName} failed (exit {exit}): {Trim(text)}";

            try
            {
                File.Delete(UnitPath);
                EngineLog.Write($"[LauncherSystemdAutostart] UnregisterVerified: removed {UnitPath}");
            }
            catch (Exception ex)
            {
                failure ??= $"could not delete {UnitPath}: {ex.Message}";
            }

            var (reloadExit, _) = ProcessRunner.Run("systemctl", "--user daemon-reload");
            EngineLog.Write($"[LauncherSystemdAutostart] daemon-reload -> exit={reloadExit}");
        }

        return failure is null;
    }

    /// <summary>
    /// Ask systemd to restart the launcher, so a launcher build that has just been REPLACED on disk
    /// becomes the one that is running.
    ///
    /// ONE OPERATION OWNED BY THE SUPERVISOR, not a stop and a start performed here - the same reason
    /// <see cref="LauncherLaunchdAutostart.Kickstart"/> gives on macOS. A stop and a start from outside
    /// would race systemd's own <c>Restart=on-failure</c> and, on a good day, leave two launchers; on a
    /// bad one it restarts the OLD build into the gap before the swap has landed.
    ///
    /// WITHOUT THIS THE SWAP IS INVISIBLE. Unix hands a running process the inode it started from, so a
    /// replaced file sits on disk while the old process keeps running, neither noticing nor reporting
    /// the new version.
    /// </summary>
    [SupportedOSPlatform("linux")]
    public static bool Restart()
    {
        EngineLog.Write($"[LauncherSystemdAutostart] Restart: systemctl --user restart {UnitName}");
        var (exit, text) = ProcessRunner.Run("systemctl", $"--user restart {UnitName}");
        if (exit != 0)
        {
            EngineLog.Write($"[LauncherSystemdAutostart] Restart FAILED: exit={exit} {Trim(text)}");
            return false;
        }
        return true;
    }

    private static string Trim(string text) => text.Length > 300 ? text[..300] + "..." : text.Trim();
}
