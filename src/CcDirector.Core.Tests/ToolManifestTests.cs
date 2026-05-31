using CcDirector.Core.Storage;
using CcDirector.Core.Tools;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Guards the embedded manifest's integrity and the catalog's honesty guarantee: every built cc-*
/// binary is either declared in the manifest or surfaced as unmanaged - never silently dropped.
/// </summary>
public class ToolManifestTests
{
    [Fact]
    public void LoadEmbedded_ReturnsTools()
    {
        var manifest = ToolManifest.LoadEmbedded();

        Assert.NotEmpty(manifest.Tools);
    }

    [Fact]
    public void Manifest_EveryEntry_HasRequiredFields()
    {
        var manifest = ToolManifest.LoadEmbedded();

        foreach (var t in manifest.Tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Name), "a tool is missing its name");
            Assert.StartsWith("cc-", t.Name, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(t.Category), $"{t.Name} is missing its category");
            Assert.False(string.IsNullOrWhiteSpace(t.Description), $"{t.Name} is missing its description");
        }
    }

    [Fact]
    public void Manifest_ToolNames_AreUnique()
    {
        var manifest = ToolManifest.LoadEmbedded();

        var dupes = manifest.Tools
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(dupes.Count == 0, "duplicate tool names in manifest: " + string.Join(", ", dupes));
    }

    [Fact]
    public void Manifest_DeclaredSmoke_HasArgs()
    {
        var manifest = ToolManifest.LoadEmbedded();

        foreach (var t in manifest.Tools.Where(t => t.Smoke is not null))
            Assert.NotEmpty(t.Smoke!.Args);
    }

    [Fact]
    public void RealBin_EveryBuiltCcBinary_IsManagedOrUnmanaged()
    {
        var binDir = CcStorage.Bin();
        if (!Directory.Exists(binDir)) return; // nothing built in this environment - nothing to verify

        var svc = new ToolCatalogService();
        var managed = svc.GetCatalog()
            .Where(d => d.IsBuilt)
            .Select(d => d.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unmanaged = svc.GetUnmanagedBinaries().ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(binDir, "*.exe"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            // Same exclusions the catalog applies: RID-suffixed duplicates, the Director, non-cc tools.
            if (name.EndsWith("-win-x64", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.StartsWith("cc-director", StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.StartsWith("cc-", StringComparison.OrdinalIgnoreCase)) continue;

            Assert.True(managed.Contains(name) || unmanaged.Contains(name),
                $"{name} is built but is neither in the manifest nor reported as unmanaged");
        }
    }
}
