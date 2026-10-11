using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE STANDARD, BOTH HALVES AT ONCE: a subscriber may drive EVERY machine registered to their own account,
/// and may NEVER reach one registered to another. On the launcher command stream specifically, which is the
/// only arm that can reach a launcher on a hosted Gateway.
///
/// WHAT THIS REPLACED. The hub used to be DENIED on hosted - not mapped at all - and the file this replaces
/// asserted that refusal. The deny was correct when it was written: LauncherHub.Hello resolved no tenant, and
/// LauncherConnectionRegistry keyed one active connection per BARE MACHINE NAME, so a launcher saying Hello
/// for machine X overwrote the row for machine X whoever owned it. One subscriber could supersede another's
/// active connection by claiming the same name, and then receive the commands meant for it.
///
/// Both of those conditions are now false. Hello resolves the tenant from the authenticated device key and
/// aborts when it resolves to none; the registry keys on (TenantId, Machine). So the assertions move from
/// "nobody may join" to "its owner may, and nobody else can see or displace them" - which is a strictly
/// stronger statement, because a hub that refuses everyone identically cannot demonstrate isolation at all.
/// It can only demonstrate absence.
///
/// WHY THE COST OF THE DENY WAS TOTAL RATHER THAN PARTIAL, which is what made replacing it urgent rather than
/// tidy: the stream is the ONLY arm that reaches a launcher - everywhere, since phase 6 deleted the
/// launcher's listener and the REST fallback with it (and even before that, the launcher's Kestrel bound
/// loopback only, so a hosted Gateway could never dial a remote machine). With the hub unmapped a
/// subscriber's launcher registered fine, heartbeated fine, listed fine - and could never receive one command.
///
/// EVERY CROSS-TENANT PROOF HERE OBSERVES THE ACT, NOT ONLY A STATUS CODE. Each launcher records the commands
/// it actually receives, so "Bob did not reach Alice's machine" is asserted against Alice's launcher having
/// received nothing - and the same-tenant tests are the control proving that instrument fires at all.
/// </summary>
[Collection("DirectorRoot")]
public sealed class HostedLauncherHubTenantTests : IAsyncLifetime
{
    private const string Token = "test-token-launcher-hub-tenant";

    /// <summary>
    /// ONE machine name, claimed by BOTH tenants. Every test uses the same string on purpose: a bare machine
    /// name is unique only WITHIN an account, and the collision it used to cause is the exact defect the deny
    /// existed to prevent. If the composite key ever regressed, these tests would fail rather than pass
    /// quietly on two conveniently different names.
    /// </summary>
    private const string SharedMachine = "SHARED-MACHINE-X";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-launcher-hub-tenant-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    private readonly List<LauncherCommand> _aliceReceived = new();
    private readonly List<LauncherCommand> _bobReceived = new();

    private string _aliceKey = "";
    private string _bobKey = "";
    private string _unboundKey = "";
    private TenantId _aliceTenant;
    private TenantId _bobTenant;

    public HostedLauncherHubTenantTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-launcher-hub-tenant-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _http?.Dispose();
        if (_gateway is not null) await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch (Exception) { }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (Exception) { }
    }

    private async Task StartHostedGatewayAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = true });
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        _aliceKey = _gateway.Devices.Register("dev-hub-alice", "ALICE-PC").DeviceKey;
        _aliceTenant = _gateway.TenantRegistry.MintOrLookupBySubject("sub-hub-alice", "hub-alice@example.com");
        _gateway.Devices.SetAccountBinding("dev-hub-alice", "sub-hub-alice", _aliceTenant.Value);

        _bobKey = _gateway.Devices.Register("dev-hub-bob", "BOB-PC").DeviceKey;
        _bobTenant = _gateway.TenantRegistry.MintOrLookupBySubject("sub-hub-bob", "hub-bob@example.com");
        _gateway.Devices.SetAccountBinding("dev-hub-bob", "sub-hub-bob", _bobTenant.Value);

        Assert.NotEqual(_aliceTenant.Value, _bobTenant.Value);

        _unboundKey = _gateway.Devices.Register("dev-hub-unbound", "NOBODY-PC").DeviceKey;
    }

    /// <summary>Dial the hub as a launcher would, recording every command that arrives into <paramref name="sink"/>.</summary>
    private async Task<HubConnection> JoinAsLauncherAsync(string deviceKey, List<LauncherCommand>? sink = null)
    {
        var conn = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{_gateway.Port}/launcher-stream", options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(deviceKey);
            })
            .Build();

        if (sink is not null)
        {
            conn.On<LauncherCommand, LauncherCommandResult>("Command", cmd =>
            {
                lock (sink) sink.Add(cmd);
                return Task.FromResult(LauncherCommandResult.Ok());
            });
        }

        await conn.StartAsync();
        await conn.InvokeAsync("Hello", new LauncherStreamHello
        {
            MachineName = SharedMachine,
            Version = "test",
        });
        return conn;
    }

    private Task<HttpResponseMessage> LaunchAsync(string deviceKey, string app)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"machines/{SharedMachine}/launch")
        {
            // confirmProtected: CR-5 gates every launch behind explicit confirmation; these tests are about
            // TENANT scoping of the relay, so they confirm and let the partition be the thing under test.
            Content = JsonContent.Create(new { app, confirmProtected = true }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        return _http.SendAsync(request);
    }

    private LauncherCommand[] AliceReceived() { lock (_aliceReceived) return _aliceReceived.ToArray(); }
    private LauncherCommand[] BobReceived() { lock (_bobReceived) return _bobReceived.ToArray(); }

    // =========================================================================================================
    // THE CAPABILITY: a subscriber reaches their own machines.
    // =========================================================================================================

    /// <summary>
    /// The plain case the deny removed entirely: a hosted subscriber's launcher joins and is registered under
    /// that subscriber's account. Before this change the connect itself failed, because the route was not
    /// mapped.
    /// </summary>
    [Fact]
    public async Task A_hosted_launcher_joins_and_is_registered_under_its_own_account()
    {
        await StartHostedGatewayAsync();

        await using var alice = await JoinAsLauncherAsync(_aliceKey);

        Assert.True(_gateway.LauncherConnections.IsStreamConnected(_aliceTenant, SharedMachine));
        Assert.NotNull(_gateway.LauncherConnections.GetActiveConnectionId(_aliceTenant, SharedMachine));
    }

    /// <summary>
    /// What the subscriber actually wants: starting something on their own machine reaches THEIR launcher,
    /// carrying what they asked for. The relay prefers the stream, so this is the arm under test.
    /// </summary>
    [Fact]
    public async Task A_launch_on_my_own_machine_reaches_my_launcher_with_what_I_asked_for()
    {
        await StartHostedGatewayAsync();
        await using var alice = await JoinAsLauncherAsync(_aliceKey, _aliceReceived);

        var response = await LaunchAsync(_aliceKey, "Chrome");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var received = Assert.Single(AliceReceived());
        Assert.Equal("launch", received.Verb);
        Assert.Equal("Chrome", received.App);
    }

    // =========================================================================================================
    // THE ISOLATION: nobody else reaches it, and the two do not collide.
    // =========================================================================================================

    /// <summary>
    /// THE PRECISE CASE THE DENY SAID WAS IMPOSSIBLE. Two accounts hold a live connection for the SAME machine
    /// name at the same time. Under the bare-name key the second Hello overwrote the first and the first
    /// subscriber silently stopped receiving; under the composite key they are two rows and both stand.
    ///
    /// This is the test that would have caught the original defect, and it is asserted on the registry rather
    /// than through a status code, because the collision was a write that succeeded rather than a request that
    /// was refused.
    /// </summary>
    [Fact]
    public async Task Two_accounts_hold_live_connections_for_the_same_machine_name_without_displacing_each_other()
    {
        await StartHostedGatewayAsync();

        await using var alice = await JoinAsLauncherAsync(_aliceKey);
        var aliceConnection = _gateway.LauncherConnections.GetActiveConnectionId(_aliceTenant, SharedMachine);

        await using var bob = await JoinAsLauncherAsync(_bobKey);

        // Both rows are live...
        Assert.True(_gateway.LauncherConnections.IsStreamConnected(_aliceTenant, SharedMachine));
        Assert.True(_gateway.LauncherConnections.IsStreamConnected(_bobTenant, SharedMachine));

        // ...they are DIFFERENT connections...
        var bobConnection = _gateway.LauncherConnections.GetActiveConnectionId(_bobTenant, SharedMachine);
        Assert.NotNull(bobConnection);
        Assert.NotEqual(aliceConnection, bobConnection);

        // ...and Bob joining did not move Alice's, which is the supersession itself.
        Assert.Equal(aliceConnection, _gateway.LauncherConnections.GetActiveConnectionId(_aliceTenant, SharedMachine));
    }

    /// <summary>
    /// The command follows the ACCOUNT, not the name. Alice and Bob both have a launcher joined for the same
    /// machine name; each one's launch must arrive at their own launcher and at no other. This is the whole
    /// requirement in one assertion pair.
    /// </summary>
    [Fact]
    public async Task A_launch_reaches_only_the_callers_own_launcher_even_when_both_claim_the_same_machine()
    {
        await StartHostedGatewayAsync();
        await using var alice = await JoinAsLauncherAsync(_aliceKey, _aliceReceived);
        await using var bob = await JoinAsLauncherAsync(_bobKey, _bobReceived);

        // Alice starts something on her machine.
        Assert.Equal(HttpStatusCode.OK, (await LaunchAsync(_aliceKey, "AliceApp")).StatusCode);
        Assert.Equal("AliceApp", Assert.Single(AliceReceived()).App);
        Assert.Empty(BobReceived());

        // Bob starts something, naming the SAME machine. It reaches HIS launcher, and Alice's is untouched -
        // still holding exactly the one command she asked for.
        Assert.Equal(HttpStatusCode.OK, (await LaunchAsync(_bobKey, "BobApp")).StatusCode);
        Assert.Equal("BobApp", Assert.Single(BobReceived()).App);
        Assert.Single(AliceReceived());
        Assert.Equal("AliceApp", AliceReceived()[0].App);
    }

    /// <summary>
    /// A subscriber with no launcher of their own does not fall through to somebody else's. Bob names Alice's
    /// machine while holding no connection for it: he must be told there is nothing there, and Alice's launcher
    /// must not be dialed on his behalf.
    /// </summary>
    [Fact]
    public async Task An_account_with_no_launcher_of_its_own_reaches_nobody_elses()
    {
        await StartHostedGatewayAsync();
        await using var alice = await JoinAsLauncherAsync(_aliceKey, _aliceReceived);

        var response = await LaunchAsync(_bobKey, "Chrome");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(AliceReceived());
    }

    /// <summary>
    /// Deny by default at the hub itself: an enrolled device bound to no account resolves to no tenant, so
    /// Hello aborts the connection rather than falling back to the Local or System partition. This is the
    /// property the deny's comment said the hub lacked.
    /// </summary>
    [Fact]
    public async Task A_device_bound_to_no_account_cannot_join_the_hub()
    {
        await StartHostedGatewayAsync();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var unbound = await JoinAsLauncherAsync(_unboundKey);
        });

        Assert.Null(_gateway.LauncherConnections.GetActiveConnectionId(TenantId.Local, SharedMachine));
    }

    // =========================================================================================================
    // The control.
    // =========================================================================================================

    /// <summary>
    /// Self-host is unchanged and is the control: the single tenant is Local, the composite key degenerates to
    /// the machine name it always was, and a launcher joins exactly as before. A change that broke self-host
    /// while satisfying every hosted assertion above would pass all of them and still be wrong.
    /// </summary>
    [Fact]
    public async Task The_hub_still_serves_on_self_host()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = false });
        await _gateway.StartAsync();

        await using var conn = await JoinAsLauncherAsync(Token);

        Assert.True(_gateway.LauncherConnections.IsStreamConnected(TenantId.Local, SharedMachine));
    }
}
