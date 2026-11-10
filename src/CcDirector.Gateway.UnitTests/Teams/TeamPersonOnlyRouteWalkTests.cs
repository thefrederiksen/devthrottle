using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CcDirector.Core.Account;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.DevReports;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Skills;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Teams.Mentor;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using CcDirector.Gateway.Workflows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// ONLY A PERSON, NEVER A COMPUTER, ON THE TEAM ROUTES (security review M1, devthrottle_internal#2786).
///
/// A Director's device key is a plain text file on its computer that the agents there can read, and a Director key
/// bound to the owner's PERSONAL account resolved to the owner on every team route. These tests map exactly what the
/// host maps (<see cref="TeamRoutes.Map"/>), in memory with no port, behind the real team gate, and walk the MAPPED
/// route table: every route under <c>/teams</c> and <c>/team-invitations</c> is called with a Director's key bound to
/// the owner's personal account, with a session key in that account, and with the machine token, and each must be
/// refused with the person-only answer - except the routes on <see cref="TeamPersonOnlyRoutes.Exceptions"/>, which are
/// pinned here by name. A new team route is walked the moment it is mapped, so it cannot slip in unnoticed.
///
/// The same routes still serve a person: the owner's own browser key and phone key are served on at least one route
/// of every family.
/// </summary>
public sealed class TeamPersonOnlyRouteWalkTests : IAsyncLifetime
{
    private const string Owner = "sub-walk-owner";
    private const string CallerHeader = "X-Test-Caller";

    /// <summary>The routes a computer may call, by name. Empty: nothing a Director, a session or the machine token does
    /// today calls a team route (see <see cref="TeamPersonOnlyRoutes"/>). Adding an exception means adding it here too,
    /// where a reviewer sees it.</summary>
    private static readonly string[] PinnedExceptions = Array.Empty<string>();

    private readonly GatewayDbTestHarness _harness = new();
    private GatewayDatabase _db = null!;
    private DeviceRegistry _devices = null!;
    private TenantRegistry _tenants = null!;
    private TeamRegistry _teams = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private IReadOnlyList<(string Method, string Pattern)> _teamRoutes = null!;
    private string _team = null!;
    private TenantId _ownerTenant;

    public async Task InitializeAsync()
    {
        var ambient = new AsyncLocalTenantContext();
        _db = _harness.Open(ambient);
        _devices = new DeviceRegistry(_db, _harness.LegacyPath("devices.json"));
        _tenants = new TenantRegistry(_db);
        _teams = new TeamRegistry(_db, _tenants);
        var access = new TeamAccess(_teams);
        var boundary = new HostedTenantBoundary(ambient, _devices, hosted: true);
        var directors = new DirectorRegistry(_harness.LegacyPath("instances"));
        var pushed = new PushedSessionStore();
        var ownership = new TeamCallerOwnership(directors, pushed, _devices, new SessionTurnStore(_db), boundary,
            new SessionKeyRegistry(_db, isHosted: true));
        var reports = new DevReportStore(_db);
        var skills = new SkillStore(_db, deferInitialize: true);
        var workflows = new WorkflowStore(_db, deferInitialize: true);
        // As the host does: the stores capture the shared library's partition, under the system scope.
        using (ambient.Enter(TenantId.System))
        {
            skills.Initialize();
            workflows.Initialize();
        }
        var gate = new TeamEndpointGate(access, _teams, _tenants, boundary, ownership);

        _ownerTenant = _tenants.MintOrLookupBySubject(Owner, "owner@example.com");
        _team = _teams.CreateTeam(Owner, "Acme").Team!.TeamId;

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Services.AddRouting();
        builder.Services.AddLogging();
        builder.WebHost.UseTestServer();
        _app = builder.Build();
        _app.Use(async (ctx, next) =>
        {
            StandInForAuthentication(ctx);
            await next();
        });
        _app.UseRouting();
        _app.Use(async (ctx, next) => await gate.RunAsync(ctx, () => next()));

        TeamRoutes.Map(_app, new TeamRouteServices(
            Teams: _teams, Access: access, Boundary: boundary, Tenants: _tenants, Devices: _devices,
            FleetMap: new TeamFleetMap(_teams, access, directors, ownership, pushed),
            InvitationMailer: new NoMailer(), PublicBase: () => "https://gateway.example",
            Requests: new TeamRequestStore(_db, _teams),
            Skills: skills, Workflows: workflows,
            MentorStore: new TeamMentorStore(_db), TimeZoneOf: _ => "UTC", MentorRunning: false,
            Reports: reports, Recipients: new DevReportRecipients(_db), Comments: new DevReportPersonComments(_db),
            Delivery: new DevReportDelivery(reports, (_, _) => throw new InvalidOperationException("no session is asked in this test"),
                (_, _) => null, CancellationToken.None),
            Factories: new CcDirector.Gateway.Factory.Registry.FactoryRegistryStore(_db),
            Activity: new CcDirector.Gateway.Factory.FactoryActivityRecord(_db),
            FactoryContributors: new FactoryContributors(_db),
            FactorySwitch: new CcDirector.Gateway.Factory.FactoryAgentsSwitch(machineWide: true, new CcDirector.Gateway.Settings.TenantSettingsStore(_db)),
            FactoryInbox: new CcDirector.Gateway.Factory.Inbox.FactoryInboxStore(_db, _ => CcDirector.Core.Dictation.Models.DictationDictionary.Empty),
            FactoryInboxMessageSent: (_, _, _) => { }, TenantSettings: new CcDirector.Gateway.Settings.TenantSettingsStore(_db)));

        _teamRoutes = ((IEndpointRouteBuilder)_app).DataSources
            .SelectMany(d => d.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => TeamPersonOnlyRoutes.InAFamily(e.RoutePattern.RawText))
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? new[] { "GET" })
                .Select(m => (m, TeamEndpointRules.Normalize(e.RoutePattern.RawText))))
            .Distinct()
            .OrderBy(r => r.Item2, StringComparer.Ordinal).ThenBy(r => r.m, StringComparer.Ordinal)
            .ToList();

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

    /// <summary>Stands in for the auth middleware: stamps the identity the header names, exactly as the real middleware
    /// stamps it for a verified key. Every one is bound to the OWNER's personal account - the case M1 is about.</summary>
    private void StandInForAuthentication(HttpContext ctx)
    {
        switch (ctx.Request.Headers[CallerHeader].ToString())
        {
            case "director":
                ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] =
                    new DeviceCredentialIdentity("owner-ns|director-1", _ownerTenant.Value, "director", "active", Owner);
                break;
            case "session":
                ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] =
                    new SessionCredentialIdentity(Guid.Parse("5a224b35-0000-4000-8000-000000000001"), _ownerTenant, "director-1");
                break;
            case "machine":
                ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "the-machine-token";
                break;
            case "browser":
            case "phone":
                var type = ctx.Request.Headers[CallerHeader].ToString();
                ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] =
                    new DeviceCredentialIdentity("owner-ns|" + type + "-1", _ownerTenant.Value, type, "active", Owner);
                ctx.Items[AuthMiddleware.DeviceTypeItemKey] = type;
                break;
        }
    }

    /// <summary>The address of a route, with the team's real id for <c>{teamId}</c> - so a refusal is never "no such
    /// team" - and a placeholder every other parameter's constraint accepts, so the request reaches the route.</summary>
    private string Address(string pattern) =>
        Regex.Replace(pattern, @"\{\**([^}:=?]+)([^}]*)\}", m =>
            m.Groups[1].Value == "teamId" ? _team
            : m.Groups[2].Value.Contains(":int", StringComparison.Ordinal) ? "1"
            : "x-" + m.Groups[1].Value);

    /// <summary>Sends one request as <paramref name="caller"/>. A write carries a body that is not JSON at all, so an
    /// answer that came from reading the body (400) cannot pass for the gate's refusal.</summary>
    private async Task<(HttpStatusCode Status, string? Code)> Call(string caller, string method, string pattern)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), Address(pattern));
        request.Headers.Add(CallerHeader, caller);
        if (!TeamEndpointRule.HttpMethodIsRead(method))
            request.Content = new StringContent("this is not json", Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        string? code = null;
        if (text.TrimStart().StartsWith('{'))
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("code", out var c)) code = c.GetString();
        }
        return (response.StatusCode, code);
    }

    private static string Name((string Method, string Pattern) route) => $"{route.Method} {route.Pattern}";

    // ---- Every route a contributor can reach is under /teams/{teamId}/ (security review M2, piece 4.1) -------------

    [Fact]
    public void EveryRouteAContributorCanReach_IsUnderTheTeamsOwnAddress()
    {
        // A route serves a contributor inside a team only when a rule states its action and the contributor's cell for
        // that action is not "no" (default deny). Every such MAPPED route must take its team from /teams/{teamId}/, so the
        // gate asks the role table for that very team; one addressed any other way would run in the caller's own tenant
        // and could look a factory or report up by a global id (M2).
        var reachable = _teamRoutes
            .Select(r => (Route: r, Rule: TeamEndpointRules.Find(r.Method, r.Pattern)))
            .Where(x => x.Rule is not null && TeamPermissions.Grant(TeamRole.Collaborator, x.Rule.Action) != TeamGrant.No)
            .ToList();

        Assert.Contains(reachable, x => x.Route.Pattern == CcDirector.Gateway.Api.ContributorEndpoints.FactoriesPattern + "/{factory}");
        Assert.Contains(reachable, x => x.Route.Pattern == CcDirector.Gateway.Api.ContributorEndpoints.InboxPattern);
        foreach (var (route, rule) in reachable)
        {
            Assert.True(route.Pattern.StartsWith("/teams/{teamId}/", StringComparison.Ordinal), $"{Name(route)} is not under /teams/{{teamId}}/");
            Assert.Equal(TeamFrom.RouteTeamId, rule!.TeamFrom);
        }

        // And no rule anywhere opens a route to a contributor by the request's own tenant: inside a team, everything a
        // contributor is given is addressed by the team in the route.
        Assert.All(TeamEndpointRules.All.Where(r => TeamPermissions.Grant(TeamRole.Collaborator, r.Action) != TeamGrant.No),
            r => Assert.True(r.TeamFrom == TeamFrom.RouteTeamId && r.Prefix.StartsWith("/teams/{teamId}/", StringComparison.Ordinal),
                $"the rule for {r.Prefix} opens a route to a contributor outside /teams/{{teamId}}/"));
    }

    // ---- The walk: every mapped team route, refused to every computer --------------------------------------------

    [Theory]
    [InlineData("director")]
    [InlineData("session")]
    [InlineData("machine")]
    public async Task EveryMappedTeamRoute_ACredentialThatIsNotAPerson_IsRefusedPersonOnly(string caller)
    {
        var served = new List<string>();
        foreach (var route in _teamRoutes.Where(r => !PinnedExceptions.Contains(Name(r))))
        {
            var (status, code) = await Call(caller, route.Method, route.Pattern);
            if (status != HttpStatusCode.Forbidden || code != PersonOnlyCaller.Code)
                served.Add($"{Name(route)} -> {(int)status} {code}");
        }

        Assert.True(_teamRoutes.Count > 0, "The mapped route table held no team route, so nothing was walked.");
        Assert.True(served.Count == 0, $"Not refused person-only for a {caller} credential:\n" + string.Join("\n", served));
    }

    [Fact]
    public void TheWalk_CoversEveryFamily_AndTheRoutesM1Names()
    {
        // The walk's pass condition is that nothing was served. Proven to have walked something: the routes the review
        // names - sending a report, reading what a member wrote, inviting, accepting a link, the member list.
        var names = _teamRoutes.Select(Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("POST /teams/{teamId}/reports/mine/{reportId}/recipients", names);
        Assert.Contains("GET /teams/{teamId}/reports/mine/{reportId}", names);
        Assert.Contains("POST /teams/{teamId}/questions/{reportId}/{questionId}/answer", names);
        Assert.Contains("POST /teams/{teamId}/invitations", names);
        Assert.Contains("POST /team-invitations/open", names);
        Assert.Contains("POST /team-invitations/accept", names);
        Assert.Contains("POST /team-invitations/decline", names);
        Assert.Contains("GET /teams/{teamId}/members", names);
        // A contributor's watch view ("Watch your factory", piece 4): mapped, so the walk refuses it to every computer.
        Assert.Contains("GET " + CcDirector.Gateway.Api.ContributorEndpoints.WatchPattern, names);
        Assert.Contains("GET /teams", names);
        Assert.Contains("POST /teams", names);
        Assert.Contains("GET /teams/{teamId}/library", names);
        Assert.Contains("GET /teams/{teamId}/governance", names);
        Assert.Contains("GET /teams/{teamId}/mentor", names);
        Assert.Contains("GET /teams/{teamId}/requests", names);
        Assert.Contains("POST /teams/{teamId}/requests/{requestId}/forward", names);
        Assert.True(_teamRoutes.Count >= 60, $"Only {_teamRoutes.Count} team routes were found in the mapped route table.");
    }

    [Fact]
    public void Exceptions_AreExactlyThePinnedList_AndEachIsAMappedTeamRoute()
    {
        var declared = TeamPersonOnlyRoutes.Exceptions.Select(e => $"{e.Method} {TeamEndpointRules.Normalize(e.Pattern)}").ToArray();

        Assert.Equal(PinnedExceptions.OrderBy(x => x, StringComparer.Ordinal), declared.OrderBy(x => x, StringComparer.Ordinal));
        var mapped = _teamRoutes.Select(Name).ToHashSet(StringComparer.Ordinal);
        Assert.All(declared, d => Assert.Contains(d, mapped));
        Assert.All(TeamPersonOnlyRoutes.Exceptions, e => Assert.False(string.IsNullOrWhiteSpace(e.Reason)));
    }

    // ---- A person is still served ---------------------------------------------------------------------------------

    /// <summary>One read per family, each answered for the team's owner.</summary>
    public static IEnumerable<object[]> OneReadPerFamily() =>
        new[]
        {
            "/teams", "/teams/{teamId}/members", "/teams/{teamId}/page", "/teams/{teamId}/fleet-map", "/teams/{teamId}/bill",
            "/teams/{teamId}/invitations", "/teams/{teamId}/invitations/options", "/teams/{teamId}/requests",
            "/teams/{teamId}/requests/mine", "/teams/{teamId}/library", "/teams/{teamId}/skills", "/teams/{teamId}/workflows",
            "/teams/{teamId}/governance", "/teams/{teamId}/mentor", "/teams/{teamId}/reports/sent-to-me",
            "/teams/{teamId}/reports/mine", "/teams/{teamId}/questions/",
        }.SelectMany(p => new[] { new object[] { "browser", p }, new object[] { "phone", p } });

    [Theory]
    [MemberData(nameof(OneReadPerFamily))]
    public async Task OneReadPerFamily_APersonsOwnBrowserOrPhoneKey_IsServed(string caller, string pattern)
    {
        Assert.True(_teamRoutes.Contains(("GET", pattern)),
            $"GET {pattern} is not in the mapped route table:\n" + string.Join("\n", _teamRoutes.Select(Name)));

        var (status, code) = await Call(caller, "GET", pattern);

        Assert.True(status == HttpStatusCode.OK, $"GET {pattern} as the owner's {caller} answered {(int)status} {code}");
    }

    [Theory]
    [InlineData("browser")]
    [InlineData("phone")]
    public async Task OpenAnInvitationLink_APersonsOwnKey_IsNotRefusedPersonOnly(string caller)
    {
        // No real link is sent, so the route answers that it cannot read one - but it is the route answering, not the
        // person-only refusal.
        var (_, code) = await Call(caller, "POST", "/team-invitations/open");

        Assert.NotEqual(PersonOnlyCaller.Code, code);
    }

    [Fact]
    public async Task CreateATeam_APersonsOwnBrowserKey_IsCreated()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/teams")
        {
            Content = new StringContent("{\"name\":\"Second\"}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(CallerHeader, "browser");

        using var response = await _http.SendAsync(request);

        Assert.True(response.IsSuccessStatusCode, $"POST /teams answered {(int)response.StatusCode}");
    }

    private sealed class NoMailer : ITeamInvitationMailer
    {
        public Task<TeamInvitationMailResult> SendAsync(string invitationId, string teamId, string acceptToken, CancellationToken ct = default) =>
            throw new InvalidOperationException("no invitation is sent in this test");

        public Task<TeamInvitationMailResult> SendAddedAsync(string invitationId, string teamId, string acceptToken, CancellationToken ct = default) =>
            throw new InvalidOperationException("no invitation is sent in this test");
    }
}
