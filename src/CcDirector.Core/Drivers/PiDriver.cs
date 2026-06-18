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
/// Transcripts exist (~/.pi/agent/sessions/&lt;cwd-slug&gt;/&lt;uuid&gt;.jsonl) but their format
/// is unparsed in v1, so TranscriptRead stays undeclared. Launching remains with the
/// Director's PiAgent (no session-id preassignment in pi).
/// </summary>
public sealed class PiDriver : IAgentDriver
{
    private static readonly byte[] EscapeByte = [0x1B];

    public AgentKind Kind => AgentKind.Pi;

    public DriverCapabilities Capabilities =>
        DriverCapabilities.Cancel | DriverCapabilities.ClearContext;

    public IReadOnlyList<AgentSlashCommand> SlashCommands => PiSlashCommands.All;

    public string ResolveExecutable(string? configuredPath) =>
        throw new NotSupportedException(
            "[PiDriver] Executable resolution is owned by the Director's PiAgent path; " +
            "hosting pi requires PreassignedSessionId support pi does not have.");

    public AgentLaunchSpec BuildLaunchSpec(string? baseArgs, string? resumeSessionId) =>
        throw new NotSupportedException(
            "[PiDriver] Launch specs are owned by the Director's PiAgent path.");

    public Task SubmitAsync(ISessionBackend backend, string text)
    {
        ArgumentNullException.ThrowIfNull(backend);
        // Blind submit: pi's composer echo layout is unverified, so no echo gate yet.
        return backend.SendTextAsync(text);
    }

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

    public List<TurnWidgetDto> ReadWidgets(string agentSessionId, string workingDirectory) =>
        throw new NotSupportedException("[PiDriver] pi transcript parsing is not implemented (v1).");

    public SessionUsageDto? ReadUsage(string agentSessionId, string workingDirectory) =>
        throw new NotSupportedException("[PiDriver] pi transcript parsing is not implemented (v1).");

    public List<(string AgentSessionId, DateTime LastWriteUtc)> ListTranscripts(string workingDirectory) =>
        throw new NotSupportedException("[PiDriver] pi transcript listing is not implemented (v1).");
}
