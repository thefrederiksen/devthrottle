using CcDirector.Core.Agents;

namespace CcDirector.Core.Skills;

/// <summary>
/// Represents a discovered slash command.
/// </summary>
public sealed class SlashCommandItem
{
    public string Name { get; }
    public string Description { get; }
    public string Source { get; } // "builtin", "global", or "project"
    public string Documentation { get; } // Body content from skill file, when available.
    public string Category { get; } // For built-in commands: "Session", "Config", "Navigation", etc.
    public AgentKind? DriverKind { get; }
    public bool IsTerminalOnly { get; }

    public SlashCommandItem(
        string name,
        string description,
        string source,
        string documentation,
        string category = "",
        AgentKind? driverKind = null,
        bool isTerminalOnly = false)
    {
        Name = name;
        Description = description;
        Source = source;
        Documentation = documentation;
        Category = category;
        DriverKind = driverKind;
        IsTerminalOnly = isTerminalOnly;
    }

    public bool IsBuiltIn => Source == "builtin";
}
