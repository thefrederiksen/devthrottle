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
/// Transcripts exist (~/.pi/agent/sessions/&lt;cwd-slug&gt;/&lt;timestamp&gt;_&lt;id&gt;.jsonl) and the
/// conversation reader parses them, but the widget/usage readers below are not implemented, so
/// TranscriptRead stays undeclared. Launching remains with the Director's PiAgent, which passes
/// <c>--session-id</c> so the id - and therefore the file - is known from birth (issue #2670);
/// the records readers below resolve by that id.
/// </summary>
public sealed class PiDriver : IAgentDriver
{
    private static readonly byte[] EscapeByte = [0x1B];

    public AgentKind Kind => AgentKind.Pi;

    public DriverCapabilities Capabilities =>
        DriverCapabilities.Cancel
        | DriverCapabilities.ClearContext
        | DriverCapabilities.ContextUsage
        | DriverCapabilities.ModelReport
        // pi compacts with /compact (its own catalog: "Manually compact context"). No completion
        // report: pi transcript parsing is not implemented, so there is nothing to read a finish from,
        // and compact-and-continue refuses for pi rather than guessing (issue #2150).
        | DriverCapabilities.CompactContext;

    /// <summary>Terminal, and API mode: <c>pi --mode rpc</c>, spoken to by <see cref="ApiMode.Pi.PiDialect"/>
    /// (issue #4070).</summary>
    public IReadOnlyList<ApiMode.SessionConnectionMode> ConnectionModes => ApiMode.SessionConnectionModes.TerminalAndApi;

    public IReadOnlyList<AgentSlashCommand> SlashCommands => PiSlashCommands.All;

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

    public Task CancelAsync(ISessionBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        FileLog.Write("[PiDriver] CancelAsync: sending Esc");
        backend.Write(EscapeByte);
        return Task.CompletedTask;
    }

    public Task InterruptAsync(ISessionBackend backend) =>
        throw new NotSupportedException(
            "[PiDriver] pi has no safe hard interrupt: Ctrl+C clears the editor and " +
            "Ctrl+C twice QUITS pi. Use CancelAsync (Esc).");

    public Task ShowHistoryAsync(ISessionBackend backend) =>
        throw new NotSupportedException(
            "[PiDriver] pi's double-Esc opens the /tree session navigator, not a history " +
            "rewind; not surfaced as History until live-verified as useful.");

    /// <summary>pi's context clear: the /new command starts a fresh session in place.</summary>
    public Task ClearContextAsync(ISessionBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        FileLog.Write("[PiDriver] ClearContextAsync: submitting /new");
        return backend.SendTextAsync("/new");
    }

    /// <summary>pi's in-place summarize: /compact, distinct from /new which starts over.</summary>
    public Task CompactContextAsync(ISessionBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        FileLog.Write("[PiDriver] CompactContextAsync: submitting /compact");
        return backend.SendTextAsync("/compact");
    }

    public List<TurnWidgetDto> ReadWidgets(string agentSessionId, string workingDirectory) =>
        throw new NotSupportedException("[PiDriver] pi transcript parsing is not implemented (v1).");

    public SessionUsageDto? ReadUsage(string agentSessionId, string workingDirectory) =>
        throw new NotSupportedException("[PiDriver] pi transcript parsing is not implemented (v1).");

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
        throw new NotSupportedException("[PiDriver] pi transcript listing is not implemented (v1).");
}
