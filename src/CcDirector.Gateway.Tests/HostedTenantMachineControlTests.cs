using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Running;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE STANDARD THIS FILE ENFORCES, IN BOTH DIRECTIONS AT ONCE.
///
/// A tenant has FULL control of every device registered to its Gateway - hosted or self-hosted, no difference -
/// including starting and stopping sessions and the launcher on any of them, and including when an agent does
/// it on the owner's behalf. AND one subscriber must never see, start, stop or enumerate another subscriber's
/// machines. Those are not in tension; they are the same requirement stated from each side, and a change that
/// satisfies only one of them is a defect either way.
///
/// WHAT THIS REPLACED, AND WHY THE REPLACEMENT IS NOT A WEAKENING. The launcher and machine families used to be
/// DENIED OUTRIGHT on hosted (issue #1917) and this file used to assert that refusal verbatim. The leak the
/// deny closed was real - the family was tenant-blind by construction, so any authenticated device key could
/// enumerate every tenant's machines through GET /launchers and then run code on them through
/// POST /machines/{machine}/launch. But the deny closed it by removing cross-machine control from every hosted
/// tenant ON THEIR OWN MACHINES, which is the product's defining capability. The deny's own un-deny
/// instruction named the correct close: bind machine identity to a tenant at registration and authorize every
/// call against the calling tenant. That is now in place, so the assertions moved from "nobody may" to
/// "its owner may, and nobody else can" - which is a STRICTLY STRONGER statement about isolation, because a
/// refusal that answers everyone identically cannot distinguish a working partition from a broken one.
///
/// EVERY CROSS-TENANT PROOF HERE OBSERVES THE ACT, NOT ONLY THE STATUS CODE. A 404 to Bob would also be
/// produced by a Gateway that was simply broken. So each cross-tenant test additionally asserts the thing the
/// leak would have caused: Alice's stub launcher receives NOTHING down its stream, Alice's registry row is NOT
/// overwritten or removed, and the spawner is NOT entered on her behalf. The same-tenant tests are the control
/// that proves those instruments are live - the same stub launcher DOES receive its owner's commands.
///
/// THE TRANSPORT UNDER TEST IS THE STREAM, BECAUSE IT IS THE ONLY ONE. Phase 6 of the
/// remove-the-network-port mission deleted the launcher's REST interface and the Gateway's dial-out arm with
/// it; a command reaches a launcher only down the connection that launcher opened. The stub here is therefore
/// a REAL SignalR client joined to /launcher-stream with Alice's device key, exactly as her real launcher
/// would be - and the isolation being proved is the composite (tenant, machine) keying of that connection.
///
/// TWO REAL TENANTS ON A REAL HOSTED GATEWAY. Alice and Bob are minted through the real
/// <c>TenantRegistry</c> and hold real enrolled device keys bound through the real <c>DeviceRegistry</c>, so
/// the tenant each request resolves to comes from the authenticated credential exactly as it does in
/// production. Nothing here stubs the tenant boundary.
/// </summary>
public sealed class HostedTenantMachineControlTests : IAsyncLifetime
{
    private const string Token = "test-token";

    /// <summary>The machine Alice owns. Bob names this SAME string in every cross-tenant attempt, because the
    /// defect being closed is precisely that a bare machine name used to be enough to reach it.</summary>
    private const string AliceMachine = "ALICE-PC";

    private const int AliceLauncherPid = 4242;

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    private string _aliceKey = "";
    private string _bobKey = "";
    private string _unboundKey = "";
    private TenantId _aliceTenant;
    private TenantId _bobTenant;

    // A stub standing in for the real cc-launcher on Alice's machine: a SignalR client joined to the
    // launcher stream with HER key. Every command it receives is recorded, so "Bob reached Alice's
    // launcher" is a DIRECT assertion rather than an inference from a status code. If the isolation ever
    // regressed, THIS is what would be executing Bob's commands.
    private HubConnection? _aliceLauncherStream;
    private readonly List<string> _aliceLauncherHits = new();

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-tenant-machines-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        // EXPLICIT, not ambient: this class asserts HOSTED behaviour, so it states hosted mode on the host it builds
        // rather than inheriting whatever the runner happened to leave set.
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true,
            // These tests assert AUTH behaviour on the machine routes. The spawn route auto-launches a
            // Director and waits for it to appear; no Director ever appears here, so the control request used
            // to sit through production's full ninety-second wait - the single slowest test in the suite
            // (issue #1156). What is under test is which STATUS comes back, never how long the Gateway is
            // willing to wait for a Director, so the wait is injected short.
            directorLaunchTimeout: TimeSpan.FromMilliseconds(250),
            options: new GatewayHostOptions { Hosted = true });
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        // Two fully enrolled, tenant-bound device keys - one per account.
        _aliceKey = _gateway.Devices.Register("dev-tenantmachines-alice", "ALICE-PC").DeviceKey;
        _aliceTenant = _gateway.TenantRegistry.MintOrLookupBySubject("sub-tenantmachines-alice", "tenantmachines-alice@example.com");
        _gateway.Devices.SetAccountBinding("dev-tenantmachines-alice", "sub-tenantmachines-alice", _aliceTenant.Value);

        _bobKey = _gateway.Devices.Register("dev-tenantmachines-bob", "BOB-PC").DeviceKey;
        _bobTenant = _gateway.TenantRegistry.MintOrLookupBySubject("sub-tenantmachines-bob", "tenantmachines-bob@example.com");
        _gateway.Devices.SetAccountBinding("dev-tenantmachines-bob", "sub-tenantmachines-bob", _bobTenant.Value);

        Assert.NotEqual(_aliceTenant.Value, _bobTenant.Value);

        // A device that is enrolled but bound to NO account. On hosted it resolves to no tenant, which is a
        // deny-by-default 403 everywhere in this family - never a fall back to the Local or System partition.
        _unboundKey = _gateway.Devices.Register("dev-tenantmachines-unbound", "NOBODY-PC").DeviceKey;
    }

    public async Task DisposeAsync()
    {
        if (_aliceLauncherStream is not null) await _aliceLauncherStream.DisposeAsync();
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch (Exception) { /* best effort */ }
    }

    internal const string StubSentinel = "alice-launcher-actually-reached";

    /// <summary>Join Alice's stub launcher to the stream with HER device key (the hub binds the connection to
    /// her tenant from that key), answering every lifecycle verb and the generic launch and recording the hit,
    /// and put her presence row into HER registry partition.</summary>
    private async Task ConnectAliceLauncherAsync()
    {
        var conn = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{_gateway.Port}/launcher-stream", options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(_aliceKey);
            })
            .Build();

        conn.On<LauncherCommand, LauncherCommandResult>("Command", cmd =>
        {
            lock (_aliceLauncherHits) _aliceLauncherHits.Add(cmd.Verb);
            return Task.FromResult(LauncherCommandResult.Ok());
        });

        await conn.StartAsync();
        await conn.InvokeAsync("Hello", new LauncherStreamHello { MachineName = AliceMachine, Version = "1.2.3" });
        _aliceLauncherStream = conn;

        _gateway.Launchers.Upsert(_aliceTenant, new LauncherRegistrationRequest
        {
            MachineName = AliceMachine,
            Pid = AliceLauncherPid,
            Version = "1.2.3",
        });
    }

    private string[] AliceLauncherHits()
    {
        lock (_aliceLauncherHits) return _aliceLauncherHits.ToArray();
    }

    // =====================================================================================================
    // THE CAPABILITY: a hosted tenant controls its OWN machines.
    // =====================================================================================================

    [Fact]
    public async Task A_hosted_tenant_registers_lists_heartbeats_and_unregisters_its_own_machine()
    {
        // REGISTER through the route, then read the effect back out of the LISTING - a different route - so
        // neither can pass by being a no-op.
        var register = await Send("POST", "launchers/register", _aliceKey, new
        {
            machineName = AliceMachine,
            pid = AliceLauncherPid,
            version = "1.2.3",
        });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        var list = await GetLaunchers(_aliceKey);
        var entry = Assert.Single(list);
        Assert.Equal(AliceMachine, entry.MachineName);
        Assert.Equal(AliceLauncherPid, entry.Pid);
        Assert.Equal("1.2.3", entry.Version);

        // HEARTBEAT - 200 for her own machine, and the handler's OWN 410 for one she does not have.
        Assert.Equal(HttpStatusCode.OK,
            (await Send("POST", $"launchers/{AliceMachine}/heartbeat", _aliceKey)).StatusCode);
        Assert.Equal(HttpStatusCode.Gone,
            (await Send("POST", "launchers/NO-SUCH-MACHINE/heartbeat", _aliceKey)).StatusCode);

        // UNREGISTER - re-read the registry OBJECT directly, so the effect is observed at the write side and
        // not only through the route that reports it.
        Assert.Equal(HttpStatusCode.OK, (await Send("DELETE", $"launchers/{AliceMachine}", _aliceKey)).StatusCode);
        Assert.Null(_gateway.Launchers.Get(_aliceTenant, AliceMachine));
        Assert.Empty(await GetLaunchers(_aliceKey));
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("start")]
    [InlineData("stop")]
    public async Task A_hosted_tenant_drives_the_launcher_lifecycle_on_its_own_machine(string verb)
    {
        // THIS IS THE CAPABILITY THE DENY REMOVED, PROVED ON THE HOSTED GATEWAY. Not "not refused" - the
        // command actually arrives at the launcher's own handler, down the stream it opened.
        await ConnectAliceLauncherAsync();

        var resp = await Send("POST", $"machines/{AliceMachine}/director/{verb}", _aliceKey);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(AliceMachine, doc.RootElement.GetProperty("machine").GetString());
        Assert.Equal(verb, doc.RootElement.GetProperty("verb").GetString());
        Assert.Equal(200, doc.RootElement.GetProperty("relayStatus").GetInt32());

        // THE ACT: the launcher's own handler ran, which only the stream delivery can explain.
        Assert.Equal(new[] { $"director/{verb}" }, AliceLauncherHits());
    }

    [Fact]
    public async Task A_hosted_tenant_drives_a_generic_launch_on_its_own_machine()
    {
        await ConnectAliceLauncherAsync();

        // Tenant-boundary hardening (inspection finding I1-03): this test used to send
        // args = "/c echo hi" on a HOSTED Gateway, which is exactly the capability that finding
        // removed - a caller selecting a catalogued command interpreter and supplying the real
        // command in the argument string. The launch capability itself is unchanged and is still
        // proven here: the launch reaches the launcher's own handler. Only the caller-supplied
        // argument channel is gone on hosted.
        var resp = await Send("POST", $"machines/{AliceMachine}/launch", _aliceKey,
            new { path = @"C:\Windows\System32\cmd.exe", confirmProtected = true });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("launch", doc.RootElement.GetProperty("verb").GetString());
        Assert.Equal(new[] { "launch" }, AliceLauncherHits());
    }

    /// <summary>
    /// Inspection finding I1-03. The catalogue allowlist alone was never containment: every ordinary
    /// machine has a command interpreter or script host among its installed applications, so a caller
    /// could select that catalogued entry and put the real command in the ARGUMENT string, which the
    /// launcher interpolates into the command line it starts. On a hosted process the launch verb now
    /// accepts no caller-supplied arguments and no caller-supplied working directory.
    ///
    /// The refusal is proven the way everything in this file is proven - by OBSERVING THE ACT, not
    /// only the status code. A 403 would also be produced by a Gateway that was simply broken, so each
    /// case additionally asserts that Alice's launcher NEVER RECEIVED a command. The test above is
    /// the control that proves that instrument is live: the same launcher DOES receive the launch when
    /// the same tenant asks for the same application with no arguments.
    ///
    /// The empty and whitespace-only cases are inspection finding M03-I2B-02. The first version of
    /// this guard tested IsNullOrWhiteSpace and then forwarded the original object, so those values
    /// passed a rule that says no caller-supplied arguments and no caller-supplied working directory
    /// are accepted - and the three cases here only covered non-whitespace values, so nothing caught
    /// it. An empty working directory is not "no working directory" downstream: it moves the
    /// launcher's choice from the application's own directory to the process default. The rule is now
    /// about the FIELD BEING PRESENT rather than about what is in it.
    /// </summary>
    [Theory]
    [InlineData("/c whoami", null)]
    [InlineData(null, @"C:\Windows\System32")]
    [InlineData("/c whoami", @"C:\Windows\System32")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, "")]
    [InlineData(null, "   ")]
    [InlineData("", "")]
    [InlineData("\t", "\t")]
    public async Task A_hosted_launch_carrying_arguments_or_a_working_directory_is_refused(string? args, string? cwd)
    {
        await ConnectAliceLauncherAsync();

        var resp = await Send("POST", $"machines/{AliceMachine}/launch", _aliceKey,
            new { path = @"C:\Windows\System32\cmd.exe", args, cwd, confirmProtected = true });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var text = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        Assert.Equal("launch_arguments_not_allowed", doc.RootElement.GetProperty("error").GetString());

        // The refusal is EXPLICIT, not a silent drop: the caller is told, rather than having a
        // different program started on their behalf and reported as success.
        Assert.Contains("does not accept", text, StringComparison.Ordinal);

        // THE ACT: the launcher never received a command, so nothing ran.
        Assert.Empty(AliceLauncherHits());
    }

    [Fact]
    public async Task The_session_spawn_route_is_served_to_a_hosted_tenant_and_is_no_longer_refused()
    {
        // The route --machine uses. It is reached, it runs its OWN validation, and it answers in its own terms.
        // A 400 here is the handler talking; under the old deny this was a 404 refusal that never reached it.
        var bad = await Send("POST", $"machines/{AliceMachine}/sessions", _aliceKey, new { agent = "ClaudeCode" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("repoPath is required", await bad.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // =====================================================================================================
    // THE HARD CONSTRAINT: same tenant only. Bob names Alice's machine and reaches NOTHING.
    // =====================================================================================================

    [Fact]
    public async Task One_tenant_cannot_enumerate_another_tenants_machines()
    {
        // The listing is what makes every other cross-tenant attack AIMABLE: it used to return every machine
        // name and process id fleet-wide. Bob's listing must not carry any of Alice's.
        await ConnectAliceLauncherAsync();

        var bobsList = await GetLaunchers(_bobKey);
        Assert.Empty(bobsList);

        // Asserted on the RAW body too, so a future DTO field that leaked a machine name would redden here
        // rather than pass a typed assertion that simply does not look at it.
        var raw = await (await Send("GET", "launchers", _bobKey)).Content.ReadAsStringAsync();
        Assert.DoesNotContain(AliceMachine, raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(AliceLauncherPid.ToString(), raw, StringComparison.Ordinal);

        // The control: Alice's OWN listing does carry it. Without this, an unconditionally empty listing - a
        // broken route - would satisfy the assertions above while proving nothing about isolation.
        Assert.Equal(AliceMachine, Assert.Single(await GetLaunchers(_aliceKey)).MachineName);
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("start")]
    [InlineData("stop")]
    public async Task One_tenant_cannot_drive_the_launcher_lifecycle_on_another_tenants_machine(string verb)
    {
        await ConnectAliceLauncherAsync();

        var resp = await Send("POST", $"machines/{AliceMachine}/director/{verb}", _bobKey);

        // 404 "no launcher registered", the SAME answer a machine nobody registered gets. A distinguishable
        // "exists, but not yours" would let Bob enumerate Alice's machines one name at a time.
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("no launcher registered", await resp.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        // AND THE ACT ITSELF DID NOT HAPPEN: Alice's launcher received nothing down its stream. This is the
        // assertion that distinguishes a real partition from a Gateway that is merely broken.
        Assert.Empty(AliceLauncherHits());
    }

    [Fact]
    public async Task One_tenant_cannot_execute_code_on_another_tenants_machine()
    {
        // The sharpest form of the leak: POST /machines/{machine}/launch forwards a caller-supplied
        // program to the launcher, which runs it.
        await ConnectAliceLauncherAsync();

        // Bob CONFIRMS (CR-5's accident guard is not aimed at him) - the tenant partition alone must refuse.
        //
        // The body carries NO args and NO cwd deliberately. Since inspection finding I1-03 a hosted
        // Gateway refuses those outright, and that refusal fires before the machine is resolved - so a
        // body carrying them would be answered 403 "arguments not allowed" and this test would no longer
        // be proving anything about TENANCY. Keeping the body clean means the only thing that can refuse
        // it is the tenant partition, which is the whole point of this test.
        var resp = await Send("POST", $"machines/{AliceMachine}/launch", _bobKey,
            new { path = @"C:\Windows\System32\cmd.exe", confirmProtected = true });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Empty(AliceLauncherHits());
    }

    [Fact]
    public async Task One_tenant_cannot_commandeer_another_tenants_machine_by_registering_over_it()
    {
        // REGISTRATION OVERWRITE, still worth closing even with nothing to dial. POST /launchers/register
        // used to overwrite a machine's stored token, port and network address - outbound-request forgery
        // when the Gateway dialed launchers. The dial-out is gone (phase 6), but a tenant-blind registry
        // would still let Bob overwrite Alice's presence row and corrupt her fleet view. Bob registering
        // ALICE-PC now writes into BOB's partition and cannot touch hers.
        await ConnectAliceLauncherAsync();

        var resp = await Send("POST", "launchers/register", _bobKey, new
        {
            machineName = AliceMachine,
            pid = 1,
            version = "6.6.6",
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        // Alice's row is untouched, read straight off the registry: her pid, her version.
        var alice = _gateway.Launchers.Get(_aliceTenant, AliceMachine);
        Assert.NotNull(alice);
        Assert.Equal(AliceLauncherPid, alice!.Pid);
        Assert.Equal("1.2.3", alice.Version);

        // Bob's write landed in BOB's partition - the two rows share a machine name and are different rows.
        var bob = _gateway.Launchers.Get(_bobTenant, AliceMachine);
        Assert.NotNull(bob);
        Assert.Equal(1, bob!.Pid);
        Assert.Equal("6.6.6", bob.Version);

        // And Alice's lifecycle still reaches ALICE's launcher, down her own stream.
        Assert.Equal(HttpStatusCode.OK,
            (await Send("POST", $"machines/{AliceMachine}/director/start", _aliceKey)).StatusCode);
        Assert.Equal(new[] { "director/start" }, AliceLauncherHits());
    }

    [Fact]
    public async Task One_tenant_cannot_delete_or_heartbeat_another_tenants_machine()
    {
        await ConnectAliceLauncherAsync();

        // A heartbeat for a machine Bob does not have is unknown to HIM - 410, the handler's own answer - and
        // does not refresh or reveal Alice's row.
        Assert.Equal(HttpStatusCode.Gone,
            (await Send("POST", $"launchers/{AliceMachine}/heartbeat", _bobKey)).StatusCode);

        // The unregister route reports success because Bob removed what Bob has, which is nothing. The
        // assertion that matters is on the REGISTRY: Alice's row survives.
        await Send("DELETE", $"launchers/{AliceMachine}", _bobKey);
        Assert.NotNull(_gateway.Launchers.Get(_aliceTenant, AliceMachine));
        Assert.Equal(AliceMachine, Assert.Single(await GetLaunchers(_aliceKey)).MachineName);
    }

    [Fact]
    public async Task One_tenant_cannot_spawn_a_session_on_another_tenants_machine_and_never_falls_back_locally()
    {
        // The route --machine uses, from the wrong tenant. Bob's partition has no launcher for ALICE-PC, so the
        // resolve fails in HIS partition and the spawn fails LOUD with 502.
        await ConnectAliceLauncherAsync();

        var resp = await Send("POST", $"machines/{AliceMachine}/sessions", _bobKey,
            new { repoPath = @"C:\repo", agent = "ClaudeCode" });

        // 502, never a 201. THE NO-LOCAL-FALLBACK RULE: an unreachable remote target must fail loudly and must
        // never quietly become a session somewhere else.
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("sessionId", body, StringComparison.OrdinalIgnoreCase);

        // And Alice's launcher was never asked to start anything on her behalf.
        Assert.Empty(AliceLauncherHits());
    }

    // =====================================================================================================
    // DENY BY DEFAULT, AND THE AUTH GATE IN FRONT OF EVERYTHING.
    // =====================================================================================================

    /// <summary>The whole family, one row per path AND verb - a property proved on GET while a sibling POST
    /// still writes is not a property.</summary>
    public static TheoryData<string, string> EveryRoute => new()
    {
        { "POST",   "launchers/register" },
        { "POST",   $"launchers/{AliceMachine}/heartbeat" },
        { "DELETE", $"launchers/{AliceMachine}" },
        { "GET",    "launchers" },
        { "POST",   $"machines/{AliceMachine}/director/restart" },
        { "POST",   $"machines/{AliceMachine}/director/start" },
        { "POST",   $"machines/{AliceMachine}/director/stop" },
        { "POST",   $"machines/{AliceMachine}/launch" },
        { "POST",   $"machines/{AliceMachine}/sessions" },
    };

    [Theory]
    [MemberData(nameof(EveryRoute))]
    public async Task An_enrolled_key_bound_to_no_tenant_reaches_nothing(string verb, string path)
    {
        // DENY BY DEFAULT, ON EVERY ROUTE INCLUDING THE SPAWN. A key that resolves to no tenant must never
        // fall back to the Local or System partition, because that fallback is exactly what would hand an
        // unbound caller somebody's real fleet.
        //
        // IT IS REFUSED ONE GATE EARLIER THAN THE ROUTE, AND THAT IS WORTH STATING PRECISELY. On hosted,
        // DeviceRegistry.ResolveCredential treats a credential whose row carries no valid tenant binding as
        // REVOKED, so the host-wide auth middleware answers 401 and the handler never runs. The in-route 403
        // (ReqTenant -> NoTenant) is therefore a SECOND, INDEPENDENT gate rather than the one doing the work
        // here - and it stays, because a route in this family must not depend on the auth layer's current
        // policy to be safe. Asserting the 401 is asserting what actually happens; asserting a 403 would have
        // been asserting a path production never takes.
        await ConnectAliceLauncherAsync();

        var resp = await Send(verb, path, _unboundKey, BodyFor(path));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Empty(AliceLauncherHits());

        // The control: the SAME request shape with a properly bound key is NOT 401, so the refusal above is
        // about this key's missing tenant and not about the request being malformed.
        Assert.NotEqual(HttpStatusCode.Unauthorized,
            (await Send(verb, path, _aliceKey, BodyFor(path))).StatusCode);
    }

    [Fact]
    public void The_in_route_tenant_gate_is_a_second_independent_gate()
    {
        // The route-level deny-by-default, stated at the layer it lives in. HostedTenantBoundary is what every
        // route in this family resolves through, and on hosted it answers NULL for a key it cannot bind to a
        // tenant - never TenantId.Local and never TenantId.System. A route reading that null refuses.
        //
        // This is unit-level on purpose: the auth gate above means no real hosted request can carry an unbound
        // key this far, so the only honest way to prove the second gate is to ask it directly. If the auth
        // policy ever loosened, THIS is the gate that would still be holding.
        var boundary = new CcDirector.Gateway.Tenancy.HostedTenantBoundary(new AsyncLocalTenantContext(), _gateway.Devices, hosted: true);
        Assert.True(boundary.IsHosted);

        Assert.Null(boundary.ResolveForDeviceKey(_unboundKey));
        Assert.Null(boundary.ResolveForDeviceKey("not-a-key-at-all"));
        Assert.Null(boundary.ResolveForDeviceKey(null));

        // And the control: a properly bound key resolves to its OWN tenant, so the null above is a decision
        // and not a boundary that answers null to everything.
        Assert.Equal(_aliceTenant, boundary.ResolveForDeviceKey(_aliceKey));
        Assert.Equal(_bobTenant, boundary.ResolveForDeviceKey(_bobKey));
    }

    [Theory]
    [MemberData(nameof(EveryRoute))]
    public async Task An_unauthenticated_caller_is_rejected_before_any_route(string verb, string path)
    {
        // The control that the tenant checks above are the SECOND gate and not the only one: without a
        // credential the host-wide auth middleware still refuses first.
        await ConnectAliceLauncherAsync();

        var req = new HttpRequestMessage(new HttpMethod(verb), path);
        if (BodyFor(path) is { } body) req.Content = JsonBody(body);
        var resp = await _http.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Empty(AliceLauncherHits());
    }

    // =====================================================================================================
    // Helpers
    // =====================================================================================================

    /// <summary>A REAL, WELL-FORMED body for each write, so a refusal can never be a validation accident: the
    /// sessions route needs repoPath and register needs machineName, and either would answer 400 rather than
    /// the status under test.
    ///
    /// The launch body carries NO args and NO cwd for the same reason. Since inspection finding I1-03 a
    /// hosted Gateway refuses a launch that supplies either, and that refusal fires before the machine is
    /// resolved - so a body carrying them would be answered "arguments not allowed" and every row of this
    /// table would stop testing the thing it names. Well-formed now means clean.</summary>
    private static object? BodyFor(string path) =>
        path.EndsWith("/sessions", StringComparison.Ordinal)
            ? new { repoPath = @"C:\repo", agent = "ClaudeCode" }
            : path.EndsWith("launchers/register", StringComparison.Ordinal)
                ? new { machineName = AliceMachine, pid = 11, version = "1.0.0" }
                : path.EndsWith("/launch", StringComparison.Ordinal)
                    ? new { path = @"C:\Windows\System32\cmd.exe", confirmProtected = true }
                    : null;

    internal static HttpContent JsonBody(object value) =>
        new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private Task<HttpResponseMessage> Send(string verb, string path, string deviceKey, object? body = null)
    {
        var req = new HttpRequestMessage(new HttpMethod(verb), path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        if (body is not null) req.Content = JsonBody(body);
        return _http.SendAsync(req);
    }

    private async Task<List<LauncherDto>> GetLaunchers(string deviceKey)
    {
        var resp = await Send("GET", "launchers", deviceKey);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<List<LauncherDto>>())!;
    }
}

/// <summary>
/// Boots ONLY the launcher/machine routes on an ephemeral port and hands the caller back the registry, the
/// spawner's call counters, and a FAKE STREAM standing in for the launcher's persistent connection - the only
/// path a command can reach a launcher by since phase 6 deleted the REST dial-out. The fake records every
/// command, so a served dispatch is proved by an effect only delivery can explain, and a probe started with
/// <c>withStubLauncher: false</c> has NO stream at all, which is how the not-connected refusal is exercised.
/// </summary>
internal sealed class MachineGroupProbeHost : IAsyncDisposable
{
    public required WebApplication App { get; init; }
    public required HttpClient Http { get; init; }
    public required LauncherRegistry Launchers { get; init; }
    public required StubResolver Resolver { get; init; }
    public required List<string> StubLauncherHits { get; init; }

    public const string SpawnedSessionId = "sid-self-host-sentinel";

    /// <summary>A resolver that returns a fixed target and COUNTS its calls, so "the spawn never happened" is statable.</summary>
    internal sealed class StubResolver : IDirectorTargetResolver
    {
        public int ResolveCount { get; private set; }

        public Task<DirectorTargetResult> ResolveAsync(string machine, string? director, CancellationToken ct)
        {
            ResolveCount++;
            return Task.FromResult(new DirectorTargetResult("d-probe", null));
        }
    }

    public static async Task<MachineGroupProbeHost> StartAsync(bool withStubLauncher = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        var launchers = new LauncherRegistry();
        var resolver = new StubResolver();
        var spawner = new MachineSessionSpawner(resolver,
            (directorId, req, ct) => Task.FromResult<(bool, SessionDto?, string?)>(
                (true, new SessionDto { SessionId = SpawnedSessionId }, null)));

        // The fake launcher stream: resolves (tenant, machine) against the registry the way the production
        // hook resolves the connection registry, records the command, and answers Ok. Absent (null) when the
        // probe is started without a stub launcher - there is then NO path to any launcher.
        var hits = new List<string>();
        LauncherCommandRouter.SendLauncherCommandAsync? sendLauncherCommand = null;
        if (withStubLauncher)
        {
            sendLauncherCommand = (tenant, machine, command, _) =>
            {
                if (launchers.Get(tenant, machine) is null)
                    return Task.FromResult<LauncherCommandResult?>(null);
                lock (hits) hits.Add(command.Verb);
                return Task.FromResult<LauncherCommandResult?>(LauncherCommandResult.Ok());
            };
        }

        // Self-host control harness: the mode is passed as false, and the boundary is the REAL self-host one,
        // built over the SingleTenantContext, which always resolves Local. A null boundary is a refusal in
        // every mode, so the absence is no longer a self-host form.
        MachineEndpoints.Map(app, false, launchers, spawner,
            boundary: new CcDirector.Gateway.Tenancy.HostedTenantBoundary(
                new CcDirector.Core.Tenancy.SingleTenantContext(), new CcDirector.Gateway.Pairing.DeviceRegistry(), hosted: false),
            sendLauncherCommand: sendLauncherCommand, sessionFactoryOf: _ => CcDirector.Gateway.History.SessionFactoryLookup.NotKnown);

        await app.StartAsync();
        return new MachineGroupProbeHost
        {
            App = app,
            Http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) },
            Launchers = launchers,
            Resolver = resolver,
            StubLauncherHits = hits,
        };
    }

    /// <summary>Register the stub launcher under <paramref name="machine"/> so the fake stream resolves it.</summary>
    public void SeedStubLauncher(string machine) =>
        Launchers.Upsert(TenantId.Local, new LauncherRegistrationRequest
        {
            MachineName = machine,
            Pid = 1234,
            Version = "9.9.9",
        });

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await App.DisposeAsync();
    }
}
