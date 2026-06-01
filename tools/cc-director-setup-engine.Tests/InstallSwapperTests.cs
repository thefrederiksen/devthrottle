using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

public class InstallSwapperTests : IDisposable
{
    private readonly string _dir;

    public InstallSwapperTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-swap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Place_FreshInstall_NoBackup()
    {
        var staged = Write("staged.bin", "v1");
        var target = Path.Combine(_dir, "sub", "app.exe");

        var backup = InstallSwapper.Place(target, staged);

        Assert.Null(backup);
        Assert.True(File.Exists(target));
        Assert.Equal("v1", File.ReadAllText(target));
    }

    [Fact]
    public void Place_OverExisting_KeepsOldBackup()
    {
        var target = Write("app.exe", "old-version");
        var staged = Write("staged.bin", "new-version");

        var backup = InstallSwapper.Place(target, staged);

        Assert.Equal(target + ".old", backup);
        Assert.Equal("new-version", File.ReadAllText(target));
        Assert.Equal("old-version", File.ReadAllText(target + ".old"));
    }

    [Fact]
    public void Rollback_RestoresPreviousVersion()
    {
        var target = Write("app.exe", "v1");
        var staged = Write("staged.bin", "v2");
        InstallSwapper.Place(target, staged);          // target now v2, .old is v1
        Assert.Equal("v2", File.ReadAllText(target));

        var rolledBack = InstallSwapper.Rollback(target);

        Assert.True(rolledBack);
        Assert.Equal("v1", File.ReadAllText(target));   // restored
        Assert.False(File.Exists(target + ".old"));     // backup consumed
    }

    [Fact]
    public void Rollback_NoBackup_ReturnsFalse()
    {
        var target = Write("app.exe", "v1");
        Assert.False(InstallSwapper.Rollback(target));
        Assert.Equal("v1", File.ReadAllText(target));   // untouched
    }

    [Fact]
    public void Place_MissingSource_Throws()
    {
        var target = Path.Combine(_dir, "app.exe");
        Assert.Throws<FileNotFoundException>(() => InstallSwapper.Place(target, Path.Combine(_dir, "nope.bin")));
    }
}
