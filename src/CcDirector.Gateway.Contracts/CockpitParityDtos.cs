namespace CcDirector.Gateway.Contracts;

/// <summary>
/// A resumable Claude Code session (returned by GET /claude-sessions). Merges CC Director's
/// session-history workspace metadata with the Claude Code session-index metadata, so the
/// Cockpit Resume tab can show the same rows the desktop dialog does.
/// </summary>
public sealed class ClaudeSessionDto
{
    /// <summary>The Claude Code session ID to resume.</summary>
    public string ClaudeSessionId { get; set; } = "";

    /// <summary>Repository / working directory the session was opened in.</summary>
    public string RepoPath { get; set; } = "";

    /// <summary>Folder name of the repo (display fallback when there is no custom name).</summary>
    public string ProjectName { get; set; } = "";

    /// <summary>User-defined workspace name, if any.</summary>
    public string? CustomName { get; set; }

    /// <summary>User-chosen header color (hex), if any.</summary>
    public string? CustomColor { get; set; }

    /// <summary>Number of messages in the Claude session (0 if unknown).</summary>
    public int MessageCount { get; set; }

    /// <summary>Best available one-line summary (summary, else first prompt, else snippet).</summary>
    public string? Summary { get; set; }

    /// <summary>When the session was last used (UTC). Drives sort order.</summary>
    public DateTime? LastUsedUtc { get; set; }
}

/// <summary>
/// A handover document on a Director (returned by GET /handovers). The Director parses the
/// markdown frontmatter so the Cockpit doesn't need filesystem access.
/// </summary>
public sealed class HandoverDto
{
    /// <summary>Absolute path of the handover markdown file on the Director.</summary>
    public string Path { get; set; } = "";

    /// <summary>Human-friendly title derived from the filename slug.</summary>
    public string Title { get; set; } = "";

    /// <summary>Display date (yyyy-MM-dd HH:mm) parsed from the filename or file write time.</summary>
    public string DateDisplay { get; set; } = "";

    /// <summary>Sort key (UTC) parsed from the filename or file write time.</summary>
    public DateTime DateUtc { get; set; }

    /// <summary>Primary repository path referenced by the handover (first of <see cref="RepoPaths"/>).</summary>
    public string? RepoPath { get; set; }

    /// <summary>All repository paths referenced by the handover frontmatter.</summary>
    public List<string> RepoPaths { get; set; } = new();

    /// <summary>Optional source session name from frontmatter.</summary>
    public string? SessionName { get; set; }
}

/// <summary>Full content of a single handover document (returned by GET /handovers/content).</summary>
public sealed class HandoverContentDto
{
    public string Path { get; set; } = "";
    public string Content { get; set; } = "";
}

/// <summary>
/// Request body for POST /handovers: create a standalone handover document in the vault
/// handover folder (no target session involved, unlike POST /handover which dispatches).
/// </summary>
public sealed class HandoverCreateRequest
{
    /// <summary>Required. Becomes the filename slug and the frontmatter title.</summary>
    public string Title { get; set; } = "";

    /// <summary>Required. Markdown body of the handover (frontmatter is composed by the Director).</summary>
    public string Content { get; set; } = "";

    /// <summary>Repository paths this handover concerns (frontmatter "repositories" list).</summary>
    public List<string> RepoPaths { get; set; } = new();

    /// <summary>Optional source session name (frontmatter "session_name").</summary>
    public string? SessionName { get; set; }
}

/// <summary>
/// A coaching quick-launch category (returned by GET /coaching/categories). The Director
/// resolves the on-disk path so the Cockpit can create a session there.
/// </summary>
public sealed class CoachingCategoryDto
{
    /// <summary>Stable key, e.g. "assistant" or "coach".</summary>
    public string Key { get; set; } = "";

    /// <summary>Display label, e.g. "Assistant".</summary>
    public string Label { get; set; } = "";

    /// <summary>Short description shown on the card.</summary>
    public string Description { get; set; } = "";

    /// <summary>Resolved Director-local directory the session should open in.</summary>
    public string Path { get; set; } = "";
}

/// <summary>One entry in a directory listing (returned inside <see cref="DirectoryListingDto"/>).</summary>
public sealed class DirEntryDto
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";

    /// <summary>True when this entry is a drive root (e.g. "C:\").</summary>
    public bool IsDrive { get; set; }
}

/// <summary>
/// A directory listing for the remote folder browser (returned by GET /fs/list). When
/// <see cref="CurrentPath"/> is null the listing is the set of drive roots.
/// </summary>
public sealed class DirectoryListingDto
{
    /// <summary>The directory that was listed, or null when listing drive roots.</summary>
    public string? CurrentPath { get; set; }

    /// <summary>The parent directory of <see cref="CurrentPath"/>, or null at a drive root / drive list.</summary>
    public string? ParentPath { get; set; }

    /// <summary>Sub-directories (or drive roots), sorted by name.</summary>
    public List<DirEntryDto> Entries { get; set; } = new();
}
