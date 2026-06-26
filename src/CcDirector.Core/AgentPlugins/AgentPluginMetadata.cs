using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;

namespace CcDirector.Core.AgentPlugins;

/// <summary>Plugin-owned settings metadata for one agent CLI.</summary>
public sealed record AgentPluginSettingsMetadata(
    string TypeLabel,
    string ConfigKey,
    Func<AgentOptions, string> GetConfiguredPath,
    Action<AgentOptions, string> SetConfiguredPath);

/// <summary>One executable candidate the detection wizard should probe for an agent CLI.</summary>
public sealed record AgentPluginDetectionCandidate(string Path);

/// <summary>Plugin-owned executable detection metadata for one agent CLI.</summary>
public sealed record AgentPluginDetectionMetadata(
    IReadOnlyList<AgentPluginDetectionCandidate> Candidates,
    string InstallHint);

/// <summary>Plugin-owned harmless validation/version probe metadata for one agent CLI.</summary>
public sealed record AgentPluginValidationMetadata(string Arguments, TimeSpan Timeout);

/// <summary>How Director can reconstruct history for an agent session.</summary>
public enum AgentHistoryProviderKind
{
    None = 0,
    TerminalBuffer = 1,
    TranscriptFile = 2,
    SqliteStore = 3,
}

/// <summary>Plugin-owned history/transcript metadata for one agent CLI.</summary>
public sealed record AgentPluginHistoryMetadata(
    AgentHistoryProviderKind ProviderKind,
    bool SupportsConversationHistory,
    string StoreDescription);

/// <summary>Plugin-owned launch metadata for one agent CLI.</summary>
public sealed record AgentPluginLaunchMetadata(
    bool SupportsPreassignedSessionId,
    bool SupportsStudioMode);

/// <summary>Input used when callers ask a plugin to build a launch spec without constructing the agent directly.</summary>
public sealed record AgentPluginLaunchRequest(
    AgentOptions Options,
    string? UserArgs,
    string? ResumeSessionId,
    bool StudioMode);

/// <summary>One selectable agent type exposed to Settings UI and settings Control API surfaces.</summary>
public sealed record AgentPluginTypeOption(AgentKind Kind, string Label);
