using System.Text.Json;
using CcDirector.Core.History;
using CcDirector.Core.Utilities;
using Microsoft.Data.Sqlite;

namespace CcDirector.Core.OpenCode;

/// <summary>
/// Reads an OpenCode CLI session's conversation from OpenCode's local SQLite store
/// (<c>~/.local/share/opencode/opencode.db</c>) and maps it into the agent-agnostic
/// <see cref="ConversationHistory"/>.
///
/// OpenCode runs full screen, so its terminal scrollback is empty; the SQLite store is the
/// only source of the conversation. The store keeps a row per turn in <c>message</c> (the
/// <c>data</c> column is a JSON blob whose <c>role</c> is user / assistant) and the turn's
/// content as ordered rows in <c>part</c> (each <c>data</c> column is a JSON blob with a
/// <c>type</c>: <c>text</c>, <c>reasoning</c>, <c>tool</c>, or the turn markers
/// <c>step-start</c> / <c>step-finish</c>).
///
/// A single <c>tool</c> part carries both the call and its result through a <c>state</c>
/// object (<c>state.input</c> is the tool input, <c>state.output</c> the result, <c>state.error</c>
/// a failure), so one OpenCode tool part expands to a ToolUse part followed by a ToolResult part.
///
/// The store is written by a live OpenCode process (a <c>-wal</c> file is present), so reads go
/// through <see cref="SqliteSnapshotReader"/>, which copies the database before opening it and
/// never locks the writer.
///
/// There is no per-session file to locate: the active session is resolved each read as the
/// newest <c>session</c> row whose <c>directory</c> matches the Director session's repository
/// path.
/// </summary>
public static class OpenCodeHistoryReader
{
    /// <summary>The OpenCode CLI session store for the current user, or null if it is absent.</summary>
    public static string? DefaultDatabasePath
    {
        get
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share",
                "opencode",
                "opencode.db");
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>
    /// Read the newest OpenCode conversation for <paramref name="repoPath"/> from the default
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
    /// Read the newest OpenCode conversation for <paramref name="repoPath"/> from the store at
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
            FileLog.Write($"[OpenCodeHistoryReader] Read error for {databasePath}: {ex.Message}");
            return ConversationHistory.Empty;
        }
    }

    private static ConversationHistory ReadFromConnection(SqliteConnection connection, string repoPath)
    {
        var sessionId = ResolveSessionId(connection, repoPath);
        if (sessionId is null)
            return ConversationHistory.Empty;

        var messages = new List<ConversationMessage>();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, time_created, data FROM message WHERE session_id = @sid ORDER BY time_created, id";
        command.Parameters.AddWithValue("@sid", sessionId);

        var rows = new List<(string MessageId, long TimeCreated, string Data)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var messageId = reader.GetString(0);
                var timeCreated = reader.GetInt64(1);
                var data = reader.GetString(2);
                rows.Add((messageId, timeCreated, data));
            }
        }

        foreach (var row in rows)
        {
            var role = ParseRole(row.Data);
            var parts = ReadParts(connection, row.MessageId);
            if (parts.Count == 0)
                continue;

            var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(row.TimeCreated);
            messages.Add(new ConversationMessage(role, parts, timestamp));
        }

        return messages.Count == 0 ? ConversationHistory.Empty : new ConversationHistory(messages);
    }

    /// <summary>
    /// The active session is the newest <c>session</c> row whose <c>directory</c> matches the
    /// repo. We compare normalized paths in code (rather than with SQL equality) so trailing
    /// separators and casing do not cause a miss - OpenCode stores native Windows paths.
    /// </summary>
    private static string? ResolveSessionId(SqliteConnection connection, string repoPath)
    {
        var target = NormalizePath(repoPath);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, directory FROM session ORDER BY time_updated DESC";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var directory = GetNullableString(reader, 1);
            if (directory != null && NormalizePath(directory) == target)
                return reader.GetString(0);
        }
        return null;
    }

    private static List<ConversationPart> ReadParts(SqliteConnection connection, string messageId)
    {
        var parts = new List<ConversationPart>();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT data FROM part WHERE message_id = @mid ORDER BY time_created, id";
        command.Parameters.AddWithValue("@mid", messageId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var data = reader.GetString(0);
            AppendParts(data, parts);
        }
        return parts;
    }

    // Map one OpenCode part JSON blob onto zero or more canonical parts. A tool part yields a
    // ToolUse and (when finished) a ToolResult; text/reasoning yield a single part; the turn
    // markers step-start / step-finish and any unknown type are skipped.
    private static void AppendParts(string data, List<ConversationPart> parts)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(data); }
        catch (Exception ex)
        {
            // Tolerate a malformed blob rather than failing the whole read, but record it so a bad
            // row is diagnosable instead of silently vanishing.
            FileLog.Write($"[OpenCodeHistoryReader] AppendParts: skipping malformed part blob: {ex.Message}");
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return;

            switch (GetString(root, "type"))
            {
                case "text":
                    var text = GetString(root, "text") ?? string.Empty;
                    if (text.Length > 0)
                        parts.Add(new ConversationPart(ConversationPartKind.Text, text));
                    break;

                case "reasoning":
                    var thinking = GetString(root, "text") ?? string.Empty;
                    if (thinking.Length > 0)
                        parts.Add(new ConversationPart(ConversationPartKind.Thinking, thinking));
                    break;

                case "tool":
                    AppendToolParts(root, parts);
                    break;

                // step-start / step-finish are turn markers, not content; anything else is unknown.
                default:
                    break;
            }
        }
    }

    private static void AppendToolParts(JsonElement root, List<ConversationPart> parts)
    {
        var toolName = GetString(root, "tool") ?? "tool";
        var callId = GetString(root, "callID");

        string input = string.Empty;
        string? output = null;
        if (root.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Object)
        {
            if (state.TryGetProperty("input", out var inputEl))
                input = RawOrString(inputEl);

            // A completed tool carries output text; a failed one carries error text. Either is
            // the result the agent saw.
            if (state.TryGetProperty("output", out var outputEl) && outputEl.ValueKind == JsonValueKind.String)
                output = outputEl.GetString();
            else if (state.TryGetProperty("error", out var errorEl) && errorEl.ValueKind == JsonValueKind.String)
                output = errorEl.GetString();
        }

        parts.Add(new ConversationPart(ConversationPartKind.ToolUse, input, toolName, callId));
        if (!string.IsNullOrEmpty(output))
            parts.Add(new ConversationPart(ConversationPartKind.ToolResult, output, null, callId));
    }

    private static ConversationRole ParseRole(string messageData)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(messageData); }
        catch (Exception ex)
        {
            // A malformed message blob cannot tell us the role; default to User but log it, since a
            // silent default here could misclassify an assistant turn as a user turn.
            FileLog.Write($"[OpenCodeHistoryReader] ParseRole: malformed message data, defaulting to User: {ex.Message}");
            return ConversationRole.User;
        }

        using (doc)
        {
            return GetString(doc.RootElement, "role") == "assistant"
                ? ConversationRole.Assistant
                : ConversationRole.User;
        }
    }

    private static string RawOrString(JsonElement el)
        => el.ValueKind == JsonValueKind.String ? el.GetString() ?? string.Empty : el.GetRawText();

    private static string? GetString(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
           ? el.GetString()
           : null;

    private static string? GetNullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\', '/').ToLowerInvariant(); }
        catch { return path.TrimEnd('\\', '/').ToLowerInvariant(); }
    }
}
