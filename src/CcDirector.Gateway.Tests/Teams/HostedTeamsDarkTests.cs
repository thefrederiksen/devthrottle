using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// Teams merges DARK (devthrottle_internal#2300, review finding F1). With the release switch off - the default - a
/// hosted Gateway does not map the team routes at all: an enrolled account gets the ordinary not-found answer on
/// every one, and no team can be created. <see cref="HostedTeamEndpointsTests"/> is the other half: switched on, the
/// same routes answer. A REAL hosted <see cref="GatewayHost"/> over REAL HTTP, so the proof is about what is mapped.
///
/// "Not mapped" is proven three ways, none of which depends on how the build was made: the finalised route table
/// holds nothing under /teams; a request to a team route gets the Gateway's not-found answer (see
/// <see cref="AssertAnsweredAsAPathThatDoesNotExist"/>); and nothing was written. A GET of an unmapped path is NOT a
/// usable comparison, because the Cockpit fallback answers it 404 when the React Cockpit is not in the test output and
/// with the 200 Cockpit shell when it is - which depends on the build and on test ORDER (CockpitReactAppServingTests
/// deletes that folder when it finishes). A dark Gateway answers 404 on a team route either way
/// (<see cref="CcDirector.Gateway.Teams.TeamsDarkRoutes"/>, devthrottle_internal#2311), so these tests pin 404 and
/// compare with an unmapped POST, which the fallback always answers with its not-found answer.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class HostedTeamsDarkTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private readonly string _subject = "sub-dark-" + Guid.NewGuid().ToString("N");
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-teams-dark-" + Guid.NewGuid().ToString("N"));
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string _key = "";
    private string? _priorHosted;
    private string? _priorRoot;
    private string? _priorTeams;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _instancesDir);
        // The DEFAULT is what is under test, so the switch is read from an environment where it is not set at all,
        // rather than passed in as off.
        _priorTeams = Environment.GetEnvironmentVariable(CcDirector.Gateway.Teams.TeamsReleaseSwitch.EnvVar);
        Environment.SetEnvironmentVariable(CcDirector.Gateway.Teams.TeamsReleaseSwitch.EnvVar, null);

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        var tenant = _gateway.TenantRegistry.MintOrLookupBySubject(_subject, "dark@example.com");
        _key = _gateway.Devices.Register("dev-dark", "M-dark").DeviceKey;
        _gateway.Devices.SetAccountBinding("dev-dark", _subject, tenant.Value);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        Environment.SetEnvironmentVariable(CcDirector.Gateway.Teams.TeamsReleaseSwitch.EnvVar, _priorTeams);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task SwitchUnset_EveryTeamRouteIsAbsent_AndNoTeamCanBeCreated()
    {
        Assert.False(_gateway.TeamsReleased);
        Assert.DoesNotContain(MappedPatterns(), p => p.StartsWith("/teams", StringComparison.Ordinal));

        foreach (var (method, path) in new[] { (HttpMethod.Get, "teams"), (HttpMethod.Post, "teams"), (HttpMethod.Get, $"teams/{Guid.NewGuid()}/members") })
            await AssertAnsweredAsAPathThatDoesNotExist(method, path, new { name = "Should not exist" });
        // Absence is proven by what was written, not by the words of the answer (the Gateway's not-found answer
        // echoes the path, which itself says "teams"): the create did not reach the registry.
        Assert.Empty(_gateway.TeamRegistry.ListTeamsFor(_subject));
    }

    [Fact]
    public async Task SwitchUnset_EveryInvitationRouteIsAbsent_AndNothingIsInvitedOrJoined()
    {
        // A team and a waiting invitation made directly through the registry, so each route has something real to act
        // on: if a route were mapped, these requests would succeed.
        var team = _gateway.TeamRegistry.CreateTeam(_subject, "Dark team").Team!.TeamId;
        using (var ctx = _gateway.GatewayDatabaseForTests.CreateUnscopedContext())
        {
            ctx.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS team_entitlements (team_id TEXT NOT NULL PRIMARY KEY, status TEXT NOT NULL, seats INTEGER NULL, " +
                "current_period_end TEXT NULL, stripe_subscription_id TEXT NULL, livemode INTEGER NULL, updated_at TEXT NULL)");
            ctx.Database.ExecuteSqlRaw("INSERT INTO team_entitlements (team_id, status, seats, livemode) VALUES ({0}, 'active', 1, 1)", team);
        }
        var created = _gateway.TeamRegistry.CreateInvitation(team, _subject, "waiting@example.com", CcDirector.Gateway.Teams.TeamRole.Developer);
        var waiting = created.Invitation!;
        var token = created.AcceptToken!;
        var hash = CcDirector.Gateway.Teams.TeamInvitationRules.HashAcceptToken(token);
        var joiner = "sub-dark-joiner-" + Guid.NewGuid().ToString("N");
        var joinerTenant = _gateway.TenantRegistry.MintOrLookupBySubject(joiner, "joiner@example.com");
        var joinerKey = _gateway.Devices.Register("dev-dark-joiner", "M-dark-joiner").DeviceKey;
        _gateway.Devices.SetAccountBinding("dev-dark-joiner", joiner, joinerTenant.Value);

        var routes = new (HttpMethod Method, string Path, string Key, object? Body)[]
        {
            (HttpMethod.Get, $"teams/{team}/invitations/options", _key, null),
            (HttpMethod.Get, $"teams/{team}/invitations", _key, null),
            (HttpMethod.Post, $"teams/{team}/invitations", _key, new { email = "new@example.com", role = "Developer" }),
            (HttpMethod.Post, $"teams/{team}/invitations/{waiting.Id}/resend", _key, new { }),
            (HttpMethod.Post, $"teams/{team}/invitations/{waiting.Id}/cancel", _key, new { }),
            (HttpMethod.Post, "team-invitations/open", joinerKey, new { token }),
            (HttpMethod.Post, "team-invitations/accept", joinerKey, new { token }),
            (HttpMethod.Post, "team-invitations/decline", joinerKey, new { token }),
        };
        Assert.DoesNotContain(MappedPatterns(), p => p.StartsWith("/teams", StringComparison.Ordinal) || p.StartsWith("/team-invitations", StringComparison.Ordinal));
        foreach (var (method, path, key, body) in routes)
            await AssertAnsweredAsAPathThatDoesNotExist(method, path, body ?? new { }, key);

        // Absence proven by what was written: still exactly one invitation, still waiting, still the same link, and
        // nobody joined.
        using (var ctx = _gateway.GatewayDatabaseForTests.CreateUnscopedContext())
        {
            var row = Assert.Single(ctx.TeamInvitations.AsNoTracking().Where(i => i.TeamId == team).ToList());
            Assert.Equal("sent", row.State);
            Assert.Equal(hash, row.AcceptTokenHash);
        }
        Assert.Null(_gateway.TeamRegistry.RoleOf(team, joiner));
    }
    [Fact]
    public async Task SwitchUnset_EveryRequestRouteIsAbsent_AndNothingIsSentOrChanged()
    {
        // A team and a request made directly through the store, so each route has something real to act on: if a route
        // were mapped, these requests would succeed. The key is a person's own browser key, the one kind the request
        // routes serve.
        var team = _gateway.TeamRegistry.CreateTeam(_subject, "Dark requests").Team!.TeamId;
        var existing = _gateway.TeamRequests.Send(team, _subject, "Made before the routes were asked").Request!;
        var tenant = _gateway.TenantRegistry.MintOrLookupBySubject(_subject, "dark@example.com");
        var browserKey = _gateway.Devices.RegisterForTenant(tenant, _subject, "dev-dark-browser", "M-dark-browser", deviceType: "browser").DeviceKey;

        var routes = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Post, $"teams/{team}/requests", new { text = "Should not exist" }),
            (HttpMethod.Get, $"teams/{team}/requests", null),
            (HttpMethod.Get, $"teams/{team}/requests/mine", null),
            (HttpMethod.Post, $"teams/{team}/requests/{existing.Id}/accept", null),
            (HttpMethod.Post, $"teams/{team}/requests/{existing.Id}/decline", new { reason = "Should not change" }),
            (HttpMethod.Post, $"teams/{team}/requests/{existing.Id}/done", null),
        };
        foreach (var (method, path, body) in routes)
            await AssertAnsweredAsAPathThatDoesNotExist(method, path, body ?? new { }, browserKey);

        // Absence proven by what was written: still the one request, still sent, with its one step.
        var mine = _gateway.TeamRequests.ListMine(team, _subject).Requests;
        var only = Assert.Single(mine);
        Assert.Equal((existing.Id, "sent", 1), (only.Id, only.State, only.Trail.Count));
    }

    [Fact]
    public async Task SwitchUnset_EveryTeamPageRouteIsAbsent_AndNoRoleChangesAndNobodyIsRemoved()
    {
        // A real team with a real Developer, so each route has something to act on: if a route were mapped, the change
        // would land (devthrottle_internal#2303).
        Assert.DoesNotContain(MappedPatterns(), p => p.StartsWith("/teams", StringComparison.Ordinal));
        var team = _gateway.TeamRegistry.CreateTeam(_subject, "Dark page").Team!.TeamId;
        var developer = "sub-dark-dev-" + Guid.NewGuid().ToString("N");
        Assert.True(_gateway.TeamRegistry.AddMember(team, developer, CcDirector.Gateway.Teams.TeamRole.Developer).IsDone);
        var memberId = CcDirector.Gateway.Teams.TeamMemberIds.For(team, developer);

        var routes = new (HttpMethod Method, string Path, object Body)[]
        {
            (HttpMethod.Get, $"teams/{team}/page", new { }),
            (HttpMethod.Put, $"teams/{team}/members/{memberId}/role", new { role = "Collaborator" }),
            (HttpMethod.Delete, $"teams/{team}/members/{memberId}", new { }),
        };
        foreach (var (method, path, body) in routes)
            await AssertAnsweredAsAPathThatDoesNotExist(method, path, body);

        // Absence proven by what was written: the Developer is still a Developer, still in the team.
        Assert.Equal(CcDirector.Gateway.Teams.TeamRole.Developer, _gateway.TeamRegistry.RoleOf(team, developer));
    }

    [Fact]
    public async Task SwitchUnset_TheTeamLibraryRoutesAreAbsent_AndThePersonalLibraryStillAnswers()
    {
        // devthrottle_internal#2304. Read from the finalised route table: nothing under /teams is mapped at all - while
        // the ordinary skill and workflow routes are, so the table read is not an empty one.
        var patterns = MappedPatterns();
        Assert.Contains("/gateway/skills", patterns);
        Assert.Contains("/gateway/workflows", patterns);
        Assert.DoesNotContain(patterns, p => p.StartsWith("/teams", StringComparison.Ordinal));

        var team = Guid.NewGuid();
        foreach (var (method, path) in new[] { (HttpMethod.Get, $"teams/{team}/skills"), (HttpMethod.Get, $"teams/{team}/workflows"),
                     (HttpMethod.Get, $"teams/{team}/library"), (HttpMethod.Post, $"teams/{team}/skills") })
            await AssertAnsweredAsAPathThatDoesNotExist(method, path, new { id = "dark-skill", name = "Dark", summary = "s", bodyMarkdown = "# d" });

        using var own = new HttpRequestMessage(HttpMethod.Get, "gateway/skills");
        own.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        using var ownResp = await _http.SendAsync(own);
        Assert.Equal(HttpStatusCode.OK, ownResp.StatusCode);
        Assert.Equal("application/json", ownResp.Content.Headers.ContentType?.MediaType);
    }

    private string[] MappedPatterns() => _gateway.MappedEndpoints.OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
        .Select(e => CcDirector.Gateway.Teams.TeamEndpointRules.Normalize(e.RoutePattern.RawText)).ToArray();

    /// <summary>
    /// The team route is answered with the Gateway's not-found answer - 404, and the same kind of body as a path that
    /// was never mapped gets when POSTed, which the Cockpit fallback always answers not-found - never with data from a
    /// handler and never with the Cockpit shell. That holds whether or not the Cockpit is in the test output, so the
    /// proof does not change with the build or the test order.
    /// </summary>
    private async Task AssertAnsweredAsAPathThatDoesNotExist(HttpMethod method, string path, object postBody, string? key = null)
    {
        using var teamResp = await SendAsync(method, path, postBody, key ?? _key);
        using var controlResp = await SendAsync(HttpMethod.Post, "no-such-route-" + Guid.NewGuid().ToString("N"), postBody, key ?? _key);

        Assert.Equal(HttpStatusCode.NotFound, controlResp.StatusCode);
        Assert.True(teamResp.StatusCode == HttpStatusCode.NotFound,
            $"{method} /{path} was answered {(int)teamResp.StatusCode} {teamResp.Content.Headers.ContentType?.MediaType}: a dark Gateway answers 404 on a team route.");
        Assert.Equal(controlResp.Content.Headers.ContentType?.MediaType, teamResp.Content.Headers.ContentType?.MediaType);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object postBody, string key)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (method == HttpMethod.Post || method == HttpMethod.Put) req.Content = JsonContent.Create(postBody);
        return await _http.SendAsync(req);
    }
}
