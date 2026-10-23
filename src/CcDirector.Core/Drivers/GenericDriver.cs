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

    private readonly IReadOnlyList<AgentSlashCommand> _slashCommands;
    private readonly Func<string, string?>? _currentModelReader;
    private readonly string? _compactCommand;

    /// <param name="compactCommand">
    /// The command THIS tool compacts with, when it has one and we have read it from the tool's own
    /// catalog - "/compact" for grok and opencode, "/compress" for gemini (issue #2150). Supplying it
    /// declares <see cref="DriverCapabilities.CompactContext"/>; leaving it null keeps compaction
    /// honestly absent, which is the right answer for a tool with no such command. It never declares
    /// <see cref="DriverCapabilities.CompactCompletionReport"/>: typing a command is not the same as
    /// being able to observe it finish, and these tools' records are not read here.
    /// </param>
    public GenericDriver(
        AgentKind kind,
        IReadOnlyList<AgentSlashCommand>? slashCommands = null,
        bool emitsContinuousIdleOutput = false,
        Func<string, string?>? currentModelReader = null,
        string? compactCommand = null)
    {
        Kind = kind;
        _slashCommands = slashCommands ?? [];
        EmitsContinuousIdleOutput = emitsContinuousIdleOutput;
        _currentModelReader = currentModelReader;
        _compactCommand = string.IsNullOrWhiteSpace(compactCommand) ? null : compactCommand.Trim();
    }

    public AgentKind Kind { get; }

    public DriverCapabilities Capabilities =>
        DriverCapabilities.Cancel
        | DriverCapabilities.Interrupt
        | (_currentModelReader is not null ? DriverCapabilities.ModelReport : DriverCapabilities.None)
        | (_compactCommand is not null ? DriverCapabilities.CompactContext : DriverCapabilities.None);

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

    public Task SubmitAsync(ISessionBackend backend, string text) =>
        TerminalSubmit.SharedSubmitAsync(backend, text, $"GenericDriver:{Kind}");

    public DriverInput Cancel() => DriverInput.PressKeys(DriverKey.Escape);

    public DriverInput Interrupt() => DriverInput.PressKeys(DriverKey.CtrlC);

    public DriverInput ShowHistory() =>
        throw new NotSupportedException($"[GenericDriver] {Kind} has no verified history picker.");

    public DriverInput ClearContext() =>
        throw new NotSupportedException($"[GenericDriver] {Kind} has no verified context-clear command.");

    /// <summary>
    /// The compaction command this tool was constructed with. A tool constructed WITHOUT one has
    /// no compaction we have read, and says so rather than asking for a plausible guess at its composer.
    /// </summary>
    public DriverInput CompactContext() => _compactCommand is null
        ? throw new NotSupportedException($"[GenericDriver] {Kind} has no known compaction command.")
        : DriverInput.TypeCommand(_compactCommand);

    public List<TurnWidgetDto> ReadWidgets(string agentSessionId, string workingDirectory) =>
        throw new NotSupportedException($"[GenericDriver] {Kind} has no verified transcript format.");

    public SessionUsageDto? ReadUsage(string agentSessionId, string workingDirectory) =>
        throw new NotSupportedException($"[GenericDriver] {Kind} has no verified transcript format.");

    public List<(string AgentSessionId, DateTime LastWriteUtc)> ListTranscripts(string workingDirectory) =>
        throw new NotSupportedException($"[GenericDriver] {Kind} has no verified transcript format.");

    /// <summary>The model the tool is currently using (capability
    /// <see cref="DriverCapabilities.ModelReport"/>), via the per-kind reader wired in
    /// <see cref="AgentDrivers"/> - a live-verified store read keyed by the working directory
    /// (issue #1637). A kind without a wired reader does not declare the capability and throws,
    /// same contract as every other undeclared verb here.</summary>
    public string? ReadCurrentModel(string agentSessionId, string workingDirectory, string? launchArgs)
    {
        if (_currentModelReader is null)
            throw new NotSupportedException(
                $"[GenericDriver] {Kind} has no verified current-model store.");
        return _currentModelReader(workingDirectory);
    }
}
