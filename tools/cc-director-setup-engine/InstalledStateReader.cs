namespace CcDirector.Setup.Engine;

/// <summary>
/// Reads the installed state (present? which version?) of components from disk.
/// File existence and version reading are injectable so the logic is testable
/// without a real filesystem; the default wiring accepts files and directories
/// (the macOS .app bundle is a directory) and reads the Windows file-version
/// stamp or the macOS bundle Info.plist version.
/// </summary>
public sealed class InstalledStateReader
{
    private readonly InstallLayout _layout;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, bool> _aliasFileExists;
    private readonly Func<string, string?> _readVersion;
    private readonly InstalledManifest _installed;

    public InstalledStateReader(
        InstallLayout layout,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? aliasFileExists = null,
        Func<string, string?>? readVersion = null,
        InstalledManifest? installed = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _fileExists = fileExists ?? DefaultExists;
        _aliasFileExists = aliasFileExists ?? DefaultAliasExists;
        _readVersion = readVersion ?? DefaultReadVersion;
        _installed = installed ?? InstalledManifest.Load(layout);
    }

    /// <summary>
    /// Default presence check. A component can be a single file (every Windows exe, the macOS
    /// launcher) or a DIRECTORY (the macOS "CC Director.app" bundle), so presence must accept
    /// both; File.Exists alone reported an installed .app bundle as "not installed".
    /// </summary>
    internal static bool DefaultExists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>
    /// Default presence check for a legacy alias (issue #1821). A pre-rename alias is only ever a
    /// FILE - the renamed component is a Windows exe, never a bundle - so this probes File.Exists
    /// only. It deliberately does NOT accept a directory (unlike <see cref="DefaultExists"/>), or a
    /// Workstation that merely happened to have a DIRECTORY named gateway\cc-director-gateway.exe
    /// would falsely read as a Gateway host.
    /// </summary>
    internal static bool DefaultAliasExists(string path) => File.Exists(path);

    /// <summary>Inspect one component.</summary>
    public InstalledComponent Read(Component component)
    {
        ArgumentNullException.ThrowIfNull(component);
        var path = _layout.PathFor(component);

        // Presence accepts a pre-rename alias (issue #1821): an existing Gateway host whose exe is the
        // legacy cc-director-gateway.exe must still read as present, or the update misclassifies it as a
        // Workstation and orphans its Gateway. The canonical path stays the target we report; the
        // version is read from whichever file is actually on disk.
        var onDisk = _fileExists(path)
            ? path
            : _layout.LegacyAliasesFor(component).FirstOrDefault(_aliasFileExists);
        if (onDisk is null)
            return new InstalledComponent(component.Id, Present: false, Version: null, Path: path);

        // The on-disk file-version stamp is the ground truth for what the exe actually IS. Read it
        // separately so the planner can cross-check the recorded version against it (issue #176).
        var fileVersion = _readVersion(onDisk);

        // Prefer the version we recorded when we placed it (reliable for every component, incl. tools
        // that carry no file-version stamp); fall back to the on-disk file version for installs that
        // predate the manifest.
        var recorded = _installed.Get(component.Id);
        var version = recorded ?? fileVersion;

        // Self-update staleness (issue #1740): the Director updates itself in place without touching
        // installed.json, so on any machine that has ever self-updated the recorded version is older
        // than the binary actually on disk. A readable on-disk version STRICTLY newer than the record
        // can only mean the record is stale - trust the binary. (Equal versions that merely differ in
        // formatting, e.g. "1.4.0" versus "1.4.0+sha", compare equal and keep the recorded form.)
        if (recorded is not null && VersionUtil.IsNewer(fileVersion, recorded))
        {
            EngineLog.Write(
                $"[InstalledStateReader] recorded version '{recorded}' for '{component.Id}' is OLDER than " +
                $"the on-disk version '{fileVersion}' (the component self-updated without updating " +
                $"installed.json); reporting the on-disk version.");
            version = fileVersion;
        }

        return new InstalledComponent(component.Id, Present: true, Version: version, Path: path, FileVersion: fileVersion);
    }

    /// <summary>Inspect a set of components, keyed by component id.</summary>
    public IReadOnlyDictionary<string, InstalledComponent> ReadAll(IEnumerable<Component> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        var map = new Dictionary<string, InstalledComponent>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in components)
            map[c.Id] = Read(c);
        return map;
    }

    /// <summary>
    /// Default version reader: the Windows product-version stamp on the exe, or the
    /// CFBundleShortVersionString of a macOS .app bundle. A macOS single-file binary
    /// carries neither, so it reads as null ("present but version unknown", which the
    /// planner answers by re-applying the release so the version gets recorded).
    /// </summary>
    private static string? DefaultReadVersion(string path) => ReadVersionFromDisk(path);

    /// <summary>
    /// The version a build on disk declares about ITSELF, for any path - not only a component's
    /// installed location. Public because a build that has been downloaded but not yet installed has
    /// no manifest entry to be looked up in, and the file's own stamp is then the only thing that can
    /// say what it is (the staged launcher <see cref="LauncherUpdateOwner"/> is about to install).
    /// </summary>
    public static string? ReadVersionFromDisk(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                return info.ProductVersion;
            }
            catch (Exception ex)
            {
                EngineLog.Write($"[InstalledStateReader] version read FAILED for {path}: {ex.Message}");
                return null;
            }
        }

        // macOS .app bundle: the version lives in Contents/Info.plist. plutil is part of macOS.
        var plist = Path.Combine(path, "Contents", "Info.plist");
        if (!OperatingSystem.IsMacOS() || !File.Exists(plist))
            return null;
        var (exit, output) = ProcessRunner.Run(
            "/usr/bin/plutil", $"-extract CFBundleShortVersionString raw \"{plist}\"");
        if (exit != 0 || string.IsNullOrWhiteSpace(output))
        {
            EngineLog.Write($"[InstalledStateReader] bundle version read FAILED for {plist} (plutil exit {exit})");
            return null;
        }
        return output.Trim();
    }
}
