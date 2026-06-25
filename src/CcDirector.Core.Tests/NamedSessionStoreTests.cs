using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

public class NamedSessionStoreTests : IDisposable
{
    private readonly string _tempDir;

    public NamedSessionStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"NamedSessionStoreTests_{Guid.NewGuid():N}");
    }

    [Fact]
    public void Save_ValidNamedSession_WritesJsonFile()
    {
        var store = new NamedSessionStore(_tempDir);
        var named = MakeNamedSession("My Project");

        var result = store.Save(named);

        Assert.True(result);
        var filePath = Path.Combine(_tempDir, "my-project.named-session.json");
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public void LoadAll_MultipleNamedSessions_ReturnsSorted()
    {
        var store = new NamedSessionStore(_tempDir);
        store.Save(MakeNamedSession("Zebra"));
        store.Save(MakeNamedSession("Alpha"));
        store.Save(MakeNamedSession("Mango"));

        var all = store.LoadAll();

        Assert.Equal(3, all.Count);
        Assert.Equal("Alpha", all[0].Name);
        Assert.Equal("Mango", all[1].Name);
        Assert.Equal("Zebra", all[2].Name);
    }

    [Fact]
    public void LoadAll_EmptyFolder_ReturnsEmptyList()
    {
        var store = new NamedSessionStore(_tempDir);

        var all = store.LoadAll();

        Assert.Empty(all);
    }

    [Fact]
    public void Load_NonExistent_ReturnsNull()
    {
        var store = new NamedSessionStore(_tempDir);

        var result = store.Load("does-not-exist");

        Assert.Null(result);
    }

    [Fact]
    public void Delete_ExistingNamedSession_RemovesFile()
    {
        var store = new NamedSessionStore(_tempDir);
        store.Save(MakeNamedSession("To Delete"));

        var deleted = store.Delete("to-delete");

        Assert.True(deleted);
        Assert.Null(store.Load("to-delete"));
    }

    [Fact]
    public void Delete_NonExistent_ReturnsFalse()
    {
        var store = new NamedSessionStore(_tempDir);

        var result = store.Delete("ghost");

        Assert.False(result);
    }

    [Theory]
    [InlineData("My Session", "my-session")]
    [InlineData("Hello   World", "hello-world")]
    [InlineData("  spaces  ", "spaces")]
    [InlineData("Special!@#Chars$%^", "specialchars")]
    [InlineData("Already-Slugged", "already-slugged")]
    [InlineData("UPPERCASE", "uppercase")]
    [InlineData("   ", "named-session")]
    [InlineData("", "named-session")]
    public void ToSlug_SpecialCharacters_ProducesValidFilename(string name, string expected)
    {
        var slug = NamedSessionStore.ToSlug(name);

        Assert.Equal(expected, slug);
    }

    [Fact]
    public void Save_OverwriteExisting_UpdatesFile_NoDuplicate()
    {
        var store = new NamedSessionStore(_tempDir);
        var original = MakeNamedSession("Overwrite Me");
        original.AgentId = "agent-1";
        store.Save(original);

        var updated = MakeNamedSession("Overwrite Me");
        updated.AgentId = "agent-2";
        store.Save(updated);

        // Same slug -> one file, latest content wins.
        Assert.Single(store.LoadAll());
        var loaded = store.Load("overwrite-me");
        Assert.NotNull(loaded);
        Assert.Equal("agent-2", loaded.AgentId);
    }

    [Fact]
    public void Save_AndLoad_RoundTripsAllFields()
    {
        var store = new NamedSessionStore(_tempDir);
        var named = new NamedSessionDefinition
        {
            Version = 1,
            Name = "Full Test",
            RepoPath = @"D:\Repos\project-a",
            AgentId = "11111111-2222-3333-4444-555555555555",
            Color = "#2563EB",
            Arguments = "--allowedTools bash",
            CreatedAt = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 3, 4, 15, 30, 0, TimeSpan.Zero),
        };

        store.Save(named);
        var loaded = store.Load("full-test");

        Assert.NotNull(loaded);
        Assert.Equal(1, loaded.Version);
        Assert.Equal("Full Test", loaded.Name);
        Assert.Equal(@"D:\Repos\project-a", loaded.RepoPath);
        Assert.Equal("11111111-2222-3333-4444-555555555555", loaded.AgentId);
        Assert.Equal("#2563EB", loaded.Color);
        Assert.Equal("--allowedTools bash", loaded.Arguments);
    }

    [Fact]
    public void Exists_ExistingNamedSession_ReturnsTrue()
    {
        var store = new NamedSessionStore(_tempDir);
        store.Save(MakeNamedSession("Exists Test"));

        Assert.True(store.Exists("exists-test"));
    }

    [Fact]
    public void Exists_NonExistent_ReturnsFalse()
    {
        var store = new NamedSessionStore(_tempDir);

        Assert.False(store.Exists("nope"));
    }

    [Fact]
    public void Save_NullSession_Throws()
    {
        var store = new NamedSessionStore(_tempDir);

        Assert.Throws<ArgumentNullException>(() => store.Save(null!));
    }

    private static NamedSessionDefinition MakeNamedSession(string name)
    {
        return new NamedSessionDefinition
        {
            Name = name,
            RepoPath = @"D:\Repos\test-repo",
            AgentId = "test-agent-id",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); }
            catch { /* cleanup best effort */ }
        }
    }
}
