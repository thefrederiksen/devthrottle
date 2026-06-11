using System.Diagnostics;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Tools;

/// <summary>
/// One catalog tool's inventory facts (issue #330): name, category, presence, and the
/// best deterministically-knowable version. Pure data; built by <see cref="ToolInventory"/>.
/// </summary>
public sealed record ToolInventoryItem(string Name, string Category, string? Version, bool IsBuilt);

/// <summary>
/// Builds the tool inventory with versions (issue #330, plan 1B) from the catalog plus the
/// setup manifest. Version resolution is deliberately CHEAP and deterministic - no process
/// is ever launched (running every tool's <c>--version</c> would cost ~45s per cold
/// PyInstaller exe; that stays the explicit POST /tools/test verb):
///
///   1. installed.json per-tool version (what the installer actually put down) - authoritative.
///   2. The resolved binary's file-version resource (stamped on our .NET exes).
///   3. installed.json "python-tools" bundle version, for tools the installer laid down as
///      wheel-bundle console scripts (resolved to pyenv\Scripts\&lt;name&gt;.exe) - those
///      pip-generated launcher exes carry NO version resource, and the version the installer
///      recorded for the bundle IS the tools' version (one release ships one bundle).
///   4. null - the version is honestly unknown (e.g. a dev-built exe with no resource).
/// </summary>
public static class ToolInventory
{
    /// <summary>The installed.json component key the setup records for the wheel bundle.</summary>
    private const string PythonToolsComponent = "python-tools";

    /// <summary>Build the inventory from a catalog and the installed-component versions.</summary>
    public static List<ToolInventoryItem> Build(
        ToolCatalogService catalog,
        IReadOnlyDictionary<string, string> installedComponents)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(installedComponents);

        FileLog.Write($"[ToolInventory] Build: installedComponents={installedComponents.Count}");
        var items = new List<ToolInventoryItem>();
        foreach (var tool in catalog.GetCatalog())
        {
            var version = installedComponents.TryGetValue(tool.Name, out var installed)
                ? installed
                : ReadFileVersion(tool) ?? BundleVersion(tool, installedComponents);
            items.Add(new ToolInventoryItem(tool.Name, tool.Category, version, tool.IsBuilt));
        }
        FileLog.Write($"[ToolInventory] Build: {items.Count} tools, {items.Count(i => i.Version is not null)} with versions");
        return items;
    }

    /// <summary>The binary's own version resource, or null when absent/unreadable (an
    /// honestly-unknown version - never invented).</summary>
    private static string? ReadFileVersion(ToolDescriptor tool)
    {
        if (!tool.IsBuilt) return null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(tool.BinaryPath);
            var version = info.ProductVersion ?? info.FileVersion;
            return string.IsNullOrWhiteSpace(version) ? null : version;
        }
        catch (FileNotFoundException)
        {
            return null; // raced a concurrent uninstall; absence is a valid fact
        }
    }

    /// <summary>
    /// The installer's wheel-bundle version for a tool resolved to the python console-script
    /// layout (<c>pyenv\Scripts\&lt;name&gt;.exe</c>, exactly where the installer's
    /// <c>bin\&lt;name&gt;.cmd</c> shims forward to). Those pip-generated launcher exes have no
    /// version resource of their own; the honest version is what installed.json recorded for
    /// the <c>python-tools</c> component when the bundle was put down. Null for any other
    /// layout (a dev build stays honestly unknown).
    /// </summary>
    private static string? BundleVersion(ToolDescriptor tool, IReadOnlyDictionary<string, string> installedComponents)
    {
        if (!tool.IsBuilt) return null;
        var dir = Path.GetDirectoryName(tool.BinaryPath);
        if (dir is null) return null;
        var isPyenvScript = dir.EndsWith(Path.Combine("pyenv", "Scripts"), StringComparison.OrdinalIgnoreCase);
        return isPyenvScript && installedComponents.TryGetValue(PythonToolsComponent, out var bundle)
            ? bundle
            : null;
    }
}
