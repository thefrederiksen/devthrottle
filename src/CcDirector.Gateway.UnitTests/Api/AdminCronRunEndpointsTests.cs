using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Util;
using Xunit;

namespace CcDirector.Gateway.Tests.Api;

/// <summary>
/// THE ADMINISTRATOR'S READ OF EVERY ACCOUNT'S SCHEDULED RUNS (issue #4403), through the real authentication middleware
/// and the real route on an in-memory server.
///
/// The refusals come first, because this route reads across accounts: no token, and a customer's own genuine
/// credentials, are both refused. Then the filters, each proved to narrow the answer and never silently ignored.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class AdminCronRunEndpointsTests : IAsyncLifetime
{
    private readonly AdminVisibilityTestHost _host = new();
    private readonly string? _priorAdmin = Environment.GetEnvironmentVariable(AdminTrialEndpoint.ServiceTokenEnvVar);

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable(AdminTrialEndpoint.ServiceTokenEnvVar, AdminVisibilityTestHost.AdminToken);
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable(AdminTrialEndpoint.ServiceTokenEnvVar, _priorAdmin);
        await _host.DisposeAsync();
    }

    private DateTime At(int hoursAgo) => _host.Now.AddHours(-hoursAgo);

    /// <summary>Account A: a run that went fine and one that did not start. Account B: one that did not report.</summary>
    private void SeedTwoAccounts()
    {
        _host.AddJob(_host.AccountA, "cj_morning", "Morning report");
        _host.AddJob(_host.AccountB, "cj_nightly", "Nightly sweep");
        _host.AddRun(_host.AccountA, "cj_morning", At(30), CronRunResults.Ok, resultUtc: At(29), sessionId: "sid-a-ok");
        _host.AddRun(_host.AccountA, "cj_morning", At(6), CronRunResults.Problem, CronRunProblems.DidNotRun,
            "it did not start a session: no Director is connected for machine workstation-a", At(6));
        _host.AddRun(_host.AccountB, "cj_nightly", At(3), CronRunResults.Problem, CronRunProblems.DidNotReport,
            "its session ended without reporting", At(2), sessionId: "sid-b-1");
    }

    private async Task<JsonElement> ReadAsync(string query)
    {
        var response = await _host.SendAsync(HttpMethod.Get, AdminCronRunEndpoints.Path + query, AdminVisibilityTestHost.AdminToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<JsonElement> RunsOf(JsonElement body) => body.GetProperty("runs").EnumerateArray().ToList();

    [Fact]
    public async Task Get_NoToken_IsRefused()
    {
        SeedTwoAccounts();

        var response = await _host.SendAsync(HttpMethod.Get, AdminCronRunEndpoints.Path, bearer: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("cj_morning", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_ACustomersOwnSessionKey_IsRefused()
    {
        SeedTwoAccounts();
        var (key, _) = _host.SessionKey(_host.AccountA, TimeSpan.FromHours(1));

        var response = await _host.SendAsync(HttpMethod.Get, AdminCronRunEndpoints.Path + "?account=" + _host.AccountA.Value, key);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("cj_morning", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_ACustomersOwnMachineCredential_IsRefused()
    {
        SeedTwoAccounts();

        var response = await _host.SendAsync(HttpMethod.Get, AdminCronRunEndpoints.Path, AdminVisibilityTestHost.MachineToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void ThePath_IsExemptFromTheDeviceGate_AndRefusedToEverySessionKey()
    {
        // The route carries its own service-token gate, like every administrator route; a session key never reaches it.
        var field = typeof(AuthMiddleware).GetField("PublicPaths",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert.Contains(AdminCronRunEndpoints.Path, ((IEnumerable<string>)field.GetValue(null)!).ToList());
        Assert.False(SessionKeyGuard.Check("GET", AdminCronRunEndpoints.Path).Allowed);
        Assert.False(SessionKeyGuard.Check("GET", AdminCronRunEndpoints.Path, raised: true).Allowed);
    }

    [Fact]
    public async Task Get_TheAdminToken_ListsEveryAccountsRuns_NewestFirst_WithAccountNameMachineAndReason()
    {
        SeedTwoAccounts();

        var body = await ReadAsync("");

        Assert.Equal("all-accounts", body.GetProperty("scope").GetString());
        var runs = RunsOf(body);
        Assert.Equal(3, runs.Count);
        Assert.Equal(new[] { "cj_nightly", "cj_morning", "cj_morning" }, runs.Select(r => r.GetProperty("job_id").GetString()));
        var nightly = runs[0];
        Assert.Equal(_host.AccountB.Value, nightly.GetProperty("account").GetString());
        Assert.Equal("Nightly sweep", nightly.GetProperty("job_name").GetString());
        Assert.Equal("sid-b-1", nightly.GetProperty("session_id").GetString());
        Assert.True(nightly.GetProperty("failed").GetBoolean());
        Assert.Equal(CronRunProblems.DidNotReport, nightly.GetProperty("problem").GetString());
        Assert.Equal("its session ended without reporting", nightly.GetProperty("reason").GetString());
        Assert.StartsWith("workstation-", nightly.GetProperty("machine").GetString());
        Assert.False(runs[2].GetProperty("failed").GetBoolean());
    }

    [Fact]
    public async Task Get_ByEmail_AndFailedOnly_ListsThatAccountsFailedRunsWithTheirReason()
    {
        SeedTwoAccounts();

        var body = await ReadAsync("?email=" + Uri.EscapeDataString(AdminVisibilityTestHost.EmailA) + "&outcome=failed");

        Assert.Equal("account", body.GetProperty("scope").GetString());
        var run = Assert.Single(RunsOf(body));
        Assert.Equal(_host.AccountA.Value, run.GetProperty("account").GetString());
        Assert.Equal("Morning report", run.GetProperty("job_name").GetString());
        Assert.Equal(CronRunProblems.DidNotRun, run.GetProperty("problem").GetString());
        Assert.Equal("not-started", run.GetProperty("status").GetString());
        Assert.Contains("no Director is connected", run.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Get_ByAccountAndJob_NarrowsToThatSchedule()
    {
        SeedTwoAccounts();
        _host.AddJob(_host.AccountA, "cj_other", "Other");
        _host.AddRun(_host.AccountA, "cj_other", At(1), CronRunResults.Pending);

        var all = RunsOf(await ReadAsync("?account=" + _host.AccountA.Value));
        var one = RunsOf(await ReadAsync("?account=" + _host.AccountA.Value + "&job=cj_other"));

        Assert.Equal(3, all.Count);
        Assert.Equal("cj_other", Assert.Single(one).GetProperty("job_id").GetString());
    }

    [Fact]
    public async Task Get_SinceAndUntil_WindowOnTheFireTime()
    {
        SeedTwoAccounts();

        var lastDay = RunsOf(await ReadAsync("?since=24h"));
        var before = RunsOf(await ReadAsync("?since=48h&until=24h"));

        Assert.Equal(2, lastDay.Count);
        Assert.Equal("sid-a-ok", Assert.Single(before).GetProperty("session_id").GetString());
    }

    [Fact]
    public async Task Get_TheDefaultWindowIsTheLastSevenDays()
    {
        _host.AddJob(_host.AccountA, "cj_old", "Old");
        _host.AddRun(_host.AccountA, "cj_old", _host.Now.AddDays(-8), CronRunResults.Ok);
        _host.AddRun(_host.AccountA, "cj_old", _host.Now.AddDays(-6), CronRunResults.Ok);

        Assert.Single(RunsOf(await ReadAsync("")));
        Assert.Equal(2, RunsOf(await ReadAsync("?since=30d")).Count);
    }

    [Fact]
    public async Task Get_Limit_CapsTheRows()
    {
        SeedTwoAccounts();

        var body = await ReadAsync("?limit=1");

        Assert.Equal("cj_nightly", Assert.Single(RunsOf(body)).GetProperty("job_id").GetString());
    }

    [Theory]
    [InlineData("?outcome=broken", HttpStatusCode.BadRequest)]
    [InlineData("?limit=0", HttpStatusCode.BadRequest)]
    [InlineData("?limit=501", HttpStatusCode.BadRequest)]
    [InlineData("?limit=ten", HttpStatusCode.BadRequest)]
    [InlineData("?since=yesterday", HttpStatusCode.BadRequest)]
    [InlineData("?since=1h&until=2h", HttpStatusCode.BadRequest)]
    [InlineData("?account=x&email=alpha%40example.com", HttpStatusCode.BadRequest)]
    [InlineData("?email=nobody%40example.com", HttpStatusCode.NotFound)]
    // A mistyped filter must never widen the read to every account.
    [InlineData("?acount=x", HttpStatusCode.BadRequest)]
    [InlineData("?outcome=failed&machine=workstation-a", HttpStatusCode.BadRequest)]
    public async Task Get_ABadFilter_IsRefusedWithAReason_NeverReadAsEverything(string query, HttpStatusCode expected)
    {
        SeedTwoAccounts();

        var response = await _host.SendAsync(HttpMethod.Get, AdminCronRunEndpoints.Path + query, AdminVisibilityTestHost.AdminToken);

        Assert.Equal(expected, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Get_ARunWhoseScheduleWasDeleted_KeepsTheNameFixedOnTheRun()
    {
        // No schedule row: the run's own stamped name is what is left.
        var record = new CronRunRecord
        {
            ScheduledUtc = At(2), FiredUtc = At(2), Machine = "m", InfraStatus = "started", TaskStatus = "unknown",
            Result = CronRunResults.Ok, JobName = "Gone schedule",
        };
        using (var ctx = _host.Db.CreateContext(_host.AccountA))
        {
            CronRunHistoryStore.AddInContext(ctx, _host.AccountA.Value, "cj_gone", record);
            ctx.SaveChanges();
        }

        var run = Assert.Single(RunsOf(await ReadAsync("")));

        Assert.Equal("Gone schedule", run.GetProperty("job_name").GetString());
    }
}
