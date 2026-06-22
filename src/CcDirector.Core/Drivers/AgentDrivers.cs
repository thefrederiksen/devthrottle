using System.Collections.Concurrent;
using CcDirector.Core.Agents;

namespace CcDirector.Core.Drivers;

/// <summary>
/// The driver registry: one shared driver instance per CLI kind. Drivers are
/// stateless behavior bundles, so singletons are safe. Kinds without a written-and-
/// live-verified driver get a <see cref="GenericDriver"/> (today's exact keystrokes,
/// minimal declared capabilities) - consumers read <see cref="IAgentDriver.Capabilities"/>
/// to know what is actually available for a given session.
/// </summary>
public static class AgentDrivers
{
    private static readonly ConcurrentDictionary<AgentKind, IAgentDriver> Cache = new();

    public static IAgentDriver For(AgentKind kind) => Cache.GetOrAdd(kind, k => k switch
    {
        AgentKind.ClaudeCode => new ClaudeDriver(),
        AgentKind.Pi => new PiDriver(),
        AgentKind.Cursor => new CursorDriver(),
        AgentKind.Copilot => new CopilotDriver(),
        AgentKind.Codex => new GenericDriver(k, CodexSlashCommands.All),
        AgentKind.Gemini => new GenericDriver(k, GeminiSlashCommands.All),
        AgentKind.OpenCode => new GenericDriver(k, OpenCodeSlashCommands.All),
        AgentKind.Grok => new GenericDriver(k, GrokSlashCommands.All),
        _ => new GenericDriver(k),
    });
}
