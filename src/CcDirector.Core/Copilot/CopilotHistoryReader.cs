using CcDirector.Core.History;
using CcDirector.Core.Utilities;
using Microsoft.Data.Sqlite;

namespace CcDirector.Core.Copilot;

/// <summary>
/// Reads a GitHub Copilot CLI session's conversation from Copilot's local SQLite store
/// (<c>~/.copilot/session-store.db</c>) and maps it into the agent-agnostic
/// <see cref="ConversationHistory"/>.
///
/// Copilot runs full screen, so its terminal scrollback is empty; the SQLite store is the
/// only source of the conversation. The store keeps a clean <c>turns</c> table - each row
/// already carries the user message and the assistant response for one turn - so the mapping
/// is direct. Tool calls live in <c>forge_trajectory_events</c>, linked to a turn by
/// <c>turn_index</c>.
///
/// The store is written by a live Copilot process (a <c>-wal</c> file is present), so reads
/// go through <see cref="SqliteSnapshotReader"/>, which copies the database before opening it
/// and never locks the writer.
///
/// There is no per-session file to locate: the active session is resolved each read as the
/// newest <c>sessions</c> row whose <c>cwd</c> matches the Director session's repository path.
/// </summary>
public static class CopilotHistoryReader
{
    /// <summary>The Copilot CLI session store for the current user, or null if it is absent.</summary>
    public static string? DefaultDatabasePath
    {
        get
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".copilot",
                "session-store.db");
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>
    /// Read the newest Copilot conversation for <paramref name="repoPath"/> from the default
    /// store, or <see cref="ConversationHistory.Empty"/> if the store is absent or has no
    /// session for that repository.
    /// </summary>
    public static ConversationHistory Read(string repoPath)
    {
        var databasePath = DefaultDatabasePath;
        if (databasePath is null)
            return ConversationHistory.Empty;

        return ReadFrom(repoPath, databasePath);
    }

    /// <summary>
    /// Read the newest Copilot conversation for <paramref name="repoPath"/> from the store at
    /// <paramref name="databasePath"/>. Exposed (with an explicit database path) for testing so
    /// it never has to touch the user profile.
    /// </summary>
    public static ConversationHistory ReadFrom(string repoPath, string databasePath)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
            return ConversationHistory.Empty;

        try
        {
            return SqliteSnapshotReader.Read(databasePath, connection => ReadFromConnection(connection, repoPath));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CopilotHistoryReader] Read error for {databasePath}: {ex.Message}");
            return ConversationHistory.Empty;
        }
    }

    private static ConversationHistory ReadFromConnection(SqliteConnection connection, string repoPath)
    {
        var sessionId = ResolveSessionId(connection, repoPath);
        if (sessionId is null)
            return ConversationHistory.Empty;

        // Read tool events defensively. forge_trajectory_events is unused by the current Copilot
        // version and may be dropped or reshaped in a future release; if that query throws we still
        // want the conversation (the turns) rather than collapsing the whole history to Empty. So a
        // tool-schema change degrades to "turns without tool structure" and is logged, not fatal.
        Dictionary<int, List<ToolEvent>> toolEventsByTurn;
        try
        {
            toolEventsByTurn = ReadToolEvents(connection, sessionId);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CopilotHistoryReader] tool-events read failed (rendering turns without tools): {ex.Message}");
            toolEventsByTurn = new Dictionary<int, List<ToolEvent>>();
        }

        var messages = new List<ConversationMessage>();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT turn_index, user_message, assistant_response, timestamp " +
            "FROM turns WHERE session_id = @sid ORDER BY turn_index";
        command.Parameters.AddWithValue("@sid", sessionId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var turnIndex = reader.GetInt32(0);
            var userMessage = GetNullableString(reader, 1);
            var assistantResponse = GetNullableString(reader, 2);
            var timestamp = ParseTimestamp(GetNullableString(reader, 3));

            if (!string.IsNullOrEmpty(userMessage))
            {
                messages.Add(new ConversationMessage(
                    ConversationRole.User,
                    new[] { new ConversationPart(ConversationPartKind.Text, userMessage) },
                    timestamp));
            }

            var assistantParts = new List<ConversationPart>();

            // Tool calls happen during the turn, before the assistant's final text, so they
            // come first in the assistant message.
            if (toolEventsByTurn.TryGetValue(turnIndex, out var events))
            {
                foreach (var toolEvent in events)
                    assistantParts.AddRange(toolEvent.ToParts());
            }

            if (!string.IsNullOrEmpty(assistantResponse))
                assistantParts.Add(new ConversationPart(ConversationPartKind.Text, assistantResponse));

            if (assistantParts.Count > 0)
                messages.Add(new ConversationMessage(ConversationRole.Assistant, assistantParts, timestamp));
        }

        return messages.Count == 0 ? ConversationHistory.Empty : new ConversationHistory(messages);
    }

    /// <summary>
    /// The active session is the newest <c>sessions</c> row whose <c>cwd</c> matches the repo.
    /// We compare normalized paths in code (rather than with SQL equality) so trailing
    /// separators and casing do not cause a miss - Copilot stores native Windows paths.
    /// </summary>
    private static string? ResolveSessionId(SqliteConnection connection, string repoPath)
    {
        var target = NormalizePath(repoPath);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, cwd FROM sessions ORDER BY updated_at DESC";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var cwd = GetNullableString(reader, 1);
            if (cwd != null && NormalizePath(cwd) == target)
                return reader.GetString(0);
        }
        return null;
    }

    private static Dictionary<int, List<ToolEvent>> ReadToolEvents(SqliteConnection connection, string sessionId)
    {
        var byTurn = new Dictionary<int, List<ToolEvent>>();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT turn_index, tool_call_id, event_type, command, output " +
            "FROM forge_trajectory_events WHERE session_id = @sid ORDER BY turn_index, id";
        command.Parameters.AddWithValue("@sid", sessionId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0))
                continue; // an event not tied to a turn cannot be placed in the thread
            var turnIndex = reader.GetInt32(0);
            var toolEvent = new ToolEvent(
                ToolCallId: GetNullableString(reader, 1),
                EventType: GetNullableString(reader, 2),
                Command: GetNullableString(reader, 3),
                Output: GetNullableString(reader, 4));

            if (!byTurn.TryGetValue(turnIndex, out var list))
            {
                list = new List<ToolEvent>();
                byTurn[turnIndex] = list;
            }
            list.Add(toolEvent);
        }
        return byTurn;
    }

    /// <summary>
    /// One <c>forge_trajectory_events</c> row. A row carries both the issued command and its
    /// output, so it can yield a tool-use part, a tool-result part, or both.
    /// </summary>
    private sealed record ToolEvent(string? ToolCallId, string? EventType, string? Command, string? Output)
    {
        public IEnumerable<ConversationPart> ToParts()
        {
            if (!string.IsNullOrEmpty(Command))
            {
                var toolName = string.IsNullOrEmpty(EventType) ? "tool" : EventType;
                yield return new ConversationPart(ConversationPartKind.ToolUse, Command, toolName, ToolCallId);
            }

            if (!string.IsNullOrEmpty(Output))
                yield return new ConversationPart(ConversationPartKind.ToolResult, Output, null, ToolCallId);
        }
    }

    private static string? GetNullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? ParseTimestamp(string? value)
        => value != null && DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\', '/').ToLowerInvariant(); }
        catch { return path.TrimEnd('\\', '/').ToLowerInvariant(); }
    }
}
