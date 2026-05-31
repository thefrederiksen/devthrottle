using CcDirector.Core.Tools;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Covers the read-only catalog: embedded manifest load, binary resolution against the bin
/// directory, NOT BUILT detection, and the unmanaged-binary honesty report. No processes launched.
/// </summary>
public class ToolCatalogServiceTests : IDisposable
{
    private readonly string _binDir;

    public ToolCatalogServiceTests()
    {
        _binDir = Path.Combine(Path.GetTempPath(), "ToolCatalogTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_binDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_binDir)) Directory.Delete(_binDir, recursive: true); }
        catch { /* temp dir cleanup is best-effort */ }
    }

    // Mirrors ToolCatalogService.ResolveBinaryFileName so IsBuilt resolution matches on each OS.
    private static string BinName(string tool) => OperatingSystem.IsWindows() ? tool + ".exe" : tool;

    private void StubBuilt(string tool) => File.WriteAllText(Path.Combine(_binDir, BinName(tool)), "stub");

    // GetUnmanagedBinaries only ever scans *.exe, so the unmanaged tests write .exe explicitly.
    private void StubExe(string baseName) => File.WriteAllText(Path.Combine(_binDir, baseName + ".exe"), "stub");

    [Fact]
    public void GetCatalog_LoadsEmbeddedManifest_ReturnsKnownTool()
    {
        var svc = new ToolCatalogService(_binDir);

        var catalog = svc.GetCatalog();

        Assert.NotEmpty(catalog);
        Assert.Contains(catalog, d => d.Name == "cc-vault");
    }

    [Fact]
    public void GetCatalog_BinaryPresent_MarksBuilt()
    {
        StubBuilt("cc-vault");
        var svc = new ToolCatalogService(_binDir);

        var vault = svc.GetCatalog().Single(d => d.Name == "cc-vault");

        Assert.True(vault.IsBuilt);
        Assert.Equal(Path.Combine(_binDir, BinName("cc-vault")), vault.BinaryPath);
    }

    [Fact]
    public void GetCatalog_BinaryAbsent_MarksNotBuilt()
    {
        var svc = new ToolCatalogService(_binDir); // empty bin dir

        var vault = svc.GetCatalog().Single(d => d.Name == "cc-vault");

        Assert.False(vault.IsBuilt);
    }

    [Fact]
    public void GetCatalog_EveryTool_HasOnPathAndVersionChecks()
    {
        var svc = new ToolCatalogService(_binDir);

        foreach (var d in svc.GetCatalog())
        {
            Assert.Contains(d.Tests, t => t.Kind == ToolTestKind.OnPath);
            Assert.Contains(d.Tests, t => t.Kind == ToolTestKind.Version);
        }
    }

    [Fact]
    public void GetCatalog_OrderedByCategoryThenName()
    {
        var svc = new ToolCatalogService(_binDir);

        var catalog = svc.GetCatalog();

        var expected = catalog
            .OrderBy(d => d.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d => d.Name);
        Assert.Equal(expected, catalog.Select(d => d.Name));
    }

    [Fact]
    public void GetTool_KnownName_ReturnsDescriptor()
    {
        var svc = new ToolCatalogService(_binDir);

        var tool = svc.GetTool("cc-vault");

        Assert.Equal("cc-vault", tool.Name);
    }

    [Fact]
    public void GetTool_UnknownName_Throws()
    {
        var svc = new ToolCatalogService(_binDir);

        Assert.Throws<InvalidOperationException>(() => svc.GetTool("cc-does-not-exist"));
    }

    [Fact]
    public void GetUnmanagedBinaries_BuiltCcBinaryNotInManifest_Reported()
    {
        StubExe("cc-vault");         // in manifest -> managed
        StubExe("cc-mystery-tool");  // not in manifest -> unmanaged
        var svc = new ToolCatalogService(_binDir);

        var unmanaged = svc.GetUnmanagedBinaries();

        Assert.Contains("cc-mystery-tool", unmanaged);
        Assert.DoesNotContain("cc-vault", unmanaged);
    }

    [Fact]
    public void GetUnmanagedBinaries_BuildArtifactsAndNonTools_Excluded()
    {
        StubExe("cc-html-win-x64"); // RID-suffixed duplicate -> excluded
        StubExe("cc-director");     // the Director itself -> excluded
        StubExe("not-a-cc-tool");   // not a cc-* tool -> excluded
        var svc = new ToolCatalogService(_binDir);

        var unmanaged = svc.GetUnmanagedBinaries();

        Assert.DoesNotContain("cc-html-win-x64", unmanaged);
        Assert.DoesNotContain("cc-director", unmanaged);
        Assert.DoesNotContain("not-a-cc-tool", unmanaged);
    }
}
