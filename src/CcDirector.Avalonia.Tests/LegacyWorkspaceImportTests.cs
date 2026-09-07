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
/// disappear in that move. So: every legacy file is pushed up ONCE, a file whose id is already on the
/// Gateway leaves the Gateway's copy alone, and nothing on disk is deleted - each file is renamed aside
/// with its bytes intact.
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

    private static object LegacyBody(string name, params (string repo, string? custom, string? agent)[] sessions)
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
    public async Task Every_legacy_file_is_pushed_up_and_renamed_aside()
    {
        var a = WriteLegacy("morning.workspace.json",
            LegacyBody("Morning fleet", (@"D:\ReposFred\devthrottle", "Gateway work", "ClaudeCode")));
        var b = WriteLegacy("cube.workspace.json",
            LegacyBody("New Studio Cube", (@"D:\ReposMindzie\cube", null, "Codex")));

        var catalog = new FakeCatalog();
        var imported = await LegacyWorkspaceImport.RunOnceAsync(catalog, _dir);

        Assert.Equal(2, imported);
        Assert.True(catalog.Stored.ContainsKey("morning-fleet"));
        Assert.True(catalog.Stored.ContainsKey("new-studio-cube"));

        // Nothing is deleted: the bytes are still on disk under the new name.
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
        Assert.True(File.Exists(a + LegacyWorkspaceImport.ImportedSuffix));
        Assert.True(File.Exists(b + LegacyWorkspaceImport.ImportedSuffix));
    }

    [Fact]
    public async Task An_imported_workspace_keeps_the_name_agent_colour_and_arguments_of_every_seat()
    {
        WriteLegacy("morning.workspace.json",
            LegacyBody("Morning fleet",
                (@"D:\ReposFred\devthrottle", "Gateway work", "Codex"),
                (@"D:\ReposFred\devthrottle_internal", null, null)));

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

        // An entry with no name falls back to the repository folder, exactly as the old dialog displayed
        // it; a null agent means the file predates the field, and those genuinely were Claude Code.
        Assert.Equal("devthrottle_internal", doc.Seats[1].Name);
        Assert.Equal("ClaudeCode", doc.Seats[1].Agent);
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
        File.WriteAllText(bad, "{ this is not json");

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
    public async Task A_folder_that_does_not_exist_imports_nothing_and_does_not_throw()
    {
        var catalog = new FakeCatalog();
        Assert.Equal(0, await LegacyWorkspaceImport.RunOnceAsync(
            catalog, Path.Combine(_dir, "no-such-folder")));
    }
}
