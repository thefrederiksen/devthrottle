namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One repository on one machine, as pushed by its Director and served fleet-wide by the Gateway
/// (GET /repositories). The verdict strings are folded on the Director side - clients and agents
/// render them verbatim (the dumb-client rule). Read-only at the Gateway: any destructive action
/// runs on the owning Director after a live re-verify (the trust rule).
/// </summary>
public class RepoStatusDto
{
    public string DirectorId { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string? RemoteUrl { get; set; }

    /// <summary>"GitHub", "AzureDevOps", "Other", or "None".</summary>
    public string Provider { get; set; } = "";
    public string? Org { get; set; }

    public string Branch { get; set; } = "";
    public bool IsClean { get; set; }
    public int UncommittedCount { get; set; }
    public DateTime? DirtySinceUtc { get; set; }
    public int AheadCount { get; set; }
    public int BehindCount { get; set; }
    public int BehindMainCount { get; set; }

    public int WorktreeCount { get; set; }
    public int WorktreesSafeToReap { get; set; }
    public int WorktreesInUse { get; set; }
    public int WorktreesNeedAttention { get; set; }
    public long WorktreeBytes { get; set; }

    /// <summary>True when the pushing Director had not yet re-verified this entry (warm-start cache).</summary>
    public bool Provisional { get; set; }

    /// <summary>
    /// IDENTITY ONLY - this row carries a repository's path and name and NOTHING ELSE (the
    /// one-repository-list mission, "the registry reaches the Gateway").
    ///
    /// The Director knows this repository exists because it is in the machine's registered repository
    /// list, which a person built by hand. It is not under any registered root folder, so the
    /// root-folder scan never reached it and nothing has computed a status for it. <c>Path</c>,
    /// <c>Name</c>, <c>MachineName</c> and <c>DirectorId</c> are real; every other field on this row is
    /// a default and describes nothing. A consumer that reads <c>Branch</c>, <c>IsClean</c>,
    /// <c>UncommittedCount</c>, the worktree counts or the ahead/behind counts off such a row is
    /// reading a fabricated fact, so <c>DirectorHub.PushRepoSnapshot</c> keeps these rows away from the
    /// two consumers that exist to report status and hands them only to the catalog, which wants
    /// identity and nothing more.
    ///
    /// It is NOT <see cref="Provisional"/>, and the difference matters. Provisional means "this
    /// Director has a status for this repository but has not re-verified it yet", and it resolves
    /// itself the moment the scan runs. This means "no status was ever computed, and none will be
    /// until this repository comes under a watched folder". A provisional row is unverified status; an
    /// identity-only row is verified identity.
    ///
    /// An older Director never sets it, and false is exactly right for those: everything an older
    /// Director pushes came from the scan and carries a status.
    /// </summary>
    public bool StatusNotComputed { get; set; }

    public List<WorktreeDto> Worktrees { get; set; } = new();

    /// <summary>
    /// WHAT CURRENTLY EXISTS UNDER THIS DIRECTOR'S REGISTERED ROOT FOLDERS, as a plain directory
    /// listing (the one-repository-list mission, "the catalogue forgets"). Null on a push from a
    /// Director that predates this, and null on every row of a push except the first - see below.
    ///
    /// IT IS A PUSH-LEVEL FACT RIDING ON A ROW, AND THAT IS DELIBERATE. The Gateway catalogue can only
    /// forget a repository when the Director that covers its folder positively says the folder is
    /// gone, and the rest of this push cannot say that: the root-folder scan reports only a direct
    /// child whose <c>.git</c> is a DIRECTORY, so a git WORKTREE - whose <c>.git</c> is a file - has
    /// never appeared in a push at all. Measured on one machine on 20 September 2026: of the fourteen
    /// repositories in that machine's catalogue that still existed, ELEVEN were worktrees the scan
    /// cannot see. Treating "absent from the push" as "gone" would have deleted all eleven.
    ///
    /// So the Director answers the question the scan cannot: for each registered root folder it could
    /// positively LIST, every direct child folder that exists right now, whatever is or is not inside
    /// it. A root it could not list - unmounted, unreadable, or no longer registered - is left out
    /// entirely, and nothing under it is ever forgotten.
    ///
    /// It rides on ONE row rather than on every row because <c>DirectorHub.PushRepoSnapshot(long,
    /// RepoStatusDto[])</c> is matched by SignalR on name and argument count: a third parameter would
    /// make every Director in the field fail its push outright. Repeating the listing on every row
    /// would cost a machine with N repositories N copies of an N-entry list on a push that fires all
    /// day. The Gateway reads the FIRST non-null it finds anywhere in the set, so the fact survives
    /// any re-ordering, and a push with no rows carries none - which costs nothing, because a push
    /// with no rows never reconciles anything either.
    /// </summary>
    public List<RootFolderListingDto>? RootFolders { get; set; }
}

/// <summary>
/// One registered root folder, and the direct child folders that existed under it when the pushing
/// Director listed it (the one-repository-list mission, "the catalogue forgets").
///
/// THE PRESENCE OF THIS ENTRY IS THE PERMISSION TO FORGET, and the property that makes it safe is that
/// <b>a root the Director CANNOT list is omitted entirely, so nothing beneath it is ever forgotten.</b>
/// A Director includes a root here only when it could read that folder, so a root that is registered but
/// unmounted, unreadable, or no longer watched is absent rather than present-and-empty. The difference
/// is the whole thing: present-and-empty authorises the Gateway to forget every repository it holds
/// under that root, and an unmounted disk means "I know nothing here", never "nothing is here".
/// </summary>
public class RootFolderListingDto
{
    /// <summary>The registered root folder, exactly as the Director holds it.</summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// The full path of every direct child FOLDER that existed under <see cref="Path"/> when the
    /// Director listed it - not only the git repositories. Full paths rather than folder names so
    /// that the Gateway compares them the one way it compares every path, through
    /// <c>KnownRepositoryStore.NormalizePathKey</c>, and never has to decide a name's case for a
    /// machine it is not.
    /// </summary>
    public List<string> ChildPaths { get; set; } = new();
}

/// <summary>One linked worktree of a repository. State strings are folded by the Director.</summary>
public class WorktreeDto
{
    public string Path { get; set; } = "";
    public string? Branch { get; set; }

    /// <summary>"safe-to-reap", "in-use", or "needs-attention" - folded, rendered verbatim.</summary>
    public string State { get; set; } = "";

    /// <summary>The one-line reason for the state, folded by the Director.</summary>
    public string Reason { get; set; } = "";

    /// <summary>Labels of live sessions working in this worktree (empty when none).</summary>
    public List<string> SessionLabels { get; set; } = new();

    public long? SizeBytes { get; set; }
    public DateTime? LastActivityUtc { get; set; }
    public int AheadOfMain { get; set; }
    public int BehindMain { get; set; }
    public int DirtyFileCount { get; set; }
    public bool IsDetachedHead { get; set; }
}

/// <summary>
/// One flattened worktree row for GET /worktrees and `cc-devthrottle worktree list`: the whole
/// fleet's worktrees, each carrying its repository, machine, verdict, and occupying sessions.
/// </summary>
public class FleetWorktreeDto
{
    public string RepoName { get; set; } = "";
    public string RepoPath { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string DirectorId { get; set; } = "";
    public string Path { get; set; } = "";
    public string? Branch { get; set; }
    public string State { get; set; } = "";
    public string Reason { get; set; } = "";
    public List<string> SessionLabels { get; set; } = new();
    public long? SizeBytes { get; set; }
    public DateTime? LastActivityUtc { get; set; }

    /// <summary>How old the pushing Director's data is, in seconds, at serve time.</summary>
    public double DataAgeSeconds { get; set; }

    /// <summary>True when the owning repository entry is still verifying (warm-start cache).</summary>
    public bool Provisional { get; set; }
}

/// <summary>
/// The one flatten fold for GET /worktrees rows, shared by the Gateway and the Director's local
/// relay so no surface can flatten its own way. Fail closed: a worktree of a PROVISIONAL
/// (still-verifying) repository is served as "verifying" - never "safe-to-reap" - because its
/// verdict is cached, unverified data. Clients and the CLI key off the folded state string and
/// need no logic of their own (the dumb-client rule).
/// </summary>
public static class FleetWorktreeFold
{
    /// <summary>The folded state for a worktree whose repository has not been re-verified yet.</summary>
    public const string VerifyingState = "verifying";

    /// <summary>The folded reason for a worktree whose repository has not been re-verified yet.</summary>
    public const string VerifyingReason = "Still verifying this repository - cached data is never acted on.";

    public static List<FleetWorktreeDto> Flatten(IEnumerable<RepoStatusDto> repositories, double dataAgeSeconds = 0)
        => repositories
            .SelectMany(r => r.Worktrees.Select(w => new FleetWorktreeDto
            {
                RepoName = r.Name,
                RepoPath = r.Path,
                MachineName = r.MachineName,
                DirectorId = r.DirectorId,
                Path = w.Path,
                Branch = w.Branch,
                State = r.Provisional ? VerifyingState : w.State,
                Reason = r.Provisional ? VerifyingReason : w.Reason,
                SessionLabels = w.SessionLabels,
                SizeBytes = w.SizeBytes,
                LastActivityUtc = w.LastActivityUtc,
                DataAgeSeconds = dataAgeSeconds,
                Provisional = r.Provisional,
            }))
            .ToList();

    /// <summary>
    /// The one repository-level serve fold (inspection round 2, ruling R2-3): a PROVISIONAL
    /// repository's safe count serves as ZERO and its nested worktree states serve as
    /// "verifying", whatever the pushing Director sent - a pre-fix Director can push
    /// Provisional=true with a stale safe count and stale state strings, and the Gateway owns
    /// the verdict at serve time. Used by BOTH the Gateway's GET /repositories serve path and
    /// the Director's outgoing mapper, so no surface can fold its own way. A verified
    /// repository passes through unchanged. Returns a copy - the cached instance is never
    /// mutated.
    /// </summary>
    public static RepoStatusDto FoldRepositoryForServe(RepoStatusDto r)
    {
        if (!r.Provisional)
            return r;
        return new RepoStatusDto
        {
            DirectorId = r.DirectorId,
            MachineName = r.MachineName,
            Path = r.Path,
            Name = r.Name,
            RemoteUrl = r.RemoteUrl,
            Provider = r.Provider,
            Org = r.Org,
            Branch = r.Branch,
            IsClean = r.IsClean,
            UncommittedCount = r.UncommittedCount,
            DirtySinceUtc = r.DirtySinceUtc,
            AheadCount = r.AheadCount,
            BehindCount = r.BehindCount,
            BehindMainCount = r.BehindMainCount,
            WorktreeCount = r.WorktreeCount,
            WorktreesSafeToReap = 0, // fail closed: unverified work is never reclaimable
            WorktreesInUse = r.WorktreesInUse,
            WorktreesNeedAttention = r.WorktreesNeedAttention,
            WorktreeBytes = r.WorktreeBytes,
            Provisional = true,
            // RootFolders is deliberately not carried: it is not a status, and this fold exists to make a
            // status honest for GET /repositories. The catalog reads the listing off the push itself,
            // before anything serves it.
            Worktrees = r.Worktrees.Select(w => new WorktreeDto
            {
                Path = w.Path,
                Branch = w.Branch,
                State = VerifyingState,
                Reason = VerifyingReason,
                SessionLabels = w.SessionLabels,
                SizeBytes = w.SizeBytes,
                LastActivityUtc = w.LastActivityUtc,
                AheadOfMain = w.AheadOfMain,
                BehindMain = w.BehindMain,
                DirtyFileCount = w.DirtyFileCount,
                IsDetachedHead = w.IsDetachedHead,
            }).ToList(),
        };
    }
}
