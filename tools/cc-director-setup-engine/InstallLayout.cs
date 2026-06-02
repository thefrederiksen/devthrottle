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

    /// <summary>Per-user install bookkeeping (installed-version manifest, pins) - NOT user data.</summary>
    public string SetupStateDir => Path.Combine(LocalRoot, "config", "setup");

    /// <summary>The installed-version manifest: component id -> the version actually placed on disk.</summary>
    public string InstalledManifestPath => Path.Combine(SetupStateDir, "installed.json");

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
