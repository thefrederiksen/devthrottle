using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.DevReports;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Registry;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams.Contributor;

/// <summary>
/// A CONTRIBUTOR IS TOLD THAT SOMETHING IS WAITING, BY THEIR OWN CHOICE, AND THE EMAIL CARRIES NO CONTENT (Team
/// implementation piece 4.4, devthrottle_internal#2801; security review Risk 8). Over a real database, through the real
/// send routes' answers (<see cref="TeamReports"/>, <see cref="FactoryShares"/>, <see cref="TeamRequestStore"/>) and the
/// real sweep (<see cref="ContributorNoticeSweep"/>), with a recording email sender and an injected clock - no port, no
/// wait.
///
/// Two teams: Acme (Owner A; contributors Mike and Nina; developer Dave; factory "website") and Beta (Owner B; contributor
/// Mike). Mike is a contributor on both.
/// </summary>
public sealed class ContributorNoticeTests : IDisposable
{
    private const string OwnerA = "sub-owner-a";
    private const string OwnerB = "sub-owner-b";
    private const string Mike = "sub-mike";
    private const string Nina = "sub-nina";
    private const string Dave = "sub-dave";
    private const string Base = "https://gateway.example.test";

    // Words that must never leave in an email: what was sent, and who else is in the team.
    private const string ReportMarker = "ZQREPORTMARKER17";
    private const string QuestionMarker = "ZQQUESTIONMARKER23";
    private const string TitleMarker = "ZQTITLEMARKER31";
    private const string FactoryMarker = "ZQFACTORYMARKER41";
    private const string RequestMarker = "ZQREQUESTMARKER53";
    private const string ReasonMarker = "ZQREASONMARKER59";
    private const string OwnerEmail = "owner-a-zq@example.com";
    private const string NinaEmail = "nina-zq@example.com";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly TenantRegistry _tenants;
    private readonly TeamRegistry _teams;
    private readonly DevReportStore _store;
    private readonly ContributorNotices _notices;
    private readonly TeamReports _reports;
    private readonly FactoryShares _shares;
    private readonly TeamRequestStore _requests;
    private readonly RecordingSender _mail = new();
    private readonly Dictionary<string, string> _zones = new(StringComparer.Ordinal);
    private string? _publicBase = Base;
    private DateTime _now = new(2026, 11, 9, 6, 0, 0, DateTimeKind.Utc);
    private readonly ContributorNoticeSweep _sweep;
    private readonly string _acme;
    private readonly string _beta;

    public ContributorNoticeTests()
    {
        _db = _harness.Open();
        _tenants = new TenantRegistry(_db);
        foreach (var (subject, email) in new[]
                 {
                     (OwnerA, OwnerEmail), (OwnerB, "b@example.com"), (Mike, "mike@example.com"), (Nina, NinaEmail), (Dave, "dave@example.com"),
                 })
            _tenants.MintOrLookupBySubject(subject, email);
        _teams = new TeamRegistry(_db, _tenants, () => _now);
        var access = new TeamAccess(_teams);
        _store = new DevReportStore(_db);
        var recipients = new DevReportRecipients(_db);
        var comments = new DevReportPersonComments(_db);
        var delivery = new DevReportDelivery(_store,
            (_, _) => new DevReportSessionLiveness(DevReportSessionReach.Busy, null, "no session in these tests"),
            (_, _) => null, CancellationToken.None, () => _now);
        var questions = new TeamQuestions(_store, recipients, comments, _teams, access, delivery, () => _now);
        _notices = new ContributorNotices(_db, _teams, () => _now);
        _reports = new TeamReports(_store, recipients, comments, _teams, access, questions, _notices, () => _now);
        var factorySwitch = new FactoryAgentsSwitch(machineWide: false, new TenantSettingsStore(_db), log: _ => { });
        var factories = new FactoryRegistryStore(_db);
        _shares = new FactoryShares(_teams, factories, new FactoryContributors(_db), factorySwitch, _notices, () => _now);
        _requests = new TeamRequestStore(_db, _teams, () => _now);
        _sweep = new ContributorNoticeSweep(_db, _notices, _teams, _tenants, _mail,
            t => _zones.TryGetValue(t.Value, out var z) ? z : "UTC", () => _publicBase, () => _now);

        _acme = _teams.CreateTeam(OwnerA, "Acme").Team!.TeamId;
        _beta = _teams.CreateTeam(OwnerB, "Beta").Team!.TeamId;
        factorySwitch.Set(new TenantId(_acme), true, "the administrator (test)", "factories for Acme", _now);
        Assert.True(_teams.AddMember(_acme, Mike, TeamRole.Collaborator).IsDone);
        Assert.True(_teams.AddMember(_acme, Nina, TeamRole.Collaborator).IsDone);
        Assert.True(_teams.AddMember(_acme, Dave, TeamRole.Developer).IsDone);
        Assert.True(_teams.AddMember(_beta, Mike, TeamRole.Collaborator).IsDone);
        factories.Register(new TenantId(_acme), new RegisterFactoryRequest
        {
            Factory = "website", Title = "Website " + FactoryMarker, Folder = "/srv/website", Computer = "BOX", BossSeat = "boss",
            Seats = { new FactorySeatManifest { Id = "boss", Name = "Boss", Role = "Boss", BriefFile = "agents/boss.yaml" } },
        }, "the owner (test)", _now);
    }

    public void Dispose() => _harness.Dispose();

    // ---------------------------------------------------------------- the email carries no content

    [Fact]
    public async Task RunOnce_EverythingSentCarriesMarkers_NoEmailCarriesAnyOfThem()
    {
        // Arrange: a report whose title, text and question carry markers, sent to Mike and Nina; the factory shared with
        // Mike; Mike's request, declined with a reason by the Owner.
        await SendReportAsync(_acme, OwnerA, Mike, Nina);
        await ShareAsync(Mike);
        var request = _requests.Send(_acme, Mike, "Could the menu show prices? " + RequestMarker).Request!;
        Assert.Equal(TeamRequestOutcome.Done, _requests.Decide(_acme, request.Id, OwnerA, TeamRequestDecision.Decline, "Not now " + ReasonMarker).Outcome);

        // Act
        await _sweep.RunOnceAsync();

        // Assert: one email per thing (Mike three, Nina one), and not one marker, address or other person anywhere in them.
        Assert.Equal(4, _mail.Sent.Count);
        foreach (var (to, email) in _mail.Sent)
        {
            var all = email.Subject + "\n" + email.Text + "\n" + email.Html;
            foreach (var forbidden in new[]
                     {
                         ReportMarker, QuestionMarker, TitleMarker, FactoryMarker, RequestMarker, ReasonMarker, OwnerEmail, NinaEmail,
                         "mike@example.com", OwnerA, Mike, Nina, Dave, "website", "Could the menu",
                     })
                Assert.DoesNotContain(forbidden, all, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"Acme\"", email.Text);
            Assert.Contains(Base + ContributorNoticeSweep.InboxPath, email.Text);
            Assert.Equal(ContributorNoticeEmail.Subject, email.Subject);
        }
    }

    [Fact]
    public void ContributorNoticeEmail_TeamNameWithMarkup_IsEscapedInTheHtml()
    {
        // Arrange / Act
        var email = ContributorNoticeEmail.RightAway("<b>Acme</b>", Base + "/contributor/inbox");

        // Assert
        Assert.DoesNotContain("<b>", email.Html);
        Assert.Contains("&lt;b&gt;Acme&lt;/b&gt;", email.Html);
    }

    // ---------------------------------------------------------------- right away, once a day, never

    [Fact]
    public async Task RunOnce_RightAway_SendsOneEmailPerEventAndNothingTwice()
    {
        // Arrange: two things for Mike, in two teams; he never chose, so right away applies.
        await SendReportAsync(_acme, OwnerA, Mike);
        await SendReportAsync(_beta, OwnerB, Mike);

        // Act
        var first = await _sweep.RunOnceAsync();
        var second = await _sweep.RunOnceAsync();

        // Assert
        Assert.Equal(2, first.EmailsSent);
        Assert.Equal(0, second.EmailsSent);
        Assert.Equal(new[] { Mike, Mike }, _mail.Sent.Select(s => s.To));
        Assert.Contains(_mail.Sent, s => s.Email.Text.Contains("\"Acme\"", StringComparison.Ordinal));
        Assert.Contains(_mail.Sent, s => s.Email.Text.Contains("\"Beta\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunOnce_Daily_SendsOneEmailAtTheChosenHourInTheirOwnZone()
    {
        // Arrange: Mike chose once a day at 09:00 in New York (14:00 UTC on 9 November); two things wait, in two teams.
        _zones[Own(Mike).Value] = "America/New_York";
        Assert.NotNull(_notices.SetChoice(Own(Mike), ContributorNoticeModes.Daily, 9).Choice);
        await SendReportAsync(_acme, OwnerA, Mike);
        await SendReportAsync(_beta, OwnerB, Mike);

        // Act / Assert: nothing before the hour, one email naming both teams at it, nothing again inside the hour.
        _now = new DateTime(2026, 11, 9, 13, 59, 0, DateTimeKind.Utc);
        Assert.Equal(0, (await _sweep.RunOnceAsync()).EmailsSent);
        _now = new DateTime(2026, 11, 9, 14, 0, 0, DateTimeKind.Utc);
        Assert.Equal(1, (await _sweep.RunOnceAsync()).EmailsSent);
        _now = new DateTime(2026, 11, 9, 14, 30, 0, DateTimeKind.Utc);
        Assert.Equal(0, (await _sweep.RunOnceAsync()).EmailsSent);

        var (to, email) = Assert.Single(_mail.Sent);
        Assert.Equal(Mike, to);
        Assert.Contains("\"Acme\" and \"Beta\"", email.Text);
    }

    [Fact]
    public async Task RunOnce_DailyWithNothingWaiting_SendsNothing()
    {
        // Arrange
        Assert.NotNull(_notices.SetChoice(Own(Mike), ContributorNoticeModes.Daily, 8).Choice);

        // Act
        _now = new DateTime(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc);
        var result = await _sweep.RunOnceAsync();

        // Assert
        Assert.Equal(0, result.EmailsSent);
        Assert.Empty(_mail.Sent);
    }

    [Fact]
    public async Task RunOnce_DailyAfterTheHour_WaitsForTomorrowsHour()
    {
        // Arrange: the day's hour passed with nothing waiting; something arrives at 10:00.
        Assert.NotNull(_notices.SetChoice(Own(Mike), ContributorNoticeModes.Daily, 8).Choice);
        _now = new DateTime(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc);
        await _sweep.RunOnceAsync();
        _now = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
        await SendReportAsync(_acme, OwnerA, Mike);

        // Act / Assert: no email the same afternoon; one the next morning at 08:00.
        Assert.Equal(0, (await _sweep.RunOnceAsync()).EmailsSent);
        _now = new DateTime(2026, 11, 10, 8, 1, 0, DateTimeKind.Utc);
        Assert.Equal(1, (await _sweep.RunOnceAsync()).EmailsSent);
    }

    [Fact]
    public async Task RunOnce_Never_SendsNothingAndStopsWaiting()
    {
        // Arrange
        Assert.NotNull(_notices.SetChoice(Own(Mike), ContributorNoticeModes.Never, null).Choice);
        await SendReportAsync(_acme, OwnerA, Mike);

        // Act
        var result = await _sweep.RunOnceAsync();

        // Assert
        Assert.Equal(0, result.EmailsSent);
        Assert.Equal(1, result.Settled);
        Assert.Empty(_mail.Sent);
        Assert.Empty(_notices.Waiting(_acme));
    }

    // ---------------------------------------------------------------- only the person it was sent to

    [Fact]
    public async Task RunOnce_ReportSentToMikeOnly_OnlyMikeIsEmailed()
    {
        // Arrange
        await SendReportAsync(_acme, OwnerA, Mike);

        // Act
        await _sweep.RunOnceAsync();

        // Assert
        var (to, _) = Assert.Single(_mail.Sent);
        Assert.Equal(Mike, to);
    }

    [Fact]
    public async Task RunOnce_ContributorRemovedBeforeTheSweep_IsNotEmailed()
    {
        // Arrange: a report reaches Nina, and she is removed from the team before anything is sent (piece 1.5).
        await SendReportAsync(_acme, OwnerA, Nina);
        Assert.True(_teams.RemoveMember(_acme, Nina).IsDone);

        // Act
        await _sweep.RunOnceAsync();

        // Assert
        Assert.Empty(_mail.Sent);
        Assert.Empty(_notices.Waiting(_acme));
    }

    [Fact]
    public async Task RunOnce_ContributorMadeADeveloperBeforeTheSweep_IsNotEmailed()
    {
        // Arrange
        await SendReportAsync(_acme, OwnerA, Nina);
        Assert.True(_teams.ChangeRole(_acme, Nina, TeamRole.Developer).IsDone);

        // Act
        var result = await _sweep.RunOnceAsync();

        // Assert
        Assert.Empty(_mail.Sent);
        Assert.Equal(1, result.Settled);
    }

    [Fact]
    public async Task Send_ReportToADeveloper_RecordsNothingToTell()
    {
        // Arrange / Act
        await SendReportAsync(_acme, OwnerA, Dave);

        // Assert: only contributors are emailed; a developer sees their work in the Cockpit.
        Assert.Empty(_notices.Waiting(_acme));
    }

    [Fact]
    public async Task Share_TheSameFactoryTwice_TellsOnce()
    {
        // Arrange / Act
        await ShareAsync(Mike);
        await ShareAsync(Mike);

        // Assert
        var notice = Assert.Single(_notices.Waiting(_acme));
        Assert.Equal(ContributorNoticeKinds.Factory, notice.Kind);
    }

    [Fact]
    public void Decide_TheOwnerAcceptsAContributorsRequest_TellsThatContributor()
    {
        // Arrange
        var request = _requests.Send(_acme, Mike, "Prices on the menu, please").Request!;

        // Act
        Assert.Equal(TeamRequestOutcome.Done, _requests.Decide(_acme, request.Id, OwnerA, TeamRequestDecision.Accept, null).Outcome);

        // Assert
        var notice = Assert.Single(_notices.Waiting(_acme));
        Assert.Equal(Mike, notice.RecipientSubject);
        Assert.Equal(ContributorNoticeKinds.Reply, notice.Kind);
    }

    // ---------------------------------------------------------------- a send that fails

    [Fact]
    public async Task RunOnce_SendFails_TriedAgainAfterTheWaitAndSettledOnlyWhenSent()
    {
        // Arrange
        await SendReportAsync(_acme, OwnerA, Mike);
        _mail.Fail = true;

        // Act / Assert: the failed try keeps it waiting; the next pass inside the minute's wait tries nothing.
        Assert.Equal(1, (await _sweep.RunOnceAsync()).Failed);
        Assert.Single(_notices.Waiting(_acme));
        _now = _now.AddSeconds(30);
        Assert.Equal(0, (await _sweep.RunOnceAsync()).Failed);
        _mail.Fail = false;
        _now = _now.AddMinutes(1);
        Assert.Equal(1, (await _sweep.RunOnceAsync()).EmailsSent);
        Assert.Empty(_notices.Waiting(_acme));
        Assert.Equal(2, _mail.Tries);
    }

    [Fact]
    public async Task RunOnce_TwoGatewaysOverOneDatabase_RightAway_SendOneEmail()
    {
        // Arrange: a second Gateway's sweep over the same database, as during a deploy; it runs its whole pass while the
        // first Gateway's send is in flight - after the first has read what waits, before it has settled anything.
        await SendReportAsync(_acme, OwnerA, Mike);
        var other = SecondGatewaySweep();
        ContributorNoticeSweepResult? otherResult = null;
        _mail.DuringSend = async () => otherResult = await other.RunOnceAsync();

        // Act
        var result = await _sweep.RunOnceAsync();

        // Assert: one email between the two Gateways, and the notice settled once.
        Assert.Equal(1, result.EmailsSent);
        Assert.NotNull(otherResult);
        Assert.Equal(0, otherResult!.EmailsSent);
        Assert.Single(_mail.Sent);
        Assert.Empty(_notices.Waiting(_acme));
    }

    [Fact]
    public async Task RunOnce_TwoGatewaysOverOneDatabase_Daily_SendOneEmail()
    {
        // Arrange
        Assert.NotNull(_notices.SetChoice(Own(Mike), ContributorNoticeModes.Daily, 8).Choice);
        await SendReportAsync(_acme, OwnerA, Mike);
        await SendReportAsync(_beta, OwnerB, Mike);
        _now = new DateTime(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc);
        var other = SecondGatewaySweep();
        ContributorNoticeSweepResult? otherResult = null;
        _mail.DuringSend = async () => otherResult = await other.RunOnceAsync();

        // Act
        var result = await _sweep.RunOnceAsync();

        // Assert
        Assert.Equal(1, result.EmailsSent);
        Assert.NotNull(otherResult);
        Assert.Equal(0, otherResult!.EmailsSent);
        Assert.Single(_mail.Sent);
        Assert.Empty(_notices.Waiting(_acme));
        Assert.Empty(_notices.Waiting(_beta));
    }

    [Fact]
    public async Task Claim_TwoGatewaysThatBothReadTheNoticeAsWaiting_OnlyOneWins()
    {
        // Arrange: both Gateways have read the same waiting notice; neither has claimed it yet.
        await SendReportAsync(_acme, OwnerA, Mike);
        var id = Assert.Single(_notices.Waiting(_acme)).Id;
        var other = new ContributorNotices(_db, new TeamRegistry(_db, new TenantRegistry(_db), () => _now), () => _now);

        // Act
        var first = _notices.Claim(_acme, id, _now, _now + ContributorNoticeSweep.ClaimHold);
        var second = other.Claim(_acme, id, _now, _now + ContributorNoticeSweep.ClaimHold);

        // Assert: one claim, and the notice is free again once the hold has passed.
        Assert.True(first);
        Assert.False(second);
        var later = _now + ContributorNoticeSweep.ClaimHold;
        Assert.True(other.Claim(_acme, id, later, later + ContributorNoticeSweep.ClaimHold));
    }

    [Fact]
    public void ClaimDay_TwoGatewaysForOneDay_OnlyOneWins_AndAReleasedDayCanBeClaimedAgain()
    {
        // Arrange
        Assert.NotNull(_notices.SetChoice(Own(Mike), ContributorNoticeModes.Daily, 8).Choice);
        var other = new ContributorNotices(_db, new TeamRegistry(_db, new TenantRegistry(_db), () => _now), () => _now);

        // Act
        var first = _notices.ClaimDay(Own(Mike), "2026-11-09");
        var second = other.ClaimDay(Own(Mike), "2026-11-09");
        _notices.ReleaseDay(Own(Mike), "2026-11-09", null);
        var afterRelease = other.ClaimDay(Own(Mike), "2026-11-09");

        // Assert
        Assert.True(first);
        Assert.False(second);
        Assert.True(afterRelease);
        Assert.True(_notices.ClaimDay(Own(Mike), "2026-11-10"));
    }

    [Fact]
    public async Task RunOnce_DailySendFails_TriedAgainInsideTheHourAndSentOnce()
    {
        // Arrange
        Assert.NotNull(_notices.SetChoice(Own(Mike), ContributorNoticeModes.Daily, 8).Choice);
        await SendReportAsync(_acme, OwnerA, Mike);
        _now = new DateTime(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc);
        _mail.Fail = true;

        // Act / Assert: the failed send gives the day back, so the try after the wait still sends today, once.
        Assert.Equal(1, (await _sweep.RunOnceAsync()).Failed);
        Assert.Null(_notices.ChoiceOf(Own(Mike)).DailyHandledLocalDate);
        _mail.Fail = false;
        _now = _now.AddMinutes(1);
        Assert.Equal(1, (await _sweep.RunOnceAsync()).EmailsSent);
        _now = _now.AddMinutes(5);
        Assert.Equal(0, (await _sweep.RunOnceAsync()).EmailsSent);
        Assert.Equal("2026-11-09", _notices.ChoiceOf(Own(Mike)).DailyHandledLocalDate);
        Assert.Single(_mail.Sent);
    }

    [Fact]
    public async Task RunOnce_EmailServiceRefusesForGood_SettledAsUndeliverableAndNotTriedAgain()
    {
        // Arrange
        await SendReportAsync(_acme, OwnerA, Mike);
        _mail.Refuse = true;

        // Act
        var first = await _sweep.RunOnceAsync();
        _now = _now.AddHours(2);
        var later = await _sweep.RunOnceAsync();

        // Assert
        Assert.Equal(1, first.Settled);
        Assert.Equal(0, first.Failed);
        Assert.Empty(_notices.Waiting(_acme));
        Assert.Equal(0, later.Settled + later.Failed + later.EmailsSent);
        Assert.Equal(1, _mail.Tries);
        using var ctx = _db.CreateContext(new TenantId(_acme));
        Assert.Equal(ContributorNoticeOutcomes.Undeliverable, Assert.Single(ctx.ContributorNotices).Outcome);
    }

    [Fact]
    public async Task RunOnce_NoPublicAddress_SendsNothingAndKeepsWaiting()
    {
        // Arrange
        await SendReportAsync(_acme, OwnerA, Mike);
        _publicBase = null;

        // Act
        var result = await _sweep.RunOnceAsync();

        // Assert
        Assert.Equal(0, result.EmailsSent);
        Assert.Single(_notices.Waiting(_acme));
        Assert.Equal(0, _mail.Tries);
    }

    [Theory]
    [InlineData(1, null, 1)]
    [InlineData(3, null, 4)]
    [InlineData(20, null, 60)]
    [InlineData(1, 300, 5)]
    public void Backoff_FailedTries_DoublesUpToAnHourOrTheServicesWait(int tries, int? retryAfterSeconds, int expectedMinutes)
    {
        // Arrange / Act
        var wait = ContributorNoticeSweep.Backoff(tries, retryAfterSeconds is { } s ? TimeSpan.FromSeconds(s) : null);

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), wait);
    }

    // ---------------------------------------------------------------- the choice and its card

    [Fact]
    public void ChoiceOf_NeverChosen_IsRightAwayAtEight()
    {
        // Arrange / Act
        var choice = _notices.ChoiceOf(Own(Mike));

        // Assert
        Assert.Equal(ContributorNoticeModes.Now, choice.Mode);
        Assert.Equal(8, choice.DailyHour);
        Assert.False(choice.Chosen);
    }

    [Theory]
    [InlineData("sometimes", 8, ContributorNotices.NotAModeRefusal)]
    [InlineData(null, 8, ContributorNotices.NotAModeRefusal)]
    [InlineData("daily", 24, ContributorNotices.NotAnHourRefusal)]
    [InlineData("daily", -1, ContributorNotices.NotAnHourRefusal)]
    public void SetChoice_NotOneTheCardOffers_IsRefusedWithTheSentence(string? mode, int hour, string refusal)
    {
        // Arrange / Act
        var result = _notices.SetChoice(Own(Mike), mode, hour);

        // Assert
        Assert.Null(result.Choice);
        Assert.Equal(refusal, result.Refusal);
        Assert.False(_notices.ChoiceOf(Own(Mike)).Chosen);
    }

    [Fact]
    public void SetChoice_OnePersonsChoice_DoesNotChangeAnothers()
    {
        // Arrange / Act
        _notices.SetChoice(Own(Mike), ContributorNoticeModes.Never, null);

        // Assert
        Assert.Equal(ContributorNoticeModes.Never, _notices.ChoiceOf(Own(Mike)).Mode);
        Assert.Equal(ContributorNoticeModes.Now, _notices.ChoiceOf(Own(Nina)).Mode);
    }

    [Fact]
    public void Card_Daily_ShowsTheHourAndEveryWordFromTheGateway()
    {
        // Arrange
        var choice = new ContributorNoticeChoice(ContributorNoticeModes.Daily, 17, null, true);

        // Act
        var card = JsonSerializer.SerializeToElement(ContributorNoticeCard.For(choice, applies: true, "Europe/Copenhagen",
            ContributorNoticeCard.Saved(choice)));

        // Assert
        Assert.True(card.GetProperty("applies").GetBoolean());
        Assert.True(card.GetProperty("showHour").GetBoolean());
        Assert.Equal(17, card.GetProperty("dailyHour").GetInt32());
        Assert.Equal(24, card.GetProperty("hours").GetArrayLength());
        Assert.Equal("17:00", card.GetProperty("hours")[17].GetProperty("label").GetString());
        Assert.Equal(new[] { "Email me right away", "Once a day, in one email", "Never - I will check myself" },
            card.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("label").GetString()));
        Assert.True(card.GetProperty("options")[1].GetProperty("selected").GetBoolean());
        Assert.Equal("Times are in your own time zone, Europe/Copenhagen.", card.GetProperty("timeZoneLine").GetString());
        Assert.Equal("Saved. You get one email a day at 17:00, only when something is waiting.", card.GetProperty("savedLine").GetString());
    }

    [Fact]
    public void Card_RightAway_HidesTheHour()
    {
        // Arrange / Act
        var card = JsonSerializer.SerializeToElement(ContributorNoticeCard.For(ContributorNotices.Default, applies: false, "UTC", null));

        // Assert
        Assert.False(card.GetProperty("showHour").GetBoolean());
        Assert.False(card.GetProperty("applies").GetBoolean());
        Assert.Equal(JsonValueKind.Null, card.GetProperty("savedLine").ValueKind);
    }

    // ---------------------------------------------------------------- helpers

    private TenantId Own(string subject) => _tenants.LookupBySubject(subject)!.Value;

    /// <summary>The sweep a second Gateway runs over the same database (its own notices and registries, as a second
    /// process would have), sending through the same recording sender so every email is counted once.</summary>
    private ContributorNoticeSweep SecondGatewaySweep()
    {
        var tenants = new TenantRegistry(_db);
        var teams = new TeamRegistry(_db, tenants, () => _now);
        return new ContributorNoticeSweep(_db, new ContributorNotices(_db, teams, () => _now), teams, tenants, _mail,
            t => _zones.TryGetValue(t.Value, out var z) ? z : "UTC", () => _publicBase, () => _now);
    }

    /// <summary>A report written in <paramref name="team"/> by <paramref name="author"/>, its title, text and one question
    /// carrying markers, sent through the real send route's answer to <paramref name="to"/>.</summary>
    private async Task SendReportAsync(string team, string author, params string[] to)
    {
        var html =
            $"<header data-dev-report=\"header\" data-dev-report-status=\"waiting-on-you\"><h1>Menu {TitleMarker}</h1></header>" +
            $"<section data-dev-report=\"summary\"><p>The menu is ready. {ReportMarker}</p></section>" +
            "<section data-dev-report=\"questions\">" +
            $"<div data-dev-report-question=\"prices\" data-dev-report-question-text=\"Show prices? {QuestionMarker}\" data-dev-report-for=\"contributors\">" +
            "<label><input type=\"radio\" name=\"prices\" value=\"yes\" data-recommended> Yes</label>" +
            "<label><input type=\"radio\" name=\"prices\" value=\"no\"> No</label>" +
            "<textarea data-dev-report-comment placeholder=\"Anything to add (optional)\"></textarea></div>" +
            $"</section><section data-dev-report=\"detail\"><p>{ReportMarker} detail</p></section>";
        var report = _store.Publish(new TenantId(team), Guid.NewGuid().ToString("D"), @"C:\work\" + Guid.NewGuid().ToString("N") + ".html",
            html, "waiting-on-you", "Menu " + TitleMarker, _now, author).Report;
        var (status, body) = await Answer(_reports.Send(team, author, report.Id.ToString("D"),
            to.Select(s => TeamMemberIds.For(team, s)).ToList(), 1));
        Assert.True(status == 200, body.ToString());
    }

    private async Task ShareAsync(string contributor)
    {
        var (status, body) = await Answer(_shares.Share(_acme, OwnerA, "website", TeamMemberIds.For(_acme, contributor)));
        Assert.True(status == 200, body.ToString());
    }

    private static async Task<(int Status, JsonElement Body)> Answer(IResult result)
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        ctx.Response.Body = new MemoryStream();
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        var text = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        return (ctx.Response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    /// <summary>Records every email the sweep hands over, to whom; fails while <see cref="Fail"/> is set.</summary>
    private sealed class RecordingSender : IPersonEmailSender
    {
        public List<(string To, PersonEmail Email)> Sent { get; } = new();
        public bool Fail { get; set; }
        public bool Refuse { get; set; }
        public int Tries { get; private set; }

        /// <summary>Run once inside the next send, before it is recorded - another Gateway's pass while this one's send is
        /// in flight.</summary>
        public Func<Task>? DuringSend { get; set; }

        public async Task<PersonEmailResult> SendAsync(string accountSubject, PersonEmail email, CancellationToken ct = default)
        {
            Tries++;
            if (DuringSend is { } during)
            {
                DuringSend = null;
                await during();
            }
            if (Refuse)
                return new PersonEmailResult(false, PersonEmailSender.Refused(404), Permanent: true);
            if (Fail)
                return new PersonEmailResult(false, "The DevThrottle email service could not be reached.");
            Sent.Add((accountSubject, email));
            return new PersonEmailResult(true, null);
        }
    }
}
