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
using CcDirector.Gateway.Tests.Teams.Questions;
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
/// A CONTRIBUTOR SEES NO MEMBER LIST AND NO OTHER PERSON'S EMAIL (devthrottle_internal#2787, security review Risk 11;
/// the owner's decision of 8 November 2026). A Collaborator sees only that the team's Owner invited them: hired helpers
/// and customers never see each other, and every screen a Collaborator sees names another person by role, never by
/// address.
///
/// The host maps exactly what the Gateway maps (<see cref="TeamRoutes.Map"/>), in memory with no port, behind the real
/// team gate, over a team whose every member's address is known and which holds something on every screen a
/// Collaborator has: a report with a question sent to them, another Collaborator's comment, a request they sent that a
/// Manager accepted, and a waiting invitation. The walk calls EVERY mapped route under <c>/teams</c> and
/// <c>/team-invitations</c> as the Collaborator, with the team's real ids in the route and one body that every write
/// route can read, and no answer may carry another member's address. A route mapped later is walked the moment it is
/// mapped, so one that leaks an address turns this red.
/// </summary>
public sealed class TeamContributorSeesNoAddressTests : IAsyncLifetime
{
    private const string Owner = "sub-owner";
    private const string Manager = "sub-manager";
    private const string Developer = "sub-developer";
    private const string Collaborator = "sub-collaborator";
    private const string OtherCollaborator = "sub-other-collaborator";
    private const string Invitee = "sub-invitee";
    private const string CallerHeader = "X-Test-Caller";
    private const string QuestionId = "trial-length";
    private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    private static readonly IReadOnlyDictionary<string, string> Addresses = new Dictionary<string, string>
    {
        [Owner] = "owner.person@example.com",
        [Manager] = "manager.person@example.com",
        [Developer] = "developer.person@example.com",
        [Collaborator] = "collaborator.person@example.com",
        [OtherCollaborator] = "other.collaborator@example.com",
        [Invitee] = "invited.person@example.com",
    };

    private readonly GatewayDbTestHarness _harness = new();
    private GatewayDatabase _db = null!;
    private DeviceRegistry _devices = null!;
    private TenantRegistry _tenants = null!;
    private TeamRegistry _teams = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private IReadOnlyList<(string Method, string Pattern)> _teamRoutes = null!;
    private readonly Dictionary<string, TenantId> _personal = new(StringComparer.Ordinal);
    private string _team = null!;
    private Guid _report;
    private string _request = null!;
    private string _inviteToken = null!;

    public async Task InitializeAsync()
    {
        var ambient = new AsyncLocalTenantContext();
        _db = _harness.Open(ambient);
        _devices = new DeviceRegistry(_db, _harness.LegacyPath("devices.json"));
        _tenants = new TenantRegistry(_db);
        _teams = new TeamRegistry(_db, _tenants, () => Now);
        var access = new TeamAccess(_teams);
        var boundary = new HostedTenantBoundary(ambient, _devices, hosted: true);
        var directors = new DirectorRegistry(_harness.LegacyPath("instances"));
        var pushed = new PushedSessionStore();
        var ownership = new TeamCallerOwnership(directors, pushed, _devices, new SessionTurnStore(_db), boundary,
            new SessionKeyRegistry(_db, isHosted: true));
        var reports = new DevReportStore(_db);
        var recipients = new DevReportRecipients(_db);
        var comments = new DevReportPersonComments(_db);
        var requests = new TeamRequestStore(_db, _teams);
        var skills = new SkillStore(_db, deferInitialize: true);
        var workflows = new WorkflowStore(_db, deferInitialize: true);
        using (ambient.Enter(TenantId.System))
        {
            skills.Initialize();
            workflows.Initialize();
        }
        var gate = new TeamEndpointGate(access, _teams, _tenants, boundary, ownership);

        // Every member but the invitee has an account now; the invitee's is made after the invitation, so the invitation
        // waits for them rather than adding them at once.
        foreach (var subject in new[] { Owner, Manager, Developer, Collaborator, OtherCollaborator })
            _personal[subject] = _tenants.MintOrLookupBySubject(subject, Addresses[subject]);
        _team = _teams.CreateTeam(Owner, "Acme").Team!.TeamId;
        TeamBillSeed.Active(_db, _team, seats: 3);
        Assert.True(_teams.AddMember(_team, Manager, TeamRole.Manager).IsDone);
        Assert.True(_teams.AddMember(_team, Developer, TeamRole.Developer).IsDone);
        Assert.True(_teams.AddMember(_team, Collaborator, TeamRole.Collaborator).IsDone);
        Assert.True(_teams.AddMember(_team, OtherCollaborator, TeamRole.Collaborator).IsDone);

        // A report with a question, written by the Developer and sent to both Collaborators; the other Collaborator commented.
        var teamTenant = new TenantId(_team);
        var sid = Guid.NewGuid().ToString("D");
        _report = reports.Publish(teamTenant, sid, @"C:\work\pricing.html", TeamQuestionsTests.Html(QuestionId), "waiting-on-you",
            "Pricing", Now, Developer).Report.Id;
        Assert.True(recipients.Send(teamTenant, _report, Developer, new[] { Collaborator, OtherCollaborator }, 1, Now).Sent);
        comments.Add(teamTenant, _report, OtherCollaborator, Developer, "A comment from the other Collaborator", Now);

        // A request the Collaborator sent, which a Manager accepted.
        _request = requests.Send(_team, Collaborator, "Please add a dark theme").Request!.Id;
        Assert.Equal(TeamRequestOutcome.Done, requests.Decide(_team, _request, Manager, TeamRequestDecision.Accept, null).Outcome);

        // A Collaborator's invitation, sent by a Manager.
        _inviteToken = _teams.CreateInvitation(_team, Manager, Addresses[Invitee], TeamRole.Collaborator).AcceptToken!;
        _personal[Invitee] = _tenants.MintOrLookupBySubject(Invitee, Addresses[Invitee]);

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
            Requests: requests,
            Skills: skills, Workflows: workflows,
            MentorStore: new TeamMentorStore(_db), TimeZoneOf: _ => "UTC", MentorRunning: false,
            Reports: reports, Recipients: recipients, Comments: comments,
            // The session that wrote the report is busy, so an answer is held for it and nothing is typed anywhere.
            Delivery: new DevReportDelivery(reports, (_, _) => new DevReportSessionLiveness(DevReportSessionReach.Busy, "director-developer", "test roster"),
                (_, _) => throw new InvalidOperationException("no session is asked in this test"), CancellationToken.None),
            Factories: new CcDirector.Gateway.Factory.Registry.FactoryRegistryStore(_db),
            Activity: new CcDirector.Gateway.Factory.FactoryActivityRecord(_db),
            FactoryContributors: new FactoryContributors(_db),
            FactorySwitch: new CcDirector.Gateway.Factory.FactoryAgentsSwitch(machineWide: true, new CcDirector.Gateway.Settings.TenantSettingsStore(_db)),
            FactoryInbox: new CcDirector.Gateway.Factory.Inbox.FactoryInboxStore(_db, _ => CcDirector.Core.Dictation.Models.DictationDictionary.Empty),
            FactoryInboxMessageSent: (_, _, _) => { },
            Settings: new CcDirector.Gateway.Settings.TenantSettingsStore(_db)));

        _teamRoutes = ((IEndpointRouteBuilder)_app).DataSources
            .SelectMany(d => d.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => TeamPersonOnlyRoutes.InAFamily(e.RoutePattern.RawText))
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? new[] { "GET" })
                .Select(m => (m, TeamEndpointRules.Normalize(e.RoutePattern.RawText))))
            .Distinct()
            // Reads first, so every screen is read before a write (an accept, a removal) changes what the team holds.
            .OrderBy(r => TeamEndpointRule.HttpMethodIsRead(r.m) ? 0 : 1)
            .ThenBy(r => r.Item2, StringComparer.Ordinal).ThenBy(r => r.m, StringComparer.Ordinal)
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

    /// <summary>Stands in for the auth middleware: the header names the account subject, and the request carries that
    /// person's own browser key, exactly as the real middleware stamps it for a verified key.</summary>
    private void StandInForAuthentication(HttpContext ctx)
    {
        var subject = ctx.Request.Headers[CallerHeader].ToString();
        if (!_personal.TryGetValue(subject, out var tenant))
            return;
        ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] =
            new DeviceCredentialIdentity(subject + "-ns|browser-1", tenant.Value, "browser", "active", subject);
        ctx.Items[AuthMiddleware.DeviceTypeItemKey] = "browser";
    }

    /// <summary>A route's address with the team's REAL ids, so every route reaches what the team holds.</summary>
    private string Address(string pattern) =>
        Regex.Replace(pattern, @"\{\**([^}:=?]+)([^}]*)\}", m => m.Groups[1].Value switch
        {
            "teamId" => _team,
            "reportId" => _report.ToString("D"),
            "questionId" => QuestionId,
            "requestId" => _request,
            "memberId" => TeamMemberIds.For(_team, OtherCollaborator),
            var other when m.Groups[2].Value.Contains(":int", StringComparison.Ordinal) => "1",
            var other => "x-" + other,
        });

    /// <summary>One body every write route can read: each route takes the fields it knows and ignores the rest.</summary>
    private string Body() => JsonSerializer.Serialize(new
    {
        token = _inviteToken,
        version = 1,
        optionValue = "30",
        comment = "Thirty, please",
        text = "A request or a comment from the Collaborator",
        reason = "No reason",
        memberIds = Array.Empty<string>(),
        role = "Developer",
        email = "someone.new@example.com",
        name = "A new name",
        on = true,
    });

    private async Task<(HttpStatusCode Status, string Text)> Call(string caller, string method, string pattern)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), Address(pattern));
        request.Headers.Add(CallerHeader, caller);
        if (!TeamEndpointRule.HttpMethodIsRead(method))
            request.Content = new StringContent(Body(), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Every address in the team that is not <paramref name="caller"/>'s own, found anywhere in
    /// <paramref name="text"/>, in any case and in JSON's escaped form too.</summary>
    private static List<string> OthersAddressesIn(string caller, string text)
    {
        var plain = Regex.Unescape(text);
        return Addresses
            .Where(a => a.Key != caller)
            .Select(a => a.Value)
            .Where(address => plain.Contains(address, StringComparison.OrdinalIgnoreCase)
                              || plain.Contains(address.Split('@')[0], StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private async Task<(Dictionary<string, HttpStatusCode> Served, List<string> Leaks)> Walk(string caller)
    {
        var served = new Dictionary<string, HttpStatusCode>(StringComparer.Ordinal);
        var leaks = new List<string>();
        foreach (var (method, pattern) in _teamRoutes)
        {
            var (status, text) = await Call(caller, method, pattern);
            served[$"{method} {pattern}"] = status;
            foreach (var address in OthersAddressesIn(caller, text))
                leaks.Add($"{method} {pattern} -> {(int)status} carries {address}");
        }
        return (served, leaks);
    }

    // ---- The member list --------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetMembers_AsACollaborator_IsRefusedByTheGate()
    {
        var (status, text) = await Call(Collaborator, "GET", "/teams/{teamId}/members");

        Assert.Equal(HttpStatusCode.Forbidden, status);
        using var doc = JsonDocument.Parse(text);
        Assert.Equal(TeamEndpointGate.RefusalCode, doc.RootElement.GetProperty("code").GetString());
        Assert.Empty(OthersAddressesIn(Collaborator, text));
    }

    [Fact]
    public async Task GetMembers_AsTheOwner_ListsEveryMemberWithTheirAddress()
    {
        var (status, text) = await Call(Owner, "GET", "/teams/{teamId}/members");

        Assert.Equal(HttpStatusCode.OK, status);
        using var doc = JsonDocument.Parse(text);
        var emails = doc.RootElement.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("email").GetString()).ToList();
        Assert.Equal(new[] { Owner, Manager, Developer, Collaborator, OtherCollaborator }.Select(s => Addresses[s]).OrderBy(e => e),
            emails.OrderBy(e => e));
    }

    // ---- The walk ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryMappedTeamRoute_AsACollaborator_CarriesNoOtherMembersAddress()
    {
        var (served, leaks) = await Walk(Collaborator);

        Assert.True(leaks.Count == 0, "A Collaborator was shown another member's address:\n" + string.Join("\n", leaks));
        // The pass condition above is an absence, so the walk must be shown to have reached every screen a Collaborator has.
        foreach (var route in new[]
                 {
                     "GET /teams", "GET /teams/{teamId}/reports/sent-to-me", "GET /teams/{teamId}/reports/sent-to-me/{reportId}",
                     "GET /teams/{teamId}/reports/sent-to-me/{reportId}/html", "GET /teams/{teamId}/questions/",
                     "GET /teams/{teamId}/requests/mine", "POST /teams/{teamId}/requests",
                     "POST /teams/{teamId}/reports/sent-to-me/{reportId}/comments",
                     "POST /teams/{teamId}/questions/{reportId}/{questionId}/answer",
                 })
        {
            Assert.True(served.TryGetValue(route, out var status), $"{route} is not in the mapped route table.");
            Assert.True((int)status is >= 200 and < 300, $"{route} answered {(int)status} to the Collaborator, so the walk did not read it.");
        }
    }

    [Fact]
    public async Task EveryMappedTeamRoute_AsTheInvitedCollaborator_CarriesNoOtherMembersAddress()
    {
        var (open, _) = await Call(Invitee, "POST", "/team-invitations/open");
        Assert.Equal(HttpStatusCode.OK, open);

        var (served, leaks) = await Walk(Invitee);

        Assert.True(leaks.Count == 0, "The person invited as a Collaborator was shown another member's address:\n" + string.Join("\n", leaks));
        Assert.True((int)served["POST /team-invitations/accept"] is >= 200 and < 300,
            $"Accepting the invitation answered {(int)served["POST /team-invitations/accept"]}, so the walk did not accept it.");
        Assert.Equal(TeamRole.Collaborator, _teams.RoleOf(_team, Invitee));
    }

    // ---- What a Collaborator sees instead -------------------------------------------------------------------------

    [Fact]
    public async Task SentToMeDetail_AsACollaborator_NamesTheAuthorByRole()
    {
        var (status, text) = await Call(Collaborator, "GET", "/teams/{teamId}/reports/sent-to-me/{reportId}");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains(TeamRegistry.RoleName(TeamRole.Developer), Regex.Unescape(text));
    }

    [Fact]
    public async Task OpenInvitation_AsTheInvitedCollaborator_NamesTheInviterByRole_AndShowsTheirOwnAddress()
    {
        var (status, text) = await Call(Invitee, "POST", "/team-invitations/open");

        Assert.Equal(HttpStatusCode.OK, status);
        var plain = Regex.Unescape(text);
        Assert.Contains(TeamRegistry.RoleName(TeamRole.Manager), plain);
        Assert.Contains(Addresses[Invitee], plain);
    }

    [Fact]
    public void ListMembers_AsADeveloper_StillCarriesEveryAddress()
    {
        var members = _teams.ListMembers(_team, Developer).Members;

        Assert.All(members, m => Assert.Equal(Addresses[m.AccountSubject], m.Email));
    }

    [Fact]
    public void ListMembers_AsACollaborator_KeepsTheirOwnAddress_AndNamesEveryoneElseByRole()
    {
        var members = _teams.ListMembers(_team, Collaborator).Members;

        var own = Assert.Single(members, m => m.AccountSubject == Collaborator);
        Assert.Equal(Addresses[Collaborator], own.Email);
        Assert.All(members.Where(m => m.AccountSubject != Collaborator), m =>
        {
            Assert.Null(m.Email);
            Assert.Equal(TeamRegistry.RoleName(m.Role), m.Name);
        });
    }

    private sealed class NoMailer : ITeamInvitationMailer
    {
        public Task<TeamInvitationMailResult> SendAsync(string invitationId, string teamId, string acceptToken, CancellationToken ct = default) =>
            throw new InvalidOperationException("no invitation is sent in this test");

        public Task<TeamInvitationMailResult> SendAddedAsync(string invitationId, string teamId, string acceptToken, CancellationToken ct = default) =>
            throw new InvalidOperationException("no invitation is sent in this test");
    }
}
