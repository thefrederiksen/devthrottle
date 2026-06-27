using System.Text;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Input;
using CcDirector.Core.Skills;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Drivers;

/// <summary>
/// The Claude Code driver: every claude.exe-specific keystroke and convention in one
/// place, extracted from what the Director and the HostedAgent already proved live:
///
///   - launch: "--dangerously-skip-permissions --session-id &lt;guid&gt;" (preassigned id
///     means the JSONL transcript path is known from birth; --resume for resumes)
///   - submit: backend.SendTextAsync - carries the TUI settle delay, explicit CR, and
///     the large-input @-temp-file trick (multi-line/huge prompts)
///   - cancel: a single Esc byte (0x1B) - identical to the Director's POST /escape
///   - clear: submit "/clear"; claude starts a NEW internal session id and transcript
///     file, which the host re-discovers via ListTranscripts
///   - transcripts: the ~/.claude/projects JSONL files, read with the same Core
///     parsers the Director uses
/// </summary>
public sealed class ClaudeDriver : IAgentDriver
{
    public const string DefaultArgs = "--dangerously-skip-permissions";

    private static readonly byte[] EscapeByte = [0x1B];
    private static readonly byte[] EnterByte = [0x0D];
    private static readonly byte[] CtrlC = [0x03];

    private readonly ITranscriptReader _transcripts;
    private readonly TimeSpan _echoTimeout;
    private readonly TimeSpan _echoPollInterval;

    public ClaudeDriver(
        ITranscriptReader? transcripts = null,
        TimeSpan? echoTimeout = null,
        TimeSpan? echoPollInterval = null)
    {
        _transcripts = transcripts ?? new ClaudeTranscriptReader();
        _echoTimeout = echoTimeout ?? TimeSpan.FromSeconds(3);
        _echoPollInterval = echoPollInterval ?? TimeSpan.FromMilliseconds(50);
    }

    public AgentKind Kind => AgentKind.ClaudeCode;

    public DriverCapabilities Capabilities =>
        DriverCapabilities.ClearContext
        | DriverCapabilities.Cancel
        | DriverCapabilities.Interrupt
        | DriverCapabilities.History
        | DriverCapabilities.TranscriptRead
        | DriverCapabilities.PreassignedSessionId
        | DriverCapabilities.ModelSelection
        | DriverCapabilities.ContextUsage;

    public IReadOnlyList<AgentSlashCommand> SlashCommands => BuiltInSlashCommands.All
        .Select(command => new AgentSlashCommand(
            command.Name,
            command.Description,
            command.Category,
            command.Source,
            AgentKind.ClaudeCode,
            Documentation: command.Documentation))
        .ToList();

    public string ModelFlag => "--model";

    /// <summary>
    /// The Claude Code models the picker offers. Hard-coded here (not read from the API - there is
    /// no CLI to list models) so the picker always shows sensible choices on a fresh machine. The
    /// "1M context" variants use the <c>[1m]</c> suffix, which is how Claude Code requests the
    /// 1-million-token window - there is no separate flag. "Use the tool's own default" is NOT in
    /// this list: it is the unset-model state, surfaced separately by the picker.
    /// </summary>
    public IReadOnlyList<AgentModelOption> KnownModels =>
    [
        new("opus[1m]", "Opus 4.8 (1M context)", "Most capable; 1-million-token window.", "1M context"),
        new("opus", "Opus 4.8", "Most capable; standard context window."),
        new("sonnet", "Sonnet 4.6", "Balanced speed and intelligence."),
        new("haiku", "Haiku 4.5", "Fastest and lowest cost."),
        new("fable", "Fable 5", "Anthropic's most capable model for the hardest work."),
    ];

    /// <summary>
    /// The configured Claude Code default model, read from <c>~/.claude/settings.json</c>'s
    /// <c>model</c> key. Null when the file/key is absent (the account-tier default applies) or
    /// unreadable. A display hint only - this is never written and never composes the launch line.
    /// </summary>
    public string? ReadConfiguredDefaultModel()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var path = Path.Combine(home, ".claude", "settings.json");
            if (!File.Exists(path))
                return null;

            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path));
            if (node?["model"] is System.Text.Json.Nodes.JsonValue value
                && value.TryGetValue<string>(out var model)
                && !string.IsNullOrWhiteSpace(model))
                return model;
            return null;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ClaudeDriver] ReadConfiguredDefaultModel: could not read settings.json: {ex.Message}");
            return null;
        }
    }

    public string ResolveExecutable(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (!File.Exists(configuredPath))
                throw new FileNotFoundException(
                    $"[ClaudeDriver] claude executable not found at the configured path: {configuredPath}");
            return configuredPath;
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), "claude.exe");
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException(
            "[ClaudeDriver] claude.exe not found on PATH. Install Claude Code or configure an explicit path.");
    }

    public AgentLaunchSpec BuildLaunchSpec(string? baseArgs, string? resumeSessionId)
    {
        var args = string.IsNullOrWhiteSpace(baseArgs) ? DefaultArgs : baseArgs;
        if (!string.IsNullOrEmpty(resumeSessionId))
        {
            FileLog.Write($"[ClaudeDriver] BuildLaunchSpec: resume={resumeSessionId}");
            return new AgentLaunchSpec($"{args} --resume {resumeSessionId}".Trim(), null);
        }

        var preassigned = Guid.NewGuid().ToString();
        FileLog.Write($"[ClaudeDriver] BuildLaunchSpec: preassigned={preassigned}");
        return new AgentLaunchSpec($"{args} --session-id {preassigned}".Trim(), preassigned);
    }

    /// <summary>
    /// ECHO-VERIFIED submit. claude's TUI input handling has a race right after a
    /// turn ends: text typed into a repainting composer can lose its Enter, or worse,
    /// pick up a stray leading "/" that turns the prompt into a bogus slash command
    /// (observed live during the hosted-agent QA: "Unknown command: /Write"). So this
    /// driver never trusts a blind write: type the text WITHOUT Enter, wait until the
    /// composer's echo in the terminal byte stream matches what was typed (and is NOT
    /// "/"-corrupted), and only then press Enter. One Esc-and-retype recovery attempt,
    /// loudly logged; a second mismatch throws.
    ///
    /// Multi-line / huge prompts delegate to the backend's @-temp-file mechanism
    /// unchanged (production-proven by the Director; its echo is the @-reference,
    /// which the backend owns).
    /// </summary>
    public async Task SubmitAsync(ISessionBackend backend, string text)
    {
        ArgumentNullException.ThrowIfNull(backend);

        if (LargeInputHandler.IsLargeInput(text.TrimEnd('\r', '\n')))
        {
            await backend.SendTextAsync(text);
            return;
        }

        var buffer = backend.Buffer
            ?? throw new InvalidOperationException(
                "[ClaudeDriver] SubmitAsync requires a buffering backend (echo verification reads the terminal stream)");

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var cursor = buffer.TotalBytesWritten;
            backend.Write(Encoding.UTF8.GetBytes(text));

            var verdict = await WaitForEchoAsync(buffer, cursor, text);
            if (verdict == EchoVerdict.Match)
            {
                backend.Write(EnterByte);
                return;
            }

            FileLog.Write($"[ClaudeDriver] SubmitAsync: echo {verdict} on attempt {attempt} " +
                          $"(len={text.Length}) - clearing the composer and retyping");
            backend.Write(EscapeByte);                  // Esc clears claude's composer
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }

        throw new InvalidOperationException(
            "[ClaudeDriver] SubmitAsync: the composer echo never matched the typed text after 2 attempts - " +
            "the TUI is not accepting input sanely (a modal like the trust-folder prompt, a picker, or a " +
            $"dead composer). Terminal tail: {TailOf(buffer)}");
    }

    /// <summary>The terminal's recent output (ANSI-stripped, whitespace-compressed) for
    /// diagnosable exceptions - shows WHAT the TUI was displaying instead of a composer.</summary>
    private static string TailOf(Memory.CircularTerminalBuffer buffer)
    {
        var text = StripAnsi(Encoding.UTF8.GetString(buffer.DumpAll()));
        var compact = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return compact.Length <= 400 ? compact : compact[^400..];
    }

    private enum EchoVerdict
    {
        Match,
        Missing,
        SlashCorrupted,
    }

    private async Task<EchoVerdict> WaitForEchoAsync(
        Memory.CircularTerminalBuffer buffer, long cursor, string text)
    {
        // Compare in a normalized space that survives the TUI's wrapping and styling:
        // ANSI sequences stripped, then only letters/digits/slash kept. Slash stays in
        // the alphabet so a corrupting "/" right before the prompt is detectable.
        var needle = NormalizeForEcho(text);
        if (needle.Length == 0)
            return EchoVerdict.Match; // nothing comparable to verify (e.g. punctuation-only)

        var deadline = DateTime.UtcNow + _echoTimeout;
        var verdict = EchoVerdict.Missing;
        while (DateTime.UtcNow < deadline)
        {
            var (bytes, _) = buffer.GetWrittenSince(cursor);
            var hay = NormalizeForEcho(StripAnsi(Encoding.UTF8.GetString(bytes)));
            var idx = hay.LastIndexOf(needle, StringComparison.Ordinal);
            if (idx >= 0)
            {
                if (idx > 0 && hay[idx - 1] == '/' && !text.StartsWith('/'))
                    verdict = EchoVerdict.SlashCorrupted;   // keep polling: a repaint may settle clean
                else
                    return EchoVerdict.Match;
            }
            await Task.Delay(_echoPollInterval);
        }
        return verdict;
    }

    /// <summary>Drop ANSI escape sequences from a terminal chunk (delegates to the shared helper).</summary>
    public static string StripAnsi(string raw) => TerminalSubmit.StripAnsi(raw);

    /// <summary>Letters, digits and '/' only - the echo comparison alphabet (shared helper).</summary>
    public static string NormalizeForEcho(string s) => TerminalSubmit.NormalizeForEcho(s);

    public Task CancelAsync(ISessionBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        FileLog.Write("[ClaudeDriver] CancelAsync: sending Esc");
        backend.Write(EscapeByte);
        return Task.CompletedTask;
    }

    public Task InterruptAsync(ISessionBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        FileLog.Write("[ClaudeDriver] InterruptAsync: sending Ctrl+C");
        backend.Write(CtrlC);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Claude's double-Esc: with an idle composer, two Esc presses open the Rewind
    /// picker ("Restore the code and/or conversation to the point before..."). The gap
    /// between the presses matters and was tuned LIVE: 120ms gets coalesced and no
    /// picker appears; 350ms reliably registers as the deliberate double-press
    /// (Director QA DQ-5, docs/features/director-drivers/QA_REPORT.html).
    /// </summary>
    public async Task ShowHistoryAsync(ISessionBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        FileLog.Write("[ClaudeDriver] ShowHistoryAsync: sending double-Esc (350ms gap)");
        backend.Write(EscapeByte);
        await Task.Delay(TimeSpan.FromMilliseconds(350));
        backend.Write(EscapeByte);
    }

    public Task ClearContextAsync(ISessionBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        FileLog.Write("[ClaudeDriver] ClearContextAsync: submitting /clear");
        return backend.SendTextAsync("/clear");
    }

    public List<TurnWidgetDto> ReadWidgets(string agentSessionId, string workingDirectory)
        => _transcripts.ReadWidgets(agentSessionId, workingDirectory);

    public SessionUsageDto? ReadUsage(string agentSessionId, string workingDirectory)
        => _transcripts.ReadUsage(agentSessionId, workingDirectory);

    /// <summary>
    /// How full the Claude context window is right now (capability
    /// <see cref="DriverCapabilities.ContextUsage"/>). Reuses the existing transcript token walk for
    /// the used-token count, then sizes the window. The AUTHORITATIVE window signal is the launch
    /// model id parsed from <paramref name="launchArgs"/> (e.g. <c>--model opus[1m]</c>): Claude's
    /// transcript records the base model id WITHOUT the <c>[1m]</c> suffix, so a 1-million-token Opus
    /// session below 200k tokens would otherwise be sized against 200k and read far too high (issue
    /// #803). When the launch model is unknown, we fall back to the transcript model with upward
    /// self-correction. Null until the first usage-bearing assistant line exists (no turn yet); when
    /// neither model maps, the window and percent are null (the raw-number fallback).
    /// </summary>
    public ContextUsageDto? ReadContextUsage(string agentSessionId, string workingDirectory, string? launchArgs)
    {
        var usage = _transcripts.ReadUsage(agentSessionId, workingDirectory);
        if (usage is null || usage.AssistantMessageCount == 0)
            return null;

        // Prefer the launched model id (carries [1m]); fall back to the transcript model, which is
        // stripped of [1m] and so needs the observed-size self-correction as its only [1m] signal.
        var launchModelId = ExtractLaunchModelId(launchArgs);
        var window = ClaudeContextWindow.WindowTokensForModel(launchModelId)
                  ?? ClaudeContextWindow.WindowTokensForModel(usage.ContextModel, usage.ContextTokens);
        var percent = window is > 0
            ? Math.Round((double)usage.ContextTokens / window.Value * 100.0, 1)
            : (double?)null;

        return new ContextUsageDto
        {
            UsedTokens = usage.ContextTokens,
            WindowTokens = window,
            PercentUsed = percent,
            AsOfUtc = usage.LastMessageUtc,
        };
    }

    /// <summary>Extracts the value passed after this driver's <see cref="ModelFlag"/> (<c>--model</c>)
    /// from a launch command line, e.g. <c>opus[1m]</c> from
    /// <c>--dangerously-skip-permissions --model opus[1m]</c>. Handles both the space form
    /// (<c>--model opus[1m]</c>) and the equals form (<c>--model=opus[1m]</c>). Returns null when no
    /// model flag is present (the session uses the provider default), driving the transcript
    /// fallback.</summary>
    private string? ExtractLaunchModelId(string? launchArgs)
    {
        if (string.IsNullOrWhiteSpace(launchArgs))
            return null;

        var tokens = launchArgs.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.Equals(ModelFlag, StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Length)
                return tokens[i + 1];
            if (token.StartsWith(ModelFlag + "=", StringComparison.OrdinalIgnoreCase))
                return token[(ModelFlag.Length + 1)..];
        }

        return null;
    }

    public List<(string AgentSessionId, DateTime LastWriteUtc)> ListTranscripts(string workingDirectory)
        => _transcripts.ListTranscripts(workingDirectory);
}
