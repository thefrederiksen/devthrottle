using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Account;
using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.DevReports;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Registry;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Inbox;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Settings;
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
/// A TEAM'S FACTORY RUNS ON THE TEAM'S DIRECTOR AND REPORTS INTO THE TEAM (Team implementation piece 3.2,
/// devthrottle_internal#2797), the whole path in one in-memory hosted Gateway with Teams released - no port, no real
/// Director, no clock. Every request carries a REAL key and goes through the pipeline the hosted host runs, in its order:
/// the real authentication middleware (device keys and session keys, with the session key guard), the tenant scope the
/// verified key names, routing, and the real team gate with the real ownership resolver. The routes are the real ones
/// wherever the path writes or reads something: the factory registry, the activity record, the dev report routes, and
/// every team route (<see cref="TeamRoutes.Map"/>). What a Director pulls while it runs a factory (its triggers and
/// checks) and the Boss's factory inbox are mapped as routes that only say they were reached, because for those the
/// question this piece asks is only whether the key, the guard and the gate let the team's own Director through.
///
/// HAND-BUILT, AND SAID SO: which factory seat a session is. The real host reads it from the session history row a seat
/// start writes; here <c>factorySeatOf</c> names the Boss session as the factory's Boss seat directly. That link is what
/// puts the report on a contributor's customer page, so that step is proven GIVEN the history row; the devlinux run proves
/// the row itself (step 6). The session's number and name are hand-built too, as nothing.
///
/// The people: Acme's Owner (on their own team Director, OWNERPC, with a Boss session on it); Alice, a Developer of Acme
/// with her own Director on Acme; Mike and Nina, contributors of Acme; Beta, another team of another Owner.
/// </summary>
public sealed class TeamFactoryOnTheTeamDirectorTests : IAsyncLifetime
{
    private const string Owner = "sub-tf-owner";
    private const string Alice = "sub-tf-alice";
    private const string Mike = "sub-tf-mike";
    private const string Nina = "sub-tf-nina";
    private const string BetaOwner = "sub-tf-beta-owner";
    private const string Factory = "demo-website";
    private const string OwnerDirector = "director-owner";
    private const string AliceDirector = "director-alice";
    private const string OwnerMachine = "OWNERPC";
    private const string AliceMachine = "ALICEPC";
    private static readonly string BossSession = "b0550000-0000-4000-8000-000000000001";
    private static readonly string AliceSession = "a11ce000-0000-4000-8000-000000000001";
    private static readonly DateTime Now = new(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>A report in the shape the publish route accepts, with no questions in it.</summary>
    private const string ReportHtml =
        "<header data-dev-report=\"header\" data-dev-report-status=\"done\"><h1>Menu page is live</h1></header>" +
        "<section data-dev-report=\"summary\"><p>The menu page went live.</p></section>" +
        "<section data-dev-report=\"questions\"><p data-dev-report-no-questions>No questions.</p></section>" +
        "<section data-dev-report=\"detail\"><p>Built and checked.</p></section>";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly string _missionsDir = Path.Combine(Path.GetTempPath(), "cc-tf-missions-" + Guid.NewGuid().ToString("N"));
    private GatewayDatabase _db = null!;
    private DeviceRegistry _devices = null!;
    private TenantRegistry _tenants = null!;
    private TeamRegistry _teams = null!;
    private DirectorRegistry _directors = null!;
    private SessionKeyRegistry _sessionKeys = null!;
    private DevReportStore _reports = null!;
    private FactoryRegistryStore _factories = null!;
    private FactoryActivityRecord _activity = null!;
    private HostedTenantBoundary _boundary = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private string _acme = null!;
    private string _beta = null!;
    private readonly Dictionary<string, string> _keys = new(StringComparer.Ordinal);
    private readonly List<string> _reached = new();
    private YourInboxStore _inbox = null!;

    public async Task InitializeAsync()
    {
        var ambient = new AsyncLocalTenantContext();
        _db = _harness.Open(ambient);
        _tenants = new TenantRegistry(_db);
        foreach (var (subject, email) in new[]
                 {
                     (Owner, "owner@example.com"), (Alice, "alice@example.com"), (Mike, "mike@example.com"),
                     (Nina, "nina@example.com"), (BetaOwner, "beta@example.com"),
                 })
            _tenants.MintOrLookupBySubject(subject, email);
        _teams = new TeamRegistry(_db, _tenants);
        var access = new TeamAccess(_teams);
        _devices = new DeviceRegistry(_db, _harness.LegacyPath("devices.json"), isHosted: true, teamsReleased: true);
        _boundary = new HostedTenantBoundary(ambient, _devices, hosted: true);
        _directors = new DirectorRegistry(_harness.LegacyPath("instances"));
        var pushed = new PushedSessionStore();
        _sessionKeys = new SessionKeyRegistry(_db, isHosted: true, clock: () => Now);
        _reports = new DevReportStore(_db);
        _factories = new FactoryRegistryStore(_db);
        _activity = new FactoryActivityRecord(_db);
        var ownership = new TeamCallerOwnership(_directors, pushed, _devices, new SessionTurnStore(_db), _boundary, _sessionKeys,
            reportAuthor: (tenant, id) => _reports.Get(tenant, id)?.AuthorSubject);
        var gate = new TeamEndpointGate(access, _teams, _tenants, _boundary, ownership);
        var factorySwitch = new FactoryAgentsSwitch(machineWide: false, new TenantSettingsStore(_db), log: _ => { });
        var skills = new SkillStore(_db, deferInitialize: true);
        var workflows = new WorkflowStore(_db, deferInitialize: true);
        using (ambient.Enter(TenantId.System))
        {
            skills.Initialize();
            workflows.Initialize();
        }
        var delivery = new DevReportDelivery(_reports,
            (_, _) => new DevReportSessionLiveness(DevReportSessionReach.Busy, null, "no session is live in these tests"),
            (_, _) => null, CancellationToken.None, () => Now);

        // The teams. Factories are switched on for Acme and Beta, as Pro carries them into a team its Owner owns.
        _acme = _teams.CreateTeam(Owner, "Acme").Team!.TeamId;
        _beta = _teams.CreateTeam(BetaOwner, "Beta").Team!.TeamId;
        factorySwitch.Set(new TenantId(_acme), true, "the administrator (test)", "factories for Acme", Now);
        factorySwitch.Set(new TenantId(_beta), true, "the administrator (test)", "factories for Beta", Now);
        Assert.True(_teams.AddMember(_acme, Alice, TeamRole.Developer).IsDone);
        Assert.True(_teams.AddMember(_acme, Mike, TeamRole.Collaborator).IsDone);
        Assert.True(_teams.AddMember(_acme, Nina, TeamRole.Collaborator).IsDone);

        // The keys, minted the way hosted enrolment mints them.
        _keys["owner-team-browser"] = _devices.RegisterForTenant(new TenantId(_acme), Owner,
            HostedEnrollmentEndpoint.TeamScopedDeviceId(_acme, Owner, "browser-owner"), "Browser", deviceType: "browser").DeviceKey;
        _keys["owner-director"] = TeamDirector(Owner, OwnerDirector, OwnerMachine);
        _keys["alice-director"] = TeamDirector(Alice, AliceDirector, AliceMachine);
        _keys["boss"] = SessionKey(OwnerDirector, BossSession);
        _keys["alice-session"] = SessionKey(AliceDirector, AliceSession);
        foreach (var (name, subject) in new[] { ("owner", Owner), ("mike", Mike), ("nina", Nina), ("beta-owner", BetaOwner) })
            _keys[name + "-browser"] = _devices.RegisterForTenant(_tenants.MintOrLookupBySubject(subject, null), subject,
                "browser-" + name, "Browser", deviceType: "browser").DeviceKey;

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Services.AddRouting();
        builder.Services.AddLogging();
        builder.WebHost.UseTestServer();
        _app = builder.Build();

        // The hosted host's order: authentication, the tenant scope of the verified key, routing, the team gate.
        var auth = new AuthMiddleware.RequireToken
        {
            Hosted = true, Devices = _devices, Sessions = _sessionKeys, Boundary = _boundary,
            IsRaised = _ => false, RecordRaisedAction = (_, _, _, _) => { },
        };
        _app.Use(async (ctx, next) => await AuthMiddleware.Run(ctx, auth, next));
        _app.Use(async (ctx, next) =>
        {
            if (_boundary.ResolveRequestTenant(ctx) is { } tenant)
            {
                using (_boundary.EnterScope(tenant))
                    await next();
            }
            else
            {
                await next();
            }
        });
        _app.UseRouting();
        _app.Use(async (ctx, next) => await gate.RunAsync(ctx, () => next()));

        // The real routes the path writes and reads through.
        var factoryGate = FactoryAgentsGate.Group(_app, factorySwitch, ctx => GatewayEndpoints.ResolveReadTenant(ctx, _boundary));
        FactoryActivityEndpoints.Map(factoryGate, _activity);
        FactoryRegistryEndpoints.Map(factoryGate, ctx => GatewayEndpoints.ResolveReadTenant(ctx, _boundary), _factories,
            sessionStart: (_, _) => null, nowUtc: () => Now, isDefined: (_, _) => false);
        DevReportEndpoints.Map(_app, _reports, delivery, _boundary,
            naming: (_, _) => new DevReportSessionNaming(null, null),
            // The session history names the Boss session as the factory's Boss seat; nothing else is a seat.
            factorySeatOf: (_, sid) => sid == BossSession ? new DevReportFactorySeatIdentity(Factory, "boss") : null,
            author: new DevReportAuthor(_teams, ownership),
            missionDocuments: new MissionDocumentLinker(new MissionStore(_missionsDir, adoptUnattributedAs: null), (_, _) => null));
        _inbox = new YourInboxStore(_db);
        YourInboxEndpoints.Map(_app, factoryGate, new YourInboxEndpoints.Parts(
            Store: _inbox,
            ResolveTenant: ctx => GatewayEndpoints.ResolveReadTenant(ctx, _boundary),
            Factories: tenant => _factories.List(tenant),
            Naming: (_, _) => new DevReportSessionNaming(null, null),
            FactorySeatOf: (_, sid) => sid == BossSession ? new DevReportFactorySeatIdentity(Factory, "boss") : null,
            ReportTitle: (tenant, id) => _reports.Get(tenant, id)?.Title,
            Liveness: (_, _) => new DevReportSessionLiveness(DevReportSessionReach.Busy, null, "no session is live in these tests"),
            PlaceOf: (_, _) => null,
            DeliverToSession: (_, _, _) => throw new InvalidOperationException("no notice is delivered in these tests"),
            SendAsAgent: (_, _, _, _) => throw new InvalidOperationException("no agent reply is sent in these tests"),
            SendToFactory: (_, _, _) => throw new InvalidOperationException("nothing is left in a factory inbox in these tests"),
            MessageSent: (_, _, _) => { },
            NowUtc: () => Now));
        TeamRoutes.Map(_app, new TeamRouteServices(
            Teams: _teams, Access: access, Boundary: _boundary, Tenants: _tenants, Devices: _devices,
            FleetMap: new TeamFleetMap(_teams, access, _directors, ownership, pushed),
            InvitationMailer: new NoMailer(), PublicBase: () => "https://gateway.example",
            Requests: new TeamRequestStore(_db, _teams), Skills: skills, Workflows: workflows,
            MentorStore: new TeamMentorStore(_db), TimeZoneOf: _ => "UTC", MentorRunning: false,
            Reports: _reports, Recipients: new DevReportRecipients(_db), Comments: new DevReportPersonComments(_db),
            Delivery: delivery, Factories: _factories, Activity: _activity, FactoryContributors: new FactoryContributors(_db),
            FactorySwitch: factorySwitch, UtcNow: () => Now));

        // What a Director pulls while it runs a factory, and the Boss's own factory inbox: reached or not.
        foreach (var (method, pattern) in DirectorAndBossRoutes)
            _app.MapMethods(pattern, new[] { method }, (HttpContext ctx) =>
            {
                _reached.Add($"{method} {pattern}");
                return Results.Ok(new { reached = true });
            });

        await _app.StartAsync();
        _http = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        _devices.Dispose();
        _harness.Dispose();
        if (Directory.Exists(_missionsDir)) Directory.Delete(_missionsDir, recursive: true);
    }

    /// <summary>What the team's Director pulls for the factories it runs, and what its Boss session reads and answers in
    /// its own factory inbox - each with the real route pattern the hosted Gateway maps.</summary>
    private static readonly (string Method, string Pattern)[] DirectorAndBossRoutes =
    {
        ("GET", "/directors/{directorId}/triggers"),
        ("POST", "/directors/{directorId}/triggers/{id}/checks"),
        ("GET", "/directors/{directorId}/factory-checks/{factory}/{version:int}"),
        ("GET", "/directors/{directorId}/factory-check-inputs/{factory}/{version:int}"),
        ("GET", "/sessions/{sid}/factory-inbox"),
        ("POST", "/sessions/{sid}/factory-inbox/{messageId}/decide"),
    };

    /// <summary>A member's Director enrolled into Acme, registered on its own key the way the tunnel's Hello does.</summary>
    private string TeamDirector(string subject, string directorId, string machine)
    {
        var deviceId = HostedEnrollmentEndpoint.TeamScopedDeviceId(_acme, subject, directorId);
        var key = _devices.RegisterForTenant(new TenantId(_acme), subject, deviceId, machine).DeviceKey;
        _directors.RegisterFromStream(directorId, machine, "u", "1.0", 1, Now, new TenantId(_acme), directorId, "device:" + deviceId);
        return key;
    }

    /// <summary>A session key the Director wrote for one of its sessions, as a Director registers it at start.</summary>
    private string SessionKey(string directorId, string sessionId)
    {
        var key = GatewaySessionKey.Mint();
        Assert.True(_sessionKeys.Register(new TenantId(_acme), directorId, sessionId, GatewaySessionKey.Hash(key), Now.AddHours(1)));
        return key;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Call(string who, string method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _keys[who]);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var json = text.TrimStart().StartsWith('{') ? JsonDocument.Parse(text).RootElement.Clone() : default;
        return (response.StatusCode, json);
    }

    private static string Describe((HttpStatusCode Status, JsonElement Body) answer) =>
        $"{(int)answer.Status} {(answer.Body.ValueKind == JsonValueKind.Undefined ? "" : answer.Body.ToString())}";

    private static object Registration(string computer) => new
    {
        factory = Factory, title = "Demo website", folder = "/srv/demo-website", computer, bossSeat = "boss",
        seats = new[] { new { id = "boss", name = "Boss", role = "Boss", briefFile = "agents/boss.yaml" } },
    };

    /// <summary>The Owner creates the factory in the team, from their browser working in the team.</summary>
    private async Task CreateFactory()
    {
        var created = await Call("owner-team-browser", "PUT", FactoryRegistryEndpoints.RegistryRoute, Registration(OwnerMachine));
        Assert.True(created.Status == HttpStatusCode.OK, Describe(created));
    }

    /// <summary>The Boss session publishes one report with its own session key.</summary>
    private async Task<Guid> PublishFromTheBoss()
    {
        var published = await Call("boss", "POST", $"/sessions/{BossSession}/dev-reports",
            new { key = @"c:\factories\demo-website\reports\menu.html", html = ReportHtml });
        Assert.True(published.Status == HttpStatusCode.OK, Describe(published));
        return published.Body.GetProperty("report").GetProperty("id").GetGuid();
    }

    private static List<string> Ids(JsonElement body, string list) =>
        body.GetProperty(list).EnumerateArray().Select(r => r.GetProperty("id").GetString()!).ToList();

    // ---------------------------------------------------------------- the whole path

    [Fact]
    public async Task TeamFactory_CreatedRunAndReported_LandsInTheTeamAndReachesOnlyTheContributorItIsSentTo()
    {
        // 1. The Owner creates the factory in the team.
        await CreateFactory();
        Assert.Single(_factories.List(new TenantId(_acme)));
        Assert.Empty(_factories.List(_tenants.MintOrLookupBySubject(Owner, null)));
        Assert.Empty(_factories.List(new TenantId(_beta)));

        // 2. The Owner's team Director pulls what it needs to run it, for itself.
        foreach (var path in new[]
                 {
                     $"/directors/{OwnerDirector}/triggers", $"/directors/{OwnerDirector}/factory-checks/{Factory}/1",
                     $"/directors/{OwnerDirector}/factory-check-inputs/{Factory}/1",
                 })
        {
            var pulled = await Call("owner-director", "GET", path);
            Assert.True(pulled.Status == HttpStatusCode.OK, $"{path}: {Describe(pulled)}");
        }

        // 3. Its Boss session reads its factory inbox, records a line for the contributor, and publishes a report.
        var inbox = await Call("boss", "GET", $"/sessions/{BossSession}/factory-inbox");
        Assert.True(inbox.Status == HttpStatusCode.OK, Describe(inbox));
        var line = await Call("boss", "POST", FactoryActivityEndpoints.Route, new
        {
            factory = Factory, factoryAgent = "boss", what = "boss published the menu page", outcome = "done",
            forContributor = "Your menu page is live.",
        });
        Assert.True(line.Status == HttpStatusCode.Created, Describe(line));
        var reportId = await PublishFromTheBoss();

        // 4. The report is in the TEAM's tenant, written by the Owner, as the factory's Boss - and nowhere else.
        var stored = _reports.Get(new TenantId(_acme), reportId);
        Assert.NotNull(stored);
        Assert.Equal(Owner, stored!.AuthorSubject);
        Assert.Null(_reports.Get(_tenants.MintOrLookupBySubject(Owner, null), reportId));
        Assert.Null(_reports.Get(new TenantId(_beta), reportId));
        var personalList = await Call("owner-browser", "GET", "/dev-reports");
        Assert.True(personalList.Status == HttpStatusCode.OK, Describe(personalList));
        Assert.DoesNotContain(reportId.ToString("D"), personalList.Body.ToString());

        // 5. The Owner, from their own account, finds it among their reports in the team, shares the factory with Mike
        //    and sends him the report.
        var mine = await Call("owner-browser", "GET", $"/teams/{_acme}/reports/mine");
        Assert.True(mine.Status == HttpStatusCode.OK, Describe(mine));
        Assert.Contains(reportId.ToString("D"), Ids(mine.Body, "reports"));
        var shared = await Call("owner-browser", "POST", $"/teams/{_acme}/factories/{Factory}/contributors",
            new { memberId = TeamMemberIds.For(_acme, Mike) });
        Assert.True(shared.Status == HttpStatusCode.OK, Describe(shared));
        var sent = await Call("owner-browser", "POST", $"/teams/{_acme}/reports/mine/{reportId:D}/recipients",
            new { memberIds = new[] { TeamMemberIds.For(_acme, Mike) }, version = 1 });
        Assert.True(sent.Status == HttpStatusCode.OK, Describe(sent));

        // 6. Mike sees the factory, its line for him and the report; Nina sees none of it.
        var mikeInbox = await Call("mike-browser", "GET", $"/teams/{_acme}/contributor/inbox");
        Assert.True(mikeInbox.Status == HttpStatusCode.OK, Describe(mikeInbox));
        Assert.Contains(reportId.ToString("D"), Ids(mikeInbox.Body, "items"));
        var mikePage = await Call("mike-browser", "GET", $"/teams/{_acme}/contributor/factories/{Factory}");
        Assert.True(mikePage.Status == HttpStatusCode.OK, Describe(mikePage));
        Assert.Contains("Your menu page is live.", mikePage.Body.ToString());
        Assert.Contains("Menu page is live", mikePage.Body.GetProperty("sentToYou").ToString());

        var ninaInbox = await Call("nina-browser", "GET", $"/teams/{_acme}/contributor/inbox");
        Assert.True(ninaInbox.Status == HttpStatusCode.OK, Describe(ninaInbox));
        Assert.DoesNotContain(reportId.ToString("D"), ninaInbox.Body.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await Call("nina-browser", "GET", $"/teams/{_acme}/contributor/factories/{Factory}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call("nina-browser", "GET", $"/teams/{_acme}/reports/sent-to-me/{reportId:D}")).Status);

        // 7. Another team's Owner cannot reach the team at all.
        Assert.Equal(HttpStatusCode.NotFound, (await Call("beta-owner-browser", "GET", $"/teams/{_acme}/reports/mine")).Status);
    }

    // ---------------------------------------------------------------- who else may run it

    [Fact]
    public async Task AnotherMembersDirector_PullingTheOwnersDirectorsWork_IsRefused()
    {
        await CreateFactory();

        var answer = await Call("alice-director", "GET", $"/directors/{OwnerDirector}/triggers");

        Assert.True(answer.Status == HttpStatusCode.Forbidden, Describe(answer));
        Assert.Equal(TeamEndpointGate.RefusalCode, answer.Body.GetProperty("code").GetString());
        Assert.DoesNotContain(_reached, r => r.StartsWith("GET /directors/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADevelopersOwnDirector_PullingFactoryWork_IsRefused_BecauseOnlyTheOwnerRunsTheTeamsFactories()
    {
        var answer = await Call("alice-director", "GET", $"/directors/{AliceDirector}/triggers");

        Assert.True(answer.Status == HttpStatusCode.Forbidden, Describe(answer));
        Assert.Empty(_reached);
    }

    [Fact]
    public async Task ADevelopersSession_ReadingAFactoryInbox_IsRefused()
    {
        var answer = await Call("alice-session", "GET", $"/sessions/{AliceSession}/factory-inbox");

        Assert.True(answer.Status == HttpStatusCode.Forbidden, Describe(answer));
        Assert.Empty(_reached);
    }

    [Fact]
    public async Task TheBossSession_ReadingAnotherMembersSessionsFactoryInbox_IsRefused()
    {
        var answer = await Call("boss", "GET", $"/sessions/{AliceSession}/factory-inbox");

        Assert.True(answer.Status == HttpStatusCode.Forbidden, Describe(answer));
        Assert.Empty(_reached);
    }

    [Fact]
    public async Task TheBossSession_PublishingAsAnotherSession_IsRefusedAndWritesNothing()
    {
        var answer = await Call("boss", "POST", $"/sessions/{AliceSession}/dev-reports",
            new { key = @"c:\elsewhere\report.html", html = ReportHtml });

        Assert.True(answer.Status == HttpStatusCode.Forbidden, Describe(answer));
        Assert.Empty(_reports.List(new TenantId(_acme), null));
    }

    // ---------------------------------------------------------------- the report reaches only who it is sent to

    [Fact]
    public async Task ABossReport_NotSentToAContributor_IsNotInTheirInbox_EvenWithTheFactoryShared()
    {
        await CreateFactory();
        var reportId = await PublishFromTheBoss();
        var shared = await Call("owner-browser", "POST", $"/teams/{_acme}/factories/{Factory}/contributors",
            new { memberId = TeamMemberIds.For(_acme, Mike) });
        Assert.True(shared.Status == HttpStatusCode.OK, Describe(shared));

        var inbox = await Call("mike-browser", "GET", $"/teams/{_acme}/contributor/inbox");

        Assert.True(inbox.Status == HttpStatusCode.OK, Describe(inbox));
        Assert.DoesNotContain(reportId.ToString("D"), inbox.Body.ToString());
    }

    [Fact]
    public async Task ABossReport_SentToAContributorWithoutTheFactoryShared_IsInTheirInbox_ButTheFactoryPageIsNotFound()
    {
        await CreateFactory();
        var reportId = await PublishFromTheBoss();
        var sent = await Call("owner-browser", "POST", $"/teams/{_acme}/reports/mine/{reportId:D}/recipients",
            new { memberIds = new[] { TeamMemberIds.For(_acme, Nina) }, version = 1 });
        Assert.True(sent.Status == HttpStatusCode.OK, Describe(sent));

        var inbox = await Call("nina-browser", "GET", $"/teams/{_acme}/contributor/inbox");
        var page = await Call("nina-browser", "GET", $"/teams/{_acme}/contributor/factories/{Factory}");

        Assert.Contains(reportId.ToString("D"), Ids(inbox.Body, "items"));
        Assert.Equal(HttpStatusCode.NotFound, page.Status);
    }

    // ---------------------------------------------------------------- the routes the path uses

    /// <summary>EVERY ROUTE A TEAM FACTORY'S BOSS SESSION WRITES OR READS while it runs, as the Boss's commands send them
    /// (cc-devthrottle factory ..., cc-dev-reports ...): the concrete path a session key sends, the route pattern the
    /// team gate matches, and the row it states. The pull request lists the same routes.</summary>
    public static TheoryData<string, string, string, TeamAction, TeamTarget> BossRoutes => new()
    {
        { "GET", "/sessions/S/factory-inbox", "/sessions/{sid}/factory-inbox", TeamAction.OpenRunAndTalkToTeamFactories, TeamTarget.CallersOwn },
        { "POST", "/sessions/S/factory-inbox/m-1/decide", "/sessions/{sid}/factory-inbox/{messageId}/decide", TeamAction.OpenRunAndTalkToTeamFactories, TeamTarget.CallersOwn },
        { "POST", "/sessions/S/dev-reports", "/sessions/{sid}/dev-reports", TeamAction.RunSessionsOnOwnComputers, TeamTarget.CallersOwn },
        { "GET", "/sessions/S/dev-reports", "/sessions/{sid}/dev-reports", TeamAction.RunSessionsOnOwnComputers, TeamTarget.CallersOwn },
        { "POST", "/sessions/S/dev-reports/r-1/replies", "/sessions/{sid}/dev-reports/{reportId}/replies", TeamAction.RunSessionsOnOwnComputers, TeamTarget.CallersOwn },
        { "GET", "/sessions/S/dev-report-answers", "/sessions/{sid}/dev-report-answers", TeamAction.RunSessionsOnOwnComputers, TeamTarget.CallersOwn },
        { "POST", "/sessions/S/dev-report-answers/r-1/claim", "/sessions/{sid}/dev-report-answers/{reportId}/claim", TeamAction.RunSessionsOnOwnComputers, TeamTarget.CallersOwn },
        { "POST", "/sessions/S/dev-report-answers/r-1/ack", "/sessions/{sid}/dev-report-answers/{reportId}/ack", TeamAction.RunSessionsOnOwnComputers, TeamTarget.CallersOwn },
        { "POST", "/gateway/factory/activity", "/gateway/factory/activity", TeamAction.OpenRunAndTalkToTeamFactories, TeamTarget.Team },
        { "GET", "/gateway/factory/activity", "/gateway/factory/activity", TeamAction.OpenRunAndTalkToTeamFactories, TeamTarget.Team },
        { "PUT", "/gateway/factory/registry", "/gateway/factory/registry", TeamAction.OpenRunAndTalkToTeamFactories, TeamTarget.Team },
        { "GET", "/gateway/factory/registry", "/gateway/factory/registry", TeamAction.OpenRunAndTalkToTeamFactories, TeamTarget.Team },
        { "POST", "/gateway/factory/goal-numbers", "/gateway/factory/goal-numbers", TeamAction.OpenRunAndTalkToTeamFactories, TeamTarget.Team },
        { "POST", "/gateway/factory/seats/writer/start", "/gateway/factory/seats/{seat}/start", TeamAction.OpenRunAndTalkToTeamFactories, TeamTarget.Team },
        { "GET", "/gateway/factories", "/gateway/factories", TeamAction.OpenRunAndTalkToTeamFactories, TeamTarget.Team },
        { "GET", "/gateway/factories/demo-website", "/gateway/factories/{factory}", TeamAction.OpenRunAndTalkToTeamFactories, TeamTarget.Team },
        { "POST", "/factory-records/demo-website/journal", "/factory-records/{factory}/journal", TeamAction.OpenRunAndTalkToTeamFactories, TeamTarget.Team },
        { "GET", "/factory-memory/notes", "/factory-memory/notes", TeamAction.OpenRunAndTalkToTeamFactories, TeamTarget.Team },
    };

    [Theory]
    [MemberData(nameof(BossRoutes))]
    public void BossRoute_PassesTheSessionKeyGuard_AndStatesItsTeamRule(string method, string path, string pattern,
        TeamAction action, TeamTarget target)
    {
        var guard = SessionKeyGuard.Check(method, path);
        var rule = TeamEndpointRules.Find(method, pattern);

        Assert.True(guard.Allowed, $"{method} {path}: {guard.Reason}");
        Assert.NotNull(rule);
        Assert.Equal((action, target), (rule!.Action, rule.Target));
    }

    /// <summary>EVERY ROUTE THE TEAM'S DIRECTOR WRITES OR READS for the factory and its sessions, with its device key.</summary>
    public static TheoryData<string, string, TeamAction, TeamTarget> DirectorRoutes => new()
    {
        { "GET", "/director-stream", TeamAction.RunSessionsOnOwnComputers, TeamTarget.CallersOwn },
        { "GET", "/directors/{directorId}/triggers", TeamAction.OpenRunAndTalkToTeamFactories, TeamTarget.CallersOwn },
        { "POST", "/directors/{directorId}/triggers/{id}/checks", TeamAction.OpenRunAndTalkToTeamFactories, TeamTarget.CallersOwn },
        { "GET", "/directors/{directorId}/factory-checks/{factory}/{version:int}", TeamAction.OpenRunAndTalkToTeamFactories, TeamTarget.CallersOwn },
        { "GET", "/directors/{directorId}/factory-check-inputs/{factory}/{version:int}", TeamAction.OpenRunAndTalkToTeamFactories, TeamTarget.CallersOwn },
        { "GET", "/sessions", TeamAction.RunSessionsOnOwnComputers, TeamTarget.ListCutToCallersOwn },
        { "POST", "/session-numbers/allocate", TeamAction.RunSessionsOnOwnComputers, TeamTarget.CallersOwn },
        { "POST", "/activity-events/batch", TeamAction.RunSessionsOnOwnComputers, TeamTarget.CallersOwn },
        { "POST", "/gateway/director-errors", TeamAction.RunSessionsOnOwnComputers, TeamTarget.CallersOwn },
        { "GET", "/gateway/skills", TeamAction.UseSharedSkillsAndWorkflows, TeamTarget.Team },
        // The seat kit a Boss cannot start without, and the factory's notes, pulled with the Director's own key.
        { "GET", "/factory-definitions/{factory}/versions/{version}/kit", TeamAction.OpenRunAndTalkToTeamFactories, TeamTarget.Team },
        { "GET", "/factory-memory/notes", TeamAction.OpenRunAndTalkToTeamFactories, TeamTarget.Team },
    };

    [Theory]
    [MemberData(nameof(DirectorRoutes))]
    public void DirectorRoute_StatesItsTeamRule(string method, string pattern, TeamAction action, TeamTarget target)
    {
        var rule = TeamEndpointRules.Find(method, pattern);

        Assert.NotNull(rule);
        Assert.Equal((action, target), (rule!.Action, rule.Target));
    }

    // ---------------------------------------------------------------- Your inbox in a team: the Owner's own sessions only

    [Fact]
    public async Task YourInbox_TheOwnersBossSession_SendsAndReadsTheTeamsInbox()
    {
        var sent = await Call("boss", "POST", $"/sessions/{BossSession}/inbox", new { subject = "Menu page", body = "It is live." });
        var listed = await Call("boss", "GET", $"/sessions/{BossSession}/inbox?view=inbox");

        Assert.True(sent.Status == HttpStatusCode.Created, Describe(sent));
        Assert.True(listed.Status == HttpStatusCode.OK, Describe(listed));
        Assert.Contains("Menu page", listed.Body.ToString());
        // In the team's inbox, and not in the Owner's personal one.
        Assert.Equal(1, _inbox.List(new TenantId(_acme), "inbox", 50).Total);
        Assert.Equal(0, _inbox.List(_tenants.MintOrLookupBySubject(Owner, null), "inbox", 50).Total);
    }

    [Theory]
    [InlineData("POST", "")]
    [InlineData("GET", "")]
    [InlineData("GET", "/ID")]
    [InlineData("POST", "/ID/state")]
    [InlineData("POST", "/ID/reply")]
    public async Task YourInbox_ADevelopersOwnSession_IsRefused_AndReadsNothingTheOwnersFactoryLeft(string method, string tail)
    {
        // Before this piece the "/sessions" rule let a Developer's own session through to the TEAM's one inbox.
        var sent = await Call("boss", "POST", $"/sessions/{BossSession}/inbox", new { subject = "For the Owner", body = "Private." });
        Assert.True(sent.Status == HttpStatusCode.Created, Describe(sent));
        var id = sent.Body.GetProperty("message").GetProperty("id").GetString()!;

        var answer = await Call("alice-session", method, $"/sessions/{AliceSession}/inbox" + tail.Replace("ID", id),
            method == "POST" ? new { subject = "s", body = "b", state = "read", text = "t" } : null);

        Assert.True(answer.Status == HttpStatusCode.Forbidden, Describe(answer));
        Assert.Equal(TeamEndpointGate.RefusalCode, answer.Body.GetProperty("code").GetString());
        Assert.DoesNotContain("Private.", answer.Body.ToString());
        Assert.Equal(1, _inbox.List(new TenantId(_acme), "inbox", 50).Total);
    }

    [Fact]
    public async Task YourInbox_TheBossNamingAnotherMembersSession_IsRefusedAsWatchingIt()
    {
        var answer = await Call("boss", "GET", $"/sessions/{AliceSession}/inbox");

        Assert.True(answer.Status == HttpStatusCode.Forbidden, Describe(answer));
        Assert.Equal(TeamEndpointGate.RefusalCode, answer.Body.GetProperty("code").GetString());
    }

    [Fact]
    public void EveryYourInboxSessionRoute_StatesTheFactoryRow()
    {
        // Walks the routes YourInboxEndpoints really maps, so a parameter renamed there cannot fall back to the
        // "/sessions" rule - which would reopen the team's inbox to every member's own session - while the tests stay green.
        var routes = ((IEndpointRouteBuilder)_app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Select(e => (Pattern: TeamEndpointRules.Normalize(e.RoutePattern.RawText),
                Methods: e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? new[] { "GET" }))
            .Where(r => System.Text.RegularExpressions.Regex.IsMatch(r.Pattern, @"^/sessions/\{[^}]+\}/inbox(/|$)"))
            .SelectMany(r => r.Methods.Select(m => (Method: m, r.Pattern)))
            .ToList();

        Assert.Equal(5, routes.Count);
        foreach (var (method, pattern) in routes)
        {
            var rule = TeamEndpointRules.Find(method, pattern);
            Assert.True(rule is { Action: TeamAction.OpenRunAndTalkToTeamFactories, Target: TeamTarget.CallersOwn },
                $"{method} {pattern} states {rule?.Prefix ?? "nothing"}");
        }
    }

    private sealed class NoMailer : ITeamInvitationMailer
    {
        public Task<TeamInvitationMailResult> SendAsync(string invitationId, string teamId, string acceptToken, CancellationToken ct = default) =>
            throw new InvalidOperationException("no invitation is sent in these tests");

        public Task<TeamInvitationMailResult> SendAddedAsync(string invitationId, string teamId, string acceptToken, CancellationToken ct = default) =>
            throw new InvalidOperationException("no invitation is sent in these tests");
    }
}
