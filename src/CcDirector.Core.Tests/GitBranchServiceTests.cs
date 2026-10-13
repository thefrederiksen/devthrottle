using CcDirector.Core.Tests.Git;
using System.Diagnostics;
using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>The pure safe-delete verdict matrix - the same fail-closed shape as worktrees.</summary>
public class BranchSafetyEvaluatorTests
{
    [Fact]
    public void Current_NeverSafe_EvenWhenMerged()
    {
        var (safe, _) = BranchSafetyEvaluator.Evaluate(
            isCurrent: true, checkedOutInWorktree: false, inspectionSucceeded: true,
            pullRequestMerged: true, originBranchGone: true, containedInMain: true);
        Assert.False(safe);
    }

    [Fact]
    public void CheckedOutInWorktree_NeverSafe_EvenWhenMerged()
    {
        var (safe, why) = BranchSafetyEvaluator.Evaluate(
            isCurrent: false, checkedOutInWorktree: true, inspectionSucceeded: true,
            pullRequestMerged: true, originBranchGone: true, containedInMain: true);
        Assert.False(safe);
        Assert.Contains("worktree", why);
    }

    [Fact]
    public void InspectionFailed_NeverSafe()
    {
        var (safe, _) = BranchSafetyEvaluator.Evaluate(false, false, inspectionSucceeded: false, true, true, true);
        Assert.False(safe);
    }

    [Theory]
    [InlineData(true, false, false)]  // pull request merged
    [InlineData(false, false, true)]  // contained in main
    public void AMergeProofSignal_IsSufficientForBranchDeletion(bool pr, bool gone, bool contained)
    {
        var (safe, _) = BranchSafetyEvaluator.Evaluate(false, false, true, pr, gone, contained);
        Assert.True(safe);
    }

    // Policy split (issue 516, blocker: deleted-remote-branch as merge proof). A missing remote
    // branch does NOT prove the commits reached origin/main - a pull request can be closed without
    // merging and its head deleted, or the remote ref deleted directly. Deleting the LOCAL branch
    // makes those commits unreachable, so origin-branch-gone alone must NOT authorize branch
    // deletion; a positively confirmed merge (pull request merged or contained in origin/main) is
    // required. The counterpart for worktree removal is unchanged (the branch ref survives there),
    // and that side is locked by WorktreeSafetyEvaluatorTests.CleanBranch_WithOriginBranchGone_IsSafe.
    [Fact]
    public void OriginBranchGoneAlone_IsNotSufficientForBranchDeletion()
    {
        var (safe, why) = BranchSafetyEvaluator.Evaluate(
            isCurrent: false, checkedOutInWorktree: false, inspectionSucceeded: true,
            pullRequestMerged: false, originBranchGone: true, containedInMain: false);
        Assert.False(safe);
        Assert.Contains("not proven", why);
    }

    [Fact]
    public void NoSignal_NotSafe_FailClosed()
    {
        var (safe, why) = BranchSafetyEvaluator.Evaluate(false, false, true, false, false, false);
        Assert.False(safe);
        Assert.Contains("not proven", why);
    }
}

/// <summary>
/// Real-git integration: listing verdicts and the delete-time re-verify.
///
/// QUARANTINED (Reliable Tests, 11 October 2026): it runs real git processes, which gate tests may no longer do,
/// but it is the only real coverage of a sweep that DELETES branches, so it is kept and run on its own: test-
/// local.ps1 leaves it out of every gate run and runs it under -Quarantine, whose result never decides a release.
/// </summary>
[Trait("Gate", "Quarantine")]
public sealed class GitBranchServiceTests : IClassFixture<GitBranchServiceTests.Template>, IDisposable
{
    private readonly string _root;
    private readonly string _origin;
    private readonly string _repo;

    /// <summary>The bare origin and its clone, built once for the class and copied per test.</summary>
    public sealed class Template : GitRepositoryTemplate
    {
        public Template() : base("ccd-branch-") { }

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
            File.WriteAllText(Path.Combine(repo, "README.md"), "init\n");
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "initial");
            RunGit(repo, "branch", "-M", "main");
            RunGit(repo, "push", "-u", "origin", "main");
        }
    }

    public GitBranchServiceTests(Template template)
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

    /// <summary>A git runner that fails "git fetch --prune" and runs every other command for real.</summary>
    private sealed class FailFetchGitRunner : GitCommandRunner
    {
        public override async Task<GitCommandResult> RunAsync(
            string workingDirectory, string[] args, CancellationToken ct = default)
        {
            if (args.Length >= 2 && args[0] == "fetch" && args[1] == "--prune")
                return new GitCommandResult { Success = false, ExitCode = 1, Error = "injected fetch failure (network down)" };
            return await base.RunAsync(workingDirectory, args, ct);
        }
    }

    private sealed class ThrowingMessageException : Exception
    {
        public override string Message => throw new InvalidOperationException("toxic Message getter");
    }

    /// <summary>
    /// A git runner that runs the first command matching <c>match</c> TO COMPLETION - the
    /// mutation really happens - and then throws, exactly where a process-layer failure after
    /// the child exited would surface. The plain ThrowingGitRunner throws INSTEAD of running
    /// the command, which is the other direction of the same hazard.
    /// </summary>
    private sealed class MutateThenThrowGitRunner : GitCommandRunner
    {
        private readonly Func<string[], bool> _match;
        private readonly Func<Exception> _exception;
        private int _fired;

        public MutateThenThrowGitRunner(Func<string[], bool> match, Func<Exception> exception)
        {
            _match = match;
            _exception = exception;
        }

        public override async Task<GitCommandResult> RunAsync(
            string workingDirectory, string[] args, CancellationToken ct = default)
        {
            if (_match(args) && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await base.RunAsync(workingDirectory, args, CancellationToken.None);
                throw _exception();
            }
            return await base.RunAsync(workingDirectory, args, ct);
        }
    }

    /// <summary>
    /// A git runner that replaces the first command matching <c>match</c> with a canned
    /// result - the deterministic stand-in for a transient git failure with a specific exit
    /// code. Every other command runs for real.
    /// </summary>
    private sealed class CannedResultGitRunner : GitCommandRunner
    {
        private readonly Func<string[], bool> _match;
        private readonly GitCommandResult _canned;
        private int _fired;

        public CannedResultGitRunner(Func<string[], bool> match, GitCommandResult canned)
        {
            _match = match;
            _canned = canned;
        }

        public override async Task<GitCommandResult> RunAsync(
            string workingDirectory, string[] args, CancellationToken ct = default)
        {
            if (_match(args) && Interlocked.Exchange(ref _fired, 1) == 0)
                return _canned;
            return await base.RunAsync(workingDirectory, args, ct);
        }
    }

    /// <summary>
    /// A git runner that fires an interleaved action right after the first command matching
    /// <c>match</c> succeeds - the deterministic stand-in for "another process acted between
    /// two of our git commands".
    /// </summary>
    private sealed class InterleavingGitRunner : GitCommandRunner
    {
        private readonly Func<string[], bool> _match;
        private readonly Func<Task> _interleaved;
        private int _fired;

        public InterleavingGitRunner(Func<string[], bool> match, Func<Task> interleaved)
        {
            _match = match;
            _interleaved = interleaved;
        }

        public override async Task<GitCommandResult> RunAsync(
            string workingDirectory, string[] args, CancellationToken ct = default)
        {
            var result = await base.RunAsync(workingDirectory, args, ct);
            if (result.Success && _match(args) && Interlocked.Exchange(ref _fired, 1) == 0)
                await _interleaved();
            return result;
        }
    }

    /// <summary>
    /// A git runner that reproduces the exact cancellation window inside the production
    /// runner's process wait (ruling R4-6): for the first command matching <c>match</c>, the
    /// child git process runs TO COMPLETION first (the mutation happens), the interleaved
    /// cancellation then fires, and the wait's cancellation is surfaced exactly where
    /// <c>WaitForExitAsync(token)</c> would surface it - AFTER the mutation, BEFORE the
    /// successful result is returned. The plain InterleavingGitRunner cannot reach this
    /// window because it only acts after RunAsync already returned its result.
    /// </summary>
    private sealed class CancelDuringProcessWaitGitRunner : GitCommandRunner
    {
        private readonly Func<string[], bool> _match;
        private readonly Action _cancel;
        private int _fired;

        public CancelDuringProcessWaitGitRunner(Func<string[], bool> match, Action cancel)
        {
            _match = match;
            _cancel = cancel;
        }

        public override async Task<GitCommandResult> RunAsync(
            string workingDirectory, string[] args, CancellationToken ct = default)
        {
            if (_match(args) && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                // The child's mutation completes regardless of the caller's token.
                var result = await base.RunAsync(workingDirectory, args, CancellationToken.None);
                _cancel();
                // WaitForExitAsync(token) throws here when the caller's token was passed in -
                // concealing the completed mutation. A token of CancellationToken.None sails
                // through, which is exactly what ruling R4-1 requires of the delete command.
                ct.ThrowIfCancellationRequested();
                return result;
            }
            return await base.RunAsync(workingDirectory, args, ct);
        }
    }

    /// <summary>
    /// A git runner that throws from the first command matching <c>match</c> - the
    /// deterministic stand-in for a runtime failure (a process-start failure and the like)
    /// inside a compensation command.
    /// </summary>
    private sealed class ThrowingGitRunner : GitCommandRunner
    {
        private readonly Func<string[], bool> _match;
        private readonly Func<Exception> _exception;
        private int _fired;

        public ThrowingGitRunner(Func<string[], bool> match, Func<Exception> exception)
        {
            _match = match;
            _exception = exception;
        }

        public override async Task<GitCommandResult> RunAsync(
            string workingDirectory, string[] args, CancellationToken ct = default)
        {
            if (_match(args) && Interlocked.Exchange(ref _fired, 1) == 0)
                throw _exception();
            return await base.RunAsync(workingDirectory, args, ct);
        }
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
