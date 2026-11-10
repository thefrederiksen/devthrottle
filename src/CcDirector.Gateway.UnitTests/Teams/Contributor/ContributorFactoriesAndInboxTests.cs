using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.DevReports;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Registry;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Tests.Teams.Questions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams.Contributor;

/// <summary>
/// A CONTRIBUTOR'S FACTORIES AND INBOX (Team implementation piece 4.1, devthrottle_internal#2798), over a real database: the
/// one check (<see cref="ContributorFactoryAccess"/>), what each contributor route answers (<see cref="ContributorPages"/>),
/// the Owner's share of a factory (<see cref="FactoryShares"/>), and the team gate deciding the new routes.
///
/// Two teams, as design.html's reviewer is: Acme (Owner A; contributors Mike and Nina; factories "website" and "secret")
/// and Beta (Owner B; contributor Mike; factory "shop"). Mike is a contributor on both.
/// </summary>
public sealed class ContributorFactoriesAndInboxTests : IDisposable
{
    private const string OwnerA = "sub-owner-a";
    private const string OwnerB = "sub-owner-b";
    private const string Mike = "sub-mike";
    private const string Nina = "sub-nina";
    private const string Stranger = "sub-stranger";
    private const string Machine = "BUILDBOX7";
    private const string Folder = "/srv/zqfolder7/website";
    private const string SeatId = "web-desk";
    private const string SessionId = "5e551011-0000-4000-8000-00000000abcd";
    private static readonly DateTime Now = new(2026, 11, 9, 15, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly TenantRegistry _tenants;
    private readonly TeamRegistry _teams;
    private readonly TeamAccess _access;
    private readonly DevReportStore _store;
    private readonly DevReportRecipients _recipients;
    private readonly FactoryRegistryStore _factories;
    private readonly FactoryActivityRecord _activity;
    private readonly FactoryContributors _shares;
    private readonly ContributorFactoryAccess _check;
    private readonly ContributorPages _pages;
    private readonly FactoryShares _owner;
    private readonly CcDirector.Gateway.Factory.FactoryAgentsSwitch _factorySwitch;
    private readonly TeamRequestStore _requests;
    private readonly TeamQuestions _questions;
    private readonly TeamEndpointGate _gate;
    private readonly string _acme;
    private readonly string _beta;

    public ContributorFactoriesAndInboxTests()
    {
        _db = _harness.Open();
        _tenants = new TenantRegistry(_db);
        foreach (var (subject, email) in new[]
                 {
                     (OwnerA, "a@example.com"), (OwnerB, "b@example.com"), (Mike, "mike@example.com"),
                     (Nina, "nina@example.com"), (Stranger, "stranger@example.com"),
                 })
            _tenants.MintOrLookupBySubject(subject, email);
        _teams = new TeamRegistry(_db, _tenants);
        _access = new TeamAccess(_teams);
        _store = new DevReportStore(_db);
        _recipients = new DevReportRecipients(_db);
        var comments = new DevReportPersonComments(_db);
        _factories = new FactoryRegistryStore(_db);
        _activity = new FactoryActivityRecord(_db);
        _shares = new FactoryContributors(_db);
        _check = new ContributorFactoryAccess(_teams, _factories, _shares);
        var delivery = new DevReportDelivery(_store,
            (_, _) => new DevReportSessionLiveness(DevReportSessionReach.Busy, null, "no session in these tests"),
            (_, _) => null, CancellationToken.None, () => Now);
        var questions = _questions = new TeamQuestions(_store, _recipients, comments, _teams, _access, delivery, () => Now);
        _requests = new TeamRequestStore(_db, _teams);
        _pages = new ContributorPages(_check, _shares, _activity, _store, _recipients, questions, _requests, _teams,
            new ContributorWatchExplainer(new CcDirector.Gateway.Settings.TenantSettingsStore(_db), _tenants, () => Now), _ => "UTC", () => Now);
        // The factory area is off unless switched on per account, as on a Gateway that does not grant it to every one.
        _factorySwitch = new CcDirector.Gateway.Factory.FactoryAgentsSwitch(machineWide: false,
            new CcDirector.Gateway.Settings.TenantSettingsStore(_db), log: _ => { });
        _owner = new FactoryShares(_teams, _factories, _shares, _factorySwitch, () => Now);
        var devices = new DeviceRegistry(_db, _harness.LegacyPath("devices.json"), isHosted: true, teamsReleased: true);
        var boundary = new HostedTenantBoundary(new AsyncLocalTenantContext(), devices, hosted: false);
        _gate = new TeamEndpointGate(_access, _teams, _tenants, boundary);

        _acme = _teams.CreateTeam(OwnerA, "Acme").Team!.TeamId;
        _beta = _teams.CreateTeam(OwnerB, "Beta").Team!.TeamId;
        _factorySwitch.Set(new TenantId(_acme), true, "the administrator (test)", "factories for Acme", Now);
        _factorySwitch.Set(new TenantId(_beta), true, "the administrator (test)", "factories for Beta", Now);
        Assert.True(_teams.AddMember(_acme, Mike, TeamRole.Collaborator).IsDone);
        Assert.True(_teams.AddMember(_acme, Nina, TeamRole.Collaborator).IsDone);
        Assert.True(_teams.AddMember(_beta, Mike, TeamRole.Collaborator).IsDone);
        Register(_acme, "website", "Erik's website");
        Register(_acme, "secret", "Internal tooling");
        Register(_beta, "shop", "Beta shop");
    }

    public void Dispose() => _harness.Dispose();

    private void Register(string team, string factory, string title) =>
        _factories.Register(new TenantId(team), new RegisterFactoryRequest
        {
            Factory = factory, Title = title, Folder = Folder, Computer = Machine, BossSeat = "boss",
            Seats =
            {
                new FactorySeatManifest { Id = "boss", Name = "Boss", Role = "Boss", BriefFile = "agents/boss.yaml" },
                new FactorySeatManifest { Id = SeatId, Name = "Web Desk", Role = "Builder", BriefFile = "agents/web.yaml" },
            },
        }, "the owner (test)", Now);

    private void Share(string team, string factory, string contributor) =>
        Assert.True(_shares.Share(new TenantId(team), factory, contributor, team == _acme ? OwnerA : OwnerB, Now));

    private void Activity(string team, string factory, string? forContributor, DateTime at) =>
        _activity.Append(new TenantId(team), new AppendFactoryActivityRequest
        {
            Factory = factory, FactoryAgent = SeatId, SessionId = SessionId, Outcome = FactoryActivityOutcome.Done,
            What = $"{SeatId} rebuilt the menu on {Machine} in {Folder} with claude-opus", Link = $"file:///{Folder}/out.html",
            Actor = SeatId, OccurredUtc = at, ForContributor = forContributor,
        }, SeatId);

    /// <summary>A report published by the factory's seat in the team and sent to <paramref name="to"/>.</summary>
    private Guid Report(string team, string? factory, string title, bool withQuestion, params string[] to)
    {
        var html = withQuestion ? TeamQuestionsTests.Html() : NoQuestions;
        var report = _store.Publish(new TenantId(team), SessionId, @"C:\work\" + Guid.NewGuid().ToString("N") + ".html", html,
            "waiting-on-you", title, Now, team == _acme ? OwnerA : OwnerB,
            factory is null ? null : new DevReportFactorySeatIdentity(factory, SeatId)).Report;
        Assert.True(_recipients.Send(new TenantId(team), report.Id, team == _acme ? OwnerA : OwnerB, to, 1, Now).Sent);
        return report.Id;
    }

    /// <summary>A report in the shape the publish route accepts, with no questions in it.</summary>
    private const string NoQuestions =
        "<header data-dev-report=\"header\" data-dev-report-status=\"done\"><h1>T</h1></header>" +
        "<section data-dev-report=\"summary\"><p>S</p></section>" +
        "<section data-dev-report=\"questions\"><p data-dev-report-no-questions>No questions.</p></section>" +
        "<section data-dev-report=\"detail\"><p>D</p></section>";

    /// <summary>A report with two questions for contributors, "first" then "second", in that order.</summary>
    private static string TwoQuestions() =>
        "<header data-dev-report=\"header\" data-dev-report-status=\"agent-working\"><h1>Two</h1></header>" +
        "<section data-dev-report=\"summary\"><p>S</p></section><section data-dev-report=\"questions\">" +
        Question("first", "Which colour?") + Question("second", "Which font?") +
        "</section><section data-dev-report=\"detail\"><p>D</p></section>";

    private static string Question(string id, string text) =>
        $"<div data-dev-report-question=\"{id}\" data-dev-report-question-text=\"{text}\" data-dev-report-for=\"contributors\">" +
        $"<label><input type=\"radio\" name=\"{id}\" value=\"14\" data-recommended> 14 days</label>" +
        $"<label><input type=\"radio\" name=\"{id}\" value=\"30\"> 30 days</label>" +
        "<textarea data-dev-report-comment placeholder=\"Anything to add (optional)\"></textarea></div>";

    private TenantId PersonalTenant(string subject) => _tenants.MintOrLookupBySubject(subject, null);

    private static JsonElement Json(object answer) => JsonSerializer.SerializeToElement(answer);

    private static async Task<(int Status, JsonElement Body)> Render(IResult result)
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        ctx.Response.Body = new MemoryStream();
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        var text = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        return (ctx.Response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    // ---------------------------------------------------------------- the one check

    [Fact]
    public void Check_SentByNameToAContributorOfALiveTeam_IsAllowed()
    {
        Share(_acme, "website", Mike);

        var verdict = _check.Check(_acme, Mike, "website");

        Assert.True(verdict.Allowed, verdict.Refusal?.ToString());
        Assert.Equal("website", verdict.Factory!.Factory);
        Assert.Equal("Acme", verdict.TeamName);
    }

    [Fact]
    public void Check_EveryWrongCase_IsRefusedWithItsOwnReason()
    {
        // Arrange: website is sent to Mike in Acme; shop is sent to Mike in Beta; nothing is sent to Nina.
        Share(_acme, "website", Mike);
        Share(_beta, "shop", Mike);
        Assert.True(_teams.AddMember(_beta, Nina, TeamRole.Collaborator).IsDone);
        Share(_beta, "shop", Nina);
        Assert.True(_teams.RemoveMember(_beta, Nina).IsDone);
        Assert.True(_teams.AddMember(_acme, Stranger, TeamRole.Developer).IsDone);
        Share(_acme, "website", Stranger);

        var walk = new (string Team, string Person, string Factory, ContributorFactoryRefusal Why)[]
        {
            (_acme, Mike, "secret", ContributorFactoryRefusal.NotSentToThem),            // not sent by name
            (_acme, Nina, "website", ContributorFactoryRefusal.NotSentToThem),           // another contributor's
            (_beta, Nina, "shop", ContributorFactoryRefusal.NotAMember),                 // removed from the team
            (_acme, OwnerB, "website", ContributorFactoryRefusal.NotAMember),            // never a member
            (_acme, Mike, "shop", ContributorFactoryRefusal.NoSuchFactory),              // the other team's factory
            (_acme, Stranger, "website", ContributorFactoryRefusal.NotAContributor),     // a member, not a contributor
            (_acme, OwnerA, "website", ContributorFactoryRefusal.NotAContributor),       // the Owner
            ("3f1d2c9e-0000-4000-8000-00000000dead", Mike, "website", ContributorFactoryRefusal.NoSuchTeam),
            (_acme, Mike, "no-such-factory", ContributorFactoryRefusal.NoSuchFactory),
        };

        foreach (var (team, person, factory, why) in walk)
        {
            var verdict = _check.Check(team, person, factory);
            Assert.False(verdict.Allowed, $"{person} on {factory} should be refused ({why})");
            Assert.Equal(why, verdict.Refusal);
            Assert.Null(verdict.Factory);
        }
    }

    [Fact]
    public void Check_ArchivedFactory_IsRefused()
    {
        Share(_acme, "website", Mike);
        _factories.Archive(new TenantId(_acme), "website", "owner (test)", Array.Empty<string>(), Now);

        var verdict = _check.Check(_acme, Mike, "website");

        Assert.False(verdict.Allowed);
        Assert.Equal(ContributorFactoryRefusal.Archived, verdict.Refusal);
    }

    // ---------------------------------------------------------------- combined across two teams

    [Fact]
    public void FactoriesAndInbox_AContributorOnTwoTeams_SeesBothLabelledAndNothingElse()
    {
        // Arrange: Mike is sent one factory and one report in each team. Nina is sent her own report in Acme; Acme's
        // "secret" factory and a report about it exist but are not sent to Mike.
        Share(_acme, "website", Mike);
        Share(_beta, "shop", Mike);
        Report(_acme, "website", "Menu page: which layout?", withQuestion: true, Mike);
        Report(_beta, "shop", "Shop home page draft", withQuestion: false, Mike);
        Report(_acme, "website", "Nina's review", withQuestion: false, Nina);
        Report(_acme, "secret", "Deploy to production?", withQuestion: false, Nina);
        Assert.Equal(TeamRequestOutcome.Done, _requests.Send(_acme, Mike, "Could the menu show prices?").Outcome);

        // Act: what the client asks, team by team.
        var factories = new[] { Json(_pages.Factories(_acme, Mike)), Json(_pages.Factories(_beta, Mike)) }
            .SelectMany(a => a.GetProperty("factories").EnumerateArray()).ToList();
        var inbox = new[] { Json(_pages.Inbox(_acme, Mike)), Json(_pages.Inbox(_beta, Mike)) }
            .SelectMany(a => a.GetProperty("items").EnumerateArray()).ToList();

        // Assert: both teams' factories, each labelled, and no other.
        Assert.Equal(new[] { ("Acme", "Erik's website"), ("Beta", "Beta shop") },
            factories.Select(f => (f.GetProperty("teamLabel").GetString()!, f.GetProperty("name").GetString()!)).OrderBy(x => x.Item1));
        Assert.Equal("1 thing needs you", factories.Single(f => f.GetProperty("factory").GetString() == "website").GetProperty("needsYouLabel").GetString());
        // Both teams' reports and his own message, each labelled - never Nina's, never one not sent to him.
        Assert.Equal(new[] { ("Acme", "Menu page: which layout?"), ("Acme", "You wrote to the team's Owner"), ("Beta", "Shop home page draft") },
            inbox.Select(i => (i.GetProperty("teamLabel").GetString()!, i.GetProperty("title").GetString()!)).OrderBy(x => x.Item1).ThenBy(x => x.Item2));
        Assert.Equal("Waiting on you", inbox.Single(i => i.GetProperty("title").GetString() == "Menu page: which layout?").GetProperty("stateLabel").GetString());
        // The combined order is the Gateway's key: what waits first.
        var ordered = inbox.OrderBy(i => i.GetProperty("order").GetString(), StringComparer.Ordinal).ToList();
        Assert.Equal("Menu page: which layout?", ordered[0].GetProperty("title").GetString());
    }

    [Fact]
    public void Factories_CarriesTheLineUnderThePagesTitle()
    {
        Assert.Equal(ContributorPages.FactoriesIntro, Json(_pages.Factories(_acme, Mike)).GetProperty("intro").GetString());
    }

    [Fact]
    public async Task Inbox_AReportCarriesItsQuestionsForThem_WaitingFirst_ThenAnswered_AndAMessageNone()
    {
        // Arrange: a report with two questions, "first" then "second"; Mike answers "first".
        var report = _store.Publish(new TenantId(_acme), SessionId, @"C:\work\two.html", TwoQuestions(), "agent-working",
            "Two questions", Now, OwnerA, new DevReportFactorySeatIdentity("website", SeatId)).Report;
        Assert.True(_recipients.Send(new TenantId(_acme), report.Id, OwnerA, new[] { Mike }, 1, Now).Sent);
        Assert.Equal(TeamRequestOutcome.Done, _requests.Send(_acme, Mike, "A message").Outcome);
        var (status, _) = await Render(await _questions.AnswerAsync(_acme, Mike, report.Id.ToString("D"), "first", 1, "14", "",
            "device", CancellationToken.None));
        Assert.Equal(200, status);

        // Act
        var items = Json(_pages.Inbox(_acme, Mike)).GetProperty("items").EnumerateArray().ToList();

        // Assert: the Gateway picks this report's questions and orders them - what waits on him, then what he answered.
        var asked = items.Single(i => i.GetProperty("kind").GetString() == "report").GetProperty("questions").EnumerateArray().ToList();
        Assert.Equal(new[] { ("second", true), ("first", false) },
            asked.Select(q => (q.GetProperty("questionId").GetString()!, q.GetProperty("canAnswer").GetBoolean())));
        Assert.All(asked, q => Assert.Equal(report.Id.ToString("D"), q.GetProperty("reportId").GetString()));
        Assert.Empty(items.Single(i => i.GetProperty("kind").GetString() == "message").GetProperty("questions").EnumerateArray());
        // A contributor reads no word about agents in what the card says about their words.
        Assert.DoesNotContain("agent", asked[0].GetProperty("commentNote").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FactoriesAndInbox_AfterLeavingATeam_ShowNothingFromIt()
    {
        Share(_beta, "shop", Mike);
        Report(_beta, "shop", "Shop home page draft", withQuestion: false, Mike);
        Assert.Single(Json(_pages.Factories(_beta, Mike)).GetProperty("factories").EnumerateArray());

        Assert.True(_teams.RemoveMember(_beta, Mike).IsDone);

        // The gate answers "no such team" for a team the person left; and the check refuses the factory itself.
        var gate = _gate.Check("GET", ContributorEndpoints.FactoriesPattern, n => n == "teamId" ? _beta : null,
            PersonalTenant(Mike), () => Mike, _ => TeamOwnership.Callers);
        Assert.Equal(TeamGateOutcome.NoSuchTeam, gate.Outcome);
        Assert.False(_check.Check(_beta, Mike, "shop").Allowed);
    }

    [Fact]
    public void RemoveMember_AContributorAddedBack_SeesNoFactorySentBeforeTheRemoval()
    {
        Share(_acme, "website", Mike);
        Share(_acme, "website", Nina);
        Assert.True(_check.Check(_acme, Mike, "website").Allowed);

        Assert.True(_teams.RemoveMember(_acme, Mike, OwnerA).IsDone);
        Assert.True(_teams.AddMember(_acme, Mike, TeamRole.Collaborator, OwnerA).IsDone);

        // A person added back starts empty (security review M4): no old factory comes back without the Owner sharing it.
        var verdict = _check.Check(_acme, Mike, "website");
        Assert.False(verdict.Allowed);
        Assert.Equal(ContributorFactoryRefusal.NotSentToThem, verdict.Refusal);
        var page = Json(_pages.Factories(_acme, Mike));
        Assert.Equal(0, page.GetProperty("count").GetInt32());
        Assert.Empty(page.GetProperty("factories").EnumerateArray());
        Assert.Null(_shares.Find(new TenantId(_acme), "website", Mike));
        // Only the removed person's shares go; another contributor of the same factory keeps theirs.
        Assert.True(_check.Check(_acme, Nina, "website").Allowed);
    }

    // ---------------------------------------------------------------- the customer page

    [Fact]
    public void CustomerPage_ShowsTheContributorWordsOfToday_AndTheReportsSentToThem()
    {
        Share(_acme, "website", Mike);
        Activity(_acme, "website", "Version 23 of your website is live.", Now.AddHours(-1));
        Activity(_acme, "website", null, Now.AddMinutes(-30));
        Activity(_acme, "website", "We started on the menu page yesterday.", Now.AddDays(-1));
        Report(_acme, "website", "Menu page: which layout?", withQuestion: true, Mike);

        var page = Json(_pages.CustomerPage(_acme, Mike, "website")!);

        Assert.Equal("Erik's website", page.GetProperty("name").GetString());
        Assert.Equal("Acme", page.GetProperty("teamLabel").GetString());
        Assert.Equal(new[] { "Version 23 of your website is live." },
            page.GetProperty("today").GetProperty("items").EnumerateArray().Select(i => i.GetProperty("text").GetString()));
        Assert.Equal("14:00", page.GetProperty("today").GetProperty("items")[0].GetProperty("timeLabel").GetString());
        Assert.Equal("Menu page: which layout?", page.GetProperty("needsYou").GetProperty("items")[0].GetProperty("title").GetString());
        Assert.Equal("Waiting on you", page.GetProperty("sentToYou").GetProperty("items")[0].GetProperty("stateLabel").GetString());
    }

    [Fact]
    public void CustomerPage_NothingInternalReachesTheContributor()
    {
        // Arrange: every internal thing a factory has - machine, folder, seats, session, model, internal steps - planted.
        Share(_acme, "website", Mike);
        Activity(_acme, "website", "Your menu page is ready to look at.", Now.AddMinutes(-5));
        Report(_acme, "website", "Menu page: which layout?", withQuestion: true, Mike);

        // Act: every answer a contributor's screens read.
        var answers = new[]
        {
            JsonSerializer.Serialize(_pages.CustomerPage(_acme, Mike, "website")!),
            JsonSerializer.Serialize(_pages.Factories(_acme, Mike)),
            JsonSerializer.Serialize(_pages.Inbox(_acme, Mike)),
        };

        // Assert: no field names one, and no value holds one.
        foreach (var text in answers)
        {
            foreach (var name in FieldNames(JsonDocument.Parse(text).RootElement))
                Assert.DoesNotMatch("(?i)session|machine|computer|seat|model|agent|folder|boss|^actor$|^link$|^what$", name);
            foreach (var secret in new[] { Machine, "zqfolder7", SeatId, "Web Desk", SessionId, "claude", "rebuilt the menu", OwnerA, Mike, "a@example.com" })
                Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static IEnumerable<string> FieldNames(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject())
            {
                yield return p.Name;
                foreach (var n in FieldNames(p.Value)) yield return n;
            }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var item in e.EnumerateArray())
                foreach (var n in FieldNames(item)) yield return n;
    }

    [Fact]
    public void CustomerPage_UnknownFactoryAndARealOneNotSentToThem_AnswerTheSame()
    {
        // The refusal question carried from piece 1.1 (note N3): a contributor-facing refusal must not tell a real
        // factory from a made-up name. Both are null here, which the route answers with one 404 and one sentence.
        Share(_acme, "website", Mike);

        Assert.Null(_pages.CustomerPage(_acme, Mike, "secret"));
        Assert.Null(_pages.CustomerPage(_acme, Mike, "no-such-factory"));
        Assert.Null(_pages.CustomerPage(_acme, Nina, "website"));
    }

    // ---------------------------------------------------------------- the Owner's share

    [Fact]
    public async Task Share_TheOwnerSendsAFactoryToAContributor_PutsItOnTheirPage()
    {
        var mikeId = TeamMemberIds.For(_acme, Mike);
        var (before, choices) = await Render(_owner.Describe(_acme, OwnerA, "website"));
        Assert.Equal(200, before);
        Assert.Contains(choices.GetProperty("choices").EnumerateArray(), c => c.GetProperty("memberId").GetString() == mikeId);

        var (status, body) = await Render(_owner.Share(_acme, OwnerA, "website", mikeId));

        Assert.Equal(200, status);
        Assert.Equal(mikeId, body.GetProperty("sentTo")[0].GetProperty("memberId").GetString());
        Assert.True(_check.Check(_acme, Mike, "website").Allowed);
    }

    [Fact]
    public async Task Share_ToAMemberWhoIsNotAContributor_IsRefusedAndRecordsNothing()
    {
        Assert.True(_teams.AddMember(_acme, Stranger, TeamRole.Developer).IsDone);

        var (status, body) = await Render(_owner.Share(_acme, OwnerA, "website", TeamMemberIds.For(_acme, Stranger)));

        Assert.Equal(400, status);
        Assert.Equal(FactoryShareEndpoints.NotAContributor, body.GetProperty("error").GetString());
        Assert.Empty(_shares.ContributorsOf(new TenantId(_acme), "website"));
    }

    [Fact]
    public async Task Share_AnotherTeamsFactoryOrAnArchivedOne_IsRefused()
    {
        var mikeId = TeamMemberIds.For(_acme, Mike);
        _factories.Archive(new TenantId(_acme), "secret", "owner (test)", Array.Empty<string>(), Now);

        Assert.Equal(404, (await Render(_owner.Share(_acme, OwnerA, "shop", mikeId))).Status);
        Assert.Equal(409, (await Render(_owner.Share(_acme, OwnerA, "secret", mikeId))).Status);
        Assert.Empty(_shares.SentTo(new TenantId(_acme), Mike));
    }

    [Fact]
    public async Task ShareRoutes_TheTeamsFactoryAreaIsOff_AnswerNotFoundAndRecordNothing()
    {
        var mikeId = TeamMemberIds.For(_acme, Mike);
        Share(_acme, "website", Mike);
        _factorySwitch.Set(new TenantId(_acme), false, "the administrator (test)", "factories off for Acme", Now);

        var (shareStatus, shareBody) = await Render(_owner.Share(_acme, OwnerA, "secret", mikeId));
        var (describeStatus, _) = await Render(_owner.Describe(_acme, OwnerA, "website"));
        var (unshareStatus, _) = await Render(_owner.Unshare(_acme, OwnerA, "website", mikeId));

        // The same answer every other factory route gives an account the area is off for, and nothing changes.
        Assert.Equal((404, 404, 404), (shareStatus, describeStatus, unshareStatus));
        Assert.Contains("switched off for this account, so nothing was done", shareBody.GetProperty("error").GetString());
        Assert.Null(_shares.Find(new TenantId(_acme), "secret", Mike));
        Assert.NotNull(_shares.Find(new TenantId(_acme), "website", Mike));
    }

    [Fact]
    public async Task Unshare_TakesTheFactoryOffTheContributorsPage()
    {
        Share(_acme, "website", Mike);

        var (status, _) = await Render(_owner.Unshare(_acme, OwnerA, "website", TeamMemberIds.For(_acme, Mike)));

        Assert.Equal(200, status);
        Assert.False(_check.Check(_acme, Mike, "website").Allowed);
        Assert.Equal(404, (await Render(_owner.Unshare(_acme, OwnerA, "website", TeamMemberIds.For(_acme, Mike)))).Status);
    }

    // ---------------------------------------------------------------- the team gate

    [Theory]
    [InlineData("GET", ContributorEndpoints.FactoriesPattern)]
    [InlineData("GET", ContributorEndpoints.FactoriesPattern + "/{factory}")]
    [InlineData("GET", ContributorEndpoints.InboxPattern)]
    public void Gate_TheContributorRoutes_AreAllowedForAContributor(string method, string pattern)
    {
        var verdict = GateFor(method, pattern, _acme, Mike);

        Assert.Equal(TeamGateOutcome.Allowed, verdict.Outcome);
    }

    [Theory]
    [InlineData("POST", ContributorEndpoints.FactoriesPattern)]
    [InlineData("DELETE", ContributorEndpoints.InboxPattern)]
    public void Gate_AWriteToAContributorRoute_IsRefusedAsUndeclared(string method, string pattern)
    {
        Assert.Equal(TeamGateOutcome.Refused, GateFor(method, pattern, _acme, Mike).Outcome);
    }

    [Theory]
    [InlineData("GET", FactoryShareEndpoints.Pattern)]
    [InlineData("POST", FactoryShareEndpoints.Pattern)]
    [InlineData("DELETE", FactoryShareEndpoints.Pattern + "/{memberId}")]
    public void Gate_TheShareRoutes_AreTheOwnersAndRefusedToAContributor(string method, string pattern)
    {
        Assert.Equal(TeamGateOutcome.Allowed, GateFor(method, pattern, _acme, OwnerA).Outcome);
        Assert.Equal(TeamGateOutcome.Refused, GateFor(method, pattern, _acme, Mike).Outcome);
    }

    [Theory]
    [InlineData("GET", "/teams/3f1d2c9e-0000-4000-8000-000000000001/contributor/factories")]
    [InlineData("GET", "/teams/3f1d2c9e-0000-4000-8000-000000000001/contributor/inbox")]
    [InlineData("POST", "/teams/3f1d2c9e-0000-4000-8000-000000000001/factories/website/contributors")]
    public void SessionKeyGuard_TheNewRoutes_AreRefusedToAnAgentSessionKey(string method, string path)
    {
        // A person's own phone or browser only: a session key, raised or not, never reads a contributor's pages or
        // shares a factory.
        Assert.False(CcDirector.Gateway.Util.SessionKeyGuard.Check(method, path).Allowed);
        Assert.False(CcDirector.Gateway.Util.SessionKeyGuard.Check(method, path, raised: true).Allowed);
    }

    private TeamGateVerdict GateFor(string method, string pattern, string team, string person) =>
        _gate.Check(method, pattern, n => n == "teamId" ? team : null, PersonalTenant(person), () => person,
            _ => TeamOwnership.Callers);
}
