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

    /// <summary>The driver can report the model the tool is CURRENTLY using, read from the tool's
    /// own records (transcript, rollout, event log, session store). Distinct from
    /// <see cref="ModelSelection"/> (the tool takes a model flag at launch): the user can switch
    /// models mid-session, so the launch flag goes stale - this capability answers "what model is
    /// it really running right now", so the Director can log model usage per session (issue
    /// #1637).</summary>
    ModelReport = 256,

    /// <summary>The driver can report CUMULATIVE token spend for the session - the running sums of
    /// input, output and cached tokens across the whole conversation - from the tool's own records
    /// (<see cref="IAgentDriver.ReadUsage"/>). Distinct from <see cref="ContextUsage"/>, which answers
    /// the narrower "how full is the window right now": that is a gauge at a point in time and cannot
    /// be summed into spend, while this is the additive total the governance statistics fold. A driver
    /// may declare one without the other - Codex reports context occupancy but not cumulative spend,
    /// so it declares ContextUsage and NOT this. Gate any turn-end spend read on this flag: several
    /// drivers implement <see cref="IAgentDriver.ReadUsage"/> as a throw, so calling it ungated would
    /// use an exception as control flow at every turn-end.</summary>
    TokenUsage = 512,

    /// <summary>The tool can SUMMARIZE its own conversation in place, freeing context window while
    /// keeping what it has learned (claude's <c>/compact</c>, gemini's <c>/compress</c>). Distinct
    /// from <see cref="ClearContext"/>, which throws the conversation away: a compacted session
    /// remembers what it was doing, a cleared one does not. Also distinct in a way that matters to
    /// the host - clearing starts a NEW agent conversation and forces a transcript re-link, while
    /// compaction continues under the SAME agent session id, so nothing is re-linked (issue #2150).
    /// A tool with no compaction command at all (Copilot) declares this absent and throws.</summary>
    CompactContext = 1024,

    /// <summary>The driver can tell when a compaction it started has FINISHED
    /// (<see cref="IAgentDriver.HasCompactedSince"/>), by reading the tool's own records rather than
    /// guessing from terminal quiet. Separate from <see cref="CompactContext"/> because submitting
    /// the command and knowing when it is done are different competences: claude has both (its
    /// transcript gains a compaction-summary entry), while a tool we can only type at has the first
    /// without the second. Compact-AND-CONTINUE requires this flag - without it there is no honest
    /// moment at which to send the follow-up prompt, so the Director refuses rather than guessing a
    /// delay and firing the prompt into a busy composer.</summary>
    CompactCompletionReport = 2048,
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

    /// <summary>
    /// Rows this agent's own interface draws ABOUT ITSELF rather than about the work: the update
    /// notice, the shortcut bar, the context-remaining hint, the interrupt hint. A row carrying one
    /// of these never counts as the conversation gaining content, so it can no longer turn a
    /// settled session blue - which is the single thing phase one of the turn-detection work exists
    /// to stop. Matched as a case-insensitive substring of the row.
    ///
    /// Deliberately a list of STRINGS and not a model or a parser. It is readable, it is testable,
    /// and when an agent changes its interface the failure is a slow return of phantom turns rather
    /// than a crash or a suppressed reply. For the same reason the strings are PRECISE rather than
    /// broad: a marker that is too narrow lets a phantom back, a marker that is too broad swallows
    /// a real reply, and only the second of those costs the owner a turn.
    ///
    /// Default empty, and that is the honest answer for most agents rather than a stub. The
    /// evidence behind the two lists that are not empty covers one machine, where 1,292 of the
    /// measured repaints were Claude Code, 43 were Codex and two were the one continuously
    /// repainting agent; every other supported agent had no cases at all. An empty list leaves an
    /// agent exactly as well off as it is today. A guessed list could suppress its real output.
    ///
    /// This is the second terminal-behaviour trait, beside <see cref="EmitsContinuousIdleOutput"/>;
    /// the interface carries plenty of other per-driver knowledge, for kind, capabilities, slash
    /// commands and the command line.
    /// </summary>
    IReadOnlyCollection<string> SelfDescribingRowMarkers => Array.Empty<string>();

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

    /// <summary>
    /// Summarize the conversation in place (capability <see cref="DriverCapabilities.CompactContext"/>):
    /// the tool rewrites its own history down to a summary and carries on. Unlike
    /// <see cref="ClearContextAsync"/> this keeps the tool's conversation identity, so the host must NOT
    /// re-link a transcript afterwards. The default throws <see cref="NotSupportedException"/> so a tool
    /// with no compaction command is honestly absent - never emulated with a clear, which would silently
    /// destroy the very work compaction exists to preserve.
    /// </summary>
    Task CompactContextAsync(ISessionBackend backend) =>
        throw new NotSupportedException(
            $"[{GetType().Name}] {Kind} does not declare DriverCapabilities.CompactContext.");

    /// <summary>
    /// Has a compaction finished since <paramref name="sinceUtc"/> (capability
    /// <see cref="DriverCapabilities.CompactCompletionReport"/>)? Read from the tool's own records - for
    /// claude, a compaction-summary entry in the transcript. This is the completion signal that lets the
    /// host wait for a compaction and only then send a follow-up prompt. The default throws
    /// <see cref="NotSupportedException"/>: a driver that cannot observe completion says so, rather than
    /// returning false forever (a silent never-finishes) or true immediately (a lie).
    /// </summary>
    bool HasCompactedSince(string agentSessionId, string workingDirectory, DateTime sinceUtc) =>
        throw new NotSupportedException(
            $"[{GetType().Name}] {Kind} does not declare DriverCapabilities.CompactCompletionReport.");

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

    /// <summary>The model the tool is currently using (capability
    /// <see cref="DriverCapabilities.ModelReport"/>), read from the tool's own records - the LATEST
    /// model the tool recorded, so a mid-session model switch is reflected. Null when it cannot be
    /// determined yet (no turn has happened). <paramref name="launchArgs"/> is the session's launch
    /// command line; a driver may use it as the pre-first-turn answer when its tool takes a model
    /// flag. The default throws <see cref="NotSupportedException"/> so a driver that does not
    /// declare the flag is honestly absent - never emulated.</summary>
    string? ReadCurrentModel(string agentSessionId, string workingDirectory, string? launchArgs) =>
        throw new NotSupportedException(
            $"[{GetType().Name}] {Kind} does not declare DriverCapabilities.ModelReport.");

    /// <summary>The working directory's transcript files, newest first
    /// (capability TranscriptRead).</summary>
    List<(string AgentSessionId, DateTime LastWriteUtc)> ListTranscripts(string workingDirectory);
}
