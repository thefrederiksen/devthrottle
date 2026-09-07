using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The workspace surface over a real Gateway with auth ON (issue #2722).
///
/// What is proven here, rather than assumed:
///  1. AUTH. Every verb is refused unauthenticated, and refused WITHOUT applying the write. A wrong
///     assumption here would leave a record of somebody's whole fleet world-writable.
///  2. THE ROUND TRIP over real HTTP - the document goes out and comes back with its seats intact, which
///     is what a Director's Save Workspace and Load Workspace now do.
///  3. THE REFUSALS reach the caller as 400 with the reason in them, so a document that cannot be acted
///     on is rejected where somebody can still fix it.
///  4. THE ROUTE'S ID WINS over the body's, so a body copied from another workspace cannot overwrite that
///     one.
///
/// What is NOT proven here: a capture of a Director with LIVE SESSIONS on it. That needs a Director
/// holding an open push stream, which this suite has no harness for; the capture route is exercised
/// against a registered Director with no live sessions (the empty fleet a finished drain leaves), and the
/// FOLD itself - every seat field, taken from the hand-written index of 2026-09-06 - is covered by
/// WorkspaceCaptureTests over real session records.
/// </summary>
public sealed class WorkspaceEndpointTests : IAsyncLifetime
{
    private const string Token = "test-token-workspaces";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cc-workspaces-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(
            port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: Path.Combine(_dir, "instances"),
            workListsPath: Path.Combine(_dir, "worklists.json"),
            missionNotesPath: Path.Combine(_dir, "mission-notes.json"),
            missionsPath: Path.Combine(_dir, "missions.json"));
        await _gateway.StartAsync();

        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/"),
        };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    private async Task<HttpResponseMessage> Authed(HttpMethod method, string path, object? body = null)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null) req.Content = JsonContent.Create(body);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return await _http.SendAsync(req);
    }

    /// <summary>
    /// A fresh workspace id for one test.
    ///
    /// Each test starts its OWN Gateway, but a Gateway per test is not a STORE per test - the database
    /// is per process - so two tests using the same slug see each other's writes, and the one that
    /// asserts a workspace is absent fails on a workspace the other one wrote. Caught exactly that way:
    /// the auth test passed alone and failed in the class.
    /// </summary>
    private static string FreshId() => "ws-" + Guid.NewGuid().ToString("N");

    private static WorkspaceDocument Authored(string id, string name = "Morning fleet")
        => new()
        {
            Id = id,
            Name = name,
            Description = "The seats I open every morning.",
            Origin = WorkspaceOrigins.Authored,
            Seats =
            {
                new WorkspaceSeat
                {
                    Name = "Linux Support - Architect",
                    Agent = "ClaudeCode",
                    RepoPath = @"D:\ReposFred\devthrottle_internal",
                    Role = "Manager",
                    Model = "claude-opus-5",
                    OpeningPrompt = "Read PHASE-A-MANDATE.md and continue.",
                    SortOrder = 0,
                },
            },
        };

    // ---- 1. AUTH -------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_verb_is_refused_unauthenticated_and_the_write_does_not_land()
    {
        var id = FreshId();

        using var list = await _http.GetAsync("/gateway/workspaces");
        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);

        using var read = await _http.GetAsync($"/gateway/workspaces/{id}");
        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);

        using var write = await _http.PutAsJsonAsync($"/gateway/workspaces/{id}", Authored(id));
        Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode);

        using var capture = await _http.PostAsJsonAsync("/gateway/workspaces",
            new WorkspaceCaptureRequest { Id = id, Name = "x", DirectorId = "d" });
        Assert.Equal(HttpStatusCode.Unauthorized, capture.StatusCode);

        using var delete = await _http.DeleteAsync($"/gateway/workspaces/{id}");
        Assert.Equal(HttpStatusCode.Unauthorized, delete.StatusCode);

        // Refused, not silently applied - the distinction the test exists for.
        using var afterwards = await Authed(HttpMethod.Get, $"/gateway/workspaces/{id}");
        Assert.Equal(HttpStatusCode.NotFound, afterwards.StatusCode);
    }

    // ---- 2. THE ROUND TRIP ---------------------------------------------------------------------------

    [Fact]
    public async Task A_workspace_goes_out_and_comes_back_with_its_seats()
    {
        var id = FreshId();
        using var put = await Authed(HttpMethod.Put, $"/gateway/workspaces/{id}", Authored(id));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var get = await Authed(HttpMethod.Get, $"/gateway/workspaces/{id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var doc = await get.Content.ReadFromJsonAsync<WorkspaceDocument>();

        Assert.NotNull(doc);
        Assert.Equal("Morning fleet", doc!.Name);
        Assert.Equal(WorkspaceOrigins.Authored, doc.Origin);
        var seat = Assert.Single(doc.Seats);
        Assert.Equal("Linux Support - Architect", seat.Name);
        Assert.Equal("ClaudeCode", seat.Agent);
        Assert.Equal(@"D:\ReposFred\devthrottle_internal", seat.RepoPath);
        Assert.Equal("Manager", seat.Role);
        Assert.Equal("claude-opus-5", seat.Model);
        Assert.Equal("Read PHASE-A-MANDATE.md and continue.", seat.OpeningPrompt);

        using var list = await Authed(HttpMethod.Get, "/gateway/workspaces");
        var listed = await list.Content.ReadFromJsonAsync<WorkspaceListEnvelope>();
        var summary = Assert.Single(listed!.Workspaces!, w => w.Id == id);
        Assert.Equal(1, summary.SeatCount);
        Assert.Equal("The seats I open every morning.", summary.Description);
    }

    [Fact]
    public async Task Delete_removes_it_and_a_second_delete_says_so()
    {
        var id = FreshId();
        await Authed(HttpMethod.Put, $"/gateway/workspaces/{id}", Authored(id));

        using var first = await Authed(HttpMethod.Delete, $"/gateway/workspaces/{id}");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await Authed(HttpMethod.Delete, $"/gateway/workspaces/{id}");
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);

        using var get = await Authed(HttpMethod.Get, $"/gateway/workspaces/{id}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    // ---- 3. THE REFUSALS REACH THE CALLER ------------------------------------------------------------

    [Fact]
    public async Task A_seat_marked_for_restore_with_no_command_is_refused_with_the_reason()
    {
        var id = FreshId();
        var doc = Authored(id);
        doc.Seats[0].Restore = new WorkspaceSeatRestore
        {
            Decision = WorkspaceRestoreDecisions.Restore,
            Why = "Head of the mission with real continuing work.",
        };

        using var put = await Authed(HttpMethod.Put, $"/gateway/workspaces/{id}", doc);
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Contains("command that brings it back", await put.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_id_that_is_not_a_slug_is_refused_with_what_a_valid_one_looks_like()
    {
        using var put = await Authed(HttpMethod.Put, "/gateway/workspaces/Morning_Fleet",
            Authored(FreshId()));
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Contains("lowercase slug", await put.Content.ReadAsStringAsync());
    }

    // ---- 4. THE ROUTE'S ID WINS ----------------------------------------------------------------------

    [Fact]
    public async Task The_id_in_the_route_wins_over_the_id_in_the_body()
    {
        var morning = FreshId();
        var evening = FreshId();
        await Authed(HttpMethod.Put, $"/gateway/workspaces/{morning}", Authored(morning));

        // A body copied from another workspace still names that one. Honouring the body would let a copy
        // silently overwrite the workspace it was copied from.
        var copied = Authored(id: morning, name: "Evening fleet");
        using var put = await Authed(HttpMethod.Put, $"/gateway/workspaces/{evening}", copied);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var original = await Authed(HttpMethod.Get, $"/gateway/workspaces/{morning}");
        var doc = await original.Content.ReadFromJsonAsync<WorkspaceDocument>();
        Assert.Equal("Morning fleet", doc!.Name);

        using var copy = await Authed(HttpMethod.Get, $"/gateway/workspaces/{evening}");
        var copyDoc = await copy.Content.ReadFromJsonAsync<WorkspaceDocument>();
        Assert.Equal("Evening fleet", copyDoc!.Name);
        Assert.Equal(evening, copyDoc.Id);
    }

    // ---- CAPTURE -------------------------------------------------------------------------------------

    [Fact]
    public async Task Capturing_a_Director_the_Gateway_has_never_seen_is_refused()
    {
        using var res = await Authed(HttpMethod.Post, "/gateway/workspaces", new WorkspaceCaptureRequest
        {
            Id = FreshId(),
            Name = "A restart of a Director that does not exist",
            DirectorId = "00000000-0000-0000-0000-000000000000",
        });

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Contains("no Director with id", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Capture_records_the_Director_it_folded_and_refuses_to_replace_it()
    {
        var directorId = Guid.NewGuid().ToString();
        using var register = await Authed(HttpMethod.Post, "directors/register", new DirectorRegistrationRequest
        {
            DirectorId = directorId,
            TailnetEndpoint = "http://soren-north.tailnet:7879",
            Pid = 9999,
            MachineName = "SOREN_NORTH",
            User = "tester",
            Version = "2.0.5",
            StartedAt = DateTime.UtcNow,
        });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        var request = new WorkspaceCaptureRequest
        {
            Id = FreshId(),
            Name = "DevThrottle_1 restart",
            DirectorId = directorId,
            Reason = "update to 2.0.6",
            DrivenBySessionId = "3807b006-185a-419a-9897-c885455117ae",
        };

        using var capture = await Authed(HttpMethod.Post, "/gateway/workspaces", request);
        Assert.Equal(HttpStatusCode.Created, capture.StatusCode);

        var doc = await capture.Content.ReadFromJsonAsync<WorkspaceDocument>();
        Assert.NotNull(doc);
        Assert.Equal(WorkspaceOrigins.Captured, doc!.Origin);
        Assert.Equal(directorId, doc.DirectorId);
        Assert.Equal("SOREN_NORTH", doc.Machine);
        Assert.Equal("2.0.5", doc.DirectorVersionBefore);
        Assert.Equal(WorkspaceOutcomes.Draining, doc.Outcome);
        Assert.Equal("update to 2.0.6", doc.Reason);

        // Capture creates and never replaces: overwriting would destroy a drain somebody is halfway
        // through, silently.
        using var again = await Authed(HttpMethod.Post, "/gateway/workspaces", request);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("already exists", await again.Content.ReadAsStringAsync());
    }

    private sealed class WorkspaceListEnvelope
    {
        public List<WorkspaceSummaryDto>? Workspaces { get; set; }
    }
}
