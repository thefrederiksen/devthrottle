using CcDirector.Core.Tests.Git;
using System.Diagnostics;
using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// The Director's repo-state collector (issue #2118), against REAL git repositories rather than a stub -
/// the whole value of this feed is that it reports what git actually says, and a faked git proves nothing
/// about "is this branch merged".
///
/// The claims that matter, and why:
///  - MERGED VERSUS UNMERGED must be right in both directions. A false "merged" recommends deleting the
///    owner's only copy of unmerged work; a false "unmerged" turns the email into noise.
///  - AN UNDETECTABLE DEFAULT BRANCH RECORDS NULL. There is nothing to be merged INTO, so every merged-ness
///    is null - not false, which would read as "definitely not merged" about branches nobody inspected.
///  - THE PAYLOAD CARRIES NO CONTENT. Asserted on the SERIALIZED snapshot of a repository whose files and
///    commit messages contain a distinctive marker: if any of it ever reaches the wire, the marker shows up.
/// </summary>
[Trait("Gate", "Quarantine")]
[Collection("ProcessWideState")]
public sealed class RepoStateSnapshotCollectorTests : IClassFixture<RepoStateSnapshotCollectorTests.Template>, IDisposable
{
    /// <summary>A string that appears ONLY in file contents and commit messages, never in a name or path.
    /// Its presence anywhere in the serialized payload is proof that content leaked.</summary>
    private const string SecretMarker = "TOP-SECRET-CONTENT-MARKER-9f3a";

    private readonly string _root;
    private readonly string _origin;
    private readonly string _repo;

    /// <summary>The bare origin and its clone, built once for the class and copied per test.</summary>
    public sealed class Template : GitRepositoryTemplate
    {
        public Template() : base("ccd-repostate-") { }

        protected override IReadOnlyList<string> Clones => new[] { "repo" };

        protected override void Build(string root)
        {
            var origin = Path.Combine(root, "origin.git");
            var repo = Path.Combine(root, "repo");
            RunGit(root, "-c", "init.defaultBranch=main", "init", "--bare", origin);
            RunGit(root, "-c", "init.defaultBranch=main", "clone", origin, repo);
            RunGit(repo, "config", "user.email", "test@cc-director.local");
            RunGit(repo, "config", "user.name", "CC Director Test");
            RunGit(repo, "config", "commit.gpgsign", "false");
            File.WriteAllText(Path.Combine(repo, "README.md"), $"init {SecretMarker}\n");
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", $"initial commit mentioning {SecretMarker}");
            RunGit(repo, "branch", "-M", "main");
            RunGit(repo, "push", "-u", "origin", "main");
        }
    }

    public RepoStateSnapshotCollectorTests(Template template)
    {
        _root = template.CopyTo();
        _origin = Path.Combine(_root, "origin.git");
        _repo = Path.Combine(_root, "repo");
    }

    public void Dispose()
    {
        // The copy is this test's own; a delete that fails is reported, not retried and swallowed - the
        // swallowed version leaked a folder per test for months, because git's objects are read-only.
        TestTempRoot.DeleteTree(_root);
    }

    private RepositoryConfig Repo(string? path = null) =>
        new() { Name = "repo-under-test", Path = path ?? _repo };

    [Fact]
    public async Task A_repository_that_cannot_be_collected_is_OMITTED_never_pushed_empty()
    {
        // A path that is not a git repository at all. An empty snapshot for it would reach the Gateway as
        // "no branches, no worktrees" - a clean bill of health invented out of a failure - so it must not
        // appear in the batch, while the healthy repository beside it still does.
        var notARepo = Path.Combine(_root, "not-a-repo");
        Directory.CreateDirectory(notARepo);

        var snapshots = await new RepoStateSnapshotCollector()
            .CollectAsync(new[] { Repo(), Repo(notARepo) });

        Assert.Single(snapshots);
        Assert.Equal(_repo, snapshots[0].Path);
    }

    [Fact]
    public async Task A_registered_folder_that_is_not_a_git_repository_is_skipped_and_reports_no_error()
    {
        // Issue #3669: sessions are started in plain folders too, and those folders are registered. Such a
        // folder is a normal case, not a failure - it must be left out WITHOUT an error line, because every
        // error line is sent to the Gateway as an error report, on every push cycle.
        var plainFolder = Path.Combine(_root, "plain-folder");
        Directory.CreateDirectory(plainFolder);
        var errors = new List<string>();
        var previous = FileLog.ErrorObserver;
        using var scope = FileLog.RedirectForTests();
        try
        {
            FileLog.ErrorObserver = errors.Add;

            var snapshots = await new RepoStateSnapshotCollector()
                .CollectAsync(new[] { Repo(), Repo(plainFolder) });

            Assert.Single(snapshots);
            Assert.Equal(_repo, snapshots[0].Path);
            Assert.Empty(errors);
        }
        finally
        {
            FileLog.ErrorObserver = previous;
        }
    }

    [Fact]
    public async Task A_folder_with_a_broken_git_link_still_fails_loudly_and_is_omitted()
    {
        // The skip for plain folders (#3669) must not swallow a real failure: a folder that claims to be a
        // repository (a .git file) and cannot be read by git is still an error, and still left out of the batch.
        var broken = Path.Combine(_root, "broken-link");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, ".git"), "gitdir: " + Path.Combine(_root, "no-such-git-dir"));
        var errors = new List<string>();
        var previous = FileLog.ErrorObserver;
        using var scope = FileLog.RedirectForTests();
        try
        {
            FileLog.ErrorObserver = errors.Add;

            var snapshots = await new RepoStateSnapshotCollector()
                .CollectAsync(new[] { Repo(), Repo(broken) });

            Assert.Single(snapshots);
            Assert.Equal(_repo, snapshots[0].Path);
            Assert.Contains(errors, e => e.Contains("CollectOne FAILED", StringComparison.Ordinal));
        }
        finally
        {
            FileLog.ErrorObserver = previous;
        }
    }

    [Fact]
    public async Task A_registered_folder_inside_a_repository_is_still_collected()
    {
        // git answers for a folder inside a repository's working tree, so the check for "is this a
        // repository" must look up the parents as git does, not only at the folder itself.
        var inner = Path.Combine(_repo, "inner");
        Directory.CreateDirectory(inner);

        var snapshot = Assert.Single(await new RepoStateSnapshotCollector().CollectAsync(new[] { Repo(inner) }));

        Assert.Equal(inner, snapshot.Path);
    }

    [Fact]
    public async Task A_registered_path_that_no_longer_exists_is_skipped_without_failing_the_batch()
    {
        var snapshots = await new RepoStateSnapshotCollector()
            .CollectAsync(new[] { Repo(Path.Combine(_root, "gone")), Repo() });

        Assert.Single(snapshots);
        Assert.Equal(_repo, snapshots[0].Path);
    }

    private static string RunGit(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({p.ExitCode}): {stderr}");
        return stdout;
    }
}
