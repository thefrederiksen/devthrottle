using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

/// <summary>
/// Refreshes the remotes a repository's branches are configured to track, so a verdict read from a
/// remote-tracking ref is as current as the caller means it to be.
///
/// <see cref="ConfiguredUpstreamProbe"/> answers "is the upstream gone?" from the local
/// remote-tracking ref of whatever remote the branch is configured to. The callers that must be
/// current before they trust that answer - the reaper, and an explicit refresh - fetch origin with
/// prune first. Origin is not the only remote a branch can track, and a remote that was never
/// fetched, or was pruned and then had the branch re-created on it, holds no tracking ref while the
/// branch exists: read as "gone", that would be a false proof of merge on a destructive path (the
/// review of pull request 3308). So those callers refresh every OTHER configured remote too, and a
/// remote that could not be refreshed is reported back so the branches on it fail closed.
/// </summary>
public static class ConfiguredRemoteRefresh
{
    /// <summary>Which remotes were pruned and which could not be reached.</summary>
    public sealed record Outcome(IReadOnlyList<string> Refreshed, IReadOnlyList<string> Failed)
    {
        public static readonly Outcome Nothing = new(Array.Empty<string>(), Array.Empty<string>());
    }

    /// <summary>The distinct remotes any local branch is configured to track (branch.*.remote).</summary>
    public static async Task<IReadOnlyList<string>> ConfiguredRemotesAsync(GitCommandRunner git, string repositoryPath, CancellationToken ct)
    {
        // Exit code 1 with no output means no branch has a remote configured - not an error.
        var result = await git.RunAsync(repositoryPath, new[] { "config", "--get-regexp", @"^branch\..*\.remote$" }, ct);
        if (!result.Success)
            return Array.Empty<string>();

        return result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && parts[1].Length > 0)
            .Select(parts => parts[1])
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// <c>git fetch --prune</c> every configured remote except <paramref name="alreadyRefreshed"/>,
    /// which the caller fetched itself. Each remote is tried on its own; one that fails is named in
    /// <see cref="Outcome.Failed"/> and does not stop the others.
    /// </summary>
    public static async Task<Outcome> PruneOtherRemotesAsync(GitCommandRunner git, string repositoryPath, string alreadyRefreshed, CancellationToken ct)
    {
        var remotes = (await ConfiguredRemotesAsync(git, repositoryPath, ct))
            .Where(r => !string.Equals(r, alreadyRefreshed, StringComparison.Ordinal))
            .ToList();
        if (remotes.Count == 0)
            return Outcome.Nothing;

        var refreshed = new List<string>();
        var failed = new List<string>();
        foreach (var remote in remotes)
        {
            var fetch = await git.RunAsync(repositoryPath, new[] { "fetch", "--prune", remote }, ct);
            if (fetch.Success)
            {
                refreshed.Add(remote);
            }
            else
            {
                failed.Add(remote);
                FileLog.Write($"[ConfiguredRemoteRefresh] fetch --prune {remote} FAILED in {repositoryPath}: {fetch.Error.Trim()} - branches tracking it cannot be inspected");
            }
        }
        FileLog.Write($"[ConfiguredRemoteRefresh] {repositoryPath}: refreshed {refreshed.Count} other remote(s), {failed.Count} failed");
        return new Outcome(refreshed, failed);
    }
}
