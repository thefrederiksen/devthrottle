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

    /// <summary>
    /// git's exit code for EVERY fatal error - "not a git repository", but also a worktree whose parent
    /// checkout is gone, a repository refused for its ownership, a corrupt <c>.git</c>. So 128 alone says
    /// nothing; see <see cref="IsPlainFolder"/>.
    /// </summary>
    private const int Fatal = 128;

    /// <summary>The words git uses, after "fatal: ", when no repository was found at all.</summary>
    private const string NotARepositoryText = "not a git repository";

    /// <summary>
    /// Read the folder's origin remote and normalise it. A checkout with no origin, and a PLAIN folder -
    /// git says it is not a repository AND nothing named <c>.git</c> exists in it or any parent - both
    /// answer the empty identity: only machine-wide and everywhere rules reach them, and the rules file
    /// says <c>"repo": ""</c>. Anything else - git not installed, a worktree whose parent is gone, a
    /// repository git refuses, any other exit code - is a FAILURE, because answering the empty identity
    /// then would quietly drop every repo rule that should reach the session.
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

        if (result.ExitCode == NoSuchRemote)
        {
            FileLog.Write($"[SessionRepoIdentity] ResolveAsync: folder={workingDirectory} has no origin remote -> no repo identity");
            return new RepoIdentityResolution("", null);
        }

        if (result.ExitCode == Fatal && IsPlainFolder(workingDirectory, result.Error))
        {
            FileLog.Write($"[SessionRepoIdentity] ResolveAsync: folder={workingDirectory} is not inside any repository -> no repo identity");
            return new RepoIdentityResolution("", null);
        }

        var failure = $"git remote get-url origin exited {result.ExitCode}: {result.Error}";
        FileLog.Write($"[SessionRepoIdentity] ResolveAsync FAILED: folder={workingDirectory}, {failure}");
        return new RepoIdentityResolution(null, failure);
    }

    /// <summary>
    /// True only when git said "not a git repository" AND there is no <c>.git</c> entry - folder or file
    /// - in the folder or any parent. A <c>.git</c> anywhere above means git found a repository it could
    /// not open (an orphaned worktree's <c>.git</c> file, a broken repository), which is a failure.
    /// </summary>
    private static bool IsPlainFolder(string workingDirectory, string error)
    {
        if (!error.Contains(NotARepositoryText, StringComparison.OrdinalIgnoreCase))
            return false;
        for (var folder = new DirectoryInfo(workingDirectory); folder is not null; folder = folder.Parent)
        {
            var entry = Path.Combine(folder.FullName, ".git");
            if (Directory.Exists(entry) || File.Exists(entry))
                return false;
        }
        return true;
    }
}
