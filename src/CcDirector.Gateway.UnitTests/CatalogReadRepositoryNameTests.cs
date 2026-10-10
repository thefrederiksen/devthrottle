using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// ONE REPOSITORY LIST MISSION: the repository list names a repository from the PATH, never from the machine
/// that happens to be reading it.
///
/// <see cref="CatalogReadExecutor"/> serves the recent-repository picker (<c>repos-list</c>) and the enriched
/// Repositories page (<c>repos-overview</c>). Where a registered repository has no stored display name, both
/// fall back to its folder name. That fallback read the name with <c>Path.GetFileName</c>, which honours only
/// the separator of the host it runs on: handed <c>D:\ReposFred\devthrottle_internal</c> on macOS or Linux it
/// finds no separator and answers with the whole path, so the list shows a path where a person is scanning
/// for a name. <see cref="Core.Utilities.RepositoryPaths.FolderName"/> reads the last segment from the path
/// itself and understands both separators, so every machine gives the one answer.
///
/// It is fixed HERE, ahead of the twelve other sites that make the same wrong call, because phase 3 of this
/// mission has the Gateway serve its ordered union on top of this list. A name defect underneath phase 3
/// would let phase 3's proof pass while showing a correctly ordered list of incorrectly named repositories.
///
/// WHAT THESE TESTS DO NOT COVER: they can only fail on macOS and Linux. On Windows
/// <c>Path.GetFileName</c> already reads both separators and understands a drive letter, so the old code was
/// right there and no test can make the defect appear on that platform.
///
/// The registry file lives in a folder of this test's own. The Director root is NOT moved: this class used to
/// point the process-wide CC_DIRECTOR_ROOT at that folder and delete the folder afterwards, under the
/// "DirectorRoot" collection name. That name kept it away from the eleven classes that share it, and from
/// nothing else - a collection serialises its members, not the rest of the assembly - so TeamMentorEndpointsTests
/// and TurnVerdictFeedbackRouteTests, which read the root and are not in it, failed with "SQLite Error 14: unable
/// to open database file" at paths inside the deleted folder. The overview lists the REGISTRY's repositories and
/// only enriches them from the root, and the registry is handed its file, so the assembly-wide throwaway root that
/// TestStorageRootRedirect sets is all it needs, and the collection attribute has no work left to do here.
/// </summary>
public sealed class CatalogReadRepositoryNameTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>A repository path as a Windows Director writes it, carried to a machine that is not Windows.</summary>
    private const string WindowsRepositoryPath = @"D:\ReposFred\devthrottle_internal";

    private readonly string _root;
    private readonly RepositoryRegistry _registry;

    public CatalogReadRepositoryNameTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-catalog-name-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        // The registry file is written by hand rather than through TryAdd, because TryAdd stores a name and
        // the fallback under test is what happens when the stored name is empty - which is what a registry
        // entry carried from another machine, or written by an older version, actually looks like on disk.
        var registryFile = Path.Combine(_root, "repositories.json");
        File.WriteAllText(registryFile,
            "[{\"Name\":\"\",\"Path\":\"" + WindowsRepositoryPath.Replace(@"\", @"\\") + "\",\"LastUsed\":null}]");
        _registry = new RepositoryRegistry(registryFile);
        _registry.Load();
    }

    public void Dispose()
    {
        // Nothing holds the folder - the registry reads its file and closes it - so a failure here is a defect to see.
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void ReposList_WindowsPathWithNoStoredName_ReadsTheFolderNameNotTheWholePath()
    {
        var result = CatalogReadExecutor.ReposList(_registry);

        Assert.Equal(DirectorCommandStatus.Ok, result.Status);
        var repos = JsonSerializer.Deserialize<List<RepositoryDto>>(result.BodyJson ?? "", Json);
        var repo = Assert.Single(repos!);
        Assert.Equal(WindowsRepositoryPath, repo.Path);
        Assert.Equal("devthrottle_internal", repo.Name);
    }

    [Fact]
    public void ReposOverview_WindowsPathWithNoStoredName_ReadsTheFolderNameNotTheWholePath()
    {
        using var sessions = new SessionManager(new AgentOptions());

        var result = CatalogReadExecutor.ReposOverview(sessions, _registry);

        Assert.Equal(DirectorCommandStatus.Ok, result.Status);
        var overview = JsonSerializer.Deserialize<List<RepoOverviewDto>>(result.BodyJson ?? "", Json);
        var repo = Assert.Single(overview!);
        Assert.Equal(WindowsRepositoryPath, repo.Path);
        Assert.Equal("devthrottle_internal", repo.Name);
    }

    [Fact]
    public void ReposList_KeepsAStoredNameRatherThanReadingTheFolder()
    {
        // The fallback is a fallback: a repository the owner renamed keeps the name he gave it.
        var named = Path.Combine(_root, "named.json");
        File.WriteAllText(named,
            "[{\"Name\":\"Internal notes\",\"Path\":\"" + WindowsRepositoryPath.Replace(@"\", @"\\") + "\",\"LastUsed\":null}]");
        var registry = new RepositoryRegistry(named);
        registry.Load();

        var result = CatalogReadExecutor.ReposList(registry);

        var repos = JsonSerializer.Deserialize<List<RepositoryDto>>(result.BodyJson ?? "", Json);
        Assert.Equal("Internal notes", Assert.Single(repos!).Name);
    }
}
