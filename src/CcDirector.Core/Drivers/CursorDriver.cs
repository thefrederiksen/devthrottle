using System.Text;
using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Drivers;

/// <summary>
/// The driver for Cursor's CLI agent (<c>cursor-agent</c>, issue #517 phase 2).
///
/// Capability honesty (the law in <see cref="GenericDriver"/>, AC11): this driver
/// advertises ONLY what has a known-safe terminal contract. Ctrl+C is the universal
/// terminal hard-interrupt, so <see cref="DriverCapabilities.Interrupt"/> is declared.
/// Cursor's interactive soft-cancel / context-reset / history keystrokes are NOT
/// documented and have NOT been live-verified (assumption A4), so those verbs stay
/// unsupported and throw <see cref="NotSupportedException"/> rather than guessing a
/// keystroke that might quit the agent or do nothing.
///
/// Cursor's on-disk chat transcript location/format is explicitly OUT of scope until
/// verified in a follow-up (issue #517 OUT list), so <see cref="DriverCapabilities.TranscriptRead"/>
/// is NOT declared and the transcript-read verbs throw. The STREAM parsing the Director
/// does live (<see cref="ParseStreamLine"/>, <see cref="TryCaptureSessionId"/>) reads
/// cursor-agent's stdout stream-json events, not an on-disk file.
/// </summary>
public sealed class CursorDriver : IAgentDriver
{
    private static readonly byte[] CtrlC = [0x03];

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
    };

    public AgentKind Kind => AgentKind.Cursor;

    /// <summary>
    /// Only Ctrl+C (Interrupt) is verified. Soft-cancel, clear-context, history, and
    /// transcript-read are deliberately absent until live-verified (assumptions A4 and
    /// the transcript OUT-of-scope note).
    /// </summary>
    public DriverCapabilities Capabilities => DriverCapabilities.Interrupt;

    public IReadOnlyList<AgentSlashCommand> SlashCommands => CursorSlashCommands.All;

    public string ResolveExecutable(string? configuredPath) =>
        throw new NotSupportedException(
            "[CursorDriver] Executable resolution is owned by the Director's CursorAgent path; " +
            "hosting cursor-agent headless requires capabilities not yet verified.");

    public AgentLaunchSpec BuildLaunchSpec(string? baseArgs, string? resumeSessionId) =>
        throw new NotSupportedException(
            "[CursorDriver] Launch specs are owned by the Director's CursorAgent path.");

    public Task SubmitAsync(ISessionBackend backend, string text)
    {
        ArgumentNullException.ThrowIfNull(backend);
        // Blind submit: cursor-agent's composer echo layout is unverified, so no echo gate
        // yet (same conservative contract as GenericDriver).
        return backend.SendTextAsync(text);
    }

    public Task CancelAsync(ISessionBackend backend) =>
        throw new NotSupportedException(
            "[CursorDriver] cursor-agent's soft-cancel keystroke is not yet live-verified " +
            "(issue #517 assumption A4). Use InterruptAsync (Ctrl+C) until it is confirmed.");

    public Task InterruptAsync(ISessionBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        FileLog.Write("[CursorDriver] InterruptAsync: sending Ctrl+C");
        backend.Write(CtrlC);
        return Task.CompletedTask;
    }

    public Task ShowHistoryAsync(ISessionBackend backend) =>
        throw new NotSupportedException(
            "[CursorDriver] cursor-agent has no verified in-terminal history picker.");

    public Task ClearContextAsync(ISessionBackend backend) =>
        throw new NotSupportedException(
            "[CursorDriver] cursor-agent has no verified in-place context-clear command.");

    public List<TurnWidgetDto> ReadWidgets(string agentSessionId, string workingDirectory) =>
        throw new NotSupportedException(
            "[CursorDriver] cursor-agent's on-disk transcript location/format is not yet " +
            "verified (issue #517 OUT of scope); only live stream-json parsing is supported.");

    public SessionUsageDto? ReadUsage(string agentSessionId, string workingDirectory) =>
        throw new NotSupportedException(
            "[CursorDriver] cursor-agent's on-disk transcript location/format is not yet verified.");

    public List<(string AgentSessionId, DateTime LastWriteUtc)> ListTranscripts(string workingDirectory) =>
        throw new NotSupportedException(
            "[CursorDriver] cursor-agent's on-disk transcript location/format is not yet verified.");

    // ---------------------------------------------------------------- stream-json

    /// <summary>
    /// Capture Cursor's self-minted session id from a stream-json line. Cursor cannot
    /// preassign an id; it emits one in the <c>system</c>/<c>init</c> event's
    /// <c>session_id</c> field (assumption A3, AC10). Returns the id when this line is
    /// the init event carrying one, else null. Non-JSON and unrelated lines return null
    /// without throwing so the caller can feed it the raw stdout stream.
    /// </summary>
    public static string? TryCaptureSessionId(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith('{'))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(trimmed, JsonOptions);
            var root = doc.RootElement;

            // Cursor's init event is type "system" with subtype "init"; some builds emit
            // type "init" directly. Accept either, and require a non-empty session_id.
            var type = GetString(root, "type");
            var subtype = GetString(root, "subtype");
            var isInit = string.Equals(type, "init", StringComparison.OrdinalIgnoreCase)
                || (string.Equals(type, "system", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(subtype, "init", StringComparison.OrdinalIgnoreCase));
            if (!isInit)
                return null;

            var sessionId = GetString(root, "session_id");
            if (string.IsNullOrWhiteSpace(sessionId))
                return null;

            FileLog.Write($"[CursorDriver] TryCaptureSessionId: captured session_id={sessionId}");
            return sessionId;
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[CursorDriver] TryCaptureSessionId: invalid JSON: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Parse one stream-json line emitted by cursor-agent into a <see cref="TurnWidgetDto"/>
    /// card, or null for lines that carry no displayable widget (init/result envelopes,
    /// non-JSON, blank). Handles Cursor's event shapes (assumption A3): <c>assistant</c>
    /// text deltas, <c>tool_call</c> started/completed, and the final <c>result</c>.
    /// Defensive: unknown shapes and bad JSON return null instead of throwing, so a noisy
    /// stdout stream never crashes the reader.
    /// </summary>
    public static TurnWidgetDto? ParseStreamLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith('{'))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(trimmed, JsonOptions);
            var root = doc.RootElement;
            var type = GetString(root, "type");

            return type switch
            {
                "assistant" => BuildAssistantWidget(root),
                "tool_call" => BuildToolCallWidget(root),
                "result" => BuildResultWidget(root),
                _ => null,
            };
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[CursorDriver] ParseStreamLine: invalid JSON: {ex.Message}");
            return null;
        }
    }

    private static TurnWidgetDto? BuildAssistantWidget(JsonElement root)
    {
        var text = ExtractMessageText(root);
        if (string.IsNullOrEmpty(text))
            return null;

        return new TurnWidgetDto
        {
            Kind = "Text",
            Header = "Cursor",
            Content = text,
        };
    }

    private static TurnWidgetDto? BuildToolCallWidget(JsonElement root)
    {
        var subtype = GetString(root, "subtype");
        var toolName = GetString(root, "tool")
            ?? GetString(root, "name")
            ?? "Tool";
        var toolUseId = GetString(root, "tool_call_id") ?? GetString(root, "id") ?? "";
        var isCompleted = string.Equals(subtype, "completed", StringComparison.OrdinalIgnoreCase);

        return new TurnWidgetDto
        {
            Kind = "GenericTool",
            Header = toolName,
            Subheader = subtype,
            Content = GetString(root, "command") ?? GetString(root, "input") ?? "",
            Result = isCompleted ? (GetString(root, "result") ?? GetString(root, "output") ?? "") : "",
            IsPending = !isCompleted,
            ToolUseId = toolUseId,
        };
    }

    private static TurnWidgetDto? BuildResultWidget(JsonElement root)
    {
        var text = GetString(root, "result") ?? GetString(root, "text") ?? "";
        var isError = root.TryGetProperty("is_error", out var err)
            && err.ValueKind == JsonValueKind.True;

        return new TurnWidgetDto
        {
            Kind = "Text",
            Header = "Cursor",
            Content = text,
            IsError = isError,
        };
    }

    /// <summary>
    /// Extract assistant text from Cursor's <c>message</c> envelope. Cursor follows the
    /// Anthropic-style shape: <c>message.content</c> is an array of blocks, each with a
    /// <c>text</c>. A flat <c>text</c> property is also accepted as a fallback shape.
    /// </summary>
    private static string ExtractMessageText(JsonElement root)
    {
        if (root.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object
                    && block.TryGetProperty("text", out var blockText)
                    && blockText.ValueKind == JsonValueKind.String)
                {
                    sb.Append(blockText.GetString());
                }
            }
            return sb.ToString();
        }

        return GetString(root, "text") ?? "";
    }

    private static string? GetString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
