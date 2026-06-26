using System.Text.Json;
using System.Text.RegularExpressions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.History;

/// <summary>
/// The transcript-derived "history state" for a session. This is a separate, experimental signal
/// shown next to the live byte-based status badge; it never replaces or writes to the live status.
/// Its unique contribution over the live detector is <see cref="BackgroundRunning"/>: the byte
/// detector sees no foreground output while a background agent or command runs, so it wrongly drifts
/// to "needs you", whereas the transcript records the full background lifecycle and knows better.
/// </summary>
public enum HistoryState
{
    /// <summary>No conversation yet, or the session's process has exited.</summary>
    Idle,

    /// <summary>The assistant has work to do: the last turn is a user message, or the last
    /// assistant turn left a tool call still awaiting its result.</summary>
    Working,

    /// <summary>The assistant finished its turn and is waiting for the user.</summary>
    NeedsYou,

    /// <summary>At least one background agent or command launched with run_in_background is still
    /// in flight (no terminal task-notification for it yet) and the session process is alive.</summary>
    BackgroundRunning,
}

/// <summary>
/// The counted background-agent lifecycle for a transcript. In-flight = launches that have not yet
/// received a terminal task-notification (completed / failed / killed).
/// </summary>
/// <param name="LaunchCount">Distinct run_in_background tool-use launches seen.</param>
/// <param name="CompletedCount">Distinct launches whose terminal status was completed.</param>
/// <param name="FailedCount">Distinct launches whose terminal status was failed.</param>
/// <param name="KilledCount">Distinct launches whose terminal status was killed (user cancelled).</param>
/// <param name="InFlightCount">Launches with no terminal notification yet.</param>
/// <param name="InFlightToolUseIds">The tool-use ids still in flight, for diagnostics.</param>
public sealed record BackgroundAgentTally(
    int LaunchCount,
    int CompletedCount,
    int FailedCount,
    int KilledCount,
    int InFlightCount,
    IReadOnlyList<string> InFlightToolUseIds)
{
    /// <summary>An empty tally (no background launches).</summary>
    public static BackgroundAgentTally Empty { get; } =
        new(0, 0, 0, 0, 0, Array.Empty<string>());
}

/// <summary>
/// The full transcript analysis used to derive a <see cref="HistoryState"/>: the background-agent
/// tally plus the minimal "last turn" facts (who spoke last, and whether the last assistant turn
/// has an unanswered tool call). All facts are read straight from the transcript - no model needed.
/// </summary>
/// <param name="Background">The background-agent lifecycle tally.</param>
/// <param name="HasMessages">True if the transcript has at least one conversational message.</param>
/// <param name="LastRole">The role of the last conversational message, or null if none.</param>
/// <param name="LastAssistantHasPendingTool">True when the last conversational message is an
/// assistant turn that issued a tool call with no matching tool_result anywhere in the transcript
/// (the assistant is mid-work, waiting on a tool).</param>
public sealed record HistoryAnalysis(
    BackgroundAgentTally Background,
    bool HasMessages,
    ConversationRole? LastRole,
    bool LastAssistantHasPendingTool)
{
    /// <summary>An empty analysis (no transcript content).</summary>
    public static HistoryAnalysis Empty { get; } =
        new(BackgroundAgentTally.Empty, false, null, false);
}

/// <summary>
/// Pure, transcript-derived history-state derivation, placed in CcDirector.Core so the desktop
/// History tab and the Cockpit web surface reuse the exact same logic rather than duplicating it.
///
/// Background-agent lifecycle (Claude transcript, no model needed):
/// - Launch: an assistant tool_use carrying <c>input.run_in_background: true</c> (a <c>toolu_</c> id).
///   Both background agents (the Agent/Task tool) and background shell commands launch this way, and
///   both share the identical notification lifecycle below, so both are counted.
/// - Terminal: a later <c>&lt;task-notification&gt;</c> block carrying the same <c>&lt;tool-use-id&gt;</c>
///   with a <c>&lt;status&gt;</c> of <c>completed</c>, <c>failed</c>, or <c>killed</c>. Interim
///   <c>running</c> heartbeats also appear and are ignored. Notifications can be re-injected, so
///   terminal ids are deduplicated.
///
/// In-flight background work = run_in_background launches minus the launches that reached a terminal
/// status. The <see cref="Derive"/> guard ensures a session whose process has exited is never
/// reported as <see cref="HistoryState.BackgroundRunning"/>, so the count cannot get stuck.
/// </summary>
public static class HistoryStateDeriver
{
    // Terminal task-notification statuses. "running" heartbeats are deliberately excluded.
    private static readonly HashSet<string> TerminalStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "completed", "failed", "killed" };

    // 100ms ceiling guards against pathological backtracking on a malformed line.
    private static readonly Regex TaskNotificationRegex = new(
        @"<task-notification>(?<body>.*?)</task-notification>",
        RegexOptions.Compiled | RegexOptions.Singleline,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex ToolUseIdRegex = new(
        @"<tool-use-id>\s*(?<id>[^<\s]+)\s*</tool-use-id>",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private static readonly Regex StatusRegex = new(
        @"<status>\s*(?<status>[^<\s]+)\s*</status>",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Analyze a Claude transcript .jsonl file. Returns <see cref="HistoryAnalysis.Empty"/> when the
    /// path is missing or unreadable. Reads with FileShare.ReadWrite because Claude appends live.
    /// </summary>
    public static HistoryAnalysis AnalyzeFile(string? jsonlPath)
    {
        if (string.IsNullOrWhiteSpace(jsonlPath) || !File.Exists(jsonlPath))
            return HistoryAnalysis.Empty;

        try
        {
            using var fs = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            return Analyze(reader.ReadToEnd());
        }
        catch (Exception ex)
        {
            FileLog.Write($"[HistoryStateDeriver] AnalyzeFile error for {jsonlPath}: {ex.Message}");
            return HistoryAnalysis.Empty;
        }
    }

    /// <summary>
    /// Analyze the raw text of a Claude transcript (newline-delimited JSON). Pure: no I/O, no model.
    /// </summary>
    public static HistoryAnalysis Analyze(string? transcriptJsonl)
    {
        if (string.IsNullOrWhiteSpace(transcriptJsonl))
            return HistoryAnalysis.Empty;

        // Distinct launch ids (a launch may, in theory, be replayed). Insertion order is preserved
        // so the in-flight list reads in launch order.
        var launchIds = new List<string>();
        var launchSet = new HashSet<string>(StringComparer.Ordinal);
        var terminalIds = new Dictionary<string, string>(StringComparer.Ordinal); // id -> terminal status
        var toolResultIds = new HashSet<string>(StringComparer.Ordinal);

        bool hasMessages = false;
        ConversationRole? lastRole = null;
        var lastAssistantToolUseIds = new List<string>();
        bool lastWasAssistant = false;

        using var lineReader = new StringReader(transcriptJsonl);
        string? line;
        while ((line = lineReader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            // 1. Terminal task-notifications can appear anywhere in a line's raw text (injected as a
            //    system reminder or inside a tool_result), so scan the raw line independent of JSON
            //    structure. Dedupe by tool-use-id; only terminal statuses count.
            ScanTaskNotifications(line, terminalIds);

            // 2. JSON-structured facts: launches, tool_result ids, last-turn shape.
            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(line); }
            catch { /* tolerate a partially written final line */ }
            if (doc is null)
                continue;

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    continue;

                var type = GetString(root, "type");
                if (type != "user" && type != "assistant")
                    continue;

                // Skip subagent sidechains - nested Task-tool conversations, not the main thread.
                if (root.TryGetProperty("isSidechain", out var sc) && sc.ValueKind == JsonValueKind.True)
                    continue;

                if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                    continue;
                if (!message.TryGetProperty("content", out var content))
                    continue;

                bool isAssistant = type == "assistant";
                var thisToolUseIds = new List<string>();
                bool sawAnyContent = false;

                if (content.ValueKind == JsonValueKind.String)
                {
                    sawAnyContent = (content.GetString() ?? string.Empty).Length > 0;
                }
                else if (content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in content.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object)
                            continue;
                        sawAnyContent = true;
                        var itemType = GetString(item, "type");

                        if (itemType == "tool_use")
                        {
                            var id = GetString(item, "id");
                            if (!string.IsNullOrEmpty(id))
                            {
                                thisToolUseIds.Add(id);
                                if (IsRunInBackground(item) && launchSet.Add(id))
                                    launchIds.Add(id);
                            }
                        }
                        else if (itemType == "tool_result")
                        {
                            var resId = GetString(item, "tool_use_id");
                            if (!string.IsNullOrEmpty(resId))
                                toolResultIds.Add(resId);
                        }
                    }
                }

                if (!sawAnyContent)
                    continue;

                hasMessages = true;
                lastRole = isAssistant ? ConversationRole.Assistant : ConversationRole.User;
                lastWasAssistant = isAssistant;
                if (isAssistant)
                {
                    lastAssistantToolUseIds.Clear();
                    lastAssistantToolUseIds.AddRange(thisToolUseIds);
                }
            }
        }

        // In-flight = launches not yet resolved by a terminal notification.
        var inFlight = launchIds.Where(id => !terminalIds.ContainsKey(id)).ToList();
        int completed = terminalIds.Values.Count(s => s.Equals("completed", StringComparison.OrdinalIgnoreCase));
        int failed = terminalIds.Values.Count(s => s.Equals("failed", StringComparison.OrdinalIgnoreCase));
        int killed = terminalIds.Values.Count(s => s.Equals("killed", StringComparison.OrdinalIgnoreCase));

        var tally = new BackgroundAgentTally(
            LaunchCount: launchIds.Count,
            CompletedCount: completed,
            FailedCount: failed,
            KilledCount: killed,
            InFlightCount: inFlight.Count,
            InFlightToolUseIds: inFlight);

        bool lastAssistantPending = lastWasAssistant
            && lastAssistantToolUseIds.Any(id => !toolResultIds.Contains(id));

        return new HistoryAnalysis(tally, hasMessages, lastRole, lastAssistantPending);
    }

    /// <summary>
    /// Derive the displayed history state from a transcript analysis and whether the session's
    /// process is alive. The liveness flag is the stuck-state guard: a dead process is never
    /// <see cref="HistoryState.BackgroundRunning"/> (and never "working"), so an in-flight count that
    /// never received its terminal notification cannot pin the label on forever.
    /// </summary>
    public static HistoryState Derive(HistoryAnalysis analysis, bool isProcessAlive)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        // Guard: a session whose process has exited is done. Never Background running / Working.
        if (!isProcessAlive)
            return HistoryState.Idle;

        if (analysis.Background.InFlightCount > 0)
            return HistoryState.BackgroundRunning;

        if (!analysis.HasMessages)
            return HistoryState.Idle;

        if (analysis.LastRole == ConversationRole.Assistant)
            return analysis.LastAssistantHasPendingTool ? HistoryState.Working : HistoryState.NeedsYou;

        // Last turn is the user (a real prompt or a fed-back tool result) - the assistant owes a reply.
        return HistoryState.Working;
    }

    /// <summary>Convenience: analyze a file and derive its state in one call.</summary>
    public static HistoryState DeriveFromFile(string? jsonlPath, bool isProcessAlive)
        => Derive(AnalyzeFile(jsonlPath), isProcessAlive);

    private static void ScanTaskNotifications(string line, Dictionary<string, string> terminalIds)
    {
        if (line.IndexOf("<task-notification>", StringComparison.Ordinal) < 0)
            return;

        try
        {
            foreach (Match block in TaskNotificationRegex.Matches(line))
            {
                var body = block.Groups["body"].Value;
                var idMatch = ToolUseIdRegex.Match(body);
                var statusMatch = StatusRegex.Match(body);
                if (!idMatch.Success || !statusMatch.Success)
                    continue;

                var status = statusMatch.Groups["status"].Value;
                if (TerminalStatuses.Contains(status))
                    terminalIds[idMatch.Groups["id"].Value] = status; // last terminal status wins
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological line - ignore its notifications rather than throw.
        }
    }

    private static bool IsRunInBackground(JsonElement toolUse)
    {
        if (!toolUse.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
            return false;
        return input.TryGetProperty("run_in_background", out var flag) && flag.ValueKind == JsonValueKind.True;
    }

    private static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
}
