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

    /// <summary>
    /// The configuration directory of this install - the parent of every per-tool config folder, and
    /// the same directory <c>CcStorage.Config()</c> resolves to for a process whose storage home is
    /// <see cref="LocalRoot"/>. Named here so the paths under it are composed from ONE definition
    /// rather than from a "config" string repeated at each site.
    /// </summary>
    public string ConfigDir => Path.Combine(LocalRoot, "config");

    /// <summary>Per-user install bookkeeping (installed-version manifest, pins) - NOT user data.</summary>
    public string SetupStateDir => Path.Combine(ConfigDir, "setup");

    /// <summary>The installed-version manifest: component id -> the version actually placed on disk.</summary>
    public string InstalledManifestPath => Path.Combine(SetupStateDir, "installed.json");

    /// <summary>The shared app config (%LOCALAPPDATA%\cc-director\config\config.json), incl. the autoUpdate section.</summary>
    public string ConfigPath => Path.Combine(ConfigDir, "config.json");

    /// <summary>
    /// Where the launcher serving this install registers what it is: its version, its process id and
    /// its interface state. Written by the running launcher (<c>LauncherDiscovery</c>) and read by
    /// anything that needs to know which launcher is on this machine.
    /// </summary>
    public string LauncherRegistrationPath => Path.Combine(ConfigDir, "launcher", "launcher.json");

    /// <summary>The Gateway tray app's binaries.</summary>
    public string GatewayDir => Path.Combine(LocalRoot, "gateway");

    /// <summary>
    /// The retired Blazor Cockpit's install directory (issue #979). Nothing installs here any more -
    /// the React Cockpit is served in-process by the Gateway - but the path is retained so the
    /// uninstaller and the install-time process-stop can clean up a directory left by a pre-cutover
    /// install.
    /// </summary>
    public string CockpitDir => Path.Combine(LocalRoot, "cockpit");

    /// <summary>
    /// The mobile app's static files (issue #809): wwwroot/mobile BESIDE the Gateway exe, exactly where
    /// <c>MobileApp.WebRoot</c> (<c>AppContext.BaseDirectory/wwwroot/mobile</c>) looks. The single-file
    /// Gateway exe carries no loose content, so the built React PWA (issue #806) ships as a side-car
    /// zip the setup engine unpacks here on clean install and self-update.
    /// </summary>
    public string GatewayMobileDir => Path.Combine(GatewayDir, "wwwroot", "mobile");

    /// <summary>
    /// The React desktop Cockpit's static files (epic #967 cutover, issue #979): wwwroot/c BESIDE the
    /// Gateway exe, exactly where <c>CockpitReactApp.WebRoot</c> (<c>AppContext.BaseDirectory/wwwroot/c</c>)
    /// looks. Same delivery as the mobile app (<see cref="GatewayMobileDir"/>): the single-file Gateway
    /// exe carries no loose content, so the built Cockpit ships as a side-car zip the setup engine
    /// unpacks here on clean install and self-update.
    /// </summary>
    public string GatewayCockpitDir => Path.Combine(GatewayDir, "wwwroot", "c");

    /// <summary>
    /// The bundled ffmpeg (issue #1186): ffmpeg.exe placed DIRECTLY beside the Gateway exe, exactly where
    /// <c>FfmpegAudioTranscoder.ResolveFfmpegPath</c> (<c>AppContext.BaseDirectory/ffmpeg.exe</c>) looks
    /// for it. The single-file Gateway exe carries no loose content, so the pinned static ffmpeg ships as
    /// a side-car zip the setup engine unpacks here on clean install and self-update (the same delivery as
    /// the mobile app / Cockpit, but at the Gateway dir root - see <see cref="FfmpegPackage"/>).
    /// </summary>
    public string GatewayFfmpegPath => Path.Combine(GatewayDir, FfmpegPackage.ExeFile);

    /// <summary>The CC Launcher tray app's binaries (issue #250).</summary>
    public string LauncherDir => Path.Combine(LocalRoot, "launcher");

    /// <summary>Setup/update scratch state (e.g. the staged Gateway exe during a self-update).</summary>
    public string StateDir => Path.Combine(LocalRoot, "state");

    /// <summary>
    /// The retained copy of the setup executable. Windows "Apps &amp; features" needs an
    /// UninstallString pointing at something that still exists months later, and the executable the
    /// user downloaded is usually long gone from their Downloads folder.
    /// </summary>
    public string SetupDir => Path.Combine(LocalRoot, "setup");

    /// <summary>Log root (FileLog writes per-component subdirs underneath).</summary>
    public string LogsDir => Path.Combine(LocalRoot, "logs");

    /// <summary>The on-disk file whose presence/version represents the component.</summary>
    public string PathFor(Component component)
    {
        ArgumentNullException.ThrowIfNull(component);

        // macOS is Workstation-only: the Director is a .app in ~/Applications (matching the manual
        // install + UpdateInstaller.SwapMac); tools carry no .exe extension. Gateway/Launcher are
        // Windows-only roles and are never placed on mac.
        if (!OperatingSystem.IsWindows())
        {
            return component.Kind switch
            {
                ComponentKind.Director => Path.Combine(MacAppsDir, "Director.app"),
                ComponentKind.Tool => Path.Combine(BinDir, component.Id),
                ComponentKind.Gateway => Path.Combine(GatewayDir, "devthrottle-gateway"),
                ComponentKind.Launcher => Path.Combine(LauncherDir, "cc-launcher"),
                _ => throw new ArgumentOutOfRangeException(nameof(component), component.Kind, "Unknown component kind."),
            };
        }

        return component.Kind switch
        {
            ComponentKind.Director => Path.Combine(AppDir, "cc-director.exe"),
            ComponentKind.Gateway => Path.Combine(GatewayDir, "devthrottle-gateway.exe"),
            ComponentKind.Tool => Path.Combine(BinDir, $"{component.Id}.exe"),
            ComponentKind.Launcher => Path.Combine(LauncherDir, "cc-launcher.exe"),
            _ => throw new ArgumentOutOfRangeException(nameof(component), component.Kind, "Unknown component kind."),
        };
    }

    /// <summary>
    /// Pre-rename on-disk names a component may still occupy on an existing host, besides its current
    /// canonical <see cref="PathFor"/> location. Presence detection accepts these so an update
    /// recognises a legacy host and refreshes it instead of silently orphaning it (issue #1821). Two
    /// components have been renamed: the Gateway (cc-director-gateway.exe -> devthrottle-gateway.exe),
    /// and the macOS Director bundle ("CC Director.app" -> "Director.app"). The CURRENT name stays
    /// canonical/target - these are read-only aliases for detection, never a place we write to.
    /// </summary>
    public IReadOnlyList<string> LegacyAliasesFor(Component component)
    {
        ArgumentNullException.ThrowIfNull(component);

        if (component.Kind == ComponentKind.Gateway)
        {
            var legacyName = OperatingSystem.IsWindows() ? "cc-director-gateway.exe" : "cc-director-gateway";
            return new[] { Path.Combine(GatewayDir, legacyName) };
        }

        // The macOS Director bundle was renamed "CC Director.app" -> "Director.app". A host installed
        // before the rename still carries the old bundle in ~/Applications; accept it for detection so
        // an update refreshes that host instead of orphaning it. Windows/tool names never changed.
        if (component.Kind == ComponentKind.Director && !OperatingSystem.IsWindows())
            return new[] { Path.Combine(MacAppsDir, "CC Director.app") };

        return Array.Empty<string>();
    }
}
