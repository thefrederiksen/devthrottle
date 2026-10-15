using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Briefs;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Registry;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Tests.Factory.Definition;
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
    private FactoryBriefStore _briefs = null!;
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
        _briefs = new FactoryBriefStore(_db, _registry);

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
            accountZoneOf: _ => TimeZoneInfo.Utc,
            isDefined: (_, _) => false);

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
        // No factory here has a definition, so the seat page and the brief routes read the brief store, as before phase 4.
        var defined = new FactoryDefinitionBriefs(new FactoryDefinitionStore(_db), _briefs);
        FactoriesScreenEndpoints.MapSeatPage(app, _ => T, sources, (_, _) => _memoryNotes, _briefs, defined);
        // The brief's own routes, as the host maps them: what the brief panel's buttons send.
        FactoryBriefEndpoints.Map(app, _ => T, _briefs, _ => sessionFactory ?? SessionFactoryLookup.NotKnown, () => Now,
            new FactoryBriefEndpoints.Defined(defined,
                new FactoryDefinitionEndpoints.Account(_ => Array.Empty<string>(), _ => Array.Empty<string>(), DefinitionFixture.AskNobody), new FactoryPublishApply(_db)));

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

    // ---------- the brief panel (phase 2b) ----------

    private const string BriefRoute = "factory-briefs/warmforward/scout";
    private const string BriefView = "gateway/factories/warmforward/seats/scout/brief";
    private const string OwnerActor = "device browser device-1";

    private Task<HttpResponseMessage> SaveDraft(string text, int expectedVersion) =>
        _http.PutAsJsonAsync($"{BriefRoute}/draft", new SaveFactoryBriefDraftRequest { Text = text, ExpectedVersion = expectedVersion });

    private Task<HttpResponseMessage> Publish(int version) =>
        _http.PostAsJsonAsync($"{BriefRoute}/publish", new PublishFactoryBriefRequest { Version = version });

    private async Task<T2> Read<T2>(string path) => (await _http.GetFromJsonAsync<T2>(path, Web))!;

    private void SessionDraft(string text, int expectedVersion) =>
        Assert.True(_briefs.SaveDraft(T, "warmforward", "scout", text, expectedVersion,
            CcDirector.Gateway.Data.Entities.FactoryMemoryAuthorKinds.Session, SessionId, Now.AddMinutes(-30)).Ok);

    [Fact]
    public async Task Brief_ASeatWithNoBriefOnTheGateway_KeepsTheFile_OffersToWriteItHere_AndTheEditorStartsEmpty()
    {
        await StartAsync();
        Seed();

        var brief = (await SeatPage("scout")).Brief;
        Assert.Equal(FactorySeatBriefStates.File, brief.State);
        Assert.Equal("/home/soren/factories/warmforward/agents/scout.yaml", brief.PathText);
        Assert.Equal("Write the brief here", brief.EditLabel);
        Assert.Null(brief.HistoryLabel);

        var editor = await Read<FactoryBriefEditorViewDto>($"{BriefView}/editor");
        Assert.Equal("Write the brief of Scout", editor.Heading);
        Assert.Equal("", editor.Text);
        Assert.Equal(0, editor.ExpectedVersion);

        var history = await Read<FactoryBriefHistoryViewDto>($"{BriefView}/history");
        Assert.Empty(history.Rows);
        Assert.Equal("This seat has no brief on the Gateway, so it has no history.", history.EmptyText);
    }

    [Fact]
    public async Task Brief_WrittenSavedPublishedAndBroughtBack_ByAPersonThroughTheRealRoutes_IsWhatTheSeatPageShows()
    {
        await StartAsync();
        Seed();

        // A first draft: the seat has drafts and nothing published, and still says where its file is.
        Assert.Equal(HttpStatusCode.OK, (await SaveDraft("You are the Scout. Scan every morning.", 0)).StatusCode);
        var drafts = (await SeatPage("scout")).Brief;
        Assert.Equal(FactorySeatBriefStates.Drafts, drafts.State);
        Assert.Equal($"This seat has 1 draft on the Gateway and none is published. The newest is version 1, written by a person ({OwnerActor}) today 15:00. " +
                     "Until you publish one, talks and runs of this seat are told the path of its brief file.", drafts.DraftText);
        Assert.Equal("Open the newest draft", drafts.EditLabel);
        Assert.NotNull(drafts.PathText);

        // Published: the page shows the text, its version, who published it and when.
        Assert.Equal(HttpStatusCode.OK, (await Publish(1)).StatusCode);
        var published = (await SeatPage("scout")).Brief;
        Assert.Equal(FactorySeatBriefStates.Published, published.State);
        Assert.Equal("You are the Scout. Scan every morning.", published.Text);
        Assert.Equal($"Version 1, published by a person ({OwnerActor}) today 15:00.", published.VersionText);
        Assert.Null(published.PathText);
        Assert.Null(published.DraftText);

        // A second version, saved and published.
        Assert.Equal(HttpStatusCode.OK, (await SaveDraft("You are the Scout. Scan every morning and every evening.", 1)).StatusCode);
        var waiting = (await SeatPage("scout")).Brief;
        Assert.Equal("You are the Scout. Scan every morning.", waiting.Text);
        Assert.StartsWith("A newer draft, version 2, was written by", waiting.DraftText);
        Assert.Equal(HttpStatusCode.OK, (await Publish(2)).StatusCode);
        Assert.Equal("You are the Scout. Scan every morning and every evening.", (await SeatPage("scout")).Brief.Text);

        // The history offers Bring back on version 1, and pressing it sends what the confirm named.
        var history = await Read<FactoryBriefHistoryViewDto>($"{BriefView}/history");
        Assert.Equal("2 versions. Version 2 is published.", history.SummaryText);
        Assert.Equal(new[] { "Published", "Published earlier" }, history.Rows.Select(r => r.StateWord).ToArray());
        var back = history.Rows[1].BringBack!;
        var restored = await _http.PostAsJsonAsync($"{BriefRoute}/restore",
            new BringBackFactoryBriefRequest { Version = back.Version, ExpectedVersion = back.ExpectedVersion });
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);

        var after = (await SeatPage("scout")).Brief;
        Assert.Equal("You are the Scout. Scan every morning.", after.Text);
        Assert.Equal($"Version 3, published by a person ({OwnerActor}) today 15:00.", after.VersionText);
        // Nothing was removed: all three versions are in the history.
        var kept = await Read<FactoryBriefHistoryViewDto>($"{BriefView}/history");
        Assert.Equal(new[] { 3, 2, 1 }, kept.Rows.Select(r => r.Version).ToArray());
        Assert.Equal(new[] { "Published", "Published earlier", "Published earlier" }, kept.Rows.Select(r => r.StateWord).ToArray());
    }

    [Fact]
    public async Task Brief_AStaleSave_IsRefusedWithTheNewerVersion_NothingIsOverwritten_AndTheEditorThenStartsFromIt()
    {
        await StartAsync();
        Seed();
        Assert.Equal(HttpStatusCode.OK, (await SaveDraft("the owner's first text", 0)).StatusCode);
        var editor = await Read<FactoryBriefEditorViewDto>($"{BriefView}/editor");
        Assert.Equal(1, editor.ExpectedVersion);

        // A session of the factory saves a draft while the owner's editor is open.
        SessionDraft("what the session wrote meanwhile", 1);

        var refused = await SaveDraft("the owner's second text", editor.ExpectedVersion);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("the brief of 'scout' is at version 2, not 1; read it again, merge, and save once more", body.GetProperty("error").GetString());
        Assert.Equal("Stale", body.GetProperty("outcome").GetString());
        // The newer version rides the refusal: its number and its whole text, which the editor shows beside the owner's.
        Assert.Equal(2, body.GetProperty("current").GetProperty("version").GetInt32());
        Assert.Equal("what the session wrote meanwhile", body.GetProperty("current").GetProperty("text").GetString());
        // Nothing was overwritten and nothing was added.
        Assert.Equal(2, _briefs.Standing(T, "warmforward", "scout").Newest!.Version);
        Assert.Equal("what the session wrote meanwhile", _briefs.Find(T, "warmforward", "scout", 2)!.Text);

        // Asked again, the editor starts from the session's draft and says whose it is.
        var again = await Read<FactoryBriefEditorViewDto>($"{BriefView}/editor");
        Assert.Equal(2, again.ExpectedVersion);
        Assert.Equal($"This starts from version 2, the newest draft, written by session {SessionId} today 14:30. Nothing is published yet.", again.StartText);

        // Saving the owner's text after the newer version is then accepted, as version 3.
        var saved = await SaveDraft("the owner's second text, merged", 2);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(3, (await saved.Content.ReadFromJsonAsync<FactoryBriefDto>(Web))!.Version);
    }

    [Fact]
    public async Task Brief_APublishOfTheVersionThatIsAlreadyPublished_IsRefusedInWords_AndNothingChanges()
    {
        await StartAsync();
        Seed();
        await SaveDraft("the brief", 0);
        Assert.Equal(HttpStatusCode.OK, (await Publish(1)).StatusCode);

        var refused = await Publish(1);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("version 1 is already the published brief of 'scout'", await ErrorOf(refused));
        Assert.Equal(1, _briefs.Standing(T, "warmforward", "scout").Newest!.Version);
    }

    [Fact]
    public async Task Brief_APublishByASession_IsRefusedByTheRoute_AndTheSeatPageStillShowsNothingPublished()
    {
        await StartAsync(Caller.SessionKey, SessionFactoryLookup.In("warmforward"));
        Seed();
        Assert.Equal(HttpStatusCode.OK, (await SaveDraft("a session's draft", 0)).StatusCode);

        var refused = await Publish(1);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("Only a person publishes a seat's brief. A session of the factory may save a draft; the owner reads it and publishes it.", await ErrorOf(refused));
        Assert.Null(_briefs.Standing(T, "warmforward", "scout").Published);
    }

    [Fact]
    public async Task BriefHistory_ALongHistory_IsReadOnePageAtATime_NewestFirst_AndEveryVersionIsReached()
    {
        await StartAsync();
        Seed();
        for (var n = 1; n <= 45; n++) SessionDraftOrOwner(n);
        Assert.Equal(HttpStatusCode.OK, (await Publish(45)).StatusCode);

        var first = await Read<FactoryBriefHistoryViewDto>($"{BriefView}/history");
        Assert.Equal("45 versions. Version 45 is published.", first.SummaryText);
        Assert.Equal("Versions 45 to 26 of 45.", first.PageText);
        Assert.Equal(Enumerable.Range(26, 20).Reverse().ToArray(), first.Rows.Select(r => r.Version).ToArray());
        Assert.Equal(26, first.NextBefore);
        Assert.Equal("Older versions", first.OlderLabel);
        Assert.Null(first.NewestLabel);

        var second = await Read<FactoryBriefHistoryViewDto>($"{BriefView}/history?before={first.NextBefore}");
        Assert.Equal("Versions 25 to 6 of 45.", second.PageText);
        Assert.Equal(6, second.NextBefore);
        Assert.Equal("Newest versions", second.NewestLabel);

        var third = await Read<FactoryBriefHistoryViewDto>($"{BriefView}/history?before={second.NextBefore}");
        Assert.Equal("Versions 5 to 1 of 45.", third.PageText);
        Assert.Equal(new[] { 5, 4, 3, 2, 1 }, third.Rows.Select(r => r.Version).ToArray());
        // The Gateway says there is no older page; the screen never works it out.
        Assert.Null(third.NextBefore);
        Assert.Null(third.OlderLabel);

        // No page carries a version's text; Open reads one version.
        var one = await Read<FactoryBriefVersionViewDto>($"{BriefView}/versions/7");
        Assert.Equal("Version 7", one.Title);
        Assert.Equal("text of version 7", one.Text);
        Assert.StartsWith("Draft. Written by a person (device browser device-1) today 15:00.", one.LineText);
    }

    // A person's drafts are not bound by the limit on waiting drafts, so a long history is written as the owner.
    private void SessionDraftOrOwner(int version) =>
        Assert.True(_briefs.SaveDraft(T, "warmforward", "scout", $"text of version {version}", version - 1,
            CcDirector.Gateway.Data.Entities.FactoryMemoryAuthorKinds.Person, OwnerActor, Now).Ok);

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("newest")]
    public async Task BriefHistory_AStartingVersionThatIsNotOne_IsRefusedInWords_NeverQuietlyReplaced(string before)
    {
        await StartAsync();
        Seed();

        var response = await _http.GetAsync($"{BriefView}/history?before={before}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal($"before is the version a page of the history starts below: a whole number from 1, not '{before}'", await ErrorOf(response));
    }

    [Fact]
    public async Task BriefVersion_AVersionThatDoesNotExist_Is404_AndSaysWhichIsNewest()
    {
        await StartAsync();
        Seed();
        await SaveDraft("the brief", 0);

        var missing = await _http.GetAsync($"{BriefView}/versions/9");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("version 9 of the brief of 'scout' does not exist; its newest version is 1", await ErrorOf(missing));

        var notANumber = await _http.GetAsync($"{BriefView}/versions/latest");
        Assert.Equal(HttpStatusCode.BadRequest, notANumber.StatusCode);
        Assert.Equal("a version is a whole number from 1, not 'latest'", await ErrorOf(notANumber));
    }

    [Fact]
    public async Task Brief_TwentyDraftsWaiting_IsSaidOnThePageTheEditorAndTheHistory_AndAPersonsPublishMakesRoom()
    {
        await StartAsync();
        Seed();
        for (var n = 1; n <= 20; n++) SessionDraft($"draft {n}", n - 1);
        // The store refuses the session's twenty-first, in its own sentence.
        var refused = _briefs.SaveDraft(T, "warmforward", "scout", "draft 21", 20,
            CcDirector.Gateway.Data.Entities.FactoryMemoryAuthorKinds.Session, SessionId, Now);
        Assert.Equal(FactoryBriefOutcome.TooManyDrafts, refused.Outcome);

        const string said = "20 drafts are waiting that nobody has published (versions 1 to 20). " +
                            "A session of this factory is refused another draft while 20 or more are waiting. " +
                            "Publish one to make room. Every version is kept.";
        Assert.Equal(said, (await SeatPage("scout")).Brief.WaitingText);
        Assert.Equal(said, (await Read<FactoryBriefEditorViewDto>($"{BriefView}/editor")).WaitingText);
        var history = await Read<FactoryBriefHistoryViewDto>($"{BriefView}/history");
        Assert.Equal(said, history.WaitingText);
        // Every one of the twenty can be published from the history.
        Assert.All(history.Rows, r => Assert.NotNull(r.Publish));

        // The person publishes the newest: nothing waits, the sentence goes, and the session may write again.
        Assert.Equal(HttpStatusCode.OK, (await Publish(20)).StatusCode);
        Assert.Null((await SeatPage("scout")).Brief.WaitingText);
        SessionDraft("draft 21", 20);
    }

    [Theory]
    [InlineData("editor")]
    [InlineData("history")]
    [InlineData("versions/1")]
    public async Task BriefViews_AreTheOwners_ASessionKeyIsRefused(string view)
    {
        await StartAsync(Caller.SessionKey, SessionFactoryLookup.In("warmforward"));
        Seed();

        var response = await _http.GetAsync($"{BriefView}/{view}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("editor")]
    [InlineData("history")]
    [InlineData("versions/1")]
    public async Task BriefViews_OfASeatTheFactoryDoesNotHave_Are404_NamingItsSeats(string view)
    {
        await StartAsync();
        Seed();

        var response = await _http.GetAsync($"gateway/factories/warmforward/seats/night-watch/brief/{view}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.StartsWith("WarmForward has no seat 'night-watch'. Its seats are: boss, scout.", await ErrorOf(response));
    }

    [Theory]
    [InlineData("GET", "/gateway/factories/warmforward/seats/scout/brief/editor")]
    [InlineData("GET", "/gateway/factories/warmforward/seats/scout/brief/history")]
    [InlineData("GET", "/gateway/factories/warmforward/seats/scout/brief/versions/3")]
    [InlineData("GET", "/gateway/factories/warmforward/seats/scout")]
    public void SessionKeyGuard_KeepsTheOwnersBriefViewsFromASessionKey(string verb, string path)
    {
        // A session reads and writes a brief through /factory-briefs, where the factory rule answers it. The owner's
        // screen views are refused at the guard, before any route runs.
        Assert.False(SessionKeyGuard.Check(verb, path).Allowed);
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
        // The whole answer, as the session reads it: the refusal, and why.
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var error = body.GetProperty("error").GetString();
        Assert.Equal("this schedule belongs to factory 'warmforward', so only a person or a session of 'warmforward' may change or run it", error);
        // The same sentence pausing it is refused with: the one rule, not a second one beside it.
        Assert.Equal(FactoryNaming.ActRefusal("schedule", "warmforward"), error);
        Assert.Equal(FactoryNaming.ActDetail("schedule", "warmforward"), body.GetProperty("detail").GetString());
        Assert.Equal(
            "Everything on a schedule decides what a session of 'warmforward' will be told to do - its prompt, where it runs, " +
            "and when - and that session may read and write that factory's memory. Changing or running it from outside the " +
            "factory would therefore direct one of its members. A schedule with no factory on it is open to any session, as before.",
            body.GetProperty("detail").GetString());
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
