using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Drivers;

/// <summary>
/// The Pi coding agent driver (@earendil-works/pi-coding-agent). Pi's keyboard map
/// (per its README "Keyboard Shortcuts", live-verified in the Director QA) differs
/// from Claude in ways that make per-CLI drivers necessary:
///
///   - Escape       = cancel/abort the current turn (same keystroke as Claude)
///   - Ctrl+C       = CLEAR THE EDITOR (not an interrupt!)
///   - Ctrl+C twice = QUIT pi entirely
///   - Esc twice    = open pi's /tree session navigator (not a history rewind)
///   - /new         = start a fresh session (pi's context clear)
///
/// Therefore: <see cref="DriverCapabilities.Interrupt"/> is NOT declared - a naive
/// Ctrl+C cascade would kill the session, and pi has no safe hard-interrupt distinct
/// from quit. History is NOT declared - double-Esc opens a different feature.
/// Transcripts exist (~/.pi/agent/sessions/&lt;cwd-slug&gt;/&lt;timestamp&gt;_&lt;id&gt;.jsonl); the
/// conversation, widget, and usage readers parse them and TranscriptRead is declared.
/// Launching remains with the Director's PiAgent, which passes
/// <c>--session-id</c> so the id - and therefore the file - is known from birth (issue #2670);
/// the records readers below resolve by that id.
/// </summary>
public sealed class PiDriver : IAgentDriver
{

    public AgentKind Kind => AgentKind.Pi;

    public DriverCapabilities Capabilities =>
        DriverCapabilities.Cancel
        | DriverCapabilities.ClearContext
        | DriverCapabilities.ContextUsage
        | DriverCapabilities.ModelReport
        | DriverCapabilities.TranscriptRead
        | DriverCapabilities.TokenUsage
        // pi compacts with /compact (its own catalog: "Manually compact context"). No completion
        // report: its transcript records the resulting conversation but no unambiguous compaction-finished event,
        // so compact-and-continue refuses for pi rather than guessing (issue #2150).
        | DriverCapabilities.CompactContext;

    /// <summary>Terminal, and API mode: <c>pi --mode rpc</c>, spoken to by <see cref="ApiMode.Pi.PiDialect"/>
    /// (issue #4070).</summary>
    public IReadOnlyList<ApiMode.SessionConnectionMode> ConnectionModes => ApiMode.SessionConnectionModes.TerminalAndApi;

    public IReadOnlyList<AgentSlashCommand> SlashCommands => PiSlashCommands.All;

    /// <summary>Pi 1.2.0 interface rows captured by the Terminal Parity state rig. These are precise interface phrases;
    /// broad fragments could hide a real reply from turn detection.</summary>
    public IReadOnlyCollection<string> SelfDescribingRowMarkers { get; } =
    [
        "Press ctrl+o to show full startup help",
        "Pi can explain its own features",
        "Update Available",
        "New version 1.4.0 is available",
        "Changelog: https://pi.dev/changelog",
    ];

    // pi selects its model inside the tool, not via a Director-passed flag (v1): no model selection.
    public string ModelFlag => "";
    public IReadOnlyList<AgentModelOption> KnownModels => [];
    public string? ReadConfiguredDefaultModel() => null;

    public string ResolveExecutable(string? configuredPath) =>
        throw new NotSupportedException(
            "[PiDriver] Executable resolution is owned by the Director's PiAgent path; " +
            "hosting pi requires PreassignedSessionId support pi does not have.");

    public AgentLaunchSpec BuildLaunchSpec(string? baseArgs, string? resumeSessionId) =>
        throw new NotSupportedException(
            "[PiDriver] Launch specs are owned by the Director's PiAgent path.");

    /// <summary>
    /// Echo-verified submit (shared helper): type the text, wait for pi's composer to echo it, then
    /// a separate Enter. This is the dropped-Enter guard that the blind submit lacked - a repainting
    /// composer can swallow a blind Enter when driven programmatically (fleet message delivery).
    /// Falls back to the backend's blind submit for non-buffering transports.
    /// </summary>
    public Task SubmitAsync(ISessionBackend backend, string text) =>
        TerminalSubmit.EchoVerifiedSubmitAsync(backend, text, "PiDriver");

    /// <summary>Measured on Pi (Terminal Parity baseline, 23 October 2026): a leading "!" runs the line as a shell command
    /// and never reaches the conversation. A leading "/" is a command only when it names one of Pi's own commands; any
    /// other line starting with "/" arrived as an ordinary prompt.</summary>
    public string? LeadingCharacterMode(string text)
    {
        if (text.Length == 0) return null;
        if (text[0] == '!') return "a shell command";
        if (text[0] != '/') return null;
        var word = text[1..].Split(' ', 2)[0];
        return SlashCommands.Any(c => string.Equals(c.NormalizedName, word, StringComparison.Ordinal)) ? "a slash command" : null;
    }

    public DriverInput Cancel() => DriverInput.PressKeys(DriverKey.Escape);

    public DriverInput Interrupt() =>
        throw new NotSupportedException(
            "[PiDriver] pi has no safe hard interrupt: Ctrl+C clears the editor and " +
            "Ctrl+C twice QUITS pi. Use Cancel (Esc).");

    public DriverInput ShowHistory() =>
        throw new NotSupportedException(
            "[PiDriver] pi's double-Esc opens the /tree session navigator, not a history " +
            "rewind; not surfaced as History until live-verified as useful.");

    /// <summary>pi's context clear: the /new command starts a fresh session in place.</summary>
    public DriverInput ClearContext() => DriverInput.TypeCommand("/new");

    /// <summary>pi's in-place summarize: /compact, distinct from /new which starts over.</summary>
    public DriverInput CompactContext() => DriverInput.TypeCommand("/compact");

    public List<TurnWidgetDto> ReadWidgets(string agentSessionId, string workingDirectory) =>
        Pi.PiTranscriptServices.ReadWidgetsForSession(agentSessionId);

    public SessionUsageDto? ReadUsage(string agentSessionId, string workingDirectory) =>
        Pi.PiTranscriptServices.ReadUsageForSession(agentSessionId);

    /// <summary>How full the pi context window is right now (capability
    /// <see cref="DriverCapabilities.ContextUsage"/>). pi's session file carries per-message
    /// <c>usage.input</c> and the model id, but NOT the window - and since issue #1100 the window is no
    /// longer derived from that model id, so this reports used tokens with no denominator until pi is
    /// actually asked. Located by the session id the Director launched pi with; the working directory
    /// and launch args are not used.</summary>
    public ContextUsageDto? ReadContextUsage(string agentSessionId, string workingDirectory, string? launchArgs) =>
        Pi.PiContextUsage.ReadForSession(agentSessionId);

    /// <summary>The model this pi session is currently using (capability
    /// <see cref="DriverCapabilities.ModelReport"/>): the LAST assistant message's model in the
    /// session file, so a mid-session model switch is reflected. Located by the session id the
    /// Director launched pi with.</summary>
    public string? ReadCurrentModel(string agentSessionId, string workingDirectory, string? launchArgs) =>
        Pi.PiCurrentModel.ReadForSession(agentSessionId);

    public List<(string AgentSessionId, DateTime LastWriteUtc)> ListTranscripts(string workingDirectory) =>
        Pi.PiSessionLocator.ListForRepo(workingDirectory);
}
