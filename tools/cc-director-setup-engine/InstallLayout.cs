namespace CcDirector.Setup.Engine;

/// <summary>
/// Resolves where each component lives on disk. The canonical layout (the master
/// spec is docs/install/INSTALLATION.md) has three trust-tiered roots:
///   - LocalRoot       %LOCALAPPDATA%\cc-director   per-user, no admin (Director + tools)
///   - ProgramFilesRoot %ProgramFiles%\CC Director   machine-wide service binaries (admin once)
///   - ProgramDataRoot  %ProgramData%\cc-director     machine-wide service data (all users)
/// Roots are injectable so tests (and the CLI's testing overrides) can point at
/// temp directories without admin.
/// </summary>
public sealed class InstallLayout
{
    /// <summary>%LOCALAPPDATA%\cc-director (or the CC_DIRECTOR_ROOT override) - per-user, no admin.</summary>
    public string LocalRoot { get; }

    /// <summary>%ProgramFiles%\CC Director - machine-wide service binaries (Gateway + Cockpit).</summary>
    public string ProgramFilesRoot { get; }

    /// <summary>%ProgramData%\cc-director - machine-wide service data (config/state/logs).</summary>
    public string ProgramDataRoot { get; }

    public InstallLayout(string localRoot, string programFilesRoot, string programDataRoot)
    {
        if (string.IsNullOrWhiteSpace(localRoot))
            throw new ArgumentException("localRoot must not be empty.", nameof(localRoot));
        if (string.IsNullOrWhiteSpace(programFilesRoot))
            throw new ArgumentException("programFilesRoot must not be empty.", nameof(programFilesRoot));
        if (string.IsNullOrWhiteSpace(programDataRoot))
            throw new ArgumentException("programDataRoot must not be empty.", nameof(programDataRoot));
        LocalRoot = localRoot;
        ProgramFilesRoot = programFilesRoot;
        ProgramDataRoot = programDataRoot;
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

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        return new InstallLayout(
            localRoot,
            Path.Combine(programFiles, "CC Director"),
            Path.Combine(programData, "cc-director"));
    }

    // ---- per-user (LocalRoot) ----------------------------------------------
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

    /// <summary>The shared app config (%LOCALAPPDATA%\cc-director\config\config.json), incl. the autoUpdate section.</summary>
    public string ConfigPath => Path.Combine(LocalRoot, "config", "config.json");

    // ---- machine-wide service binaries (ProgramFilesRoot) ------------------
    public string GatewayDir => Path.Combine(ProgramFilesRoot, "gateway");
    public string CockpitDir => Path.Combine(ProgramFilesRoot, "cockpit");

    // ---- machine-wide service data (ProgramDataRoot) -----------------------
    public string ServiceConfigDir => Path.Combine(ProgramDataRoot, "config");
    public string ServiceStateDir => Path.Combine(ProgramDataRoot, "state");
    public string ServiceLogsDir => Path.Combine(ProgramDataRoot, "logs");

    /// <summary>The on-disk file whose presence/version represents the component.</summary>
    public string PathFor(Component component)
    {
        ArgumentNullException.ThrowIfNull(component);

        // macOS is Workstation-only: the Director is a .app in ~/Applications (matching the manual
        // install + UpdateInstaller.SwapMac); tools carry no .exe extension. Gateway/Cockpit are
        // Windows-only roles and are never placed on mac.
        if (!OperatingSystem.IsWindows())
        {
            return component.Kind switch
            {
                ComponentKind.Director => Path.Combine(MacAppsDir, "CC Director.app"),
                ComponentKind.Tool => Path.Combine(BinDir, component.Id),
                ComponentKind.Gateway => Path.Combine(GatewayDir, "cc-director-gateway"),
                ComponentKind.Cockpit => Path.Combine(CockpitDir, "cc-director-cockpit"),
                _ => throw new ArgumentOutOfRangeException(nameof(component), component.Kind, "Unknown component kind."),
            };
        }

        return component.Kind switch
        {
            ComponentKind.Director => Path.Combine(AppDir, "cc-director.exe"),
            ComponentKind.Gateway => Path.Combine(GatewayDir, "cc-director-gateway.exe"),
            ComponentKind.Cockpit => Path.Combine(CockpitDir, "cc-director-cockpit.exe"),
            ComponentKind.Tool => Path.Combine(BinDir, $"{component.Id}.exe"),
            _ => throw new ArgumentOutOfRangeException(nameof(component), component.Kind, "Unknown component kind."),
        };
    }
}
