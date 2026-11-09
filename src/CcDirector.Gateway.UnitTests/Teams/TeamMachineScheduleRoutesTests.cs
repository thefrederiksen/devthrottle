using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Triggers;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// INSIDE A TEAM, A SCHEDULE, A TRIGGER AND THE FOLDER CHOICES NAME ONLY THE CALLER'S OWN COMPUTERS (Team implementation
/// 2.1, review note N1), through their real routes. The routes are mapped exactly as the host maps them, on an in-memory
/// test server (no port, no Director, no clock), with the machine check the host passes - asked here as the team's Owner,
/// over a real team where the Owner's Director runs on OWNER-PC and a Developer's on DEV-PC.
/// </summary>
public sealed class TeamMachineScheduleRoutesTests : IAsyncLifetime
{
    private static readonly TenantId A = TenantId.Local;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Folder = "/home/owner/project";

    private readonly TeamMachineFixture _team = new();
    private readonly GatewayDbTestHarness _h = new();
    private CronJobStore _jobs = null!;
    private TriggerStore _triggers = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        var db = _h.Open();
        _jobs = new CronJobStore(db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json"));
        _triggers = new TriggerStore(db);
        var history = new CronRunHistoryStore(db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".runs.json"));
        var directors = new[]
        {
            new DirectorDto { DirectorId = "d-owner", MachineName = TeamMachineFixture.OwnerPc, Os = DirectorOs.Linux, Version = "2.30.0" },
            new DirectorDto { DirectorId = "d-dev", MachineName = TeamMachineFixture.DeveloperPc, Os = DirectorOs.Linux, Version = "2.30.0" },
        };
        var check = new ScheduleFolderCheck(_ => directors, (_, _) => true, (_, _, _) =>
            Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success(JsonSerializer.Serialize(
                new FolderCheckAnswer { Exists = true, IsDirectory = true, Os = DirectorOs.Linux }, Json))));
        // The check as the host asks it: in the team, for a request the gate allowed for the Owner.
        Func<Microsoft.AspNetCore.Http.HttpContext, string?, string, string?> refuseMachine = _team.AsOwner;

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Services.AddRouting();
        builder.Services.AddLogging();
        builder.WebHost.UseTestServer();
        _app = builder.Build();
        CronJobEndpoints.Map(_app, _jobs, sessionFactoryOf: _ => SessionFactoryLookup.NotKnown,
            findFactory: (_, _) => null,
            runRecords: new CronRunRecordReader(history, new SessionHistoryStore(db).EndingsOf),
            accountZoneOf: _ => TimeZoneInfo.Utc, isDefined: (_, _) => false,
            checkFolder: (_, machine, path, ct) => check.CheckAsync(A, machine, path, ct), refuseMachine: refuseMachine);
        CronFolderChoicesEndpoint.Map(_app, (_, machine) => new CronFolderChoicesEndpoint.MachineFolders(check.OsOf(A, machine),
            check.IsOnline(A, machine), new[] { new KnownRepositoryDto { Name = "project", Path = Folder } }), refuseMachine: refuseMachine);
        var service = new TriggerService(_triggers, new FactoryActivityRecord(db),
            (_, _, _) => Task.FromResult(TriggerStartAttempt.Failed("no session is started in this test")),
            findSession: (_, _) => null, findSessionByName: (_, _) => null,
            timeZone: _ => TimeZoneInfo.Utc, nowUtc: () => FactoryPublishNow, startLifetime: CancellationToken.None,
            directorRunsDefinitionChecks: (_, _) => true, machineRunsDefinitionChecks: (_, _) => true, definitionOf: (_, _) => null,
            registeredFactoryOf: (_, _) => null, recordCheckStartedRun: (_, _, _) => { }, mailDeskOn: _ => false);
        TriggerEndpoints.Map(_app, _ => A, service, directorMachine: (_, _) => null,
            nowUtc: () => FactoryPublishNow, sessionFactoryOf: _ => SessionFactoryLookup.NotKnown, isDefined: (_, _) => false,
            dueChecks: new DueCheckService(history, (tenant, id) => _jobs.Get(tenant, id), _triggers.CheckStates, new NoStarts(),
                directorRunsDefinitionChecks: (_, _) => true, resultRecorded: (_, _) => { }, nowUtc: () => FactoryPublishNow,
                startLifetime: CancellationToken.None, mailDeskOn: _ => false),
            refuseMachine: refuseMachine);
        await _app.StartAsync();
        _http = _app.GetTestClient();
    }

    private static readonly DateTime FactoryPublishNow = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        _h.Dispose();
        _team.Dispose();
    }

    private static CronJobDto Schedule(string machine) => new()
    {
        Name = "nightly tidy",
        Enabled = true,
        ScheduleKind = "recurring",
        CronExpression = "0 3 * * *",
        TimeZoneId = "UTC",
        Target = new CronJobTarget { Machine = machine },
        Action = new CronJobAction { RepoPath = Folder, Seed = "/help" },
    };

    private static TriggerDefinitionRequest Trigger(string machine) => new()
    {
        Name = "new mail",
        Factory = "erik-laursen",
        FactoryAgent = "outreach",
        Machine = machine,
        RepoPath = Folder,
        CheckCommand = "python checks/new_mail.py",
        IntervalSeconds = 600,
        Prompt = "New mail.",
    };

    /// <summary>The refusal is the team gate's own 403, carrying the one sentence - which names no computer and no person.</summary>
    private static async Task AssertRefusedNamingNothing(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(TeamEndpointGate.RefusalCode, body.GetProperty("code").GetString());
        var error = body.GetProperty("error").GetString()!;
        Assert.Equal(TeamMachineGuard.Refusal, error);
        Assert.DoesNotContain(TeamMachineFixture.DeveloperPc, error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TeamMachineFixture.OwnerPc, error, StringComparison.OrdinalIgnoreCase);
    }

    // ---- schedules (/cron/jobs) -----------------------------------------------------------------------------------

    [Fact]
    public async Task CreateSchedule_InATeam_TheOwnerOnTheirOwnComputer_IsSaved()
    {
        var response = await _http.PostAsJsonAsync("/cron/jobs", Schedule(TeamMachineFixture.OwnerPc));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Single(_jobs.ListAll());
    }

    [Fact]
    public async Task CreateSchedule_InATeam_TheOwnerNamingADevelopersComputer_IsRefused_AndNothingIsStored()
    {
        var response = await _http.PostAsJsonAsync("/cron/jobs", Schedule(TeamMachineFixture.DeveloperPc));

        await AssertRefusedNamingNothing(response);
        Assert.Empty(_jobs.ListAll());
    }

    [Fact]
    public async Task UpdateSchedule_InATeam_MovingItToADevelopersComputer_IsRefused_AndTheStoredOneStays()
    {
        var created = (await (await _http.PostAsJsonAsync("/cron/jobs", Schedule(TeamMachineFixture.OwnerPc)))
            .Content.ReadFromJsonAsync<CronJobDto>(Json))!;

        var response = await _http.PutAsJsonAsync($"/cron/jobs/{created.Id}", Schedule(TeamMachineFixture.DeveloperPc));

        await AssertRefusedNamingNothing(response);
        Assert.Equal(TeamMachineFixture.OwnerPc, _jobs.ListAll().Single().Target.Machine);
    }

    // ---- triggers ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateTrigger_InATeam_TheOwnerOnTheirOwnComputer_IsSaved()
    {
        var response = await _http.PostAsJsonAsync("/triggers", Trigger(TeamMachineFixture.OwnerPc));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Single(_triggers.List(A));
    }

    [Fact]
    public async Task CreateTrigger_InATeam_TheOwnerNamingADevelopersComputer_IsRefused_AndNothingIsStored()
    {
        var response = await _http.PostAsJsonAsync("/triggers", Trigger(TeamMachineFixture.DeveloperPc));

        await AssertRefusedNamingNothing(response);
        Assert.Empty(_triggers.List(A));
    }

    [Fact]
    public async Task UpdateTrigger_InATeam_MovingItToADevelopersComputer_IsRefused()
    {
        var created = JsonDocument.Parse(await (await _http.PostAsJsonAsync("/triggers", Trigger(TeamMachineFixture.OwnerPc)))
            .Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString();

        var response = await _http.PutAsJsonAsync($"/triggers/{created}", new TriggerDefinitionRequest { Machine = TeamMachineFixture.DeveloperPc });

        await AssertRefusedNamingNothing(response);
        Assert.Equal(TeamMachineFixture.OwnerPc, _triggers.List(A).Single().Machine);
    }

    // ---- the schedule editor's folder choices -----------------------------------------------------------------------

    [Fact]
    public async Task FolderChoices_InATeam_TheOwnersOwnComputer_ListsItsRepositories()
    {
        var response = await _http.GetAsync($"/cron/folder-choices?machine={TeamMachineFixture.OwnerPc}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single((await response.Content.ReadFromJsonAsync<CronFolderChoicesDto>(Json))!.Repositories);
    }

    [Fact]
    public async Task FolderChoices_InATeam_ADevelopersComputer_IsRefused_AndListsNothing()
    {
        var response = await _http.GetAsync($"/cron/folder-choices?machine={TeamMachineFixture.DeveloperPc}");

        await AssertRefusedNamingNothing(response);
    }

    private sealed class NoStarts : ICronSessionStarter
    {
        public Task<(string? sessionId, string? directorId, string? error)> StartAsync(CronJobDto job, CancellationToken ct)
            => throw new InvalidOperationException("no schedule starts in this test");

        public Task<CronRunStart> StartCheckedRunAsync(CronJobDto job, string directorId, string seed, DueCheckFinding found, CancellationToken ct)
            => throw new InvalidOperationException("no due check starts in this test");
    }
}
