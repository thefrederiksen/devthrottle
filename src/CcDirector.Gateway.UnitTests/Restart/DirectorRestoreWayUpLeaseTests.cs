using System.Net;
using System.Text;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.ControlApi.SmartRestart;
using CcDirector.Core.Configuration;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Workspaces;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Restart;

/// <summary>
/// THE PROOF THAT WAS MISSING (product issue 3395): "Bring back" on the start-up window and on File, Restart
/// history against a Gateway THAT ENFORCES THE RESTORE LEASE.
///
/// WHY THE FEATURE SHIPPED BROKEN. Every other way-up bring-back test supplies a fake
/// <see cref="IWayUpRestore"/>, which has no lease to hold and nothing to refuse. Those tests prove what the
/// engine ORDERS - which seats, in what order, with which seed file - and they are right to. What no test
/// touched was <see cref="DirectorRestoreWayUp"/>, the one class that decides HOW the order is carried out:
/// it was named in no test at all. It ran <c>DirectorRestore</c> inside the Director, which holds no lease,
/// so the Gateway refused its very first restore mark and nothing ever came back. A green suite certified it
/// for as long as it existed.
///
/// SO THESE TESTS DELIBERATELY DO NOT MOCK THE RESTORE. They drive the REAL way up, the REAL
/// <see cref="DirectorRestoreWayUp"/> and the REAL <see cref="GatewayClient"/>, and behind the client they put
/// a fake Gateway whose lease and whose marks are the PRODUCT'S OWN <see cref="WorkspaceStore"/>. The refusal
/// sentence a test reads here is therefore the Gateway's real sentence, not a second copy of it written to
/// agree with the code under test.
///
/// WHAT THIS FAKE DOES NOT COVER, said plainly rather than left to be discovered. It is the Gateway's
/// WORKSPACE half only: it does not authenticate, it does not check that the Director's machine matches the
/// record's, and it stands in for the tunnel by writing the far-end Director's marks itself instead of
/// relaying the order to a second Director. So it proves that the way up takes the lease at the one door that
/// grants it and that its marks are then accepted - and it proves nothing about the relay, about two
/// Directors racing, or about the order the far end starts seats in. Those live in the Gateway's own
/// endpoint tests and in <c>DirectorRestoreTests</c>.
/// </summary>
[Collection(DirectorGatesCollection.Name)]
public sealed class DirectorRestoreWayUpLeaseTests : IDisposable
{
    private const string ThisDirectorId = "director-after-the-restart";
    private const string WorkspaceId = "restart-20260925-0732-devthrottle-mac-mini";

    private static readonly DateTime Shutdown = new(2026, 9, 25, 7, 32, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "way-up-lease-tests-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>Create the drain folder the handovers and the seed files share.</summary>
    public DirectorRestoreWayUpLeaseTests() => Directory.CreateDirectory(_folder);

    /// <summary>Take the folder and the database away again.</summary>
    public void Dispose()
    {
        _harness.Dispose();
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private string Handover(string seat) => Path.Combine(_folder, $"{seat} - handover.md");

    /// <summary>A seat that handed over and is owed, as the Gateway's own validation insists a stored one
    /// looks: a seat decided "restore" must carry the command line that brings it back, which the rig's
    /// in-memory seats have never needed because nothing validated them.</summary>
    /// <param name="id">Its captured session id.</param>
    /// <param name="name">Its name.</param>
    /// <param name="reportsTo">The captured session id of the seat it reports to, or null.</param>
    /// <param name="sortOrder">Where it sits in the Director's own order.</param>
    private WorkspaceSeat Owed(string id, string name, string? reportsTo = null, int sortOrder = 0)
    {
        var seat = WayUpTestRig.Owed(id, name, Handover(id), reportsTo, sortOrder: sortOrder);
        seat.Restore!.Command = $"cc-devthrottle director restore {WorkspaceId} --seat {id}";
        return seat;
    }

    // ================= the fake Gateway =================

    /// <summary>
    /// The Gateway's workspace routes, over the product's own <see cref="WorkspaceStore"/>, answering the
    /// REAL <see cref="GatewayClient"/>. The lease is the store's and so is every refusal sentence.
    ///
    /// It stands in for the tunnel: when a restore is asked for, it takes the lease for the asking Director
    /// and then writes the marks that Director's restore would write with the lease held, so a caller that
    /// went through the door sees its seats come back and a caller that did not sees its marks refused.
    /// </summary>
    private sealed class LeaseEnforcingGateway : HttpMessageHandler
    {
        private readonly WorkspaceStore _store;
        private int _next;

        public LeaseEnforcingGateway(WorkspaceStore store) => _store = store;

        /// <summary>Every restore asked for at the one door that grants the lease, in order.</summary>
        public List<WorkspaceRestoreRequest> RestoreRequests { get; } = new();

        /// <summary>The Gateway's own sentence for every restore mark it refused, in order. A bring back that
        /// went through the door writes no refused mark at all, so an entry here IS the defect.</summary>
        public List<string> RefusedMarks { get; } = new();

        /// <summary>Every route asked for that this fake does not serve. A test asserts it is empty, so a
        /// path that quietly took an unserved route cannot pass by looking like a Gateway failure.</summary>
        public List<string> Unserved { get; } = new();

        /// <summary>When set, a Director already holds the lease when the restore is asked for, so the store
        /// refuses it in its own words.</summary>
        public string? AnotherDirectorIsAlreadyRestoring { get; set; }

        /// <summary>How many sessions the spawn door was asked to start. The fixed way up never asks it
        /// directly - the far end of the tunnel does - so this staying at zero is part of the proof.</summary>
        public int Spawns { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);

            // THE FLEET ROSTER, served empty: nothing is running. It is here so that a caller which restores
            // IN-PROCESS - the shipped build - gets as far as its first restore mark and fails on the LEASE,
            // which is the defect, rather than on a route this fake happens not to serve. A test whose red is
            // produced by the rig proves nothing about the product.
            if (request.Method == HttpMethod.Get && path == "/sessions")
                return Json(HttpStatusCode.OK, """{"sessions":[],"directors":[]}""");

            if (request.Method == HttpMethod.Get && path == "/gateway/workspaces")
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { workspaces = _store.List() }, Options));

            if (request.Method == HttpMethod.Get && path.StartsWith("/gateway/workspaces/", StringComparison.Ordinal)
                && !path.Contains("/restore", StringComparison.Ordinal))
            {
                var doc = _store.Get(Slug(path));
                return doc is null
                    ? Json(HttpStatusCode.NotFound, """{"error":"no such workspace"}""")
                    : Json(HttpStatusCode.OK, JsonSerializer.Serialize(doc, Options));
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/restore", StringComparison.Ordinal))
                return TakeTheRestore(Slug(path[..^"/restore".Length]), body);

            if (request.Method == HttpMethod.Post && path.EndsWith("/restore/marks", StringComparison.Ordinal))
                return WriteTheMark(Slug(path[..^"/restore/marks".Length]), body);

            if (request.Method == HttpMethod.Post && path.EndsWith("/sessions", StringComparison.Ordinal))
            {
                Spawns++;
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(
                    new SessionDto { SessionId = $"spawned-{++_next}", DirectorId = ThisDirectorId }, Options));
            }

            Unserved.Add($"{request.Method} {path}");
            return Json(HttpStatusCode.NotFound, """{"error":"this fake Gateway does not serve that route"}""");
        }

        /// <summary>
        /// <c>POST /gateway/workspaces/{id}/restore</c> - the ONE place the restore lease is granted, and then
        /// the tunnel, stood in for: the marks the named Director's own restore would write while holding it.
        /// </summary>
        private HttpResponseMessage TakeTheRestore(string id, string? body)
        {
            var asked = JsonSerializer.Deserialize<WorkspaceRestoreRequest>(body ?? "{}", Options)!;
            RestoreRequests.Add(asked);

            var doc = _store.Get(id);
            if (doc is null) return Json(HttpStatusCode.NotFound, """{"error":"no such workspace"}""");

            if (AnotherDirectorIsAlreadyRestoring is { } other)
                _store.TakeRestoreLease(id, other, null, DateTime.UtcNow);

            var director = asked.DirectorId!;
            try
            {
                _store.TakeRestoreLease(id, director, null, DateTime.UtcNow);
            }
            catch (WorkspaceConflictException ex)
            {
                return Json(HttpStatusCode.Conflict, JsonSerializer.Serialize(new { error = ex.Message }));
            }

            var seats = asked.Seats ?? doc.Seats
                .Where(s => s.Restore?.Decision == WorkspaceRestoreDecisions.Restore
                            && string.IsNullOrWhiteSpace(s.RestoredSessionId))
                .Select(s => s.SessionId!)
                .ToList();

            foreach (var seat in seats)
            {
                var token = Guid.NewGuid().ToString("N");
                _store.RecordRestoreMark(id, new WorkspaceRestoreMark
                {
                    DirectorId = director,
                    Kind = WorkspaceRestoreMarkKinds.Started,
                    SeatSessionId = seat,
                    Token = token,
                    RequestedBySessionId = null,
                }, DateTime.UtcNow);

                _store.RecordRestoreMark(id, new WorkspaceRestoreMark
                {
                    DirectorId = director,
                    Kind = WorkspaceRestoreMarkKinds.Restored,
                    SeatSessionId = seat,
                    Token = token,
                    RestoredSessionId = $"back-{seat}",
                    SeedFile = asked.Seeds is not null && asked.Seeds.TryGetValue(seat, out var seed) ? seed : null,
                }, DateTime.UtcNow);
            }

            _store.RecordRestoreMark(id, new WorkspaceRestoreMark
            {
                DirectorId = director,
                Kind = WorkspaceRestoreMarkKinds.Finished,
            }, DateTime.UtcNow);

            return Json(HttpStatusCode.Accepted, JsonSerializer.Serialize(
                new WorkspaceRestoreAccepted { Taken = true, WorkspaceId = id, DirectorId = director, Seats = seats.ToList() },
                Options));
        }

        /// <summary><c>POST /gateway/workspaces/{id}/restore/marks</c> - refused, in the store's own words,
        /// by any Director that does not hold the lease.</summary>
        private HttpResponseMessage WriteTheMark(string id, string? body)
        {
            var mark = JsonSerializer.Deserialize<WorkspaceRestoreMark>(body ?? "{}", Options)!;
            try
            {
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(_store.RecordRestoreMark(id, mark, DateTime.UtcNow), Options));
            }
            catch (WorkspaceConflictException ex)
            {
                RefusedMarks.Add(ex.Message);
                return Json(HttpStatusCode.Conflict, JsonSerializer.Serialize(new { error = ex.Message }));
            }
            catch (WorkspaceValidationException ex)
            {
                RefusedMarks.Add(ex.Message);
                return Json(HttpStatusCode.BadRequest, JsonSerializer.Serialize(new { error = ex.Message }));
            }
        }

        private static JsonSerializerOptions Options => WorkspaceStore.DocumentJsonOptions;

        private static string Slug(string path) => Uri.UnescapeDataString(path["/gateway/workspaces/".Length..]);

        private static HttpResponseMessage Json(HttpStatusCode status, string body)
            => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    // ================= the rig =================

    /// <summary>The whole way up, over one fake Gateway: a record with a lead and a worker under it, both
    /// owed, and every seam the real Director wires at start-up.</summary>
    private (IDirectorWayUp WayUp, LeaseEnforcingGateway Gateway, GatewayClient Client) Rig()
    {
        var store = new WorkspaceStore(_harness.Open());
        store.Create(WayUpTestRig.Record(
            WorkspaceId,
            Shutdown,
            new[]
            {
                Owed("lead-a", "Cube Coordinator", sortOrder: 0),
                Owed("worker-a", "Cube Coordinator - QA", reportsTo: "lead-a", sortOrder: 1),
            }), Shutdown);

        var gateway = new LeaseEnforcingGateway(store);
        var client = new GatewayClient(
            new GatewayConfig { Url = "http://gateway.example:7878", Token = "test-token" },
            directorId: ThisDirectorId,
            version: "1.0.0",
            gateway);

        var wayUp = new DirectorWayUp(
            new GatewayClientWayUp(() => client, ThisDirectorId),
            new DirectorRestoreWayUp(() => client, ThisDirectorId),
            WayUpTestRig.ThisMachine,
            () => WayUpTestRig.ThisDirector,
            () => Shutdown.AddMinutes(6));

        return (wayUp, gateway, client);
    }

    // ================= the tests =================

    /// <summary>
    /// THE DEFECT, PINNED. "Bring back" on the start-up window asks the Gateway's restore door - the one place
    /// the restore lease is granted - naming this Director, both seats and the seed file each one was given.
    /// Both sessions come back, and NOT ONE MARK IS REFUSED.
    ///
    /// On the code of 25 September 2026 this fails exactly as the owner saw it: no restore is ever asked for,
    /// the in-process restore's first mark is refused because nobody holds the lease, and nothing comes back.
    /// </summary>
    [Fact]
    public async Task Bring_back_asks_the_Gateway_restore_door_so_the_lease_is_held_and_the_sessions_come_back()
    {
        var (wayUp, gateway, client) = Rig();
        using (client)
        {
            var result = await wayUp.BringBackAsync(
                new WayUpBringBackRequest(WorkspaceId, new[] { "lead-a" }), CancellationToken.None);

            // THE REPORTED SYMPTOM, ASSERTED FIRST, so that a red here reads as the owner's own complaint
            // rather than as a missing detail: the window says the sessions came back, and not one restore
            // mark was refused. The refusal list is the Gateway's own lease rule, over the Gateway's own
            // store, saying the writer was entitled to write what its restore did.
            Assert.Empty(gateway.RefusedMarks);
            Assert.True(result.Started, result.Refusal ?? "the bring back refused with no reason");
            Assert.Null(result.Refusal);
            Assert.Equal("2 sessions came back.", result.Message);
            Assert.Equal(new[] { "back-lead-a", "back-worker-a" }, result.Seats.Select(s => s.RestoredSessionId).ToArray());

            // The one door, and what was named at it: this Director, both seats, and a seed file for each.
            var asked = Assert.Single(gateway.RestoreRequests);
            Assert.Equal(ThisDirectorId, asked.DirectorId);
            Assert.Equal(new[] { "lead-a", "worker-a" }, asked.Seats?.ToArray());
            Assert.Equal(Path.Combine(_folder, WayUpWords.SeedFileName("lead-a", "Cube Coordinator")), asked.Seeds?["lead-a"]);
            Assert.Equal(Path.Combine(_folder, WayUpWords.SeedFileName("worker-a", "Cube Coordinator - QA")), asked.Seeds?["worker-a"]);

            // NOTHING WENT ANYWHERE THIS FAKE DOES NOT SERVE, so no assertion above was satisfied - or
            // broken - by a route that answered 404 for a reason of the rig's own.
            Assert.Empty(gateway.Unserved);
        }
    }

    /// <summary>
    /// FILE, RESTART HISTORY IS THE SAME DEFECT AND THE SAME FIX, because both surfaces share
    /// <see cref="DirectorWayUp"/>. This reads the history the way the window does, takes the record it
    /// offers, and brings it back - so a fix that only reached the start-up path would not pass here.
    ///
    /// The record is deliberately read at a moment PAST the start-up offer window
    /// (<see cref="DirectorWayUp.OfferedForDays"/>), which is the case the history exists for: it has stopped
    /// interrupting the owner at start-up and it still carries a working offer.
    /// </summary>
    [Fact]
    public async Task Restart_history_brings_a_record_back_through_the_same_door()
    {
        var store = new WorkspaceStore(_harness.Open());
        store.Create(WayUpTestRig.Record(
            WorkspaceId,
            Shutdown,
            new[] { Owed("lead-a", "Cube Coordinator", sortOrder: 0) }), Shutdown);

        var gateway = new LeaseEnforcingGateway(store);
        using var client = new GatewayClient(
            new GatewayConfig { Url = "http://gateway.example:7878", Token = "test-token" },
            directorId: ThisDirectorId,
            version: "1.0.0",
            gateway);

        var wayUp = new DirectorWayUp(
            new GatewayClientWayUp(() => client, ThisDirectorId),
            new DirectorRestoreWayUp(() => client, ThisDirectorId),
            WayUpTestRig.ThisMachine,
            () => WayUpTestRig.ThisDirector,
            () => Shutdown.AddDays(DirectorWayUp.OfferedForDays + 1));

        var history = await wayUp.ReadHistoryAsync(CancellationToken.None);
        Assert.False(history.Refused);
        var entry = Assert.Single(history.Entries);
        Assert.NotNull(entry.Offer);
        Assert.NotNull(entry.NotOfferedAtStartUpLabel);

        var row = Assert.Single(entry.Offer!.Rows, r => r.Kind == WayUpRowKind.BringBack);
        var result = await wayUp.BringBackAsync(
            new WayUpBringBackRequest(entry.WorkspaceId, new[] { row.RowId }), CancellationToken.None);

        Assert.Empty(gateway.RefusedMarks);
        Assert.True(result.Started, result.Refusal ?? "the bring back refused with no reason");
        Assert.Equal("One session came back.", result.Message);
        Assert.Equal("back-lead-a", Assert.Single(result.Seats).RestoredSessionId);

        var asked = Assert.Single(gateway.RestoreRequests);
        Assert.Equal(ThisDirectorId, asked.DirectorId);
        Assert.Equal(new[] { "lead-a" }, asked.Seats?.ToArray());
        Assert.Empty(gateway.Unserved);
    }

    /// <summary>
    /// A RESTORE THE DOOR WILL NOT TAKE REACHES THE WINDOW IN THE GATEWAY'S OWN WORDS, and starts nothing.
    ///
    /// The sentence asserted is the one the DOOR produces when another Director already holds the lease. The
    /// shipped build never reaches that door, so it cannot produce this sentence at all - it produces the
    /// marks route's refusal instead, which is a different sentence about a different thing.
    /// </summary>
    [Fact]
    public async Task A_restore_the_door_refuses_reaches_the_window_in_the_Gateway_own_words()
    {
        var (wayUp, gateway, client) = Rig();
        gateway.AnotherDirectorIsAlreadyRestoring = "some-other-director";
        using (client)
        {
            var result = await wayUp.BringBackAsync(
                new WayUpBringBackRequest(WorkspaceId, new[] { "lead-a" }), CancellationToken.None);

            Assert.False(result.Started);
            Assert.Contains("is already restoring workspace", result.Refusal);
            Assert.Contains("some-other-director", result.Refusal);
            Assert.Empty(result.Seats);

            // Nothing was started: the door refused before the order was relayed, and no seat was marked.
            Assert.Equal(0, gateway.Spawns);
            Assert.Empty(gateway.RefusedMarks);
        }
    }
}
