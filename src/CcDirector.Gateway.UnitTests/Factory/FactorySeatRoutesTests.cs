using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Registry;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Tests.Factory.Registry;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory;

/// <summary>
/// THE SEAT PAGE AND "TAKE OFF THIS SEAT" OVER REAL HTTP (Factory Seats mission, phase 1), against the real schedule
/// store, the real factory registry and the real activity record on one database - so what is proven is the whole
/// chain the owner's screen depends on: a schedule taken off a seat is really gone from that seat's list (which the
/// registry DERIVES from the schedules), the seat's page then reads talk only, the factory with nothing left
/// scheduled reads TALK ONLY, and the schedule itself is still there, switched as it was.
///
/// A refusal is asserted by the response that carries it AND by the store being unchanged afterwards.
/// </summary>
[Trait("Category", "FactoryRegistry")]
public sealed class FactorySeatRoutesTests : IAsyncDisposable
{
    private static readonly TenantId T = TenantId.Local;
    private static readonly DateTime Now = new(2026, 10, 13, 15, 0, 0, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string SessionId = "11111111-2222-4333-8444-555555555555";

    public enum Caller { OwnerBrowser, SessionKey, MachineToken }

    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase _db = null!;
    private CronJobStore _schedules = null!;
    private FactoryRegistryStore _registry = null!;
    private FactoryActivityRecord _record = null!;
    private WebApplication? _app;
    private HttpClient _http = null!;
    private int _memoryNotes;

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        if (_app is not null) await _app.DisposeAsync();
        _h.Dispose();
    }

    private static RegisterFactoryRequest WarmForward() => new()
    {
        Factory = "warmforward",
        Title = "WarmForward",
        Folder = "/home/soren/factories/warmforward",
        Computer = "devlinux",
        BossSeat = "boss",
        GoalText = "Cut the propane bill by a third this season.",
        Seats =
        {
            new FactorySeatManifest { Id = "boss", Name = "Boss", Role = "Boss", BriefFile = "agents/boss.yaml" },
            new FactorySeatManifest { Id = "scout", Name = "Scout", Role = "Scout", BriefFile = "agents/scout.yaml" },
        },
    };

    /// <param name="caller">Who every request of this test comes from.</param>
    /// <param name="sessionFactory">The factory the calling session is in, when <paramref name="caller"/> is a session.</param>
    private async Task StartAsync(Caller caller = Caller.OwnerBrowser, SessionFactoryLookup? sessionFactory = null)
    {
        _db = _h.Open();
        _schedules = new CronJobStore(_db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json"));
        _registry = new FactoryRegistryStore(_db);
        _record = new FactoryActivityRecord(_db);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.Use(async (ctx, next) =>
        {
            ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "credential";
            if (caller == Caller.SessionKey)
                ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] =
                    new SessionCredentialIdentity(Guid.Parse(SessionId), T, "dir-1");
            if (caller == Caller.OwnerBrowser)
            {
                ctx.Items[AuthMiddleware.DeviceTypeItemKey] = "browser";
                ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] =
                    new DeviceCredentialIdentity("device-1", T.Value, "browser", "active");
            }
            await next();
        });

        var runs = new CronRunHistoryStore(_db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".runs.json"));
        var runRecords = new CronRunRecordReader(runs, new SessionHistoryStore(_db).EndingsOf);
        CronJobEndpoints.Map(app, _schedules,
            sessionFactoryOf: _ => sessionFactory ?? SessionFactoryLookup.NotKnown,
            findFactory: (_, id) => _registry.Find(T, id),
            runRecords: runRecords,
            accountZoneOf: _ => TimeZoneInfo.Utc);

        var settings = new TenantSettingsStore(_db);
        var record = _record;
        var sources = new FactoriesScreenSources(
            new FactoryAgentsSources(
                Query: (tenant, q) => record.Query(tenant, q.Factory, q.Agent, q.Outcome, q.FromUtc, q.ToUtc, q.OldestFirst, q.Offset, q.Limit),
                Append: (tenant, request, actor) => record.Append(tenant, request, actor),
                Triggers: _ => Array.Empty<FactoryTriggerFacts>(),
                SetTriggerPaused: (_, _, _, _, _) => Task.FromResult(false),
                LiveSessionIds: _ => new HashSet<string>(),
                TimeZone: _ => TimeZoneInfo.Utc,
                NowUtc: () => Now,
                Reports: new FactoryReportStore(settings),
                Maps: new FactoryMapStore(settings)),
            _registry,
            tenant => _schedules.ListAll(tenant),
            (_, _, _) => new Dictionary<string, IReadOnlyList<CronRunRecord>>(),
            (_, f) => new CronRunProblemsDto { Factory = f },
            (_, f) => new FactoryEmailsDto { Factory = f });
        FactoriesScreenEndpoints.Map(app, _ => T, sources);
        FactoriesScreenEndpoints.MapSeatPage(app, _ => T, sources, (_, _) => _memoryNotes);

        await app.StartAsync();
        _app = app;
        _http = new HttpClient { BaseAddress = new Uri(app.Urls.First() + "/") };
    }

    /// <summary>Register WarmForward with the Scout run by <c>cj_scout</c>, and one plain job in no factory.</summary>
    private void Seed(bool scoutEnabled = true)
    {
        ScheduleLinkSeed.Link(_db, T, "warmforward", "scout", "cj_scout", enabled: scoutEnabled, name: "WarmForward Factory - Scout - daily scan");
        ScheduleLinkSeed.Link(_db, T, factory: null, seat: null, "cj_plain", name: "Daily job search");
        _registry.Register(T, WarmForward(), "the owner (test)", Now.AddDays(-3));
    }

    private Task<HttpResponseMessage> Unlink(string id) => _http.PostAsync($"cron/jobs/{id}/unlink", content: null);

    private static async Task<string> ErrorOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString() ?? "";

    private async Task<FactorySeatPageViewDto> SeatPage(string seat) =>
        (await _http.GetFromJsonAsync<FactorySeatPageViewDto>($"gateway/factories/warmforward/seats/{seat}", Web))!;

    // ---------- the seat page ----------

    [Fact]
    public async Task SeatPage_ASeatWithASchedule_AndASeatWithNone_BothOpen()
    {
        await StartAsync();
        Seed();
        _memoryNotes = 2;

        var scout = await SeatPage("scout");
        Assert.Equal("Scheduled", scout.StartsWord);
        var schedule = Assert.Single(scout.Starts.Schedules);
        Assert.Equal("cj_scout", schedule.JobId);
        Assert.Equal("Daily 07:00", schedule.WhenText);
        Assert.Equal("Take off this seat", schedule.Unlink!.Label);
        Assert.Equal("/home/soren/factories/warmforward/agents/scout.yaml", scout.Brief.PathText);
        Assert.Equal("devlinux", scout.Brief.ComputerText);
        Assert.StartsWith("2 notes in the factory's memory.", scout.LeftBehind.MemoryText);

        // The boss has nothing scheduled, and its page is whole all the same.
        var boss = await SeatPage("boss");
        Assert.Equal("Boss", boss.Name);
        Assert.Equal("Talk only", boss.StartsWord);
        Assert.Equal("Talk only. Nothing is scheduled for this seat. You run it by talking to it.", boss.Starts.TalkOnlyText);
        Assert.Empty(boss.Starts.Schedules);
        Assert.Equal("/home/soren/factories/warmforward/agents/boss.yaml", boss.Brief.PathText);
        Assert.Equal("Cut the propane bill by a third this season.", boss.Goal.Text);
        Assert.Equal("None yet.", boss.LeftBehind.LastTalkText);
    }

    [Fact]
    public async Task SeatPage_TheLastTalkIsTheSeatsOwn_NotTheFactorysNewest()
    {
        await StartAsync();
        Seed();
        _record.Append(T, new AppendFactoryActivityRequest
        {
            Factory = "warmforward", FactoryAgent = "scout", Outcome = FactoryActivityOutcome.Talked,
            What = "agreed to scan twice a day", OccurredUtc = Now.AddDays(-20),
        }, "session:test");
        _record.Append(T, new AppendFactoryActivityRequest
        {
            Factory = "warmforward", FactoryAgent = "boss", Outcome = FactoryActivityOutcome.Talked,
            What = "the boss talked later", OccurredUtc = Now.AddHours(-1),
        }, "session:test");

        Assert.Equal("Talked with you, 23 Sep 15:00 - agreed to scan twice a day", (await SeatPage("scout")).LeftBehind.LastTalkText);
        Assert.Equal("Talked with you, today 14:00 - the boss talked later", (await SeatPage("boss")).LeftBehind.LastTalkText);
    }

    [Fact]
    public async Task SeatPage_ASeatTheFactoryDoesNotHave_Is404_NamingItsSeats()
    {
        await StartAsync();
        Seed();

        var response = await _http.GetAsync("gateway/factories/warmforward/seats/owner-session");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("WarmForward has no seat 'owner-session'. Its seats are: boss, scout. Only a registered seat has a page; " +
                     "what any other session wrote is on the factory's Activity tab.", await ErrorOf(response));
    }

    [Fact]
    public async Task SeatPage_AFactoryThatIsNotRegistered_Is404()
    {
        await StartAsync();
        Seed();

        var response = await _http.GetAsync("gateway/factories/nowhere/seats/boss");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("There is no registered factory 'nowhere'", await ErrorOf(response));
    }

    [Fact]
    public async Task SeatPage_IsTheOwners_ASessionKeyIsRefused()
    {
        await StartAsync(Caller.SessionKey, SessionFactoryLookup.In("warmforward"));
        Seed();

        var response = await _http.GetAsync("gateway/factories/warmforward/seats/scout");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------- take a schedule off a seat ----------

    [Fact]
    public async Task Unlink_ByTheOwner_TakesItOffTheSeat_KeepsItSwitchedOn_AndTheFactoryBecomesTalkOnly()
    {
        await StartAsync();
        Seed();
        var before = _schedules.Get("cj_scout")!;
        Assert.Equal("RUNNING", (await _http.GetFromJsonAsync<FactoryPageViewDto>("gateway/factories/warmforward", Web))!.StatusWord);

        var response = await Unlink("cj_scout");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<CronUnlinkResultDto>(Web))!;
        Assert.Equal("Taken off Scout of WarmForward. 'WarmForward Factory - Scout - daily scan' is still switched on and " +
                     "keeps running at its times, as a plain schedule in no factory.", result.Text);
        Assert.Equal("warmforward", result.Factory);
        Assert.Equal("scout", result.Seat);
        Assert.Null(result.Job.Factory);
        Assert.Null(result.Job.Seat);
        Assert.True(result.Job.Enabled);

        // The schedule is still there, switched as it was, with the run it had - only the link is gone.
        var after = _schedules.Get("cj_scout")!;
        Assert.Null(after.Factory);
        Assert.Null(after.Seat);
        Assert.True(after.Enabled);
        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.CronExpression, after.CronExpression);
        Assert.Equal(before.NextRunUtc, after.NextRunUtc);
        Assert.Equal(before.Action.Seed, after.Action.Seed);

        // The registry derives the seat's schedules from the links, so the seat no longer names it...
        Assert.Empty(_registry.Find(T, "warmforward")!.Seats.Single(s => s.Id == "scout").Schedules);
        // ...its page reads talk only...
        var scout = await SeatPage("scout");
        Assert.Equal("Talk only", scout.StartsWord);
        Assert.Empty(scout.Starts.Schedules);
        // ...and the factory, with nothing left scheduled, is TALK ONLY and not PAUSED.
        var page = (await _http.GetFromJsonAsync<FactoryPageViewDto>("gateway/factories/warmforward", Web))!;
        Assert.Equal("TALK ONLY", page.StatusWord);
        Assert.Equal("Nothing scheduled. You run it by talking to it.", page.StatusLine);
        // The schedule that kept running is not hidden: it is listed as a schedule outside any factory.
        var list = (await _http.GetFromJsonAsync<FactoriesListViewDto>("gateway/factories", Web))!;
        Assert.Contains(list.OutsideRows, r => r.Id == "cj_scout" && r.Reason == "In no factory");
    }

    [Fact]
    public async Task Unlink_ASwitchedOffSchedule_StaysSwitchedOff()
    {
        await StartAsync();
        Seed(scoutEnabled: false);

        var response = await Unlink("cj_scout");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<CronUnlinkResultDto>(Web))!;
        Assert.Equal("Taken off Scout of WarmForward. 'WarmForward Factory - Scout - daily scan' is still switched off, and is now in no factory.", result.Text);
        var after = _schedules.Get("cj_scout")!;
        Assert.False(after.Enabled);
        Assert.Null(after.Factory);
    }

    [Fact]
    public async Task Unlink_ByASessionOutsideTheFactory_IsRefusedInThePauseRulesOwnWords_AndNothingChanges()
    {
        await StartAsync(Caller.SessionKey, SessionFactoryLookup.In("invoice-factory"));
        Seed();

        var response = await Unlink("cj_scout");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var error = await ErrorOf(response);
        Assert.Equal("this schedule belongs to factory 'warmforward', so only a person or a session of 'warmforward' may change or run it", error);
        // The same sentence pausing it is refused with: the one rule, not a second one beside it.
        Assert.Equal(FactoryNaming.ActRefusal("schedule", "warmforward"), error);
        var after = _schedules.Get("cj_scout")!;
        Assert.Equal("warmforward", after.Factory);
        Assert.Equal("scout", after.Seat);
        Assert.Equal(new[] { "cj_scout" }, _registry.Find(T, "warmforward")!.Seats.Single(s => s.Id == "scout").Schedules);
    }

    [Fact]
    public async Task Unlink_ByASessionInNoFactory_IsRefused_AndNothingChanges()
    {
        await StartAsync(Caller.SessionKey, SessionFactoryLookup.InNoFactory);
        Seed();

        var response = await Unlink("cj_scout");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(FactoryNaming.ActRefusal("schedule", "warmforward"), await ErrorOf(response));
        Assert.Equal("warmforward", _schedules.Get("cj_scout")!.Factory);
    }

    [Fact]
    public async Task Unlink_ByASessionTheGatewayDoesNotKnowYet_GetsTheTryAgainAnswer_AndNothingChanges()
    {
        await StartAsync(Caller.SessionKey, SessionFactoryLookup.NotKnown);
        Seed();

        var response = await Unlink("cj_scout");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("warmforward", _schedules.Get("cj_scout")!.Factory);
    }

    [Fact]
    public async Task Unlink_ByASessionOfTheFactory_IsAllowed()
    {
        await StartAsync(Caller.SessionKey, SessionFactoryLookup.In("warmforward"));
        Seed();

        var response = await Unlink("cj_scout");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(_schedules.Get("cj_scout")!.Factory);
    }

    [Fact]
    public async Task Unlink_ByTheAccountsMachineToken_IsAllowed_AsPausingIs()
    {
        await StartAsync(Caller.MachineToken);
        Seed();

        Assert.Equal(HttpStatusCode.OK, (await Unlink("cj_scout")).StatusCode);
        Assert.Null(_schedules.Get("cj_scout")!.Factory);
    }

    [Fact]
    public async Task Unlink_AScheduleOnNoSeat_IsRefused_NotAnsweredAsDone()
    {
        await StartAsync();
        Seed();

        var response = await Unlink("cj_plain");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("'Daily job search' is on no seat of any factory, so there is nothing to take off.", await ErrorOf(response));
    }

    [Fact]
    public async Task Unlink_AScheduleThatDoesNotExist_Is404()
    {
        await StartAsync();
        Seed();

        Assert.Equal(HttpStatusCode.NotFound, (await Unlink("cj_nowhere")).StatusCode);
    }

    [Fact]
    public async Task Unlink_AScheduleTheFactorysArchiveSwitchedOff_IsRefused_BecauseRestoreWouldSwitchItBackOn()
    {
        await StartAsync();
        Seed();
        _schedules.SetEnabled(T, "cj_scout", false);
        _registry.Archive(T, "warmforward", "the owner (test)", new[] { "cj_scout" }, Now.AddHours(-1));

        var response = await Unlink("cj_scout");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("WarmForward is archived, and 'WarmForward Factory - Scout - daily scan' is one of the schedules its archive switched off. " +
                     "Restore the factory from the Factories screen first, then take the schedule off.", await ErrorOf(response));
        Assert.Equal("warmforward", _schedules.Get("cj_scout")!.Factory);
    }

    [Theory]
    [InlineData("POST", "/cron/jobs/cj_scout/unlink", true)]
    [InlineData("GET", "/cron/jobs/cj_scout/unlink", false)]
    [InlineData("DELETE", "/cron/jobs/cj_scout/unlink", false)]
    public void SessionKeyGuard_LetsASessionReachUnlink_SoTheFactoryRuleIsWhatAnswersIt(string verb, string path, bool allowed)
    {
        // The refusal a session outside the factory reads must be the factory rule's own sentence (above), which
        // needs the request to reach the route. A blanket refusal at the guard would hide it - and would also stop a
        // factory's own boss taking a schedule off one of its seats.
        Assert.Equal(allowed, SessionKeyGuard.Check(verb, path).Allowed);
    }
}
