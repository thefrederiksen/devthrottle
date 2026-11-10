using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Reports;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using CcDirector.Gateway.Workspaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// A TEAM DIRECTOR'S SHUTDOWN RECORD (devthrottle_internal#2829, found by piece 3.1). A Director's smart shutdown captures
/// a record of its own sessions (<c>POST /gateway/workspaces</c>) and then saves its drain's judgments onto that record
/// (<c>PUT /gateway/workspaces/{id}</c>). Inside a team both were undeclared and refused - "not been opened to teams
/// yet" - so a team Director's shutdown left no record to restart from, while the identical write with a personal key
/// was accepted. Over a real database, a hosted device registry with Teams released, a real Director registry, the real
/// team gate and the team caller wired as the Gateway host wires it: Alice and Bob (Developers) each have a Director set
/// up in the team on their own key.
/// </summary>
public sealed class TeamShutdownRecordTests : IAsyncLifetime
{
    private const string Owner = "sub-record-owner";
    private const string Alice = "sub-record-alice";
    private const string Bob = "sub-record-bob";
    private const string Carol = "sub-record-carol";

    /// <summary>When the Directors said Hello: a fixed time, because it is stored, not judged.</summary>
    private static readonly DateTime HelloAt = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly TeamRegistry _teams;
    private readonly DeviceRegistry _devices;
    private readonly DirectorRegistry _directors;
    private readonly HostedTenantBoundary _boundary;
    private readonly TeamCallerOwnership _ownership;
    private readonly TeamEndpointGate _gate;
    private readonly WorkspaceStore _store;
    private readonly RepoStateStore _repoState;
    private readonly TenantId _teamA;
    private readonly TenantId _teamB;
    private readonly TenantId _alicePersonal;
    private readonly string _aliceKey;
    private readonly string _bobKey;
    private readonly string _aliceTeamBKey;
    private readonly string _alicePersonalKey;
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public TeamShutdownRecordTests()
    {
        var db = _harness.Open();
        var tenants = new TenantRegistry(db);
        _teams = new TeamRegistry(db, tenants);
        _devices = new DeviceRegistry(db, _harness.LegacyPath("devices.json"), isHosted: true, teamsReleased: true);
        _directors = new DirectorRegistry(_harness.LegacyPath("instances"));
        _boundary = new HostedTenantBoundary(new AsyncLocalTenantContext(), _devices, hosted: false);
        _ownership = new TeamCallerOwnership(_directors, new PushedSessionStore(), _devices, new SessionTurnStore(db), _boundary,
            new SessionKeyRegistry(db, isHosted: true));
        _gate = new TeamEndpointGate(new TeamAccess(_teams), _teams, tenants, _boundary, _ownership);
        _store = new WorkspaceStore(db);
        _repoState = new RepoStateStore(db);

        _teamA = new TenantId(_teams.CreateTeam(Owner, "A").Team!.TeamId);
        _teamB = new TenantId(_teams.CreateTeam(Owner, "B").Team!.TeamId);
        Assert.True(_teams.AddMember(_teamA.Value, Alice, TeamRole.Developer).IsDone);
        Assert.True(_teams.AddMember(_teamA.Value, Bob, TeamRole.Developer).IsDone);
        Assert.True(_teams.AddMember(_teamA.Value, Carol, TeamRole.Collaborator).IsDone);
        Assert.True(_teams.AddMember(_teamB.Value, Alice, TeamRole.Developer).IsDone);

        _aliceKey = TeamDirector(_teamA, Alice, "director-alice", "ALICE-PC");
        _bobKey = TeamDirector(_teamA, Bob, "director-bob", "BOB-PC");
        _aliceTeamBKey = TeamDirector(_teamB, Alice, "director-alice-b", "ALICE-PC");

        _alicePersonal = tenants.MintOrLookupBySubject(Alice, null);
        _alicePersonalKey = _devices.RegisterForTenant(_alicePersonal, Alice, "personal|director-alice-own", "ALICE-PC").DeviceKey;
        _directors.RegisterFromStream("director-alice-own", "ALICE-PC", "u", "1.0", 1, HelloAt, _alicePersonal, "director-alice-own",
            "device:personal|director-alice-own");
    }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddLogging();
        _app = builder.Build();
        _app.UseRouting();
        // What the auth middleware leaves on a request made with a device key, or with a session key of a Director.
        _app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Headers.TryGetValue("X-Test-Device-Key", out var key))
            {
                var resolution = _devices.ResolveCredential(key.ToString());
                Assert.Equal(DeviceCredentialResolutionKind.Active, resolution.Kind);
                ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = resolution.Identity;
            }
            if (ctx.Request.Headers.TryGetValue("X-Test-Session-Of", out var director))
                ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] = new SessionCredentialIdentity(Guid.NewGuid(), _teamA, director.ToString());
            await next();
        });
        _app.Use((ctx, next) => _gate.RunAsync(ctx, () => next()));
        WorkspaceEndpoints.Map(_app, _store,
            // Every Director here is connected and running nothing: a real, empty answer.
            connectedFleet: _ => (FleetObservation.Observed, Array.Empty<SessionDto>()),
            lookupDirector: id => new[] { _teamA, _teamB, _alicePersonal }.Select(t => _directors.Get(t, id)).FirstOrDefault(d => d is not null),
            // The Director takes the restore; the seats it would start are not these tests' business.
            sendRestore: (_, _, _) => Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success("{\"accepted\":true}")),
            // The credential is the named Director's, so the team check is the only one that can refuse here.
            callerIsDirector: (_, _) => true,
            passDevReports: (_, req, _) => new WorkspaceDevReportPassResult { FromSessionId = req.SeatSessionId, ToSessionId = req.SeatSessionId },
            // As the Gateway host wires it (GatewayHost.TeamCallerFor): the caller in a team, as the gate allowed them.
            teamCaller: ctx => _boundary.ResolveRequestTenant(ctx) is { } tenant && _teams.IsTeam(tenant)
                ? _ownership.CallerIn(tenant,
                    string.Equals(TeamEndpointGate.AllowedTeam(ctx), tenant.Value, StringComparison.Ordinal) ? TeamEndpointGate.AllowedCaller(ctx) : null,
                    AuthMiddleware.AuthenticatedDevice(ctx), AuthMiddleware.CallingSession(ctx))
                : null);
        RepoStateEndpoints.Map(_app, _repoState, _boundary, teamCaller: (ctx, tenant) => _teams.IsTeam(tenant)
            ? _ownership.CallerIn(tenant,
                string.Equals(TeamEndpointGate.AllowedTeam(ctx), tenant.Value, StringComparison.Ordinal) ? TeamEndpointGate.AllowedCaller(ctx) : null,
                AuthMiddleware.AuthenticatedDevice(ctx), AuthMiddleware.CallingSession(ctx))
            : null);
        await _app.StartAsync();
        _http = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        _devices.Dispose();
        _harness.Dispose();
    }

    private string TeamDirector(TenantId team, string subject, string directorId, string machine)
    {
        var deviceId = HostedEnrollmentEndpoint.TeamScopedDeviceId(team.Value, subject, directorId);
        var key = _devices.RegisterForTenant(team, subject, deviceId, machine).DeviceKey;
        _directors.RegisterFromStream(directorId, machine, "u", "1.0", 1, HelloAt, team, directorId, "device:" + deviceId);
        return key;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod method, string path, string key,
        object? body = null, string? sessionOf = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Test-Device-Key", key);
        if (sessionOf is not null)
            request.Headers.Add("X-Test-Session-Of", sessionOf);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: WorkspaceStore.DocumentJsonOptions);
        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> CaptureAsync(string key, string id, string directorId, string? sessionOf = null) =>
        SendAsync(HttpMethod.Post, "/gateway/workspaces", key,
            new WorkspaceCaptureRequest { Id = id, Name = id, DirectorId = directorId, Reason = "smart shutdown" }, sessionOf);

    private static void AssertTeamRefusal((HttpStatusCode Status, JsonElement Body) answer)
    {
        Assert.Equal(HttpStatusCode.Forbidden, answer.Status);
        Assert.Equal(TeamEndpointGate.RefusalCode, answer.Body.GetProperty("code").GetString());
    }

    // ---- the record is written ---------------------------------------------------------------------------------------

    // Shows: Alice's team Director, on its own team key, captures the record of its own shutdown and then saves its
    // drain's judgments onto it - the two writes a smart shutdown makes. Before the fix the capture was refused with
    // "not been opened to teams yet", so a team Director's shutdown left no restart record.
    [Fact]
    public async Task Capture_TheTeamDirectorsOwnRecord_IsWrittenAndCanBeSaved()
    {
        var captured = await CaptureAsync(_aliceKey, "alice-shutdown", "director-alice");

        Assert.Equal(HttpStatusCode.Created, captured.Status);
        var doc = _store.Get("alice-shutdown");
        Assert.NotNull(doc);
        Assert.Equal("director-alice", doc!.DirectorId);

        doc.Description = "every session handed over";
        var saved = await SendAsync(HttpMethod.Put, "/gateway/workspaces/alice-shutdown", _aliceKey, doc);
        Assert.Equal(HttpStatusCode.OK, saved.Status);
        Assert.Equal("every session handed over", _store.Get("alice-shutdown")!.Description);
    }

    // Shows: a drain driven by a session of Alice's own Director writes the record too, as it does on a personal account.
    [Fact]
    public async Task Capture_BySessionKeyOfTheOwnDirector_IsWritten()
    {
        Assert.Equal(HttpStatusCode.Created, (await CaptureAsync(_aliceKey, "alice-by-session", "director-alice", sessionOf: "director-alice")).Status);
    }

    // ---- refused -----------------------------------------------------------------------------------------------------

    // Shows: Alice cannot capture a record of Bob's Director, nor of a Director nobody in the team has, and nothing is
    // written.
    [Theory]
    [InlineData("director-bob")]
    [InlineData("director-nobody")]
    public async Task Capture_AnotherMembersDirectorOrAnUnknownOne_IsRefusedAndNothingIsWritten(string directorId)
    {
        AssertTeamRefusal(await CaptureAsync(_aliceKey, "not-alices", directorId));
        Assert.Null(_store.Get("not-alices"));
    }

    // Shows: a session key of Alice's Director cannot capture Bob's Director either.
    [Fact]
    public async Task Capture_BySessionKeyNamingAnotherMembersDirector_IsRefused()
    {
        AssertTeamRefusal(await CaptureAsync(_aliceKey, "bob-by-session", "director-bob", sessionOf: "director-alice"));
        Assert.Null(_store.Get("bob-by-session"));
    }

    // Shows: a key bound to another team cannot capture this team's Director: its requests are in its own team, where
    // that Director is nobody's.
    [Fact]
    public async Task Capture_AKeyBoundToAnotherTeam_IsRefused()
    {
        AssertTeamRefusal(await CaptureAsync(_aliceTeamBKey, "cross-team", "director-alice"));
        Assert.Null(_store.Get("cross-team"));
    }

    // Shows: Alice cannot overwrite Bob's record, and cannot write a new workspace by hand inside the team - it names no
    // Director, so it cannot be shown to be anyone's. Bob's record is unchanged.
    [Fact]
    public async Task Save_AnotherMembersRecordOrANewHandWrittenOne_IsRefused()
    {
        Assert.Equal(HttpStatusCode.Created, (await CaptureAsync(_bobKey, "bob-shutdown", "director-bob")).Status);
        var bobs = _store.Get("bob-shutdown")!;
        bobs.Description = "overwritten by Alice";

        AssertTeamRefusal(await SendAsync(HttpMethod.Put, "/gateway/workspaces/bob-shutdown", _aliceKey, bobs));
        Assert.NotEqual("overwritten by Alice", _store.Get("bob-shutdown")!.Description);

        AssertTeamRefusal(await SendAsync(HttpMethod.Put, "/gateway/workspaces/by-hand", _aliceKey,
            new WorkspaceDocument { Id = "by-hand", Name = "by hand", Origin = WorkspaceOrigins.Authored }));
        Assert.Null(_store.Get("by-hand"));
    }

    // Shows: deleting a record is not one of the two shutdown writes, so it stays refused in a team, even of the
    // caller's own record.
    [Fact]
    public async Task Delete_TheCallersOwnRecord_StaysRefused()
    {
        Assert.Equal(HttpStatusCode.Created, (await CaptureAsync(_aliceKey, "alice-keep", "director-alice")).Status);

        AssertTeamRefusal(await SendAsync(HttpMethod.Delete, "/gateway/workspaces/alice-keep", _aliceKey));
        Assert.NotNull(_store.Get("alice-keep"));
    }

    // Shows: a Contributor runs no sessions, so the gate refuses their capture and save before the endpoint is reached.
    [Theory]
    [InlineData("POST", "/gateway/workspaces")]
    [InlineData("PUT", "/gateway/workspaces/{id}")]
    public void Check_AContributorsShutdownWrite_IsRefused(string method, string pattern)
    {
        var verdict = _gate.Check(method, pattern, _ => null, _teamA, () => Carol,
            _ => _ownership.Whose(_teamA, Carol, pattern, _ => null, method));
        Assert.Equal(TeamGateOutcome.Refused, verdict.Outcome);

        Assert.Equal(TeamGateOutcome.Allowed, _gate.Check(method, pattern, _ => null, _teamA, () => Alice,
            _ => _ownership.Whose(_teamA, Alice, pattern, _ => null, method)).Outcome);
    }

    // Shows: the restart record's routes, down and back up, are answered as the caller's for the endpoint to check;
    // deleting a record and the link requests stay Unknown.
    [Theory]
    [InlineData("POST", "/gateway/workspaces", true)]
    [InlineData("PUT", "/gateway/workspaces/{id}", true)]
    [InlineData("GET", "/gateway/workspaces/{id}", true)]
    [InlineData("POST", "/gateway/workspaces/crash-records", true)]
    [InlineData("POST", "/gateway/workspaces/{id}/restore", true)]
    [InlineData("POST", "/gateway/workspaces/{id}/restore/marks", true)]
    [InlineData("POST", "/gateway/workspaces/{id}/restore/dev-reports", true)]
    [InlineData("POST", "/gateway/repostate", true)]
    [InlineData("DELETE", "/gateway/workspaces/{id}", false)]
    [InlineData("GET", "/fleet/link-requests", false)]
    public void Whose_TheRestartRecordRoutes_AreTheCallersForTheEndpointToCheck(string method, string pattern, bool callers)
    {
        Assert.Equal(callers ? TeamOwnership.Callers : TeamOwnership.Unknown,
            _ownership.Whose(_teamA, Alice, pattern, _ => "x", method));
    }

    // ---- the way back up ---------------------------------------------------------------------------------------------

    private static CrashJournalDto Journal(string deadDirector) => new()
    {
        DirectorId = deadDirector,
        Pid = 4242,
        MachineName = "ALICE-PC",
        User = "u",
        StartedAtUtc = new DateTimeOffset(HelloAt.AddHours(-2)),
        LastUpdatedUtc = new DateTimeOffset(HelloAt.AddHours(-1)),
        Sessions = new List<CrashJournalSessionDto> { new() { SessionId = "33333333-3333-3333-3333-333333333333", Name = "Worker", RepoPath = "D:/repos/alice" } },
    };

    private Task<(HttpStatusCode Status, JsonElement Body)> CrashRecordAsync(string key, string directorId) =>
        SendAsync(HttpMethod.Post, "/gateway/workspaces/crash-records", key, new WorkspaceCrashRecordRequest
        {
            Id = WorkspaceCrashRecords.IdFor(Journal(directorId)),
            DirectorId = directorId,
            Journal = Journal(directorId),
        });

    // Shows: after a restart, Alice's own team Director reads its record, asks for the restore, writes what the restore
    // did and passes a seat's dev reports - the whole way back up a smart restart takes. Each was refused before as
    // "not been opened to teams yet".
    [Fact]
    public async Task WayUp_TheTeamDirectorsOwnRecord_IsReadRestoredMarkedAndItsReportsPassed()
    {
        Assert.Equal(HttpStatusCode.Created, (await CaptureAsync(_aliceKey, "alice-up", "director-alice")).Status);

        var read = await SendAsync(HttpMethod.Get, "/gateway/workspaces/alice-up", _aliceKey);
        Assert.Equal(HttpStatusCode.OK, read.Status);
        Assert.Equal("director-alice", read.Body.GetProperty("directorId").GetString());

        var restored = await SendAsync(HttpMethod.Post, "/gateway/workspaces/alice-up/restore", _aliceKey,
            new WorkspaceRestoreRequest { DirectorId = "director-alice" });
        Assert.Equal(HttpStatusCode.Accepted, restored.Status);

        var marked = await SendAsync(HttpMethod.Post, "/gateway/workspaces/alice-up/restore/marks", _aliceKey,
            new WorkspaceRestoreMark { DirectorId = "director-alice", Kind = WorkspaceRestoreMarkKinds.Finished });
        Assert.Equal(HttpStatusCode.OK, marked.Status);

        var passed = await SendAsync(HttpMethod.Post, "/gateway/workspaces/alice-up/restore/dev-reports", _aliceKey,
            new WorkspaceDevReportPassRequest { DirectorId = "director-alice", SeatSessionId = "seat-1" });
        Assert.Equal(HttpStatusCode.OK, passed.Status);
    }

    // Shows: Alice's own team Director sends the crash journal of the Director that ran before it, and pushes its own
    // repositories.
    [Fact]
    public async Task WayUp_TheOwnDirectorsCrashRecordAndRepositoryPush_AreAccepted()
    {
        var crash = await CrashRecordAsync(_aliceKey, "director-alice");
        Assert.True(crash.Status == HttpStatusCode.Created, crash.Body.ToString());

        var pushed = await SendAsync(HttpMethod.Post, RepoStateEndpoints.Path, _aliceKey,
            new RepoStatePushRequest { DirectorId = "director-alice", MachineName = "ALICE-PC" });
        Assert.Equal(HttpStatusCode.OK, pushed.Status);
    }

    // Shows: Alice cannot read, restore, mark or pass the reports of Bob's record - nor restore her own record onto
    // Bob's Director - and a record that does not exist is refused the same way, so the answer never says which
    // records a colleague has.
    [Fact]
    public async Task WayUp_AnotherMembersRecordOrDirector_IsRefused()
    {
        Assert.Equal(HttpStatusCode.Created, (await CaptureAsync(_bobKey, "bob-up", "director-bob")).Status);
        Assert.Equal(HttpStatusCode.Created, (await CaptureAsync(_aliceKey, "alice-own-up", "director-alice")).Status);

        AssertTeamRefusal(await SendAsync(HttpMethod.Get, "/gateway/workspaces/bob-up", _aliceKey));
        AssertTeamRefusal(await SendAsync(HttpMethod.Get, "/gateway/workspaces/no-such-record", _aliceKey));
        AssertTeamRefusal(await SendAsync(HttpMethod.Post, "/gateway/workspaces/bob-up/restore", _aliceKey,
            new WorkspaceRestoreRequest { DirectorId = "director-alice" }));
        AssertTeamRefusal(await SendAsync(HttpMethod.Post, "/gateway/workspaces/alice-own-up/restore", _aliceKey,
            new WorkspaceRestoreRequest { DirectorId = "director-bob" }));
        AssertTeamRefusal(await SendAsync(HttpMethod.Post, "/gateway/workspaces/bob-up/restore/marks", _aliceKey,
            new WorkspaceRestoreMark { DirectorId = "director-alice", Kind = WorkspaceRestoreMarkKinds.Finished }));
        AssertTeamRefusal(await SendAsync(HttpMethod.Post, "/gateway/workspaces/alice-own-up/restore/marks", _aliceKey,
            new WorkspaceRestoreMark { DirectorId = "director-bob", Kind = WorkspaceRestoreMarkKinds.Finished }));
        AssertTeamRefusal(await SendAsync(HttpMethod.Post, "/gateway/workspaces/bob-up/restore/dev-reports", _aliceKey,
            new WorkspaceDevReportPassRequest { DirectorId = "director-alice", SeatSessionId = "seat-1" }));
        Assert.Null(_store.Get("bob-up")!.RestoreLease);
    }

    // Shows: a crash record and a repository push for Bob's Director, or for a Director nobody has, are refused, and a
    // key bound to another team cannot send one for this team's Director.
    [Theory]
    [InlineData("director-bob")]
    [InlineData("director-nobody")]
    public async Task WayUp_ACrashRecordOrPushForAnotherMembersOrAnUnknownDirector_IsRefused(string directorId)
    {
        AssertTeamRefusal(await CrashRecordAsync(_aliceKey, directorId));
        AssertTeamRefusal(await SendAsync(HttpMethod.Post, RepoStateEndpoints.Path, _aliceKey,
            new RepoStatePushRequest { DirectorId = directorId, MachineName = "ALICE-PC" }));
        AssertTeamRefusal(await CrashRecordAsync(_aliceTeamBKey, "director-alice"));
    }

    // Shows: a Contributor runs no sessions, so the gate refuses every way-up route before the endpoint is reached.
    [Theory]
    [InlineData("GET", "/gateway/workspaces/{id}")]
    [InlineData("POST", "/gateway/workspaces/crash-records")]
    [InlineData("POST", "/gateway/workspaces/{id}/restore")]
    [InlineData("POST", "/gateway/workspaces/{id}/restore/marks")]
    [InlineData("POST", "/gateway/workspaces/{id}/restore/dev-reports")]
    [InlineData("POST", "/gateway/repostate")]
    public void Check_AContributorsWayUpRequest_IsRefused(string method, string pattern)
    {
        Assert.Equal(TeamGateOutcome.Refused, _gate.Check(method, pattern, _ => null, _teamA, () => Carol,
            _ => _ownership.Whose(_teamA, Carol, pattern, _ => null, method)).Outcome);
    }

    // ---- review finding 2: the way back up before the new Director's Hello -------------------------------------------

    // Shows: a team Director that has said goodbye, and whose new process has not yet said Hello, still reads the
    // record its shutdown left - its device row is the durable record that the Director is Alice's. A colleague still
    // cannot read it. Before, the read asked only the connected Directors and was refused in that window.
    [Fact]
    public async Task WayUp_BeforeTheNewDirectorsHello_TheOwnRecordIsStillRead()
    {
        var downKey = _devices.RegisterForTenant(_teamA, Alice,
            HostedEnrollmentEndpoint.TeamScopedDeviceId(_teamA.Value, Alice, "director-alice-down"), "ALICE-PC").DeviceKey;
        _store.Create(new WorkspaceDocument { Id = "alice-down", Name = "alice-down", DirectorId = "director-alice-down" }, HelloAt);
        Assert.Null(_directors.Get(_teamA, "director-alice-down"));

        var read = await SendAsync(HttpMethod.Get, "/gateway/workspaces/alice-down", downKey);

        Assert.Equal(HttpStatusCode.OK, read.Status);
        Assert.Equal("director-alice-down", read.Body.GetProperty("directorId").GetString());
        AssertTeamRefusal(await SendAsync(HttpMethod.Get, "/gateway/workspaces/alice-down", _bobKey));
    }

    // ---- handler census: every restart record route the gate lets through is checked by its handler -------------------

    public static IEnumerable<object[]> RecordRoutes() =>
        TeamCallerOwnership.RestartRecordRoutes.Select(r => new object[] { r.Method, r.Pattern });

    // Shows: for every restart record route the gate answers as the caller's for the endpoint to check, the endpoint
    // does check - Alice sending it for Bob's record or Bob's Director is refused. A route added to the list without a
    // handler check fails here, and so does a route listed here that the census does not know how to send.
    [Theory]
    [MemberData(nameof(RecordRoutes))]
    public async Task Census_EveryRecordRouteTheGateLetsThrough_IsRefusedForAColleaguesDirector(string method, string pattern)
    {
        Assert.Equal(HttpStatusCode.Created, (await CaptureAsync(_bobKey, "bob-census", "director-bob")).Status);
        var path = pattern.Replace("{id}", "bob-census", StringComparison.Ordinal);
        object body = (method, pattern) switch
        {
            ("GET", "/gateway/workspaces/{id}") => new { },
            ("PUT", "/gateway/workspaces/{id}") => _store.Get("bob-census")!,
            ("POST", "/gateway/workspaces") => new WorkspaceCaptureRequest { Id = "bob-census-2", Name = "x", DirectorId = "director-bob", Reason = "smart shutdown" },
            ("POST", "/gateway/workspaces/crash-records") => new WorkspaceCrashRecordRequest
                { Id = WorkspaceCrashRecords.IdFor(Journal("director-bob")), DirectorId = "director-bob", Journal = Journal("director-bob") },
            ("POST", "/gateway/workspaces/{id}/restore") => new WorkspaceRestoreRequest { DirectorId = "director-bob" },
            ("POST", "/gateway/workspaces/{id}/restore/marks") => new WorkspaceRestoreMark { DirectorId = "director-bob", Kind = WorkspaceRestoreMarkKinds.Finished },
            ("POST", "/gateway/workspaces/{id}/restore/dev-reports") => new WorkspaceDevReportPassRequest { DirectorId = "director-bob", SeatSessionId = "seat-1" },
            ("POST", RepoStateEndpoints.Path) => new RepoStatePushRequest { DirectorId = "director-bob", MachineName = "BOB-PC" },
            _ => throw new InvalidOperationException($"the census does not know how to send {method} {pattern}; add it here"),
        };

        AssertTeamRefusal(await SendAsync(new HttpMethod(method), path, _aliceKey, method == "GET" ? null : body));
        Assert.Null(_store.Get("bob-census-2"));
    }

    // ---- personal accounts unchanged ---------------------------------------------------------------------------------

    // Shows: on a person's own account the capture and the save are exactly as before.
    [Fact]
    public async Task Capture_OnAPersonalAccount_IsUnchanged()
    {
        Assert.Equal(HttpStatusCode.Created, (await CaptureAsync(_alicePersonalKey, "alice-own", "director-alice-own")).Status);
        var doc = _store.Get("alice-own")!;
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, "/gateway/workspaces/alice-own", _alicePersonalKey, doc)).Status);
    }
}
