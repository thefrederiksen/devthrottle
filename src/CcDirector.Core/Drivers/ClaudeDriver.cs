using System.Text;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Input;
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
        | DriverCapabilities.TranscriptRead
        | DriverCapabilities.PreassignedSessionId;

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

    /// <summary>Drop ANSI escape sequences (CSI / OSC / two-byte) from a terminal chunk.</summary>
    public static string StripAnsi(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c != '\x1B')
            {
                sb.Append(c);
                continue;
            }
            if (i + 1 >= raw.Length) break;
            var kind = raw[i + 1];
            if (kind == '[')
            {
                // CSI: ESC [ ... final byte 0x40-0x7E
                i += 2;
                while (i < raw.Length && (raw[i] < '\x40' || raw[i] > '\x7E')) i++;
            }
            else if (kind == ']')
            {
                // OSC: ESC ] ... BEL or ESC \
                i += 2;
                while (i < raw.Length && raw[i] != '\a' && raw[i] != '\x1B') i++;
                if (i + 1 < raw.Length && raw[i] == '\x1B') i++;
            }
            else
            {
                i++; // two-byte sequence (ESC + single char)
            }
        }
        return sb.ToString();
    }

    /// <summary>Letters, digits and '/' only - the comparison alphabet for echo checks.</summary>
    public static string NormalizeForEcho(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c) || c == '/')
                sb.Append(c);
        return sb.ToString();
    }

    public Task CancelAsync(ISessionBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        FileLog.Write("[ClaudeDriver] CancelAsync: sending Esc");
        backend.Write(EscapeByte);
        return Task.CompletedTask;
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

    public List<(string AgentSessionId, DateTime LastWriteUtc)> ListTranscripts(string workingDirectory)
        => _transcripts.ListTranscripts(workingDirectory);
}
