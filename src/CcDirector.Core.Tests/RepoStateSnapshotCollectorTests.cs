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
public sealed class RepoStateSnapshotCollectorTests : IDisposable
{
    /// <summary>A string that appears ONLY in file contents and commit messages, never in a name or path.
    /// Its presence anywhere in the serialized payload is proof that content leaked.</summary>
    private const string SecretMarker = "TOP-SECRET-CONTENT-MARKER-9f3a";

    private readonly string _root;
    private readonly string _origin;
    private readonly string _repo;

    public RepoStateSnapshotCollectorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-repostate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _origin = Path.Combine(_root, "origin.git");
        _repo = Path.Combine(_root, "repo");
        RunGit(_root, "-c", "init.defaultBranch=main", "init", "--bare", _origin);
        RunGit(_root, "-c", "init.defaultBranch=main", "clone", _origin, _repo);
        RunGit(_repo, "config", "user.email", "test@cc-director.local");
        RunGit(_repo, "config", "user.name", "CC Director Test");
        RunGit(_repo, "config", "commit.gpgsign", "false");
        WriteFile("README.md", $"init {SecretMarker}\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", $"initial commit mentioning {SecretMarker}");
        RunGit(_repo, "branch", "-M", "main");
        RunGit(_repo, "push", "-u", "origin", "main");
    }

    public void Dispose()
    {
        for (var i = 0; i < 3; i++)
        {
            try { Directory.Delete(_root, recursive: true); return; }
            catch { Thread.Sleep(100); }
        }
    }

    private RepositoryConfig Repo(string? path = null) =>
        new() { Name = "repo-under-test", Path = path ?? _repo };

    [Fact]
    public async Task Merged_and_unmerged_branches_are_told_apart_in_both_directions()
    {
        // merged: pushed, then fast-forwarded into main - every commit is contained in the default branch.
        RunGit(_repo, "checkout", "-b", "merged-branch");
        WriteFile("m.txt", $"m {SecretMarker}\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", $"merged work {SecretMarker}");
        RunGit(_repo, "push", "-u", "origin", "merged-branch");
        RunGit(_repo, "checkout", "main");
        RunGit(_repo, "merge", "--ff-only", "merged-branch");
        RunGit(_repo, "push", "origin", "main");

        // unmerged: two commits main does not have.
        RunGit(_repo, "checkout", "-b", "unmerged-branch");
        WriteFile("u1.txt", "u1\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", "first");
        WriteFile("u2.txt", "u2\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", "second");
        RunGit(_repo, "checkout", "main");

        var snapshot = Assert.Single(await new RepoStateSnapshotCollector().CollectAsync(new[] { Repo() }));

        Assert.Equal("origin/main", snapshot.DefaultBranch);
        Assert.Equal("main", snapshot.CurrentBranch);

        var merged = Assert.Single(snapshot.Branches, b => b.Name == "merged-branch");
        Assert.True(merged.MergedIntoDefault);
        Assert.Equal(0, merged.CommitsAheadOfDefault);
        Assert.NotNull(merged.TipCommitUtc);

        var unmerged = Assert.Single(snapshot.Branches, b => b.Name == "unmerged-branch");
        Assert.False(unmerged.MergedIntoDefault);
        Assert.Equal(2, unmerged.CommitsAheadOfDefault);

        var main = Assert.Single(snapshot.Branches, b => b.Name == "main");
        Assert.True(main.CheckedOut);
    }

    [Fact]
    public async Task An_undetectable_default_branch_records_null_and_never_guesses()
    {
        // A repository with no origin at all: there is no origin/main and no origin/master, so there is
        // nothing to be merged into. Every merged-ness must be null - NOT false, which a downstream reader
        // would legitimately treat as "this branch definitely has unmerged work".
        var lonely = Path.Combine(_root, "lonely");
        RunGit(_root, "-c", "init.defaultBranch=main", "init", lonely);
        RunGit(lonely, "config", "user.email", "test@cc-director.local");
        RunGit(lonely, "config", "user.name", "CC Director Test");
        RunGit(lonely, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(lonely, "a.txt"), "a\n");
        RunGit(lonely, "add", "-A");
        RunGit(lonely, "commit", "-m", "only commit");

        var snapshot = Assert.Single(
            await new RepoStateSnapshotCollector().CollectAsync(new[] { Repo(lonely) }));

        Assert.Null(snapshot.DefaultBranch);
        Assert.NotEmpty(snapshot.Branches);
        Assert.All(snapshot.Branches, b => Assert.Null(b.MergedIntoDefault));
    }

    [Fact]
    public async Task Linked_worktrees_are_reported_and_the_primary_checkout_is_not_one_of_them()
    {
        RunGit(_repo, "branch", "wt-branch");
        var worktreePath = Path.Combine(_root, "wt-one");
        RunGit(_repo, "worktree", "add", worktreePath, "wt-branch");

        var snapshot = Assert.Single(await new RepoStateSnapshotCollector().CollectAsync(new[] { Repo() }));

        var worktree = Assert.Single(snapshot.Worktrees);
        Assert.Equal("wt-branch", worktree.Branch);
        Assert.Contains("wt-one", worktree.Path);
        // The primary checkout IS the repository; listing it as one of its own worktrees would make every
        // repository look like it had one more stale checkout than it has.
        Assert.DoesNotContain(snapshot.Worktrees, w => w.Path.TrimEnd('\\', '/') == _repo.TrimEnd('\\', '/'));
        // Its branch is unmerged (it carries no commits main lacks, so in fact it IS contained) - the point
        // here is only that the verdict is carried across from the one branch inventory, never recomputed.
        var branch = Assert.Single(snapshot.Branches, b => b.Name == "wt-branch");
        Assert.Equal(branch.MergedIntoDefault, worktree.BranchMergedIntoDefault);
    }

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

    [Fact]
    public async Task The_serialized_payload_carries_no_file_content_and_no_commit_message()
    {
        // The repository's files AND its commit messages contain the marker; a worktree and a second branch
        // widen the surface. If the collector ever grows a field that carries content, the marker appears.
        RunGit(_repo, "checkout", "-b", "another");
        WriteFile("secret.txt", $"{SecretMarker}\n");
        RunGit(_repo, "add", "-A");
        RunGit(_repo, "commit", "-m", $"a commit message containing {SecretMarker}");
        RunGit(_repo, "checkout", "main");
        RunGit(_repo, "worktree", "add", Path.Combine(_root, "wt-two"), "another");

        var snapshots = await new RepoStateSnapshotCollector().CollectAsync(new[] { Repo() });
        var json = JsonSerializer.Serialize(snapshots, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.DoesNotContain(SecretMarker, json, StringComparison.Ordinal);
        // And prove the assertion above can actually fail - a payload that carried the branch name would
        // contain "another", so the marker check is testing content specifically, not an empty payload.
        Assert.Contains("another", json, StringComparison.Ordinal);
    }

    private void WriteFile(string rel, string content)
        => File.WriteAllText(Path.Combine(_repo, rel), content);

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
