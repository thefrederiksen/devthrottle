using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CcDirector.Core.Account;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.DevReports;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Inbox;
using CcDirector.Gateway.Factory.Registry;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Skills;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Teams.Mentor;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Tests.Teams.Questions;
using CcDirector.Gateway.Util;
using CcDirector.Gateway.Workflows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams.Contributor;

/// <summary>
/// A CONTRIBUTOR'S WORDS REACH NO AGENT WITHOUT THE OWNER'S FORWARD (Team implementation piece 4.3, devthrottle_internal#2800;
/// security review Risk 4; design.html: "Their words reach no agent until you have read them").
///
/// The proof is a walk and a look, in memory with no port, over exactly what the host maps (<see cref="TeamRoutes.Map"/>)
/// behind the real team gate:
/// <list type="number">
/// <item>THE WALK. Every mapped WRITE route under <c>/teams</c> and <c>/team-invitations</c> is called with a contributor's
/// own browser key, with every route parameter set to a real thing the contributor may reach (their team, a report sent to
/// them with a question in it, a factory sent to them, a message they wrote) and a body that carries one marker word in
/// every field a route might read words from.</item>
/// <item>THE LOOK. Then every column of every table in the database is searched for the marker. It may be found only in the
/// person-only tables - the team's messages (<c>team_requests</c>) and a person's comments on a report
/// (<c>dev_report_comments</c>) - which no session, Director, factory or Boss reads. Every table a session or a factory
/// reads - the factory inbox, the account's Your inbox, the fleet messages, a report's answers waiting for a session, the
/// activity record, prompts - is covered by the same search, because it searches every table there is.</item>
/// <item>A PRESENCE, NOT AN ABSENCE. The walk must have written the marker somewhere (the message and the comment), so a
/// walk that reached no route cannot pass as a clean one.</item>
/// <item>AND THE ONE WAY OUT. When the team's Owner forwards the message, the marker appears in the factory inbox - and
/// only then.</item>
/// </list>
/// </summary>
public sealed class ContributorWordsReachNoAgentTests : IAsyncLifetime
{
    private const string Owner = "sub-walk-owner";
    private const string Carla = "sub-walk-carla";
    private const string CallerHeader = "X-Test-Caller";
    private const string Marker = "zqmarker7731";
    private const string QuestionId = "trial-length";
    private static readonly DateTime Now = new(2026, 11, 9, 15, 0, 0, DateTimeKind.Utc);

    /// <summary>The tables a contributor's own words may be stored in: read by people only, never on an agent's path.</summary>
    private static readonly string[] PersonOnlyTables = { "team_requests", "dev_report_comments" };

    private readonly GatewayDbTestHarness _harness = new();
    private GatewayDatabase _db = null!;
    private DeviceRegistry _devices = null!;
    private TenantRegistry _tenants = null!;
    private TeamRegistry _teams = null!;
    private TeamRequestStore _requests = null!;
    private FactoryInboxStore _factoryInbox = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private IReadOnlyList<(string Method, string Pattern)> _writeRoutes = null!;
    private string _team = null!;
    private TenantId _ownerTenant;
    private TenantId _carlaTenant;
    private Guid _report;
    private string _seedRequest = null!;

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
        var recipients = new DevReportRecipients(_db);
        var skills = new SkillStore(_db, deferInitialize: true);
        var workflows = new WorkflowStore(_db, deferInitialize: true);
        using (ambient.Enter(TenantId.System))
        {
            skills.Initialize();
            workflows.Initialize();
        }
        var gate = new TeamEndpointGate(access, _teams, _tenants, boundary, ownership);
        var factories = new FactoryRegistryStore(_db);
        var shares = new FactoryContributors(_db);
        var factorySwitch = new FactoryAgentsSwitch(machineWide: true, new CcDirector.Gateway.Settings.TenantSettingsStore(_db));
        _requests = new TeamRequestStore(_db, _teams, () => Now);
        _factoryInbox = new FactoryInboxStore(_db, _ => CcDirector.Core.Dictation.Models.DictationDictionary.Empty);

        _ownerTenant = _tenants.MintOrLookupBySubject(Owner, "owner@example.com");
        _carlaTenant = _tenants.MintOrLookupBySubject(Carla, "carla@client.example");
        _team = _teams.CreateTeam(Owner, "Acme").Team!.TeamId;
        Assert.True(_teams.AddMember(_team, Carla, TeamRole.Collaborator).IsDone);

        // What the contributor can reach: a report sent to them with a question in it, a factory sent to them by name, and
        // a message of their own.
        var team = new TenantId(_team);
        _report = reports.Publish(team, "5e551011-0000-4000-8000-00000000abcd", @"C:\work\pricing.html", TeamQuestionsTests.Html(QuestionId),
            "waiting-on-you", "Pricing", Now, Owner, new DevReportFactorySeatIdentity("website", "boss")).Report.Id;
        Assert.True(recipients.Send(team, _report, Owner, new[] { Carla }, 1, Now).Sent);
        factories.Register(team, new RegisterFactoryRequest
        {
            Factory = "website", Title = "Erik's website", Folder = "/srv/website", Computer = "BOX", BossSeat = "boss",
            Seats = { new FactorySeatManifest { Id = "boss", Name = "Boss", Role = "Boss", BriefFile = "agents/boss.yaml" } },
        }, "the owner (test)", Now);
        Assert.True(shares.Share(team, "website", Carla, Owner, Now));
        _seedRequest = _requests.Send(_team, Carla, "A first message").Request!.Id;

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
            Requests: _requests, Skills: skills, Workflows: workflows,
            MentorStore: new TeamMentorStore(_db), TimeZoneOf: _ => "UTC", MentorRunning: false,
            Reports: reports, Recipients: recipients, Comments: new DevReportPersonComments(_db),
            // No Director is connected: an answer that is queued for the session waits in the report's items, which the
            // look below searches with everything else.
            Delivery: new DevReportDelivery(reports,
                (_, _) => new DevReportSessionLiveness(DevReportSessionReach.Busy, null, "no session in this test"),
                (_, _) => null, CancellationToken.None, () => Now),
            Factories: factories, Activity: new FactoryActivityRecord(_db), FactoryContributors: shares, FactorySwitch: factorySwitch,
            FactoryInbox: _factoryInbox, FactoryInboxMessageSent: (_, _, _) => { },
            Notices: new ContributorNotices(_db, _teams, () => Now), UtcNow: () => Now));

        _writeRoutes = ((IEndpointRouteBuilder)_app).DataSources
            .SelectMany(d => d.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => TeamPersonOnlyRoutes.InAFamily(e.RoutePattern.RawText))
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? new[] { "POST" })
                .Select(m => (m, TeamEndpointRules.Normalize(e.RoutePattern.RawText))))
            .Where(r => !TeamEndpointRule.HttpMethodIsRead(r.m))
            // POST /teams creates a NEW team of the person's own, named by them - not a write into a team they are a
            // contributor on, and it reaches no one else's anything.
            .Where(r => !(r.m == "POST" && r.Item2 == "/teams"))
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

    private void StandInForAuthentication(HttpContext ctx)
    {
        var (subject, tenant) = ctx.Request.Headers[CallerHeader].ToString() switch
        {
            "owner" => (Owner, _ownerTenant),
            "carla" => (Carla, _carlaTenant),
            var other => throw new InvalidOperationException($"No test caller named '{other}'."),
        };
        ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] =
            new DeviceCredentialIdentity(subject + "|browser-1", tenant.Value, "browser", "active", subject);
        ctx.Items[AuthMiddleware.DeviceTypeItemKey] = "browser";
    }

    /// <summary>A route's address, each parameter set to the real thing the contributor may reach.</summary>
    private string Address(string pattern) =>
        Regex.Replace(pattern, @"\{\**([^}:=?]+)([^}]*)\}", m => m.Groups[1].Value switch
        {
            "teamId" => _team,
            "reportId" => _report.ToString("D"),
            "questionId" => QuestionId,
            "factory" => "website",
            "requestId" => _seedRequest,
            "memberId" => TeamMemberIds.For(_team, Owner),
            var name => "x-" + name,
        });

    /// <summary>Two bodies per route: every field a route might read words from carries the marker - once with the
    /// question's "own answer" in the person's words, once choosing an option the agent wrote.</summary>
    private static IEnumerable<string> Bodies()
    {
        var words = new Dictionary<string, object?>
        {
            ["text"] = Marker + " the words", ["reason"] = Marker, ["comment"] = Marker, ["note"] = Marker, ["body"] = Marker,
            ["subject"] = Marker, ["name"] = Marker, ["message"] = Marker, ["words"] = Marker, ["title"] = Marker,
            ["content"] = Marker, ["description"] = Marker, ["email"] = Marker + "@client.example", ["role"] = "Developer",
            ["factory"] = "website", ["version"] = 1, ["optionValue"] = "14",
        };
        yield return JsonSerializer.Serialize(new Dictionary<string, object?>(words) { ["ownAnswer"] = Marker + " my own answer" });
        yield return JsonSerializer.Serialize(words);
    }

    private async Task<HttpStatusCode> Send(string caller, string method, string path, string json)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(CallerHeader, caller);
        using var response = await _http.SendAsync(request);
        return response.StatusCode;
    }

    /// <summary>Every table and column holding the marker, searched straight in the database file past every tenant filter.</summary>
    private IReadOnlyList<string> WhereTheMarkerIs()
    {
        var found = new List<string>();
        using var connection = new SqliteConnection($"Data Source={_harness.DbPath};Pooling=False");
        connection.Open();
        var tables = new List<string>();
        using (var list = connection.CreateCommand())
        {
            list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
            using var reader = list.ExecuteReader();
            while (reader.Read()) tables.Add(reader.GetString(0));
        }
        Assert.True(tables.Count > 50, $"Only {tables.Count} tables were found, so the look cannot be the whole database.");
        foreach (var table in tables)
        {
            var columns = new List<string>();
            using (var info = connection.CreateCommand())
            {
                info.CommandText = $"PRAGMA table_info(\"{table}\")";
                using var reader = info.ExecuteReader();
                while (reader.Read()) columns.Add(reader.GetString(1));
            }
            foreach (var column in columns)
            {
                using var count = connection.CreateCommand();
                count.CommandText = $"SELECT COUNT(*) FROM \"{table}\" WHERE CAST(\"{column}\" AS TEXT) LIKE '%{Marker}%'";
                if (Convert.ToInt32(count.ExecuteScalar()) > 0) found.Add($"{table}.{column}");
            }
        }
        return found;
    }

    private async Task WalkEveryWriteRouteAsTheContributor()
    {
        foreach (var (method, pattern) in _writeRoutes)
            foreach (var body in Bodies())
                await Send("carla", method, Address(pattern), body);
    }

    // ---------------------------------------------------------------- the walk and the look

    [Fact]
    public async Task EveryWriteRouteAContributorCanCall_TheirWordsLandOnlyInPersonOnlyTables()
    {
        // Act
        await WalkEveryWriteRouteAsTheContributor();

        // Assert: the marker is ONLY in the person-only tables...
        var found = WhereTheMarkerIs();
        var elsewhere = found.Where(f => !PersonOnlyTables.Contains(f.Split('.')[0])).ToList();
        Assert.True(elsewhere.Count == 0, "A contributor's words reached a table outside the person-only ones:\n" + string.Join("\n", elsewhere));
        // ...and it IS there: the walk wrote a message to the Owner and a comment on the report (a presence, so a walk that
        // reached nothing cannot pass as a clean one).
        Assert.Contains("team_requests.text", found);
        Assert.Contains(found, f => f.StartsWith("dev_report_comments.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheWalk_ReachesTheRoutesAContributorWritesWith()
    {
        // The routes a contributor's words can be sent on today: a message to the Owner, a comment on a report sent to
        // them, an answer to a question in it - and the Owner's forward, which the contributor must be refused.
        var names = _writeRoutes.Select(r => $"{r.Method} {r.Pattern}").ToHashSet(StringComparer.Ordinal);
        Assert.Contains("POST /teams/{teamId}/requests", names);
        Assert.Contains("POST /teams/{teamId}/reports/sent-to-me/{reportId}/comments", names);
        Assert.Contains("POST /teams/{teamId}/questions/{reportId}/{questionId}/answer", names);
        Assert.Contains("POST /teams/{teamId}/requests/{requestId}/forward", names);

        Assert.Equal(HttpStatusCode.Created, await Send("carla", "POST", Address("/teams/{teamId}/requests"), Bodies().Last()));
        Assert.Equal(HttpStatusCode.Forbidden, await Send("carla", "POST", Address("/teams/{teamId}/requests/{requestId}/forward"), Bodies().Last()));
    }

    [Fact]
    public async Task TheOwnersForward_IsTheOneWayOut_TheWordsThenReachTheFactoryInboxMarked()
    {
        // Arrange: the contributor writes; nothing of theirs is in the factory inbox.
        Assert.Equal(HttpStatusCode.Created, await Send("carla", "POST", Address("/teams/{teamId}/requests"), Bodies().Last()));
        Assert.DoesNotContain(WhereTheMarkerIs(), f => f.StartsWith("factory_inbox_messages.", StringComparison.Ordinal));
        var id = _requests.ListMine(_team, Carla).Requests.Single(r => r.Text.Contains(Marker, StringComparison.Ordinal)).Id;

        // Act: the Owner forwards it.
        var status = await Send("owner", "POST", $"/teams/{_team}/requests/{id}/forward",
            JsonSerializer.Serialize(new { factory = "website", note = "Worth doing." }));

        // Assert
        Assert.Equal(HttpStatusCode.OK, status);
        var left = Assert.Single(_factoryInbox.Thread(new TenantId(_team), "website", 10).Messages);
        Assert.Contains(Marker, left.Text, StringComparison.Ordinal);
        Assert.StartsWith("Worth doing.\n\n" + TeamRequestForwarding.QuoteIntro, left.Text);
        Assert.Contains("From: carla@client.example, a contributor on the team \"Acme\"", left.Text);
        Assert.Equal((FactoryInboxWords.Surfaces.Cockpit, Owner + "|browser-1"), (left.Surface, left.SentBy));
    }

    private sealed class NoMailer : ITeamInvitationMailer
    {
        public Task<TeamInvitationMailResult> SendAsync(string invitationId, string teamId, string acceptToken, CancellationToken ct = default) =>
            throw new InvalidOperationException("no invitation is sent in this test");

        public Task<TeamInvitationMailResult> SendAddedAsync(string invitationId, string teamId, string acceptToken, CancellationToken ct = default) =>
            throw new InvalidOperationException("no invitation is sent in this test");
    }
}
