using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Drivers;

/// <summary>
/// Driver for OpenAI Codex CLI. Codex is no longer represented by the generic driver:
/// its plugin/driver owns Codex slash commands, launch argument composition, context clear,
/// and the honest control surface exposed to the UI.
/// </summary>
public sealed class CodexDriver : IAgentDriver
{
    private static readonly byte[] EscapeByte = [0x1B];
    private static readonly byte[] CtrlC = [0x03];

    public AgentKind Kind => AgentKind.Codex;

    /// <summary>
    /// See <see cref="IAgentDriver.SelfDescribingRowMarkers"/>. Taken on 15 September from the same
    /// labelled corpus as the Claude Code list, but from FORTY-THREE repaint-labelled episodes
    /// rather than 1,292. That is thin, and this list should be read as what forty-three cases
    /// support rather than as a survey of what this agent draws about itself. Codex's phantom-blue
    /// problem is small; its real problem is the opposite one, red landing while it is still
    /// working, and that is phase two.
    /// </summary>
    public IReadOnlyCollection<string> SelfDescribingRowMarkers { get; } =
    [
        // One row, three fragments, because it is drawn as one line and truncates.
        "background terminal running",
        "/ps to view",
        "/stop to close",
        "Token usage:",
        // The resume banner, drawn across two rows.
        "To continue this session, run:",
        "codex resume",
    ];

    public DriverCapabilities Capabilities =>
        DriverCapabilities.Cancel
        | DriverCapabilities.Interrupt
        | DriverCapabilities.ClearContext
        | DriverCapabilities.ContextUsage
        | DriverCapabilities.ModelReport
        // Codex compacts with /compact, the same command its own catalog documents. It gets
        // CompactContext but NOT CompactCompletionReport: the Director cannot read codex's records to
        // learn when a compaction finished, so compact-and-continue refuses for codex rather than
        // timing the follow-up on a guess (issue #2150).
        | DriverCapabilities.CompactContext;

    public IReadOnlyList<AgentSlashCommand> SlashCommands => CodexSlashCommands.All;

    public string ModelFlag => "";

    public IReadOnlyList<AgentModelOption> KnownModels => [];

    public string? ReadConfiguredDefaultModel() => null;

    public string ResolveExecutable(string? configuredPath)
    {
        var configured = string.IsNullOrWhiteSpace(configuredPath) ? "codex" : configuredPath.Trim();
        var resolved = ExecutableResolver.Resolve(configured);
        if (resolved is not null)
        {
            FileLog.Write($"[CodexDriver] ResolveExecutable: resolved '{configured}' to '{resolved}'");
            return resolved;
        }

        throw new InvalidOperationException(
            $"[CodexDriver] Could not resolve the Codex CLI from '{configured}'. " +
            "Install Codex or set the Codex path in Settings > Agents.");
    }

    public AgentLaunchSpec BuildLaunchSpec(string? baseArgs, string? resumeSessionId)
    {
        FileLog.Write($"[CodexDriver] BuildLaunchSpec: baseArgs={baseArgs ?? "(null)"}, resume={resumeSessionId ?? "(null)"}");
        if (!string.IsNullOrEmpty(resumeSessionId))
            FileLog.Write($"[CodexDriver] BuildLaunchSpec: ignoring resume={resumeSessionId} (Codex does not support Director-preassigned resume in this integration).");

        var args = (baseArgs ?? string.Empty).Trim();
        FileLog.Write($"[CodexDriver] BuildLaunchSpec result: argsLen={args.Length}");
        return new AgentLaunchSpec(args, PreassignedSessionId: null);
    }

    /// <summary>
    /// Echo-verified submit: type the text, wait until the composer echoes it, then a SEPARATE
    /// Enter. Codex's composer repaints a cycling placeholder, so a blind text+Enter is dropped when
    /// driven programmatically (the prompt parks unsubmitted). Confirmed live on codex 0.141.0. The
    /// full explanation and the shared implementation live in <see cref="TerminalSubmit"/>.
    /// </summary>
    public Task SubmitAsync(ISessionBackend backend, string text) =>
        TerminalSubmit.SharedSubmitAsync(backend, text, "CodexDriver");

    public Task CancelAsync(ISessionBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        FileLog.Write("[CodexDriver] CancelAsync: sending Esc");
        backend.Write(EscapeByte);
        return Task.CompletedTask;
    }

    public Task InterruptAsync(ISessionBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        FileLog.Write("[CodexDriver] InterruptAsync: sending Ctrl+C");
        backend.Write(CtrlC);
        return Task.CompletedTask;
    }

    public Task ShowHistoryAsync(ISessionBackend backend) =>
        throw new NotSupportedException(
            "[CodexDriver] Codex history is surfaced through the Director history tab from rollout files, " +
            "not through a verified in-terminal history picker.");

    public Task ClearContextAsync(ISessionBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        FileLog.Write("[CodexDriver] ClearContextAsync: submitting /clear");
        return backend.SendTextAsync("/clear");
    }

    /// <summary>Codex's in-place summarize: the /compact command its own catalog documents.</summary>
    public Task CompactContextAsync(ISessionBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        FileLog.Write("[CodexDriver] CompactContextAsync: submitting /compact");
        return backend.SendTextAsync("/compact");
    }

    public List<TurnWidgetDto> ReadWidgets(string agentSessionId, string workingDirectory) =>
        throw new NotSupportedException(
            "[CodexDriver] Codex rollout history is exposed through SessionHistoryReader; " +
            "driver widget conversion is not implemented.");

    public SessionUsageDto? ReadUsage(string agentSessionId, string workingDirectory) =>
        throw new NotSupportedException("[CodexDriver] Codex token usage reading is not implemented.");

    /// <summary>How full the Codex context window is right now (capability
    /// <see cref="DriverCapabilities.ContextUsage"/>). Codex writes its own <c>token_count</c> event
    /// to the rollout carrying both the used tokens and the model window, so nothing is guessed - the
    /// launch args are not needed. Located by repo path (Codex has no Director-preassigned id).</summary>
    public ContextUsageDto? ReadContextUsage(string agentSessionId, string workingDirectory, string? launchArgs) =>
        Codex.CodexContextUsage.ReadForRepo(workingDirectory);

    /// <summary>The model this Codex session is currently using (capability
    /// <see cref="DriverCapabilities.ModelReport"/>): the LAST <c>turn_context</c> event's model in
    /// the rollout, so a mid-session model switch is reflected. Located by repo path (Codex has no
    /// Director-preassigned id).</summary>
    public string? ReadCurrentModel(string agentSessionId, string workingDirectory, string? launchArgs) =>
        Codex.CodexCurrentModel.ReadForRepo(workingDirectory);

    public List<(string AgentSessionId, DateTime LastWriteUtc)> ListTranscripts(string workingDirectory) =>
        throw new NotSupportedException(
            "[CodexDriver] Codex rollout discovery is owned by CodexRolloutLocator/SessionHistoryReader.");
}
