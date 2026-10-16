using System.Text.Json;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Talk;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Tests.Factory.Definition;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Running;

/// <summary>
/// A RUN NOW THAT STARTS A SEAT IS NEVER RECORDED AS "DID NOT RUN" (Factory Definition mission, phase 5, QA finding 9).
///
/// A seat's start takes longer than `cc-devthrottle schedule run` waited. The route handed the request's abort to the
/// start, so when the command line hung up the create to the Director was cancelled, the router read the cancelled
/// invocation as a dropped connection, and the run was recorded as never started - while the Director carried the
/// create on and the seat ran. Once the Gateway has decided to run, the caller going away no longer cancels it.
///
/// Each test drives the real run-now route, in memory (no port), on the real engine and the real cron starter and
/// spawner, over a fake Director command that behaves as the real connection does when its token is cancelled: it
/// completes with a plain "Invocation canceled by the server." exception, not a cancellation. The caller goes away
/// while the Director is still working; the Director then answers.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class RunNowOutlivesTheCallerTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 16, 4, 5, 7, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string Refusal =
        "The seat keeper needs the secret 'qa-p6-no-such-secret', which SOREN_NORTH does not hold, so no session was started. Add it there with cc-secrets.";

    private readonly GatewayDbTestHarness _h = new();
    private readonly GatewayDatabase _db;
    private readonly ServiceProvider _services;
    private readonly CronJobStore _schedules;
    private readonly CronRunHistoryStore _runs;

    /// <summary>Set when the create reaches the Director.</summary>
    private readonly TaskCompletionSource _createSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>What the Director answers, when the test says so.</summary>
    private readonly TaskCompletionSource<DirectorCommandResult> _directorAnswer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<DirectorCommand> _sent = new();

    public RunNowOutlivesTheCallerTests()
    {
        _db = _h.Open();
        _services = new ServiceCollection().AddRouting().AddLogging().BuildServiceProvider();
        _schedules = new CronJobStore(_db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json"));
        _runs = new CronRunHistoryStore(_db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".runs.json"));
    }

    public void Dispose()
    {
        _services.Dispose();
        _h.Dispose();
    }

    // ---------- the world ----------

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => Now;
    }

    private sealed class OneDirector : IDirectorTargetResolver
    {
        public Task<DirectorTargetResult> ResolveAsync(string machine, string? director, CancellationToken ct)
            => Task.FromResult(new DirectorTargetResult("08520f72", null));
    }

    private sealed class UnusedWorkListRunner : ICronWorkListRunner
    {
        public Task<CronWorkListOutcome> TriggerAsync(CronJobDto job, CancellationToken ct) =>
            throw new InvalidOperationException("a seed schedule must not trigger the work-list runner");
    }

    private sealed class Routes(IServiceProvider services) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = services;
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }

    /// <summary>The Director's end of the create, as the real connection behaves: a cancelled token completes the
    /// invocation with a plain exception, exactly what SignalR does.</summary>
    private async Task<DirectorCommandResult?> Director(string directorId, DirectorCommand command, CancellationToken ct)
    {
        _sent.Add(command);
        using var cancelled = ct.Register(() => _directorAnswer.TrySetException(new Exception("Invocation canceled by the server.")));
        _createSent.TrySetResult();
        return await _directorAnswer.Task;
    }

    private static PublishedFactoryDefinition Published()
        => new(3, FactoryDefinitionCheck.Read(DefinitionFixture.Publishable()).Definition!);

    private static ScheduledRunSeat Registered() => new(
        new RegisteredFactoryDto
        {
            Factory = DefinitionFixture.FactoryId,
            Title = "DevThrottle",
            Folder = @"D:\ReposFred\devthrottle",
            BossSeat = "boss",
            Seats = { new RegisteredFactorySeatDto { Id = "boss", Name = "Boss", Role = "Boss" } },
        },
        new RegisteredFactorySeatDto { Id = "boss", Name = "Boss", Role = "Boss" },
        new FactoryGivenBrief(3, "Run the factory.", FactoryBriefVersionKinds.Definition));

    /// <summary>A schedule of the defined factory's boss seat, or - <paramref name="defined"/> false - a plain schedule
    /// in no factory, which starts in its folder exactly as before phase 5.</summary>
    private CronJobDto Schedule(bool defined) => _schedules.Create(new CronJobDto
    {
        Name = defined ? "DevThrottle - Boss" : "Nightly triage",
        ScheduleKind = CronSchedule.KindRecurring,
        CronExpression = "0 6 * * *",
        TimeZoneId = "UTC",
        Factory = defined ? DefinitionFixture.FactoryId : null,
        Seat = defined ? "boss" : null,
        Target = new CronJobTarget { Machine = "SOREN_NORTH" },
        Action = new CronJobAction { RepoPath = @"D:\repo", Seed = "Morning round." },
    });

    /// <summary>The route, wired as the host wires it, with <paramref name="runLifetime"/> as the Gateway's own lifetime.</summary>
    private Routes Map(bool defined, CancellationToken runLifetime)
    {
        var spawner = new MachineSessionSpawner(new OneDirector(), Director) { RefuseSeatStart = (_, _) => null };
        var starter = new DirectorCronSessionStarter(spawner, new FixedClock(), refuseFactoryWork: _ => null,
            seatWithABrief: j => j.Seat is null ? null : Registered(),
            definitionOf: j => defined && j.Factory is not null ? Published() : null);
        var engine = new CronEngine(_schedules, _runs, starter, new UnusedWorkListRunner(), new NullCronNotifier(), new FixedClock());
        var routes = new Routes(_services);
        CronRunEndpoints.Map(routes, engine, new CronRunRecordReader(_runs, new CcDirector.Gateway.History.SessionHistoryStore(_db).EndingsOf),
            jobById: id => _schedules.Get(id), sessionFactoryOf: _ => SessionFactoryLookup.NotKnown,
            accountZoneOf: _ => TimeZoneInfo.Utc, runLifetime: runLifetime);
        return routes;
    }

    /// <summary>POST /cron/jobs/{id}/run as the owner's browser, the request aborted by <paramref name="callerGoesAway"/>.</summary>
    private (Task Answered, HttpContext Context) RunNow(Routes routes, string id, CancellationToken callerGoesAway)
    {
        var endpoint = routes.DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/cron/jobs/{id}/run");
        var ctx = new DefaultHttpContext { RequestServices = _services, RequestAborted = callerGoesAway };
        ctx.Request.Method = "POST";
        ctx.Request.Path = $"/cron/jobs/{id}/run";
        ctx.Request.RouteValues = new RouteValueDictionary { ["id"] = id };
        ctx.Response.Body = new MemoryStream();
        ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "credential";
        ctx.Items[AuthMiddleware.DeviceTypeItemKey] = "browser";
        return (endpoint.RequestDelegate!(ctx), ctx);
    }

    /// <summary>Start the run now, let the caller hang up while the create is with the Director, then let the Director
    /// answer <paramref name="answer"/>. Returns the run as recorded.</summary>
    private async Task<CronRunRecord> RunNowAndHangUp(bool defined, DirectorCommandResult answer)
    {
        var job = Schedule(defined);
        using var caller = new CancellationTokenSource();
        var (answered, _) = RunNow(Map(defined, CancellationToken.None), job.Id, caller.Token);

        await _createSent.Task.WaitAsync(TimeSpan.FromSeconds(30));
        caller.Cancel();                       // `schedule run` gives up
        _directorAnswer.TrySetResult(answer);  // and the Director carries the create on
        await answered.WaitAsync(TimeSpan.FromSeconds(30));

        return Assert.Single(_runs.List(job.Id));
    }

    private static DirectorCommandResult Started(string sid) =>
        DirectorCommandResult.Success(JsonSerializer.Serialize(new SessionDto { SessionId = sid }, Web));

    // ---------- the tests ----------

    [Fact]
    public async Task RunNow_TheCallerGoesAwayWhileTheSeatStarts_DoesNotCancelTheStart_AndTheRunIsRecordedStarted()
    {
        var run = await RunNowAndHangUp(defined: true, Started("sid-worker"));

        Assert.Equal("create", Assert.Single(_sent).Verb);
        Assert.Equal("sid-worker", run.SessionId);
        Assert.Equal("started", run.InfraStatus);
        Assert.Equal(CronRunResults.Pending, run.Result);
        Assert.Null(run.ResultReason);
    }

    [Fact]
    public async Task RunNow_TheDirectorRefusesTheSeatAfterTheCallerWentAway_RecordsTheDirectorsSentence()
    {
        var run = await RunNowAndHangUp(defined: true, DirectorCommandResult.Fail(DirectorCommandStatus.Conflict, Refusal));

        Assert.Null(run.SessionId);
        Assert.Equal("not-started", run.InfraStatus);
        Assert.Equal(CronRunResults.Problem, run.Result);
        Assert.Equal("it did not start a session: " + Refusal, run.ResultReason);
    }

    [Fact]
    public async Task RunNow_TheDirectorFaultsAfterTheCallerWentAway_IsStillRecordedAsTheDirectorsFault()
    {
        var run = await RunNowAndHangUp(defined: true, DirectorCommandResult.Fail(DirectorCommandStatus.Error, "the pool has no free slot"));

        Assert.Null(run.SessionId);
        Assert.Equal("not-started", run.InfraStatus);
        Assert.Equal(CronRunResults.Problem, run.Result);
        Assert.Equal("it did not start a session: director returned Error: the pool has no free slot", run.ResultReason);
        Assert.DoesNotContain("dropped", run.ResultReason);
    }

    [Fact]
    public async Task RunNow_UNDEFINED_FACTORY_TheCallerGoesAway_APlainScheduleIsRecordedStartedInItsFolder()
    {
        var run = await RunNowAndHangUp(defined: false, Started("sid-plain"));

        var create = Assert.Single(_sent);
        Assert.Contains(@"D:\\repo", create.PayloadJson);
        Assert.Equal("sid-plain", run.SessionId);
        Assert.Equal("started", run.InfraStatus);
    }

    [Fact]
    public async Task RunNow_UNDEFINED_FACTORY_TheCallerWaits_AnswersTheRecordedRunAsBefore()
    {
        var job = Schedule(defined: false);
        _directorAnswer.TrySetResult(Started("sid-plain"));

        var (answered, ctx) = RunNow(Map(defined: false, CancellationToken.None), job.Id, CancellationToken.None);
        await answered.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(200, ctx.Response.StatusCode);
        ctx.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(ctx.Response.Body);
        Assert.Equal("sid-plain", body.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal("started", body.RootElement.GetProperty("infraStatus").GetString());
    }
}
