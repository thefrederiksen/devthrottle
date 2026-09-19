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

    // --- Orphaned legacy alias shim purge (issue #823) --------------------------------------------

    [Fact]
    public void LegacyAliasShimNames_AreTheRetiredFleetCommandsPlusUnshippedTools()
    {
        // The list is EXACTLY two groups and nothing else: the retired per-tool fleet commands
        // consolidated into cc-devthrottle (#823), which are in no registry and are named here by hand,
        // plus every tool the registry knows and the product does not ship.
        //
        // DERIVED RATHER THAN TRANSCRIBED. This test used to repeat the nine names the list held, which
        // meant it guarded the list against an edit and not against being WRONG: it was green the whole
        // time the list omitted cc-comm-queue, whose shim was sitting on the owner's machine. Copying
        // the answer into the test proves only that somebody copied it twice.
        var retiredFleetCommands = new[]
        {
            "cc-send", "cc-ask", "cc-spawn", "cc-sessions", "cc-whoami", "cc-settings", "cc-cron", "cc-fleet-selftest",
        };
        var shipped = ShippedToolNames();
        var expected = retiredFleetCommands
            .Concat(RegistryToolNames().Where(n => !shipped.Contains(n)))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, PythonToolsInstaller.LegacyAliasShimNames.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void LegacyAliasShimNames_NameNoShippedTool()
    {
        // The property that makes the purge safe: it deletes shims by name, so a SHIPPED tool's name in
        // this list would delete a live tool's shim on every install. That was previously asserted for
        // one name (cc-devthrottle) by hand, which says nothing about the other eight - and the list now
        // grows whenever a tool is unshipped, which is exactly when the mistake would be made. Checked
        // against the shipped manifest itself so the guard cannot go stale as the shipped set changes.
        var shipped = ShippedToolNames();
        Assert.NotEmpty(shipped);
        var overlap = PythonToolsInstaller.LegacyAliasShimNames.Where(shipped.Contains).ToArray();
        Assert.Empty(overlap);
    }

    /// <summary>
    /// The tool names in the shipped health manifest (src/CcDirector.Core/Tools/tools-manifest.json),
    /// read off disk by walking up from the test binary - the same way the Core guard test finds it.
    /// </summary>
    private static HashSet<string> ShippedToolNames()
    {
        var relative = Path.Combine("src", "CcDirector.Core", "Tools", "tools-manifest.json");
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? found = null;
        while (dir is not null && found is null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) found = candidate;
            dir = dir.Parent;
        }
        Assert.NotNull(found);

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(found!));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in doc.RootElement.GetProperty("tools").EnumerateArray())
            if (tool.TryGetProperty("name", out var n) && n.GetString() is { } name)
                names.Add(name);
        return names;
    }

    [Fact]
    public async Task InstallAsync_PurgesOrphanedLegacyAliasShims()
    {
        // A machine carrying orphaned legacy alias shims from an older install: each retired command has a
        // bin\<name>.cmd and a bare-name bash shim whose pyenv\Scripts\<name>.exe target no longer ships, so
        // running it fails with exit 127. InstallAsync must delete every one. The purge runs up front, so it
        // happens even though this install then fails at SHA-verify (a network-free way to drive InstallAsync).
        // WRITTEN WITH THE PRODUCT'S OWN SHIM BODIES, not a stub, because the purge now requires a file
        // to prove the product wrote it before deleting it. A stub body would leave every one of these
        // standing and this test would report a purge that never happened.
        Directory.CreateDirectory(_layout.BinDir);
        var orphans = new List<string>();
        foreach (var name in PythonToolsInstaller.LegacyAliasShimNames)
        {
            var cmd = Path.Combine(_layout.BinDir, $"{name}.cmd");
            var bare = Path.Combine(_layout.BinDir, name);
            File.WriteAllText(cmd, PythonToolsInstaller.BuildWindowsShimBody(name));
            File.WriteAllText(bare, PythonToolsInstaller.BuildWindowsBashShimBody(name));
            orphans.Add(cmd);
            orphans.Add(bare);
        }

        var result = await new PythonToolsInstaller(_layout).InstallAsync(StageBadShaRelease("1.0.0"), new ReleaseSource());

        Assert.False(result.Success); // bad-SHA download fails the install, but the purge already ran
        foreach (var path in orphans)
            Assert.False(File.Exists(path), $"orphaned legacy alias shim survived the install: {path}");
    }

    [Fact]
    public async Task InstallAsync_LeavesNonLegacyToolShimsAlone()
    {
        // The purge must remove ONLY the retired alias names. A live tool's shim (cc-pdf) sitting in bin\
        // must survive - purging it would break a working command. cc-send (a retired alias) is removed.
        Directory.CreateDirectory(_layout.BinDir);
        var liveShim = Path.Combine(_layout.BinDir, "cc-pdf.cmd");
        var legacyShim = Path.Combine(_layout.BinDir, "cc-send.cmd");
        File.WriteAllText(liveShim, PythonToolsInstaller.BuildWindowsShimBody("cc-pdf"));
        File.WriteAllText(legacyShim, PythonToolsInstaller.BuildWindowsShimBody("cc-send"));

        var result = await new PythonToolsInstaller(_layout).InstallAsync(StageBadShaRelease("1.0.0"), new ReleaseSource());

        Assert.False(result.Success);
        Assert.True(File.Exists(liveShim), "purge wrongly removed a live tool shim (cc-pdf.cmd)");
        Assert.False(File.Exists(legacyShim), "purge failed to remove a retired alias shim (cc-send.cmd)");
    }

    /// <summary>
    /// The list must COVER every tool the registry knows and the product does not ship.
    ///
    /// The other direction is already guarded (a shipped tool's name must never appear here). This is
    /// the direction that actually drifted: the list was nine hand-written names that happened to
    /// include cc-playwright and not cc-comm-queue, while both shims were on the owner's machine. A
    /// hand-written list of names that must equal a derived set is a list that goes stale the next time
    /// a tool is unshipped, so the derivation is the test.
    /// </summary>
    [Fact]
    public void LegacyAliasShimNames_CoverEveryKnownUnshippedTool()
    {
        var known = RegistryToolNames();
        var shipped = ShippedToolNames();
        Assert.NotEmpty(known);
        Assert.NotEmpty(shipped);

        var listed = new HashSet<string>(PythonToolsInstaller.LegacyAliasShimNames, StringComparer.OrdinalIgnoreCase);
        var missing = known.Where(n => !shipped.Contains(n) && !listed.Contains(n)).OrderBy(n => n).ToArray();

        Assert.True(missing.Length == 0,
            "tools/registry.json knows these, the product does not ship them, and LegacyAliasShimNames does "
            + "not name them, so their leftover launchers are never purged: " + string.Join(", ", missing));
    }

    /// <summary>
    /// A NAME ON THE LIST IS NOT PERMISSION ON ITS OWN. The launcher has to prove the product wrote it.
    ///
    /// This is the condition that makes it safe for the list to name cc-docgen, cc-excel, cc-video and
    /// the other tools the owner runs by hand: his own launcher for one of those is a file no install
    /// could ever put back, and name alone would delete it on every repair.
    /// </summary>
    [Fact]
    public void AHandWrittenLauncherForAListedNameIsKept_AndTheProductsOwnShimIsRemoved()
    {
        Directory.CreateDirectory(_layout.BinDir);

        // Measured on the owner's machine: this is what his cc-linkedin.cmd actually says. cc-docgen is
        // on the list (registry-known, unshipped) and he runs it by hand.
        var handWritten = Path.Combine(_layout.BinDir, "cc-docgen.cmd");
        File.WriteAllText(handWritten, "py -3.11 \"D:\\ReposFred\\cc-docgen\\cc_docgen.py\" %*\r\n");

        var ourShim = Path.Combine(_layout.BinDir, "cc-comm-queue.cmd");
        File.WriteAllText(ourShim, PythonToolsInstaller.BuildWindowsShimBody("cc-comm-queue"));

        var installer = new PythonToolsInstaller(_layout);
        installer.RemoveLegacyAliasShims();

        Assert.True(File.Exists(handWritten),
            "the purge removed a launcher the product did not write, and could never put back");
        Assert.False(File.Exists(ourShim),
            "the purge left a shim the product itself wrote for a tool it no longer ships");
    }

    [Fact]
    public void AShimBodyForADIFFERENTNameDoesNotCountAsOurs()
    {
        // The content test is keyed to the name of the file it is judging. A cc-docgen.cmd that forwards
        // to cc-pdf is somebody's own wrapper, not our shim for cc-docgen, and deleting it on the
        // strength of a pyenv path that names a different tool would be exactly the too-wide rule the
        // content test replaced.
        Directory.CreateDirectory(_layout.BinDir);
        var wrapper = Path.Combine(_layout.BinDir, "cc-docgen.cmd");
        File.WriteAllText(wrapper, PythonToolsInstaller.BuildWindowsShimBody("cc-pdf"));

        new PythonToolsInstaller(_layout).RemoveLegacyAliasShims();

        Assert.True(File.Exists(wrapper));
    }

    /// <summary>The tool names in tools/registry.json, found the same way the shipped manifest is.</summary>
    private static HashSet<string> RegistryToolNames()
    {
        var relative = Path.Combine("tools", "registry.json");
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? found = null;
        while (dir is not null && found is null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) found = candidate;
            dir = dir.Parent;
        }
        Assert.NotNull(found);

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(found!));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in doc.RootElement.GetProperty("tools").EnumerateArray())
            if (tool.TryGetProperty("name", out var n) && n.GetString() is { } name)
                names.Add(name);
        return names;
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

    [Fact]
    public void WindowsBashShimBody_HasShebang_ForwardsToVenvExe_LfEndings()
    {
        // The bare-name shim lets an agent driving Git Bash run a cc-* tool by bare name (the .cmd is
        // not resolved by Git Bash via PATHEXT). It must be a shebang shell script with LF endings.
        var body = PythonToolsInstaller.BuildWindowsBashShimBody("cc-devthrottle");

        Assert.StartsWith("#!/bin/sh\n", body);                  // shebang so msys runs it
        Assert.Contains("../pyenv/Scripts/cc-devthrottle.exe", body);     // forwards to the same venv exe (forward slashes for bash)
        Assert.Contains("\"$@\"", body);                          // passes arguments through
        Assert.DoesNotContain("\r", body);                        // LF only - a CR would break the shebang line under msys
    }
}
