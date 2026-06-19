using CcDirector.Core.Agents;

namespace CcDirector.Core.Drivers;

/// <summary>
/// Slash command metadata owned by an agent driver. These commands describe the
/// agent composer model and do not control the Director action buttons.
/// </summary>
public sealed record AgentSlashCommand(
    string Name,
    string Description,
    string Category,
    string Source,
    AgentKind DriverKind,
    bool IsTerminalOnly = false,
    string Documentation = "")
{
    public string NormalizedName => Name.StartsWith('/') ? Name[1..] : Name;
}
