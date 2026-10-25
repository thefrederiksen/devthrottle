using System.Diagnostics;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Fast, offline tests for the issue #577 self-healing pieces that do NOT require a real bundle install:
/// the venv health probe, the heal-on-unhealthy update gate, the version-record gating, the no-stale-shim
/// sequencing, and the self-checking Windows shim body.
///
/// On every system: the shims are the ones the product writes THERE (a <c>.cmd</c> and a bare bash launcher on
/// Windows, a symbolic link on macOS and Linux), written by the product itself, and the release is in that system's
/// shape (<see cref="PlatformBundle"/>).
/// </summary>
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
        Directory.CreateDirectory(_layout.PyenvBinDir);
        foreach (var s in scripts)
            File.WriteAllText(PythonToolsInstaller.ConsoleScriptPath(_layout, s), "fake-exe");
    }

    /// <summary>The folder the product writes this system's shims in.</summary>
    private string ShimDir => OperatingSystem.IsWindows() ? _layout.BinDir : _layout.ToolShimDir;

    /// <summary>The shims the product itself writes for <paramref name="name"/> on this system, written by the product.</summary>
    private List<string> ProductShimsFor(string name)
    {
        new PythonToolsInstaller(_layout).WriteShims(new[] { name });
        return OperatingSystem.IsWindows()
            ? new List<string> { Path.Combine(_layout.BinDir, $"{name}.cmd"), Path.Combine(_layout.BinDir, name) }
            : new List<string> { Path.Combine(_layout.ToolShimDir, name) };
    }

    /// <summary>Is anything at <paramref name="path"/> - a file, or a symbolic link even when its target is gone.</summary>
    private static bool Present(string path) => File.Exists(path) || new FileInfo(path).LinkTarget is not null;

    private static ResolvedRelease ReleaseWithBundle(string version)
    {
        var json = "{\"version\":\"VER\",\"assets\":{" +
                   PlatformBundle.AssetEntry(PythonToolsInstaller.PythonAsset, "VER", "a", 1) + "," +
                   PlatformBundle.AssetEntry(PythonToolsInstaller.ToolsAsset, "VER", "b", 1) + "}}";
        json = json.Replace("VER", version);
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
        var json = ("{\"version\":\"VER\",\"assets\":{" +
                    PlatformBundle.AssetEntry(PythonToolsInstaller.PythonAsset, "VER", new string('0', 64), 13) + "," +
                    PlatformBundle.AssetEntry(PythonToolsInstaller.ToolsAsset, "VER", new string('1', 64), 13) + "}}")
                   .Replace("VER", version);
        File.WriteAllText(Path.Combine(releaseDir, "release-manifest.json"), json);
        return ReleaseSource.LoadLocalReleaseDir(releaseDir);
    }

    /// <summary>
    /// Stage a local release whose assets are real, SHA-matching zips that extract successfully. Its
    /// "python.exe" is only a placeholder file: what running it answers is decided by the runner the test
    /// hands the installer (<see cref="StdlibProbePassesVenvFails"/>), never by starting a real program.
    /// The tools bundle carries a minimal tools-manifest.json + wheelhouse so the manifest parse succeeds.
    /// </summary>
    private ResolvedRelease StageVenvFailRelease(string version, params string[] scripts)
    {
        var releaseDir = Path.Combine(_dir, "release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(releaseDir);
        var work = Path.Combine(_dir, "work-" + Guid.NewGuid().ToString("N"));

        // Python asset: a placeholder "python.exe". The installer checks the file is there and then runs it
        // through the injected runner, so its contents never matter.
        var pyStage = Path.Combine(work, "py");
        var stagedInterpreter = Path.Combine(pyStage, PlatformBundle.InterpreterInAsset);
        Directory.CreateDirectory(Path.GetDirectoryName(stagedInterpreter)!);
        File.WriteAllText(stagedInterpreter, "placeholder interpreter");
        var pyZip = Path.Combine(releaseDir, PythonToolsInstaller.PythonAsset);
        PlatformBundle.Pack(pyStage, pyZip);

        // Tools asset: tools-manifest.json + a (possibly empty) wheelhouse so the bundle parses.
        var toolsStage = Path.Combine(work, "tools");
        Directory.CreateDirectory(Path.Combine(toolsStage, "wheelhouse"));
        var toolsArr = string.Join(",", scripts.Select(s => $"{{\"dist\":\"{s}\",\"scripts\":[\"{s}\"]}}"));
        File.WriteAllText(Path.Combine(toolsStage, "tools-manifest.json"),
            $"{{\"bundleVersion\":\"{version}\",\"tools\":[{toolsArr}]}}");
        var toolsZip = Path.Combine(releaseDir, PythonToolsInstaller.ToolsAsset);
        PlatformBundle.Pack(toolsStage, toolsZip);

        var json = "{\"version\":\"" + version + "\",\"assets\":{" +
                   PlatformBundle.AssetEntry(pyZip, version) + "," + PlatformBundle.AssetEntry(toolsZip, version) + "}}";
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
    public async Task InstallAsync_FailedVenvRebuild_LeavesTheInstalledToolAndItsShimAlone()
    {
        // A machine with a working tool. The rebuild gets past extract and fails at venv-create. The new venv is
        // built BESIDE the live one, so a failure there changes nothing installed: the console script and the
        // shim that forwards to it are both still there and still agree (issue #577 - a shim never points at a
        // missing target), and no version is recorded for a rebuild that did not happen.
        Directory.CreateDirectory(_layout.PyenvBinDir);
        var liveScript = PythonToolsInstaller.ConsoleScriptPath(_layout, "cc-pdf");
        File.WriteAllText(liveScript, "installed console script");
        new PythonToolsInstaller(_layout).WriteShims(new[] { "cc-pdf" });
        var shim = PlatformBundle.ShimPath(_layout, "cc-pdf");

        var release = StageVenvFailRelease("9.9.9", "cc-pdf");
        var runs = new List<string>();
        var installer = new PythonToolsInstaller(_layout, StdlibProbePassesVenvFails(runs));
        var result = await installer.InstallAsync(release, new ReleaseSource());

        Assert.False(result.Success);
        // It failed AT the venv create - not earlier, where an untouched machine would say nothing about it.
        Assert.Contains("venv creation failed", result.Message);
        Assert.Contains("not touched", result.Message);
        Assert.Contains(runs, r => r.StartsWith("-m venv", StringComparison.Ordinal));
        Assert.Equal("installed console script", File.ReadAllText(liveScript));
        Assert.True(File.Exists(shim), "a failed rebuild took the shim of a tool that still works");
        Assert.Null(InstalledManifest.Load(_layout).Get(PythonToolsInstaller.ComponentId));
    }

    /// <summary>
    /// A runner whose answers are fixed: the standard-library probe passes and the venv create fails. Every
    /// argument string it is asked to run is recorded in <paramref name="runs"/>; anything else it is asked
    /// to run fails the test, because an unexpected program would mean the install took a path this test
    /// does not describe.
    /// </summary>
    private static RunProcess StdlibProbePassesVenvFails(List<string> runs) => (exe, arguments, _, _) =>
    {
        if (PlatformBundle.TryUnpack(exe, arguments, out var unpacked)) return unpacked;
        runs.Add(arguments);
        if (arguments == PythonRuntimeProbe.ProbeArguments) return (0, "");
        if (arguments.StartsWith("-m venv", StringComparison.Ordinal)) return (1, "venv create refused by the test runner");
        throw new InvalidOperationException($"unexpected run of {exe} {arguments}");
    };

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
        var orphans = new List<string>();
        foreach (var name in PythonToolsInstaller.LegacyAliasShimNames)
            orphans.AddRange(ProductShimsFor(name));

        var result = await new PythonToolsInstaller(_layout).InstallAsync(StageBadShaRelease("1.0.0"), new ReleaseSource());

        Assert.False(result.Success); // bad-SHA download fails the install, but the purge already ran
        foreach (var path in orphans)
            Assert.False(Present(path), $"orphaned legacy alias shim survived the install: {path}");
    }

    [Fact]
    public async Task InstallAsync_LeavesNonLegacyToolShimsAlone()
    {
        // The purge must remove ONLY the retired alias names. A live tool's shim (cc-pdf) sitting in bin\
        // must survive - purging it would break a working command. cc-send (a retired alias) is removed.
        var liveShim = ProductShimsFor("cc-pdf")[0];
        var legacyShim = ProductShimsFor("cc-send")[0];

        var result = await new PythonToolsInstaller(_layout).InstallAsync(StageBadShaRelease("1.0.0"), new ReleaseSource());

        Assert.False(result.Success);
        Assert.True(Present(liveShim), $"purge wrongly removed a live tool shim ({liveShim})");
        Assert.False(Present(legacyShim), $"purge failed to remove a retired alias shim ({legacyShim})");
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
        Directory.CreateDirectory(ShimDir);

        // Measured on the owner's machine: this is what his cc-linkedin.cmd actually says. cc-docgen is
        // on the list (registry-known, unshipped) and he runs it by hand. Elsewhere, the same thing as a script.
        var handWritten = OperatingSystem.IsWindows() ? Path.Combine(_layout.BinDir, "cc-docgen.cmd") : Path.Combine(ShimDir, "cc-docgen");
        File.WriteAllText(handWritten, OperatingSystem.IsWindows()
            ? "py -3.11 \"D:\\ReposFred\\cc-docgen\\cc_docgen.py\" %*\r\n"
            : "#!/bin/sh\nexec python3 \"$HOME/repos/cc-docgen/cc_docgen.py\" \"$@\"\n");

        var ourShim = ProductShimsFor("cc-comm-queue")[0];

        var installer = new PythonToolsInstaller(_layout);
        installer.RemoveLegacyAliasShims();

        Assert.True(Present(handWritten),
            "the purge removed a launcher the product did not write, and could never put back");
        Assert.False(Present(ourShim),
            "the purge left a shim the product itself wrote for a tool it no longer ships");
    }

    [Fact]
    public void AShimBodyForADIFFERENTNameDoesNotCountAsOurs()
    {
        // The content test is keyed to the name of the file it is judging. A cc-docgen.cmd that forwards
        // to cc-pdf is somebody's own wrapper, not our shim for cc-docgen, and deleting it on the
        // strength of a pyenv path that names a different tool would be exactly the too-wide rule the
        // content test replaced.
        Directory.CreateDirectory(ShimDir);
        string wrapper;
        if (OperatingSystem.IsWindows())
        {
            wrapper = Path.Combine(_layout.BinDir, "cc-docgen.cmd");
            File.WriteAllText(wrapper, PythonToolsInstaller.BuildWindowsShimBody("cc-pdf"));
        }
        else
        {
            // The macOS and Linux form of the same thing: a link named cc-docgen that points at cc-pdf's console script.
            wrapper = Path.Combine(ShimDir, "cc-docgen");
            File.CreateSymbolicLink(wrapper, PythonToolsInstaller.ConsoleScriptPath(_layout, "cc-pdf"));
        }

        new PythonToolsInstaller(_layout).RemoveLegacyAliasShims();

        Assert.True(Present(wrapper));
    }

    /// <summary>
    /// THE RELATIVE COMPOSITION IS THE TEST, NOT THE VENV PATH.
    ///
    /// The most natural way for a person to write their own launcher for a tool the product no longer
    /// ships is to point straight at the executable. Such a file names exactly the same
    /// pyenv\Scripts\&lt;name&gt;.exe the product's own shim names, so a content test that looked only for
    /// that tail would delete it - and no install could ever put it back. What tells the two apart is the
    /// product's own RELATIVE composition: %~dp0..\ in the .cmd and $(dirname "$0")/../ in the bash shim.
    /// A hand-written absolute path cannot have either.
    /// </summary>
    [Fact]
    public void ALauncherNamingTheVenvExecutableByABSOLUTEPathIsKept()
    {
        Directory.CreateDirectory(ShimDir);

        // On macOS and Linux the product's own shim is a link, so a launcher that is a FILE naming the venv executable
        // by absolute path is somebody's own on its face; on Windows it is the .cmd and bash forms below.
        var windowsForm = OperatingSystem.IsWindows() ? Path.Combine(_layout.BinDir, "cc-docgen.cmd") : Path.Combine(ShimDir, "cc-docgen");
        File.WriteAllText(windowsForm, OperatingSystem.IsWindows()
            ? "@echo off\r\n\"C:\\Users\\soren\\AppData\\Local\\cc-director\\pyenv\\Scripts\\cc-docgen.exe\" %*\r\n"
            : $"#!/bin/sh\nexec \"{PythonToolsInstaller.ConsoleScriptPath(_layout, "cc-docgen")}\" \"$@\"\n");

        var bashForm = OperatingSystem.IsWindows() ? Path.Combine(_layout.BinDir, "cc-excel") : Path.Combine(ShimDir, "cc-excel");
        File.WriteAllText(bashForm, OperatingSystem.IsWindows()
            ? "#!/bin/sh\nexec \"/c/Users/soren/AppData/Local/cc-director/pyenv/Scripts/cc-excel.exe\" \"$@\"\n"
            : $"#!/bin/sh\nexec \"{PythonToolsInstaller.ConsoleScriptPath(_layout, "cc-excel")}\" \"$@\"\n");

        new PythonToolsInstaller(_layout).RemoveLegacyAliasShims();

        Assert.True(File.Exists(windowsForm),
            "the purge removed a hand-written .cmd that points straight at the venv executable");
        Assert.True(File.Exists(bashForm),
            "the purge removed a hand-written bash launcher that points straight at the venv executable");
    }

    /// <summary>
    /// BOTH FORMS THE PRODUCT WRITES MUST STILL BE PURGED, and the bash one is not decoration.
    ///
    /// Measured on the owner's machine before the release: cc-playwright and cc-comm-queue are both
    /// present as BARE-NAME BASH shims, which compose the same relative path a different way from the
    /// .cmd. A rule taking only the .cmd wording (%~dp0..\) would be inert on every one of them, and the
    /// purge would quietly stop working on exactly the two files it exists to remove while every other
    /// test here stayed green.
    /// </summary>
    [Fact]
    public void BothFormsTheProductWritesAreStillPurged()
    {
        var written = new List<string>();
        foreach (var name in new[] { "cc-playwright", "cc-comm-queue" })
            written.AddRange(ProductShimsFor(name));

        new PythonToolsInstaller(_layout).RemoveLegacyAliasShims();

        foreach (var path in written)
            Assert.False(Present(path), $"a launcher the product itself wrote survived the purge: {path}");
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
