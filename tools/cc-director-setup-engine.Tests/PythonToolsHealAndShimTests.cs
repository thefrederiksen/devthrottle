using System.Diagnostics;
using System.Runtime.Versioning;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Fast, offline tests for the issue #577 self-healing pieces that do NOT require a real bundle install:
/// the venv health probe, the heal-on-unhealthy update gate, the version-record gating, the no-stale-shim
/// sequencing, and the self-checking Windows shim body.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PythonToolsHealAndShimTests : IDisposable
{
    private readonly string _dir;
    private readonly InstallLayout _layout;

    public PythonToolsHealAndShimTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-pyheal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _layout = new InstallLayout(Path.Combine(_dir, "local"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>Create the venv Scripts dir and place a fake console-script exe for each given script name.</summary>
    private void PlaceVenvScripts(params string[] scripts)
    {
        Directory.CreateDirectory(_layout.PyenvScriptsDir);
        foreach (var s in scripts)
            File.WriteAllText(PythonToolsInstaller.ConsoleScriptPath(_layout, s), "fake-exe");
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

    /// <summary>
    /// Stage a local release dir whose bundle assets EXIST (so DownloadAssetAsync succeeds) but whose
    /// declared SHA-256s do NOT match the file content - so InstallAsync fails cleanly at the SHA-verify
    /// step and returns a Fail result (rather than throwing on a missing source). This lets us drive a
    /// realistic "the install failed" path without a real bundle.
    /// </summary>
    private ResolvedRelease StageBadShaRelease(string version)
    {
        var releaseDir = Path.Combine(_dir, "release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(releaseDir);
        foreach (var asset in new[] { PythonToolsInstaller.PythonAsset, PythonToolsInstaller.ToolsAsset })
            File.WriteAllText(Path.Combine(releaseDir, asset), "not-a-real-zip");
        var json = """
        {"version":"VER","assets":{
          "cc-python-win-x64.zip":{"version":"VER","sha256":"0000000000000000000000000000000000000000000000000000000000000000","platform":"windows","size":13},
          "cc-tools-pyenv-win-x64.zip":{"version":"VER","sha256":"1111111111111111111111111111111111111111111111111111111111111111","platform":"windows","size":13}
        }}
        """.Replace("VER", version);
        File.WriteAllText(Path.Combine(releaseDir, "release-manifest.json"), json);
        return ReleaseSource.LoadLocalReleaseDir(releaseDir);
    }

    /// <summary>
    /// Stage a local release whose assets are real, SHA-matching zips that extract successfully, but whose
    /// "python.exe" is a stub that cannot create a venv - so InstallAsync gets PAST download/extract and
    /// fails at the venv-create step (the exact point after the venv dir + shims have been reset). This is
    /// what exercises the no-stale-shim sequencing on a real rebuild failure (not a pre-venv download fail).
    /// The tools bundle carries a minimal tools-manifest.json + wheelhouse so the manifest parse succeeds.
    /// </summary>
    private ResolvedRelease StageVenvFailRelease(string version, params string[] scripts)
    {
        var releaseDir = Path.Combine(_dir, "release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(releaseDir);
        var work = Path.Combine(_dir, "work-" + Guid.NewGuid().ToString("N"));

        // Python asset: a "python.exe" that is a REAL, runnable PE (a copy of cmd.exe) but is NOT a Python
        // interpreter - so "python.exe -m venv ..." starts fine and exits non-zero, making InstallAsync fail
        // at the venv-create step with a clean Fail result (not a Process.Start exception).
        var pyStage = Path.Combine(work, "py");
        Directory.CreateDirectory(pyStage);
        var realCmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        File.Copy(realCmd, Path.Combine(pyStage, "python.exe"));
        var pyZip = Path.Combine(releaseDir, PythonToolsInstaller.PythonAsset);
        System.IO.Compression.ZipFile.CreateFromDirectory(pyStage, pyZip);

        // Tools asset: tools-manifest.json + a (possibly empty) wheelhouse so the bundle parses.
        var toolsStage = Path.Combine(work, "tools");
        Directory.CreateDirectory(Path.Combine(toolsStage, "wheelhouse"));
        var toolsArr = string.Join(",", scripts.Select(s => $"{{\"dist\":\"{s}\",\"scripts\":[\"{s}\"]}}"));
        File.WriteAllText(Path.Combine(toolsStage, "tools-manifest.json"),
            $"{{\"bundleVersion\":\"{version}\",\"tools\":[{toolsArr}]}}");
        var toolsZip = Path.Combine(releaseDir, PythonToolsInstaller.ToolsAsset);
        System.IO.Compression.ZipFile.CreateFromDirectory(toolsStage, toolsZip);

        var pySha = Hashing.Sha256OfFile(pyZip);
        var toolsSha = Hashing.Sha256OfFile(toolsZip);
        var pySize = new FileInfo(pyZip).Length;
        var toolsSize = new FileInfo(toolsZip).Length;
        var json =
            "{\"version\":\"" + version + "\",\"assets\":{" +
            "\"cc-python-win-x64.zip\":{\"version\":\"" + version + "\",\"sha256\":\"" + pySha + "\",\"platform\":\"windows\",\"size\":" + pySize + "}," +
            "\"cc-tools-pyenv-win-x64.zip\":{\"version\":\"" + version + "\",\"sha256\":\"" + toolsSha + "\",\"platform\":\"windows\",\"size\":" + toolsSize + "}" +
            "}}";
        File.WriteAllText(Path.Combine(releaseDir, "release-manifest.json"), json);
        return ReleaseSource.LoadLocalReleaseDir(releaseDir);
    }

    // --- VenvHasAllTools ---------------------------------------------------------------------------

    [Fact]
    public void VenvHasAllTools_AllScriptsPresent_ReturnsTrue()
    {
        PlaceVenvScripts("cc-pdf", "cc-html", "cc-word");
        Assert.True(PythonToolsInstaller.VenvHasAllTools(_layout, new[] { "cc-pdf", "cc-html", "cc-word" }));
    }

    [Fact]
    public void VenvHasAllTools_OneScriptMissing_ReturnsFalse()
    {
        PlaceVenvScripts("cc-pdf", "cc-word"); // cc-html missing
        Assert.False(PythonToolsInstaller.VenvHasAllTools(_layout, new[] { "cc-pdf", "cc-html", "cc-word" }));
    }

    [Fact]
    public void VenvHasAllTools_EmptyScriptList_ReturnsFalse()
    {
        // Nothing to verify is treated as not-healthy so a manifest with no scripts forces a rebuild.
        Assert.False(PythonToolsInstaller.VenvHasAllTools(_layout, Array.Empty<string>()));
    }

    // --- Heal-on-unhealthy auto-update gate --------------------------------------------------------

    [Fact]
    public async Task RefreshPythonTools_HealthyCurrentVenv_DoesNotReinstall()
    {
        // Recorded current, scripts sidecar present, every script on disk -> healthy -> null (no reinstall).
        var im = InstalledManifest.Load(_layout);
        im.Set(PythonToolsInstaller.ComponentId, "1.2.0");
        im.Save(_layout);
        PythonToolsState.SaveScripts(_layout, new[] { "cc-pdf", "cc-html" });
        PlaceVenvScripts("cc-pdf", "cc-html");

        var result = await new ToolUpdater(_layout).RefreshPythonToolsAsync(ReleaseWithBundle("1.2.0"), new ReleaseSource());

        Assert.Null(result); // current + healthy => nothing to do
    }

    [Fact]
    public async Task RefreshPythonTools_UnhealthyCurrentVenv_TriggersReinstall()
    {
        // Recorded current, but a console script is missing -> unhealthy -> must proceed past the version
        // gate into InstallAsync. We prove it proceeded by getting a non-null result (the install fails on
        // the fake-SHA download, which is fine - reaching InstallAsync at all is the gate behavior we test).
        var im = InstalledManifest.Load(_layout);
        im.Set(PythonToolsInstaller.ComponentId, "1.2.0");
        im.Save(_layout);
        PythonToolsState.SaveScripts(_layout, new[] { "cc-pdf", "cc-html" });
        PlaceVenvScripts("cc-pdf"); // cc-html missing -> unhealthy

        var result = await new ToolUpdater(_layout).RefreshPythonToolsAsync(StageBadShaRelease("1.2.0"), new ReleaseSource());

        Assert.NotNull(result); // proceeded into InstallAsync to self-heal (did not early-return null)
    }

    // --- Version-record gating ---------------------------------------------------------------------

    [Fact]
    public async Task InstallAsync_IncompleteVenv_DoesNotRecordVersion()
    {
        // A release whose bundle assets have fake SHA-256s makes InstallAsync fail at the download/verify
        // step - well before it could ever record a version. The bundle version must NOT appear in
        // installed.json after a failed/partial install (the im.Set gate).
        var release = StageBadShaRelease("9.9.9");
        var result = await new PythonToolsInstaller(_layout).InstallAsync(release, new ReleaseSource());

        Assert.False(result.Success);
        Assert.Null(InstalledManifest.Load(_layout).Get(PythonToolsInstaller.ComponentId));
    }

    [Fact]
    public async Task InstallAsync_FailedVenvRebuild_LeavesNoManagedShim()
    {
        // Simulate a machine that already had a shim from a prior install. The rebuild gets past extract and
        // fails at venv-create (stub python). The managed bin\cc-pdf.cmd shim must be GONE afterward (removed
        // up front, never rewritten because the venv never became healthy) - so no shim points at a missing
        // pyenv\Scripts\cc-pdf.exe target. Shim and target live and die together (issue #577).
        Directory.CreateDirectory(_layout.BinDir);
        var staleShim = Path.Combine(_layout.BinDir, "cc-pdf.cmd");
        File.WriteAllText(staleShim, "@echo off\r\n");

        var release = StageVenvFailRelease("9.9.9", "cc-pdf");
        var result = await new PythonToolsInstaller(_layout).InstallAsync(release, new ReleaseSource());

        Assert.False(result.Success); // venv create failed
        Assert.False(File.Exists(staleShim), "managed shim survived a failed venv rebuild (would point at a missing target)");
        // And the version was not recorded for a failed rebuild either.
        Assert.Null(InstalledManifest.Load(_layout).Get(PythonToolsInstaller.ComponentId));
    }

    // --- Self-checking Windows shim body -----------------------------------------------------------

    [Fact]
    public void WindowsShimBody_TargetMissing_ExitsNonZeroWithRepairGuidance()
    {
        // Lay the shim where it expects to be (bin\), with NO pyenv\Scripts\<name>.exe target present.
        var binDir = Path.Combine(_dir, "shimtest", "bin");
        Directory.CreateDirectory(binDir);
        var shim = Path.Combine(binDir, "cc-pdf.cmd");
        File.WriteAllText(shim, PythonToolsInstaller.BuildWindowsShimBody("cc-pdf"));

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{shim}\"\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        Assert.NotNull(p);
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);

        Assert.NotEqual(0, p.ExitCode); // non-zero, not cmd.exe's raw "is not recognized" (which is exit 9009 but with no guidance)
        Assert.Contains("not fully installed", output, StringComparison.Ordinal);
        Assert.Contains("Fix it", output, StringComparison.Ordinal); // points only at the live repair path (Home > Fix it)
    }
}
