using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Triggers;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Tests.Factory.Definition;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Triggers;

/// <summary>
/// BACKUP MACHINES, END TO END THROUGH THE ROUTES A DIRECTOR CALLS (Mail Desk, phase 5; review of pull request 4232,
/// findings 1 and 2). A definition with a backup machine is saved and published by the real publish, which writes its
/// trigger; then each "Director" does what the real one does, in order: asks for its triggers
/// (<c>GET /directors/{id}/triggers</c>), downloads the check it was handed
/// (<c>GET /directors/{id}/factory-checks/{factory}/{version}</c>), and reports (<c>POST .../checks</c>). In memory
/// through the real Map functions, no port, no Director, with an injected clock.
///
/// The fixture's interval start is on the seat outreach, on SOREN_SOUTH, every 10 minutes, with the check
/// <c>checks/new_mail.py</c>; the backup machine added to it is LAPTOP_2.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class BackupMachinesThroughTheRoutesTests : IDisposable
{
    private static readonly TenantId T = TenantId.Local;
    private const string Factory = DefinitionFixture.FactoryId;
    private const string Seat = "seats/outreach.yaml";
    private const string Prompt = "    prompt: \"New mail: {count}. {context}\"\n";

    private static readonly Dictionary<string, string> Directors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dir-south"] = "SOREN_SOUTH", ["dir-backup"] = "LAPTOP_2", ["dir-elsewhere"] = "LAPTOP_9",
    };

    private readonly GatewayDbTestHarness _h = new();
    private readonly GatewayDatabase _db;
    private readonly ServiceProvider _services;
    private readonly Routes _routes;
    private readonly FactoryPublishScenario _publish;
    private readonly TriggerStore _triggers;
    private DateTime _now = FactoryPublishScenario.Now;

    public BackupMachinesThroughTheRoutesTests()
    {
        _db = _h.Open();
        _services = new ServiceCollection().AddRouting().AddLogging().BuildServiceProvider();
        _routes = new Routes(_services);
        _publish = new FactoryPublishScenario(_db, T);
        _triggers = new TriggerStore(_db);
        var jobs = new CronJobStore(_db, _h.LegacyPath("jobs.json"));
        var runs = new CronRunHistoryStore(_db, _h.LegacyPath("runs.json"));

        FactoryCheckEndpoints.Map(_routes, _ => T, directorMachine: (_, id) => Directors.GetValueOrDefault(id), _publish.Store);
        var service = new TriggerService(_triggers, new FactoryActivityRecord(_db),
            (_, _, _) => Task.FromResult(TriggerStartAttempt.Failed("no session is started in this test")),
            findSession: (_, _) => null, findSessionByName: (_, _) => null,
            timeZone: _ => TimeZoneInfo.Utc, nowUtc: () => _now, startLifetime: CancellationToken.None,
            directorRunsDefinitionChecks: (_, _) => true, machineRunsDefinitionChecks: (_, _) => true, definitionOf: (_, _) => null);
        TriggerEndpoints.Map(_routes, _ => T, service, directorMachine: (_, id) => Directors.GetValueOrDefault(id),
            nowUtc: () => _now, sessionFactoryOf: _ => SessionFactoryLookup.NotKnown, isDefined: _publish.Store.IsDefined,
            dueChecks: new DueCheckService(runs, (tenant, id) => jobs.Get(tenant, id), _triggers.CheckStates, new NoStarts(),
                directorRunsDefinitionChecks: (_, _) => true, resultRecorded: (_, _) => { }, nowUtc: () => _now,
                startLifetime: CancellationToken.None));
    }

    public void Dispose()
    {
        _services.Dispose();
        _h.Dispose();
    }

    private static Dictionary<string, string> WithBackup()
        => DefinitionFixture.Edit(Seat, Prompt, Prompt + "    backupMachines: [LAPTOP_2]\n");

    private Guid TriggerId => _publish.Triggers(Factory).Single().Id;

    private static string Nothing(DateTime at, string state)
        => JsonSerializer.Serialize(new TriggerCheckReport { CheckedAtUtc = at, ExitCode = 0, Output = """{"count": 0}""", State = state });

    /// <summary>The triggers a Director is handed by its pull.</summary>
    private async Task<List<JsonElement>> Pull(string director)
    {
        var (status, body) = await Send("GET", $"/directors/{director}/triggers");
        Assert.Equal(200, status);
        return body.GetProperty("triggers").EnumerateArray().ToList();
    }

    private int Runs()
    {
        using var ctx = _db.CreateContext(T);
        return ctx.TriggerRuns.Count(r => r.TriggerId == TriggerId);
    }

    private void GoQuiet() => _now = _now.Add(TriggerStore.ClaimLapse(600)).AddSeconds(1);

    // ---------- finding 1: the backup is given the check's code ----------

    [Fact]
    public async Task Get_FactoryChecks_ADirectorOnABackupMachineWithNoSeat_IsGivenTheChecks()
    {
        _publish.SaveAndPublish(WithBackup());

        var (status, body) = await Send("GET", $"/directors/dir-backup/factory-checks/{Factory}/1");

        Assert.True(status == 200, $"expected 200, got {status}: {body}");
        Assert.True(body.GetProperty("files").TryGetProperty("checks/new_mail.py", out _));
    }

    [Fact]
    public async Task Get_FactoryChecks_AMachineThatIsNeitherASeatsNorABackup_IsStillRefused()
    {
        _publish.SaveAndPublish(WithBackup());

        var (status, body) = await Send("GET", $"/directors/dir-elsewhere/factory-checks/{Factory}/1");

        Assert.Equal(404, status);
        Assert.Equal("no_seat_on_machine", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Handover_ThroughTheRoutes_TheBackupPullsDownloadsAndReports_ThenTheSeatsMachineTakesItBack()
    {
        // Arrange: the seat's machine runs the check once and leaves its cursor.
        _publish.SaveAndPublish(WithBackup());
        Assert.Single(await Pull("dir-south"));
        Assert.Equal(200, (await Send("POST", $"/directors/dir-south/triggers/{TriggerId:D}/checks", Nothing(_now, "cursor 41"))).Status);

        // Act and assert: before the lapse, the backup is handed nothing.
        _now = _now.Add(TriggerStore.ClaimLapse(600));
        Assert.Empty(await Pull("dir-backup"));

        // After it, the backup is handed the trigger with the cursor, downloads its check, and its report is recorded.
        _now = _now.AddSeconds(1);
        var handed = Assert.Single(await Pull("dir-backup"));
        Assert.Equal("cursor 41", handed.GetProperty("state").GetString());
        var version = handed.GetProperty("definitionVersion").GetInt32();
        var (download, files) = await Send("GET", $"/directors/dir-backup/factory-checks/{Factory}/{version}");
        Assert.True(download == 200, $"the backup could not download its check: {download} {files}");
        Assert.True(files.GetProperty("files").TryGetProperty(handed.GetProperty("checkPath").GetString()!, out _));
        var (reported, run) = await Send("POST", $"/directors/dir-backup/triggers/{TriggerId:D}/checks", Nothing(_now, "cursor 42"));
        Assert.True(reported == 200, $"the backup's report was not recorded: {reported} {run}");
        Assert.Equal("dir-backup", run.GetProperty("directorId").GetString());

        // The seat's machine returns: it is handed the trigger at once with the backup's cursor, and the backup's next
        // report is refused and recorded nothing.
        _now = _now.AddSeconds(30);
        Assert.Equal("cursor 42", Assert.Single(await Pull("dir-south")).GetProperty("state").GetString());
        var runs = Runs();
        var (refused, why) = await Send("POST", $"/directors/dir-backup/triggers/{TriggerId:D}/checks", Nothing(_now, "late"));
        Assert.Equal((409, "held_by_another_director"), (refused, why.GetProperty("code").GetString()));
        Assert.Equal(runs, Runs());
        Assert.Empty(await Pull("dir-backup"));
    }

    // ---------- finding 2: a report from a machine a republish removed ----------

    [Fact]
    public async Task Post_Checks_AReportFromABackupARepublishRemoved_IsRefused_AndTheSeatsMachineIsHandedItAtOnce()
    {
        // Arrange: the seat's machine is away, the backup holds the claim; then the owner republishes without the
        // backup while the backup's check is still running.
        _publish.SaveAndPublish(WithBackup());
        GoQuiet();
        Assert.Single(await Pull("dir-backup"));
        _publish.SaveAndPublish(DefinitionFixture.Valid());
        var runs = Runs();

        // Act: that check's report arrives after the publish.
        var (status, body) = await Send("POST", $"/directors/dir-backup/triggers/{TriggerId:D}/checks", Nothing(_now, "stale"));

        // Assert: refused, nothing recorded, no claim taken - so the seat's machine is handed the trigger now, not
        // after a lapse.
        Assert.Equal((409, "machine_not_on_the_list"), (status, body.GetProperty("code").GetString()));
        Assert.Equal("trigger 'devthrottle-outreach-start-1' runs on SOREN_SOUTH, not on LAPTOP_2, so this report from " +
                     "Director dir-backup was not recorded", body.GetProperty("error").GetString());
        Assert.Equal(runs, Runs());
        Assert.Equal("", _triggers.Find(T, TriggerId.ToString("D"))!.ClaimedByDirectorId);
        Assert.Single(await Pull("dir-south"));
    }

    [Fact]
    public async Task Post_Checks_AReportFromAMachineOnNoList_IsRefused_EvenWithTheClaimFree()
    {
        _publish.SaveAndPublish(WithBackup());

        var (status, body) = await Send("POST", $"/directors/dir-elsewhere/triggers/{TriggerId:D}/checks", Nothing(_now, "x"));

        Assert.Equal((409, "machine_not_on_the_list"), (status, body.GetProperty("code").GetString()));
        Assert.Equal(0, Runs());
    }

    // ---------- the in-memory pipeline ----------

    private sealed class NoStarts : ICronSessionStarter
    {
        public Task<(string? sessionId, string? directorId, string? error)> StartAsync(CronJobDto job, CancellationToken ct)
            => throw new InvalidOperationException("no schedule starts in this test");

        public Task<CronRunStart> StartCheckedRunAsync(CronJobDto job, string directorId, string seed, DueCheckFinding found, CancellationToken ct)
            => throw new InvalidOperationException("no due check starts in this test");
    }

    private sealed class Routes(IServiceProvider services) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = services;
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }

    private sealed class HasABody : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private (RouteEndpoint Endpoint, RouteValueDictionary Values)? Match(string method, string path)
    {
        foreach (var endpoint in _routes.DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
                     .OrderBy(e => e.RoutePattern.Parameters.Count))
        {
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
            if (methods is null || !methods.Contains(method)) continue;
            var values = new RouteValueDictionary();
            var matcher = new TemplateMatcher(TemplateParser.Parse(endpoint.RoutePattern.RawText!), new RouteValueDictionary());
            if (matcher.TryMatch(path, values)) return (endpoint, values);
        }
        return null;
    }

    private async Task<(int Status, JsonElement Body)> Send(string method, string path, string? json = null)
    {
        var matched = Match(method, path);
        Assert.True(matched is not null, $"no mapped route matches {method} {path}");
        var ctx = new DefaultHttpContext { RequestServices = _services };
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Request.RouteValues = matched!.Value.Values;
        var bytes = Encoding.UTF8.GetBytes(json ?? "");
        ctx.Request.Body = new MemoryStream(bytes);
        if (json is not null)
        {
            ctx.Request.ContentType = "application/json";
            ctx.Request.ContentLength = bytes.Length;
            ctx.Features.Set<IHttpRequestBodyDetectionFeature>(new HasABody());
        }
        ctx.Response.Body = new MemoryStream();

        await matched.Value.Endpoint.RequestDelegate!(ctx);

        ctx.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(ctx.Response.Body);
        return (ctx.Response.StatusCode, document.RootElement.Clone());
    }
}
