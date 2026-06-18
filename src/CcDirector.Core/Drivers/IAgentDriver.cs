using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Drivers;

/// <summary>
/// What a driver can do. A verb a tool lacks is DECLARED absent here and its method
/// throws <see cref="NotSupportedException"/> - never emulated with a guess.
/// </summary>
[Flags]
public enum DriverCapabilities
{
    None = 0,

    /// <summary>The tool has an in-place context reset (e.g. claude's /clear).</summary>
    ClearContext = 1,

    /// <summary>The tool can abort the current turn without dying (e.g. claude's Esc).</summary>
    Cancel = 2,

    /// <summary>The tool writes a machine-readable conversation transcript that the
    /// driver can read (replies, token usage).</summary>
    TranscriptRead = 4,

    /// <summary>The tool accepts a caller-chosen session id at launch (claude's
    /// --session-id), so the transcript location is known from birth.</summary>
    PreassignedSessionId = 8,

    /// <summary>The tool survives a hard interrupt keystroke (Ctrl+C) without dying -
    /// distinct from <see cref="Cancel"/>'s soft turn-abort.</summary>
    Interrupt = 16,

    /// <summary>The tool has an in-terminal history/rewind picker (claude's double-Esc
    /// "jump to a previous message"). A VISIBLE-terminal feature: only meaningful when
    /// a human is watching the rendered terminal.</summary>
    History = 32,
}

/// <summary>
/// The per-CLI interaction protocol: one driver class per agent CLI (Claude, Codex,
/// Gemini, Pi, ...), encoding how THAT tool is driven inside a terminal - submit
/// semantics, cancel keystrokes, context reset, transcript access. Drivers are
/// stateless behavior bundles: they own no process; hosts (HostedAgent today, the
/// Director's Session later) own the <see cref="ISessionBackend"/> and its lifecycle
/// and pass it in per call.
///
/// Layering: ISessionBackend = the terminal (transport), IAgentDriver = the tool's
/// protocol, the host = lifecycle + orchestration. See docs/plans/agent-driver.md.
/// </summary>
public interface IAgentDriver
{
    AgentKind Kind { get; }

    DriverCapabilities Capabilities { get; }

    /// <summary>Slash command metadata for the agent's own composer model. This is
    /// separate from <see cref="Capabilities"/>, which controls Director action buttons.</summary>
    IReadOnlyList<AgentSlashCommand> SlashCommands { get; }

    /// <summary>Resolve the tool's executable: validate an explicit path, or search
    /// PATH. Throws with install guidance when not found - no silent fallback.</summary>
    string ResolveExecutable(string? configuredPath);

    /// <summary>Build the spawn arguments (and the preassigned session id when the
    /// tool supports one).</summary>
    AgentLaunchSpec BuildLaunchSpec(string? baseArgs, string? resumeSessionId);

    /// <summary>Submit a prompt with the tool's correct typing semantics.</summary>
    Task SubmitAsync(ISessionBackend backend, string text);

    /// <summary>Abort the current turn (capability <see cref="DriverCapabilities.Cancel"/>).</summary>
    Task CancelAsync(ISessionBackend backend);

    /// <summary>Hard interrupt (capability <see cref="DriverCapabilities.Interrupt"/>) -
    /// Ctrl+C for every terminal CLI verified so far. Stronger than CancelAsync.</summary>
    Task InterruptAsync(ISessionBackend backend);

    /// <summary>Open the tool's in-terminal history/rewind picker (capability
    /// <see cref="DriverCapabilities.History"/>) - claude's double-Esc.</summary>
    Task ShowHistoryAsync(ISessionBackend backend);

    /// <summary>Reset the conversation context in place (capability
    /// <see cref="DriverCapabilities.ClearContext"/>). The host is responsible for
    /// re-discovering the tool's new transcript id afterwards via
    /// <see cref="ListTranscripts"/>.</summary>
    Task ClearContextAsync(ISessionBackend backend);

    /// <summary>Parsed conversation widgets of one transcript, chronological; empty
    /// when the transcript does not exist yet (capability TranscriptRead).</summary>
    List<TurnWidgetDto> ReadWidgets(string agentSessionId, string workingDirectory);

    /// <summary>Token usage of one transcript; null when it does not exist yet
    /// (capability TranscriptRead).</summary>
    SessionUsageDto? ReadUsage(string agentSessionId, string workingDirectory);

    /// <summary>The working directory's transcript files, newest first
    /// (capability TranscriptRead).</summary>
    List<(string AgentSessionId, DateTime LastWriteUtc)> ListTranscripts(string workingDirectory);
}
