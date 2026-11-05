using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

/// <summary>
/// Answers the C2 question - "was this branch's upstream deleted on its remote?" - against
/// the branch's CONFIGURED upstream: <c>branch.&lt;name&gt;.remote</c> plus
/// <c>branch.&lt;name&gt;.merge</c>. Querying origin for the LOCAL branch name is wrong twice
/// over: the upstream ref can carry a different name than the local branch, and the remote can
/// be one other than origin. Either mismatch makes a live upstream look deleted, which would
/// rule real unmerged work "merged and safe to delete". If either config value is missing the
/// branch has no configured upstream and C2 does not apply at all.
///
/// The answer comes from the LOCAL remote-tracking ref, never from the network. It is the same
/// answer <c>git branch -vv</c> gives when it prints <c>[gone]</c>: the configured upstream is
/// mapped through the remote's fetch refspec to <c>refs/remotes/&lt;remote&gt;/&lt;name&gt;</c>,
/// and "gone" means that ref is absent. So the verdict is exactly as fresh as the last
/// <c>git fetch --prune</c> of that remote - which is what the callers that must be current
/// (the reaper, and an explicit refresh) run first, for origin and for every other remote a
/// branch is configured to (<see cref="ConfiguredRemoteRefresh"/>), and what the callers that
/// scan a whole machine deliberately do not. Until 22 September 2026 this probe ran <c>git ls-remote</c>
/// instead, one network round trip to the hosting provider per branch per inventory; on one
/// Director that was 51,864 calls in a day, none of them asked for by a person.
///
/// IT IS READ ONCE PER REPOSITORY, NOT ONCE PER BRANCH. Until 5 November 2026 it started three
/// git processes for every branch of every worktree on every inventory - which on this fleet's
/// busiest Mac was 8,658 of the 42,093 git commands the Director ran in sixteen hours, a fifth of
/// them, for an answer that changes only when somebody deletes a branch. The two reads below
/// answer every branch of a repository at once, and <see cref="ConfiguredUpstreamSnapshot.Verdict"/>
/// then starts no process at all. Nothing is cached between inventories: the snapshot lives for one
/// pass and is read again on the next, so it can never be stale (owner's decision, 5 November 2026).
/// </summary>
public static class ConfiguredUpstreamProbe
{
    /// <summary>
    /// <paramref name="HasConfiguredUpstream"/>: both branch.&lt;name&gt;.remote and
    /// branch.&lt;name&gt;.merge are set. <paramref name="UpstreamGone"/>: the remote-tracking ref
    /// for the configured upstream is absent locally (only meaningful when a configured upstream
    /// exists). <paramref name="InspectionSucceeded"/>: false when git could not answer - the
    /// caller must fail closed.
    /// </summary>
    public readonly record struct UpstreamVerdict(bool HasConfiguredUpstream, bool UpstreamGone, bool InspectionSucceeded);

    // One tab-separated line per branch: its own name, the resolved upstream ref (empty when git
    // cannot map the configured upstream to a remote-tracking ref), then the tracking state without
    // brackets, which is the word "gone" when the remote-tracking ref does not exist.
    private const string RefFormat = "%(refname:short)%09%(upstream)%09%(upstream:track,nobracket)";

    /// <summary>
    /// Reads one repository's whole branch-to-upstream picture: two git processes, whatever the
    /// number of branches. A read that fails returns a snapshot whose every verdict fails closed.
    /// </summary>
    public static async Task<ConfiguredUpstreamSnapshot> ReadAsync(
        GitCommandRunner git, string repoPath, CancellationToken ct = default)
    {
        var refs = await git.RunAsync(repoPath, new[] { "for-each-ref", $"--format={RefFormat}", "refs/heads/" }, ct);
        if (!refs.Success)
        {
            FileLog.Write($"[ConfiguredUpstreamProbe] for-each-ref FAILED for {repoPath}: {refs.Error}");
            return ConfiguredUpstreamSnapshot.CouldNotRead($"git for-each-ref failed: {refs.Error}");
        }

        // --get-regexp, not --get per branch: git permits MULTIPLE merge values (an octopus pull),
        // and this form prints every one of them, which is what keeps the ambiguous case refusable
        // rather than silently reduced to its last value (ruling R2-7).
        var config = await git.RunAsync(repoPath, new[] { "config", "--get-regexp", "^branch\\." }, ct);
        // Exit 1 is "no key matched" - a repository that configures no branch at all. That is an
        // answer, not a failure.
        if (!config.Success && config.ExitCode != 1)
        {
            FileLog.Write($"[ConfiguredUpstreamProbe] config --get-regexp FAILED for {repoPath}: {config.Error}");
            return ConfiguredUpstreamSnapshot.CouldNotRead($"git config --get-regexp failed: {config.Error}");
        }

        return ConfiguredUpstreamSnapshot.Parse(refs.Output, config.Success ? config.Output : "");
    }
}
