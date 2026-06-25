using CcDirector.Core.Copilot;
using CcDirector.Core.History;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CcDirector.Core.Tests.Copilot;

/// <summary>
/// Validates that CopilotHistoryReader maps Copilot's SQLite store into the canonical
/// ConversationHistory: each turn becomes a User text message and an Assistant message; tool
/// calls from forge_trajectory_events attach to their turn as ToolUse / ToolResult parts
/// (before the assistant text); the newest session for the repo cwd wins and other cwds are
/// ignored.
/// </summary>
public class CopilotHistoryReaderTests
{
    [Fact]
    public void ReadFrom_MapsTurnsAndToolEventsToCanonicalHistory()
    {
        var dbPath = NewDbPath();
        const string repo = @"C:\target\repo";
        try
        {
            using (var conn = OpenWritable(dbPath))
            {
                CreateSchema(conn);
                InsertSession(conn, "s-target", repo, updatedAt: "2026-06-20T10:00:00.000Z");

                // Turn 0: a plain prompt and reply, no tools.
                InsertTurn(conn, "s-target", 0, "read the file", "Sure, reading it.", "2026-06-20T10:00:01.000Z");
                // Turn 1: a prompt, a tool call + output, then the assistant's final text.
                InsertTurn(conn, "s-target", 1, "now run it", "All done.", "2026-06-20T10:00:05.000Z");
                InsertToolEvent(conn, "s-target", turnIndex: 1, toolCallId: "call_1",
                    eventType: "bash", command: "cat x", output: "file contents");
            }

            var history = CopilotHistoryReader.ReadFrom(repo, dbPath);

            // user(0), assistant(0), user(1), assistant(1) = 4 messages.
            Assert.Equal(4, history.Messages.Count);

            Assert.Equal(ConversationRole.User, history.Messages[0].Role);
            Assert.Equal("read the file", Assert.Single(history.Messages[0].Parts).Text);

            Assert.Equal(ConversationRole.Assistant, history.Messages[1].Role);
            Assert.Equal("Sure, reading it.", Assert.Single(history.Messages[1].Parts).Text);

            Assert.Equal(ConversationRole.User, history.Messages[2].Role);
            Assert.Equal("now run it", Assert.Single(history.Messages[2].Parts).Text);

            // The assistant turn carries the tool call, its result, then the final text - in order.
            var assistant1 = history.Messages[3];
            Assert.Equal(ConversationRole.Assistant, assistant1.Role);
            Assert.Equal(3, assistant1.Parts.Count);

            var call = assistant1.Parts[0];
            Assert.Equal(ConversationPartKind.ToolUse, call.Kind);
            Assert.Equal("bash", call.ToolName);
            Assert.Equal("call_1", call.ToolId);
            Assert.Equal("cat x", call.Text);

            var result = assistant1.Parts[1];
            Assert.Equal(ConversationPartKind.ToolResult, result.Kind);
            Assert.Equal("call_1", result.ToolId);
            Assert.Equal("file contents", result.Text);

            var text = assistant1.Parts[2];
            Assert.Equal(ConversationPartKind.Text, text.Kind);
            Assert.Equal("All done.", text.Text);
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public void ReadFrom_PicksNewestSessionForCwd_IgnoringOtherCwdsAndOlderSessions()
    {
        var dbPath = NewDbPath();
        const string repo = @"C:\target\repo";
        try
        {
            using (var conn = OpenWritable(dbPath))
            {
                CreateSchema(conn);

                // An older session for the same repo - must be ignored in favor of the newer one.
                InsertSession(conn, "s-old", repo, updatedAt: "2026-06-20T09:00:00.000Z");
                InsertTurn(conn, "s-old", 0, "old prompt", "old reply", "2026-06-20T09:00:01.000Z");

                // The newest session overall, but a different cwd - must be ignored.
                InsertSession(conn, "s-other", @"C:\other\repo", updatedAt: "2026-06-20T12:00:00.000Z");
                InsertTurn(conn, "s-other", 0, "other prompt", "other reply", "2026-06-20T12:00:01.000Z");

                // The newest session for the target repo - this one should win.
                InsertSession(conn, "s-new", repo, updatedAt: "2026-06-20T11:00:00.000Z");
                InsertTurn(conn, "s-new", 0, "newest prompt", "newest reply", "2026-06-20T11:00:01.000Z");
            }

            var history = CopilotHistoryReader.ReadFrom(repo, dbPath);

            Assert.Equal(2, history.Messages.Count);
            Assert.Equal("newest prompt", Assert.Single(history.Messages[0].Parts).Text);
            Assert.Equal("newest reply", Assert.Single(history.Messages[1].Parts).Text);
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public void ReadFrom_MatchesCwdIgnoringTrailingSeparatorAndCase()
    {
        var dbPath = NewDbPath();
        try
        {
            using (var conn = OpenWritable(dbPath))
            {
                CreateSchema(conn);
                InsertSession(conn, "s1", @"C:\Target\Repo\", updatedAt: "2026-06-20T10:00:00.000Z");
                InsertTurn(conn, "s1", 0, "hi", "hello", "2026-06-20T10:00:01.000Z");
            }

            var history = CopilotHistoryReader.ReadFrom(@"c:\target\repo", dbPath);

            Assert.Equal(2, history.Messages.Count);
            Assert.Equal("hi", Assert.Single(history.Messages[0].Parts).Text);
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public void ReadFrom_NoSessionForRepo_ReturnsEmpty()
    {
        var dbPath = NewDbPath();
        try
        {
            using (var conn = OpenWritable(dbPath))
            {
                CreateSchema(conn);
                InsertSession(conn, "s1", @"C:\some\other", updatedAt: "2026-06-20T10:00:00.000Z");
                InsertTurn(conn, "s1", 0, "hi", "hello", "2026-06-20T10:00:01.000Z");
            }

            Assert.Same(ConversationHistory.Empty, CopilotHistoryReader.ReadFrom(@"C:\target\repo", dbPath));
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    // -- fixture builders --

    private static string NewDbPath()
        => Path.Combine(Path.GetTempPath(), "copilot-fixture-" + Guid.NewGuid().ToString("N") + ".db");

    private static SqliteConnection OpenWritable(string path)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
        var conn = new SqliteConnection(connectionString);
        conn.Open();
        return conn;
    }

    private static void CreateSchema(SqliteConnection conn)
    {
        Execute(conn,
            """
            CREATE TABLE sessions (
                id TEXT PRIMARY KEY,
                cwd TEXT,
                repository TEXT,
                host_type TEXT,
                branch TEXT,
                summary TEXT,
                created_at TEXT,
                updated_at TEXT
            );
            CREATE TABLE turns (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                turn_index INTEGER NOT NULL,
                user_message TEXT,
                assistant_response TEXT,
                timestamp TEXT
            );
            CREATE TABLE forge_trajectory_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                tool_call_id TEXT,
                turn_index INTEGER,
                event_type TEXT NOT NULL,
                command TEXT,
                output TEXT,
                exit_code INTEGER,
                event_key TEXT,
                event_value TEXT,
                created_at TEXT
            );
            """);
    }

    private static void InsertSession(SqliteConnection conn, string id, string cwd, string updatedAt)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO sessions (id, cwd, updated_at) VALUES (@id, @cwd, @updated)";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@cwd", cwd);
        cmd.Parameters.AddWithValue("@updated", updatedAt);
        cmd.ExecuteNonQuery();
    }

    private static void InsertTurn(SqliteConnection conn, string sessionId, int turnIndex,
        string? userMessage, string? assistantResponse, string timestamp)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO turns (session_id, turn_index, user_message, assistant_response, timestamp) " +
            "VALUES (@sid, @idx, @user, @assistant, @ts)";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@idx", turnIndex);
        cmd.Parameters.AddWithValue("@user", (object?)userMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@assistant", (object?)assistantResponse ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ts", timestamp);
        cmd.ExecuteNonQuery();
    }

    private static void InsertToolEvent(SqliteConnection conn, string sessionId, int turnIndex,
        string toolCallId, string eventType, string? command, string? output)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO forge_trajectory_events (session_id, tool_call_id, turn_index, event_type, command, output) " +
            "VALUES (@sid, @callId, @idx, @type, @command, @output)";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@callId", toolCallId);
        cmd.Parameters.AddWithValue("@idx", turnIndex);
        cmd.Parameters.AddWithValue("@type", eventType);
        cmd.Parameters.AddWithValue("@command", (object?)command ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@output", (object?)output ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
