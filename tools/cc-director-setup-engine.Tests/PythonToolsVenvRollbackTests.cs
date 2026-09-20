using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Issue #3150: a failed tools rebuild must never leave the machine with LESS than it started with.
///
/// The venv used to be reset in place, so everything from the venv creation to the end of the pip install
/// was a window in which a machine that had working tools ended up with none - the reset had already
/// deleted them and nothing put them back. One Mac lost every cc-* tool, and with them every session's
/// fleet commands, to a single failed pip run.
///
/// These tests cover the two halves of the guarantee that replaced it: the previous environment is moved
/// aside rather than deleted, and it is moved back when the rebuild fails. They are deliberately about
/// directories rather than a real venv - a venv needs a real interpreter, and the guarantee under test is
/// about what survives a failure, not about Python.
/// </summary>
public sealed class PythonToolsVenvRollbackTests : IDisposable
{
    private readonly string _dir;

    public PythonToolsVenvRollbackTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-venvroll-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string Live => Path.Combine(_dir, "pyenv");
    private string Backup => Path.Combine(_dir, "pyenv.prev");

    private void GiveLiveAVenv(string marker = "cc-secrets")
    {
        Directory.CreateDirectory(Path.Combine(Live, "bin"));
        File.WriteAllText(Path.Combine(Live, "bin", marker), "the previous console script");
    }

    // --- SetDirAside: keep the old one, never delete it ---------------------------------------------

    [Fact]
    public void SetDirAside_ExistingVenv_KeepsEveryByteAtTheBackup()
    {
        GiveLiveAVenv();

        var ok = PythonToolsInstaller.SetDirAside(Live, Backup, out var error);

        Assert.True(ok, error);
        Assert.False(Directory.Exists(Live));
        Assert.Equal("the previous console script", File.ReadAllText(Path.Combine(Backup, "bin", "cc-secrets")));
    }

    [Fact]
    public void SetDirAside_NoVenvYet_SucceedsAndCreatesNoBackup()
    {
        // A first install has nothing to keep. That is not a failure, and the caller tells the two apart
        // by whether the backup exists - so it must not be created empty.
        var ok = PythonToolsInstaller.SetDirAside(Live, Backup, out var error);

        Assert.True(ok, error);
        Assert.False(Directory.Exists(Backup));
    }

    // --- RestoreDirOver: put it back over whatever the failed rebuild left --------------------------

    [Fact]
    public void RestoreDirOver_HalfBuiltVenvInPlace_ReplacesItWithThePreviousOne()
    {
        // The shape of the real failure: the rebuild got as far as creating an empty venv, then pip died.
        GiveLiveAVenv();
        PythonToolsInstaller.SetDirAside(Live, Backup, out _);
        Directory.CreateDirectory(Path.Combine(Live, "bin"));
        File.WriteAllText(Path.Combine(Live, "bin", "python3"), "a half-built venv with no tools in it");

        var ok = PythonToolsInstaller.RestoreDirOver(Backup, Live, out var error);

        Assert.True(ok, error);
        Assert.Equal("the previous console script", File.ReadAllText(Path.Combine(Live, "bin", "cc-secrets")));
        Assert.False(File.Exists(Path.Combine(Live, "bin", "python3"))); // the half-built tree is gone
        Assert.False(Directory.Exists(Backup));                          // consumed by the restore
    }

    [Fact]
    public void RestoreDirOver_NothingWasSetAside_ReportsItRatherThanClaimingARestore()
    {
        var ok = PythonToolsInstaller.RestoreDirOver(Backup, Live, out var error);

        Assert.False(ok);
        Assert.Contains(Backup, error);
    }

    [Fact]
    public void SetAsideThenRestore_RoundTrip_LeavesTheMachineExactlyAsItStarted()
    {
        // The whole point of the issue: after a rebuild that fails, every tool that worked before still works.
        GiveLiveAVenv("cc-devthrottle");
        File.WriteAllText(Path.Combine(Live, "bin", "cc-dev-reports"), "another console script");
        var before = Directory.GetFiles(Live, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Live, f)).OrderBy(x => x).ToList();

        Assert.True(PythonToolsInstaller.SetDirAside(Live, Backup, out var setAsideError), setAsideError);
        Directory.CreateDirectory(Live); // the rebuild starts, then fails
        Assert.True(PythonToolsInstaller.RestoreDirOver(Backup, Live, out var restoreError), restoreError);

        var after = Directory.GetFiles(Live, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Live, f)).OrderBy(x => x).ToList();
        Assert.Equal(before, after);
        Assert.Equal("another console script", File.ReadAllText(Path.Combine(Live, "bin", "cc-dev-reports")));
    }
}
