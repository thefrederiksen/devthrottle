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
/// <param name="SupportsPreassignedSessionId">The agent accepts a caller-chosen session id at launch.</param>
/// <param name="SupportsStudioMode">The agent can be launched in Studio (stream-json) mode.</param>
/// <param name="CanResumeSavedConversation">
/// THE AGENT CAN BE STARTED ON A SAVED CONVERSATION: handed a conversation id at launch, it comes back with
/// that conversation rather than a blank one. Every plugin must state it - there is no default - because a
/// plugin that could stay silent would be read as "cannot resume" by whoever asks, and the way up then tells
/// the owner his conversation is lost when it is not (review of phase 3, finding 2).
///
/// It is stated HERE, beside the driver that really does it, and never in a second hand-kept list somewhere
/// else: two lists drift apart, and this one had. <c>AgentPluginLaunchMetadataTests</c> walks every
/// registered agent and fails the day this flag stops matching what the agent's own arguments do.
/// </param>
public sealed record AgentPluginLaunchMetadata(
    bool SupportsPreassignedSessionId,
    bool SupportsStudioMode,
    bool CanResumeSavedConversation);

/// <summary>Input used when callers ask a plugin to build a launch spec without constructing the agent directly.</summary>
public sealed record AgentPluginLaunchRequest(
    AgentOptions Options,
    string? UserArgs,
    string? ResumeSessionId,
    bool StudioMode);

/// <summary>One selectable agent type exposed to Settings UI and settings Control API surfaces.</summary>
public sealed record AgentPluginTypeOption(AgentKind Kind, string Label);

/// <summary>
/// How an agent surfaces the launch-time fleet preamble (its identity + the cc-* fleet commands),
/// or that it genuinely cannot. The four working strategies plus an explicit "cannot" let the UI and
/// reports tell the truth per agent instead of leaving a silent gap.
/// </summary>
public enum FleetPreambleStrategy
{
    /// <summary>The agent cannot inject context (e.g. an arbitrary raw command with no contract).</summary>
    None = 0,
    /// <summary>A config hook emits the preamble as additionalContext (Claude, Codex, Gemini, Cursor).</summary>
    NativeHook = 1,
    /// <summary>We subscribe to the agent's event stream and push the preamble (OpenCode).</summary>
    EventBus = 2,
    /// <summary>An in-process extension we author injects the preamble (Pi).</summary>
    Extension = 3,
    /// <summary>A file the agent re-reads every prompt carries the preamble (Grok, Copilot).</summary>
    InstructionFile = 4,
}

/// <summary>Where the fleet-preamble work stands for an agent.</summary>
public enum FleetPreambleStatus
{
    /// <summary>Genuinely cannot be done - the agent has no usable mechanism.</summary>
    Unsupported = 0,
    /// <summary>The strategy is known but not yet implemented.</summary>
    Planned = 1,
    /// <summary>Implemented and active.</summary>
    Wired = 2,
}

/// <summary>Plugin-owned declaration of how (or whether) this agent gets the fleet preamble.</summary>
public sealed record AgentPluginFleetMetadata(
    FleetPreambleStrategy Strategy,
    FleetPreambleStatus Status,
    string Note);
