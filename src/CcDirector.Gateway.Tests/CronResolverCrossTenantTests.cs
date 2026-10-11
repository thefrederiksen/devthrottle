using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Hosted Multi-Tenancy (audit H1, gap audit-e): a cron job resolves its target MACHINE to a Director through
/// <see cref="Running.RegistryDirectorTargetResolver"/>, which the Gateway used to feed the FLEET-GLOBAL
/// <c>DirectorRegistry.ListDirectors()</c>. A machine-name match against that global list could select ANOTHER
/// tenant's Director running on the same machine name and persist its DirectorId into this tenant's
/// <see cref="CronRunRecord.TargetDirectorId"/> - a cross-tenant id the caller cannot even address.
///
/// This drives the REAL cron fire over real HTTP: tenant B owns a Director "d-bob" on machine "SHARED_TARGET";
/// tenant A owns NONE there. Tenant A creates a cron job targeting "SHARED_TARGET" and runs it now. With the
/// resolver scoped to the caller's partition (the fix), A resolves NOTHING on that machine, so the run record
/// carries no target Director (empty) - crucially, never B's "d-bob". Reverting the Gateway wiring to
/// <c>registry.ListDirectors()</c> makes A's fire resolve B's "d-bob" and stamp it into A's run record, so both
/// assertions below go RED.
/// </summary>
public sealed class CronResolverCrossTenantTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private const string SharedMachine = "SHARED_TARGET";
    private const string BobDirectorId = "d-bob";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private FakeTunnelDirector _dirB = null!;

    private string _keyA = "";
    private string _keyB = "";

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-cron-xt-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = true });
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        _keyA = HostedTestEnrollment.Enroll(
            _gateway, "sub-alice", "alice@example.com", "dev-a", "MA").DeviceKey;
        _keyB = HostedTestEnrollment.Enroll(
            _gateway, "sub-bob", "bob@example.com", "dev-b", "MB").DeviceKey;

        // ONLY tenant B has a Director on SHARED_TARGET (bound to B's partition by its authenticated Hello).
        // Tenant A owns nothing on that machine, so a correctly tenant-scoped resolve finds nothing there.
        _dirB = await FakeTunnelDirector.StartAsync(_gateway, _keyB, BobDirectorId, SharedMachine);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _dirB.DisposeAsync();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task Cron_fire_never_resolves_another_tenants_director_on_the_same_machine()
    {
        // Tenant A creates a seed cron job targeting SHARED_TARGET and fires it now (both under A's scope).
        var job = new CronJobDto
        {
            Name = "audit-h1-repro",
            Enabled = true,
            ScheduleKind = "recurring",
            CronExpression = "0 0 * * *",
            TimeZoneId = "America/Chicago",
            Target = new CronJobTarget { Machine = SharedMachine },
            Action = new CronJobAction { RepoPath = @"D:\repo", Seed = "/help" },
        };
        var createResp = await Post("cron/jobs", _keyA, job);
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var created = await createResp.Content.ReadFromJsonAsync<CronJobDto>(JsonOpts);
        Assert.NotNull(created);

        var runResp = await Post($"cron/jobs/{created!.Id}/run", _keyA, new { });
        runResp.EnsureSuccessStatusCode();
        var record = await runResp.Content.ReadFromJsonAsync<CronRunRecord>(JsonOpts);
        Assert.NotNull(record);

        // The security assertion: A's run never named B's Director...
        Assert.NotEqual(BobDirectorId, record!.TargetDirectorId);
        // ...and, with A owning nothing on that machine, resolved to no Director at all (empty), never a
        // cross-tenant one. On revert this becomes "d-bob".
        Assert.Equal(string.Empty, record.TargetDirectorId);
    }

    private Task<HttpResponseMessage> Post(string path, string deviceKey, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        return _http.SendAsync(req);
    }

}
