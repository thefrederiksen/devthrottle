using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Running;

/// <summary>
/// Issue #4421 through the routes: the create and the update check a schedule's folder on its machine with the real
/// <see cref="ScheduleFolderCheck"/>, and the Schedule form's folder choices come from the machine. The host is an
/// in-memory test server and the machine is a fake tunnel; nothing listens on a port and nothing waits.
/// </summary>
public sealed class ScheduleFolderRoutesTests : IAsyncLifetime
{
    private static readonly TenantId A = new("acct-a");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Mac = "STUDIO_MAC";

    private readonly GatewayDbTestHarness _h = new();
    private readonly List<DirectorCommand> _sent = new();
    private readonly HashSet<string> _folders = new(StringComparer.Ordinal) { "/Users/pat/Documents/project" };
    private bool _online = true;
    private CronJobStore _store = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        var db = _h.Open();
        _store = new CronJobStore(db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json"));
        var history = new CronRunHistoryStore(db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".runs.json"));
        var directors = new[] { new DirectorDto { DirectorId = "d-mac", MachineName = Mac, Os = DirectorOs.MacOs, Version = "2.30.0" } };
        var check = new ScheduleFolderCheck(_ => directors, (_, _) => _online, (directorId, command, ct) =>
        {
            _sent.Add(command);
            var path = JsonSerializer.Deserialize<FolderCheckRequest>(command.PayloadJson, Json)!.Path;
            var exists = _folders.Contains(path);
            return Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success(JsonSerializer.Serialize(
                new FolderCheckAnswer { Exists = exists, IsDirectory = exists, Os = DirectorOs.MacOs }, Json)));
        });

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Services.AddRouting();
        builder.Services.AddLogging();
        builder.WebHost.UseTestServer();
        _app = builder.Build();
        CronJobEndpoints.Map(_app, _store, sessionFactoryOf: _ => CcDirector.Gateway.History.SessionFactoryLookup.NotKnown,
            findFactory: (_, _) => null,
            runRecords: new CronRunRecordReader(history, new CcDirector.Gateway.History.SessionHistoryStore(db).EndingsOf),
            accountZoneOf: _ => TimeZoneInfo.Utc, isDefined: (_, _) => false,
            checkFolder: (_, machine, path, ct) => check.CheckAsync(A, machine, path, ct));
        CronFolderChoicesEndpoint.Map(_app, (_, machine) => new CronFolderChoicesEndpoint.MachineFolders(check.OsOf(A, machine),
            check.IsOnline(A, machine), new[] { new KnownRepositoryDto { Name = "project", Path = "/Users/pat/Documents/project" } }));
        await _app.StartAsync();
        _http = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        _h.Dispose();
    }

    private static CronJobDto Schedule(string repoPath, bool enabled = true) => new()
    {
        Name = "nightly tidy",
        Enabled = enabled,
        ScheduleKind = "recurring",
        CronExpression = "0 3 * * *",
        TimeZoneId = "UTC",
        Target = new CronJobTarget { Machine = Mac },
        Action = new CronJobAction { RepoPath = repoPath, Seed = "/help" },
    };

    private static async Task<string> ErrorOf(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString()!;
    }

    [Fact]
    public async Task Create_AWindowsPathForAMac_IsRefusedInPlainWords_AndNothingIsStored()
    {
        var response = await _http.PostAsJsonAsync("/cron/jobs", Schedule(@"\\documents\\devthrottle"));

        Assert.Equal(@"'\\documents\\devthrottle' is a Windows path, but STUDIO_MAC is a Mac. Use a path like /Users/<you>/Documents/devthrottle.",
            await ErrorOf(response));
        Assert.Empty(_store.ListAll());
    }

    [Fact]
    public async Task Create_ARightShapedFolderTheMacDoesNotHave_IsRefused()
    {
        var response = await _http.PostAsJsonAsync("/cron/jobs", Schedule("/Users/pat/Documents/elsewhere"));

        Assert.Equal("The folder '/Users/pat/Documents/elsewhere' does not exist on STUDIO_MAC (a Mac). Choose a folder that exists on STUDIO_MAC.",
            await ErrorOf(response));
        Assert.Empty(_store.ListAll());
    }

    [Fact]
    public async Task Create_AFolderTheMacHas_IsSaved_WithNoFolderWarning()
    {
        var response = await _http.PostAsJsonAsync("/cron/jobs", Schedule("/Users/pat/Documents/project"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var saved = (await response.Content.ReadFromJsonAsync<CronJobDto>(Json))!;
        Assert.Null(saved.FolderWarning);
        Assert.Single(_sent);
        Assert.Single(_store.ListAll());
    }

    [Fact]
    public async Task Create_WithTheMacOffline_IsSaved_WithAWarningThatTheFolderWasNotChecked()
    {
        _online = false;

        var response = await _http.PostAsJsonAsync("/cron/jobs", Schedule("/Users/pat/Documents/anything"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var saved = (await response.Content.ReadFromJsonAsync<CronJobDto>(Json))!;
        Assert.Equal("STUDIO_MAC is offline, so the folder '/Users/pat/Documents/anything' could not be checked. It is checked again when the schedule first runs.",
            saved.FolderWarning);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Update_SwitchingOffASchedule_WhoseFolderWentMissing_Lands_WithoutAsking()
    {
        var created = (await (await _http.PostAsJsonAsync("/cron/jobs", Schedule("/Users/pat/Documents/project"))).Content.ReadFromJsonAsync<CronJobDto>(Json))!;
        _folders.Clear();
        _sent.Clear();

        var off = Schedule("/Users/pat/Documents/project", enabled: false);
        var response = await _http.PutAsJsonAsync($"/cron/jobs/{created.Id}", off);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(_store.Get(created.Id)!.Enabled);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Update_ToAFolderTheMacDoesNotHave_IsRefused_AndTheStoredFolderStays()
    {
        var created = (await (await _http.PostAsJsonAsync("/cron/jobs", Schedule("/Users/pat/Documents/project"))).Content.ReadFromJsonAsync<CronJobDto>(Json))!;

        var response = await _http.PutAsJsonAsync($"/cron/jobs/{created.Id}", Schedule(@"C:\repos\project"));

        Assert.StartsWith(@"'C:\repos\project' is a Windows path, but STUDIO_MAC is a Mac.", await ErrorOf(response));
        Assert.Equal("/Users/pat/Documents/project", _store.Get(created.Id)!.Action.RepoPath);
    }

    [Fact]
    public async Task FolderChoices_OfferTheMachinesRepositories_AndAPlaceholderInItsShape()
    {
        var choices = (await _http.GetFromJsonAsync<CronFolderChoicesDto>($"/cron/folder-choices?machine={Mac}", Json))!;

        Assert.Equal(DirectorOs.MacOs, choices.Os);
        Assert.Equal("Mac", choices.OsName);
        Assert.True(choices.Online);
        Assert.Equal("/Users/<you>/Documents/my-project", choices.Placeholder);
        Assert.Null(choices.Note);
        Assert.Equal("/Users/pat/Documents/project", Assert.Single(choices.Repositories).Path);
    }

    [Fact]
    public async Task FolderChoices_ForAnOfflineMachine_SayTheFolderIsCheckedOnTheFirstRun()
    {
        _online = false;

        var choices = (await _http.GetFromJsonAsync<CronFolderChoicesDto>($"/cron/folder-choices?machine={Mac}", Json))!;

        Assert.False(choices.Online);
        Assert.Equal("STUDIO_MAC is offline, so the folder cannot be checked when you save. It is checked when the schedule first runs.", choices.Note);
    }

    [Fact]
    public async Task FolderChoices_WithNoMachine_IsABadRequest()
    {
        var response = await _http.GetAsync("/cron/folder-choices");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
