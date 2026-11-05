namespace CcDirector.Core.Git;

/// <summary>
/// One repository's branch-to-upstream picture, read in two git calls and then answered from memory
/// for every branch. <see cref="ConfiguredUpstreamProbe.ReadAsync"/> builds it;
/// <see cref="Verdict"/> is pure and starts no process.
///
/// It holds exactly what the per-branch questions used to ask one process at a time: the configured
/// remote and the configured merge value or values for each branch (from
/// <c>git config --get-regexp ^branch.</c>), and each branch's resolved upstream ref with git's own
/// tracking word (from one <c>git for-each-ref</c> over <c>refs/heads/</c>). A branch whose remote
/// branch has been deleted is the one whose tracking word is <c>gone</c> - which is also what
/// <c>git branch -vv</c> prints in brackets for it.
/// </summary>
public sealed class ConfiguredUpstreamSnapshot
{
    private static readonly string[] LineBreaks = { "\r\n", "\n", "\r" };

    private readonly Dictionary<string, (string Upstream, string Track)> _refs;
    private readonly Dictionary<string, string> _configuredRemote;
    private readonly Dictionary<string, List<string>> _configuredMerge;

    /// <summary>Null when the two reads succeeded; otherwise why they did not, and every verdict fails closed.</summary>
    public string? Unreadable { get; }

    private ConfiguredUpstreamSnapshot(
        Dictionary<string, (string Upstream, string Track)> refs,
        Dictionary<string, string> configuredRemote,
        Dictionary<string, List<string>> configuredMerge,
        string? unreadable)
    {
        _refs = refs;
        _configuredRemote = configuredRemote;
        _configuredMerge = configuredMerge;
        Unreadable = unreadable;
    }

    /// <summary>
    /// The snapshot for a repository git could not be read from. Every branch's inspection fails,
    /// which is what the callers treat as "cannot tell" - never as "no upstream configured", because
    /// that absence is what the merge proof reads as a reason a worktree may be deleted.
    /// </summary>
    public static ConfiguredUpstreamSnapshot CouldNotRead(string why) => new(
        new Dictionary<string, (string, string)>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, List<string>>(StringComparer.Ordinal),
        string.IsNullOrWhiteSpace(why) ? "git could not be read" : why);

    /// <summary>
    /// Builds the snapshot from the two command outputs.
    /// <paramref name="forEachRefOutput"/> is one line per branch:
    /// <c>name TAB upstream TAB track</c>. <paramref name="branchConfigOutput"/> is one line per
    /// configured value: <c>branch.&lt;name&gt;.&lt;key&gt; &lt;value&gt;</c>, and an empty string
    /// when the repository configures no branch at all.
    /// </summary>
    public static ConfiguredUpstreamSnapshot Parse(string forEachRefOutput, string branchConfigOutput)
    {
        var refs = new Dictionary<string, (string Upstream, string Track)>(StringComparer.Ordinal);
        foreach (var line in Lines(forEachRefOutput))
        {
            var parts = line.Split('\t');
            if (parts.Length < 1)
                continue;
            var name = parts[0].Trim();
            if (name.Length == 0)
                continue;
            var upstream = parts.Length > 1 ? parts[1].Trim() : "";
            var track = parts.Length > 2 ? parts[2].Trim() : "";
            refs[name] = (upstream, track);
        }

        var remotes = new Dictionary<string, string>(StringComparer.Ordinal);
        var merges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var line in Lines(branchConfigOutput))
        {
            // "branch.<name>.remote origin" - the key cannot contain a space and a git ref name
            // cannot either, so the first space is the whole separator.
            var space = line.IndexOf(' ');
            if (space <= 0)
                continue;
            var key = line[..space];
            var value = line[(space + 1)..].Trim();
            if (value.Length == 0 || !key.StartsWith("branch.", StringComparison.Ordinal))
                continue;

            if (key.EndsWith(".remote", StringComparison.Ordinal))
            {
                var name = key["branch.".Length..^".remote".Length];
                // Last value wins, which is what `git config --get` returned to the per-branch read.
                if (name.Length > 0)
                    remotes[name] = value;
            }
            else if (key.EndsWith(".merge", StringComparison.Ordinal))
            {
                var name = key["branch.".Length..^".merge".Length];
                if (name.Length == 0)
                    continue;
                if (!merges.TryGetValue(name, out var list))
                    merges[name] = list = new List<string>();
                list.Add(value);
            }
        }

        return new ConfiguredUpstreamSnapshot(refs, remotes, merges, unreadable: null);
    }

    /// <summary>
    /// The verdict for one branch. Starts no process.
    /// </summary>
    /// <param name="unrefreshedRemotes">Remotes the caller tried and failed to refresh. A branch
    /// configured to one of them cannot be inspected: its tracking ref may be stale in the direction
    /// that reads as "gone", which on a destructive path would be a false proof of merge.</param>
    public ConfiguredUpstreamProbe.UpstreamVerdict Verdict(
        string branch, IReadOnlyCollection<string>? unrefreshedRemotes = null)
    {
        if (Unreadable != null)
            return new ConfiguredUpstreamProbe.UpstreamVerdict(false, false, InspectionSucceeded: false);

        var hasRemote = _configuredRemote.TryGetValue(branch, out var remoteName) && remoteName!.Length > 0;
        var mergeRefs = _configuredMerge.TryGetValue(branch, out var list) ? list : null;

        if (!hasRemote || mergeRefs is null || mergeRefs.Count == 0)
            return new ConfiguredUpstreamProbe.UpstreamVerdict(HasConfiguredUpstream: false, UpstreamGone: false, InspectionSucceeded: true);

        if (mergeRefs.Count > 1)
        {
            // Ambiguous configuration fails closed: the branch is simply NOT eligible for the
            // origin-gone signal, exactly as if no upstream were configured (ruling R2-7). git
            // permits MULTIPLE merge values (an octopus pull), and one of them being gone proves
            // nothing while another survives.
            Utilities.FileLog.Write($"[ConfiguredUpstreamProbe] branch {branch} has {mergeRefs.Count} configured merge values - C2 does not apply");
            return new ConfiguredUpstreamProbe.UpstreamVerdict(HasConfiguredUpstream: false, UpstreamGone: false, InspectionSucceeded: true);
        }

        if (unrefreshedRemotes is not null && unrefreshedRemotes.Contains(remoteName!))
        {
            Utilities.FileLog.Write($"[ConfiguredUpstreamProbe] branch {branch} tracks {remoteName}, which could not be refreshed - cannot inspect");
            return new ConfiguredUpstreamProbe.UpstreamVerdict(HasConfiguredUpstream: true, UpstreamGone: false, InspectionSucceeded: false);
        }

        if (!_refs.TryGetValue(branch, out var tracking))
        {
            // The branch has upstream configuration but no ref of its own - git printed no line for
            // it. Nothing can be said about it, so say so rather than guess.
            Utilities.FileLog.Write($"[ConfiguredUpstreamProbe] branch {branch} has an upstream configured but refs/heads/{branch} does not exist - cannot inspect");
            return new ConfiguredUpstreamProbe.UpstreamVerdict(HasConfiguredUpstream: true, UpstreamGone: false, InspectionSucceeded: false);
        }

        if (tracking.Upstream.Length == 0)
        {
            // Configured, but git cannot map it to a remote-tracking ref (a remote with no fetch
            // refspec, or a merge value outside refs/heads). The ref cannot be read locally, so
            // the inspection fails closed - never "gone", which would be a false proof of merge.
            Utilities.FileLog.Write($"[ConfiguredUpstreamProbe] branch {branch}: git cannot map {remoteName}/{mergeRefs[0]} to a remote-tracking ref - cannot inspect");
            return new ConfiguredUpstreamProbe.UpstreamVerdict(HasConfiguredUpstream: true, UpstreamGone: false, InspectionSucceeded: false);
        }

        return new ConfiguredUpstreamProbe.UpstreamVerdict(
            HasConfiguredUpstream: true,
            UpstreamGone: string.Equals(tracking.Track, "gone", StringComparison.Ordinal),
            InspectionSucceeded: true);
    }

    private static IEnumerable<string> Lines(string output)
        => output.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries);
}
