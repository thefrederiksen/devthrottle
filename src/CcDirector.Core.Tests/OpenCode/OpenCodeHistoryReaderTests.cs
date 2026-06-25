using CcDirector.Core.History;
using CcDirector.Core.OpenCode;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CcDirector.Core.Tests.OpenCode;

/// <summary>
/// Validates that OpenCodeHistoryReader maps OpenCode's SQLite store into the canonical
/// ConversationHistory: each message row becomes a User or Assistant message per its
/// data.role; ordered part rows map text -> Text, reasoning -> Thinking, tool -> ToolUse
/// followed by ToolResult, and the turn markers step-start / step-finish are skipped; the
/// newest session for the repo directory wins and other directories are ignored.
/// </summary>
public class OpenCodeHistoryReaderTests
{
    [Fact]
    public void ReadFrom_MapsMessagesAndPartsToCanonicalHistory()
    {
        var dbPath = NewDbPath();
        const string repo = @"C:\target\repo";
        try
        {
            using (var conn = OpenWritable(dbPath))
            {
                CreateSchema(conn);
                InsertSession(conn, "ses-target", repo, timeUpdated: 2000);

                // A user turn: one text part.
                InsertMessage(conn, "msg-user", "ses-target", role: "user", timeCreated: 1000);
                InsertPart(conn, "prt-u1", "msg-user", "ses-target", timeCreated: 1001,
                    data: """{"type":"text","text":"read the file then run it"}""");

                // An assistant turn carrying, in order: a step-start marker (skipped), a reasoning
                // block, a tool call + its result, a step-finish marker (skipped), then final text.
                InsertMessage(conn, "msg-asst", "ses-target", role: "assistant", timeCreated: 1100);
                InsertPart(conn, "prt-a1", "msg-asst", "ses-target", timeCreated: 1101,
                    data: """{"type":"step-start"}""");
                InsertPart(conn, "prt-a2", "msg-asst", "ses-target", timeCreated: 1102,
                    data: """{"type":"reasoning","text":"I should look at the file first."}""");
                InsertPart(conn, "prt-a3", "msg-asst", "ses-target", timeCreated: 1103,
                    data: """{"type":"tool","tool":"bash","callID":"call_1","state":{"status":"completed","input":{"command":"cat x"},"output":"file contents"}}""");
                InsertPart(conn, "prt-a4", "msg-asst", "ses-target", timeCreated: 1104,
                    data: """{"type":"step-finish","reason":"stop"}""");
                InsertPart(conn, "prt-a5", "msg-asst", "ses-target", timeCreated: 1105,
                    data: """{"type":"text","text":"All done."}""");
            }

            var history = OpenCodeHistoryReader.ReadFrom(repo, dbPath);

            // user(1 part) + assistant(reasoning, tooluse, toolresult, text) = 2 messages.
            Assert.Equal(2, history.Messages.Count);

            var user = history.Messages[0];
            Assert.Equal(ConversationRole.User, user.Role);
            Assert.Equal("read the file then run it", Assert.Single(user.Parts).Text);

            var assistant = history.Messages[1];
            Assert.Equal(ConversationRole.Assistant, assistant.Role);
            Assert.Equal(4, assistant.Parts.Count);

            var thinking = assistant.Parts[0];
            Assert.Equal(ConversationPartKind.Thinking, thinking.Kind);
            Assert.Equal("I should look at the file first.", thinking.Text);

            var call = assistant.Parts[1];
            Assert.Equal(ConversationPartKind.ToolUse, call.Kind);
            Assert.Equal("bash", call.ToolName);
            Assert.Equal("call_1", call.ToolId);
            Assert.Equal("""{"command":"cat x"}""", call.Text);

            var result = assistant.Parts[2];
            Assert.Equal(ConversationPartKind.ToolResult, result.Kind);
            Assert.Equal("call_1", result.ToolId);
            Assert.Equal("file contents", result.Text);

            var text = assistant.Parts[3];
            Assert.Equal(ConversationPartKind.Text, text.Kind);
            Assert.Equal("All done.", text.Text);
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public void ReadFrom_ToolError_MapsToToolResult()
    {
        var dbPath = NewDbPath();
        const string repo = @"C:\target\repo";
        try
        {
            using (var conn = OpenWritable(dbPath))
            {
                CreateSchema(conn);
                InsertSession(conn, "ses-target", repo, timeUpdated: 2000);
                InsertMessage(conn, "msg-asst", "ses-target", role: "assistant", timeCreated: 1100);
                InsertPart(conn, "prt-a1", "msg-asst", "ses-target", timeCreated: 1101,
                    data: """{"type":"tool","tool":"bash","callID":"call_err","state":{"status":"error","input":{"command":"boom"},"error":"command failed"}}""");
            }

            var history = OpenCodeHistoryReader.ReadFrom(repo, dbPath);

            var assistant = Assert.Single(history.Messages);
            Assert.Equal(2, assistant.Parts.Count);
            Assert.Equal(ConversationPartKind.ToolUse, assistant.Parts[0].Kind);
            Assert.Equal(ConversationPartKind.ToolResult, assistant.Parts[1].Kind);
            Assert.Equal("command failed", assistant.Parts[1].Text);
            Assert.Equal("call_err", assistant.Parts[1].ToolId);
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public void ReadFrom_PicksNewestSessionForDirectory_IgnoringOtherDirectoriesAndOlderSessions()
    {
        var dbPath = NewDbPath();
        const string repo = @"C:\target\repo";
        try
        {
            using (var conn = OpenWritable(dbPath))
            {
                CreateSchema(conn);

                // An older session for the same repo - must be ignored in favor of the newer one.
                InsertSession(conn, "ses-old", repo, timeUpdated: 1000);
                InsertMessage(conn, "msg-old", "ses-old", role: "user", timeCreated: 1001);
                InsertPart(conn, "prt-old", "msg-old", "ses-old", timeCreated: 1002,
                    data: """{"type":"text","text":"old prompt"}""");

                // The newest session overall, but a different directory - must be ignored.
                InsertSession(conn, "ses-other", @"C:\other\repo", timeUpdated: 3000);
                InsertMessage(conn, "msg-other", "ses-other", role: "user", timeCreated: 3001);
                InsertPart(conn, "prt-other", "msg-other", "ses-other", timeCreated: 3002,
                    data: """{"type":"text","text":"other prompt"}""");

                // The newest session for the target repo - this one should win.
                InsertSession(conn, "ses-new", repo, timeUpdated: 2000);
                InsertMessage(conn, "msg-new", "ses-new", role: "user", timeCreated: 2001);
                InsertPart(conn, "prt-new", "msg-new", "ses-new", timeCreated: 2002,
                    data: """{"type":"text","text":"newest prompt"}""");
            }

            var history = OpenCodeHistoryReader.ReadFrom(repo, dbPath);

            var message = Assert.Single(history.Messages);
            Assert.Equal("newest prompt", Assert.Single(message.Parts).Text);
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public void ReadFrom_MatchesDirectoryIgnoringTrailingSeparatorAndCase()
    {
        var dbPath = NewDbPath();
        try
        {
            using (var conn = OpenWritable(dbPath))
            {
                CreateSchema(conn);
                InsertSession(conn, "ses-1", @"C:\Target\Repo\", timeUpdated: 1000);
                InsertMessage(conn, "msg-1", "ses-1", role: "user", timeCreated: 1001);
                InsertPart(conn, "prt-1", "msg-1", "ses-1", timeCreated: 1002,
                    data: """{"type":"text","text":"hi"}""");
            }

            var history = OpenCodeHistoryReader.ReadFrom(@"c:\target\repo", dbPath);

            Assert.Equal("hi", Assert.Single(Assert.Single(history.Messages).Parts).Text);
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
                InsertSession(conn, "ses-1", @"C:\some\other", timeUpdated: 1000);
                InsertMessage(conn, "msg-1", "ses-1", role: "user", timeCreated: 1001);
                InsertPart(conn, "prt-1", "msg-1", "ses-1", timeCreated: 1002,
                    data: """{"type":"text","text":"hi"}""");
            }

            Assert.Same(ConversationHistory.Empty, OpenCodeHistoryReader.ReadFrom(@"C:\target\repo", dbPath));
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public void ReadFrom_SkipsEmptyTextAndReasoningParts()
    {
        var dbPath = NewDbPath();
        const string repo = @"C:\target\repo";
        try
        {
            using (var conn = OpenWritable(dbPath))
            {
                CreateSchema(conn);
                InsertSession(conn, "ses-target", repo, timeUpdated: 2000);
                InsertMessage(conn, "msg-asst", "ses-target", role: "assistant", timeCreated: 1100);
                // OpenAI reasoning is encrypted and stored with empty text - it must not become an
                // empty Thinking part.
                InsertPart(conn, "prt-a1", "msg-asst", "ses-target", timeCreated: 1101,
                    data: """{"type":"reasoning","text":""}""");
                InsertPart(conn, "prt-a2", "msg-asst", "ses-target", timeCreated: 1102,
                    data: """{"type":"text","text":"pong"}""");
            }

            var history = OpenCodeHistoryReader.ReadFrom(repo, dbPath);

            var assistant = Assert.Single(history.Messages);
            var part = Assert.Single(assistant.Parts);
            Assert.Equal(ConversationPartKind.Text, part.Kind);
            Assert.Equal("pong", part.Text);
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    // -- fixture builders --

    private static string NewDbPath()
        => Path.Combine(Path.GetTempPath(), "opencode-fixture-" + Guid.NewGuid().ToString("N") + ".db");

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
            CREATE TABLE session (
                id TEXT PRIMARY KEY,
                project_id TEXT,
                directory TEXT NOT NULL,
                title TEXT,
                time_created INTEGER,
                time_updated INTEGER
            );
            CREATE TABLE message (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                time_created INTEGER NOT NULL,
                time_updated INTEGER,
                data TEXT NOT NULL
            );
            CREATE TABLE part (
                id TEXT PRIMARY KEY,
                message_id TEXT NOT NULL,
                session_id TEXT NOT NULL,
                time_created INTEGER NOT NULL,
                time_updated INTEGER,
                data TEXT NOT NULL
            );
            """);
    }

    private static void InsertSession(SqliteConnection conn, string id, string directory, long timeUpdated)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO session (id, directory, time_created, time_updated) VALUES (@id, @dir, @created, @updated)";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@dir", directory);
        cmd.Parameters.AddWithValue("@created", timeUpdated);
        cmd.Parameters.AddWithValue("@updated", timeUpdated);
        cmd.ExecuteNonQuery();
    }

    private static void InsertMessage(SqliteConnection conn, string id, string sessionId, string role, long timeCreated)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO message (id, session_id, time_created, time_updated, data) " +
            "VALUES (@id, @sid, @created, @created, @data)";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@created", timeCreated);
        cmd.Parameters.AddWithValue("@data", $$"""{"role":"{{role}}"}""");
        cmd.ExecuteNonQuery();
    }

    private static void InsertPart(SqliteConnection conn, string id, string messageId, string sessionId,
        long timeCreated, string data)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO part (id, message_id, session_id, time_created, time_updated, data) " +
            "VALUES (@id, @mid, @sid, @created, @created, @data)";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@mid", messageId);
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@created", timeCreated);
        cmd.Parameters.AddWithValue("@data", data);
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
