namespace CcDirector.Core.Sessions;

/// <summary>
/// A single session entry within a workspace definition.
/// Workspaces always start fresh sessions (no ClaudeSessionId).
/// </summary>
public class WorkspaceSessionEntry
{
    public string RepoPath { get; set; } = string.Empty;
    public string? CustomName { get; set; }
    public string? CustomColor { get; set; }
    public int SortOrder { get; set; }
    public string? ClaudeArgs { get; set; }

    /// <summary>
    /// Optional reference to a handover document the wingman wrote for this session at
    /// save time (issue #512). When the workspace is reopened with "Seed from handovers"
    /// enabled, the restored session is seeded from this note so it picks up where the
    /// original left off. Null means the entry carries no handover - the session is
    /// restored fresh (today's behavior).
    /// </summary>
    public string? HandoverPath { get; set; }
}

/// <summary>
/// A named collection of sessions that can be saved and loaded.
/// Stored as individual JSON files in the workspaces directory.
/// </summary>
public class WorkspaceDefinition
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<WorkspaceSessionEntry> Sessions { get; set; } = new();
}
