using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Mobile;
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
/// THE CONTRIBUTOR'S WELCOME (Team implementation piece 4.2, devthrottle_internal#2799), over a real database: the words
/// are the owner's approved words exactly, the welcome is where a contributor's app opens until they have seen it and
/// never after, it is shown once per person (a second team does not show it again), and only a contributor is ever
/// given it - an Owner, a Developer or someone outside the team never is.
///
/// Two teams: Acme (Owner A; contributor Mike; Developer Dev) and Beta (Owner B; contributor Mike).
/// </summary>
public sealed class ContributorWelcomeTests : IDisposable
{
    /// <summary>
    /// The owner's approved words, copied byte for byte from cc-consult writing/2026-11-09-contributor-welcome-page/shipped.md
    /// (approved 10 November 2026: "Yes, build it as shown", step two reading "It is checked first."). A change here is a
    /// change to approved words and needs the owner's approval first.
    /// </summary>
    private const string ShippedMd = """
        # Welcome to the team

        A factory is a group of workers that builds something for you.

        1. **You send a request.** Say what you want in your own words, like "Make me a website for my bakery."
        2. **It is checked first.** The team's owner reads every request before any work starts.
        3. **The factory starts.** You get an email to say so.
        4. **You can watch it work.** Watch your factory shows the workers on your request as a drawing, live or as a replay.
        5. **You are told when it is finished.** The result is waiting in your Inbox.

        Allow notifications to hear on your phone or in your browser too.

        What should your factory build first?

        [Send a request]

        """;

    private const string OwnerA = "sub-owner-a";
    private const string OwnerB = "sub-owner-b";
    private const string Mike = "sub-mike";
    private const string Dev = "sub-dev";
    private const string Stranger = "sub-stranger";
    private static readonly DateTime Now = new(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly TenantRegistry _tenants;
    private readonly TeamRegistry _teams;
    private readonly TenantSettingsStore _settings;
    private readonly ContributorWelcome _welcome;
    private readonly TeamEndpointGate _gate;
    private readonly string _acme;
    private readonly string _beta;

    public ContributorWelcomeTests()
    {
        _db = _harness.Open();
        _tenants = new TenantRegistry(_db);
        foreach (var subject in new[] { OwnerA, OwnerB, Mike, Dev, Stranger })
            _tenants.MintOrLookupBySubject(subject, $"{subject}@example.com");
        _teams = new TeamRegistry(_db, _tenants);
        _settings = new TenantSettingsStore(_db);
        _welcome = new ContributorWelcome(_settings, _tenants, () => Now);
        var devices = new DeviceRegistry(_db, _harness.LegacyPath("devices.json"), isHosted: true, teamsReleased: true);
        var boundary = new HostedTenantBoundary(new AsyncLocalTenantContext(), devices, hosted: false);
        _gate = new TeamEndpointGate(new TeamAccess(_teams), _teams, _tenants, boundary);

        _acme = _teams.CreateTeam(OwnerA, "Acme").Team!.TeamId;
        _beta = _teams.CreateTeam(OwnerB, "Beta").Team!.TeamId;
        Assert.True(_teams.AddMember(_acme, Mike, TeamRole.Collaborator).IsDone);
        Assert.True(_teams.AddMember(_acme, Dev, TeamRole.Developer).IsDone);
        Assert.True(_teams.AddMember(_beta, Mike, TeamRole.Collaborator).IsDone);
    }

    public void Dispose() => _harness.Dispose();

    // ---------------------------------------------------------------- the words

    [Fact]
    public async Task Welcome_TheServedWordsRebuiltAsShippedMd_AreTheApprovedWordsExactly()
    {
        var (status, page) = await Render(ContributorEndpoints.Welcome(_teams, _acme, Mike));

        Assert.Equal(200, status);
        // Rebuilt from what reaches the screen, in shipped.md's own layout, and compared character for character: any
        // changed word, mark or added sentence on the Gateway fails here.
        var md = new StringBuilder();
        md.Append("# ").Append(page.GetProperty("title").GetString()).Append("\n\n");
        md.Append(page.GetProperty("lead").GetString()).Append("\n\n");
        foreach (var step in page.GetProperty("steps").EnumerateArray())
            md.Append(step.GetProperty("number").GetInt32()).Append(". **").Append(step.GetProperty("title").GetString())
                .Append("** ").Append(step.GetProperty("text").GetString()).Append('\n');
        md.Append('\n').Append(page.GetProperty("notifications").GetString()).Append("\n\n");
        md.Append(page.GetProperty("question").GetString()).Append("\n\n");
        md.Append('[').Append(page.GetProperty("button").GetProperty("label").GetString()).Append("]\n");

        Assert.Equal(ShippedMd.ReplaceLineEndings("\n"), md.ToString());
    }

    [Fact]
    public async Task Welcome_AContributor_GetsTheirTeamsNameStepOneAsTheirsAndTheWayToWriteToTheOwner()
    {
        var (_, page) = await Render(ContributorEndpoints.Welcome(_teams, _beta, Mike));

        Assert.Equal("Beta", page.GetProperty("teamLabel").GetString());
        var steps = page.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(new[] { true, false, false, false, false }, steps.Select(s => s.GetProperty("yours").GetBoolean()));
        Assert.Equal(new[] { false, false, false, true, false }, steps.Select(s => s.GetProperty("watch").GetBoolean()));
        Assert.Equal("Watch your factory", page.GetProperty("watchLabel").GetString());
        // "Send a request" opens the Inbox's write box on THIS team's Owner (piece 4.1's write path).
        Assert.Equal("/contributor/inbox?team=" + Uri.EscapeDataString(_beta), page.GetProperty("button").GetProperty("href").GetString());
    }

    // ---------------------------------------------------------------- shown once, only to a contributor

    [Fact]
    public async Task ListTeams_AContributorWhoHasNotSeenIt_OpensOnTheWelcome_WithItInTheMenu()
    {
        var app = await AppIn(_acme, Mike, _welcome.HasSeen(Mike));

        Assert.Equal(ContributorWelcome.PagePath, app.GetProperty("landing").GetString());
        var welcome = app.GetProperty("pages").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "welcome");
        Assert.Equal(ContributorWelcome.PagePath, welcome.GetProperty("path").GetString());
        Assert.True(welcome.GetProperty("inMenu").GetBoolean());
    }

    [Fact]
    public async Task ListTeams_AfterTheWelcomeWasSeen_OpensOnFactories_AndTheWelcomeLeavesTheMenuButStillOpens()
    {
        var (status, _) = await Render(ContributorEndpoints.WelcomeSeen(_teams, _welcome, _acme, Mike));

        var app = await AppIn(_acme, Mike, _welcome.HasSeen(Mike));

        Assert.Equal(200, status);
        Assert.Equal("/contributor/factories", app.GetProperty("landing").GetString());
        var pages = app.GetProperty("pages").EnumerateArray().ToList();
        // Still one of the pages, so a mailed link to it opens it - but no longer offered in the menu.
        Assert.False(pages.Single(p => p.GetProperty("id").GetString() == "welcome").GetProperty("inMenu").GetBoolean());
        Assert.All(pages.Where(p => p.GetProperty("id").GetString() != "welcome"), p => Assert.True(p.GetProperty("inMenu").GetBoolean()));
    }

    [Fact]
    public async Task WelcomeSeen_OnOneTeam_IsSeenOnEveryTeam_BecauseItIsShownOncePerPerson()
    {
        await Render(ContributorEndpoints.WelcomeSeen(_teams, _welcome, _acme, Mike));

        var beta = await AppIn(_beta, Mike, _welcome.HasSeen(Mike));

        Assert.Equal("/contributor/factories", beta.GetProperty("landing").GetString());
    }

    [Fact]
    public void MarkSeen_Twice_KeepsTheFirstMark()
    {
        _welcome.MarkSeen(Mike);
        var later = new ContributorWelcome(_settings, _tenants, () => Now.AddDays(3));

        later.MarkSeen(Mike);

        var own = _tenants.LookupBySubject(Mike)!.Value;
        Assert.Equal(Now, DateTime.Parse(_settings.Get(own, TenantSettingKeys.ContributorWelcomeSeen)!,
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    [Fact]
    public void HasSeen_ADifferentPersonsMark_DoesNotCount()
    {
        _welcome.MarkSeen(Mike);

        Assert.False(new ContributorWelcome(_settings, _tenants).HasSeen(Stranger));
    }

    [Theory]
    [InlineData(OwnerA)]
    [InlineData(Dev)]
    public async Task ListTeams_ARoleWithTheWholeApp_NeverHasTheWelcome(string person)
    {
        var app = await AppIn(_acme, person, _welcome.HasSeen(person));

        Assert.True(app.GetProperty("full").GetBoolean());
        Assert.DoesNotContain(app.GetProperty("pages").EnumerateArray(), p => p.GetProperty("id").GetString() == "welcome");
    }

    [Theory]
    [InlineData(OwnerA)]
    [InlineData(Dev)]
    public async Task WelcomeRoutes_ARoleWithTheWholeApp_IsNotFound_AndNothingIsMarked(string person)
    {
        var (getStatus, getBody) = await Render(ContributorEndpoints.Welcome(_teams, _acme, person));
        var (postStatus, _) = await Render(ContributorEndpoints.WelcomeSeen(_teams, _welcome, _acme, person));

        Assert.Equal((404, 404), (getStatus, postStatus));
        Assert.Equal(ContributorWelcome.NotForThisRole, getBody.GetProperty("error").GetString());
        Assert.False(_welcome.HasSeen(person));
    }

    // ---------------------------------------------------------------- the team gate, the session-key guard, the phone

    [Theory]
    [InlineData("GET", ContributorEndpoints.WelcomePattern)]
    [InlineData("POST", ContributorEndpoints.WelcomeSeenPattern)]
    public void Gate_TheWelcomeRoutes_AreAllowedForAContributor(string method, string pattern)
    {
        Assert.Equal(TeamGateOutcome.Allowed, GateFor(method, pattern, _acme, Mike).Outcome);
    }

    [Theory]
    [InlineData("GET", ContributorEndpoints.WelcomePattern)]
    [InlineData("POST", ContributorEndpoints.WelcomeSeenPattern)]
    public void Gate_TheWelcomeRoutes_ForSomeoneOutsideTheTeam_AreNoSuchTeam(string method, string pattern)
    {
        Assert.Equal(TeamGateOutcome.NoSuchTeam, GateFor(method, pattern, _acme, Stranger).Outcome);
    }

    [Fact]
    public void Gate_AWriteToTheWelcomePageItself_IsRefusedAsUndeclared()
    {
        // Only the "seen" mark is a declared write; the page's own address is read-only.
        Assert.Equal(TeamGateOutcome.Refused, GateFor("POST", ContributorEndpoints.WelcomePattern, _acme, Mike).Outcome);
    }

    [Theory]
    [InlineData("GET", "/teams/3f1d2c9e-0000-4000-8000-000000000001/contributor/welcome")]
    [InlineData("POST", "/teams/3f1d2c9e-0000-4000-8000-000000000001/contributor/welcome/seen")]
    public void SessionKeyGuard_TheWelcomeRoutes_AreRefusedToAnAgentSessionKey(string method, string path)
    {
        Assert.False(CcDirector.Gateway.Util.SessionKeyGuard.Check(method, path).Allowed);
        Assert.False(CcDirector.Gateway.Util.SessionKeyGuard.Check(method, path, raised: true).Allowed);
    }

    [Fact]
    public void MobileRedirect_APhoneAtTheWelcome_IsGivenTheCockpitPage_WhileTeamsIsReleased()
    {
        const string phone = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) Mobile/15E148";

        Assert.False(MobileRedirect.ShouldRedirectToMobile("GET", ContributorWelcome.PagePath, "text/html", phone, teamsReleased: true));
        Assert.True(MobileRedirect.ShouldRedirectToMobile("GET", ContributorWelcome.PagePath, "text/html", phone, teamsReleased: false));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The page verdict GET /teams gives <paramref name="person"/> for one team.</summary>
    private async Task<JsonElement> AppIn(string team, string person, bool welcomed)
    {
        var (_, body) = await Render(TeamEndpoints.ListTeams(_teams, person, ownAccountHasADirector: false, welcomed));
        return body.GetProperty("teams").EnumerateArray().Single(t => t.GetProperty("id").GetString() == team).GetProperty("app");
    }

    private TeamGateVerdict GateFor(string method, string pattern, string team, string person) =>
        _gate.Check(method, pattern, n => n == "teamId" ? team : null, _tenants.MintOrLookupBySubject(person, null), () => person,
            _ => TeamOwnership.Callers);

    private static async Task<(int Status, JsonElement Body)> Render(IResult result)
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        ctx.Response.Body = new MemoryStream();
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        var text = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        return (ctx.Response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }
}
