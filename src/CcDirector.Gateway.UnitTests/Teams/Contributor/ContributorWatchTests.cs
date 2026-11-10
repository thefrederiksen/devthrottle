using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.DevReports;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Registry;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams.Contributor;

/// <summary>
/// "WATCH YOUR FACTORY" FOR A CONTRIBUTOR (version one, piece 4): the route's handler
/// (<see cref="ContributorEndpoints.Watch"/>) over <see cref="ContributorPages.Watch"/>, on the in-memory test database.
///
/// Two teams: Acme (Owner A; contributors Mike and Nina; a developer, Dev; factories "website" and "secret") and Beta
/// (Owner B; contributor Mike; a factory also called "website"). Each test proves one rule, and each is red when that rule
/// is removed.
/// </summary>
public sealed class ContributorWatchTests : IDisposable
{
    private const string OwnerA = "sub-owner-a";
    private const string OwnerB = "sub-owner-b";
    private const string Mike = "sub-mike";
    private const string Nina = "sub-nina";
    private const string Dev = "sub-dev";
    private const string TeamMachine = "BUILDBOX7";
    private const string PersonalMachine = "MIKESLAPTOP9";
    private const string Folder = "/srv/zqfolder7/website";
    private const string SeatId = "web-desk";
    private const string SessionId = "5e551011-0000-4000-8000-00000000abcd";
    private const string Sentence = "The menu page is ready for you to look at.";
    private const string NoSuchTeam = "3f1d2c9e-0000-4000-8000-00000000dead";
    private static readonly DateTime Now = new(2026, 11, 9, 15, 0, 0, DateTimeKind.Utc);

    /// <summary>The customer page's refusal, byte for byte - what every refused watch must answer too.</summary>
    private const string RefusalBody = "{\"error\":\"There is no factory sent to you with that name.\",\"code\":\"factory_not_found\"}";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly TenantRegistry _tenants;
    private readonly TeamRegistry _teams;
    private readonly FactoryRegistryStore _factories;
    private readonly FactoryActivityRecord _activity;
    private readonly FactoryContributors _shares;
    private readonly TenantSettingsStore _settings;
    private DateTime _now = Now;
    private readonly ContributorPages _pages;
    private readonly string _acme;
    private readonly string _beta;

    public ContributorWatchTests()
    {
        _db = _harness.Open();
        _tenants = new TenantRegistry(_db);
        foreach (var subject in new[] { OwnerA, OwnerB, Mike, Nina, Dev })
            _tenants.MintOrLookupBySubject(subject, subject + "@example.com");
        _teams = new TeamRegistry(_db, _tenants);
        var access = new TeamAccess(_teams);
        var store = new DevReportStore(_db);
        var recipients = new DevReportRecipients(_db);
        _factories = new FactoryRegistryStore(_db);
        _activity = new FactoryActivityRecord(_db);
        _shares = new FactoryContributors(_db);
        _settings = new TenantSettingsStore(_db);
        var delivery = new DevReportDelivery(store,
            (_, _) => new DevReportSessionLiveness(DevReportSessionReach.Busy, null, "no session in these tests"),
            (_, _) => null, CancellationToken.None, () => Now);
        var questions = new TeamQuestions(store, recipients, new DevReportPersonComments(_db), _teams, access, delivery, () => Now);
        _pages = new ContributorPages(new ContributorFactoryAccess(_teams, _factories, _shares), _shares, _activity, store, recipients,
            questions, new TeamRequestStore(_db, _teams), _teams, new ContributorWatchExplainer(_settings, _tenants, () => _now), _ => "UTC", () => Now);

        _acme = _teams.CreateTeam(OwnerA, "Acme").Team!.TeamId;
        _beta = _teams.CreateTeam(OwnerB, "Beta").Team!.TeamId;
        Assert.True(_teams.AddMember(_acme, Mike, TeamRole.Collaborator).IsDone);
        Assert.True(_teams.AddMember(_acme, Nina, TeamRole.Collaborator).IsDone);
        Assert.True(_teams.AddMember(_acme, Dev, TeamRole.Developer).IsDone);
        Assert.True(_teams.AddMember(_beta, Mike, TeamRole.Collaborator).IsDone);
        Register(_acme, "website", "Erik's website");
        Register(_acme, "secret", "Internal tooling");
        Register(_beta, "website", "Beta's website");
        Activity(_acme, "website", Sentence, Now.AddHours(-1));
        Activity(_beta, "website", Sentence, Now.AddHours(-1));
    }

    public void Dispose() => _harness.Dispose();

    // ---------------------------------------------------------------- arrange helpers

    private void Register(string team, string factory, string title, string workerName = "Web Desk") =>
        _factories.Register(new TenantId(team), new RegisterFactoryRequest
        {
            Factory = factory, Title = title, Folder = Folder, Computer = TeamMachine, BossSeat = "boss",
            Seats =
            {
                new FactorySeatManifest { Id = "boss", Name = "Boss", Role = "Boss", BriefFile = "agents/boss.yaml" },
                new FactorySeatManifest { Id = SeatId, Name = workerName, Role = "Builder", BriefFile = "agents/web.yaml" },
            },
        }, "the owner (test)", Now);

    private void Share(string team, string factory, string contributor) =>
        Assert.True(_shares.Share(new TenantId(team), factory, contributor, team == _acme ? OwnerA : OwnerB, Now));

    private void Activity(string team, string factory, string? forContributor, DateTime at) =>
        _activity.Append(new TenantId(team), new AppendFactoryActivityRequest
        {
            Factory = factory, FactoryAgent = SeatId, SessionId = SessionId, Outcome = FactoryActivityOutcome.Done,
            What = $"{SeatId} rebuilt the menu on {TeamMachine} in {Folder} with claude-opus", Link = $"file:///{Folder}/out.html",
            Actor = SeatId, OccurredUtc = at, ForContributor = forContributor,
        }, SeatId);

    private void SeedMachine(string machineName, string tenantId)
    {
        using var ctx = _harness.Open().CreateUnscopedContext();
        ctx.DeviceCredentials.Add(new CcDirector.Gateway.Data.Entities.DeviceCredentialEntity
        {
            DeviceId = Guid.NewGuid().ToString(), MachineName = machineName, DeviceKeyHash = Guid.NewGuid().ToString("N"),
            KeyPrefix = "dt_", KeyLast4 = "abcd", IssuedAtUtc = Now.AddDays(-7), Status = "active", Platform = "windows",
            DeviceType = "workstation", TenantId = tenantId,
        });
        ctx.SaveChanges();
    }

    /// <summary>What the route answers, as the bytes a browser receives.</summary>
    private async Task<(int Status, string Body)> Get(string team, string caller, string factory, string? window = null)
    {
        var result = ContributorEndpoints.Watch(_pages, team, caller, factory, window, null, null);
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        ctx.Response.Body = new MemoryStream();
        await result.ExecuteAsync(ctx);
        return (ctx.Response.StatusCode, Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray()));
    }

    /// <summary>What the explainer-seen route answers, as the bytes a browser receives.</summary>
    private async Task<(int Status, string Body)> Seen(string team, string caller, string factory)
    {
        var result = ContributorEndpoints.ExplainerSeen(_pages, team, caller, factory);
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        ctx.Response.Body = new MemoryStream();
        await result.ExecuteAsync(ctx);
        return (ctx.Response.StatusCode, Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray()));
    }

    private static JsonElement Explainer(string body) => JsonDocument.Parse(body).RootElement.GetProperty("explainer").Clone();

    private static List<JsonElement> Steps(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("steps").EnumerateArray().Select(s => s.Clone()).ToList();

    // ---------------------------------------------------------------- allowed

    [Fact]
    public async Task Watch_AFactorySentToThem_AnswersItsWatchView()
    {
        Share(_acme, "website", Mike);

        var (status, body) = await Get(_acme, Mike, "website");

        Assert.Equal(200, status);
        var root = JsonDocument.Parse(body).RootElement;
        Assert.Equal("Erik's website", root.GetProperty("title").GetString());
        Assert.Equal(2, root.GetProperty("stations").GetArrayLength());
        Assert.Single(Steps(body));
    }

    // ---------------------------------------------------------------- refused, identically

    [Fact]
    public async Task Watch_EveryRefusal_AnswersTheCustomerPagesNotFound_ByteForByte()
    {
        // Arrange: website is sent to Mike in BETA only, and to Nina in Acme; Acme's own website and secret are not sent
        // to Mike. Archived: a factory sent to Mike, then archived. Removed: Nina, after her factory was sent to her.
        Share(_beta, "website", Mike);
        Register(_acme, "old", "Old site");
        Share(_acme, "old", Mike);
        _factories.Archive(new TenantId(_acme), "old", "the owner (test)", Array.Empty<string>(), Now);
        Share(_acme, "secret", Nina);
        Share(_acme, "secret", Dev);

        var cases = new (string Why, Func<Task<(int, string)>> Ask)[]
        {
            ("not sent to them", () => Get(_acme, Mike, "secret")),
            ("another team's factory of the same name", () => Get(_acme, Mike, "website")),
            ("archived", () => Get(_acme, Mike, "old")),
            ("the Owner", () => Get(_acme, OwnerA, "secret")),
            ("a team member who is not a contributor", () => Get(_acme, Dev, "secret")),
            ("a team that does not exist", () => Get(NoSuchTeam, Mike, "website")),
            ("no such factory", () => Get(_acme, Mike, "no-such-factory")),
            ("a bad window on a refused factory", () => Get(_acme, Mike, "secret", "not-a-window")),
        };
        foreach (var (why, ask) in cases)
        {
            var (status, body) = await ask();
            Assert.True(status == 404 && body == RefusalBody, $"{why}: {status} {body}");
        }

        // Removed from the team, after the factory was sent to her.
        Assert.Equal(200, (await Get(_acme, Nina, "secret")).Status);
        Assert.True(_teams.RemoveMember(_acme, Nina).IsDone);
        var removed = await Get(_acme, Nina, "secret");
        Assert.True(removed.Status == 404 && removed.Body == RefusalBody, $"removed: {removed.Status} {removed.Body}");
    }

    [Fact]
    public async Task Watch_ABadWindowOnAFactorySentToThem_IsA400NamingTheWindow()
    {
        Share(_acme, "website", Mike);

        var (status, body) = await Get(_acme, Mike, "website", "not-a-window");

        Assert.Equal(400, status);
        Assert.Contains("is not a window", body, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- rule (A)

    [Fact]
    public async Task Watch_TheOnlyContributor_TheirStepsCarryWords()
    {
        Share(_acme, "website", Mike);

        var step = Steps((await Get(_acme, Mike, "website")).Body).Single();

        Assert.True(step.GetProperty("mine").GetBoolean());
        Assert.Equal(Sentence, step.GetProperty("words").GetString());
    }

    [Fact]
    public async Task Watch_TwoContributors_NoStepCarriesWords()
    {
        Share(_acme, "website", Mike);
        Share(_acme, "website", Nina);

        var steps = Steps((await Get(_acme, Mike, "website")).Body);

        Assert.NotEmpty(steps);
        Assert.All(steps, s =>
        {
            Assert.False(s.GetProperty("mine").GetBoolean());
            Assert.Equal("", s.GetProperty("words").GetString());
        });
    }

    [Fact]
    public void IsWholeFactory_ExactlyOneShareAndItIsThisPerson_OnlyThen()
    {
        FactoryShare To(string who) => new("website", who, OwnerA, Now);

        Assert.True(ContributorWatchScope.IsWholeFactory(new[] { To(Mike) }, Mike));
        Assert.False(ContributorWatchScope.IsWholeFactory(new[] { To(Mike), To(Nina) }, Mike));
        Assert.False(ContributorWatchScope.IsWholeFactory(new[] { To(Nina) }, Mike));
        Assert.False(ContributorWatchScope.IsWholeFactory(Array.Empty<FactoryShare>(), Mike));
    }

    // ---------------------------------------------------------------- the contributor audience, never the owner's

    [Fact]
    public async Task Watch_AnswersTheContributorAudience_NoNotesNoFactoryIdOpaqueKeys_NothingInternal()
    {
        Share(_acme, "website", Mike);

        var (_, body) = await Get(_acme, Mike, "website");

        var root = JsonDocument.Parse(body).RootElement;
        Assert.Equal("", root.GetProperty("factoryId").GetString());
        Assert.Empty(root.GetProperty("notes").EnumerateArray());
        Assert.All(root.GetProperty("stations").EnumerateArray(), s => Assert.Matches("^seat-[0-9]+$", s.GetProperty("id").GetString()!));
        Assert.All(root.GetProperty("characters").EnumerateArray(), c => Assert.Matches("^seat-[0-9]+$", c.GetProperty("seat").GetString()!));
        Assert.All(Steps(body), s => Assert.Matches("^step-[0-9]+$", s.GetProperty("key").GetString()!));
        foreach (var internalWord in new[] { TeamMachine, Folder, SessionId, "claude-opus", SeatId, "rebuilt the menu", "file:" })
            Assert.DoesNotContain(internalWord, body, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- the machine names are the team's

    [Fact]
    public async Task Watch_ChecksWordsAgainstTheTeamAccountsMachines_NotTheCallersOwn()
    {
        // Arrange: a factory whose worker seat is named after one of the TEAM's machines, and another named after a machine
        // on Mike's OWN account. Only the team's machines are this factory's buildings.
        SeedMachine(TeamMachine, _acme);
        SeedMachine(PersonalMachine, _tenants.MintOrLookupBySubject(Mike, null).Value);
        Register(_acme, "named-team", "Team machine seat", workerName: TeamMachine);
        Register(_acme, "named-personal", "Personal machine seat", workerName: PersonalMachine);
        Share(_acme, "named-team", Mike);
        Share(_acme, "named-personal", Mike);

        static IEnumerable<string> Labels(string body) =>
            JsonDocument.Parse(body).RootElement.GetProperty("stations").EnumerateArray().Select(s => s.GetProperty("label").GetString()!);

        // Assert: the team's machine name is replaced by the neutral word; the caller's own is not a team machine.
        var team = Labels((await Get(_acme, Mike, "named-team")).Body).ToList();
        Assert.DoesNotContain(TeamMachine, team);
        Assert.Contains("Worker", team);
        Assert.Contains(PersonalMachine, Labels((await Get(_acme, Mike, "named-personal")).Body));
    }

    // ---------------------------------------------------------------- never an agent's route

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SessionKeyGuard_TheWatchRoute_IsRefusedToEverySessionKey(bool raised)
    {
        // Every team route is a person's (security review M1): a session key, raised or not, never reaches one. The guard
        // is an allow list, so this stays true only while nobody adds the route to it.
        var path = Regex.Replace(ContributorEndpoints.WatchPattern, @"\{teamId\}", _acme).Replace("{factory}", "website");

        var verdict = SessionKeyGuard.Check("GET", path, raised);

        Assert.False(verdict.Allowed, path);
    }

    // ---------------------------------------------------------------- the explainer card (piece 6)

    /// <summary>The card's words as the owner approved them on 10 November 2026, written out here on purpose: a reword in
    /// the Gateway turns this red.</summary>
    private static readonly string[] ApprovedParagraphs =
    {
        "Your factory does real work, and we take that work seriously. We also think it is fun to watch. So we drew it.",
        "Each character is one of the workers in your factory. What it does comes from the factory's own record. Only the walking and the timing are drawn for the screen. An hour of work takes seconds.",
        "We thought a drawing was the best way to show what goes on inside. We hope it helps.",
    };

    [Fact]
    public async Task Watch_BeforeTheirFirstWatch_CarriesTheExplainer_InTheOwnersApprovedWords()
    {
        Share(_acme, "website", Mike);

        var explainer = Explainer((await Get(_acme, Mike, "website")).Body);

        Assert.Equal(JsonValueKind.Object, explainer.ValueKind);
        Assert.Equal(ApprovedParagraphs, explainer.GetProperty("paragraphs").EnumerateArray().Select(p => p.GetString()!).ToArray());
        Assert.Equal("Watch your factory", explainer.GetProperty("buttonText").GetString());
    }

    [Fact]
    public async Task ExplainerSeen_ThenWatch_CarriesNoExplainer_OnThatTeamAndEveryOtherTeam()
    {
        // Arrange: Mike watches a factory in Acme and one in Beta.
        Share(_acme, "website", Mike);
        Share(_beta, "website", Mike);

        // Act: he presses the card's button once, in Acme.
        var (status, body) = await Seen(_acme, Mike, "website");

        // Assert: recorded; the card is gone in Acme AND in Beta, because it is remembered for the person, not the team.
        Assert.Equal(204, status);
        Assert.Equal("", body);
        Assert.Equal(JsonValueKind.Null, Explainer((await Get(_acme, Mike, "website")).Body).ValueKind);
        Assert.Equal(JsonValueKind.Null, Explainer((await Get(_beta, Mike, "website")).Body).ValueKind);
    }

    [Fact]
    public async Task ExplainerSeen_IsRememberedPerPerson_AnotherContributorStillSeesTheCard()
    {
        Share(_acme, "website", Mike);
        Share(_acme, "website", Nina);

        await Seen(_acme, Mike, "website");

        Assert.Equal(JsonValueKind.Null, Explainer((await Get(_acme, Mike, "website")).Body).ValueKind);
        Assert.Equal(JsonValueKind.Object, Explainer((await Get(_acme, Nina, "website")).Body).ValueKind);
    }

    [Fact]
    public async Task ExplainerSeen_IsKeptOnThePersonsOwnAccount_AndTheFirstTimeIsNeverMoved()
    {
        Share(_acme, "website", Mike);
        var mikesOwn = _tenants.LookupBySubject(Mike)!.Value;

        await Seen(_acme, Mike, "website");
        _now = Now.AddDays(3);
        Assert.Equal(204, (await Seen(_acme, Mike, "website")).Status);

        Assert.Equal(Now.ToString("O"), _settings.Get(mikesOwn, TenantSettingKeys.WatchExplainerSeenAt));
        Assert.Null(_settings.Get(new TenantId(_acme), TenantSettingKeys.WatchExplainerSeenAt));
    }

    [Fact]
    public async Task ExplainerSeen_EveryRefusal_AnswersTheCustomerPagesNotFound_ByteForByte_AndRecordsNothing()
    {
        // Arrange: the same refusals the watch view gives. Website is sent to Mike in BETA only; Acme's secret to Nina.
        Share(_beta, "website", Mike);
        Register(_acme, "old", "Old site");
        Share(_acme, "old", Mike);
        _factories.Archive(new TenantId(_acme), "old", "the owner (test)", Array.Empty<string>(), Now);
        Share(_acme, "secret", Nina);
        Share(_acme, "secret", Dev);

        var cases = new (string Why, Func<Task<(int, string)>> Ask)[]
        {
            ("not sent to them", () => Seen(_acme, Mike, "secret")),
            ("another team's factory of the same name", () => Seen(_acme, Mike, "website")),
            ("archived", () => Seen(_acme, Mike, "old")),
            ("the Owner", () => Seen(_acme, OwnerA, "secret")),
            ("a team member who is not a contributor", () => Seen(_acme, Dev, "secret")),
            ("a team that does not exist", () => Seen(NoSuchTeam, Mike, "website")),
            ("no such factory", () => Seen(_acme, Mike, "no-such-factory")),
        };
        foreach (var (why, ask) in cases)
        {
            var (status, body) = await ask();
            Assert.True(status == 404 && body == RefusalBody, $"{why}: {status} {body}");
        }

        // Nothing was recorded for anyone who was refused: Mike still gets the card where he may watch.
        Assert.Equal(JsonValueKind.Object, Explainer((await Get(_beta, Mike, "website")).Body).ValueKind);
        foreach (var who in new[] { Mike, OwnerA, Dev })
            Assert.Null(_settings.Get(_tenants.LookupBySubject(who)!.Value, TenantSettingKeys.WatchExplainerSeenAt));
    }

    [Fact]
    public void Factories_EveryCard_CarriesTheWatchButtonsWords()
    {
        Share(_acme, "website", Mike);

        var json = JsonSerializer.Serialize(_pages.Factories(_acme, Mike), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var card = JsonDocument.Parse(json).RootElement.GetProperty("factories").EnumerateArray().Single();
        Assert.Equal("Watch your factory", card.GetProperty("watchLabel").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SessionKeyGuard_TheExplainerSeenRoute_IsRefusedToEverySessionKey(bool raised)
    {
        // A person-only route: a session key, raised or not, never reaches it. The guard is an allow list, so this stays
        // true only while nobody adds the route to it.
        var path = Regex.Replace(ContributorEndpoints.ExplainerSeenPattern, @"\{teamId\}", _acme).Replace("{factory}", "website");

        var verdict = SessionKeyGuard.Check("POST", path, raised);

        Assert.False(verdict.Allowed, path);
    }

    [Fact]
    public void TeamGate_TheExplainerSeenRoute_IsTheOnlyWriteUnderTheContributorRoutes_AndOnlyExactly()
    {
        var seen = TeamEndpointRules.Find("POST", ContributorEndpoints.ExplainerSeenPattern);

        Assert.NotNull(seen);
        Assert.Equal(TeamAction.AnswerQuestionsSendRequestsReadReports, seen!.Action);
        Assert.True(seen.Exact);
        foreach (var other in new[] { ContributorEndpoints.WatchPattern, ContributorEndpoints.FactoriesPattern, ContributorEndpoints.InboxPattern,
                     ContributorEndpoints.FactoriesPattern + "/{factory}", ContributorEndpoints.ExplainerSeenPattern + "/more" })
            Assert.Null(TeamEndpointRules.Find("POST", other));
    }
}
