using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests;

public class RepositoryRegistryRenameTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;

    public RepositoryRegistryRenameTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"RepoRegistryRenameTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "repositories.json");
    }

    [Fact]
    public void Rename_RegisteredRepo_UpdatesNameAndPersists()
    {
        var repoDir = Path.Combine(_tempDir, "test-repo");
        Directory.CreateDirectory(repoDir);

        var registry = new RepositoryRegistry(_filePath);
        registry.Load();
        registry.TryAdd(repoDir);

        var renamed = registry.Rename(repoDir, "My Fancy Name");

        Assert.True(renamed);
        Assert.Equal("My Fancy Name", registry.Repositories[0].Name);

        // Verify persisted to disk
        var registry2 = new RepositoryRegistry(_filePath);
        registry2.Load();
        Assert.Equal("My Fancy Name", registry2.Repositories[0].Name);
    }

    [Fact]
    public void Rename_PathMatchIsCaseInsensitiveAndTrailingSlashTolerant()
    {
        var repoDir = Path.Combine(_tempDir, "Case-Repo");
        Directory.CreateDirectory(repoDir);

        var registry = new RepositoryRegistry(_filePath);
        registry.Load();
        registry.TryAdd(repoDir);

        var renamed = registry.Rename(repoDir.ToUpperInvariant() + "\\", "Renamed");

        Assert.True(renamed);
        Assert.Equal("Renamed", registry.Repositories[0].Name);
    }

    [Fact]
    public void Rename_UnknownPath_ReturnsFalse()
    {
        var registry = new RepositoryRegistry(_filePath);
        registry.Load();

        var renamed = registry.Rename(Path.Combine(_tempDir, "never-added"), "Whatever");

        Assert.False(renamed);
    }

    [Fact]
    public void Rename_BlankName_Throws()
    {
        var repoDir = Path.Combine(_tempDir, "blank-repo");
        Directory.CreateDirectory(repoDir);

        var registry = new RepositoryRegistry(_filePath);
        registry.Load();
        registry.TryAdd(repoDir);

        Assert.Throws<ArgumentException>(() => registry.Rename(repoDir, "   "));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }
}
