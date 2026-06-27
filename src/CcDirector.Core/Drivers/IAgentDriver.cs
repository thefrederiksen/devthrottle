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

    /// <summary>The tool takes a model on the command line (claude's <c>--model</c>), so the
    /// Edit Agent dialog can offer a driver-supplied model picker. When absent, model
    /// selection is hidden and the tool's own default is used.</summary>
    ModelSelection = 64,

    /// <summary>The driver can report how full the context window currently is (used tokens, and
    /// where the model's window is known, the window size and percent), so the Director can show a
    /// live context gauge without the user typing a slash command. Distinct from
    /// <see cref="TranscriptRead"/>: that means "I can parse the whole conversation", this means "I
    /// can answer the narrower question 'how full is the window right now'" - a driver may have one
    /// without the other.</summary>
    ContextUsage = 128,
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

    /// <summary>
    /// True when this CLI's idle terminal never goes byte-silent: it continuously repaints
    /// an animated footer (a spinner, a shortcuts hint, a clock, the synchronized-output
    /// heartbeat) even after the turn is finished and it is waiting for input. The byte-only
    /// idle rule in <c>TerminalStateDetector</c> would pin such a session to Working forever,
    /// so for these agents the detector switches to a screen-content rule: it only treats the
    /// session as active while the screen body (above the cursor) is changing. Default false -
    /// a well-behaved CLI stops emitting bytes when idle, which the cheap byte rule handles.
    /// This is a terminal-behavior trait, deliberately separate from <see cref="Capabilities"/>
    /// (which drives the Director action buttons).
    /// </summary>
    bool EmitsContinuousIdleOutput => false;

    /// <summary>Slash command metadata for the agent's own composer model. This is
    /// separate from <see cref="Capabilities"/>, which controls Director action buttons.</summary>
    IReadOnlyList<AgentSlashCommand> SlashCommands { get; }

    /// <summary>The command-line flag this tool takes a model on (e.g. <c>--model</c>), or
    /// empty when the tool has no model flag. Used to compose the effective launch command so
    /// the flag is owned by the driver, not hard-coded in the config layer.</summary>
    string ModelFlag { get; }

    /// <summary>The models this driver knows about, for the Edit Agent model picker. Empty
    /// when the tool has no <see cref="DriverCapabilities.ModelSelection"/>. "Use the tool's
    /// own default" is NOT an entry here - it is represented by an unset model.</summary>
    IReadOnlyList<AgentModelOption> KnownModels { get; }

    /// <summary>The model the tool is currently configured to default to (read from the tool's
    /// own settings), or null when none is set / it cannot be determined. A display hint only -
    /// never written, never used to compose the launch command.</summary>
    string? ReadConfiguredDefaultModel();

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

    /// <summary>How full the context window is right now (capability
    /// <see cref="DriverCapabilities.ContextUsage"/>): used tokens, the window size when the model
    /// is known, and the percent. Null when it cannot be determined yet (no turn has happened).
    /// <paramref name="launchArgs"/> is the session's launch command line (e.g.
    /// <c>--model opus[1m]</c>): the AUTHORITATIVE window signal, because the transcript model id is
    /// recorded without the <c>[1m]</c> suffix. A driver that needs no launch hint may ignore it. The
    /// default throws <see cref="NotSupportedException"/> so a driver that does not declare the flag
    /// is honestly absent - never emulated; only a driver that declares ContextUsage overrides
    /// this.</summary>
    ContextUsageDto? ReadContextUsage(string agentSessionId, string workingDirectory, string? launchArgs) =>
        throw new NotSupportedException(
            $"[{GetType().Name}] {Kind} does not declare DriverCapabilities.ContextUsage.");

    /// <summary>The working directory's transcript files, newest first
    /// (capability TranscriptRead).</summary>
    List<(string AgentSessionId, DateTime LastWriteUtc)> ListTranscripts(string workingDirectory);
}
