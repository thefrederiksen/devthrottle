using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Talk;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// A SEAT START THE DIRECTOR REFUSES REACHES THE PERSON AS ITS OWN SENTENCE (Factory Definition mission, phase 5, QA
/// finding 6). The Director refuses a seat it cannot start - a secret the machine does not hold, a repository it has not
/// registered, an agent that cannot take the seat's rules - with one sentence. That sentence is the answer: a 409 with
/// the sentence alone on the doors that answer with a status (the Director door the talk takes, the machine door), and
/// the reason recorded on a scheduled run. A Director FAULT - the create threw, the Director did not answer - is still
/// a 5xx, and a create that is not a seat start is answered exactly as before.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactorySeatStartRefusalTests : IDisposable
{
    private const string DirectorId = "dir-seat";
    private const string Machine = "SEAT-PC";
    private const string TheFactory = "website-factory";
    private static readonly Guid Caller = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>The Director's sentence for a secret its machine does not hold, as FactoryKitLaunch words it.</summary>
    private const string MissingSecret =
        "The seat keeper needs the secret 'qa-p6-no-such-secret', which SEAT-PC does not hold, so no session was started. Add it there with cc-secrets.";

    /// <summary>The Director's sentence for a repository its machine has not registered, as SettleSeatStart words it.</summary>
    private const string UnregisteredRepository =
        "The seat stray works in the repository 'qa-not-here', which is not registered on SEAT-PC, so no session was started. Register it there, or change the seat's repository in the definition.";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-seat-refusal-" + Guid.NewGuid().ToString("N"));
    private readonly DirectorRegistry _registry;
    private readonly FactoryKitCapabilityRegistry _kit = new();
    private DirectorCommandResult _directorAnswers = DirectorCommandResult.Fail(DirectorCommandStatus.Conflict, MissingSecret);
    private int _creates;
    private WebApplication? _app;
    // The machine door's caller: a seat of a defined factory, unless a test makes it a session with no seat.
    private SessionFactoryLookup _callerRow = SessionFactoryLookup.In(TheFactory, "outreach", 4);

    public FactorySeatStartRefusalTests()
    {
        Directory.CreateDirectory(_dir);
        _registry = new DirectorRegistry(Path.Combine(_dir, "instances"));
        _registry.RegisterFromStream(DirectorId, Machine, "test", "2.22.0", pid: 1,
            startedAt: new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc), tenant: TenantId.Local);
        _kit.Record(TenantId.Local, DirectorId, buildsFactoryKit: true);
    }

    public void Dispose()
    {
        _app?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _registry.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* scratch folder */ }
    }

    /// <summary>The Director's stream: every create is answered with <see cref="_directorAnswers"/>.</summary>
    private Task<DirectorCommandResult?> Send(string directorId, DirectorCommand command, CancellationToken ct)
    {
        if (command.Verb != "create") return Task.FromResult<DirectorCommandResult?>(null);
        _creates++;
        return Task.FromResult<DirectorCommandResult?>(_directorAnswers);
    }

    /// <summary>The seat start the talk sends: built by the Gateway from a published definition, in no folder.</summary>
    private static NewSessionRequest TalkSeatStart() => new()
    {
        RepoPath = "",
        Agent = "ClaudeCode",
        Factory = TheFactory,
        FactorySeat = "keeper",
        FactoryDefinitionVersion = 4,
        FactoryKit = FactoryKits.Seat,
        FactoryHome = true,
    };

    /// <summary>A person's ordinary spawn: no factory, no seat.</summary>
    private static NewSessionRequest PlainSpawn() => new()
    {
        RepoPath = Path.GetTempPath(),
        Agent = "ClaudeCode",
    };

    private static string? ErrorOf(IResult? result)
    {
        var value = (result as IValueHttpResult)?.Value;
        return value?.GetType().GetProperty("error")?.GetValue(value) as string;
    }

    private static int? StatusOf(IResult? result) => (result as IStatusCodeHttpResult)?.StatusCode;

    // ---------- which answers are a seat start's refusal ----------

    [Theory]
    [InlineData(DirectorCommandStatus.Conflict)]
    [InlineData(DirectorCommandStatus.BadRequest)]
    public void SeatStartRefusal_ASeatStartTheDirectorRefused_IsTheDirectorsSentenceAlone(DirectorCommandStatus status)
    {
        var refusal = DirectorCommandRouter.SeatStartRefusal(TalkSeatStart(), DirectorCommandResult.Fail(status, MissingSecret));

        Assert.Equal(MissingSecret, refusal);
    }

    [Fact]
    public void SeatStartRefusal_AChildOfASeat_WithItsRulesOnlyKit_IsARefusalToo()
    {
        var child = new NewSessionRequest { RepoPath = Path.GetTempPath(), Agent = "Codex", FactoryKit = FactoryKits.RulesOnly };

        Assert.Equal("No session was started: Codex cannot take rules.",
            DirectorCommandRouter.SeatStartRefusal(child,
                DirectorCommandResult.Fail(DirectorCommandStatus.Conflict, "No session was started: Codex cannot take rules.")));
    }

    [Theory]
    [InlineData(DirectorCommandStatus.Error)]
    [InlineData(DirectorCommandStatus.Timeout)]
    [InlineData(DirectorCommandStatus.TunnelDropped)]
    [InlineData(DirectorCommandStatus.NotFound)]
    public void SeatStartRefusal_ADirectorFault_IsNotARefusal(DirectorCommandStatus status)
    {
        Assert.Null(DirectorCommandRouter.SeatStartRefusal(TalkSeatStart(), DirectorCommandResult.Fail(status, "the process could not be started")));
    }

    [Fact]
    public void SeatStartRefusal_UNDEFINED_FACTORY_ACreateWithNoSeat_IsNeverARefusal()
    {
        var undefined = new NewSessionRequest { RepoPath = Path.GetTempPath(), Agent = "ClaudeCode", Factory = TheFactory };

        Assert.Null(DirectorCommandRouter.SeatStartRefusal(undefined,
            DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "repoPath does not exist: C:\\nowhere")));
    }

    [Fact]
    public void SeatStartRefusal_AStartThatSucceeded_IsNotARefusal()
    {
        Assert.Null(DirectorCommandRouter.SeatStartRefusal(TalkSeatStart(), DirectorCommandResult.Success("{}")));
    }

    // ---------- the Director answers a refused seat with a refusal's status, a fault with Error ----------

    [Fact]
    public void CreateFailureStatus_ASeatTheMachineRefuses_IsAConflict()
    {
        var refused = new FactorySeatRefusedException(new FactoryKitRequest(TheFactory, 4, "keeper", FactoryKits.Seat), MissingSecret);

        Assert.Equal(DirectorCommandStatus.Conflict, SessionCommandExecutor.CreateFailureStatus(refused));
    }

    /// <summary>The Director's sentence for a seat whose machine's worktree pool refused a slot (review finding 1).</summary>
    private const string PoolFull =
        "No session was opened. A seat of a defined factory always works in a pooled worktree of D:\\ReposFred\\devthrottle, " +
        "and cc-worktrees refused: every slot is in use.";

    [Fact]
    public void CreateFailureStatus_ASeatWhosePoolIsFull_IsAConflict()
    {
        var refused = new FactorySeatRefusedException(new FactoryKitRequest(TheFactory, 4, "keeper", FactoryKits.Seat), PoolFull,
            new InvalidOperationException("every slot is in use"));

        Assert.Equal(DirectorCommandStatus.Conflict, SessionCommandExecutor.CreateFailureStatus(refused));
        Assert.Equal(PoolFull, refused.Message);
    }

    [Fact]
    public async Task DirectorDoor_ATalkWhosePoolIsFull_Is409WithTheDirectorsSentenceAlone()
    {
        _directorAnswers = DirectorCommandResult.Fail(DirectorCommandStatus.Conflict, PoolFull);

        var outcome = await GatewayEndpoints.StartSessionOnDirectorAsync(TalkFromAPerson(), DirectorId, TalkSeatStart(), Door());

        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(outcome.Refusal));
        Assert.Equal(PoolFull, ErrorOf(outcome.Refusal));
    }

    [Fact]
    public void CreateFailureStatus_AKitThatCouldNotBeHad_IsStillAnError()
    {
        var fault = new FactoryKitUnavailableException(new FactoryKitRequest(TheFactory, 4, "keeper", FactoryKits.Seat),
            "it was written but could not be read: the disk is full");

        Assert.Equal(DirectorCommandStatus.Error, SessionCommandExecutor.CreateFailureStatus(fault));
    }

    [Fact]
    public void CreateFailureStatus_UNDEFINED_FACTORY_AnyOtherFailureToStart_IsStillAnError()
    {
        Assert.Equal(DirectorCommandStatus.Error,
            SessionCommandExecutor.CreateFailureStatus(new InvalidOperationException("the agent's process could not be started")));
    }

    // ---------- the Director door: the one the talk takes ----------

    private DirectorSpawnDoor Door() => new(
        new HostedTenantBoundary(new SingleTenantContext(), new DeviceRegistry(), hosted: false),
        _registry,
        _ => SessionFactoryLookup.NotKnown,
        Send,
        Missions: null, WorkflowRuns: null, Workspaces: null,
        KitCapabilities: _kit,
        SeatRulesFor: _ => (_, _, _) => SeatRulesRead.Carries(true));

    /// <summary>A person pressing Talk: the Gateway marked the seat fields as its own, as the talk route does.</summary>
    private static HttpContext TalkFromAPerson()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[AuthMiddleware.DeviceTypeItemKey] = "browser";
        SpawnFactory.MarkGatewaySeatStart(ctx);
        return ctx;
    }

    [Fact]
    public async Task DirectorDoor_ATalkWhoseSecretTheMachineDoesNotHold_Is409WithTheDirectorsSentenceAlone()
    {
        var outcome = await GatewayEndpoints.StartSessionOnDirectorAsync(TalkFromAPerson(), DirectorId, TalkSeatStart(), Door());

        Assert.Null(outcome.Session);
        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(outcome.Refusal));
        Assert.Equal(MissingSecret, ErrorOf(outcome.Refusal));
        Assert.Equal(1, _creates);
    }

    [Fact]
    public async Task DirectorDoor_ATalkInARepositoryTheMachineHasNotRegistered_Is409WithTheDirectorsSentenceAlone()
    {
        _directorAnswers = DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, UnregisteredRepository);

        var outcome = await GatewayEndpoints.StartSessionOnDirectorAsync(TalkFromAPerson(), DirectorId, TalkSeatStart(), Door());

        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(outcome.Refusal));
        Assert.Equal(UnregisteredRepository, ErrorOf(outcome.Refusal));
    }

    [Fact]
    public async Task DirectorDoor_ATalkTheDirectorFaultedOn_IsStill502()
    {
        _directorAnswers = DirectorCommandResult.Fail(DirectorCommandStatus.Error, "the agent's process could not be started");

        var outcome = await GatewayEndpoints.StartSessionOnDirectorAsync(TalkFromAPerson(), DirectorId, TalkSeatStart(), Door());

        Assert.Equal(StatusCodes.Status502BadGateway, StatusOf(outcome.Refusal));
    }

    [Fact]
    public async Task DirectorDoor_UNDEFINED_FACTORY_APlainSpawnTheDirectorRefuses_IsStill502WithTheDirectorReturnedWords()
    {
        _directorAnswers = DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "repoPath does not exist: C:\\nowhere");
        var ctx = new DefaultHttpContext();
        ctx.Items[AuthMiddleware.DeviceTypeItemKey] = "browser";

        var outcome = await GatewayEndpoints.StartSessionOnDirectorAsync(ctx, DirectorId, PlainSpawn(), Door());

        Assert.Equal(StatusCodes.Status502BadGateway, StatusOf(outcome.Refusal));
        var problem = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult>(outcome.Refusal);
        Assert.Equal("director returned BadRequest: repoPath does not exist: C:\\nowhere", problem.ProblemDetails.Detail);
    }

    // ---------- the machine spawner: the scheduled run, the trigger and the machine door ----------

    private sealed class AlwaysFound : IDirectorTargetResolver
    {
        public Task<DirectorTargetResult> ResolveAsync(string machine, string? director, CancellationToken ct)
            => Task.FromResult(new DirectorTargetResult(DirectorId, null));
    }

    private MachineSessionSpawner Spawner() => new(new AlwaysFound(), (DirectorCommandRouter.SendDirectorCommandAsync)Send)
    {
        // The resolved Director builds a seat's kit.
        RefuseSeatStart = (_, _) => null,
    };

    [Fact]
    public async Task Spawner_ASeatStartTheDirectorRefused_SaysSo_WithTheSentenceAlone()
    {
        var r = await Spawner().SpawnOnMachineWithOutcomeAsync(Machine, TalkSeatStart(), CancellationToken.None);

        Assert.False(r.Ok);
        Assert.True(r.SeatStartRefused);
        Assert.False(r.OutcomeUnknown);
        Assert.Equal(MissingSecret, r.Error);
    }

    [Fact]
    public async Task Spawner_ASeatStartTheDirectorFaultedOn_IsNotARefusal()
    {
        _directorAnswers = DirectorCommandResult.Fail(DirectorCommandStatus.Error, "the agent's process could not be started");

        var r = await Spawner().SpawnOnMachineWithOutcomeAsync(Machine, TalkSeatStart(), CancellationToken.None);

        Assert.False(r.SeatStartRefused);
        Assert.Equal("director returned Error: the agent's process could not be started", r.Error);
    }

    [Fact]
    public async Task Spawner_UNDEFINED_FACTORY_APlainCreateTheDirectorRefused_IsWordedAsBefore()
    {
        _directorAnswers = DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "repoPath does not exist: C:\\nowhere");

        var r = await Spawner().SpawnOnMachineWithOutcomeAsync(Machine, PlainSpawn(), CancellationToken.None);

        Assert.False(r.SeatStartRefused);
        Assert.Equal("director returned BadRequest: repoPath does not exist: C:\\nowhere", r.Error);
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => new(2026, 10, 15, 6, 0, 0, DateTimeKind.Utc);
    }

    private DirectorCronSessionStarter Starter(Func<CronJobDto, PublishedFactoryDefinition?> definitionOf) => new(
        Spawner(), new FixedClock(), refuseFactoryWork: _ => null,
        seatWithABrief: j => j.Seat is null ? null : new ScheduledRunSeat(
            new RegisteredFactoryDto
            {
                Factory = DefinitionFixture.FactoryId,
                Title = "DevThrottle",
                BossSeat = "boss",
                Seats = { new RegisteredFactorySeatDto { Id = "boss", Name = "Boss", Role = "Boss" } },
            },
            new RegisteredFactorySeatDto { Id = "boss", Name = "Boss", Role = "Boss" },
            new FactoryGivenBrief(3, "Run the factory.", FactoryBriefVersionKinds.Definition)),
        definitionOf: definitionOf);

    private static CronJobDto Job(string? seat) => new()
    {
        Id = "cj_boss",
        Name = "DevThrottle - Boss",
        TimeZoneId = "UTC",
        ScheduleKind = CronSchedule.KindRecurring,
        CronExpression = "0 6 * * *",
        Factory = seat is null ? null : DefinitionFixture.FactoryId,
        Seat = seat,
        Target = new CronJobTarget { Machine = Machine },
        Action = new CronJobAction { RepoPath = seat is null ? Path.GetTempPath() : "", Seed = "Morning round.", AutoDismiss = true },
    };

    [Fact]
    public async Task ScheduledRun_ADefinedSeatTheDirectorRefused_ReportsTheDirectorsSentenceAloneAsItsError()
    {
        var published = new PublishedFactoryDefinition(3, FactoryDefinitionCheck.Read(DefinitionFixture.Publishable()).Definition!);

        var start = await Starter(_ => published).StartRunAsync(Job("boss"), CancellationToken.None);

        Assert.Null(start.SessionId);
        Assert.Equal(MissingSecret, start.Error);
    }

    [Fact]
    public async Task ScheduledRun_UNDEFINED_FACTORY_ARunTheDirectorRefused_RecordsTheSameWordsAsBefore()
    {
        _directorAnswers = DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "repoPath does not exist: C:\\nowhere");

        var start = await Starter(_ => null).StartRunAsync(Job(null), CancellationToken.None);

        Assert.Null(start.SessionId);
        Assert.Equal("director returned BadRequest: repoPath does not exist: C:\\nowhere", start.Error);
    }

    // ---------- the machine door, driven whole in memory ----------

    private async Task<HttpClient> MachineDoorAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        // An in-memory host: the route is driven over HTTP with no listener and no port.
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "credential";
            ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] = new SessionCredentialIdentity(Caller, TenantId.Local, DirectorId);
            await next();
        });
        MachineEndpoints.Map(app, false, new LauncherRegistry(), Spawner(),
            boundary: new HostedTenantBoundary(new SingleTenantContext(), new DeviceRegistry(), hosted: false),
            // The calling session is a seat of a defined factory, so its child leaves as the seat with a rules-only kit.
            sessionFactoryOf: id => id == Caller.ToString() ? _callerRow : SessionFactoryLookup.NotKnown,
            directors: _registry,
            seatRulesFor: _ => (_, _, _) => SeatRulesRead.Carries(true));
        await app.StartAsync();
        _app = app;
        return app.GetTestClient();
    }

    private static object ChildOfTheSeat() => new { repoPath = Path.GetTempPath(), agent = "ClaudeCode", controllerSessionId = "none" };

    [Fact]
    public async Task MachineDoor_AChildOfASeatTheDirectorRefused_Is409WithTheDirectorsSentenceAlone()
    {
        _directorAnswers = DirectorCommandResult.Fail(DirectorCommandStatus.Conflict, "No session was started: the agent cannot take the seat's rules.");
        var http = await MachineDoorAsync();

        var response = await http.PostAsJsonAsync($"/machines/{Machine}/sessions", ChildOfTheSeat(), Web);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("No session was started: the agent cannot take the seat's rules.", body.RootElement.GetProperty("error").GetString());
        Assert.Equal(1, _creates);
    }

    [Fact]
    public async Task MachineDoor_AChildOfASeatTheDirectorFaultedOn_IsStill502()
    {
        _directorAnswers = DirectorCommandResult.Fail(DirectorCommandStatus.Error, "the agent's process could not be started");
        var http = await MachineDoorAsync();

        var response = await http.PostAsJsonAsync($"/machines/{Machine}/sessions", ChildOfTheSeat(), Web);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("director returned Error: the agent's process could not be started", body.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task MachineDoor_UNDEFINED_FACTORY_AChildWithNoSeatTheDirectorRefused_IsStill502WithTheDirectorReturnedWords()
    {
        _callerRow = SessionFactoryLookup.In(TheFactory);
        _directorAnswers = DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "repoPath does not exist: C:\\nowhere");
        var http = await MachineDoorAsync();

        var response = await http.PostAsJsonAsync($"/machines/{Machine}/sessions", ChildOfTheSeat(), Web);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("director returned BadRequest: repoPath does not exist: C:\\nowhere", body.RootElement.GetProperty("error").GetString());
    }
}
