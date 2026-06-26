using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Drivers;

namespace CcDirector.Core.AgentPlugins;

/// <summary>
/// Registry of available CLI plugins. This first implementation exposes the current
/// built-in tools through the plugin shape while preserving the existing catalog and
/// driver behavior.
/// </summary>
public static class AgentPluginRegistry
{
    private static readonly Lazy<IReadOnlyList<IAgentPlugin>> BuiltInPlugins = new(BuildBuiltIns);

    /// <summary>Built-in CLI plugins in display/catalog order.</summary>
    public static IReadOnlyList<IAgentPlugin> BuiltIns => BuiltInPlugins.Value;

    /// <summary>True when a plugin is registered for the supplied built-in agent kind.</summary>
    public static bool Contains(AgentKind kind) => BuiltIns.Any(plugin => plugin.Kind == kind);

    /// <summary>Look up a plugin by built-in agent kind.</summary>
    public static IAgentPlugin Get(AgentKind kind)
    {
        foreach (var plugin in BuiltIns)
        {
            if (plugin.Kind == kind)
                return plugin;
        }

        throw new NotSupportedException($"[AgentPluginRegistry] Agent kind {kind} has no registered plugin.");
    }

    private static IReadOnlyList<IAgentPlugin> BuildBuiltIns() =>
        AgentToolCatalog.Entries
            .Select<AgentToolCatalogEntry, IAgentPlugin>(entry => entry.Tool == AgentKind.Codex
                ? new CodexAgentPlugin()
                : new BuiltInAgentPlugin(
                    AgentToolConfig.KeyFor(entry.Tool),
                    AgentToolConfig.KeyFor(entry.Tool),
                    entry,
                    AgentDrivers.For(entry.Tool),
                    AgentFactory(entry.Tool),
                    SupportsConversationHistory(entry.Tool)))
            .ToArray();

    private static Func<AgentOptions, IAgent> AgentFactory(AgentKind kind) => kind switch
    {
        AgentKind.ClaudeCode => options => new ClaudeAgent(options),
        AgentKind.Pi => options => new PiAgent(options),
        AgentKind.Gemini => options => new GeminiAgent(options),
        AgentKind.OpenCode => options => new OpenCodeAgent(options),
        AgentKind.Cursor => options => new CursorAgent(options),
        AgentKind.Grok => options => new GrokAgent(options),
        AgentKind.Copilot => options => new CopilotAgent(options),
        _ => throw new NotSupportedException($"[AgentPluginRegistry] Agent kind {kind} cannot be created by a built-in plugin."),
    };

    private static bool SupportsConversationHistory(AgentKind kind) =>
        kind is AgentKind.ClaudeCode
            or AgentKind.Pi
            or AgentKind.Gemini
            or AgentKind.OpenCode
            or AgentKind.Grok
            or AgentKind.Copilot;
}
