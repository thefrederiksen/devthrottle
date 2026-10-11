using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Covers the tenant-scoped reads on POST /machines/{machine}/sessions: a spawn that names a workflow run or
/// a mission must be REFUSED cleanly when it does not exist, never fault.
///
/// WHAT THESE TESTS ACTUALLY PROVE, STATED NARROWLY BECAUSE A STRONGER CLAIM WAS INVESTIGATED AND DISPROVED.
/// <c>WorkflowRunStore.Get</c> and <c>.List</c> each open a context through
/// <c>GatewayDatabase.CreateContext</c>, which reads <c>ITenantContext.Current</c>; on hosted that is
/// <c>AsyncLocalTenantContext</c>, which THROWS by design when no scope is in effect. It was believed that
/// those reads ran outside any scope on this route and therefore answered 500 on hosted. They do not: on
/// hosted <c>GatewayHost</c> registers a middleware that resolves the request's tenant from the authenticated
/// device key and enters a scope around the WHOLE pipeline, so a device-key request arrives already scoped.
///
/// That was established by experiment rather than by reading: the route's own scope entry was moved back below
/// the reads and these tests still passed. So they are NOT a regression guard against an unscoped read - they
/// cannot fail that way - and they are not described as one. What they do hold is the behaviour a caller
/// depends on either way: an unknown run and an unknown mission are answered as bad requests, and a spawn that
/// names neither still fails for its own honest reason rather than being refused earlier.
///
/// A genuine guard for the unscoped-read theory would have to remove the middleware, not the route's line.
/// </summary>
public sealed class MachineSpawnWorkflowScopeTests : IAsyncLifetime
{
    private const string Token = "test-token";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string _aliceKey = "";
    private TenantId _aliceTenant;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-spawn-scope-" + Guid.NewGuid().ToString("N"));

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

        _aliceKey = _gateway.Devices.Register("dev-spawnscope-alice", "ALICE-PC").DeviceKey;
        _aliceTenant = _gateway.TenantRegistry.MintOrLookupBySubject("sub-spawnscope-alice", "spawnscope-alice@example.com");
        _gateway.Devices.SetAccountBinding("dev-spawnscope-alice", "sub-spawnscope-alice", _aliceTenant.Value);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch (Exception) { /* best effort */ }
    }

    private Task<HttpResponseMessage> Spawn(object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "machines/ALICE-PC/sessions")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _aliceKey);
        return _http.SendAsync(request);
    }

    /// <summary>
    /// An unknown run must be REFUSED, which requires the read to have happened and returned. A fault here
    /// would mean the read threw - the state the scoping is there to prevent.
    /// </summary>
    [Fact]
    public async Task A_spawn_naming_an_unknown_workflow_run_is_refused_and_does_not_fault()
    {
        var response = await Spawn(new
        {
            repoPath = @"C:\repo",
            agent = "ClaudeCode",
            workflowRunId = Guid.NewGuid(),
        });

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("unknown workflow run", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The other read. A mission-scoped spawn with no explicit run reaches <c>WorkflowRunStore.List</c> to
    /// auto-seat, which opens a context the same way <c>Get</c> does. An unknown mission is refused before
    /// that point, so this asserts the refusal rather than a fault.
    /// </summary>
    [Fact]
    public async Task A_spawn_naming_an_unknown_mission_is_refused_and_does_not_fault()
    {
        var response = await Spawn(new
        {
            repoPath = @"C:\repo",
            agent = "ClaudeCode",
            missionId = Guid.NewGuid(),
        });

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// The control. A spawn with neither identifier never touches the run store at all, so it must still fail
    /// for its own honest reason - reaching the spawner and finding no Director for that machine - rather than
    /// being refused earlier. Without this, the two tests above would still pass on a route that had begun
    /// rejecting every spawn.
    /// </summary>
    [Fact]
    public async Task A_spawn_with_no_workflow_or_mission_still_fails_for_its_own_reason()
    {
        var response = await Spawn(new { repoPath = @"C:\repo", agent = "ClaudeCode" });

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }
}
