using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

public class NamedSessionStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _existingRepo;

    public NamedSessionStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"NamedSessionStoreTests_{Guid.NewGuid():N}");
        // A real directory used as the "repository folder" for launchable-preset cases.
        _existingRepo = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(_existingRepo);
    }

    [Fact]
    public void Save_ValidPreset_WritesJsonFile()
    {
        var store = new NamedSessionStore(_tempDir);
        var preset = MakePreset("Director on Opus");

        var result = store.Save(preset);

        Assert.True(result);
        var filePath = Path.Combine(_tempDir, "director-on-opus.named-session.json");
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public void LoadAll_MultiplePresets_ReturnsSorted()
    {
        var store = new NamedSessionStore(_tempDir);
        store.Save(MakePreset("Zebra"));
        store.Save(MakePreset("Alpha"));
        store.Save(MakePreset("Mango"));

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
    public void Delete_ExistingPreset_RemovesFile()
    {
        var store = new NamedSessionStore(_tempDir);
        store.Save(MakePreset("To Delete"));

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

    [Fact]
    public void Save_AndLoad_RoundTripsAllFields()
    {
        var store = new NamedSessionStore(_tempDir);
        var preset = new NamedSessionDefinition
        {
            Version = 1,
            Name = "Full Test",
            RepositoryPath = _existingRepo,
            AgentId = "agent-123",
            Model = "claude-opus-4",
            CreatedAt = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 3, 4, 15, 30, 0, TimeSpan.Zero)
        };

        store.Save(preset);
        var loaded = store.Load("full-test");

        Assert.NotNull(loaded);
        Assert.Equal(1, loaded.Version);
        Assert.Equal("Full Test", loaded.Name);
        Assert.Equal(_existingRepo, loaded.RepositoryPath);
        Assert.Equal("agent-123", loaded.AgentId);
        Assert.Equal("claude-opus-4", loaded.Model);
    }

    [Fact]
    public void Save_OverwriteExisting_UpdatesFile()
    {
        var store = new NamedSessionStore(_tempDir);
        var original = MakePreset("Overwrite Me");
        original.Model = "claude-sonnet-4";
        store.Save(original);

        var updated = MakePreset("Overwrite Me");
        updated.Model = "claude-opus-4";
        store.Save(updated);

        var loaded = store.Load("overwrite-me");
        Assert.NotNull(loaded);
        Assert.Equal("claude-opus-4", loaded.Model);
        Assert.Single(store.LoadAll());
    }

    [Fact]
    public void Exists_ExistingPreset_ReturnsTrue()
    {
        var store = new NamedSessionStore(_tempDir);
        store.Save(MakePreset("Exists Test"));

        Assert.True(store.Exists("exists-test"));
    }

    [Fact]
    public void Exists_NonExistent_ReturnsFalse()
    {
        var store = new NamedSessionStore(_tempDir);

        Assert.False(store.Exists("nope"));
    }

    [Theory]
    [InlineData("Director on Opus", "director-on-opus")]
    [InlineData("Hello   World", "hello-world")]
    [InlineData("  spaces  ", "spaces")]
    [InlineData("Special!@#Chars$%^", "specialchars")]
    [InlineData("UPPERCASE", "uppercase")]
    [InlineData("   ", "named-session")]
    [InlineData("", "named-session")]
    public void ToSlug_SpecialCharacters_ProducesValidFilename(string name, string expected)
    {
        var slug = NamedSessionStore.ToSlug(name);

        Assert.Equal(expected, slug);
    }

    [Fact]
    public void LoadAllWithStatus_RepositoryAndAgentPresent_IsLaunchable()
    {
        var store = new NamedSessionStore(_tempDir);
        var preset = MakePreset("Healthy");
        preset.AgentId = "agent-A";
        store.Save(preset);

        var statuses = store.LoadAllWithStatus(new[] { "agent-A" });

        var single = Assert.Single(statuses);
        Assert.True(single.IsLaunchable);
        Assert.Equal(NamedSessionOrphanReason.None, single.OrphanReason);
    }

    [Fact]
    public void LoadAllWithStatus_MissingRepository_IsRepositoryMissingOrphan()
    {
        var store = new NamedSessionStore(_tempDir);
        var preset = MakePreset("Broken Repo");
        preset.RepositoryPath = Path.Combine(_tempDir, "no-such-repo");
        preset.AgentId = "agent-A";
        store.Save(preset);

        var statuses = store.LoadAllWithStatus(new[] { "agent-A" });

        var single = Assert.Single(statuses);
        Assert.False(single.IsLaunchable);
        Assert.Equal(NamedSessionOrphanReason.RepositoryMissing, single.OrphanReason);
    }

    [Fact]
    public void LoadAllWithStatus_RemovedAgent_IsAgentRemovedOrphan()
    {
        var store = new NamedSessionStore(_tempDir);
        var preset = MakePreset("Broken Agent");
        preset.AgentId = "gone-agent";
        store.Save(preset);

        var statuses = store.LoadAllWithStatus(new[] { "agent-A", "agent-B" });

        var single = Assert.Single(statuses);
        Assert.False(single.IsLaunchable);
        Assert.Equal(NamedSessionOrphanReason.AgentRemoved, single.OrphanReason);
    }

    [Fact]
    public void LoadAllWithStatus_MissingRepositoryAndAgent_ReportsRepositoryFirst()
    {
        var store = new NamedSessionStore(_tempDir);
        var preset = MakePreset("Doubly Broken");
        preset.RepositoryPath = Path.Combine(_tempDir, "no-such-repo");
        preset.AgentId = "gone-agent";
        store.Save(preset);

        var statuses = store.LoadAllWithStatus(Array.Empty<string>());

        var single = Assert.Single(statuses);
        Assert.Equal(NamedSessionOrphanReason.RepositoryMissing, single.OrphanReason);
    }

    [Fact]
    public void ResolveOrphanReason_EmptyRepositoryPath_IsRepositoryMissing()
    {
        var preset = new NamedSessionDefinition { Name = "x", RepositoryPath = "", AgentId = "a" };

        var reason = NamedSessionStore.ResolveOrphanReason(preset, new HashSet<string>(new[] { "a" }));

        Assert.Equal(NamedSessionOrphanReason.RepositoryMissing, reason);
    }

    private NamedSessionDefinition MakePreset(string name)
    {
        return new NamedSessionDefinition
        {
            Name = name,
            RepositoryPath = _existingRepo,
            AgentId = "agent-default",
            Model = "claude-opus-4",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
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
