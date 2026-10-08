using CcDirector.Gateway.Tests.Factory.Registry;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Registry;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory;

/// <summary>
/// THE OWNER'S ACTIONS ON A FACTORY OVER HTTP (Factories screen mission, round 2), driven the way the Cockpit calls
/// them, against the real activity record and registry and a fake schedule store: the bulk "mark handled" writes a
/// row per item and one owner row; Archive switches off exactly the schedules its confirm named and Restore switches
/// those back on; and a session key, a Director's device key and the machine token are refused with nothing done.
/// </summary>
[Trait("Category", "FactoryRegistry")]
public sealed class FactoryOwnerActionEndpointTests : IAsyncDisposable
{
    private static readonly DateTime Now = new(2026, 10, 6, 10, 0, 0, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase? _db;
    private GatewayDatabase Db => _db ??= _h.Open();
    private WebApplication? _app;
    private HttpClient? _http;
    private FactoryActivityRecord? _record;
    private FactoryRegistryStore? _registry;

    /// <summary>The fake schedule store: every schedule of the account, by id.</summary>
    private readonly Dictionary<string, CronJobDto> _schedules = new(StringComparer.Ordinal);

    /// <summary>Every switch the routes asked of the schedule store, in order.</summary>
    private readonly List<(string Id, bool Enabled)> _switches = new();

    public enum Caller { OwnerBrowser, OwnerPhone, SessionKey, DirectorDeviceKey, MachineToken }

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        if (_app is not null) await _app.StopAsync();
        _h.Dispose();
    }

    private static RegisterFactoryRequest Website() => new()
    {
        Factory = "website-business",
        Title = "Website Business",
        Folder = @"D:\ReposFred\cc-consult\ideas\website-factory",
        Computer = "SOREN_NORTH",
        CeoSeat = "malik",
        Seats =
        {
            new FactorySeatManifest { Id = "malik", Name = "Malik Grant", Role = "CEO", BriefFile = "agents/ceo.yaml", Schedules = { "cj_ceo" } },
            new FactorySeatManifest { Id = "sender", Name = "Sender", Role = "Sender", BriefFile = "agents/sender.yaml", Schedules = { "cj_send", "cj_off" } },
        },
    };

    private void Schedule(string id, string name, bool enabled) => _schedules[id] = new CronJobDto
    {
        Id = id, Name = name, Enabled = enabled, ScheduleKind = CronSchedule.KindRecurring, CronExpression = "0 6 * * *", TimeZoneId = "UTC",
    };

    private async Task StartAsync(Caller caller = Caller.OwnerBrowser, bool switchOn = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.Use(async (ctx, next) =>
        {
            ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "credential";
            if (caller == Caller.SessionKey)
                ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] =
                    new SessionCredentialIdentity(Guid.Parse("11111111-2222-4333-8444-555555555555"), TenantId.Local, "dir-1");
            var deviceType = caller switch
            {
                Caller.OwnerBrowser => "browser",
                Caller.OwnerPhone => "phone",
                Caller.DirectorDeviceKey => "workstation",
                _ => null,
            };
            if (deviceType is not null)
            {
                ctx.Items[AuthMiddleware.DeviceTypeItemKey] = deviceType;
                ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] =
                    new DeviceCredentialIdentity("device-1", TenantId.Local.Value, deviceType, "active");
            }
            await next();
        });

        _record = new FactoryActivityRecord(Db);
        _registry = new FactoryRegistryStore(Db);
        // A seat's schedules are the ones that point at it (issue #3650): the links come first. Disabled, because
        // what these tests switch on and off is the schedule list the routes are handed below.
        foreach (var (seat, id) in new[] { ("malik", "cj_ceo"), ("sender", "cj_send"), ("sender", "cj_off") })
            ScheduleLinkSeed.Link(Db, TenantId.Local, "website-business", seat, id, enabled: false);
        _registry.Register(TenantId.Local, Website(), "the owner (test)", Now.AddDays(-20));
        var settings = new TenantSettingsStore(Db);
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
            _ => _schedules.Values.Select(Copy).ToList());

        FactoryOwnerActionEndpoints.Map(app, new FactoryAgentsSwitch(switchOn, settings),
            resolveTenant: _ => TenantId.Local,
            sources: sources,
            appendRows: (tenant, rows, actor) => record.Append(tenant, rows, actor),
            setScheduleEnabled: (_, id, enabled) =>
            {
                _switches.Add((id, enabled));
                if (!_schedules.TryGetValue(id, out var job)) return null;
                job.Enabled = enabled;
                return Copy(job);
            });

        await app.StartAsync();
        _app = app;
        _http = new HttpClient { BaseAddress = new Uri(app.Urls.First() + "/") };
    }

    private static CronJobDto Copy(CronJobDto j) => new()
    {
        Id = j.Id, Name = j.Name, Enabled = j.Enabled, ScheduleKind = j.ScheduleKind, CronExpression = j.CronExpression, TimeZoneId = j.TimeZoneId,
    };

    private FactoryActivityDto Waiting(string agent, string outcome, string what, DateTime at) =>
        _record!.Append(TenantId.Local, new AppendFactoryActivityRequest
        {
            Factory = "website-business", FactoryAgent = agent, Outcome = outcome, What = what, OccurredUtc = at,
        }, "session:test");

    private Task<HttpResponseMessage> Post(string path, object? body) =>
        _http!.PostAsJsonAsync($"gateway/factories/website-business/{path}", body, Web);

    private static async Task<string> ErrorOf(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString() ?? "";

    private List<FactoryActivityDto> AllRows() =>
        _record!.Query(TenantId.Local, factory: "website-business", limit: 1000).Rows;

    private FactoryPageViewDto Page()
    {
        var registered = _registry!.Find(TenantId.Local, "website-business")!;
        var settings = new TenantSettingsStore(Db);
        var record = _record!;
        var sources = new FactoriesScreenSources(
            new FactoryAgentsSources(
                (tenant, q) => record.Query(tenant, q.Factory, q.Agent, q.Outcome, q.FromUtc, q.ToUtc, q.OldestFirst, q.Offset, q.Limit),
                (tenant, request, actor) => record.Append(tenant, request, actor),
                _ => Array.Empty<FactoryTriggerFacts>(), (_, _, _, _, _) => Task.FromResult(false), _ => new HashSet<string>(),
                _ => TimeZoneInfo.Utc, () => Now, new FactoryReportStore(settings), new FactoryMapStore(settings)),
            _registry!, _ => _schedules.Values.Select(Copy).ToList());
        return FactoriesScreenFold.Page(registered, FactoriesScreenEndpoints.Inputs(sources, TenantId.Local, FactoryAgentsFold.WindowLast7d, "website-business"));
    }

    // ---------- mark everything older than 7 days ----------

    [Fact]
    public async Task HandledOlder_MarksExactlyTheCountTheConfirmShowed_AndRecordsTheOwnersAct()
    {
        await StartAsync();
        var oldDecision = Waiting("malik", "escalated", "C has 55.3 GB free, below the 60 GB line", Now.AddDays(-13));
        var oldQuestion = Waiting("malik", "asked", "Which domain?", Now.AddDays(-9));
        var fresh = Waiting("sender", "escalated", "Gmail asks to sign in again", Now.AddHours(-2));
        var bulk = Page().Waiting.BulkHandled!;
        Assert.Equal(2, bulk.ExpectedCount);

        var response = await Post("waiting/handled-older", new FactoryOwnerActionRequest { CutoffUtc = bulk.CutoffUtc, ExpectedCount = bulk.ExpectedCount });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<FactoryOwnerActionResultDto>(Web))!;
        Assert.Equal(2, result.Marked);
        Assert.Equal("Marked 2 items handled. The activity record holds a handled row for each, and one row for what you did.", result.Text);

        var rows = AllRows();
        Assert.Equal(new[] { oldDecision.Id, oldQuestion.Id }.OrderBy(i => i), rows.Where(r => r.CorrectsId is not null).Select(r => r.CorrectsId!.Value).OrderBy(i => i));
        var owner = Assert.Single(rows, r => r.FactoryAgent == "owner");
        Assert.Equal("The owner marked 2 items waiting on you as handled: everything from before 29 Sep 10:00.", owner.What);
        Assert.Equal("owner (device:device-1)", owner.Actor);

        // The fresh one is still waiting, and nothing is older than 7 days now.
        var waiting = Page().Waiting;
        Assert.Equal(fresh.Id, Assert.Single(waiting.Items).Id);
        Assert.Null(waiting.BulkHandled);
    }

    [Fact]
    public async Task HandledOlder_LeavesAnotherFactorysOldItemsOpen()
    {
        await StartAsync();
        _registry!.Register(TenantId.Local, new RegisterFactoryRequest
        {
            Factory = "machine-care",
            Title = "Machine Care",
            Folder = @"D:\ReposFred\machine-care",
            Computer = "SOREN_NORTH",
            Seats = { new FactorySeatManifest { Id = "caretaker", Name = "Caretaker", Role = "CEO", BriefFile = "agents/ceo.yaml" } },
        }, "the owner (test)", Now.AddDays(-20));
        var ours = Waiting("malik", "escalated", "Old website decision", Now.AddDays(-13));
        var theirs = _record!.Append(TenantId.Local, new AppendFactoryActivityRequest
        {
            Factory = "machine-care", FactoryAgent = "caretaker", Outcome = "escalated",
            What = "C has 55.3 GB free, below the 60 GB line", OccurredUtc = Now.AddDays(-13),
        }, "session:test");
        var bulk = Page().Waiting.BulkHandled!;
        Assert.Equal(1, bulk.ExpectedCount);

        var response = await Post("waiting/handled-older", new FactoryOwnerActionRequest { CutoffUtc = bulk.CutoffUtc, ExpectedCount = bulk.ExpectedCount });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var corrected = _record.Query(TenantId.Local, limit: 1000).Rows.Where(r => r.CorrectsId is not null).Select(r => r.CorrectsId!.Value).ToList();
        Assert.Equal(new[] { ours.Id }, corrected);
        Assert.DoesNotContain(theirs.Id, corrected);
        Assert.DoesNotContain(_record.Query(TenantId.Local, factory: "machine-care", limit: 1000).Rows, r => r.FactoryAgent == "owner");
    }

    [Fact]
    public async Task HandledOlder_WhenTheCountChanged_Is409_AndWritesNothing()
    {
        await StartAsync();
        Waiting("malik", "escalated", "old", Now.AddDays(-13));
        var before = AllRows().Count;

        var response = await Post("waiting/handled-older", new FactoryOwnerActionRequest { CutoffUtc = Now.AddDays(-7), ExpectedCount = 5 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Nothing was marked", await ErrorOf(response));
        Assert.Equal(before, AllRows().Count);
    }

    // ---------- archive and restore ----------

    [Fact]
    public async Task Archive_SwitchesOffTheNamedSchedules_KeepsTheEntry_AndRecordsIt_ThenRestoreSwitchesBackOnOnlyThose()
    {
        Schedule("cj_ceo", "Website - Malik Grant - morning run", enabled: true);
        Schedule("cj_send", "Website - Sender - send", enabled: true);
        Schedule("cj_off", "Website - Sender - retry", enabled: false);
        Schedule("cj_else", "Another factory's run", enabled: true);
        await StartAsync();
        var archive = Page().Archive!;
        Assert.Equal(new[] { "cj_ceo", "cj_send" }, archive.Schedules);

        var response = await Post("archive", new FactoryOwnerActionRequest { Schedules = archive.Schedules });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<FactoryOwnerActionResultDto>(Web))!;
        Assert.Equal("Website Business is archived. Switched off: \"Website - Malik Grant - morning run\" (cj_ceo) and \"Website - Sender - send\" (cj_send).", result.Text);
        Assert.Equal(new[] { ("cj_ceo", false), ("cj_send", false) }, _switches);
        Assert.False(_schedules["cj_ceo"].Enabled);
        Assert.True(_schedules["cj_else"].Enabled);
        var entry = _registry!.Find(TenantId.Local, "website-business")!;
        Assert.NotNull(entry.ArchivedAtUtc);
        Assert.Equal(new[] { "cj_ceo", "cj_send" }, entry.ArchivedSchedules);
        Assert.Equal(2, entry.Seats.Count);
        var archivedRow = Assert.Single(AllRows(), r => r.FactoryAgent == "owner");
        Assert.StartsWith("The owner archived Website Business", archivedRow.What);

        // Somebody switches the retry schedule on while it is archived; Restore must not touch it, and must say which.
        _schedules["cj_off"].Enabled = true;
        _switches.Clear();
        var restore = Page().Restore!;
        Assert.Equal(new[] { "cj_ceo", "cj_send" }, restore.Schedules);

        response = await Post("restore", new FactoryOwnerActionRequest { Schedules = restore.Schedules });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        result = (await response.Content.ReadFromJsonAsync<FactoryOwnerActionResultDto>(Web))!;
        Assert.Equal("Website Business is back on the Factories list. Switched back on: \"Website - Malik Grant - morning run\" (cj_ceo) and \"Website - Sender - send\" (cj_send).", result.Text);
        Assert.Equal(new[] { ("cj_ceo", true), ("cj_send", true) }, _switches);
        Assert.Null(_registry.Find(TenantId.Local, "website-business")!.ArchivedAtUtc);
        Assert.Equal(2, AllRows().Count(r => r.FactoryAgent == "owner"));
    }

    [Fact]
    public async Task Archive_WhenTheSchedulesChangedSinceTheConfirm_Is409_AndNothingIsDone()
    {
        Schedule("cj_ceo", "CEO run", enabled: true);
        Schedule("cj_send", "Send", enabled: true);
        await StartAsync();

        var response = await Post("archive", new FactoryOwnerActionRequest { Schedules = new() { "cj_ceo" } });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(_switches);
        Assert.Null(_registry!.Find(TenantId.Local, "website-business")!.ArchivedAtUtc);
        Assert.Empty(AllRows());
    }

    [Fact]
    public async Task Archive_Twice_IsRefused()
    {
        await StartAsync();
        Assert.Equal(HttpStatusCode.OK, (await Post("archive", new FactoryOwnerActionRequest { Schedules = new() })).StatusCode);

        var again = await Post("archive", new FactoryOwnerActionRequest { Schedules = new() });

        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("Website Business is already archived.", await ErrorOf(again));
    }

    [Fact]
    public async Task Restore_OfAFactoryThatIsNotArchived_IsRefused()
    {
        await StartAsync();
        var response = await Post("restore", new FactoryOwnerActionRequest { Schedules = new() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Website Business is not archived.", await ErrorOf(response));
    }

    // ---------- only the owner ----------

    [Theory]
    [InlineData(Caller.SessionKey, "waiting/handled-older")]
    [InlineData(Caller.SessionKey, "archive")]
    [InlineData(Caller.SessionKey, "restore")]
    [InlineData(Caller.DirectorDeviceKey, "waiting/handled-older")]
    [InlineData(Caller.DirectorDeviceKey, "archive")]
    [InlineData(Caller.DirectorDeviceKey, "restore")]
    [InlineData(Caller.MachineToken, "waiting/handled-older")]
    [InlineData(Caller.MachineToken, "archive")]
    [InlineData(Caller.MachineToken, "restore")]
    public async Task AnyCallerButTheOwnersBrowserOrPhone_Is403_AndNothingIsDone(Caller caller, string path)
    {
        Schedule("cj_ceo", "CEO run", enabled: true);
        await StartAsync(caller);
        Waiting("malik", "escalated", "old", Now.AddDays(-13));
        var before = AllRows().Count;

        var response = await Post(path, new FactoryOwnerActionRequest { CutoffUtc = Now.AddDays(-7), ExpectedCount = 1, Schedules = new() { "cj_ceo" } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("owner_only", body.GetProperty("code").GetString());
        Assert.Empty(_switches);
        Assert.Equal(before, AllRows().Count);
        Assert.Null(_registry!.Find(TenantId.Local, "website-business")!.ArchivedAtUtc);
    }

    [Fact]
    public async Task TheOwnersPhone_MayArchive()
    {
        await StartAsync(Caller.OwnerPhone);
        Assert.Equal(HttpStatusCode.OK, (await Post("archive", new FactoryOwnerActionRequest { Schedules = new() })).StatusCode);
    }

    [Fact]
    public async Task WithTheSwitchOff_TheOwnerIsToldWhy()
    {
        await StartAsync(switchOn: false);
        var response = await Post("archive", new FactoryOwnerActionRequest { Schedules = new() });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("switched off for this account", await ErrorOf(response));
    }

    [Theory]
    [InlineData("/gateway/factories/website-business/waiting/handled-older")]
    [InlineData("/gateway/factories/website-business/archive")]
    [InlineData("/gateway/factories/website-business/restore")]
    public void ASessionsOwnKey_NeverReachesTheRoute(string path)
        => Assert.False(SessionKeyGuard.Check("POST", path).Allowed, "the owner's acts on a factory are not a session's");
}
