using System.Runtime.Versioning;

namespace CcDirector.Setup.Engine;

/// <summary>
/// The Gateway's per-user autostart on Linux: a <c>systemd --user</c> service unit at
/// ~/.config/systemd/user/devthrottle-gateway.service (issue #2022). The Linux twin of
/// <see cref="GatewayAutostart"/> (the Windows Run key) and <see cref="GatewayLaunchdAutostart"/>
/// (the macOS launch agent).
///
/// It lives in the engine for the same reason its two siblings do: the installer, the uninstaller, and the
/// <c>devthrottle-setup-cli autostart</c> command must agree on one unit name, one file path, and one
/// command-line format.
///
/// Per-user (<c>--user</c>), never a system service: the Gateway only does useful work while the user is
/// logged in - the whole fleet is logon-bound - so it belongs in the user's own systemd session, exactly as
/// the Windows side reasons about HKCU and the macOS side about the gui domain. <c>WantedBy=default.target</c>
/// starts it with the user session; <c>Restart=on-failure</c> resurrects it after a crash but leaves a CLEAN
/// exit (POST /shutdown) exited - the systemd analogue of launchd's <c>KeepAlive SuccessfulExit=false</c>.
///
/// A headless Linux server is the case this mechanism exists for: it has no tray and no window, so the CLI
/// (backed by this unit) is the ONLY home a host setting can have there - which is exactly why start-at-login
/// moved off the web Settings page and onto the CLI (issue #2022).
/// </summary>
public static class GatewaySystemdAutostart
{
    /// <summary>The systemd unit name (also the unit file name).</summary>
    public const string UnitName = "devthrottle-gateway.service";

    /// <summary>The per-user unit file path: ~/.config/systemd/user/devthrottle-gateway.service.</summary>
    public static string UnitPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "systemd", "user", UnitName);

    /// <summary>
    /// The full unit file for the given executable and arguments. Pure, for tests. The ExecStart command is
    /// double-quoted so a path containing spaces stays one token.
    /// </summary>
    public static string UnitContent(string exePath, string? arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        var execStart = string.IsNullOrWhiteSpace(arguments)
            ? $"\"{exePath}\""
            : $"\"{exePath}\" {arguments}";

        return $"""
            [Unit]
            Description=DevThrottle Gateway
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
    /// Ensure the user unit is written and enabled (started now and at login) for the given executable and
    /// arguments. Idempotent: returns true if a write or a systemd (re)enable was performed, false if
    /// everything was already correct.
    /// </summary>
    [SupportedOSPlatform("linux")]
    public static bool EnsureRegistered(string exePath, string? arguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        var desired = UnitContent(exePath, arguments);
        EngineLog.Write($"[GatewaySystemdAutostart] EnsureRegistered: exe={exePath}, args={arguments ?? "(none)"}");

        var unitUnchanged = File.Exists(UnitPath)
            && string.Equals(File.ReadAllText(UnitPath), desired, StringComparison.Ordinal);

        if (unitUnchanged && IsEnabled())
        {
            EngineLog.Write("[GatewaySystemdAutostart] EnsureRegistered: already up to date and enabled");
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(UnitPath)!);
        File.WriteAllText(UnitPath, desired);
        EngineLog.Write($"[GatewaySystemdAutostart] EnsureRegistered: wrote {UnitPath}");

        // systemd caches units, so a changed definition must be reloaded before enable.
        var (reloadExit, reloadText) = ProcessRunner.Run("systemctl", "--user daemon-reload");
        EngineLog.Write($"[GatewaySystemdAutostart] daemon-reload -> exit={reloadExit} {Trim(reloadText)}");

        var (exit, text) = ProcessRunner.Run("systemctl", $"--user enable --now {UnitName}");
        if (exit != 0)
            throw new InvalidOperationException(
                $"systemctl --user enable --now {UnitName} failed (exit {exit}): {Trim(text)}");

        EngineLog.Write("[GatewaySystemdAutostart] EnsureRegistered: enabled user unit");
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

    /// <summary>
    /// Disable and stop the user unit, then remove its unit file. Returns true if the unit file existed.
    /// </summary>
    [SupportedOSPlatform("linux")]
    public static bool Unregister()
    {
        EngineLog.Write("[GatewaySystemdAutostart] Unregister");
        var existed = File.Exists(UnitPath);

        if (existed)
        {
            var (exit, text) = ProcessRunner.Run("systemctl", $"--user disable --now {UnitName}");
            EngineLog.Write($"[GatewaySystemdAutostart] disable --now -> exit={exit} {Trim(text)}");
            File.Delete(UnitPath);
            EngineLog.Write($"[GatewaySystemdAutostart] Unregister: removed {UnitPath}");
            var (reloadExit, _) = ProcessRunner.Run("systemctl", "--user daemon-reload");
            EngineLog.Write($"[GatewaySystemdAutostart] daemon-reload -> exit={reloadExit}");
        }
        return existed;
    }

    /// <summary>Whether systemd currently reports the user unit as enabled.</summary>
    [SupportedOSPlatform("linux")]
    public static bool IsEnabled()
    {
        var (exit, _) = ProcessRunner.Run("systemctl", $"--user is-enabled {UnitName}");
        return exit == 0;
    }

    private static string Trim(string text) => text.Length > 300 ? text[..300] + "..." : text.Trim();
}
