using System.Runtime.Versioning;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Fast tests for ToolUpdater.RefreshPythonToolsAsync's decision gate - the cases that return
/// without ever extracting Python or running pip. The actual install is covered by the live,
/// opt-in PythonToolsInstallerTests.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BundleRefreshGateTests : IDisposable
{
    private readonly string _dir;
    private readonly InstallLayout _layout;

    public BundleRefreshGateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-bundlegate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _layout = new InstallLayout(Path.Combine(_dir, "local"), Path.Combine(_dir, "pf"), Path.Combine(_dir, "pd"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static ResolvedRelease ReleaseWithBundle(string version)
    {
        var json = """
        {"version":"VER","assets":{
          "cc-python-win-x64.zip":{"version":"VER","sha256":"a","platform":"windows","size":1},
          "cc-tools-pyenv-win-x64.zip":{"version":"VER","sha256":"b","platform":"windows","size":1}
        }}
        """.Replace("VER", version);
        return new ResolvedRelease(ReleaseManifest.Parse(json), new Dictionary<string, string>());
    }

    private static ResolvedRelease ReleaseWithoutBundle()
    {
        var json = """{"version":"1.0.0","assets":{"cc-director-win-x64.exe":{"sha256":"a","platform":"windows","size":1}}}""";
        return new ResolvedRelease(ReleaseManifest.Parse(json), new Dictionary<string, string>());
    }

    [Fact]
    public async Task RefreshPythonTools_NoBundleInRelease_ReturnsNull()
    {
        var result = await new ToolUpdater(_layout).RefreshPythonToolsAsync(ReleaseWithoutBundle(), new ReleaseSource());
        Assert.Null(result);
    }

    [Fact]
    public async Task RefreshPythonTools_AlreadyAtReleaseVersion_ReturnsNull()
    {
        // Record the bundle as installed at the same version the release ships -> nothing to do.
        var im = InstalledManifest.Load(_layout);
        im.Set(PythonToolsInstaller.ComponentId, "1.2.0");
        im.Save(_layout);

        var result = await new ToolUpdater(_layout).RefreshPythonToolsAsync(ReleaseWithBundle("1.2.0"), new ReleaseSource());
        Assert.Null(result);
    }

    [Fact]
    public async Task RefreshPythonTools_InstalledNewerThanRelease_ReturnsNull()
    {
        var im = InstalledManifest.Load(_layout);
        im.Set(PythonToolsInstaller.ComponentId, "2.0.0");
        im.Save(_layout);

        var result = await new ToolUpdater(_layout).RefreshPythonToolsAsync(ReleaseWithBundle("1.2.0"), new ReleaseSource());
        Assert.Null(result);
    }
}
