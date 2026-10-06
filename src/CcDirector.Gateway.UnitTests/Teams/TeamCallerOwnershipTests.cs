using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// The team gate knows who is calling and whose a request touches (devthrottle_internal#2311). Over a real database, a
/// hosted device registry, a real Director registry and session store: two members of one team each have a Director,
/// registered by its own key the way the tunnel's Hello registers it, and each Director has a session. The gate then
/// runs exactly as production runs it, with the authenticated device identity the auth middleware leaves behind.
/// </summary>
public sealed class TeamCallerOwnershipTests : IDisposable
{
    private const string Owner = "sub-owner";
    private const string Alice = "sub-alice";
    private const string Bob = "sub-bob";
    private const string Carol = "sub-carol";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly TenantRegistry _tenants;
    private readonly TeamRegistry _teams;
    private readonly DeviceRegistry _devices;
    private readonly DirectorRegistry _directors;
    private readonly PushedSessionStore _sessions = new();
    private readonly TeamCallerOwnership _ownership;
    private readonly TeamEndpointGate _gate;
    private readonly string _team;
    private readonly TenantId _tenant;
    private readonly DeviceCredentialIdentity _aliceKey;
    private readonly DeviceCredentialIdentity _bobKey;

    public TeamCallerOwnershipTests()
    {
        _db = _harness.Open();
        _tenants = new TenantRegistry(_db);
        _teams = new TeamRegistry(_db, _tenants);
        _devices = new DeviceRegistry(_db, _harness.LegacyPath("devices.json"), isHosted: true, teamsReleased: true);
        _directors = new DirectorRegistry(_harness.LegacyPath("instances"));
        var boundary = new HostedTenantBoundary(new AsyncLocalTenantContext(), _devices);
        _ownership = new TeamCallerOwnership(_directors, _sessions, _devices, new CcDirector.Gateway.History.SessionTurnStore(_db), boundary, Keys());
        _gate = new TeamEndpointGate(new TeamAccess(_teams), _teams, _tenants, boundary, _ownership);

        _team = _teams.CreateTeam(Owner, "Acme").Team!.TeamId;
        _tenant = new TenantId(_team);
        Assert.True(_teams.AddMember(_team, Alice, TeamRole.Developer).IsDone);
        Assert.True(_teams.AddMember(_team, Bob, TeamRole.Developer).IsDone);
        Assert.True(_teams.AddMember(_team, Carol, TeamRole.Collaborator).IsDone);

        _aliceKey = DirectorWithSession(Alice, "director-alice", "session-alice");
        _bobKey = DirectorWithSession(Bob, "director-bob", "session-bob");
    }

    private CcDirector.Gateway.History.SessionTurnStore Turns() => new(_db);

    private SessionKeyRegistry Keys() => new(_db, isHosted: true);

    private HostedTenantBoundary Boundary() => new(new AsyncLocalTenantContext(), _devices);

    public void Dispose()
    {
        _devices.Dispose();
        _harness.Dispose();
    }

    /// <summary>A member's Director, enrolled into the team, said Hello on its own key, with one session pushed.</summary>
    private DeviceCredentialIdentity DirectorWithSession(string subject, string directorId, string sessionId)
    {
        var deviceId = _team + "|" + directorId;
        var key = _devices.RegisterForTenant(_tenant, subject, deviceId, "M").DeviceKey;
        var identity = _devices.ResolveCredential(key).Identity!;
        _directors.RegisterFromStream(directorId, "M", "u", "1.0", 1, DateTime.UtcNow, _tenant, directorId, "device:" + deviceId);
        _sessions.RegisterConnection(_tenant, directorId, "conn-" + directorId);
        Assert.True(_sessions.ApplySnapshot(_tenant, directorId, "conn-" + directorId, 1, new[] { new SessionDto { SessionId = sessionId } }));
        return identity;
    }

    private static Func<string, string?> Values(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    // ---- Whose ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Whose_ADirectorsOwnSession_IsTheCallersOwn_AnotherMembersIsSomeoneElses()
    {
        Assert.Equal(TeamOwnership.Callers, _ownership.Whose(_tenant, Alice, "/sessions/{sid}/prompt", Values(("sid", "session-alice"))));
        Assert.Equal(TeamOwnership.SomeoneElses, _ownership.Whose(_tenant, Alice, "/sessions/{sid}/prompt", Values(("sid", "session-bob"))));
        // Any route that names the session by {sid}: its transcript is its owner's too.
        Assert.Equal(TeamOwnership.Callers, _ownership.Whose(_tenant, Bob, "/history/{sid}", Values(("sid", "session-bob"))));
        Assert.Equal(TeamOwnership.SomeoneElses, _ownership.Whose(_tenant, Bob, "/history/{sid}", Values(("sid", "session-alice"))));
    }

    [Fact]
    public void Whose_ADirector_IsItsKeysPersons()
    {
        Assert.Equal(TeamOwnership.Callers, _ownership.Whose(_tenant, Bob, "/directors/{id}/sessions", Values(("id", "director-bob"))));
        Assert.Equal(TeamOwnership.SomeoneElses, _ownership.Whose(_tenant, Bob, "/directors/{id}/sessions", Values(("id", "director-alice"))));
        // {directorId} is the Director where a route names it so, even beside an {id} that names something else.
        Assert.Equal(TeamOwnership.Callers, _ownership.Whose(_tenant, Bob, "/directors/{directorId}/triggers/{id}/checks",
            Values(("directorId", "director-bob"), ("id", "trigger-1"))));
    }

    [Fact]
    public void Whose_TheTunnel_IsTheCallersOwnConnection()
    {
        Assert.Equal(TeamOwnership.Callers, _ownership.Whose(_tenant, Alice, "/director-stream", Values()));
        Assert.Equal(TeamOwnership.Callers, _ownership.Whose(_tenant, Alice, "/director-stream/negotiate", Values()));
    }

    [Fact]
    public void Whose_WhatCannotBeShown_IsUnknown()
    {
        // A list across the team, a session or Director the Gateway does not know, a route outside the families.
        Assert.Equal(TeamOwnership.Unknown, _ownership.Whose(_tenant, Alice, "/sessions", Values()));
        Assert.Equal(TeamOwnership.Unknown, _ownership.Whose(_tenant, Alice, "/sessions/{sid}", Values(("sid", "no-such-session"))));
        Assert.Equal(TeamOwnership.Unknown, _ownership.Whose(_tenant, Alice, "/directors/{id}", Values(("id", "no-such-director"))));
        Assert.Equal(TeamOwnership.Unknown, _ownership.Whose(_tenant, Alice, "/history", Values()));
        Assert.Equal(TeamOwnership.Unknown, _ownership.Whose(_tenant, Alice, "/missions/{id}", Values(("id", "director-alice"))));
        Assert.Equal(TeamOwnership.Unknown, _ownership.Whose(_tenant, Alice, null, Values()));
        Assert.Equal(TeamOwnership.Unknown, _ownership.Whose(_tenant, " ", "/director-stream", Values()));

        // A Director registered by something other than a device key names no owner.
        _directors.RegisterFromStream("director-token", "M", "u", "1.0", 1, DateTime.UtcNow, _tenant, "t", "machine-token");
        Assert.Equal(TeamOwnership.Unknown, _ownership.Whose(_tenant, Alice, "/directors/{id}", Values(("id", "director-token"))));
    }

    [Fact]
    public void Constructor_AndWhose_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new TeamCallerOwnership(null!, _sessions, _devices, Turns(), Boundary(), Keys()));
        Assert.Throws<ArgumentNullException>(() => new TeamCallerOwnership(_directors, null!, _devices, Turns(), Boundary(), Keys()));
        Assert.Throws<ArgumentNullException>(() => new TeamCallerOwnership(_directors, _sessions, null!, Turns(), Boundary(), Keys()));
        Assert.Throws<ArgumentNullException>(() => new TeamCallerOwnership(_directors, _sessions, _devices, null!, Boundary(), Keys()));
        Assert.Throws<ArgumentNullException>(() => new TeamCallerOwnership(_directors, _sessions, _devices, Turns(), null!, Keys()));
        Assert.Throws<ArgumentNullException>(() => new TeamCallerOwnership(_directors, _sessions, _devices, Turns(), Boundary(), null!));
        Assert.Throws<ArgumentNullException>(() => _ownership.Whose(_tenant, Alice, "/sessions", null!));
    }

    // ---- The gate, as production runs it -------------------------------------------------------------------------

    private static DefaultHttpContext Request(string method, string pattern, DeviceCredentialIdentity? device,
        params (string Name, string Value)[] routeValues)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Response.Body = new MemoryStream();
        ctx.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse(pattern), 0, EndpointMetadataCollection.Empty, pattern));
        foreach (var (name, value) in routeValues)
            ctx.Request.RouteValues[name] = value;
        if (device is not null)
            ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = device;
        return ctx;
    }

    private async Task<(bool Reached, int Status, string? Error)> Run(DefaultHttpContext ctx)
    {
        var reached = false;
        await _gate.RunAsync(ctx, () => { reached = true; return Task.CompletedTask; });
        string? error = null;
        if (!reached)
        {
            ctx.Response.Body.Position = 0;
            error = JsonDocument.Parse(ctx.Response.Body).RootElement.GetProperty("error").GetString();
        }
        return (reached, ctx.Response.StatusCode, error);
    }

    [Fact]
    public async Task RunAsync_AMembersDirectorKey_ActsOnItsOwnSession()
    {
        var result = await Run(Request("POST", "/sessions/{sid}/prompt", _aliceKey, ("sid", "session-alice")));
        Assert.True(result.Reached);
    }

    [Fact]
    public async Task RunAsync_AMembersDirectorKey_TouchingAnotherMembersSession_IsRefusedAsWatchingIt()
    {
        var result = await Run(Request("POST", "/sessions/{sid}/prompt", _aliceKey, ("sid", "session-bob")));

        Assert.False(result.Reached);
        Assert.Equal(StatusCodes.Status403Forbidden, result.Status);
        Assert.Contains("join or watch someone else's session", result.Error);
    }

    [Fact]
    public async Task RunAsync_AMembersDirectorKey_UsesTheSharedSkills_AndOpensItsOwnTunnel()
    {
        Assert.True((await Run(Request("GET", "/gateway/skills", _bobKey))).Reached);
        Assert.True((await Run(Request("POST", "/director-stream/negotiate", _bobKey))).Reached);
    }

    [Fact]
    public async Task RunAsync_ATeamRequestThatCannotIdentifyThePerson_IsStillRefused()
    {
        // No device key at all (a session key or the machine token leaves no device identity): the gate cannot say
        // who is asking.
        var ctx = Request("GET", "/gateway/skills", null);
        ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = new DeviceCredentialIdentity("d", _team, "director", "active");
        var result = await Run(ctx);

        Assert.False(result.Reached);
        Assert.Equal(TeamEndpointGate.CallerUnknownRefusal, result.Error);
    }

    [Fact]
    public async Task RunAsync_AListAcrossTheTeam_IsRefusedAsNotShownToBeTheCallersOwn()
    {
        // A list of a person's private things that does NOT cut itself to the caller's own is still refused.
        var result = await Run(Request("GET", "/interrupted", _aliceKey));
        Assert.False(result.Reached);
        Assert.Equal(TeamEndpointGate.OwnershipUnknownRefusal, result.Error);
    }

    [Fact]
    public async Task RunAsync_TheSessionRoster_ReachesTheEndpoint_WhichCutsItToTheCallersOwn()
    {
        // devthrottle_internal#2311, live proof F1: the roster cuts its own answer (ListCutToCallersOwn), so the gate
        // asks only the cell, and records the person it allowed for the endpoint to cut by.
        var result = await Run(Request("GET", "/sessions", _aliceKey));
        Assert.True(result.Reached);
    }

    [Fact]
    public void CallerSubject_InATeam_IsTheKeysPerson_InAPersonalTenant_IsTheTenantsAccount()
    {
        Assert.Equal(Alice, _gate.CallerSubject(_tenant, _aliceKey));
        Assert.Null(_gate.CallerSubject(_tenant, null));
        // A key bound to another tenant is not this team's caller.
        Assert.Null(_gate.CallerSubject(_tenant, _aliceKey with { TenantId = Guid.NewGuid().ToString() }));

        var personal = _tenants.MintOrLookupBySubject(Carol, null);
        Assert.Equal(Carol, _gate.CallerSubject(personal, null));
        Assert.Null(_gate.CallerSubject(null, _aliceKey));
    }

    // ---- The Mentor's prompt stamp and session person (devthrottle_internal#2305) ----------------------------------

    [Fact]
    public async Task RunAsync_PushingPrompts_IsTheCallersOwn_AndReachesTheEndpoint()
    {
        Assert.True((await Run(Request("POST", "/prompts", _aliceKey))).Reached);
    }

    [Fact]
    public async Task RunAsync_PushingPrompts_OnACollaboratorsKey_IsRefused()
    {
        // "The caller's own" settles whose data it is, not who may act: a Collaborator still may not push prompts.
        var carolKey = DirectorWithSession(Carol, "director-carol", "session-carol");

        var result = await Run(Request("POST", "/prompts", carolKey));

        Assert.False(result.Reached);
        Assert.Equal(StatusCodes.Status403Forbidden, result.Status);
    }

    // Each of these reads or deletes the WHOLE team's prompt log, with no filter on the person, so none of them may ever
    // become "the caller's own" (devthrottle_internal#2305, part 3 ruling).

    [Fact]
    public async Task RunAsync_ReadingPrompts_InATeam_StaysRefused()
    {
        var result = await Run(Request("GET", "/prompts", _aliceKey));

        Assert.False(result.Reached);
        Assert.Equal(StatusCodes.Status403Forbidden, result.Status);
    }

    [Fact]
    public async Task RunAsync_ExportingPrompts_InATeam_StaysRefused()
    {
        var result = await Run(Request("GET", "/prompts/export", _aliceKey));

        Assert.False(result.Reached);
        Assert.Equal(StatusCodes.Status403Forbidden, result.Status);
    }

    [Fact]
    public async Task RunAsync_DeletingPrompts_InATeam_StaysRefused()
    {
        var result = await Run(Request("DELETE", "/prompts", _aliceKey));

        Assert.False(result.Reached);
        Assert.Equal(StatusCodes.Status403Forbidden, result.Status);
    }

    [Fact]
    public void Whose_OnlyPostOnPromptsItself_IsTheCallersOwn()
    {
        Assert.Equal(TeamOwnership.Callers, _ownership.Whose(_tenant, Alice, "/prompts", _ => null, "POST"));
        Assert.Equal(TeamOwnership.Unknown, _ownership.Whose(_tenant, Alice, "/prompts", _ => null, "GET"));
        Assert.Equal(TeamOwnership.Unknown, _ownership.Whose(_tenant, Alice, "/prompts", _ => null, "DELETE"));
        Assert.Equal(TeamOwnership.Unknown, _ownership.Whose(_tenant, Alice, "/prompts", _ => null));
        Assert.Equal(TeamOwnership.Unknown, _ownership.Whose(_tenant, Alice, "/prompts/export", _ => null, "POST"));
    }

    [Fact]
    public void OwnerOf_IsThePersonBehindItsKey_AndNobodyForADirectorItCannotShow()
    {
        // The one lookup the session history stamps a team session's person with (devthrottle_internal#2305).
        Assert.Equal(Alice, _ownership.OwnerOf(_tenant, "director-alice"));
        Assert.Equal(Bob, _ownership.OwnerOf(_tenant, "director-bob"));
        Assert.Null(_ownership.OwnerOf(_tenant, "director-nobody-registered"));
        Assert.Null(_ownership.OwnerOf(new TenantId(Guid.NewGuid().ToString()), "director-alice"));
        Assert.Null(_ownership.OwnerOf(_tenant, " "));
    }

    [Fact]
    public async Task Ingest_APushIntoTheTeam_IsStampedWithTheKeysPerson_WhateverTheClientWrote()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stamp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new CcDirector.Gateway.Prompts.GatewayPromptLog(dir);
            var at = new DateTime(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc);
            // Alice's key pushes a record that claims to be Bob's, with an id of its own choosing.
            var forged = new PromptRecord
            {
                TsUtc = at, SessionId = "session-alice", Role = "user", TimestampFromAgent = true,
                CharCount = 5, WordCount = 1, Text = "hello", PersonSubject = Bob, PromptId = "chosen-by-the-client",
            };
            var ctx = Request("POST", "/prompts", _aliceKey);

            // The production wiring, exactly: the gate's own answer to "who is asking".
            var result = CcDirector.Gateway.Prompts.PromptEndpoints.Ingest(ctx, _tenant,
                new PromptIngestRequest { Records = new[] { forged } }, log, _teams.IsTeam,
                (c, t) => _gate.CallerSubject(t, AuthMiddleware.AuthenticatedDevice(c)), history: null, tenantBoundary: null);
            await result.ExecuteAsync(WithServices(ctx));

            Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
            var stored = Assert.Single(log.Read(_tenant, at, at));
            Assert.Equal(Alice, stored.PersonSubject);
            Assert.NotEqual("chosen-by-the-client", stored.PromptId);
            Assert.False(string.IsNullOrWhiteSpace(stored.PromptId));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (DirectoryNotFoundException) { }
        }
    }

    [Fact]
    public async Task Ingest_APushIntoTheTeamThatNamesNoPerson_IsRefused_AndNothingIsStored()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stamp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new CcDirector.Gateway.Prompts.GatewayPromptLog(dir);
            var at = new DateTime(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc);
            var record = new PromptRecord
            {
                TsUtc = at, SessionId = "s", Role = "user", TimestampFromAgent = true, CharCount = 1, WordCount = 1, Text = "x",
            };
            // A key bound to another tenant: it is not this team's caller.
            var ctx = Request("POST", "/prompts", _aliceKey with { TenantId = Guid.NewGuid().ToString() });

            var result = CcDirector.Gateway.Prompts.PromptEndpoints.Ingest(ctx, _tenant,
                new PromptIngestRequest { Records = new[] { record } }, log, _teams.IsTeam,
                (c, t) => _gate.CallerSubject(t, AuthMiddleware.AuthenticatedDevice(c)), history: null, tenantBoundary: null);
            await result.ExecuteAsync(WithServices(ctx));

            Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
            Assert.Empty(log.Read(_tenant, at, at));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (DirectoryNotFoundException) { }
        }
    }

    [Fact]
    public async Task Ingest_APersonalTenant_StoresTheRecordsAsBefore_WithNoPersonAndNoId()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stamp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new CcDirector.Gateway.Prompts.GatewayPromptLog(dir);
            var personal = _tenants.MintOrLookupBySubject(Carol, "carol@example.com");
            var at = new DateTime(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc);
            var record = new PromptRecord
            {
                TsUtc = at, SessionId = "s", Role = "user", TimestampFromAgent = true, CharCount = 1, WordCount = 1, Text = "x",
                PersonSubject = Bob, PromptId = "client",
            };
            var ctx = Request("POST", "/prompts", null);

            var result = CcDirector.Gateway.Prompts.PromptEndpoints.Ingest(ctx, personal,
                new PromptIngestRequest { Records = new[] { record } }, log, _teams.IsTeam,
                (c, t) => _gate.CallerSubject(t, AuthMiddleware.AuthenticatedDevice(c)), history: null, tenantBoundary: null);
            await result.ExecuteAsync(WithServices(ctx));

            Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
            var stored = Assert.Single(log.Read(personal, at, at));
            Assert.Null(stored.PersonSubject);
            Assert.Null(stored.PromptId);
            Assert.DoesNotContain("\"person\"", File.ReadAllText(log.FileFor(personal, at)));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (DirectoryNotFoundException) { }
        }
    }

    private static DefaultHttpContext WithServices(DefaultHttpContext ctx)
    {
        ctx.RequestServices = new ServiceCollection()
            .AddLogging().AddOptions().BuildServiceProvider();
        return ctx;
    }
}
