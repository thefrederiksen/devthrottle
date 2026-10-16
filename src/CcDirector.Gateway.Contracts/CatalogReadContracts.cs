namespace CcDirector.Gateway.Contracts;

// Gateway Cleanup mission, Phase 0 (Worker R2): the request shapes for the CATALOG / director-level READ
// verbs that were moved onto the tunnel command surface. These are ADDITIVE - they name the exact
// query-string arguments the old REST lambdas took (they have no home on DirectorCommand, so they ride in
// the command payload), so the tunnel verb and the re-pointed REST route serialize identical JSON. The
// response shapes are the DTOs the old routes already produced (ClaudeSessionDto, CoachingCategoryDto,
// DirectoryListingDto, GitSnapshot, DirectorCrashJournalData) - unchanged. Kept in one new file so no shared
// Contracts file is edited.

/// <summary>
/// GET /claude-sessions request. Carries the optional <c>repo</c> query-string argument the old REST route
/// took to filter the resumable-session list to one repository. Null / absent means no filter (every repo),
/// matching the route's <c>string? repo</c>.
/// </summary>
public sealed class ClaudeSessionsRequest
{
    /// <summary>When set, keep only sessions whose repo path matches this one (normalized comparison).</summary>
    public string? Repo { get; set; }
}

/// <summary>
/// GET /fs/list request. Carries the optional <c>path</c> query-string argument the old REST route took. Null
/// / absent lists the drive roots; otherwise the named directory is listed, matching the route's
/// <c>string? path</c>.
/// </summary>
public sealed class FsListRequest
{
    /// <summary>The directory to list, or null / absent to list the drive roots.</summary>
    public string? Path { get; set; }
}

/// <summary>
/// Issue #4078: the <c>path-status</c> request. The Chat asks the session's own Director what each path it
/// shows as a link is - a file, a folder, missing from disk, or not allowed for this session - so a path that
/// is not on disk can be greyed out before anyone clicks it. The session is the command's own session id.
/// </summary>
public sealed class PathStatusRequest
{
    /// <summary>The paths to describe, exactly as the Chat detected them.</summary>
    public List<string> Paths { get; set; } = new();
}

/// <summary>Issue #4078: the <c>path-status</c> answer, one row per requested path, in request order.</summary>
public sealed class PathStatusResponse
{
    public List<PathStatusDto> Paths { get; set; } = new();
}

/// <summary>
/// Issue #4078: what one path is, for one session, decided on that session's Director. The same shape is the
/// failure body of a refused file read, so the viewer shows the very sentence the Director wrote.
/// </summary>
public sealed class PathStatusDto
{
    /// <summary>The path exactly as it was asked about.</summary>
    public string Path { get; set; } = "";

    /// <summary>One of <see cref="PathStatusCodes"/>.</summary>
    public string Code { get; set; } = "";

    /// <summary>
    /// One plain sentence a person reads, written by the Director. Empty for an ordinary file, which needs no
    /// explanation. Clients show it verbatim and never write their own.
    /// </summary>
    public string Sentence { get; set; } = "";
}

/// <summary>The four answers a path can get (issue #4078). Strings on the wire, so an older client can read them.</summary>
public static class PathStatusCodes
{
    public const string File = "file";
    public const string Folder = "folder";
    public const string NotOnDisk = "not-on-disk";
    public const string NotAllowed = "not-allowed";
}
