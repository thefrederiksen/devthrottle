using System.Diagnostics;
using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests.GitMachine;

/// <summary>
/// The upstream read, against a REAL git. Two things are facts about git itself and can only be seen
/// by running it:
///
/// 1. One <c>for-each-ref</c> over <c>refs/heads/</c> still tells apart the three states the Director
///    decides on - a branch with no upstream, a branch whose remote branch is still there, and a
///    branch whose remote branch was deleted. The third is the one that matters: git prints the word
///    "gone" in the tracking field, which is what <c>git branch -vv</c> shows in brackets. If a
///    future git stopped printing it, a worktree holding unmerged work could be ruled safe to delete,
///    so this is asserted against the real tool rather than a canned string.
/// 2. The whole answer costs TWO git processes for a repository, whatever its number of branches.
///    Until 5 November 2026 it cost three per branch, which was a fifth of every git command one
///    Director ran in a day.
///
/// The parsing and every fail-closed rule are fixed in ConfiguredUpstreamSnapshotTests, in the gate,
/// where no process runs.
/// </summary>
public sealed class UpstreamIsReadOncePerRepositoryTests : IClassFixture<UpstreamIsReadOncePerRepositoryTests.RepoWithEveryUpstreamState>
{
    private readonly RepoWithEveryUpstreamState _repo;

    public UpstreamIsReadOncePerRepositoryTests(RepoWithEveryUpstreamState repo)
    {
        _repo = repo;
    }

    [Fact]
    public async Task ReadAsync_BranchWhoseRemoteBranchWasDeleted_IsGone()
    {
        var snapshot = await ConfiguredUpstreamProbe.ReadAsync(new GitCommandRunner(), _repo.Path);
        Assert.Null(snapshot.Unreadable);

        var gone = snapshot.Verdict("gone");
        Assert.True(gone.InspectionSucceeded);
        Assert.True(gone.HasConfiguredUpstream);
        Assert.True(gone.UpstreamGone);
    }

    [Fact]
    public async Task ReadAsync_BranchWhoseRemoteBranchIsStillThere_IsNotGone()
    {
        var snapshot = await ConfiguredUpstreamProbe.ReadAsync(new GitCommandRunner(), _repo.Path);

        var keep = snapshot.Verdict("keep");
        Assert.True(keep.InspectionSucceeded);
        Assert.True(keep.HasConfiguredUpstream);
        Assert.False(keep.UpstreamGone);
    }

    [Fact]
    public async Task ReadAsync_BranchThatWasNeverPushed_HasNoConfiguredUpstream()
    {
        var snapshot = await ConfiguredUpstreamProbe.ReadAsync(new GitCommandRunner(), _repo.Path);

        var never = snapshot.Verdict("never");
        Assert.True(never.InspectionSucceeded);
        Assert.False(never.HasConfiguredUpstream);
        Assert.False(never.UpstreamGone);
    }

    [Fact]
    public async Task ReadAsync_BranchWithTwoConfiguredMergeValues_IsRuledOutRatherThanJudged()
    {
        var snapshot = await ConfiguredUpstreamProbe.ReadAsync(new GitCommandRunner(), _repo.Path);

        var octopus = snapshot.Verdict("octopus");
        Assert.True(octopus.InspectionSucceeded);
        Assert.False(octopus.HasConfiguredUpstream);
        Assert.False(octopus.UpstreamGone);
    }

    [Fact]
    public async Task ReadAsync_WhateverTheBranchCount_StartsTwoGitProcesses()
    {
        var counting = new CountingGit();
        var snapshot = await ConfiguredUpstreamProbe.ReadAsync(counting, _repo.Path);
        Assert.Null(snapshot.Unreadable);

        // Four branches and one of every state, answered by two processes. Reading every verdict
        // afterwards starts none at all.
        foreach (var branch in new[] { "gone", "keep", "never", "octopus", "main" })
            _ = snapshot.Verdict(branch);

        Assert.Equal(2, counting.Commands.Count);
        Assert.Contains(counting.Commands, c => c.StartsWith("for-each-ref", StringComparison.Ordinal));
        Assert.Contains(counting.Commands, c => c.StartsWith("config --get-regexp", StringComparison.Ordinal));
        Assert.DoesNotContain(counting.Commands, c => c.Contains("--get-all", StringComparison.Ordinal));
    }

    private sealed class CountingGit : GitCommandRunner
    {
        public List<string> Commands { get; } = new();

        public override Task<GitCommandResult> RunAsync(string workingDirectory, string[] args, CancellationToken ct = default)
        {
            Commands.Add(string.Join(' ', args));
            return base.RunAsync(workingDirectory, args, ct);
        }
    }

    /// <summary>
    /// A clone holding one branch of every state the verdict distinguishes: a live upstream, an
    /// upstream whose remote branch was deleted, a branch never pushed, and a branch configured with
    /// two merge values. Built once for the class - a git process costs real time on a loaded machine.
    /// </summary>
    public sealed class RepoWithEveryUpstreamState : IDisposable
    {
        private readonly string _root;

        public string Path { get; }

        public RepoWithEveryUpstreamState()
        {
            _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ccd-upstream-" + Guid.NewGuid().ToString("N"));
            var remote = System.IO.Path.Combine(_root, "remote");
            Path = System.IO.Path.Combine(_root, "clone");
            Directory.CreateDirectory(remote);
            Directory.CreateDirectory(Path);

            Git(remote, "init", "-q", "--bare", ".");
            Git(_root, "clone", "-q", remote, "clone");
            Git(Path, "config", "user.email", "test@example.com");
            Git(Path, "config", "user.name", "test");
            File.WriteAllText(System.IO.Path.Combine(Path, "a.txt"), "one\n");
            Git(Path, "add", "a.txt");
            Git(Path, "commit", "-q", "-m", "one");
            // The machine's init.defaultBranch decides what the first branch is called, so name it
            // here: the test asserts on branch names and must not read the machine's preference.
            Git(Path, "branch", "-M", "main");
            Git(Path, "push", "-q", "origin", "HEAD:refs/heads/main");
            Git(Path, "branch", "--set-upstream-to=origin/main", "main");

            Git(Path, "branch", "-q", "keep");
            Git(Path, "push", "-q", "origin", "keep:refs/heads/keep");
            Git(Path, "branch", "--set-upstream-to=origin/keep", "keep");

            Git(Path, "branch", "-q", "gone");
            Git(Path, "push", "-q", "origin", "gone:refs/heads/gone");
            Git(Path, "branch", "--set-upstream-to=origin/gone", "gone");
            Git(Path, "push", "-q", "origin", "--delete", "gone");
            Git(Path, "fetch", "-q", "--prune", "origin");

            // Never pushed, so no upstream configuration of any kind.
            Git(Path, "branch", "-q", "never");

            // Two configured merge values - the ambiguous case the verdict refuses to judge.
            Git(Path, "branch", "-q", "octopus");
            Git(Path, "config", "branch.octopus.remote", "origin");
            Git(Path, "config", "branch.octopus.merge", "refs/heads/one");
            Git(Path, "config", "--add", "branch.octopus.merge", "refs/heads/two");
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }

        private static void Git(string workingDirectory, params string[] args)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            // A machine whose git is configured to sign or to template would answer differently.
            psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            _ = stdout.Result;
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"git {string.Join(' ', args)} exited {proc.ExitCode}: {stderr}");
        }
    }
}
