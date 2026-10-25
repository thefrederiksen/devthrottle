using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests.Git;

/// <summary>
/// The per-repository pooled-worktree setting: it is OFF unless a repository says otherwise, its
/// default size is four, and one repository's answer says nothing about another's.
///
/// Each test runs against an isolated CC_DIRECTOR_ROOT so it reads and writes a throwaway
/// config.json; the "ConfigEnvSerial" collection keeps it from racing the other classes that
/// redirect the process-wide root.
/// </summary>
[Collection("ConfigEnvSerial")]
public sealed class WorktreePoolSettingsTests : IDisposable
{
    private readonly string _root;
    private readonly string? _previousRoot;

    private const string Repo = @"D:\Repos\PooledRepo";
    private const string OtherRepo = @"D:\Repos\PlainRepo";

    public WorktreePoolSettingsTests()
    {
        _previousRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-worktreepool-setting-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _previousRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void For_RepositoryNobodyConfigured_IsOff()
    {
        var setting = WorktreePoolSettings.For(Repo);

        Assert.False(setting.Enabled);
        Assert.Equal(4, setting.PoolSize);
    }

    [Fact]
    public void For_BlankPath_IsOff()
    {
        Assert.False(WorktreePoolSettings.For(null).Enabled);
        Assert.False(WorktreePoolSettings.For("   ").Enabled);
    }

    [Fact]
    public void DefaultPoolSize_IsFour()
    {
        // The Architect's ruling, written down where it is read: a slot costs a full checkout on disk,
        // so four - not the twelve the research suggested.
        Assert.Equal(4, WorktreePoolSettings.DefaultPoolSize);
        Assert.Equal(4, WorktreePoolSettings.Default.PoolSize);
        Assert.False(WorktreePoolSettings.Default.Enabled);
    }

    [Fact]
    public void Save_ThenFor_ReadsItBack()
    {
        WorktreePoolSettings.Save(Repo, new WorktreePoolSetting(Enabled: true, PoolSize: 7));

        var setting = WorktreePoolSettings.For(Repo);
        Assert.True(setting.Enabled);
        Assert.Equal(7, setting.PoolSize);
    }

    [Fact]
    public void Save_IsPerRepository_AndLeavesOthersOff()
    {
        WorktreePoolSettings.Save(Repo, new WorktreePoolSetting(Enabled: true, PoolSize: 5));

        Assert.True(WorktreePoolSettings.For(Repo).Enabled);
        Assert.False(WorktreePoolSettings.For(OtherRepo).Enabled);
    }

    [Fact]
    public void For_IgnoresWhichSeparatorWasTypedAndATrailingOne()
    {
        WorktreePoolSettings.Save(Repo, new WorktreePoolSetting(Enabled: true, PoolSize: 4));

        Assert.True(WorktreePoolSettings.For(@"D:/Repos/PooledRepo").Enabled);
        Assert.True(WorktreePoolSettings.For(Repo + @"\").Enabled);
        Assert.True(WorktreePoolSettings.For(Repo + "/").Enabled);
    }

    /// <summary>
    /// Letter case is ignored on Windows only, where the file system ignores it too. On Linux two
    /// spellings that differ in case are two different folders, so they are two repositories with two
    /// settings - folding them would hand one repository the other's pool. On macOS the key keeps its
    /// case as well: a repository opened under a differently cased spelling reads as not configured,
    /// which is OFF, the answer this setting gives whenever it is unsure. The key is the one
    /// BrowserDefaultStore uses, and the two must stay the same.
    /// </summary>
    [Fact]
    public void For_IgnoresLetterCaseOnlyOnWindows()
    {
        WorktreePoolSettings.Save(Repo, new WorktreePoolSetting(Enabled: true, PoolSize: 4));

        Assert.Equal(OperatingSystem.IsWindows(), WorktreePoolSettings.For(@"d:/repos/pooledrepo").Enabled);
    }

    [Fact]
    public void Clear_PutsItBackToOff()
    {
        WorktreePoolSettings.Save(Repo, new WorktreePoolSetting(Enabled: true, PoolSize: 9));
        WorktreePoolSettings.Clear(Repo);

        var setting = WorktreePoolSettings.For(Repo);
        Assert.False(setting.Enabled);
        Assert.Equal(4, setting.PoolSize);
    }

    [Fact]
    public void Save_RefusesAPoolSizeBelowOne()
    {
        // A pool that can never hand anything out is not a setting anyone means to write, and it would
        // read to the user as the tool being broken.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => WorktreePoolSettings.Save(Repo, new WorktreePoolSetting(Enabled: true, PoolSize: 0)));
    }
}
