namespace CcDirector.Setup.Engine;

/// <summary>
/// Resolves where each component lives on disk. The canonical layout (the master
/// spec is docs/install/INSTALLATION.md) has ONE per-user root:
///   - LocalRoot  %LOCALAPPDATA%\cc-director   per-user, no admin
/// Every component - Director, tools, Gateway, Cockpit - installs under it, so the
/// whole lifecycle (install, self-update, uninstall) runs unelevated. The Gateway
/// stopped being a machine service (docs/plans/gateway-tray-app.md): it is a per-user
/// tray app now, so the old %ProgramFiles% / %ProgramData% roots are gone.
/// The root is injectable so tests can point at temp directories.
/// </summary>
public sealed class InstallLayout
{
    /// <summary>%LOCALAPPDATA%\cc-director (or the CC_DIRECTOR_ROOT override) - per-user, no admin.</summary>
    public string LocalRoot { get; }

    public InstallLayout(string localRoot)
    {
        if (string.IsNullOrWhiteSpace(localRoot))
            throw new ArgumentException("localRoot must not be empty.", nameof(localRoot));
        LocalRoot = localRoot;
    }

    /// <summary>The production layout, honoring CC_DIRECTOR_ROOT for the per-user root like CcStorage does.</summary>
    public static InstallLayout Default()
    {
        var localRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        if (string.IsNullOrWhiteSpace(localRoot))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            localRoot = Path.Combine(localAppData, "cc-director");
        }

        return new InstallLayout(localRoot);
    }

    public string AppDir => Path.Combine(LocalRoot, "app");
    public string BinDir => Path.Combine(LocalRoot, "bin");

    /// <summary>The bundled python-build-standalone CPython (from cc-python-win-x64.zip).</summary>
    public string PythonDir => Path.Combine(LocalRoot, "python");

    /// <summary>The shared venv every cc-* Python tool installs into (from the wheelhouse).</summary>
    public string PyenvDir => Path.Combine(LocalRoot, "pyenv");

    /// <summary>The shared venv's Scripts dir (Windows), where pip generates each tool's console-script exe.</summary>
    public string PyenvScriptsDir => Path.Combine(PyenvDir, "Scripts");

    /// <summary>The shared venv's executables dir: "Scripts" on Windows, "bin" on macOS/Unix.</summary>
    public string PyenvBinDir => Path.Combine(PyenvDir, OperatingSystem.IsWindows() ? "Scripts" : "bin");

    /// <summary>macOS user apps dir (~/Applications) - where the Director .app is placed (user-writable).</summary>
    public string MacAppsDir => Path.Combine(HomeDir, "Applications");

    /// <summary>macOS user bin (~/.local/bin) - where cc-* tool shim symlinks go (the .app launcher PATHs it).</summary>
    public string MacUserBinDir => Path.Combine(HomeDir, ".local", "bin");

    private static string HomeDir => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Per-user install bookkeeping (installed-version manifest, pins) - NOT user data.</summary>
    public string SetupStateDir => Path.Combine(LocalRoot, "config", "setup");

    /// <summary>The installed-version manifest: component id -> the version actually placed on disk.</summary>
    public string InstalledManifestPath => Path.Combine(SetupStateDir, "installed.json");

    /// <summary>The owned-skills manifest (issue #257): the skill names CC Director installed into
    /// %USERPROFILE%\.claude\skills, so uninstall removes only those and never the user's own.</summary>
    public string SkillManifestPath => Path.Combine(SetupStateDir, "skills.json");

    /// <summary>The shared app config (%LOCALAPPDATA%\cc-director\config\config.json), incl. the autoUpdate section.</summary>
    public string ConfigPath => Path.Combine(LocalRoot, "config", "config.json");

    /// <summary>The Gateway tray app's binaries.</summary>
    public string GatewayDir => Path.Combine(LocalRoot, "gateway");

    /// <summary>The Cockpit web app's binaries (unpacked from the Cockpit zip, supervised by the Gateway).</summary>
    public string CockpitDir => Path.Combine(LocalRoot, "cockpit");

    /// <summary>The CC Launcher tray app's binaries (issue #250).</summary>
    public string LauncherDir => Path.Combine(LocalRoot, "launcher");

    /// <summary>Setup/update scratch state (e.g. the staged Gateway exe during a self-update).</summary>
    public string StateDir => Path.Combine(LocalRoot, "state");

    /// <summary>Log root (FileLog writes per-component subdirs underneath).</summary>
    public string LogsDir => Path.Combine(LocalRoot, "logs");

    /// <summary>The on-disk file whose presence/version represents the component.</summary>
    public string PathFor(Component component)
    {
        ArgumentNullException.ThrowIfNull(component);

        // macOS is Workstation-only: the Director is a .app in ~/Applications (matching the manual
        // install + UpdateInstaller.SwapMac); tools carry no .exe extension. Gateway/Cockpit/Launcher
        // are Windows-only roles and are never placed on mac.
        if (!OperatingSystem.IsWindows())
        {
            return component.Kind switch
            {
                ComponentKind.Director => Path.Combine(MacAppsDir, "CC Director.app"),
                ComponentKind.Tool => Path.Combine(BinDir, component.Id),
                ComponentKind.Gateway => Path.Combine(GatewayDir, "devthrottle-gateway"),
                ComponentKind.Cockpit => Path.Combine(CockpitDir, "devthrottle-cockpit"),
                ComponentKind.Launcher => Path.Combine(LauncherDir, "cc-launcher"),
                _ => throw new ArgumentOutOfRangeException(nameof(component), component.Kind, "Unknown component kind."),
            };
        }

        return component.Kind switch
        {
            ComponentKind.Director => Path.Combine(AppDir, "cc-director.exe"),
            ComponentKind.Gateway => Path.Combine(GatewayDir, "devthrottle-gateway.exe"),
            ComponentKind.Cockpit => Path.Combine(CockpitDir, "devthrottle-cockpit.exe"),
            ComponentKind.Tool => Path.Combine(BinDir, $"{component.Id}.exe"),
            ComponentKind.Launcher => Path.Combine(LauncherDir, "cc-launcher.exe"),
            _ => throw new ArgumentOutOfRangeException(nameof(component), component.Kind, "Unknown component kind."),
        };
    }
}
