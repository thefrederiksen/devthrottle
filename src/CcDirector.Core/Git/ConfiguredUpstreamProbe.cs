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
/// (the reaper, and an explicit refresh) run first, and what the callers that scan a whole
/// machine deliberately do not. Until 22 September 2026 this probe ran <c>git ls-remote</c>
/// instead, one network round trip to the hosting provider per branch per inventory; on one
/// Director that was 51,864 calls in a day, none of them asked for by a person.
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

    // One tab-separated line: the resolved upstream ref (empty when git cannot map the configured
    // upstream to a remote-tracking ref), then the tracking state without brackets, which is the
    // word "gone" when the remote-tracking ref does not exist.
    private const string UpstreamFormat = "%(upstream)%09%(upstream:track,nobracket)";

    public static async Task<UpstreamVerdict> ProbeAsync(GitCommandRunner git, string repoPath, string branch, CancellationToken ct)
    {
        var remote = await git.RunAsync(repoPath, new[] { "config", "--get", $"branch.{branch}.remote" }, ct);
        // --get-all, not --get: git permits MULTIPLE merge values (an octopus pull), and --get
        // silently returns only the last one - which could be gone while another configured
        // merge ref survives, a false "upstream gone" on a destructive path (ruling R2-7).
        var merge = await git.RunAsync(repoPath, new[] { "config", "--get-all", $"branch.{branch}.merge" }, ct);
        var remoteName = remote.Success ? remote.Output.Trim() : "";
        var mergeRefs = merge.Success
            ? merge.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();

        if (remoteName.Length == 0 || mergeRefs.Length == 0)
            return new UpstreamVerdict(HasConfiguredUpstream: false, UpstreamGone: false, InspectionSucceeded: true);

        if (mergeRefs.Length > 1)
        {
            // Ambiguous configuration fails closed: the branch is simply NOT eligible for the
            // origin-gone signal, exactly as if no upstream were configured.
            FileLog.Write($"[ConfiguredUpstreamProbe] branch {branch} has {mergeRefs.Length} configured merge values - C2 does not apply");
            return new UpstreamVerdict(HasConfiguredUpstream: false, UpstreamGone: false, InspectionSucceeded: true);
        }

        // Ask git for the branch's own view of its upstream. This reads refs on disk only.
        var tracking = await git.RunAsync(repoPath, new[] { "for-each-ref", $"--format={UpstreamFormat}", $"refs/heads/{branch}" }, ct);
        if (!tracking.Success)
        {
            FileLog.Write($"[ConfiguredUpstreamProbe] for-each-ref FAILED for branch {branch}: {tracking.Error}");
            return new UpstreamVerdict(HasConfiguredUpstream: true, UpstreamGone: false, InspectionSucceeded: false);
        }

        var line = tracking.Output.Trim('\r', '\n', ' ');
        if (line.Length == 0)
        {
            // The branch has upstream configuration but no ref of its own - git prints nothing.
            // Nothing can be said about it, so say so rather than guess.
            FileLog.Write($"[ConfiguredUpstreamProbe] branch {branch} has an upstream configured but refs/heads/{branch} does not exist - cannot inspect");
            return new UpstreamVerdict(HasConfiguredUpstream: true, UpstreamGone: false, InspectionSucceeded: false);
        }

        var tab = line.IndexOf('\t');
        var upstreamRef = tab < 0 ? line : line[..tab];
        var track = tab < 0 ? "" : line[(tab + 1)..].Trim();

        if (upstreamRef.Length == 0)
        {
            // Configured, but git cannot map it to a remote-tracking ref (a remote with no fetch
            // refspec, or a merge value outside refs/heads). The ref cannot be read locally, so
            // the inspection fails closed - never "gone", which would be a false proof of merge.
            FileLog.Write($"[ConfiguredUpstreamProbe] branch {branch}: git cannot map {remoteName}/{mergeRefs[0]} to a remote-tracking ref - cannot inspect");
            return new UpstreamVerdict(HasConfiguredUpstream: true, UpstreamGone: false, InspectionSucceeded: false);
        }

        return new UpstreamVerdict(
            HasConfiguredUpstream: true,
            UpstreamGone: string.Equals(track, "gone", StringComparison.Ordinal),
            InspectionSucceeded: true);
    }
}
