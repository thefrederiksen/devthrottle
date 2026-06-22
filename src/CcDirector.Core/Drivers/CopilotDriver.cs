using System.Text;
using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Drivers;

/// <summary>
/// The driver for GitHub Copilot's CLI agent (<c>copilot</c>, issue #625 phase 2).
///
/// Capability honesty (the law in <see cref="GenericDriver"/>/<see cref="CursorDriver"/>, AC14):
/// this driver advertises ONLY what has a known-safe, live-verified contract:
/// <list type="bullet">
/// <item><see cref="DriverCapabilities.Interrupt"/> - Ctrl+C is the universal terminal
/// hard-interrupt.</item>
/// <item><see cref="DriverCapabilities.PreassignedSessionId"/> - Copilot accepts a caller-chosen
/// UUID via <c>--session-id</c> (verified: the preassigned id appears in the
/// <c>--output-format json</c> stream).</item>
/// </list>
/// Copilot's interactive soft-cancel keystroke and its in-terminal history picker are NOT
/// documented/verified, so <see cref="DriverCapabilities.Cancel"/> and
/// <see cref="DriverCapabilities.History"/> stay unsupported and throw rather than guess a
/// keystroke that might quit the agent.
///
/// Copilot's on-disk transcript location/format is not yet verified, so
/// <see cref="DriverCapabilities.TranscriptRead"/> is NOT declared and the on-disk transcript
/// verbs (<see cref="ReadWidgets"/>/<see cref="ReadUsage"/>/<see cref="ListTranscripts"/>) throw.
/// The structured parsing the driver DOES do live (<see cref="ParseStreamLine"/>,
/// <see cref="TryCaptureSessionId"/>) reads Copilot's <c>--output-format json</c> JSONL stream,
/// one event object per line - not an on-disk file.
/// </summary>
public sealed class CopilotDriver : IAgentDriver
{
    private static readonly byte[] CtrlC = [0x03];

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
    };

    public AgentKind Kind => AgentKind.Copilot;

    /// <summary>
    /// Only Ctrl+C (Interrupt) and session-id preassignment are verified. Soft-cancel,
    /// clear-context, history, and on-disk transcript-read are deliberately absent until
    /// live-verified.
    /// </summary>
    public DriverCapabilities Capabilities =>
        DriverCapabilities.Interrupt | DriverCapabilities.PreassignedSessionId;

    public IReadOnlyList<AgentSlashCommand> SlashCommands => CopilotSlashCommands.All;

    /// <summary>Copilot takes a model on the command line via <c>--model</c> (issue #625).</summary>
    public string ModelFlag => "--model";

    // The concrete model catalog is not pinned here (Copilot's model list is multi-provider and
    // versions frequently); the Edit Agent picker stays empty and the tool's own default is used
    // unless the user pins one via the args. Model selection is therefore not declared in
    // Capabilities (no curated KnownModels list yet).
    public IReadOnlyList<AgentModelOption> KnownModels => [];
    public string? ReadConfiguredDefaultModel() => null;

    /// <summary>
    /// Resolve the <c>copilot</c> executable: validate an explicit path, or search PATH/PATHEXT
    /// (which yields <c>copilot.cmd</c> on Windows). Throws with install guidance when not found -
    /// no silent fallback.
    /// </summary>
    public string ResolveExecutable(string? configuredPath)
    {
        var configured = string.IsNullOrWhiteSpace(configuredPath) ? "copilot" : configuredPath.Trim();
        var resolved = ExecutableResolver.Resolve(configured);
        if (resolved is not null)
        {
            FileLog.Write($"[CopilotDriver] ResolveExecutable: resolved '{configured}' to '{resolved}'");
            return resolved;
        }

        throw new InvalidOperationException(
            $"[CopilotDriver] Could not resolve the GitHub Copilot CLI from '{configured}'. " +
            "Install it (npm install -g @github/copilot, the gh.io/copilot-install script, " +
            "Homebrew, or WinGet) or set the Copilot path in Settings > Agents. On Windows the " +
            "launchable shim is copilot.cmd.");
    }

    /// <summary>
    /// Build the spawn arguments and the preassigned session id. Copilot preassigns the UUID via
    /// <c>--session-id</c> (AC2/AC11): a new session mints a fresh UUID; a resume passes
    /// <c>--resume &lt;id&gt;</c>.
    /// </summary>
    public AgentLaunchSpec BuildLaunchSpec(string? baseArgs, string? resumeSessionId)
    {
        FileLog.Write($"[CopilotDriver] BuildLaunchSpec: baseArgs={baseArgs ?? "(null)"}, resume={resumeSessionId ?? "(null)"}");

        var args = (baseArgs ?? string.Empty).Trim();
        string? preassignedSessionId = null;

        if (!string.IsNullOrEmpty(resumeSessionId))
        {
            args = $"{args} --resume {resumeSessionId}".Trim();
        }
        else
        {
            preassignedSessionId = Guid.NewGuid().ToString();
            args = $"{args} --session-id {preassignedSessionId}".Trim();
        }

        FileLog.Write($"[CopilotDriver] BuildLaunchSpec result: argsLen={args.Length}, preassignedId={preassignedSessionId ?? "(null)"}");
        return new AgentLaunchSpec(args, preassignedSessionId);
    }

    public Task SubmitAsync(ISessionBackend backend, string text)
    {
        ArgumentNullException.ThrowIfNull(backend);
        // Blind submit: Copilot's composer echo layout is unverified, so no echo gate yet
        // (same conservative contract as GenericDriver/CursorDriver).
        return backend.SendTextAsync(text);
    }

    public Task CancelAsync(ISessionBackend backend) =>
        throw new NotSupportedException(
            "[CopilotDriver] copilot's soft-cancel keystroke is not yet live-verified (issue #625). " +
            "Use InterruptAsync (Ctrl+C) until it is confirmed.");

    public Task InterruptAsync(ISessionBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        FileLog.Write("[CopilotDriver] InterruptAsync: sending Ctrl+C");
        backend.Write(CtrlC);
        return Task.CompletedTask;
    }

    public Task ShowHistoryAsync(ISessionBackend backend) =>
        throw new NotSupportedException(
            "[CopilotDriver] copilot has no verified in-terminal history picker.");

    public Task ClearContextAsync(ISessionBackend backend) =>
        throw new NotSupportedException(
            "[CopilotDriver] copilot has no verified in-place context-clear command.");

    public List<TurnWidgetDto> ReadWidgets(string agentSessionId, string workingDirectory) =>
        throw new NotSupportedException(
            "[CopilotDriver] copilot's on-disk transcript location/format is not yet verified " +
            "(issue #625); only live --output-format json stream parsing is supported.");

    public SessionUsageDto? ReadUsage(string agentSessionId, string workingDirectory) =>
        throw new NotSupportedException(
            "[CopilotDriver] copilot's on-disk transcript location/format is not yet verified.");

    public List<(string AgentSessionId, DateTime LastWriteUtc)> ListTranscripts(string workingDirectory) =>
        throw new NotSupportedException(
            "[CopilotDriver] copilot's on-disk transcript location/format is not yet verified.");

    // ----------------------------------------------------------- --output-format json (JSONL)

    /// <summary>
    /// Capture the session id echoed in a Copilot JSONL line (AC11). Copilot preassigns the id via
    /// <c>--session-id</c>; the value is echoed in events as <c>sessionId</c> (some builds nest it
    /// under <c>session.id</c> or emit a flat <c>session_id</c>). Returns the id when this line
    /// carries one, else null. Non-JSON and unrelated lines return null without throwing so the
    /// caller can feed it the raw stdout stream.
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

            var id = GetString(root, "sessionId")
                ?? GetString(root, "session_id");

            if (id is null
                && root.TryGetProperty("session", out var session)
                && session.ValueKind == JsonValueKind.Object)
            {
                id = GetString(session, "id");
            }

            if (string.IsNullOrWhiteSpace(id))
                return null;

            FileLog.Write($"[CopilotDriver] TryCaptureSessionId: captured sessionId={id}");
            return id;
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[CopilotDriver] TryCaptureSessionId: invalid JSON: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Parse one Copilot <c>--output-format json</c> JSONL line into a <see cref="TurnWidgetDto"/>
    /// card, or null for lines that carry no displayable widget (turn-boundary envelopes,
    /// session.* load events, non-JSON, blank). Handles Copilot's event shapes (AC12): the
    /// terminal <c>assistant.message</c> (and incremental <c>assistant.message_delta</c>) carry
    /// assistant text; <c>user.message</c> is the user's prompt; <c>result</c> is the turn
    /// completion. Defensive: unknown shapes and bad JSON return null instead of throwing, so a
    /// noisy stdout stream never crashes the reader.
    ///
    /// Delta assembly: <c>assistant.message_delta</c> events stream partial text while a turn is in
    /// flight; the terminal <c>assistant.message</c> carries the full assembled text. Both produce a
    /// Text widget here, leaving final de-duplication/replacement to the consumer that tracks the
    /// event <c>id</c> - pinned by the committed JSONL fixture test.
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
                "user.message" => BuildUserWidget(root),
                "assistant.message" => BuildAssistantWidget(root),
                "assistant.message_delta" => BuildAssistantWidget(root),
                "result" => BuildResultWidget(root),
                _ => null,
            };
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[CopilotDriver] ParseStreamLine: invalid JSON: {ex.Message}");
            return null;
        }
    }

    private static TurnWidgetDto? BuildUserWidget(JsonElement root)
    {
        var text = ExtractText(root);
        if (string.IsNullOrEmpty(text))
            return null;

        return new TurnWidgetDto
        {
            Kind = "UserMessage",
            Header = "You",
            Content = text,
        };
    }

    private static TurnWidgetDto? BuildAssistantWidget(JsonElement root)
    {
        var text = ExtractText(root);
        if (string.IsNullOrEmpty(text))
            return null;

        return new TurnWidgetDto
        {
            Kind = "Text",
            Header = "GitHub Copilot",
            Content = text,
        };
    }

    private static TurnWidgetDto? BuildResultWidget(JsonElement root)
    {
        var text = ExtractText(root);
        var isError = root.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True
            || (string.Equals(GetString(root, "status"), "error", StringComparison.OrdinalIgnoreCase));

        // A result with no text is a pure turn-completion marker; nothing to show.
        if (string.IsNullOrEmpty(text) && !isError)
            return null;

        return new TurnWidgetDto
        {
            Kind = "Text",
            Header = "GitHub Copilot",
            Content = text,
            IsError = isError,
        };
    }

    /// <summary>
    /// Extract message text from a Copilot event. Copilot carries text in <c>text</c> (deltas and
    /// the assembled message), <c>delta</c> (incremental chunk), or a Claude-style
    /// <c>message.content[]</c> block array. Accepts whichever shape is present.
    /// </summary>
    private static string ExtractText(JsonElement root)
    {
        var flat = GetString(root, "text") ?? GetString(root, "delta");
        if (!string.IsNullOrEmpty(flat))
            return flat;

        if (root.TryGetProperty("message", out var message))
        {
            if (message.ValueKind == JsonValueKind.String)
                return message.GetString() ?? "";

            if (message.ValueKind == JsonValueKind.Object
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

            if (message.ValueKind == JsonValueKind.Object)
                return GetString(message, "text") ?? "";
        }

        return "";
    }

    private static string? GetString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
