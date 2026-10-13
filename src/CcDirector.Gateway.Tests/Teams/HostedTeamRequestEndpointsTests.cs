using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// Requests to a team's Owner and Managers over REAL HTTP on a hosted Gateway with Teams released
/// (devthrottle_internal#2308). Every caller is a person on their own signed-in browser key; the Directors are real
/// tunnel connections (<see cref="FakeTunnelDirector"/>) that record every command the Gateway sends them.
///
/// The four tests of the issue, over the wire: the list is the Owner's and Managers' only; every change of state is
/// shown to the sender, with the reason for "Not doing this"; a request's text never reaches a session; and a
/// Developer cannot accept or reject one. Plus tenant isolation and the missing reason.
///
/// This class sets the process-wide CC_DIRECTOR_ROOT, so it joins the DirectorRoot collection.
/// </summary>
[Collection("DirectorRoot")]
public sealed class HostedTeamRequestEndpointsTests : IAsyncLifetime
{
    private const string Token = "test-token";

    private readonly string _run = Guid.NewGuid().ToString("N")[..10];
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-team-req-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string? _priorRoot;

    private Person _owner = null!;
    private Person _manager = null!;
    private Person _developer = null!;
    private Person _collaborator = null!;
    private Person _otherCollaborator = null!;
    private Person _otherTeamOwner = null!;
    private string _team = "";
    private string _otherTeam = "";

    private sealed record Person(string Subject, string Email, TenantId Tenant, string Key);

    public HostedTeamRequestEndpointsTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _instancesDir);

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true, teamsReleased: true,
            options: new GatewayHostOptions { Hosted = true });
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/"), Timeout = TimeSpan.FromMinutes(2) };

        _owner = Enroll("owner", "olivia@acme.example");
        _manager = Enroll("manager", "mark@acme.example");
        _developer = Enroll("developer", "dev@acme.example");
        _collaborator = Enroll("collaborator", "carla@client.example");
        _otherCollaborator = Enroll("collaborator2", "colin@client.example");
        _otherTeamOwner = Enroll("other-owner", "other@elsewhere.example");

        _team = _gateway.TeamRegistry.CreateTeam(_owner.Subject, "Acme").Team!.TeamId;
        Assert.True(_gateway.TeamRegistry.AddMember(_team, _manager.Subject, TeamRole.Manager).IsDone);
        Assert.True(_gateway.TeamRegistry.AddMember(_team, _developer.Subject, TeamRole.Developer).IsDone);
        Assert.True(_gateway.TeamRegistry.AddMember(_team, _collaborator.Subject, TeamRole.Collaborator).IsDone);
        Assert.True(_gateway.TeamRegistry.AddMember(_team, _otherCollaborator.Subject, TeamRole.Collaborator).IsDone);
        _otherTeam = _gateway.TeamRegistry.CreateTeam(_otherTeamOwner.Subject, "Elsewhere").Team!.TeamId;
        // The team pays: a team Director's access is read from the team's bill (devthrottle_internal#2311 step 2), so
        // without one a team key would be refused as unknown before any request route is reached.
        HostedTeamBill.Start(_gateway, _team, seats: 5);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    /// <summary>A person signed in on their own BROWSER - the only kind of key the request routes serve.</summary>
    private Person Enroll(string who, string email)
    {
        var subject = $"sub-req-{who}-{_run}";
        var tenant = _gateway.TenantRegistry.MintOrLookupBySubject(subject, email);
        _gateway.SeedEntitlementForTest(subject);
        var key = _gateway.Devices.RegisterForTenant(tenant, subject, $"dev-req-{who}-{_run}", "BROWSER", deviceType: "browser").DeviceKey;
        return new Person(subject, email, tenant, key);
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Call(HttpMethod method, string path, string key, object? body = null)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (body is not null) req.Content = JsonContent.Create(body);
        using var resp = await _http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        _output.WriteLine($"{method} {path} -> {(int)resp.StatusCode} {text}");
        var json = string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
        return (resp.StatusCode, json);
    }

    private async Task<string> SendRequest(Person sender, string text)
    {
        var (status, body) = await Call(HttpMethod.Post, $"teams/{_team}/requests", sender.Key, new { text });
        Assert.Equal(HttpStatusCode.Created, status);
        return body.GetProperty("request").GetProperty("id").GetString()!;
    }

    private (string State, int Steps) Stored(string requestId)
    {
        var id = Guid.Parse(requestId);
        using var ctx = _gateway.GatewayDatabaseForTests.CreateContext(new TenantId(_team));
        return (ctx.TeamRequests.AsNoTracking().Single(r => r.Id == id).State, ctx.TeamRequestChanges.Count(s => s.RequestId == id));
    }

    // ---- Test 1 of #2308: a request is visible to the Owner and Managers of that team only ------------------------

    [Fact]
    public async Task RequestList_IsVisibleToTheOwnerAndManagersOnly_OverTheWire()
    {
        var id = await SendRequest(_collaborator, "Please export the report as a spreadsheet");

        foreach (var reader in new[] { _owner, _manager })
        {
            var (status, body) = await Call(HttpMethod.Get, $"teams/{_team}/requests", reader.Key);
            Assert.Equal(HttpStatusCode.OK, status);
            var request = Assert.Single(body.GetProperty("requests").EnumerateArray());
            Assert.Equal(id, request.GetProperty("id").GetString());
            Assert.Equal("carla@client.example", request.GetProperty("sentBy").GetString());
            Assert.True(request.GetProperty("canAccept").GetBoolean());
        }

        // A Developer and another Collaborator are refused the list by the server, in the role table's words.
        foreach (var (reader, role) in new[] { (_developer, TeamRole.Developer), (_otherCollaborator, TeamRole.Collaborator) })
        {
            var (status, body) = await Call(HttpMethod.Get, $"teams/{_team}/requests", reader.Key);
            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.Equal(TeamEndpointGate.RefusalCode, body.GetProperty("code").GetString());
            Assert.Equal(TeamAccessDecision.RoleRefusal(role, TeamPermissions.Row(TeamAction.ReadAndDecideTeamRequests)),
                body.GetProperty("error").GetString());
        }

        // A member of another team is told there is no such team.
        var (stranger, _) = await Call(HttpMethod.Get, $"teams/{_team}/requests", _otherTeamOwner.Key);
        Assert.Equal(HttpStatusCode.NotFound, stranger);

        // Another Collaborator's own list does not hold it; the sender's does.
        var (_, theirs) = await Call(HttpMethod.Get, $"teams/{_team}/requests/mine", _otherCollaborator.Key);
        Assert.Equal(0, theirs.GetProperty("count").GetInt32());
        var (_, mine) = await Call(HttpMethod.Get, $"teams/{_team}/requests/mine", _collaborator.Key);
        Assert.Equal(id, Assert.Single(mine.GetProperty("requests").EnumerateArray()).GetProperty("id").GetString());
    }

    // ---- A Collaborator is a free seat: no plan of their own, and the routes still serve them ----------------------

    /// <summary>
    /// A Collaborator is a free seat (devthrottle_internal#2098), so the person this page is built for has, typically,
    /// never paid for anything. Every other test here seeds a hosted entitlement on each person's own account; this one
    /// enrols a Collaborator with the test seeding switched off, proves their account holds NO entitlement row at all,
    /// and then sends a request and reads it back over the wire with that person's own browser key.
    /// </summary>
    [Fact]
    public async Task ACollaboratorWithNoEntitlementOfTheirOwn_SendsAndReadsTheirRequests_OverTheWire()
    {
        var seed = _gateway.Devices.OnAccountBoundForTest;
        _gateway.Devices.OnAccountBoundForTest = null;
        Person unpaid;
        try
        {
            var subject = $"sub-req-unpaid-{_run}";
            var tenant = _gateway.TenantRegistry.MintOrLookupBySubject(subject, "una@client.example");
            var key = _gateway.Devices.RegisterForTenant(tenant, subject, $"dev-req-unpaid-{_run}", "BROWSER", deviceType: "browser").DeviceKey;
            unpaid = new Person(subject, "una@client.example", tenant, key);
        }
        finally
        {
            _gateway.Devices.OnAccountBoundForTest = seed;
        }
        // No entitlement row and no trial row: nothing was ever bought or started. The account therefore reads as the
        // FREE plan, which is what any person who never paid is - free carries the hosted Gateway and nothing else.
        using (var db = _gateway.GatewayDatabaseForTests.CreateUnscopedContext())
        {
            Assert.False(db.Entitlements.AsNoTracking().Any(e => e.Subject == unpaid.Subject));
            Assert.False(db.AccountTrials.AsNoTracking().Any(t => t.Subject == unpaid.Subject));
        }
        Assert.Equal(new EntitlementDecision(EntitlementOutcome.Entitled, EntitlementRegistry.TierFree),
            _gateway.EntitlementRegistry.Evaluate(unpaid.Subject, DateTime.UtcNow));
        Assert.True(_gateway.TeamRegistry.AddMember(_team, unpaid.Subject, TeamRole.Collaborator).IsDone);

        var (sent, sentBody) = await Call(HttpMethod.Post, $"teams/{_team}/requests", unpaid.Key, new { text = "Please add the region column" });
        Assert.Equal(HttpStatusCode.Created, sent);
        var id = sentBody.GetProperty("request").GetProperty("id").GetString();

        var (read, mine) = await Call(HttpMethod.Get, $"teams/{_team}/requests/mine", unpaid.Key);
        Assert.Equal(HttpStatusCode.OK, read);
        Assert.Equal(id, Assert.Single(mine.GetProperty("requests").EnumerateArray()).GetProperty("id").GetString());
    }

    // ---- Test 2 of #2308: each state change is shown to the Collaborator who sent it, with the reason ------------

    [Fact]
    public async Task EveryStateChange_IsShownToTheCollaboratorWhoSentIt_WithWhoAndTheReason()
    {
        var first = await SendRequest(_collaborator, "Add a dark mode");
        var second = await SendRequest(_collaborator, "Move the deadline to Friday");

        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"teams/{_team}/requests/{first}/accept", _manager.Key)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"teams/{_team}/requests/{first}/done", _owner.Key)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"teams/{_team}/requests/{second}/decline", _owner.Key,
            new { reason = "The launch date is fixed by the client contract." })).Status);

        var (status, mine) = await Call(HttpMethod.Get, $"teams/{_team}/requests/mine", _collaborator.Key);
        Assert.Equal(HttpStatusCode.OK, status);
        var requests = mine.GetProperty("requests").EnumerateArray().ToDictionary(r => r.GetProperty("id").GetString()!);

        var done = requests[first];
        Assert.Equal("Done", done.GetProperty("stateLabel").GetString());
        Assert.Equal(new[] { "Sent by You", "Accepted by mark@acme.example", "Marked Done by olivia@acme.example" },
            done.GetProperty("trail").EnumerateArray().Select(s => s.GetProperty("sentence").GetString()));
        Assert.All(done.GetProperty("trail").EnumerateArray(), s => Assert.True(s.GetProperty("atUtc").GetDateTime() > DateTime.MinValue));

        var declined = requests[second];
        Assert.Equal("Not doing this", declined.GetProperty("stateLabel").GetString());
        var last = declined.GetProperty("trail").EnumerateArray().Last();
        Assert.Equal("Not doing this - olivia@acme.example", last.GetProperty("sentence").GetString());
        Assert.Equal("The launch date is fixed by the client contract.", last.GetProperty("reason").GetString());

        // The sender may decide nothing: every verdict is false on their own list.
        Assert.All(requests.Values, r => Assert.False(r.GetProperty("canAccept").GetBoolean() || r.GetProperty("canDecline").GetBoolean()
                                                     || r.GetProperty("canMarkDone").GetBoolean()));
    }

    // ---- Test 4 of #2308: a Developer cannot accept or reject a request -------------------------------------------

    [Fact]
    public async Task ADeveloper_CannotAcceptRejectOrFinishARequest_AndNothingChanges()
    {
        var id = await SendRequest(_collaborator, "Rename the project");

        foreach (var (verb, body) in new (string, object?)[] { ("accept", null), ("decline", new { reason = "no" }), ("done", null) })
        {
            var (status, refusal) = await Call(HttpMethod.Post, $"teams/{_team}/requests/{id}/{verb}", _developer.Key, body);
            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.Equal(TeamEndpointGate.RefusalCode, refusal.GetProperty("code").GetString());
        }

        Assert.Equal((TeamRequestStates.Sent, 1), Stored(id));
    }

    // ---- Not doing this without a reason; tenant isolation ---------------------------------------------------------

    [Fact]
    public async Task NotDoingThis_WithoutAReason_IsRefusedAndNothingChanges()
    {
        var id = await SendRequest(_collaborator, "Add single sign-on");

        var (missing, body) = await Call(HttpMethod.Post, $"teams/{_team}/requests/{id}/decline", _owner.Key, new { reason = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, missing);
        Assert.Contains("Say why", body.GetProperty("error").GetString());
        var (noBody, _) = await Call(HttpMethod.Post, $"teams/{_team}/requests/{id}/decline", _owner.Key, new { });
        Assert.Equal(HttpStatusCode.BadRequest, noBody);

        Assert.Equal((TeamRequestStates.Sent, 1), Stored(id));
    }

    [Fact]
    public async Task AStateChangeOnAnotherTeamsRequest_IsRefused_AndNothingChanges()
    {
        var id = await SendRequest(_collaborator, "Add an audit log");

        // Through the other team, where its Owner may decide: the request is not there.
        var (viaOwnTeam, _) = await Call(HttpMethod.Post, $"teams/{_otherTeam}/requests/{id}/accept", _otherTeamOwner.Key);
        Assert.Equal(HttpStatusCode.NotFound, viaOwnTeam);
        // Through this team, of which they are not a member.
        var (viaThisTeam, _) = await Call(HttpMethod.Post, $"teams/{_team}/requests/{id}/accept", _otherTeamOwner.Key);
        Assert.Equal(HttpStatusCode.NotFound, viaThisTeam);

        Assert.Equal((TeamRequestStates.Sent, 1), Stored(id));
        var (_, otherList) = await Call(HttpMethod.Get, $"teams/{_otherTeam}/requests", _otherTeamOwner.Key);
        Assert.Equal(0, otherList.GetProperty("count").GetInt32());
    }

    // ---- Test 3 of #2308: a request's text never reaches a session's input -----------------------------------------

    /// <summary>
    /// Test 3 again, with keys bound to the TEAM's tenant (devthrottle_internal#2311, merged as #3530): the Owner's and
    /// the Manager's Directors set up for the team, and a session key under each, in the team's tenant. Every request
    /// route refuses every one of them, the refusal carries no request text, and nothing changes in the store.
    ///
    /// WHAT REFUSES THEM, measured, not assumed, with the team paying (a live team bill, set up above):
    /// <list type="bullet">
    /// <item>A team SESSION key is refused first by the session-key guard (403 session_key_out_of_scope): a session key
    /// may not call a team route at all, in any tenant.</item>
    /// <item>A team DIRECTOR key authenticates, the request-path access lease reads the TEAM's bill
    /// (devthrottle_internal#2311 step 2, #3552) and lets it through, and the request routes then refuse it themselves:
    /// 403 person_only, the same answer the personal-account test above proves for Director keys. Before step 2 the
    /// lease read a personal account's bill and refused it first (402 hosted_subscription_required); without a team bill
    /// it is now refused as unknown (503 entitlement_unknown) - neither is the routes' own check, so the bill is set
    /// up.</item>
    /// </list>
    /// The "live session receives nothing" half of test 3 runs above on the personal Directors.
    /// </summary>
    [Fact]
    public async Task ARequestsRoutes_RefuseTeamBoundDirectorAndSessionKeys_AndNothingChanges()
    {
        var marker = "TEAM-KEY-MARKER-" + Guid.NewGuid().ToString("N");
        var id = await SendRequest(_collaborator, $"Please change the report header {marker}");

        var keys = new List<(string What, string Key, HttpStatusCode Status, string Code)>();
        foreach (var person in new[] { _owner, _manager })
        {
            var directorId = $"director-team-{person.Email.Split('@')[0]}-{_run}";
            var teamDeviceId = HostedEnrollmentEndpoint.TeamScopedDeviceId(_team, person.Subject, directorId);
            var directorKey = _gateway.Devices.RegisterForTenant(new TenantId(_team), person.Subject, teamDeviceId, "MTEAM").DeviceKey;
            var sessionKey = GatewaySessionKey.Mint();
            Assert.True(_gateway.SessionKeys.Register(new TenantId(_team), teamDeviceId, Guid.NewGuid().ToString("D"),
                GatewaySessionKey.Hash(sessionKey), DateTime.UtcNow.AddHours(1)));
            keys.Add(($"{person.Email} team Director key", directorKey, HttpStatusCode.Forbidden, "person_only"));
            keys.Add(($"{person.Email} team session key", sessionKey, HttpStatusCode.Forbidden, "session_key_out_of_scope"));
        }

        foreach (var (what, key, expectedStatus, expectedCode) in keys)
        {
            foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
                     {
                         (HttpMethod.Get, $"teams/{_team}/requests", null),
                         (HttpMethod.Get, $"teams/{_team}/requests/mine", null),
                         (HttpMethod.Post, $"teams/{_team}/requests", new { text = "from an agent" }),
                         (HttpMethod.Post, $"teams/{_team}/requests/{id}/accept", null),
                         (HttpMethod.Post, $"teams/{_team}/requests/{id}/decline", new { reason = "agent" }),
                         (HttpMethod.Post, $"teams/{_team}/requests/{id}/done", null),
                     })
            {
                var (status, refusal) = await Call(method, path, key, body);
                _output.WriteLine($"{what}: {method} /{path} -> {(int)status} {refusal}");
                Assert.Equal((expectedStatus, expectedCode), (status, refusal.GetProperty("code").GetString()));
                Assert.DoesNotContain(marker, refusal.ToString(), StringComparison.Ordinal);
            }
        }

        Assert.Equal((TeamRequestStates.Sent, 1), Stored(id));
        using var ctx = _gateway.GatewayDatabaseForTests.CreateContext(new TenantId(_team));
        Assert.Equal(1, ctx.TeamRequests.Count());
    }
}
