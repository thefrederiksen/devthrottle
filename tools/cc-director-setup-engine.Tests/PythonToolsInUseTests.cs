using System.Runtime.Versioning;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Issue #3987: a tools update must never leave a machine with FEWER working tools than it had.
///
/// On 14 October 2026 a `cc-secrets ask` pop-up, waiting hours for the owner, held a file open inside the shared
/// venv. The hourly refresh deleted the shims and most of the venv, stopped on that file with "Access to the
/// path '_rust.pyd' is denied", and left every session on the machine without cc-devthrottle. These tests hold
/// the three guarantees that replaced it: a tool in use HOLDS the update and changes nothing; a failure after
/// something live has moved puts the previous Python, venv and shims back; and nothing in the installer deletes
/// a live tools tree in place.
///
/// The open file here is a FileStream with FileShare.None standing in for the running tool - the same thing
/// Windows sees, and the same refusal of a directory rename.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PythonToolsInUseTests : IDisposable
{
    private const string PreviousVersion = "1.0.0";
    private const string NewVersion = "2.0.0";
    private const string Tool = "cc-pdf";

    private readonly string _dir;
    private readonly InstallLayout _layout;

    public PythonToolsInUseTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-pyinuse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _layout = new InstallLayout(Path.Combine(_dir, "local"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string PythonSentinel => Path.Combine(_layout.PythonDir, "PREVIOUS-PYTHON.txt");
    private string PreviousConsoleScript => PythonToolsInstaller.ConsoleScriptPath(_layout, Tool);
    private string Shim => Path.Combine(_layout.BinDir, Tool + ".cmd");

    /// <summary>A machine with a working previous install: base Python, a venv with the tool, its shim, its version.</summary>
    private void GiveTheMachineWorkingTools()
    {
        Directory.CreateDirectory(_layout.PythonDir);
        File.WriteAllText(Path.Combine(_layout.PythonDir, "python.exe"), "previous interpreter");
        File.WriteAllText(PythonSentinel, "previous");
        Directory.CreateDirectory(_layout.PyenvBinDir);
        File.WriteAllText(Path.Combine(_layout.PyenvBinDir, "python.exe"), "previous venv interpreter");
        File.WriteAllText(PreviousConsoleScript, "previous console script");
        new PythonToolsInstaller(_layout).WriteShims(new[] { Tool });
        var im = InstalledManifest.Load(_layout);
        im.Set(PythonToolsInstaller.ComponentId, PreviousVersion);
        im.Save(_layout);
        PythonToolsState.SaveScripts(_layout, new[] { Tool });
    }

    private void AssertThePreviousToolsStillWork()
    {
        Assert.True(File.Exists(PythonSentinel), "the previous base Python is gone");
        Assert.Equal("previous console script", File.ReadAllText(PreviousConsoleScript));
        Assert.True(File.Exists(Shim), "the tool's shim is gone, so the tool cannot be reached");
        Assert.Equal(PreviousVersion, InstalledManifest.Load(_layout).Get(PythonToolsInstaller.ComponentId));
    }

    [Fact]
    public async Task InstallAsync_AToolHoldsAFileInTheVenv_HoldsAndChangesNothing()
    {
        GiveTheMachineWorkingTools();
        var runs = new List<string>();
        var installer = new PythonToolsInstaller(_layout, Runner(runs, pipSucceeds: true));

        PythonToolsResult result;
        using (new FileStream(PreviousConsoleScript, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await installer.InstallAsync(StageRelease(NewVersion, Tool), new ReleaseSource());
        }

        Assert.False(result.Success);
        Assert.True(result.HeldBecauseInUse, result.Message);
        Assert.Contains("nothing was changed", result.Message);
        // It stopped BEFORE anything destructive: no venv was created, no pip ran.
        Assert.DoesNotContain(runs, r => r.StartsWith("-m venv", StringComparison.Ordinal));
        AssertThePreviousToolsStillWork();
    }

    [Fact]
    public async Task InstallAsync_AToolHoldsAFileInThePython_HoldsAndChangesNothing()
    {
        GiveTheMachineWorkingTools();
        var runs = new List<string>();
        var installer = new PythonToolsInstaller(_layout, Runner(runs, pipSucceeds: true));

        PythonToolsResult result;
        using (new FileStream(PythonSentinel, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await installer.InstallAsync(StageRelease(NewVersion, Tool), new ReleaseSource());
        }

        Assert.True(result.HeldBecauseInUse, result.Message);
        Assert.DoesNotContain(runs, r => r.StartsWith("-m venv", StringComparison.Ordinal));
        AssertThePreviousToolsStillWork();
    }

    [Fact]
    public async Task InstallAsync_WhilePipRuns_TheInstalledToolAndItsShimAreStillThere()
    {
        // The 16 October 2026 failure: a license switch looked for cc-devthrottle in the venv 41 seconds into a
        // tools update and found nothing, because the update set the live venv aside for the whole pip install.
        // The new runtime is now built beside the live one, so at the moment pip is running the installed tool,
        // its shim and the base Python are all exactly where they were.
        GiveTheMachineWorkingTools();
        bool? scriptThereDuringPip = null, shimThereDuringPip = null, pythonThereDuringPip = null;
        var runs = new List<string>();
        var installer = new PythonToolsInstaller(_layout, Runner(runs, pipSucceeds: true, onPip: () =>
        {
            scriptThereDuringPip = File.Exists(PreviousConsoleScript) && File.ReadAllText(PreviousConsoleScript) == "previous console script";
            shimThereDuringPip = File.Exists(Shim);
            pythonThereDuringPip = File.Exists(PythonSentinel);
        }));

        var result = await installer.InstallAsync(StageRelease(NewVersion, Tool), new ReleaseSource());

        Assert.True(result.Success, result.Message);
        Assert.True(scriptThereDuringPip, "the installed console script was gone while pip ran");
        Assert.True(shimThereDuringPip, "the tool's shim was gone while pip ran");
        Assert.True(pythonThereDuringPip, "the installed base Python was gone while pip ran");
    }

    [Fact]
    public async Task InstallAsync_PipFails_LeavesTheInstalledToolsUntouched()
    {
        GiveTheMachineWorkingTools();
        var runs = new List<string>();
        var installer = new PythonToolsInstaller(_layout, Runner(runs, pipSucceeds: false));

        var result = await installer.InstallAsync(StageRelease(NewVersion, Tool), new ReleaseSource());

        Assert.False(result.Success);
        Assert.False(result.HeldBecauseInUse);
        // It really did get as far as pip: a venv was created and pip ran before the failure.
        Assert.Contains(runs, r => r.StartsWith("-m pip install", StringComparison.Ordinal));
        Assert.Contains("not touched", result.Message);
        AssertThePreviousToolsStillWork();
        Assert.Empty(SiblingsOf(_layout.PythonDir).Concat(SiblingsOf(_layout.PyenvDir)));
    }

    [Fact]
    public async Task InstallAsync_Succeeds_InstallsTheNewToolsAndRemovesLeftoversButNeverAProbe()
    {
        GiveTheMachineWorkingTools();
        // Trees earlier runs set aside and could not delete - and a probe, which is NOT a leftover: a probe left
        // behind means its rename was never undone, so it may be the only copy of the live tree.
        var oldPython = _layout.PythonDir + ".old-0123";
        var prevVenv = _layout.PyenvDir + ".prev-0123";
        var probe = _layout.PyenvDir + ".probe-0123";
        foreach (var d in new[] { oldPython, prevVenv, probe }) Directory.CreateDirectory(d);

        var runs = new List<string>();
        var result = await new PythonToolsInstaller(_layout, Runner(runs, pipSucceeds: true))
            .InstallAsync(StageRelease(NewVersion, Tool), new ReleaseSource());

        Assert.True(result.Success, result.Message);
        // The new console script is in place, and it names the venv where it now lives - not the staging folder
        // it was built in, which no longer exists.
        var installed = File.ReadAllText(PreviousConsoleScript);
        Assert.EndsWith("new console script", installed);
        Assert.StartsWith("#!" + Path.Combine(_layout.PyenvBinDir, "python.exe") + "\n", installed);
        Assert.Contains("home = " + _layout.PythonDir + "\r\n", File.ReadAllText(Path.Combine(_layout.PyenvDir, "pyvenv.cfg")));
        Assert.True(File.Exists(Shim));
        Assert.False(File.Exists(PythonSentinel), "the previous Python was not replaced");
        Assert.Equal(NewVersion, InstalledManifest.Load(_layout).Get(PythonToolsInstaller.ComponentId));
        Assert.False(Directory.Exists(oldPython));
        Assert.False(Directory.Exists(prevVenv));
        Assert.True(Directory.Exists(probe), "a probe directory was deleted; it may have been the live tree");
    }

    [Fact]
    public async Task InstallAsync_AToolStartsInTheVenvAfterTheCheck_HoldsAndChangesNothing()
    {
        // The race the second hold covers: the check passes, then a tool opens a file in the venv before it is
        // swapped out. The new runtime has been built by then, but the swap is refused and nothing live moved.
        GiveTheMachineWorkingTools();
        FileStream? lateTool = null;
        var runs = new List<string>();
        var installer = new PythonToolsInstaller(_layout, Runner(runs, pipSucceeds: true,
            onStagedPythonProbe: () => lateTool = new FileStream(PreviousConsoleScript, FileMode.Open, FileAccess.Read, FileShare.None)));

        PythonToolsResult result;
        try { result = await installer.InstallAsync(StageRelease(NewVersion, Tool), new ReleaseSource()); }
        finally { lateTool?.Dispose(); }

        Assert.True(result.HeldBecauseInUse, result.Message);
        AssertThePreviousToolsStillWork();
        Assert.Empty(SiblingsOf(_layout.PythonDir).Concat(SiblingsOf(_layout.PyenvDir)));
    }

    [Fact]
    public async Task InstallAsync_AToolStartsInThePythonAfterTheCheck_PutsTheVenvBackAndHolds()
    {
        // Here the venv HAS been set aside when the Python refuses to move, so the hold has to restore it first.
        GiveTheMachineWorkingTools();
        FileStream? lateTool = null;
        var runs = new List<string>();
        var installer = new PythonToolsInstaller(_layout, Runner(runs, pipSucceeds: true,
            onStagedPythonProbe: () => lateTool = new FileStream(PythonSentinel, FileMode.Open, FileAccess.Read, FileShare.None)));

        PythonToolsResult result;
        try { result = await installer.InstallAsync(StageRelease(NewVersion, Tool), new ReleaseSource()); }
        finally { lateTool?.Dispose(); }

        Assert.True(result.HeldBecauseInUse, result.Message);
        AssertThePreviousToolsStillWork();
        Assert.Empty(SiblingsOf(_layout.PythonDir).Concat(SiblingsOf(_layout.PyenvDir)));
    }

    [Fact]
    public async Task InstallAsync_AnExceptionMidInstall_FailsWithItInsteadOfEscaping()
    {
        // The 14 October failure was an EXCEPTION ("Access to the path '_rust.pyd' is denied"), not an exit code,
        // and it escaped past every restore. Thrown from inside pip here, while the new runtime is being built.
        GiveTheMachineWorkingTools();
        var runs = new List<string>();
        var installer = new PythonToolsInstaller(_layout, Runner(runs, pipSucceeds: true, pipThrows: true));

        var result = await installer.InstallAsync(StageRelease(NewVersion, Tool), new ReleaseSource());

        Assert.False(result.Success);
        Assert.Contains("_rust.pyd", result.Message);
        Assert.Contains("not touched", result.Message);
        AssertThePreviousToolsStillWork();
        Assert.Empty(SiblingsOf(_layout.PythonDir).Concat(SiblingsOf(_layout.PyenvDir)));
    }

    [Fact]
    public void ToolTreesAreFree_ALiveTreeWasLeftUnderItsProbeName_PutsItBack()
    {
        // A probe whose rename back was refused for too long leaves the live tree under its probe name. The next
        // check must put it back rather than let the next cycle reinstall from nothing.
        GiveTheMachineWorkingTools();
        var probe = _layout.PyenvDir + ".probe-0123";
        Directory.Move(_layout.PyenvDir, probe);

        var free = new PythonToolsInstaller(_layout).ToolTreesAreFree(out var reason);

        Assert.True(free, reason);
        Assert.False(Directory.Exists(probe));
        AssertThePreviousToolsStillWork();
    }

    [Fact]
    public async Task RefreshPythonToolsAsync_AToolIsRunning_HoldsBeforeDownloading()
    {
        GiveTheMachineWorkingTools();
        var release = StageRelease(NewVersion, Tool);

        PythonToolsResult? result;
        using (new FileStream(PreviousConsoleScript, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await new ToolUpdater(_layout).RefreshPythonToolsAsync(release, new ReleaseSource());
        }

        Assert.NotNull(result);
        Assert.True(result!.HeldBecauseInUse, result.Message);
        // Held before the install began: the installer records its steps, and a pre-download hold has none.
        Assert.Empty(result.Steps);
        AssertThePreviousToolsStillWork();
    }

    [Fact]
    public void ToolTreesAreFree_NothingOpen_ReturnsTrueAndLeavesBothTreesInPlace()
    {
        GiveTheMachineWorkingTools();

        var free = new PythonToolsInstaller(_layout).ToolTreesAreFree(out var reason);

        Assert.True(free, reason);
        AssertThePreviousToolsStillWork();
        Assert.Empty(SiblingsOf(_layout.PythonDir).Concat(SiblingsOf(_layout.PyenvDir)));
    }

    [Fact]
    public void Installer_NeverDeletesALiveToolsTreeInPlace()
    {
        // The defect in one line: `ResetDir(_layout.PyenvDir)` - a recursive delete of the live venv that stops
        // partway on a locked file. Only staging directories may be reset; the live trees are only ever renamed.
        var source = File.ReadAllText(InstallerSourcePath());

        foreach (var live in new[] { "_layout.PyenvDir", "_layout.PythonDir" })
        {
            Assert.DoesNotContain($"ResetDir({live})", source);
            Assert.DoesNotContain($"Directory.Delete({live}", source);
            Assert.DoesNotContain($"TryDeleteDir({live})", source);
        }
    }

    private static string InstallerSourcePath()
    {
        var relative = Path.Combine("tools", "cc-director-setup-engine", "PythonToolsInstaller.cs");
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"could not find {relative} above {AppContext.BaseDirectory}");
    }

    /// <summary>
    /// Directories beside <paramref name="live"/> named after it (<c>pyenv.prev-...</c> and so on). Filtered by
    /// name rather than trusted to the pattern: on Windows <c>pyenv.*</c> also matches <c>pyenv</c> itself.
    /// </summary>
    private static IEnumerable<string> SiblingsOf(string live) =>
        Directory.EnumerateDirectories(Path.GetDirectoryName(live)!, Path.GetFileName(live) + ".*")
            .Where(d => Path.GetFileName(d).StartsWith(Path.GetFileName(live) + ".", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A runner that plays the interpreter: the standard-library probe passes, `-m venv` creates the venv's
    /// interpreter, and pip either writes the tool's console script or fails. Anything else fails the test.
    /// </summary>
    private RunProcess Runner(List<string> runs, bool pipSucceeds, Action? onStagedPythonProbe = null, bool pipThrows = false,
        Action? onPip = null) =>
        (exe, arguments, _, _) =>
    {
        runs.Add(arguments);
        if (arguments == PythonRuntimeProbe.ProbeArguments)
        {
            // The staged interpreter is probed AFTER the in-use check and BEFORE anything live moves: the exact
            // window in which a tool can start and slip past the check.
            if (exe.Contains(".staging-", StringComparison.Ordinal)) onStagedPythonProbe?.Invoke();
            return (0, "");
        }
        if (arguments.StartsWith("-m venv", StringComparison.Ordinal))
        {
            // The venv is made wherever the installer says, the way `python -m venv <folder>` does.
            var venv = arguments["-m venv ".Length..].Trim('"');
            var scripts = Path.Combine(venv, "Scripts");
            Directory.CreateDirectory(scripts);
            File.WriteAllText(Path.Combine(scripts, "python.exe"), "new venv interpreter");
            File.WriteAllText(Path.Combine(venv, "pyvenv.cfg"), $"home = {Path.GetDirectoryName(exe)}\r\n");
            return (0, "");
        }
        if (arguments.StartsWith("-m pip install", StringComparison.Ordinal))
        {
            onPip?.Invoke();
            if (pipThrows) throw new IOException("The process cannot access the file '_rust.pyd' (thrown by the test runner)");
            if (!pipSucceeds) return (1, "pip refused by the test runner");
            // pip writes each console script beside the interpreter that ran it, naming that interpreter on its
            // first line - the absolute path a real venv bakes in.
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(exe)!, Tool + ".exe"), $"#!{exe}\nnew console script");
            return (0, "");
        }
        throw new InvalidOperationException($"unexpected run of {exe} {arguments}");
    };

    /// <summary>A local release whose two assets are real, SHA-matching zips with a placeholder interpreter.</summary>
    private ResolvedRelease StageRelease(string version, params string[] scripts)
    {
        var releaseDir = Path.Combine(_dir, "release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(releaseDir);
        var work = Path.Combine(_dir, "work-" + Guid.NewGuid().ToString("N"));

        var pyStage = Path.Combine(work, "py");
        Directory.CreateDirectory(pyStage);
        File.WriteAllText(Path.Combine(pyStage, "python.exe"), "new interpreter");
        var pyZip = Path.Combine(releaseDir, PythonToolsInstaller.PythonAsset);
        System.IO.Compression.ZipFile.CreateFromDirectory(pyStage, pyZip);

        var toolsStage = Path.Combine(work, "tools");
        Directory.CreateDirectory(Path.Combine(toolsStage, "wheelhouse"));
        var toolsArr = string.Join(",", scripts.Select(s => $"{{\"dist\":\"{s}\",\"scripts\":[\"{s}\"]}}"));
        File.WriteAllText(Path.Combine(toolsStage, "tools-manifest.json"),
            $"{{\"bundleVersion\":\"{version}\",\"tools\":[{toolsArr}]}}");
        var toolsZip = Path.Combine(releaseDir, PythonToolsInstaller.ToolsAsset);
        System.IO.Compression.ZipFile.CreateFromDirectory(toolsStage, toolsZip);

        var json =
            "{\"version\":\"" + version + "\",\"assets\":{" +
            "\"cc-python-win-x64.zip\":{\"version\":\"" + version + "\",\"sha256\":\"" + Hashing.Sha256OfFile(pyZip) + "\",\"platform\":\"windows\",\"size\":" + new FileInfo(pyZip).Length + "}," +
            "\"cc-tools-pyenv-win-x64.zip\":{\"version\":\"" + version + "\",\"sha256\":\"" + Hashing.Sha256OfFile(toolsZip) + "\",\"platform\":\"windows\",\"size\":" + new FileInfo(toolsZip).Length + "}" +
            "}}";
        File.WriteAllText(Path.Combine(releaseDir, "release-manifest.json"), json);
        return ReleaseSource.LoadLocalReleaseDir(releaseDir);
    }
}
