using System.Runtime.Versioning;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Fast tests for PythonToolsInstaller.InstallExtrasAsync's pre-flight gates (issue #174) - the
/// cases that fail loudly before any download. The actual extras install is covered by the live,
/// opt-in PythonToolsInstallerTests.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PythonExtrasGateTests : IDisposable
{
    private readonly string _dir;
    private readonly InstallLayout _layout;

    public PythonExtrasGateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-extrasgate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _layout = new InstallLayout(Path.Combine(_dir, "local"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static ResolvedRelease ReleaseWith(params string[] assets)
    {
        var entries = string.Join(',', assets.Select(a =>
            $"\"{a}\":{{\"version\":\"1.0.0\",\"sha256\":\"a\",\"platform\":\"windows\",\"size\":1}}"));
        return new ResolvedRelease(
            ReleaseManifest.Parse($"{{\"version\":\"1.0.0\",\"assets\":{{{entries}}}}}"),
            new Dictionary<string, string>());
    }

    [Fact]
    public async Task InstallExtras_ReleaseMissingExtrasAsset_FailsNamingTheAsset()
    {
        var release = ReleaseWith(PythonToolsInstaller.PythonAsset, PythonToolsInstaller.ToolsAsset);

        var result = await new PythonToolsInstaller(_layout).InstallExtrasAsync(release, new ReleaseSource());

        Assert.False(result.Success);
        Assert.Contains(PythonToolsInstaller.ExtrasAsset, result.Message);
    }

    [Fact]
    public async Task InstallExtras_NoVenvInstalled_FailsWithExactFix()
    {
        // The extras asset exists, but no shared venv does -> stop with the fix, never download.
        var release = ReleaseWith(PythonToolsInstaller.ExtrasAsset);

        var result = await new PythonToolsInstaller(_layout).InstallExtrasAsync(release, new ReleaseSource());

        Assert.False(result.Success);
        Assert.Contains("workstation install", result.Message);
    }
}
