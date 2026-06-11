using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Engine.Dispatcher;
using CcDirector.Gateway.Contracts;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// End-to-end tests for <c>POST /dispatch</c> (issue #329) - the Phase-1B comm-dispatch
/// verb. Runs against a real ControlApiHost (auth ENABLED, so the token gate is exercised
/// on every request) wired to a REAL CommunicationDispatcher over a temp SQLite
/// communications DB with a MOCK channel runner injected - the full Engine dispatch path
/// executes, and nothing can ever actually send.
/// </summary>
[Collection("DirectorRoot")]
public sealed class DispatchEndpointTests : IAsyncLifetime
{
    private const string MockSender = "mock-sender@example.invalid";
    private const string MockRecipient = "queue-test@example.invalid";

    private readonly string _instancesDir;
    private readonly string _dbPath;
    private readonly List<IReadOnlyList<string>> _sends = new();

    private ControlApiHost _host = null!; // Initialized in InitializeAsync
    private SessionManager _sm = null!; // Initialized in InitializeAsync
    private HttpClient _client = null!; // Initialized in InitializeAsync
    private CommunicationDispatcher? _dispatcher;

    public DispatchEndpointTests()
    {
        var unique = Guid.NewGuid().ToString("N");
        _instancesDir = Path.Combine(Path.GetTempPath(), "ccd-dispatch-endpoint-test-" + unique);
        _dbPath = Path.Combine(Path.GetTempPath(), "ccd-dispatch-endpoint-db-" + unique + ".db");
    }

    public async Task InitializeAsync()
    {
        CreateCommunicationsDb(_dbPath);

        _dispatcher = new CommunicationDispatcher(
            _dbPath,
            new EmailRoutingTable(new[] { new EmailRoute(MockSender, @"C:\mock\cc-mockmail.exe", "cc-mockmail", "mock") }),
            processRunner: (_, args) =>
            {
                _sends.Add(args);
                return Task.FromResult(new ToolProcessResult(0, "ok", ""));
            });

        _sm = new SessionManager(new AgentOptions());
        _host = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask,
            useEphemeralPort: true, authEnabled: true, instancesDirectory: _instancesDir,
            commDispatcherAccessor: () => _dispatcher);
        var port = await _host.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        var token = DirectorAuth.LoadOrCreateToken();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _sm.Dispose();
        _dispatcher?.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { /* best effort temp cleanup */ }
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, recursive: true); } catch (IOException) { /* best effort */ }
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

    private void InsertItem(string id, int ticket, string status)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO communications (id, ticket_number, platform, type, persona, content, created_at, status, send_from, email_specific)
            VALUES (@id, @ticket, 'email', 'email', 'personal', 'Endpoint test body.', @now, @status, @sendFrom, @spec)
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@ticket", ticket);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@sendFrom", MockSender);
        cmd.Parameters.AddWithValue("@spec", $$"""{"to":["{{MockRecipient}}"],"subject":"TEST #329 endpoint"}""");
        cmd.ExecuteNonQuery();
    }

    private string ReadStatus(string id)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status FROM communications WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        var status = cmd.ExecuteScalar() as string;
        Assert.NotNull(status);
        return status;
    }

    // ===== Acceptance criterion 1: approved item dispatches and advances to posted =====

    [Fact]
    public async Task Dispatch_ApprovedItem_Returns200AndItemIsPosted()
    {
        InsertItem("ep-approved", 8001, "approved");

        var response = await _client.PostAsJsonAsync("dispatch", new DispatchRequest { QueueItemId = "ep-approved" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<DispatchResultDto>();
        Assert.NotNull(dto);
        Assert.True(dto.Dispatched);
        Assert.Equal("dispatched", dto.Outcome);
        Assert.Equal("cc-mockmail", dto.Channel);
        Assert.Equal("posted", dto.ItemStatus);
        Assert.Equal(8001, dto.TicketNumber);

        Assert.Single(_sends);
        Assert.Equal("posted", ReadStatus("ep-approved"));
    }

    // ===== Acceptance criterion 2: pending item refused, nothing sends =====

    [Fact]
    public async Task Dispatch_PendingReviewItem_Returns409AndNothingSent()
    {
        InsertItem("ep-pending", 8002, "pending_review");

        var response = await _client.PostAsJsonAsync("dispatch", new DispatchRequest { QueueItemId = "ep-pending" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<DispatchResultDto>();
        Assert.NotNull(dto);
        Assert.False(dto.Dispatched);
        Assert.Equal("notApproved", dto.Outcome);
        Assert.Equal("pending_review", dto.ItemStatus);
        Assert.NotNull(dto.Error);

        Assert.Empty(_sends);
        Assert.Equal("pending_review", ReadStatus("ep-pending"));
    }

    [Fact]
    public async Task Dispatch_AlreadyPostedItem_Returns409AndNothingSent()
    {
        InsertItem("ep-posted", 8003, "posted");

        var response = await _client.PostAsJsonAsync("dispatch", new DispatchRequest { QueueItemId = "ep-posted" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(_sends);
    }

    // ===== Acceptance criterion 3: unknown id 404, missing token 401 =====

    [Fact]
    public async Task Dispatch_UnknownItemId_Returns404()
    {
        var response = await _client.PostAsJsonAsync("dispatch", new DispatchRequest { QueueItemId = "no-such-item" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<DispatchResultDto>();
        Assert.NotNull(dto);
        Assert.Equal("notFound", dto.Outcome);
        Assert.Empty(_sends);
    }

    [Fact]
    public async Task Dispatch_WithoutBearerToken_Returns401AndNothingSent()
    {
        InsertItem("ep-auth", 8004, "approved");
        using var anonymous = new HttpClient { BaseAddress = _client.BaseAddress };

        var response = await anonymous.PostAsJsonAsync("dispatch", new DispatchRequest { QueueItemId = "ep-auth" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_sends);
        Assert.Equal("approved", ReadStatus("ep-auth"));
    }

    // ===== Request validation =====

    [Fact]
    public async Task Dispatch_MissingQueueItemId_Returns400()
    {
        var response = await _client.PostAsJsonAsync("dispatch", new DispatchRequest { QueueItemId = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_sends);
    }

    [Fact]
    public async Task Dispatch_MalformedJsonBody_Returns400()
    {
        var response = await _client.PostAsync("dispatch",
            new StringContent("{not json", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_sends);
    }

    // ===== Dispatcher lifecycle =====

    [Fact]
    public async Task Dispatch_DispatcherNotReady_Returns503()
    {
        InsertItem("ep-notready", 8005, "approved");
        var realDispatcher = _dispatcher;
        _dispatcher = null; // Simulate engine still discovering channels (accessor resolves per request).
        try
        {
            var response = await _client.PostAsJsonAsync("dispatch", new DispatchRequest { QueueItemId = "ep-notready" });

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Empty(_sends);
            Assert.Equal("approved", ReadStatus("ep-notready"));
        }
        finally
        {
            _dispatcher = realDispatcher;
        }
    }
}
