namespace CcDirector.Core.Sessions;

/// <summary>
/// A named session preset (issue #508): a saved launch combination of a repository, an agent
/// (referenced by <see cref="AgentId"/>, an <c>AgentEntry.Id</c>), and a model, under a fixed
/// <see cref="Name"/>. The user saves the combination once and launches it in one click from the
/// New Session dialog. Stored as an individual JSON file in the named-sessions directory, mirroring
/// <see cref="WorkspaceDefinition"/>.
/// </summary>
public class NamedSessionDefinition
{
    /// <summary>Schema version of this preset file. Bumped only on a breaking shape change.</summary>
    public int Version { get; set; } = 1;

    /// <summary>The fixed display name shown in the New Session dropdown and the Manage dialog.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The repository folder this preset launches in.</summary>
    public string RepositoryPath { get; set; } = string.Empty;

    /// <summary>
    /// The stable identity of the configured agent this preset launches (an
    /// <c>AgentEntry.Id</c>). When the matching agent entry no longer exists, the preset is an
    /// orphan and is shown only in the Manage dialog with launch disabled.
    /// </summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// The model string passed to the agent for this preset (the same model field the New Session
    /// dialog uses). Empty means the agent's own configured default model.
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>When the preset was first created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the preset was last written (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
