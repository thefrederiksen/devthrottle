namespace CcDirector.Core.Sessions;

/// <summary>
/// A single reusable, named session: a repository path plus a chosen agent (referenced by the
/// registered agent entry's stable id), under a user-chosen name, optionally with a colour and
/// extra command-line arguments. Unlike a workspace (which holds many sessions and always starts
/// them fresh as a set), a named session is ONE launchable item the user saves once and relaunches
/// in a single click from the New Session dialog.
///
/// Stored as an individual JSON file in the named-sessions directory, mirroring
/// <see cref="WorkspaceDefinition"/> / <see cref="WorkspaceStore"/>.
/// </summary>
public class NamedSessionDefinition
{
    /// <summary>Schema version for forward-compatibility, matching the workspace store convention.</summary>
    public int Version { get; set; } = 1;

    /// <summary>The user-chosen display name; also the source of the on-disk file slug.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The repository path the relaunched session opens in.</summary>
    public string RepoPath { get; set; } = string.Empty;

    /// <summary>
    /// The stable id of the registered agent entry (<c>agent.entries</c>, issue #489) this named
    /// session launches with. If that agent is later removed, the named session is shown as
    /// unavailable rather than silently launching a different agent.
    /// </summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>Optional session colour (hex string such as <c>#2563EB</c>); null when not set.</summary>
    public string? Color { get; set; }

    /// <summary>Optional extra command-line arguments appended at launch; null when not set.</summary>
    public string? Arguments { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
