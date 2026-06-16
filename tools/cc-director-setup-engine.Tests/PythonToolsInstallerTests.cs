using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Live integration test for the shared-venv Python tools install. It is OPT-IN: set
/// CC_PYBUNDLE_DIR to the directory holding cc-python-win-x64.zip + cc-tools-pyenv-win-x64.zip
/// (the output of scripts/build-python-bundle.ps1). Without it the test no-ops, so normal CI
/// stays fast and offline. When enabled it stages a local release, runs the real installer
/// (extract python -> venv -> offline pip), and asserts the venv, shims, and a runnable tool.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PythonToolsInstallerTests : IDisposable
{
    private readonly string _root;

    public PythonToolsInstallerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cc-pytools-it-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task InstallAsync_FromLocalBundle_CreatesVenvShimsAndRunnableTool()
    {
        var bundleDir = Environment.GetEnvironmentVariable("CC_PYBUNDLE_DIR");
        if (string.IsNullOrWhiteSpace(bundleDir))
            return; // opt-in; see class summary.

        // Stage a local release: the bundle assets + a release-manifest.json with real SHA-256s.
        var releaseDir = Path.Combine(_root, "release");
        Directory.CreateDirectory(releaseDir);
        var assets = new List<string> { PythonToolsInstaller.PythonAsset, PythonToolsInstaller.ToolsAsset };
        var assetJson = new StringBuilder();
        foreach (var a in assets)
        {
            var src = Path.Combine(bundleDir, a);
            Assert.True(File.Exists(src), $"missing bundle asset {src}");
            var dest = Path.Combine(releaseDir, a);
            File.Copy(src, dest, overwrite: true);
            var sha = Hashing.Sha256OfFile(dest);
            if (assetJson.Length > 0) assetJson.Append(',');
            assetJson.Append($"\"{a}\":{{\"version\":\"9.9.9\",\"sha256\":\"{sha}\",\"platform\":\"windows\",\"size\":{new FileInfo(dest).Length}}}");
        }
        File.WriteAllText(Path.Combine(releaseDir, "release-manifest.json"),
            $"{{\"version\":\"9.9.9\",\"assets\":{{{assetJson}}}}}");

        var layout = new InstallLayout(Path.Combine(_root, "local"));
        var release = ReleaseSource.LoadLocalReleaseDir(releaseDir);
        var installer = new PythonToolsInstaller(layout);

        var result = await installer.InstallAsync(release, new ReleaseSource());

        Assert.True(result.Success, $"install failed: {result.Message}\n{string.Join("\n", result.Steps)}");
        Assert.True(result.ToolCount > 0);

        // The bundled python, the venv, and at least the cc-pdf console script + its shim must exist.
        Assert.True(File.Exists(Path.Combine(layout.PythonDir, "python.exe")), "bundled python missing");
        Assert.True(File.Exists(Path.Combine(layout.PyenvScriptsDir, "cc-pdf.exe")), "venv console script missing");
        var shim = Path.Combine(layout.BinDir, "cc-pdf.cmd");
        Assert.True(File.Exists(shim), "bin shim missing");

        // The bundle carries its OWN version (from tools-manifest.json), not the release tag;
        // it is recorded for the updater.
        Assert.False(string.IsNullOrWhiteSpace(result.BundleVersion));
        Assert.Equal(result.BundleVersion, InstalledManifest.Load(layout).Get(PythonToolsInstaller.ComponentId));

        // The bundle ships ONLY core tools (ship: true in registry.json). A non-core tool like
        // cc-crawl4ai must never leak into it - that is the whole point of the allowlist.
        Assert.False(File.Exists(Path.Combine(layout.PyenvScriptsDir, "cc-crawl4ai.exe")),
            "non-core tool cc-crawl4ai leaked into the shipped bundle");

        // The shim actually runs the tool.
        var (exit, _) = ProcessRunnerTestProbe.Run(shim, "--help");
        Assert.Equal(0, exit);

        // The doc tools the user actually relies on (cc-html/cc-pdf/cc-word) must all be present and
        // runnable from their bin shim - cc-html is the one whose missing exe started this whole fix.
        foreach (var tool in new[] { "cc-html", "cc-word" })
        {
            Assert.True(File.Exists(Path.Combine(layout.PyenvScriptsDir, $"{tool}.exe")), $"{tool} console script missing");
            var toolShim = Path.Combine(layout.BinDir, $"{tool}.cmd");
            Assert.True(File.Exists(toolShim), $"{tool} shim missing");
            var (te, _) = ProcessRunnerTestProbe.Run(toolShim, "--help");
            Assert.Equal(0, te);
        }

        // --- REGRESSION (#453): a half-installed venv must REBUILD on re-install, not early-out. ---
        // Simulate the field failure: the version stamp + python.exe survive, but a tool console script
        // is gone (stripped/empty site-packages). The old early-out trusted (version match + python.exe)
        // and skipped, leaving the tool broken. The hardened early-out verifies the scripts are on disk,
        // so re-running the installer repairs it.
        var strippedScript = Path.Combine(layout.PyenvScriptsDir, "cc-pdf.exe");
        File.Delete(strippedScript);
        Assert.False(File.Exists(strippedScript), "precondition: tool script removed");

        var repair = await installer.InstallAsync(release, new ReleaseSource());
        Assert.True(repair.Success, $"repair re-install failed: {repair.Message}\n{string.Join("\n", repair.Steps)}");
        Assert.DoesNotContain("already installed", repair.Message); // must NOT take the early-out
        Assert.True(File.Exists(strippedScript), "stripped tool script was not repaired by re-install");
    }
}

/// <summary>Tiny cmd runner for the test (the engine's ProcessRunner is internal-only to commands).</summary>
internal static class ProcessRunnerTestProbe
{
    public static (int exit, string output) Run(string cmdFile, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{cmdFile}\" {args}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = System.Diagnostics.Process.Start(psi);
        if (p is null) throw new InvalidOperationException("cmd.exe did not start");
        var outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        return (p.ExitCode, outp);
    }
}
