using CcDirector.Core.Git;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Rules;

/// <summary>
/// A session's repo identity, or why it could not be read. Exactly one of the two is set. An EMPTY
/// <see cref="Repo"/> is a real answer - the checkout has no <c>origin</c> - and is not a failure.
/// </summary>
public sealed record RepoIdentityResolution(string? Repo, string? Failure);

/// <summary>
/// Session Rules part 2, phase 1: how a session's repository is recognised for its rules - by the
/// <c>origin</c> remote of the checkout it works in, reduced to one spelling by
/// <see cref="GitRemoteIdentity.Normalise"/> (decision D3, ruling P6). Every worktree of a repository has
/// the same remote, so every worktree gets that repository's rules without being listed.
///
/// Only this machine can answer it: the remote is written inside the folder.
/// </summary>
public static class SessionRepoIdentity
{
    /// <summary>git's exit code for <c>remote get-url</c> naming a remote that does not exist.</summary>
    private const int NoSuchRemote = 2;

    /// <summary>git's exit code when the folder is not inside a git repository at all.</summary>
    private const int NotARepository = 128;

    /// <summary>
    /// Read the folder's origin remote and normalise it. A checkout with no origin, and a folder that is
    /// not a repository, both answer the empty identity: only machine-wide and everywhere rules reach
    /// them, and the rules file says <c>"repo": ""</c>. Anything else - git not installed, a folder that
    /// is gone, an exit code git does not use for either of those - is a FAILURE, because answering the
    /// empty identity then would quietly drop every repo rule that should reach the session.
    /// </summary>
    public static async Task<RepoIdentityResolution> ResolveAsync(GitCommandRunner git, string workingDirectory, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(git);
        var result = await git.RunAsync(workingDirectory, new[] { "remote", "get-url", "origin" }, ct).ConfigureAwait(false);

        if (result.Success)
        {
            var repo = GitRemoteIdentity.Normalise(result.Output);
            FileLog.Write($"[SessionRepoIdentity] ResolveAsync: folder={workingDirectory}, repo={repo}");
            return new RepoIdentityResolution(repo, null);
        }

        if (result.ExitCode is NoSuchRemote or NotARepository)
        {
            FileLog.Write($"[SessionRepoIdentity] ResolveAsync: folder={workingDirectory} has no origin remote (git exit {result.ExitCode}) -> no repo identity");
            return new RepoIdentityResolution("", null);
        }

        var failure = $"git remote get-url origin exited {result.ExitCode}: {result.Error}";
        FileLog.Write($"[SessionRepoIdentity] ResolveAsync FAILED: folder={workingDirectory}, {failure}");
        return new RepoIdentityResolution(null, failure);
    }
}
