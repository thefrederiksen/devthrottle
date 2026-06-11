namespace CcDirector.Setup.Engine;

/// <summary>
/// Reads the installed state (present? which version?) of components from disk.
/// File existence and version reading are injectable so the logic is testable
/// without a real filesystem; the default wiring uses <see cref="File.Exists"/>
/// and the Windows file-version stamp.
/// </summary>
public sealed class InstalledStateReader
{
    private readonly InstallLayout _layout;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, string?> _readVersion;
    private readonly InstalledManifest _installed;

    public InstalledStateReader(
        InstallLayout layout,
        Func<string, bool>? fileExists = null,
        Func<string, string?>? readVersion = null,
        InstalledManifest? installed = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _fileExists = fileExists ?? File.Exists;
        _readVersion = readVersion ?? DefaultReadVersion;
        _installed = installed ?? InstalledManifest.Load(layout);
    }

    /// <summary>Inspect one component.</summary>
    public InstalledComponent Read(Component component)
    {
        ArgumentNullException.ThrowIfNull(component);
        var path = _layout.PathFor(component);
        if (!_fileExists(path))
            return new InstalledComponent(component.Id, Present: false, Version: null, Path: path);

        // The on-disk file-version stamp is the ground truth for what the exe actually IS. Read it
        // separately so the planner can cross-check the recorded version against it (issue #176).
        var fileVersion = _readVersion(path);

        // Prefer the version we recorded when we placed it (reliable for every component, incl. tools
        // that carry no file-version stamp); fall back to the on-disk file version for installs that
        // predate the manifest.
        var version = _installed.Get(component.Id) ?? fileVersion;
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
    /// Default version reader: the Windows product-version stamp on the exe. Only
    /// meaningful on Windows; returns null elsewhere (the engine treats a null
    /// version as "present but version unknown", which the planner handles).
    /// </summary>
    private static string? DefaultReadVersion(string path)
    {
        if (!OperatingSystem.IsWindows())
            return null;
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
}
