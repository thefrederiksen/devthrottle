using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

public class WorkspaceStoreTests : IDisposable
{
    private readonly string _tempDir;

    public WorkspaceStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"WorkspaceStoreTests_{Guid.NewGuid():N}");
    }

    [Fact]
    public void Save_ValidWorkspace_WritesJsonFile()
    {
        var store = new WorkspaceStore(_tempDir);
        var workspace = MakeWorkspace("My Project");

        var result = store.Save(workspace);

        Assert.True(result);
        var filePath = Path.Combine(_tempDir, "my-project.workspace.json");
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public void LoadAll_MultipleWorkspaces_ReturnsSorted()
    {
        var store = new WorkspaceStore(_tempDir);
        store.Save(MakeWorkspace("Zebra"));
        store.Save(MakeWorkspace("Alpha"));
        store.Save(MakeWorkspace("Mango"));

        var all = store.LoadAll();

        Assert.Equal(3, all.Count);
        Assert.Equal("Alpha", all[0].Name);
        Assert.Equal("Mango", all[1].Name);
        Assert.Equal("Zebra", all[2].Name);
    }

    [Fact]
    public void Load_NonExistent_ReturnsNull()
    {
        var store = new WorkspaceStore(_tempDir);

        var result = store.Load("does-not-exist");

        Assert.Null(result);
    }

    [Fact]
    public void Delete_ExistingWorkspace_RemovesFile()
    {
        var store = new WorkspaceStore(_tempDir);
        store.Save(MakeWorkspace("To Delete"));

        var deleted = store.Delete("to-delete");

        Assert.True(deleted);
        Assert.Null(store.Load("to-delete"));
    }

    [Fact]
    public void Delete_NonExistent_ReturnsFalse()
    {
        var store = new WorkspaceStore(_tempDir);

        var result = store.Delete("ghost");

        Assert.False(result);
    }

    [Theory]
    [InlineData("My Project", "my-project")]
    [InlineData("Hello   World", "hello-world")]
    [InlineData("  spaces  ", "spaces")]
    [InlineData("Special!@#Chars$%^", "specialchars")]
    [InlineData("Already-Slugged", "already-slugged")]
    [InlineData("UPPERCASE", "uppercase")]
    [InlineData("   ", "workspace")]
    [InlineData("", "workspace")]
    public void ToSlug_SpecialCharacters_ProducesValidFilename(string name, string expected)
    {
        var slug = WorkspaceStore.ToSlug(name);

        Assert.Equal(expected, slug);
    }

    [Fact]
    public void Save_OverwriteExisting_UpdatesFile()
    {
        var store = new WorkspaceStore(_tempDir);
        var original = MakeWorkspace("Overwrite Me");
        original.Description = "Original";
        store.Save(original);

        var updated = MakeWorkspace("Overwrite Me");
        updated.Description = "Updated";
        store.Save(updated);

        var loaded = store.Load("overwrite-me");
        Assert.NotNull(loaded);
        Assert.Equal("Updated", loaded.Description);
    }

    [Fact]
    public void Save_AndLoad_RoundTripsAllFields()
    {
        var store = new WorkspaceStore(_tempDir);
        var workspace = new WorkspaceDefinition
        {
            Version = 1,
            Name = "Full Test",
            Description = "A complete workspace",
            CreatedAt = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 3, 4, 15, 30, 0, TimeSpan.Zero),
            Sessions = new List<WorkspaceSessionEntry>
            {
                new()
                {
                    RepoPath = @"D:\Repos\project-a",
                    CustomName = "Frontend",
                    CustomColor = "#2563EB",
                    SortOrder = 0,
                    ClaudeArgs = "--allowedTools bash",
                    HandoverPath = @"C:\handovers\20260301_1000_frontend-handover.md"
                },
                new()
                {
                    RepoPath = @"D:\Repos\project-b",
                    SortOrder = 1
                }
            }
        };

        store.Save(workspace);
        var loaded = store.Load("full-test");

        Assert.NotNull(loaded);
        Assert.Equal(1, loaded.Version);
        Assert.Equal("Full Test", loaded.Name);
        Assert.Equal("A complete workspace", loaded.Description);
        Assert.Equal(2, loaded.Sessions.Count);
        Assert.Equal("Frontend", loaded.Sessions[0].CustomName);
        Assert.Equal("#2563EB", loaded.Sessions[0].CustomColor);
        Assert.Equal(0, loaded.Sessions[0].SortOrder);
        Assert.Equal("--allowedTools bash", loaded.Sessions[0].ClaudeArgs);
        Assert.Equal(@"C:\handovers\20260301_1000_frontend-handover.md", loaded.Sessions[0].HandoverPath);
        Assert.Null(loaded.Sessions[1].CustomName);
        Assert.Null(loaded.Sessions[1].HandoverPath);
    }

    [Fact]
    public void Exists_ExistingWorkspace_ReturnsTrue()
    {
        var store = new WorkspaceStore(_tempDir);
        store.Save(MakeWorkspace("Exists Test"));

        Assert.True(store.Exists("exists-test"));
    }

    [Fact]
    public void Exists_NonExistent_ReturnsFalse()
    {
        var store = new WorkspaceStore(_tempDir);

        Assert.False(store.Exists("nope"));
    }

    private static WorkspaceDefinition MakeWorkspace(string name)
    {
        return new WorkspaceDefinition
        {
            Name = name,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Sessions = new List<WorkspaceSessionEntry>
            {
                new() { RepoPath = @"D:\Repos\test-repo", SortOrder = 0 }
            }
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
