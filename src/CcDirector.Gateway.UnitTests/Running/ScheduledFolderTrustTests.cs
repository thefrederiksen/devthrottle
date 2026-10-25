using System.Text.Json.Nodes;
using CcDirector.ControlApi;
using CcDirector.Core.Claude;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Gateway.Tests.Running;

/// <summary>
/// A SCHEDULED CLAUDE CODE SESSION NEVER WAITS ON THE FOLDER-TRUST QUESTION (issue #3469). A folder that is one of the
/// machine's registered repositories is marked trusted before the agent starts; a folder the owner never trusted and
/// never registered is refused at once, in words; a folder already trusted is left as it is. Each case runs against a
/// Claude Code configuration file of its own in a scratch folder - never the user's.
/// </summary>
public sealed class ScheduledFolderTrustTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("scheduled-folder-trust-").FullName;
    private readonly string _config;
    private readonly string _repo;

    public ScheduledFolderTrustTests()
    {
        _config = Path.Combine(_root, ".claude.json");
        File.WriteAllText(_config, "{}");
        _repo = Directory.CreateDirectory(Path.Combine(_root, "repos", "site")).FullName;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static IReadOnlyList<RepositoryConfig> Registered(params string[] paths) =>
        paths.Select(p => new RepositoryConfig { Name = Path.GetFileName(p), Path = p }).ToList();

    private bool TrustedInTheFile(string folder) =>
        JsonNode.Parse(File.ReadAllText(_config))?["projects"]?[ClaudeFolderTrust.KeyFor(folder)]?["hasTrustDialogAccepted"]?.GetValue<bool>() == true;

    [Fact]
    public void ARegisteredRepository_IsMarkedTrustedBeforeTheAgentStarts()
    {
        var trust = new ClaudeFolderTrust(_config);

        Assert.Null(SessionCommandExecutor.ScheduledFolderTrustRefusal(_repo, Registered(_repo), trust, "devlinux"));

        Assert.True(TrustedInTheFile(_repo));
        Assert.True(trust.Covers(_repo));
    }

    [Fact]
    public void AFolderNeitherTrustedNorRegistered_IsRefusedAtOnce_InWords()
    {
        var trust = new ClaudeFolderTrust(_config);
        var elsewhere = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;

        var refusal = SessionCommandExecutor.ScheduledFolderTrustRefusal(elsewhere, Registered(_repo), trust, "devlinux");

        Assert.NotNull(refusal);
        Assert.StartsWith($"not started: the folder '{elsewhere}' is not one of the repositories of devlinux", refusal);
        Assert.Contains("open it (or the repository it is in) once", refusal);
        Assert.Contains("would wait at the folder-trust question with nobody to answer it", refusal);
        Assert.False(TrustedInTheFile(elsewhere));
    }

    [Fact]
    public void AFolderAlreadyTrusted_IsLeftAsItIs_EvenWhenNotRegistered()
    {
        var trust = new ClaudeFolderTrust(_config);
        trust.Trust(_repo);
        var before = File.ReadAllText(_config);

        Assert.Null(SessionCommandExecutor.ScheduledFolderTrustRefusal(_repo, Registered(), trust, "devlinux"));
        Assert.Equal(before, File.ReadAllText(_config));
    }

    [Fact]
    public void ADirectorThatMarksNoFolders_DecidesNothing()
    {
        Assert.Null(SessionCommandExecutor.ScheduledFolderTrustRefusal(_repo, Registered(), trust: null, "devlinux"));
    }
}
