using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Drivers;

/// <summary>
/// The driver for agent CLIs we have NOT live-verified yet (Codex, Gemini, OpenCode,
/// ...). It reproduces exactly what the Director did for every session before the
/// driver layer existed: blind submit via the backend's typing semantics, Esc for the
/// soft stop, Ctrl+C for the hard interrupt. Nothing else is declared - context
/// clear, history, and transcript access throw NotSupported until a tool-specific
/// driver is written and verified (docs/plans/agent-driver.md).
///
/// This is NOT a fallback that hides differences: it is the explicit statement that
/// "for this tool, these two keystrokes are all we have verified."
/// </summary>
public sealed class GenericDriver : IAgentDriver
{
    private static readonly byte[] EscapeByte = [0x1B];
    private static readonly byte[] CtrlC = [0x03];

    private readonly IReadOnlyList<AgentSlashCommand> _slashCommands;

    public GenericDriver(
        AgentKind kind,
        IReadOnlyList<AgentSlashCommand>? slashCommands = null,
        bool emitsContinuousIdleOutput = false)
    {
        Kind = kind;
        _slashCommands = slashCommands ?? [];
        EmitsContinuousIdleOutput = emitsContinuousIdleOutput;
    }

    public AgentKind Kind { get; }

    public DriverCapabilities Capabilities =>
        DriverCapabilities.Cancel | DriverCapabilities.Interrupt;

    /// <summary>
    /// Set true for Grok: its idle terminal keeps repainting an animated footer (spinner +
    /// shortcuts + synchronized-output heartbeat), so the byte-only idle rule never fires.
    /// See <see cref="IAgentDriver.EmitsContinuousIdleOutput"/>.
    /// </summary>
    public bool EmitsContinuousIdleOutput { get; }

    public IReadOnlyList<AgentSlashCommand> SlashCommands => _slashCommands;

    // Unverified tools declare no model flag: model selection stays hidden until a tool-specific
    // driver is written and verified (same conservative contract as the other capabilities here).
    public string ModelFlag => "";
    public IReadOnlyList<AgentModelOption> KnownModels => [];
    public string? ReadConfiguredDefaultModel() => null;

    public string ResolveExecutable(string? configuredPath) =>
        throw new NotSupportedException(
            $"[GenericDriver] Executable resolution for {Kind} is not implemented - launching is " +
            "owned by the Director's IAgent path; hosting requires a verified driver.");

    public AgentLaunchSpec BuildLaunchSpec(string? baseArgs, string? resumeSessionId) =>
        throw new NotSupportedException(
            $"[GenericDriver] Launch specs for {Kind} are owned by the Director's IAgent path; " +
            "hosting requires a verified driver.");

    public Task SubmitAsync(ISessionBackend backend, string text)
    {
        ArgumentNullException.ThrowIfNull(backend);
        // Pre-driver behavior, byte for byte: the backend's typing semantics, no echo
        // verification (echo layouts are tool-specific and unverified for this CLI).
        return backend.SendTextAsync(text);
    }

    public Task CancelAsync(ISessionBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        FileLog.Write($"[GenericDriver:{Kind}] CancelAsync: sending Esc");
        backend.Write(EscapeByte);
        return Task.CompletedTask;
    }

    public Task InterruptAsync(ISessionBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        FileLog.Write($"[GenericDriver:{Kind}] InterruptAsync: sending Ctrl+C");
        backend.Write(CtrlC);
        return Task.CompletedTask;
    }

    public Task ShowHistoryAsync(ISessionBackend backend) =>
        throw new NotSupportedException($"[GenericDriver] {Kind} has no verified history picker.");

    public Task ClearContextAsync(ISessionBackend backend) =>
        throw new NotSupportedException($"[GenericDriver] {Kind} has no verified context-clear command.");

    public List<TurnWidgetDto> ReadWidgets(string agentSessionId, string workingDirectory) =>
        throw new NotSupportedException($"[GenericDriver] {Kind} has no verified transcript format.");

    public SessionUsageDto? ReadUsage(string agentSessionId, string workingDirectory) =>
        throw new NotSupportedException($"[GenericDriver] {Kind} has no verified transcript format.");

    public List<(string AgentSessionId, DateTime LastWriteUtc)> ListTranscripts(string workingDirectory) =>
        throw new NotSupportedException($"[GenericDriver] {Kind} has no verified transcript format.");
}
