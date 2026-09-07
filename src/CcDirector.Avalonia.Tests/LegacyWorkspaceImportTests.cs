using System.Text.Json;
using CcDirector.Avalonia;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Moving the workspaces this machine already had up to the Gateway (issue #2722).
///
/// The feature's fate is what these tests are actually about. Workspaces used to be files under this
/// Director's own configuration directory; they are now on the Gateway, and somebody's saved work must not
/// disappear in that move. Each test below is a specific way it could:
///
///  - importing and then losing the file (the archived bytes are COMPARED, not merely found);
///  - importing half a file and marking it done;
///  - overwriting a Gateway copy somebody has edited since;
///  - deleting an older archive to make room for a new one;
///  - two files whose names give the same id, where the second silently vanishes.
/// </summary>
public sealed class LegacyWorkspaceImportTests : IDisposable
{
    private readonly string _dir;

    public LegacyWorkspaceImportTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "LegacyWorkspaceImportTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        LegacyWorkspaceImport.ResetForTests();
    }

    public void Dispose()
    {
        LegacyWorkspaceImport.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>A catalog in memory, so the import can be watched without a Gateway.</summary>
    private sealed class FakeCatalog : IWorkspaceCatalog
    {
        public Dictionary<string, WorkspaceDocument> Stored { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int SaveCalls { get; private set; }

        public Task<IReadOnlyList<WorkspaceSummaryDto>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkspaceSummaryDto>>(
                Stored.Values.Select(d => new WorkspaceSummaryDto
                {
                    Id = d.Id, Name = d.Name, Origin = d.Origin, SeatCount = d.Seats.Count,
                }).ToList());

        public Task<WorkspaceDocument?> GetAsync(string id, CancellationToken ct = default)
            => Task.FromResult(Stored.TryGetValue(id, out var d) ? d : null);

        public Task<WorkspaceDocument> SaveAsync(WorkspaceDocument doc, CancellationToken ct = default)
        {
            SaveCalls++;
            Stored[doc.Id] = doc;
            return Task.FromResult(doc);
        }

        public Task<bool> DeleteAsync(string id, CancellationToken ct = default)
            => Task.FromResult(Stored.Remove(id));
    }

    private string WriteLegacy(string fileName, object body)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(body));
        return path;
    }

    private static object LegacyBody(string name, params (string? repo, string? custom, string? agent)[] sessions)
        => new
        {
            version = 1,
            name,
            description = "saved before the Gateway held these",
            sessions = sessions.Select((s, i) => new
            {
                repoPath = s.repo,
                customName = s.custom,
                customColor = "#FF8800",
                sortOrder = i,
                claudeArgs = "--model opus",
                agent = s.agent,
            }).ToArray(),
        };

    [Fact]
    public async Task Every_legacy_file_is_pushed_up_and_its_bytes_survive_under_the_new_name()
    {
        var a = WriteLegacy("morning.workspace.json",
            LegacyBody("Morning fleet", (@"D:\ReposFred\devthrottle", "Gateway work", "ClaudeCode")));
        var b = WriteLegacy("cube.workspace.json",
            LegacyBody("New Studio Cube", (@"D:\ReposMindzie\cube", null, "Codex")));

        var beforeA = await File.ReadAllBytesAsync(a);
        var beforeB = await File.ReadAllBytesAsync(b);

        var catalog = new FakeCatalog();
        var imported = await LegacyWorkspaceImport.RunOnceAsync(catalog, _dir);

        Assert.Equal(2, imported);
        Assert.True(catalog.Stored.ContainsKey("morning-fleet"));
        Assert.True(catalog.Stored.ContainsKey("new-studio-cube"));

        // Nothing is deleted, and the archived file is the SAME BYTES. Checking only that a file exists
        // at the new name would pass an implementation that wrote an empty one and deleted the source.
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
        Assert.Equal(beforeA, await File.ReadAllBytesAsync(a + LegacyWorkspaceImport.ImportedSuffix));
        Assert.Equal(beforeB, await File.ReadAllBytesAsync(b + LegacyWorkspaceImport.ImportedSuffix));
    }

    [Fact]
    public async Task An_imported_workspace_keeps_the_name_agent_colour_arguments_and_order_of_every_seat()
    {
        WriteLegacy("morning.workspace.json", new
        {
            version = 1,
            name = "Morning fleet",
            description = "saved before the Gateway held these",
            sessions = new object[]
            {
                // Deliberately out of order in the file, to prove the file's own sortOrder is what counts
                // and not the array position.
                new { repoPath = @"D:\ReposFred\devthrottle_internal", customName = (string?)null,
                      customColor = (string?)null, sortOrder = 1, claudeArgs = (string?)null,
                      agent = (string?)null },
                new { repoPath = @"D:\ReposFred\devthrottle", customName = (string?)"Gateway work",
                      customColor = (string?)"#FF8800", sortOrder = 0, claudeArgs = (string?)"--model opus",
                      agent = (string?)"Codex" },
            },
        });

        var catalog = new FakeCatalog();
        await LegacyWorkspaceImport.RunOnceAsync(catalog, _dir);

        var doc = catalog.Stored["morning-fleet"];
        Assert.Equal("Morning fleet", doc.Name);
        Assert.Equal(WorkspaceOrigins.Authored, doc.Origin);
        Assert.Equal(2, doc.Seats.Count);

        Assert.Equal("Gateway work", doc.Seats[0].Name);
        Assert.Equal("Codex", doc.Seats[0].Agent);
        Assert.Equal("#FF8800", doc.Seats[0].Color);
        Assert.Equal("--model opus", doc.Seats[0].AgentArgs);
        Assert.Equal(0, doc.Seats[0].SortOrder);

        // An entry with no name falls back to the repository folder, exactly as the old dialog displayed
        // it; a null agent means the file predates the field, and those genuinely were Claude Code.
        Assert.Equal("devthrottle_internal", doc.Seats[1].Name);
        Assert.Equal("ClaudeCode", doc.Seats[1].Agent);
        Assert.Equal(1, doc.Seats[1].SortOrder);
    }

    [Fact]
    public async Task A_workspace_already_on_the_Gateway_is_left_exactly_as_it_is()
    {
        var path = WriteLegacy("morning.workspace.json",
            LegacyBody("Morning fleet", (@"D:\old\path", "the old one", "ClaudeCode")));

        var catalog = new FakeCatalog();
        catalog.Stored["morning-fleet"] = new WorkspaceDocument
        {
            Id = "morning-fleet",
            Name = "Morning fleet",
            Origin = WorkspaceOrigins.Authored,
            Seats = { new WorkspaceSeat { Name = "the edited one", Agent = "ClaudeCode", RepoPath = @"D:\new\path" } },
        };

        var imported = await LegacyWorkspaceImport.RunOnceAsync(catalog, _dir);

        // The Gateway's copy is the one people have been editing since the move. Overwriting it with a
        // file from before the upgrade would lose exactly the work this import exists to protect.
        Assert.Equal(0, imported);
        Assert.Equal(0, catalog.SaveCalls);
        Assert.Equal("the edited one", catalog.Stored["morning-fleet"].Seats[0].Name);

        // It is still renamed aside - it has been dealt with, and leaving it would import it every time.
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + LegacyWorkspaceImport.ImportedSuffix));
    }

    [Fact]
    public async Task An_existing_archive_is_never_deleted_to_make_room()
    {
        var path = WriteLegacy("morning.workspace.json",
            LegacyBody("Morning fleet", (@"D:\ReposFred\devthrottle", "Gateway work", "ClaudeCode")));

        // An earlier run already archived a DIFFERENT file under the name this one wants.
        var archive = path + LegacyWorkspaceImport.ImportedSuffix;
        await File.WriteAllTextAsync(archive, "the older saved workspace nobody must lose");

        var catalog = new FakeCatalog();
        Assert.Equal(1, await LegacyWorkspaceImport.RunOnceAsync(catalog, _dir));

        // The old archive is untouched and the new one went somewhere else.
        Assert.Equal("the older saved workspace nobody must lose", await File.ReadAllTextAsync(archive));
        Assert.True(File.Exists(archive + "-2"));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task It_runs_once_per_process()
    {
        WriteLegacy("morning.workspace.json",
            LegacyBody("Morning fleet", (@"D:\ReposFred\devthrottle", "Gateway work", "ClaudeCode")));

        var catalog = new FakeCatalog();
        Assert.Equal(1, await LegacyWorkspaceImport.RunOnceAsync(catalog, _dir));
        Assert.Equal(0, await LegacyWorkspaceImport.RunOnceAsync(catalog, _dir));
        Assert.Equal(1, catalog.SaveCalls);
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_is_left_alone_under_its_own_name()
    {
        var good = WriteLegacy("morning.workspace.json",
            LegacyBody("Morning fleet", (@"D:\ReposFred\devthrottle", "Gateway work", "ClaudeCode")));
        var bad = Path.Combine(_dir, "broken.workspace.json");
        await File.WriteAllTextAsync(bad, "{ this is not json");

        var catalog = new FakeCatalog();
        var imported = await LegacyWorkspaceImport.RunOnceAsync(catalog, _dir);

        // The good one still goes up; the unreadable one keeps its name so the bytes are findable, and
        // renaming it aside would say it had been dealt with when it has not.
        Assert.Equal(1, imported);
        Assert.True(File.Exists(good + LegacyWorkspaceImport.ImportedSuffix));
        Assert.True(File.Exists(bad));
        Assert.False(File.Exists(bad + LegacyWorkspaceImport.ImportedSuffix));
    }

    [Fact]
    public async Task A_file_with_an_entry_that_has_no_repository_is_refused_whole()
    {
        var partial = WriteLegacy("partial.workspace.json",
            LegacyBody("Partial fleet",
                (@"D:\ReposFred\devthrottle", "this one is fine", "ClaudeCode"),
                (null, "this one has nowhere to run", "ClaudeCode")));

        var catalog = new FakeCatalog();
        var imported = await LegacyWorkspaceImport.RunOnceAsync(catalog, _dir);

        // Importing the entry that parsed and renaming the file as done would silently drop the other.
        Assert.Equal(0, imported);
        Assert.Empty(catalog.Stored);
        Assert.True(File.Exists(partial));
        Assert.False(File.Exists(partial + LegacyWorkspaceImport.ImportedSuffix));
    }

    [Fact]
    public async Task A_file_with_no_sessions_is_refused_rather_than_imported_as_an_empty_workspace()
    {
        var empty = WriteLegacy("empty.workspace.json", LegacyBody("Empty fleet"));

        var catalog = new FakeCatalog();
        Assert.Equal(0, await LegacyWorkspaceImport.RunOnceAsync(catalog, _dir));
        Assert.Empty(catalog.Stored);
        Assert.True(File.Exists(empty));
    }

    [Fact]
    public async Task Two_files_whose_names_give_the_same_id_do_not_overwrite_each_other()
    {
        // "Morning fleet" and "morning FLEET" both slug to morning-fleet.
        var first = WriteLegacy("a-morning.workspace.json",
            LegacyBody("Morning fleet", (@"D:\first", "the first one", "ClaudeCode")));
        var second = WriteLegacy("b-morning.workspace.json",
            LegacyBody("morning FLEET", (@"D:\second", "the second one", "ClaudeCode")));

        var catalog = new FakeCatalog();
        var imported = await LegacyWorkspaceImport.RunOnceAsync(catalog, _dir);

        Assert.Equal(1, imported);
        Assert.Equal("the first one", catalog.Stored["morning-fleet"].Seats[0].Name);

        // The loser is LEFT IN PLACE. Renaming it aside would say it had been dealt with, and its
        // contents would be gone from anywhere anyone looks.
        Assert.False(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.False(File.Exists(second + LegacyWorkspaceImport.ImportedSuffix));
    }

    [Fact]
    public async Task A_folder_that_does_not_exist_imports_nothing_and_does_not_throw()
    {
        var catalog = new FakeCatalog();
        Assert.Equal(0, await LegacyWorkspaceImport.RunOnceAsync(
            catalog, Path.Combine(_dir, "no-such-folder")));
    }
}
