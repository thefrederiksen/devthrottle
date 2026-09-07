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
/// holding an open push stream, which this suite has no harness for. What IS proven is the refusal in
/// front of it - a Director that is registered but not stream-connected is refused rather than captured
/// as an empty fleet - and the FOLD itself, every seat field taken from the hand-written index of
/// 2026-09-06, is covered by WorkspaceCaptureTests over real session records.
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
    public async Task Capturing_a_Director_that_is_not_connected_is_refused_rather_than_recorded_as_empty()
    {
        // The registry knows a Director for a while after it stops talking, and the live roster comes
        // from the push stream. Without this refusal the capture folds to ZERO seats and returns 201 -
        // and "no sessions" is exactly what a finished drain looks like, so the record would say nothing
        // was running on a machine nobody could reach.
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

        var id = FreshId();
        using var capture = await Authed(HttpMethod.Post, "/gateway/workspaces", new WorkspaceCaptureRequest
        {
            Id = id,
            Name = "DevThrottle_1 restart",
            DirectorId = directorId,
        });

        Assert.Equal(HttpStatusCode.Conflict, capture.StatusCode);
        var body = await capture.Content.ReadAsStringAsync();
        Assert.Contains("not connected to this Gateway", body);
        Assert.Contains("EMPTY fleet", body);

        // And nothing was stored, so a retry after reconnecting is not blocked by a phantom row.
        using var get = await Authed(HttpMethod.Get, $"/gateway/workspaces/{id}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task A_PUT_cannot_mint_a_captured_workspace()
    {
        var id = FreshId();
        var doc = Authored(id);
        doc.Origin = WorkspaceOrigins.Captured;
        doc.DirectorId = Guid.NewGuid().ToString();
        doc.Machine = "A MACHINE THAT NEVER RAN THIS";

        // The capture verb exists because these facts come firsthand from the Gateway. A write path that
        // let a caller assemble them would make that reason worth nothing.
        using var put = await Authed(HttpMethod.Put, $"/gateway/workspaces/{id}", doc);
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Contains("POST /gateway/workspaces", await put.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_malformed_body_is_a_bad_request_and_not_a_server_error()
    {
        var id = FreshId();
        var doc = Authored(id);
        doc.Seats = null!;

        // This used to reach the store as a NullReferenceException and come back as a bare 500, telling
        // the caller nothing about what they had sent.
        using var put = await Authed(HttpMethod.Put, $"/gateway/workspaces/{id}", doc);
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Contains("seats", await put.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_blocked_run_that_was_recovered_without_a_restart_can_be_recorded()
    {
        // The terminal state of the never-force path. A drain closes leaf-first as each handover lands,
        // so a run that blocks has already emptied part of the Director; without this outcome nothing
        // could say those seats had been brought back, and "a partly drained Director is a normal,
        // recoverable state" would be a slogan rather than a state.
        var id = FreshId();
        var doc = Authored(id);
        doc.DirectorOutcome = WorkspaceDirectorOutcomes.NotRestarted;
        doc.SeatOutcome = new WorkspaceSeatOutcome { RestoredCount = 4 };

        using var put = await Authed(HttpMethod.Put, $"/gateway/workspaces/{id}", doc);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var get = await Authed(HttpMethod.Get, $"/gateway/workspaces/{id}");
        var stored = await get.Content.ReadFromJsonAsync<WorkspaceDocument>();
        Assert.Equal("not-restarted", stored!.DirectorOutcome);
        Assert.Equal("all", stored.SeatOutcome!.Scope);
        Assert.Equal(4, stored.SeatOutcome.RestoredCount);
    }

    [Fact]
    public async Task Registering_a_Director_is_not_enough_to_capture_it()
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

        // Registered is not connected, and only connected can be captured.
        using var capture = await Authed(HttpMethod.Post, "/gateway/workspaces", request);
        Assert.Equal(HttpStatusCode.Conflict, capture.StatusCode);

        // WHAT THIS FILE THEREFORE DOES NOT COVER, said here rather than left to be assumed: a successful
        // capture, and the create-only guarantee over the route. This suite cannot put a Director on the
        // push stream. WorkspaceStoreTests.Create_refuses_to_replace_an_existing_workspace holds the
        // create-only rule, and WorkspaceCaptureTests holds what the fold produces from real session
        // records.
    }

    private sealed class WorkspaceListEnvelope
    {
        public List<WorkspaceSummaryDto>? Workspaces { get; set; }
    }
}
