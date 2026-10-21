using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Triggers;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Tests.Factory.Triggers;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;
using SessionHistoryStore = CcDirector.Gateway.History.SessionHistoryStore;

namespace CcDirector.Gateway.Tests.Factory.Memory;

/// <summary>
/// THE OWNER'S BOSS, DRIVEN THROUGH THE ROUTES (issue #4351). The owner talks to a factory's Boss to improve the
/// factory, so the Boss started for the owner must be able to do what its night start does - and the seats that are
/// not the Boss must still not rearrange the factory's starts.
///
/// Every request goes in at the route with a session credential whose REAL history row (the production reader,
/// <see cref="SessionHistoryStore.FactoryOf"/> over a real database) names the factory and the seat, so a test here
/// cannot pass by reading the body:
///
///  - the Director spawn door, asked by a session in no factory for the factory's Boss, sends the Director a create
///    seated as the Boss; asked for the Finder, it sends nothing;
///  - the trigger routes and the schedule routes accept the Boss and refuse the Finder, on the same row;
///  - a session of the factory with no seat recorded keeps the rule it had.
/// </summary>
[Trait("Category", "FactoryMemory")]
[Collection("DirectorRoot")]
public sealed class FactoryBossThroughTheRoutesTests : IDisposable
{
    private const string TheFactory = "website-factory";
    private const string TheBoss = "boss";
    private const string TheFinder = "finder";
    private const int ThePublished = 4;
    private const string DirectorId = "dir-boss-wiring";
    private const string Machine = "BOSS-PC";
    private const string SessionHeader = "X-Test-Session";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>A fixed time: nothing here is decided by the clock, and a gate test never reads the real one.</summary>
    private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _db = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-boss-wiring-" + Guid.NewGuid().ToString("N"));
    private readonly SessionHistoryStore _history;

    private WebApplication? _app;
    private HttpClient? _http;
    private DirectorRegistry? _registry;
    private CronJobStore? _schedules;
    private TriggerService? _triggers;
    private NewSessionRequest? _directorSaw;

    public FactoryBossThroughTheRoutesTests()
    {
        Directory.CreateDirectory(_dir);
        _history = new SessionHistoryStore(_db.Open());
    }

    public void Dispose()
    {
        _http?.Dispose();
        if (_app is not null) _app.StopAsync().GetAwaiter().GetResult();
        _registry?.Dispose();
        _db.Dispose();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (Exception) { /* best effort */ }
    }

    private static RegisteredFactoryDto Registration() => new()
    {
        Factory = TheFactory,
        Title = "Website Factory",
        BossSeat = TheBoss,
        Seats = { new RegisteredFactorySeatDto { Id = TheBoss }, new RegisteredFactorySeatDto { Id = TheFinder } },
    };

    private async Task StartGatewayAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Headers.TryGetValue(SessionHeader, out var sid) && Guid.TryParse(sid.ToString(), out var id))
                ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] = new SessionCredentialIdentity(id, TenantId.Local, DirectorId);
            await next();
        });

        _registry = new DirectorRegistry(Path.Combine(_dir, "instances"));
        _registry.RegisterFromStream(DirectorId, Machine, "test", "2.24.0-test", pid: 1, startedAt: Now, tenant: TenantId.Local);
        var kits = new Streaming.FactoryKitCapabilityRegistry();
        kits.Record(TenantId.Local, DirectorId, buildsFactoryKit: true);

        DirectorCommandRouter.SendDirectorCommandAsync send = (_, command, _) =>
        {
            if (command.Verb != "create") return Task.FromResult<DirectorCommandResult?>(null);
            _directorSaw = JsonSerializer.Deserialize<NewSessionRequest>(command.PayloadJson ?? "{}", Web);
            return Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success(
                JsonSerializer.Serialize(new SessionDto { SessionId = Guid.NewGuid().ToString() }, Web)));
        };

        var boundary = new HostedTenantBoundary(new SingleTenantContext(), new DeviceRegistry(), hosted: false);
        GatewayEndpoints.Map(app, false, _registry, version: "test", token: "test-token",
            tenantBoundary: boundary, aiGate: TenantAiGate.SelfHost, sessionFactoryOf: _history.FactoryOf, sendCommand: send,
            factoryKitCapabilities: kits,
            seatRulesFor: _ => (_, _, _) => SeatRulesRead.Carries(false),
            bossStartFor: _ => factory => factory == TheFactory
                ? new FactoryBossStart(TheFactory, TheBoss, ThePublished, Archived: false)
                : null);

        var db = _db.Open();
        _schedules = new CronJobStore(db, Path.Combine(_dir, "jobs.json"));
        var runs = new CronRunHistoryStore(db, Path.Combine(_dir, "runs.json"));
        CronJobEndpoints.Map(app, _schedules, sessionFactoryOf: _history.FactoryOf,
            findFactory: (_, id) => id == TheFactory ? Registration() : null,
            runRecords: new CronRunRecordReader(runs, _history.EndingsOf), accountZoneOf: _ => TimeZoneInfo.Utc,
            isDefined: (_, _) => false);

        _triggers = new TriggerService(new TriggerStore(_db.Open()), new FactoryActivityRecord(_db.Open()),
            (_, _, _) => Task.FromResult(TriggerStartAttempt.Failed("no machine in this test")),
            findSession: (_, _) => null, findSessionByName: (_, _) => null,
            timeZone: _ => TimeZoneInfo.Utc, nowUtc: () => Now, startLifetime: CancellationToken.None,
            directorRunsDefinitionChecks: (_, _) => true, machineRunsDefinitionChecks: (_, _) => true, definitionOf: (_, _) => null, mailDeskOn: _ => false);
        TriggerEndpoints.Map(app, _ => TenantId.Local, _triggers,
            directorMachine: (_, _) => Machine, nowUtc: () => Now, sessionFactoryOf: _history.FactoryOf,
            isDefined: (_, _) => false,
            dueChecks: DueCheckTestParts.NoDueChecks(_db.Open(), Path.Combine(_dir, "due.runs.json")),
            bossSeatOf: (_, id) => id == TheFactory ? TheBoss : null);

        await app.StartAsync();
        _app = app;
        _http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
    }

    /// <summary>The history row a roster push writes for a session: its factory, and the seat it was started as.</summary>
    private Guid Session(string? factory, string? seat = null)
    {
        var id = Guid.NewGuid();
        _history.UpsertLive(DirectorId, new SessionDto
        {
            SessionId = id.ToString(),
            Name = seat is null ? "caller" : $"Website Factory - {seat}",
            RepoPath = Path.GetTempPath(),
            Agent = "ClaudeCode",
            MachineName = Machine,
            CreatedAt = Now.AddMinutes(-1),
            LastActivityAt = Now,
            ActivityState = "Working",
            Status = "Running",
            Factory = factory,
            FactorySeat = seat,
            FactoryDefinitionVersion = seat is null ? null : ThePublished,
            FactoryKit = seat is null ? null : FactoryKits.RulesOnly,
        }, Now);
        return id;
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, Guid asSession, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(SessionHeader, asSession.ToString());
        if (body is not null) request.Content = JsonContent.Create(body, options: Web);
        return _http!.SendAsync(request);
    }

    private static CronJobDto Schedule(bool enabled = true) => new()
    {
        Name = "Finder",
        TimeZoneId = "UTC",
        ScheduleKind = "recurring",
        CronExpression = "0 7 * * *",
        Factory = TheFactory,
        Seat = TheFinder,
        Enabled = enabled,
        Target = new CronJobTarget { Machine = Machine },
        Action = new CronJobAction { RepoPath = Path.GetTempPath(), Seed = "/find" },
    };

    private static TriggerDefinitionRequest Trigger()
    {
        var req = TriggerDefinitionTests.Valid();
        req.Name = "finder-starts-writer";
        return req;
    }

    // ---- the spawn door ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_SESSION_IN_NO_FACTORY_STARTS_THE_BOSS_SEATED_AND_IS_REFUSED_ANY_OTHER_SEAT()
    {
        await StartGatewayAsync();
        var owners = Session(factory: null);
        object Spawn(string seat) => new
        {
            repoPath = Path.GetTempPath(),
            agent = "ClaudeCode",
            controllerSessionId = "none",
            factory = TheFactory,
            factorySeat = seat,
        };

        var finder = await Send(HttpMethod.Post, $"/directors/{DirectorId}/sessions", owners, Spawn(TheFinder));
        Assert.Equal(HttpStatusCode.Forbidden, finder.StatusCode);
        Assert.Null(_directorSaw);

        var boss = await Send(HttpMethod.Post, $"/directors/{DirectorId}/sessions", owners, Spawn(TheBoss));
        Assert.True(boss.IsSuccessStatusCode, $"the Boss spawn answered {(int)boss.StatusCode}: {await boss.Content.ReadAsStringAsync()}");
        var sent = Assert.IsType<NewSessionRequest>(_directorSaw);
        Assert.Equal(TheFactory, sent.Factory);
        Assert.Equal(TheBoss, sent.FactorySeat);
        Assert.Equal(ThePublished, sent.FactoryDefinitionVersion);
        Assert.Equal(FactoryKits.RulesOnly, sent.FactoryKit);
    }

    // ---- triggers -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task THE_BOSS_ADDS_PAUSES_AND_RESUMES_A_TRIGGER_AND_A_FINDER_IS_REFUSED_EACH()
    {
        await StartGatewayAsync();
        var finder = Session(TheFactory, TheFinder);
        var boss = Session(TheFactory, TheBoss);

        var refused = await Send(HttpMethod.Post, "/triggers", finder, Trigger());
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("only its Boss (the seat 'boss')", await refused.Content.ReadAsStringAsync());
        Assert.Empty(_triggers!.Store.List(TenantId.Local));

        var added = await Send(HttpMethod.Post, "/triggers", boss, Trigger());
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var id = Assert.Single(_triggers.Store.List(TenantId.Local)).Id.ToString("D");

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"/triggers/{id}/pause", finder)).StatusCode);
        Assert.False(_triggers.Store.Find(TenantId.Local, id)!.Paused);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"/triggers/{id}/pause", boss)).StatusCode);
        Assert.True(_triggers.Store.Find(TenantId.Local, id)!.Paused);

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"/triggers/{id}/resume", finder)).StatusCode);
        Assert.True(_triggers.Store.Find(TenantId.Local, id)!.Paused);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"/triggers/{id}/resume", boss)).StatusCode);
        Assert.False(_triggers.Store.Find(TenantId.Local, id)!.Paused);

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Delete, $"/triggers/{id}", finder)).StatusCode);
        Assert.NotNull(_triggers.Store.Find(TenantId.Local, id));
    }

    [Fact]
    public async Task A_SESSION_OF_THE_FACTORY_WITH_NO_SEAT_RECORDED_KEEPS_THE_RULE_IT_HAD()
    {
        // Every session of a factory with no published definition, and every run of a plain schedule, carries no seat.
        // It cannot be told from the Boss, so it is not narrowed.
        await StartGatewayAsync();
        var member = Session(TheFactory);

        Assert.Equal(HttpStatusCode.Created, (await Send(HttpMethod.Post, "/triggers", member, Trigger())).StatusCode);
    }

    // ---- schedules ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task THE_BOSS_SWITCHES_A_SCHEDULE_OFF_AND_A_FINDER_IS_REFUSED()
    {
        await StartGatewayAsync();
        var job = _schedules!.Create(Schedule());
        var finder = Session(TheFactory, TheFinder);
        var boss = Session(TheFactory, TheBoss);

        var refused = await Send(HttpMethod.Put, $"/cron/jobs/{job.Id}", finder, Schedule(enabled: false));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.True(_schedules.Get(job.Id)!.Enabled);

        var switched = await Send(HttpMethod.Put, $"/cron/jobs/{job.Id}", boss, Schedule(enabled: false));
        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);
        Assert.False(_schedules.Get(job.Id)!.Enabled);
    }

    [Fact]
    public async Task A_FINDER_IS_REFUSED_CREATING_DELETING_AND_UNLINKING_A_SCHEDULE_AND_THE_BOSS_IS_NOT()
    {
        await StartGatewayAsync();
        var finder = Session(TheFactory, TheFinder);
        var boss = Session(TheFactory, TheBoss);

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, "/cron/jobs", finder, Schedule())).StatusCode);
        Assert.Empty(_schedules!.ListAll());
        Assert.Equal(HttpStatusCode.Created, (await Send(HttpMethod.Post, "/cron/jobs", boss, Schedule())).StatusCode);
        var id = Assert.Single(_schedules.ListAll()).Id;

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"/cron/jobs/{id}/unlink", finder)).StatusCode);
        Assert.Equal(TheFactory, _schedules.Get(id)!.Factory);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Delete, $"/cron/jobs/{id}", finder)).StatusCode);
        Assert.NotNull(_schedules.Get(id));

        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Delete, $"/cron/jobs/{id}", boss)).StatusCode);
        Assert.Null(_schedules.Get(id));
    }
}
