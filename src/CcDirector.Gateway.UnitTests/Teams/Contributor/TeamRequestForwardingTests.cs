using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.DevReports;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Inbox;
using CcDirector.Gateway.Factory.Registry;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams.Contributor;

/// <summary>
/// THE OWNER'S "FORWARD TO THE FACTORY" (Team implementation piece 4.3, devthrottle_internal#2800), over a real database
/// and the real factory inbox store: a contributor's words reach a factory only when the team's Owner forwards them,
/// quoted and marked as the contributor's (who, which team, when), in the Owner's name, into a factory of THAT team.
///
/// Acme: Owner Olivia, Manager Mark, Developer Dev, contributor Carla; factories "website" and "old" (archived).
/// Beta: Owner Bob; factory "shop".
/// </summary>
public sealed class TeamRequestForwardingTests : IDisposable
{
    private const string Owner = "sub-owner";
    private const string Manager = "sub-manager";
    private const string Developer = "sub-developer";
    private const string Carla = "sub-carla";
    private const string BetaOwner = "sub-beta-owner";
    private const string OwnerPhone = "owner-ns|phone-1";
    private static readonly DateTime Written = new(2026, 11, 9, 14, 5, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 11, 9, 15, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly TeamRegistry _teams;
    private readonly TeamRequestStore _requests;
    private readonly FactoryRegistryStore _factories;
    private readonly FactoryInboxStore _inbox;
    private readonly FactoryAgentsSwitch _factorySwitch;
    private readonly TeamRequestForwarding _forwarding;
    private readonly List<(TenantId Tenant, string Factory)> _wakes = new();
    private readonly string _acme;
    private readonly string _beta;
    private DateTime _clock = Written;

    public TeamRequestForwardingTests()
    {
        _db = _harness.Open();
        var tenants = new TenantRegistry(_db);
        foreach (var (subject, email) in new[]
                 {
                     (Owner, "olivia@example.com"), (Manager, "mark@example.com"), (Developer, "dev@example.com"),
                     (Carla, "carla@client.example"), (BetaOwner, "bob@example.com"),
                 })
            tenants.MintOrLookupBySubject(subject, email);
        _teams = new TeamRegistry(_db, tenants);
        _requests = new TeamRequestStore(_db, _teams, () => _clock);
        _factories = new FactoryRegistryStore(_db);
        _inbox = new FactoryInboxStore(_db, _ => CcDirector.Core.Dictation.Models.DictationDictionary.Empty);
        _factorySwitch = new FactoryAgentsSwitch(machineWide: false, new CcDirector.Gateway.Settings.TenantSettingsStore(_db), log: _ => { });
        _forwarding = new TeamRequestForwarding(_requests, _teams, _factories, _factorySwitch,
            (tenant, input, ct) => _inbox.SendAsync(tenant, input, ct), (tenant, factory, _) => _wakes.Add((tenant, factory)), () => Now);

        _acme = _teams.CreateTeam(Owner, "Acme").Team!.TeamId;
        _beta = _teams.CreateTeam(BetaOwner, "Beta").Team!.TeamId;
        Assert.True(_teams.AddMember(_acme, Manager, TeamRole.Manager).IsDone);
        Assert.True(_teams.AddMember(_acme, Developer, TeamRole.Developer).IsDone);
        Assert.True(_teams.AddMember(_acme, Carla, TeamRole.Collaborator).IsDone);
        _factorySwitch.Set(new TenantId(_acme), true, "the administrator (test)", "factories for Acme", Now);
        _factorySwitch.Set(new TenantId(_beta), true, "the administrator (test)", "factories for Beta", Now);
        Register(_acme, "website", "Erik's website");
        Register(_acme, "old", "Old site");
        _factories.Archive(new TenantId(_acme), "old", "the owner (test)", Array.Empty<string>(), Now);
        Register(_beta, "shop", "Beta shop");
    }

    public void Dispose() => _harness.Dispose();

    private void Register(string team, string factory, string title) =>
        _factories.Register(new TenantId(team), new RegisterFactoryRequest
        {
            Factory = factory, Title = title, Folder = "/srv/" + factory, Computer = "BOX", BossSeat = "boss",
            Seats = { new FactorySeatManifest { Id = "boss", Name = "Boss", Role = "Boss", BriefFile = "agents/boss.yaml" } },
        }, "the owner (test)", Now);

    private string CarlaWrites(string text = "Could the menu also show prices?\nAnd the opening hours.")
    {
        var sent = _requests.Send(_acme, Carla, text);
        Assert.Equal(TeamRequestOutcome.Done, sent.Outcome);
        return sent.Request!.Id;
    }

    private Task<TeamRequestResult> Forward(string caller, string requestId, string? factory = "website", string? note = null, string team = "") =>
        _forwarding.ForwardAsync(team.Length == 0 ? _acme : team, requestId, caller,
            new TeamForwardDevice(FactoryInboxWords.Surfaces.Phone, OwnerPhone), factory, note);

    private IReadOnlyList<FactoryInboxMessageEntityView> FactoryMessages(string team, string factory) =>
        _inbox.Thread(new TenantId(team), factory, 50).Messages.Select(m => new FactoryInboxMessageEntityView(m.Text, m.Surface, m.SentBy, m.Source)).ToList();

    /// <summary>Every factory inbox message in the database, in every tenant - read past the tenant filter, straight from
    /// the table, so a message left in the wrong team's tenant is counted too.</summary>
    private int EveryFactoryMessage()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_harness.DbPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM factory_inbox_messages";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private sealed record FactoryInboxMessageEntityView(string Text, string Surface, string SentBy, string Source);

    // ---------------------------------------------------------------- the forward arrives quoted and marked

    [Fact]
    public async Task ForwardAsync_TheOwner_LeavesTheWordsInTheFactoryInboxQuotedMarkedAndInTheOwnersName()
    {
        // Arrange
        var id = CarlaWrites();

        // Act
        var result = await Forward(Owner, id);

        // Assert: one message in Acme's website factory, from the Owner's own phone, typed, and marked.
        Assert.Equal(TeamRequestOutcome.Done, result.Outcome);
        var left = Assert.Single(FactoryMessages(_acme, "website"));
        Assert.Equal((FactoryInboxWords.Surfaces.Phone, OwnerPhone, FactoryInboxWords.Sources.Typed), (left.Surface, left.SentBy, left.Source));
        Assert.Equal(
            TeamRequestForwarding.QuoteIntro + "\n" +
            "From: carla@client.example, a contributor on the team \"Acme\"\n" +
            "Written: 9 November 2026 at 14:05 UTC\n\n" +
            "> Could the menu also show prices?\n" +
            "> And the opening hours.",
            left.Text);
        Assert.Equal((new TenantId(_acme), "website"), Assert.Single(_wakes));
    }

    [Fact]
    public async Task ForwardAsync_WithANote_PutsTheOwnersNoteAboveTheQuote()
    {
        var id = CarlaWrites("Prices please.");

        var result = await Forward(Owner, id, note: "  Please add this to the menu page.  ");

        Assert.Equal(TeamRequestOutcome.Done, result.Outcome);
        var text = Assert.Single(FactoryMessages(_acme, "website")).Text;
        Assert.StartsWith("Please add this to the menu page.\n\n" + TeamRequestForwarding.QuoteIntro + "\n", text);
        Assert.EndsWith("> Prices please.", text);
    }

    [Fact]
    public async Task ForwardAsync_TheOwner_RecordsTheForwardAndTheOwnerSeesWhichFactory()
    {
        var id = CarlaWrites();

        var result = await Forward(Owner, id);

        var view = result.Request!;
        Assert.Equal((TeamRequestStates.Forwarded, "Forwarded to the factory"), (view.State, view.StateLabel));
        Assert.False(view.CanForward);
        var step = view.Trail.Last();
        Assert.Equal(("Forwarded to the factory by You", "website", (string?)null), (step.Sentence, step.ForwardedTo, step.Reason));
    }

    // ---------------------------------------------------------------- the contributor sees the forwarded state, nothing internal

    [Fact]
    public async Task ListMine_AfterTheForward_TheContributorSeesItWasForwardedAndNotWhere()
    {
        var id = CarlaWrites();
        Assert.Equal(TeamRequestOutcome.Done, (await Forward(Owner, id)).Outcome);

        var mine = Assert.Single(_requests.ListMine(_acme, Carla).Requests);

        Assert.Equal((TeamRequestStates.Forwarded, "Forwarded to the factory"), (mine.State, mine.StateLabel));
        var step = mine.Trail.Last();
        Assert.Equal("Forwarded to the factory by the team's Owner", step.Sentence);
        Assert.Null(step.ForwardedTo);
        Assert.Null(step.Reason);
        Assert.False(mine.CanForward);
        var wire = JsonSerializer.Serialize(TeamRequestEndpoints.Describe(mine));
        Assert.DoesNotContain("website", wire, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Erik", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("olivia@", wire, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContributorInbox_AfterTheForward_ShowsTheGatewaysNoticeAndNothingInternal()
    {
        // Arrange: the contributor's Inbox page, as piece 4.1 reads it.
        var id = CarlaWrites();
        Assert.Equal(TeamRequestOutcome.Done, (await Forward(Owner, id)).Outcome);
        var reports = new DevReportStore(_db);
        var recipients = new DevReportRecipients(_db);
        var access = new TeamAccess(_teams);
        var delivery = new DevReportDelivery(reports,
            (_, _) => new DevReportSessionLiveness(DevReportSessionReach.Busy, null, "no session in these tests"),
            (_, _) => null, CancellationToken.None, () => Now);
        var questions = new TeamQuestions(reports, recipients, new DevReportPersonComments(_db), _teams, access, delivery, () => Now);
        var pages = new ContributorPages(new ContributorFactoryAccess(_teams, _factories, new FactoryContributors(_db)),
            new FactoryActivityRecord(_db), reports, recipients, questions, _requests, _teams, _ => "UTC", () => Now);

        // Act
        var inbox = JsonSerializer.SerializeToElement(pages.Inbox(_acme, Carla));

        // Assert
        var item = Assert.Single(inbox.GetProperty("items").EnumerateArray());
        Assert.Equal("Forwarded to the factory", item.GetProperty("stateLabel").GetString());
        Assert.Equal(ContributorPages.ForwardedNotice, item.GetProperty("notice").GetString());
        var raw = inbox.GetRawText();
        Assert.DoesNotContain("website", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Erik", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(OwnerPhone, raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListForTeam_AfterTheForward_AManagerSeesItWasForwardedButNotWhere()
    {
        var id = CarlaWrites();
        Assert.Equal(TeamRequestOutcome.Done, (await Forward(Owner, id)).Outcome);

        var seen = Assert.Single(_requests.ListForTeam(_acme, Manager).Requests);

        Assert.Null(seen.Trail.Last().ForwardedTo);
        Assert.False(seen.CanForward);
        Assert.True(seen.CanMarkDone);
    }

    // ---------------------------------------------------------------- refused

    [Theory]
    [InlineData(Manager)]
    [InlineData(Developer)]
    [InlineData(Carla)]
    public async Task ForwardAsync_AnyoneButTheOwner_IsRefusedAndNoFactoryGetsAWord(string caller)
    {
        var id = CarlaWrites();

        var result = await Forward(caller, id);

        Assert.Equal(TeamRequestOutcome.Refused, result.Outcome);
        Assert.Equal(0, EveryFactoryMessage());
        Assert.Empty(_wakes);
        Assert.Equal(TeamRequestStates.Sent, Assert.Single(_requests.ListMine(_acme, Carla).Requests).State);
    }

    [Fact]
    public async Task ForwardAsync_AStranger_IsNoSuchTeam()
    {
        var id = CarlaWrites();

        var result = await Forward(BetaOwner, id);

        Assert.Equal(TeamRequestOutcome.NoSuchTeam, result.Outcome);
        Assert.Equal(0, EveryFactoryMessage());
    }

    [Fact]
    public async Task ForwardAsync_ToAFactoryOfAnotherTeam_IsRefusedAndNothingIsLeftThere()
    {
        // "shop" is Beta's factory; Acme's Owner names it on Acme's route.
        var id = CarlaWrites();

        var result = await Forward(Owner, id, factory: "shop");

        Assert.Equal(TeamRequestOutcome.Invalid, result.Outcome);
        Assert.Equal(TeamRequestForwarding.NoSuchFactory, result.Refusal);
        Assert.Equal(0, EveryFactoryMessage());
        Assert.Equal(TeamRequestStates.Sent, Assert.Single(_requests.ListMine(_acme, Carla).Requests).State);
    }

    [Fact]
    public async Task ForwardAsync_AMessageOfAnotherTeam_IsNoSuchRequest()
    {
        // Beta's Owner, on Beta's route, names Acme's message id.
        var id = CarlaWrites();

        var result = await Forward(BetaOwner, id, factory: "shop", team: _beta);

        Assert.Equal(TeamRequestOutcome.NoSuchRequest, result.Outcome);
        Assert.Equal(0, EveryFactoryMessage());
    }

    [Fact]
    public async Task ForwardAsync_ToAnArchivedFactory_IsRefused()
    {
        var id = CarlaWrites();

        var result = await Forward(Owner, id, factory: "old");

        Assert.Equal((TeamRequestOutcome.Conflict, TeamRequestForwarding.ArchivedFactory), (result.Outcome, result.Refusal));
        Assert.Equal(0, EveryFactoryMessage());
    }

    [Fact]
    public async Task ForwardAsync_WithTheTeamsFactoryAreaOff_IsRefused()
    {
        var id = CarlaWrites();
        _factorySwitch.Set(new TenantId(_acme), false, "the administrator (test)", "off", Now);

        var result = await Forward(Owner, id);

        Assert.Equal(TeamRequestOutcome.Conflict, result.Outcome);
        Assert.Equal(0, EveryFactoryMessage());
    }

    [Fact]
    public async Task ForwardAsync_Twice_TheSecondIsRefusedAndTheFactoryHasOneMessage()
    {
        var id = CarlaWrites();
        Assert.Equal(TeamRequestOutcome.Done, (await Forward(Owner, id)).Outcome);

        var again = await Forward(Owner, id);

        Assert.Equal(TeamRequestOutcome.Conflict, again.Outcome);
        Assert.Single(FactoryMessages(_acme, "website"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task ForwardAsync_NoFactoryNamed_IsInvalid(string? factory)
    {
        var id = CarlaWrites();

        var result = await Forward(Owner, id, factory: factory);

        Assert.Equal(TeamRequestOutcome.Invalid, result.Outcome);
        Assert.Equal(0, EveryFactoryMessage());
    }

    [Fact]
    public async Task ForwardAsync_ANoteTooLong_IsInvalid()
    {
        var id = CarlaWrites();

        var result = await Forward(Owner, id, note: new string('n', TeamRequestStates.MaxNoteLength + 1));

        Assert.Equal(TeamRequestOutcome.Invalid, result.Outcome);
        Assert.Equal(0, EveryFactoryMessage());
    }

    [Fact]
    public async Task ForwardAsync_ADeclinedMessage_IsRefused()
    {
        var id = CarlaWrites();
        Assert.Equal(TeamRequestOutcome.Done, _requests.Decide(_acme, id, Owner, TeamRequestDecision.Decline, "Not now").Outcome);

        var result = await Forward(Owner, id);

        Assert.Equal(TeamRequestOutcome.Conflict, result.Outcome);
        Assert.Equal(0, EveryFactoryMessage());
    }

    [Fact]
    public void Decide_TheForwardDecision_ThrowsBecauseAForwardGoesThroughTheFactoryInbox()
    {
        var id = CarlaWrites();

        Assert.Throws<ArgumentException>(() => _requests.Decide(_acme, id, Owner, TeamRequestDecision.Forward, null));
    }

    // ---------------------------------------------------------------- what the Owner may forward to

    [Fact]
    public void Targets_TheOwner_GetsTheTeamsLiveFactoriesOnly()
    {
        var targets = _forwarding.Targets(_acme, Owner);

        Assert.Equal(new[] { new TeamForwardTarget("website", "Erik's website") }, targets.Factories);
        Assert.Null(targets.EmptyText);
    }

    [Theory]
    [InlineData(Manager)]
    [InlineData(Carla)]
    public void Targets_AnyoneButTheOwner_GetsNothing(string caller)
    {
        Assert.Empty(_forwarding.Targets(_acme, caller).Factories);
    }

    [Fact]
    public void Targets_TheFactoryAreaOff_GetsNothingAndSaysWhy()
    {
        _factorySwitch.Set(new TenantId(_acme), false, "the administrator (test)", "off", Now);

        var targets = _forwarding.Targets(_acme, Owner);

        Assert.Empty(targets.Factories);
        Assert.False(string.IsNullOrWhiteSpace(targets.EmptyText));
    }

    [Fact]
    public void ListForTeam_TheOwner_MayForwardASentMessage_AManagerMayNot()
    {
        CarlaWrites();

        Assert.True(Assert.Single(_requests.ListForTeam(_acme, Owner).Requests).CanForward);
        Assert.False(Assert.Single(_requests.ListForTeam(_acme, Manager).Requests).CanForward);
    }

    [Fact]
    public void Describe_AContributorsMessage_TheOwnerReadsWhoWroteItAndThatNoAgentHasSeenIt()
    {
        CarlaWrites();

        var wire = JsonSerializer.SerializeToElement(TeamRequestEndpoints.Describe(Assert.Single(_requests.ListForTeam(_acme, Owner).Requests)));

        Assert.Equal("From carla@client.example, a contributor", wire.GetProperty("fromLabel").GetString());
        Assert.Equal(TeamRequestEndpoints.NotSeenByAgents, wire.GetProperty("privacyNote").GetString());
        Assert.True(wire.GetProperty("canForward").GetBoolean());
    }

    // ---------------------------------------------------------------- the quote

    [Fact]
    public void Quote_ASenderWhoHasLeft_SaysSoAndStillQuotesEveryLine()
    {
        var text = TeamRequestForwarding.Quote(null, TeamRequestStore.FormerMemberName, null, "Acme", Written, "one\r\ntwo");

        Assert.Contains("From: A former member of the team, who has since left the team \"Acme\"", text);
        Assert.EndsWith("> one\n> two", text);
    }
}
