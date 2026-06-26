using System.Text;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Input;
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
    private static readonly byte[] EnterByte = [0x0D];
    private static readonly byte[] CtrlC = [0x03];

    // Echo-verified submit timing (see SubmitAsync). The composer repaints a cycling
    // placeholder, so we wait for the typed text to appear before pressing Enter.
    private static readonly TimeSpan EchoTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan EchoPollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan EnterSettleDelay = TimeSpan.FromMilliseconds(40);

    public AgentKind Kind => AgentKind.Codex;

    public DriverCapabilities Capabilities =>
        DriverCapabilities.Cancel
        | DriverCapabilities.Interrupt
        | DriverCapabilities.ClearContext;

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
    /// ECHO-VERIFIED submit. Codex's TUI repaints a cycling placeholder in its composer, so the
    /// backend's blind submit (type text, wait 50 ms, send one Enter) drops the Enter when driven
    /// programmatically: the Enter lands mid-repaint and is swallowed, leaving the prompt parked
    /// in the composer unsubmitted (confirmed live on codex 0.141.0 via the REST prompt path).
    /// The desktop UI is unaffected because there the Enter is a separate, later human keystroke.
    /// Mirror the Claude fix: type the text WITHOUT Enter, wait until the composer echoes it back
    /// in the terminal byte stream, then press Enter. One Esc-and-retype recovery; a second miss
    /// throws rather than silently parking the prompt.
    ///
    /// Large / multi-line prompts delegate to the backend's @-temp-file submit path unchanged.
    /// </summary>
    public async Task SubmitAsync(ISessionBackend backend, string text)
    {
        ArgumentNullException.ThrowIfNull(backend);

        if (LargeInputHandler.IsLargeInput(text.TrimEnd('\r', '\n')))
        {
            await backend.SendTextAsync(text);
            return;
        }

        var buffer = backend.Buffer;
        if (buffer is null)
        {
            // No buffering backend (non-PTY transport): nothing to echo-verify against.
            await backend.SendTextAsync(text);
            return;
        }

        // Letters/digits/slash alphabet, ANSI stripped - survives the composer's wrapping/styling.
        var needle = ClaudeDriver.NormalizeForEcho(text);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var cursor = buffer.TotalBytesWritten;
            backend.Write(Encoding.UTF8.GetBytes(text));

            if (needle.Length == 0 || await WaitForComposerEchoAsync(buffer, cursor, needle))
            {
                await Task.Delay(EnterSettleDelay);   // let the repaint settle before Enter
                backend.Write(EnterByte);
                return;
            }

            FileLog.Write($"[CodexDriver] SubmitAsync: composer echo not seen on attempt {attempt} " +
                          $"(len={text.Length}) - clearing the composer and retyping");
            backend.Write(EscapeByte);
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }

        throw new InvalidOperationException(
            "[CodexDriver] SubmitAsync: the Codex composer never echoed the typed text after 2 attempts - " +
            "the TUI is not accepting input (a modal, a picker, or a composer still initializing).");
    }

    /// <summary>Poll the terminal byte stream until the typed text echoes back in the composer.</summary>
    private static async Task<bool> WaitForComposerEchoAsync(
        Memory.CircularTerminalBuffer buffer, long cursor, string needle)
    {
        var deadline = DateTime.UtcNow + EchoTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var (bytes, _) = buffer.GetWrittenSince(cursor);
            var hay = ClaudeDriver.NormalizeForEcho(ClaudeDriver.StripAnsi(Encoding.UTF8.GetString(bytes)));
            if (hay.Contains(needle, StringComparison.Ordinal))
                return true;
            await Task.Delay(EchoPollInterval);
        }
        return false;
    }

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

    public List<TurnWidgetDto> ReadWidgets(string agentSessionId, string workingDirectory) =>
        throw new NotSupportedException(
            "[CodexDriver] Codex rollout history is exposed through SessionHistoryReader; " +
            "driver widget conversion is not implemented.");

    public SessionUsageDto? ReadUsage(string agentSessionId, string workingDirectory) =>
        throw new NotSupportedException("[CodexDriver] Codex token usage reading is not implemented.");

    public List<(string AgentSessionId, DateTime LastWriteUtc)> ListTranscripts(string workingDirectory) =>
        throw new NotSupportedException(
            "[CodexDriver] Codex rollout discovery is owned by CodexRolloutLocator/SessionHistoryReader.");
}
