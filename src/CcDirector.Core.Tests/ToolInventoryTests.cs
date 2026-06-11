using CcDirector.Core.Tools;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// The tool inventory with versions (issue #330): every catalog tool appears, the
/// installer-recorded version (installed.json) wins, and a version that cannot be
/// known deterministically is null - never invented.
/// </summary>
public sealed class ToolInventoryTests : IDisposable
{
    private readonly string _binDir = Path.Combine(Path.GetTempPath(), "ToolInventoryTests_" + Guid.NewGuid().ToString("N"));

    public ToolInventoryTests()
    {
        Directory.CreateDirectory(_binDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_binDir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Build_EveryCatalogToolAppears()
    {
        var catalog = new ToolCatalogService(_binDir);

        var items = ToolInventory.Build(catalog, new Dictionary<string, string>());

        Assert.Equal(catalog.GetCatalog().Count, items.Count);
        Assert.All(items, i => Assert.False(string.IsNullOrEmpty(i.Name)));
    }

    [Fact]
    public void Build_InstalledComponentVersion_Wins()
    {
        var catalog = new ToolCatalogService(_binDir);
        var anyTool = catalog.GetCatalog()[0].Name;
        var installed = new Dictionary<string, string> { [anyTool] = "9.9.9-installed" };

        var items = ToolInventory.Build(catalog, installed);

        Assert.Equal("9.9.9-installed", items.Single(i => i.Name == anyTool).Version);
    }

    [Fact]
    public void Build_NotBuilt_NotInstalled_VersionIsHonestlyNull()
    {
        var catalog = new ToolCatalogService(_binDir); // empty bin dir: nothing is built

        var items = ToolInventory.Build(catalog, new Dictionary<string, string>());

        Assert.All(items, i =>
        {
            Assert.False(i.IsBuilt);
            Assert.Null(i.Version);
        });
    }

    [Fact]
    public void Build_BuiltExeWithoutVersionResource_VersionIsNull_IsBuiltTrue()
    {
        var catalog = new ToolCatalogService(_binDir);
        var anyTool = catalog.GetCatalog()[0].Name;
        // A dummy binary with no version resource - exactly a dev-built PyInstaller shape.
        // File name mirrors ToolCatalogService.ResolveBinaryFileName per OS (the
        // ToolCatalogServiceTests convention).
        var fileName = OperatingSystem.IsWindows() ? anyTool + ".exe" : anyTool;
        File.WriteAllBytes(Path.Combine(_binDir, fileName), new byte[] { 0x4D, 0x5A });

        var items = ToolInventory.Build(catalog, new Dictionary<string, string>());

        var item = items.Single(i => i.Name == anyTool);
        Assert.True(item.IsBuilt);
        Assert.Null(item.Version); // honestly unknown, never invented
    }

    [Fact]
    public void Build_PyenvConsoleScript_GetsPythonToolsBundleVersion()
    {
        // The real installed layout: bin\<name>.cmd shims forward to pyenv\Scripts\<name>.exe
        // (a pip console-script launcher with NO version resource); installed.json records the
        // bundle version under "python-tools". The inventory must surface that bundle version.
        var binDir = Path.Combine(_binDir, "bin");
        Directory.CreateDirectory(binDir);
        var catalog = new ToolCatalogService(binDir);
        var anyTool = catalog.GetCatalog()[0].Name;
        var scriptsDir = Path.Combine(_binDir, "pyenv", "Scripts");
        Directory.CreateDirectory(scriptsDir);
        File.WriteAllBytes(Path.Combine(scriptsDir, anyTool + ".exe"), new byte[] { 0x4D, 0x5A });
        var installed = new Dictionary<string, string> { ["python-tools"] = "0.6.22" };

        var items = ToolInventory.Build(new ToolCatalogService(binDir), installed);

        var item = items.Single(i => i.Name == anyTool);
        Assert.True(item.IsBuilt);
        Assert.Equal("0.6.22", item.Version);
    }

    [Fact]
    public void Build_PerToolEntry_BeatsBundleVersion()
    {
        var binDir = Path.Combine(_binDir, "bin");
        Directory.CreateDirectory(binDir);
        var catalog = new ToolCatalogService(binDir);
        var anyTool = catalog.GetCatalog()[0].Name;
        var scriptsDir = Path.Combine(_binDir, "pyenv", "Scripts");
        Directory.CreateDirectory(scriptsDir);
        File.WriteAllBytes(Path.Combine(scriptsDir, anyTool + ".exe"), new byte[] { 0x4D, 0x5A });
        var installed = new Dictionary<string, string>
        {
            ["python-tools"] = "0.6.22",
            [anyTool] = "1.2.3-per-tool",
        };

        var items = ToolInventory.Build(new ToolCatalogService(binDir), installed);

        Assert.Equal("1.2.3-per-tool", items.Single(i => i.Name == anyTool).Version);
    }
}
