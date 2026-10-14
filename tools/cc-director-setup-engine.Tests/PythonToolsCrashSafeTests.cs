using System.Runtime.Versioning;
using System.Threading;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Tests for the issue #994 crash-safe base-Python provisioning: the whole-directory set-aside that leaves the
/// live runtime intact when a rebuild cannot complete, the runtime-verify that rejects an incomplete staged
/// Python before it can replace a working one, and the cross-process install lock. These are the guards that
/// stop a redundant or interrupted install (a running Director holding a .pyd open) from stripping the shared
/// Python of its standard library and breaking every cc-* tool.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PythonToolsCrashSafeTests : IDisposable
{
    private readonly string _dir;
    private readonly InstallLayout _layout;

    public PythonToolsCrashSafeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-pycrash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _layout = new InstallLayout(Path.Combine(_dir, "local"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    // --- SetDirAside / RestoreDirOver: live trees are renamed, never deleted in place ------------------

    [Fact]
    public void SetDirAside_ExistingDir_KeepsEveryByteAtTheBackup()
    {
        var live = Path.Combine(_dir, "live");
        Directory.CreateDirectory(live);
        File.WriteAllText(Path.Combine(live, "keep.txt"), "original");
        var backup = Path.Combine(_dir, "backup");

        var ok = PythonToolsInstaller.SetDirAside(live, backup, out var err);

        Assert.True(ok, err);
        Assert.False(Directory.Exists(live));
        Assert.Equal("original", File.ReadAllText(Path.Combine(backup, "keep.txt")));
    }

    [Fact]
    public void SetDirAside_NoLiveDir_SucceedsAndCreatesNoBackup()
    {
        // A first install has nothing to keep. That is not a failure, and the caller tells the two apart by
        // whether the backup exists - so it must not be created empty.
        var backup = Path.Combine(_dir, "backup");

        var ok = PythonToolsInstaller.SetDirAside(Path.Combine(_dir, "live"), backup, out var err);

        Assert.True(ok, err);
        Assert.False(Directory.Exists(backup));
    }

    [Fact]
    public void SetDirAside_LiveHasOpenFile_FailsAndLeavesLiveIntact()
    {
        // The issue #994 and #3987 guarantee: when a running process holds a file open inside the live tree,
        // Windows refuses the rename, so this fails - and the live tree is left EXACTLY as it was, instead of
        // the recursive delete that used to strip it and then stop on the locked file.
        var live = Path.Combine(_dir, "live");
        Directory.CreateDirectory(live);
        File.WriteAllText(Path.Combine(live, "keep.txt"), "original");
        File.WriteAllText(Path.Combine(live, "locked.pyd"), "held by a running tool");
        var backup = Path.Combine(_dir, "backup");

        bool ok;
        string err;
        using (new FileStream(Path.Combine(live, "locked.pyd"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            ok = PythonToolsInstaller.SetDirAside(live, backup, out err);
        }

        Assert.False(ok);
        Assert.NotEqual("", err);
        Assert.Equal("original", File.ReadAllText(Path.Combine(live, "keep.txt")));
        Assert.False(Directory.Exists(backup));
    }

    [Fact]
    public void RestoreDirOver_HalfBuiltTreeInPlace_ReplacesItWithThePreviousOne()
    {
        // The shape of the real failure: the rebuild got as far as creating a new tree, then failed.
        var live = Path.Combine(_dir, "live");
        var backup = Path.Combine(_dir, "backup");
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "keep.txt"), "original");
        Directory.CreateDirectory(live);
        File.WriteAllText(Path.Combine(live, "half.txt"), "a half-built tree");

        var ok = PythonToolsInstaller.RestoreDirOver(backup, live, out var err);

        Assert.True(ok, err);
        Assert.Equal("original", File.ReadAllText(Path.Combine(live, "keep.txt")));
        Assert.False(File.Exists(Path.Combine(live, "half.txt")));
        Assert.False(Directory.Exists(backup)); // consumed by the restore
    }

    [Fact]
    public void RestoreDirOver_NothingWasSetAside_ReportsItRatherThanClaimingARestore()
    {
        var backup = Path.Combine(_dir, "backup");

        var ok = PythonToolsInstaller.RestoreDirOver(backup, Path.Combine(_dir, "live"), out var err);

        Assert.False(ok);
        Assert.Contains(backup, err);
    }

    // --- InstallAsync: an incomplete staged Python never replaces a working one --------------------

    [Fact]
    public async Task InstallAsync_IncompleteStagedPython_LeavesLivePythonUntouched()
    {
        // The live base Python is healthy-looking (a sentinel proves it). The release's Python asset extracts
        // an interpreter that cannot run (a non-PE "python.exe"), so it fails the runtime verify while still
        // in staging. The install must fail WITHOUT touching the live Python and WITHOUT recording a version.
        Directory.CreateDirectory(_layout.PythonDir);
        var sentinel = Path.Combine(_layout.PythonDir, "SENTINEL.txt");
        File.WriteAllText(sentinel, "do-not-delete");

        var release = StageIncompletePythonRelease("9.9.9", "cc-pdf");
        var result = await new PythonToolsInstaller(_layout).InstallAsync(release, new ReleaseSource());

        Assert.False(result.Success);
        Assert.Contains("standard library", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(sentinel), "the live base Python was disturbed by a failed rebuild");
        Assert.Equal("do-not-delete", File.ReadAllText(sentinel));
        Assert.Null(InstalledManifest.Load(_layout).Get(PythonToolsInstaller.ComponentId));
    }

    // --- SharedInstallLock: cross-process serialization --------------------------------------------

    [Fact]
    public void SharedInstallLock_HeldByAnotherThread_TimesOut()
    {
        // EVERY ACQUIRE HERE IS SCOPED TO THIS TEST'S OWN ROOT. The lock used to be one machine-wide
        // name, so this test contended with every other test that installs, with every other worktree
        // running the same suite, and with any real installer on the box. It failed with "waited 0 min
        // for the shared Python install lock" whenever a neighbour happened to hold it - the holder
        // thread below timing out before it could set the event. Nothing about the code under test had
        // gone wrong; the test was simply reading somebody else's lock.
        var root = Path.Combine(_dir, "lock-scope");

        using var acquired = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);

        var holder = new Thread(() =>
        {
            using var held = SharedInstallLock.Acquire(TimeSpan.FromSeconds(5), root);
            acquired.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        }) { IsBackground = true };
        holder.Start();

        Assert.True(acquired.Wait(TimeSpan.FromSeconds(5)), "holder never acquired the lock");
        // A second acquirer (this thread) must not be able to take it while it is held elsewhere.
        Assert.Throws<TimeoutException>(() => SharedInstallLock.Acquire(TimeSpan.FromMilliseconds(300), root));

        release.Set();
        holder.Join(TimeSpan.FromSeconds(5));

        // Once released, it is acquirable again.
        using var reacquired = SharedInstallLock.Acquire(TimeSpan.FromSeconds(5), root);
    }

    [Fact]
    public void SharedInstallLock_DifferentRoots_DoNotContend()
    {
        // The property that makes the lock correct rather than merely quieter: two installs into
        // DIFFERENT trees must not block each other. Without this, scoping could be "fixed" by handing
        // every caller the same key again and the test above would still pass.
        var rootA = Path.Combine(_dir, "tree-a");
        var rootB = Path.Combine(_dir, "tree-b");

        using var heldA = SharedInstallLock.Acquire(TimeSpan.FromSeconds(5), rootA);

        // Must be immediate, not a wait-then-succeed: a short bound proves there is no contention.
        using var heldB = SharedInstallLock.Acquire(TimeSpan.FromMilliseconds(300), rootB);

        Assert.NotEqual(SharedInstallLock.NameFor(rootA), SharedInstallLock.NameFor(rootB));
    }

    [Fact]
    public void SharedInstallLock_SameRootSpeltDifferently_IsTheSameLock()
    {
        // The other half: scoping must not be defeated by a trailing separator or a case difference,
        // or two installs into ONE tree would proceed at once - the race the lock exists to stop
        // (issue #994).
        var root = Path.Combine(_dir, "tree-c");
        var sameWithSeparator = root + Path.DirectorySeparatorChar;
        var sameDifferentCase = root.ToUpperInvariant();

        Assert.Equal(SharedInstallLock.NameFor(root), SharedInstallLock.NameFor(sameWithSeparator));
        Assert.Equal(SharedInstallLock.NameFor(root), SharedInstallLock.NameFor(sameDifferentCase));
    }

    /// <summary>
    /// Stage a local release whose assets are real, SHA-matching zips, but whose "python.exe" is a non-PE
    /// text file - so InstallAsync gets past download/extract and fails at the runtime verify of the STAGED
    /// Python (BasePythonRuns), before any swap. The tools bundle carries a minimal manifest + wheelhouse.
    /// </summary>
    private ResolvedRelease StageIncompletePythonRelease(string version, params string[] scripts)
    {
        var releaseDir = Path.Combine(_dir, "release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(releaseDir);
        var work = Path.Combine(_dir, "work-" + Guid.NewGuid().ToString("N"));

        // Python asset: a "python.exe" that is NOT a valid executable, so launching it throws and the runtime
        // probe reports the interpreter as dead (exactly the "missing standard library" shape from the field).
        var pyStage = Path.Combine(work, "py");
        Directory.CreateDirectory(pyStage);
        File.WriteAllText(Path.Combine(pyStage, "python.exe"), "not a real interpreter");
        var pyZip = Path.Combine(releaseDir, PythonToolsInstaller.PythonAsset);
        System.IO.Compression.ZipFile.CreateFromDirectory(pyStage, pyZip);

        // Tools asset: tools-manifest.json + an (empty) wheelhouse so the bundle parses.
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
