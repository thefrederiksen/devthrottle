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
/// the macOS side about the gui domain. <c>Restart=on-failure</c> resurrects it after a crash but leaves
/// a CLEAN exit (the shutdown lifecycle signal a self-update raises) exited - the systemd analogue of
/// launchd's <c>KeepAlive SuccessfulExit=false</c>. <c>Restart=always</c> would fight every deliberate stop.
///
/// WHY A systemd USER UNIT AND NOT AN XDG AUTOSTART ENTRY (~/.config/autostart). Both start a program at
/// login. Only the unit SUPERVISES it: it brings a crashed launcher back, and it gives a launcher update
/// one operation that replaces the running build (<c>systemctl --user restart</c>, see <see cref="Restart"/>)
/// - the same shape macOS gets from launchd. An autostart entry starts the launcher once and forgets it,
/// so a launcher that crashed at 3am stays gone until the next login, and with it every Director update.
/// It is also the mechanism the Gateway already uses on Linux (<see cref="GatewaySystemdAutostart"/>).
///
/// WHY graphical-session.target AND NOT default.target, unlike the Gateway. The launcher's job is to
/// stop, replace and START THE DIRECTOR, which is a windowed application, and a child inherits the
/// launcher's environment. <c>default.target</c> is reached when the user manager starts - before the
/// desktop has published DISPLAY / WAYLAND_DISPLAY to it - so a launcher started there would start a
/// Director that has no display, which dies before its first window and is rolled back as a bad build.
/// <c>graphical-session.target</c> is reached once the desktop session is up and its environment is in
/// the manager. <c>PartOf=</c> ends the launcher with that session, as logging out ends a Run-key app.
/// The Gateway is different: it is headless and should run on a server with no desktop at all.
///
/// <c>KillMode=process</c> IS LOAD-BEARING. The launcher starts the Director as its own child, so the
/// Director lives in the launcher unit's control group. systemd's default (<c>control-group</c>) kills
/// EVERY process in that group when the unit stops or restarts - so every launcher self-update, crash
/// restart or logout of the unit would also kill the Director and every session running in it.
/// <c>process</c> stops only the launcher itself.
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

    /// <summary>The target the unit starts with and belongs to: the user's desktop session. See the class
    /// remarks for why this is not <c>default.target</c>.</summary>
    public const string SessionTarget = "graphical-session.target";

    /// <summary>The argument format every writer of the unit uses: the installer, the uninstaller's
    /// check and the launcher's own registration all write <c>--managed</c> and nothing else.</summary>
    public const string Arguments = "--managed";

    /// <summary>Runs a short command and returns its exit code and combined output. Injectable so tests
    /// can fake systemctl on any host without a real systemd.</summary>
    public delegate (int Exit, string Output) CommandRunner(string executable, string arguments);

    private static (int Exit, string Output) RunReal(string exe, string args) => ProcessRunner.Run(exe, args);

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
            PartOf={SessionTarget}
            After={SessionTarget}

            [Service]
            Type=simple
            ExecStart={execStart}
            Restart=on-failure
            RestartSec=5
            KillMode=process

            [Install]
            WantedBy={SessionTarget}

            """;
    }

    /// <summary>
    /// Ensure the user unit is written and enabled (so it starts at the next login) for the given executable
    /// and arguments. Idempotent: returns true if a write or a systemd (re)enable was performed, false if
    /// everything was already correct.
    ///
    /// ENABLE, NOT ENABLE --now. This is called by the RUNNING launcher on every start, and by the installer.
    /// A launcher that was started by hand, or by an older install, and then asked systemd to start the unit
    /// "now" would start a second launcher beside itself. Starting is the installer's job, done with
    /// <see cref="Restart"/> once the new build is on disk.
    /// </summary>
    [SupportedOSPlatform("linux")]
    public static bool EnsureRegistered(string exePath, string? arguments = null)
        => EnsureRegistered(exePath, arguments, RunReal, UnitPath);

    /// <summary><see cref="EnsureRegistered(string, string?)"/> with the command runner and unit path given,
    /// so it can be driven on any host.</summary>
    public static bool EnsureRegistered(string exePath, string? arguments, CommandRunner run, string unitPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ArgumentNullException.ThrowIfNull(run);
        var desired = UnitContent(exePath, arguments);
        EngineLog.Write($"[LauncherSystemdAutostart] EnsureRegistered: exe={exePath}, args={arguments ?? "(none)"}");

        var unitUnchanged = File.Exists(unitPath)
            && string.Equals(File.ReadAllText(unitPath), desired, StringComparison.Ordinal);

        if (unitUnchanged && IsEnabled(run))
        {
            EngineLog.Write("[LauncherSystemdAutostart] EnsureRegistered: already up to date and enabled");
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(unitPath)!);
        File.WriteAllText(unitPath, desired);
        EngineLog.Write($"[LauncherSystemdAutostart] EnsureRegistered: wrote {unitPath}");

        // systemd caches units, so a changed definition must be reloaded before enable.
        var (reloadExit, reloadText) = run("systemctl", "--user daemon-reload");
        EngineLog.Write($"[LauncherSystemdAutostart] daemon-reload -> exit={reloadExit} {Trim(reloadText)}");

        var (exit, text) = run("systemctl", $"--user enable {UnitName}");
        if (exit != 0)
            throw new InvalidOperationException(
                $"systemctl --user enable {UnitName} failed (exit {exit}): {Trim(text)}");

        EngineLog.Write("[LauncherSystemdAutostart] EnsureRegistered: enabled user unit");
        return true;
    }

    /// <summary>The registered ExecStart command line, or null when the unit file does not exist.</summary>
    public static string? Registered() => Registered(UnitPath);

    /// <summary><see cref="Registered()"/> for a given unit path.</summary>
    public static string? Registered(string unitPath)
    {
        if (!File.Exists(unitPath)) return null;
        foreach (var line in File.ReadAllLines(unitPath))
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
    public static bool IsEnabled() => IsEnabled(RunReal);

    /// <summary><see cref="IsEnabled()"/> with the command runner given.</summary>
    public static bool IsEnabled(CommandRunner run) => run("systemctl", $"--user is-enabled {UnitName}").Exit == 0;

    /// <summary>Whether systemd currently reports the user unit as active (running).</summary>
    [SupportedOSPlatform("linux")]
    public static bool IsActive() => IsActive(RunReal);

    /// <summary><see cref="IsActive()"/> with the command runner given.</summary>
    public static bool IsActive(CommandRunner run) => run("systemctl", $"--user is-active {UnitName}").Exit == 0;

    /// <summary>
    /// The process id systemd is running the unit as, or 0 when it runs none (or cannot be asked). This is
    /// what lets the installer certify the launcher IT started rather than any launcher that answers - the
    /// question the macOS installer asks launchd with <c>launchctl print</c>.
    /// </summary>
    public static int MainPid(CommandRunner run)
    {
        var (exit, text) = run("systemctl", $"--user show --property=MainPID --value {UnitName}");
        return exit == 0 ? ParseMainPid(text) : 0;
    }

    /// <summary>Testable parse of <c>systemctl show --property=MainPID --value</c>: the number, or 0.</summary>
    public static int ParseMainPid(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return 0;
        var first = output.Trim().Split('\n')[0].Trim();
        if (first.StartsWith("MainPID=", StringComparison.Ordinal)) first = first["MainPID=".Length..];
        return int.TryParse(first, out var pid) && pid > 0 ? pid : 0;
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
    public static bool UnregisterVerified(out string? failure) => UnregisterVerified(RunReal, UnitPath, out failure);

    /// <summary><see cref="UnregisterVerified(out string?)"/> with the command runner and unit path given.</summary>
    public static bool UnregisterVerified(CommandRunner run, string unitPath, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(run);
        failure = null;
        EngineLog.Write("[LauncherSystemdAutostart] UnregisterVerified");

        if (File.Exists(unitPath))
        {
            var (exit, text) = run("systemctl", $"--user disable --now {UnitName}");
            EngineLog.Write($"[LauncherSystemdAutostart] disable --now -> exit={exit} {Trim(text)}");
            // Still enabled after being asked to go is the case that must not pass for success.
            if (exit != 0 && IsEnabled(run))
                failure = $"systemctl --user disable --now {UnitName} failed (exit {exit}): {Trim(text)}";

            try
            {
                File.Delete(unitPath);
                EngineLog.Write($"[LauncherSystemdAutostart] UnregisterVerified: removed {unitPath}");
            }
            catch (Exception ex)
            {
                failure ??= $"could not delete {unitPath}: {ex.Message}";
            }

            var (reloadExit, _) = run("systemctl", "--user daemon-reload");
            EngineLog.Write($"[LauncherSystemdAutostart] daemon-reload -> exit={reloadExit}");
        }

        return failure is null;
    }

    /// <summary>
    /// Ask systemd to restart the launcher, so a launcher build that has just been REPLACED on disk
    /// becomes the one that is running. Starts it when it is not running, which is how the installer
    /// brings it up for the first time.
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
    public static bool Restart() => Restart(RunReal);

    /// <summary><see cref="Restart()"/> with the command runner given.</summary>
    public static bool Restart(CommandRunner run)
    {
        EngineLog.Write($"[LauncherSystemdAutostart] Restart: systemctl --user restart {UnitName}");
        var (exit, text) = run("systemctl", $"--user restart {UnitName}");
        if (exit != 0)
        {
            EngineLog.Write($"[LauncherSystemdAutostart] Restart FAILED: exit={exit} {Trim(text)}");
            return false;
        }
        return true;
    }

    internal static string Trim(string text) => text.Length > 300 ? text[..300] + "..." : text.Trim();
}
