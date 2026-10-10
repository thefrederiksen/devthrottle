using System.Text.Json;
using CcDirector.Core.History;
using CcDirector.Core.Utilities;
using Microsoft.Data.Sqlite;

namespace CcDirector.Core.OpenCode;

/// <summary>
/// Reads the model an OpenCode CLI session is currently using from OpenCode's local SQLite store
/// (<c>~/.local/share/opencode/opencode.db</c>). The <c>session</c> table keeps the session's
/// current model directly, as a JSON blob in the <c>model</c> column:
///
///   {"id":"gpt-5.3-chat-latest","providerID":"openai"}
///
/// OpenCode updates the row when the model changes, so this is the live answer. The active session
/// is the newest <c>session</c> row whose <c>directory</c> matches the repo - the same resolution
/// as <see cref="OpenCodeHistoryReader"/>. The store is written by a live OpenCode process, so
/// reads go through <see cref="SqliteSnapshotReader"/> (verified against opencode 1.15.12, issue
/// #1637).
/// </summary>
public static class OpenCodeCurrentModel
{
    /// <summary>The current model of the newest OpenCode session matching
    /// <paramref name="repoPath"/> from the default store, or null when the store is absent, no
    /// session matches, or the session has no model recorded.</summary>
    public static string? ReadForRepo(string repoPath)
    {
        var databasePath = OpenCodeHistoryReader.DefaultDatabasePath;
        if (databasePath is null)
            return null;
        return ReadFrom(repoPath, databasePath);
    }

    /// <summary>The current model from the store at <paramref name="databasePath"/>. Exposed (with
    /// an explicit database path) for testing so it never has to touch the user profile.</summary>
    public static string? ReadFrom(string repoPath, string databasePath)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
            return null;

        try
        {
            return SqliteSnapshotReader.Read(databasePath, connection => ReadFromConnection(connection, repoPath));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[OpenCodeCurrentModel] Read error for {databasePath} FAILED: {ex.Message}");
            return null;
        }
    }

    private static string? ReadFromConnection(SqliteConnection connection, string repoPath)
    {
        var target = NormalizePath(repoPath);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT directory, model FROM session ORDER BY time_updated DESC";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var directory = reader.IsDBNull(0) ? null : reader.GetString(0);
            if (directory is null || NormalizePath(directory) != target)
                continue;

            var modelJson = reader.IsDBNull(1) ? null : reader.GetString(1);
            var model = ParseModelId(modelJson);
            if (model is not null)
                FileLog.Write($"[OpenCodeCurrentModel] model={model}");
            return model;
        }

        return null;
    }

    /// <summary>Parse the <c>id</c> out of the session row's model JSON blob, or null.</summary>
    public static string? ParseModelId(string? modelJson)
    {
        if (string.IsNullOrWhiteSpace(modelJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(modelJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            return root.TryGetProperty("id", out var id)
                   && id.ValueKind == JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(id.GetString())
                ? id.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string NormalizePath(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd('\\', '/').ToLowerInvariant(); }
        catch { return p.TrimEnd('\\', '/').ToLowerInvariant(); }
    }
}
