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

    // ---------- Installed-layout resolution: bin\<name>.exe, else pyenv\Scripts\<name>.exe ----------

    /// <summary>
    /// Builds the installer's real layout (root\bin + root\pyenv\Scripts) in an isolated temp
    /// root, so the pyenv probe is exercised hermetically (issue #328).
    /// </summary>
    private static (string Root, string BinDir) NewInstallLayout()
    {
        var root = Path.Combine(Path.GetTempPath(), "ToolCatalogLayout_" + Guid.NewGuid().ToString("N"));
        var binDir = Path.Combine(root, "bin");
        Directory.CreateDirectory(binDir);
        Directory.CreateDirectory(Path.Combine(root, "pyenv", "Scripts"));
        return (root, binDir);
    }

    [Fact]
    public void GetCatalog_PyenvScriptExePresent_ResolvesShimTargetAndMarksBuilt()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (root, binDir) = NewInstallLayout();
        try
        {
            // The installer's layout: bin holds only the .cmd shim; the real exe is in pyenv\Scripts.
            File.WriteAllText(Path.Combine(binDir, "cc-vault.cmd"), "@echo off\r\n\"%~dp0..\\pyenv\\Scripts\\cc-vault.exe\" %*\r\n");
            var pyenvExe = Path.Combine(root, "pyenv", "Scripts", "cc-vault.exe");
            File.WriteAllText(pyenvExe, "stub");

            var vault = new ToolCatalogService(binDir).GetCatalog().Single(d => d.Name == "cc-vault");

            Assert.True(vault.IsBuilt);
            Assert.Equal(pyenvExe, vault.BinaryPath);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp dir cleanup is best-effort */ }
        }
    }

    [Fact]
    public void GetCatalog_NativeExeAndPyenvExeBothPresent_NativeWins()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (root, binDir) = NewInstallLayout();
        try
        {
            var native = Path.Combine(binDir, "cc-vault.exe");
            File.WriteAllText(native, "stub");
            File.WriteAllText(Path.Combine(root, "pyenv", "Scripts", "cc-vault.exe"), "stub");

            var vault = new ToolCatalogService(binDir).GetCatalog().Single(d => d.Name == "cc-vault");

            Assert.True(vault.IsBuilt);
            Assert.Equal(native, vault.BinaryPath);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp dir cleanup is best-effort */ }
        }
    }

    // ---------- IsExpected: installed-but-broken vs never-installed (over-nag fix) ----------

    [Fact]
    public void GetCatalog_ShimWithoutExe_ExpectedButNotBuilt()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (root, binDir) = NewInstallLayout();
        try
        {
            // The installer left the bin shim, but the venv exe is gone: the broken half-install.
            File.WriteAllText(Path.Combine(binDir, "cc-vault.cmd"), "@echo off\r\n");

            var vault = new ToolCatalogService(binDir).GetCatalog().Single(d => d.Name == "cc-vault");

            Assert.True(vault.IsExpected, "a shim means this install was expected to provide the tool");
            Assert.False(vault.IsBuilt, "the backing exe is missing, so it is broken");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void GetCatalog_NoShimNoExe_NotExpected()
    {
        // Never installed here (extras tier / other bundle / drift): the home must not nag about it.
        var vault = new ToolCatalogService(_binDir).GetCatalog().Single(d => d.Name == "cc-vault");

        Assert.False(vault.IsExpected);
        Assert.False(vault.IsBuilt);
    }

    [Fact]
    public void GetCatalog_BuiltTool_IsExpected()
    {
        StubBuilt("cc-vault");

        var vault = new ToolCatalogService(_binDir).GetCatalog().Single(d => d.Name == "cc-vault");

        Assert.True(vault.IsExpected);
        Assert.True(vault.IsBuilt);
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
