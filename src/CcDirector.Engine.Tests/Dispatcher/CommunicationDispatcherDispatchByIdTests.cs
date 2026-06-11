using CcDirector.Engine.Dispatcher;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CcDirector.Engine.Tests.Dispatcher;

/// <summary>
/// Tests for <see cref="CommunicationDispatcher.DispatchByIdAsync"/> (issue #329) - the
/// invocable Engine dispatch primitive behind <c>POST /dispatch</c>. Every test runs
/// against a REAL temp SQLite communications DB and a MOCK channel (injected
/// <see cref="ToolProcessRunner"/>), so the full path is exercised and NOTHING can
/// ever actually send.
/// </summary>
public sealed class CommunicationDispatcherDispatchByIdTests : IDisposable
{
    private const string MockSender = "mock-sender@example.invalid";
    private const string MockRecipient = "queue-test@example.invalid";

    private readonly string _dbPath;
    private readonly List<(string ToolPath, IReadOnlyList<string> Args)> _sends = new();
    private ToolProcessResult _nextResult = new(0, "ok", "");

    public CommunicationDispatcherDispatchByIdTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "ccd-dispatch-test-" + Guid.NewGuid().ToString("N") + ".db");
        CreateCommunicationsDb(_dbPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { /* best effort temp cleanup */ }
    }

    // ===== Fixture =====

    private static void CreateCommunicationsDb(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE communications (
                id TEXT PRIMARY KEY,
                ticket_number INTEGER UNIQUE NOT NULL,
                platform TEXT NOT NULL,
                type TEXT NOT NULL,
                persona TEXT NOT NULL,
                content TEXT NOT NULL,
                created_at TEXT NOT NULL,
                posted_at TEXT,
                posted_by TEXT,
                status TEXT NOT NULL DEFAULT 'pending_review',
                send_from TEXT,
                notes TEXT,
                email_specific TEXT
            )
            """;
        cmd.ExecuteNonQuery();
    }

    private void InsertItem(
        string id, int ticket, string status,
        string platform = "email",
        string? sendFrom = MockSender,
        string? emailSpecific = null)
    {
        emailSpecific ??= $$"""{"to":["{{MockRecipient}}"],"subject":"TEST #329 dispatch","attachments":[]}""";

        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO communications (id, ticket_number, platform, type, persona, content, created_at, status, send_from, email_specific)
            VALUES (@id, @ticket, @platform, 'email', 'personal', 'Test body for issue 329.', @now, @status, @sendFrom, @spec)
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@ticket", ticket);
        cmd.Parameters.AddWithValue("@platform", platform);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@sendFrom", (object?)sendFrom ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@spec", emailSpecific);
        cmd.ExecuteNonQuery();
    }

    private (string Status, string? PostedBy, string? Notes) ReadItem(string id)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status, posted_by, notes FROM communications WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read(), $"item {id} should exist");
        return (
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private CommunicationDispatcher CreateDispatcher(params EmailRoute[] routes)
    {
        if (routes.Length == 0)
            routes = new[] { new EmailRoute(MockSender, @"C:\mock\cc-mockmail.exe", "cc-mockmail", "mock") };

        return new CommunicationDispatcher(
            _dbPath,
            new EmailRoutingTable(routes),
            processRunner: (toolPath, args) =>
            {
                _sends.Add((toolPath, args));
                return Task.FromResult(_nextResult);
            });
    }

    // ===== The approved path =====

    [Fact]
    public async Task DispatchByIdAsync_ApprovedItem_SendsViaChannelAndMarksPosted()
    {
        InsertItem("item-approved", 9001, "approved");
        using var dispatcher = CreateDispatcher();

        var result = await dispatcher.DispatchByIdAsync("item-approved");

        Assert.Equal(QueueDispatchOutcome.Dispatched, result.Outcome);
        Assert.True(result.Dispatched);
        Assert.Equal("cc-mockmail", result.Channel);
        Assert.Equal("posted", result.ItemStatus);
        Assert.Equal(9001, result.TicketNumber);

        // Exactly one channel invocation with the routed tool and the item's recipient/subject.
        var send = Assert.Single(_sends);
        Assert.Equal(@"C:\mock\cc-mockmail.exe", send.ToolPath);
        Assert.Contains(MockRecipient, send.Args);
        Assert.Contains("TEST #329 dispatch", send.Args);
        Assert.Contains("send", send.Args);

        // Same observable state change as the in-app path: status posted, posted_by cc-director.
        var (status, postedBy, _) = ReadItem("item-approved");
        Assert.Equal("posted", status);
        Assert.Equal("cc-director", postedBy);
    }

    // ===== The approval gate (the issue's core acceptance criterion) =====

    [Theory]
    [InlineData("pending_review")]
    [InlineData("rejected")]
    [InlineData("posted")]
    public async Task DispatchByIdAsync_NotApprovedItem_RefusedAndNothingSent(string status)
    {
        InsertItem("item-gated", 9002, status);
        using var dispatcher = CreateDispatcher();

        var result = await dispatcher.DispatchByIdAsync("item-gated");

        Assert.Equal(QueueDispatchOutcome.NotApproved, result.Outcome);
        Assert.False(result.Dispatched);
        Assert.Equal(status, result.ItemStatus);
        Assert.NotNull(result.Error);
        Assert.Contains("approved", result.Error);

        // NOTHING was sent and the item did not move.
        Assert.Empty(_sends);
        var (statusAfter, _, _) = ReadItem("item-gated");
        Assert.Equal(status, statusAfter);
    }

    [Fact]
    public async Task DispatchByIdAsync_UnknownId_NotFound()
    {
        using var dispatcher = CreateDispatcher();

        var result = await dispatcher.DispatchByIdAsync("no-such-item");

        Assert.Equal(QueueDispatchOutcome.NotFound, result.Outcome);
        Assert.False(result.Dispatched);
        Assert.Empty(_sends);
    }

    [Fact]
    public async Task DispatchByIdAsync_ApprovedNonEmailPlatform_RefusedUnsupportedPlatform()
    {
        InsertItem("item-linkedin", 9003, "approved", platform: "linkedin");
        using var dispatcher = CreateDispatcher();

        var result = await dispatcher.DispatchByIdAsync("item-linkedin");

        Assert.Equal(QueueDispatchOutcome.UnsupportedPlatform, result.Outcome);
        Assert.False(result.Dispatched);
        Assert.Empty(_sends);
        var (statusAfter, _, _) = ReadItem("item-linkedin");
        Assert.Equal("approved", statusAfter);
    }

    [Fact]
    public async Task DispatchByIdAsync_ApprovedItemWithoutRecipients_RefusedInvalidItem()
    {
        InsertItem("item-norecipients", 9004, "approved",
            emailSpecific: """{"to":[],"subject":"no recipients"}""");
        using var dispatcher = CreateDispatcher();

        var result = await dispatcher.DispatchByIdAsync("item-norecipients");

        Assert.Equal(QueueDispatchOutcome.InvalidItem, result.Outcome);
        Assert.False(result.Dispatched);
        Assert.Empty(_sends);
    }

    // ===== Provider failure (send attempted, channel failed) =====

    [Fact]
    public async Task DispatchByIdAsync_NoRouteForSender_SendFailedItemStaysApproved()
    {
        InsertItem("item-noroute", 9005, "approved", sendFrom: "unrouted@example.invalid");
        using var dispatcher = CreateDispatcher();

        var result = await dispatcher.DispatchByIdAsync("item-noroute");

        Assert.Equal(QueueDispatchOutcome.SendFailed, result.Outcome);
        Assert.False(result.Dispatched);
        Assert.Empty(_sends);

        var (statusAfter, _, notes) = ReadItem("item-noroute");
        Assert.Equal("approved", statusAfter);
        Assert.NotNull(notes);
        Assert.Contains("Send failed", notes);
    }

    [Fact]
    public async Task DispatchByIdAsync_ChannelToolExitsNonZero_SendFailedItemStaysApproved()
    {
        InsertItem("item-toolfail", 9006, "approved");
        _nextResult = new ToolProcessResult(1, "", "smtp refused");
        using var dispatcher = CreateDispatcher();

        var result = await dispatcher.DispatchByIdAsync("item-toolfail");

        Assert.Equal(QueueDispatchOutcome.SendFailed, result.Outcome);
        Assert.False(result.Dispatched);
        Assert.Equal("cc-mockmail", result.Channel);
        Assert.Contains("smtp refused", result.Error);
        Assert.Single(_sends);

        var (statusAfter, _, notes) = ReadItem("item-toolfail");
        Assert.Equal("approved", statusAfter);
        Assert.NotNull(notes);
        Assert.Contains("smtp refused", notes);
    }

    // ===== Input validation =====

    [Fact]
    public async Task DispatchByIdAsync_EmptyId_ThrowsArgumentException()
    {
        using var dispatcher = CreateDispatcher();

        await Assert.ThrowsAsync<ArgumentException>(() => dispatcher.DispatchByIdAsync(""));
    }

    [Fact]
    public async Task DispatchByIdAsync_MissingDatabase_NotFound()
    {
        var missingDb = Path.Combine(Path.GetTempPath(), "ccd-missing-" + Guid.NewGuid().ToString("N") + ".db");
        using var dispatcher = new CommunicationDispatcher(
            missingDb,
            new EmailRoutingTable(new[] { new EmailRoute(MockSender, @"C:\mock\cc-mockmail.exe", "cc-mockmail", "mock") }),
            processRunner: (_, _) => Task.FromResult(new ToolProcessResult(0, "", "")));

        var result = await dispatcher.DispatchByIdAsync("any");

        Assert.Equal(QueueDispatchOutcome.NotFound, result.Outcome);
    }
}
