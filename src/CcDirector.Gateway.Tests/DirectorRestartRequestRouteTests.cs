using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Pairing;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The restart-request routes on a REAL host - issue #2725: a real launcher stream carrying a real
/// declaration, a real tunnel Director answering the eligibility question and taking the cycle, and a
/// session key minted the way a Director mints one.
///
/// WHAT THIS PROVES THAT THE UNIT TESTS CANNOT: the guard. The service's tests prove the decisions; only a
/// booted host runs <c>SessionKeyGuard</c> in front of them, and the whole design is that the SAME session
/// key may ask and may not restart. So the first test asserts both halves with one key, and the 403 is the
/// exact one the 2026-09-06 probe recorded: <c>session_key_out_of_scope</c>.
///
/// NOTHING HERE RESTARTS ANYTHING. The stub launcher answers no commands because none reach it: the
/// accept hands the cycle to the Director, and the fake Director here only records that it was told.
/// </summary>
public sealed class DirectorRestartRequestRouteTests : IAsyncLifetime
{
    private const string Token = "restart-request-route-token";
    private const string Machine = "RESTART-REQUEST-MACHINE";
    private const string DirectorId = "director-restart-request-route";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private GatewayHost _gateway = null!;
    private HttpClient _owner = null!;
    private HttpClient _session = null!;
    private HubConnection? _launcher;
    private FakeTunnelDirector? _director;
    private readonly Guid _askingSession = Guid.NewGuid();

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-restart-request-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();

        _owner = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        // A session key for a session of the Director under test, registered exactly as the Director's
        // Hello registers them.
        var key = GatewaySessionKey.Mint();
        Assert.True(_gateway.SessionKeys.Register(TenantId.Local, DirectorId, _askingSession.ToString(),
            GatewaySessionKey.Hash(key), DateTime.UtcNow.AddHours(1)));
        _session = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _session.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public async Task DisposeAsync()
    {
        if (_launcher is not null) await _launcher.DisposeAsync();
        if (_director is not null) await _director.DisposeAsync();
        _owner.Dispose();
        _session.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { }
    }

    /// <summary>A launcher that can be told, and that declares the guard.</summary>
    private async Task LauncherThatDeclaresTheGuardAsync()
    {
        _gateway.Launchers.Upsert(TenantId.Local, new LauncherRegistrationRequest { MachineName = Machine, Pid = 4242, Version = "2.1.0" });
        var conn = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{_gateway.Port}/launcher-stream",
                options => options.AccessTokenProvider = () => Task.FromResult<string?>(Token))
            .Build();
        await conn.StartAsync();
        await conn.InvokeAsync("Hello", new LauncherStreamHello
        {
            MachineName = Machine,
            Version = "2.1.0",
            Capability = new LauncherCapabilityDeclaration
            {
                Commands = new List<string> { LauncherCapabilities.DirectorRestart, LauncherCapabilities.DirectorRestartOnlyIfEmpty },
                RestartSignalArmed = true,
                ServingRootIsInstanceHome = false,
                ServingRootKey = "b1706c7af60c",
            },
        });
        _launcher = conn;
    }

    /// <summary>A tunnel Director on the machine that says it is the launcher's Director and takes the cycle.</summary>
    private async Task DirectorThatAnswersAsync()
    {
        _director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId, Machine, cmd => cmd.Verb switch
        {
            DirectorRestartVerbs.Eligibility => FakeTunnelDirector.Ok(new DirectorRestartEligibilityDto { Eligible = true, Reason = "it is the launcher's Director" }),
            DirectorRestartVerbs.Cycle => FakeTunnelDirector.Ok(new { taken = true }),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}"),
        });
        await _director.PushSnapshotAsync(
            new SessionDto { SessionId = _askingSession.ToString(), DirectorId = DirectorId, Name = "Rig Alpha - Architect" },
            new SessionDto { SessionId = Guid.NewGuid().ToString(), DirectorId = DirectorId, Name = "Rig Alpha - Worker" });
    }

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static string S(JsonElement e, string name) => e.GetProperty(name).GetString() ?? "";

    private Task<HttpResponseMessage> Ask(HttpClient client, string reason = "the launcher has a staged update")
        => client.PostAsJsonAsync($"machines/{Machine}/director/restart-requests", new { reason });

    // =========================================================================================
    // THE PAIR: the same session key may ask, and still may not restart
    // =========================================================================================

    [Fact]
    public async Task A_session_key_can_create_a_request_and_still_cannot_restart_the_Director_directly()
    {
        await LauncherThatDeclaresTheGuardAsync();
        await DirectorThatAnswersAsync();

        // Half one: the ask is allowed and creates a pending record.
        var created = await Ask(_session);
        var body = await BodyOf(created);
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"expected 201, got {(int)created.StatusCode}: {body}");
        Assert.Equal("Pending", S(body, "state"));
        Assert.True(body.GetProperty("canAccept").GetBoolean());
        Assert.Equal(_askingSession.ToString(), S(body, "requestedBySessionId"));
        Assert.StartsWith("Rig Alpha - Architect", S(body, "requestedBySessionName"));
        Assert.Equal(2, body.GetProperty("liveSessionCount").GetInt32());
        Assert.Equal("CanRestart", S(body.GetProperty("capability"), "verdict"));
        Assert.Equal("Available", S(body.GetProperty("capability"), "guardedRestart"));

        // Half two: the direct restart is refused to the SAME key with the SAME 403 the 2026-09-06 probe
        // recorded. If this half ever goes green, the admission surface was widened.
        var direct = await _session.PostAsJsonAsync($"machines/{Machine}/director/restart", new { onlyIfEmpty = true });
        Assert.Equal(HttpStatusCode.Forbidden, direct.StatusCode);
        Assert.Contains("session_key_out_of_scope", await direct.Content.ReadAsStringAsync());

        // And the decision stays out of the session's hands too.
        var accept = await _session.PostAsync($"machines/{Machine}/director/restart-requests/{S(body, "id")}/accept", null);
        Assert.Equal(HttpStatusCode.Forbidden, accept.StatusCode);
        Assert.Contains("session_key_out_of_scope", await accept.Content.ReadAsStringAsync());

        // The record is readable by the session that asked.
        var read = await _session.GetAsync($"machines/{Machine}/director/restart-requests/{S(body, "id")}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var list = await _session.GetAsync("gateway/director-restart-requests");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Single((await BodyOf(list)).GetProperty("requests").EnumerateArray());
    }

    // =========================================================================================
    // Refused BEFORE any approval exists, in Phase 1's words
    // =========================================================================================

    [Fact]
    public async Task A_machine_that_cannot_be_restarted_is_refused_before_any_approval_exists_in_Phase_1s_words()
    {
        // A Director on the machine, but NO launcher at all. Phase 1's answer for that machine, taken from
        // Phase 1's own route so the words compared are Phase 1's and not this file's.
        await DirectorThatAnswersAsync();
        var capability = await BodyOf(await _owner.GetAsync($"machines/{Machine}/restart-capability"));
        Assert.Equal("CannotRestart", S(capability, "verdict"));

        var refused = await Ask(_session);
        var body = await BodyOf(refused);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("cannot_restart", S(body, "code"));
        Assert.Contains(S(capability, "reason"), S(body, "error"));
        Assert.Contains("no launcher is registered", S(body, "error"));

        // No approval exists: nothing to accept, nothing listed.
        var list = await BodyOf(await _owner.GetAsync("gateway/director-restart-requests"));
        Assert.Empty(list.GetProperty("requests").EnumerateArray());
    }

    // =========================================================================================
    // One pending request per machine
    // =========================================================================================

    [Fact]
    public async Task A_second_request_while_one_is_pending_is_refused()
    {
        await LauncherThatDeclaresTheGuardAsync();
        await DirectorThatAnswersAsync();

        var first = await BodyOf(await Ask(_session));
        var second = await Ask(_session, "asking again");
        var body = await BodyOf(second);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("request_already_pending", S(body, "code"));
        Assert.Contains(S(first, "id"), S(body, "error"));
    }

    // =========================================================================================
    // An approval older than its expiry is refused
    // =========================================================================================

    [Fact]
    public async Task An_approval_older_than_thirty_minutes_is_refused()
    {
        await LauncherThatDeclaresTheGuardAsync();
        await DirectorThatAnswersAsync();
        var created = await BodyOf(await Ask(_session));
        var id = S(created, "id");

        // Thirty minutes pass on the store's clock.
        var later = DateTime.UtcNow + Api.DirectorRestartRequestStore.Expiry + TimeSpan.FromSeconds(1);
        _gateway.DirectorRestartRequests.Clock = () => later;

        var accept = await _owner.PostAsync($"machines/{Machine}/director/restart-requests/{id}/accept", null);
        var body = await BodyOf(accept);

        Assert.Equal(HttpStatusCode.Conflict, accept.StatusCode);
        Assert.Equal("request_expired", S(body, "code"));
        Assert.Equal("Expired", S(body.GetProperty("request"), "state"));
        // And the Director was never told to begin.
        Assert.NotEqual(DirectorRestartVerbs.Cycle, _director!.LastCommand?.Verb);
    }

    // =========================================================================================
    // The accept hands the cycle to the Director, once
    // =========================================================================================

    [Fact]
    public async Task The_owners_accept_hands_the_cycle_to_the_Director_and_a_second_accept_does_not()
    {
        await LauncherThatDeclaresTheGuardAsync();
        await DirectorThatAnswersAsync();
        var created = await BodyOf(await Ask(_session));
        var id = S(created, "id");

        var accept = await _owner.PostAsync($"machines/{Machine}/director/restart-requests/{id}/accept", null);
        var body = await BodyOf(accept);

        Assert.True(accept.StatusCode == HttpStatusCode.OK, $"expected 200, got {(int)accept.StatusCode}: {body}");
        Assert.Equal("Accepted", S(body, "state"));
        Assert.False(body.GetProperty("canAccept").GetBoolean());

        // The Director was handed the cycle, with the request named on it.
        Assert.Equal(DirectorRestartVerbs.Cycle, _director!.LastCommand?.Verb);
        var order = JsonSerializer.Deserialize<DirectorRestartCycleOrder>(_director.LastCommand!.PayloadJson, Web)!;
        Assert.Equal(id, order.RequestId);
        Assert.Equal(Machine, order.Machine);

        // A second accept is refused and sends nothing more.
        _director.OnCommand(cmd => throw new InvalidOperationException("a second command reached the Director"));
        var again = await _owner.PostAsync($"machines/{Machine}/director/restart-requests/{id}/accept", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("request_not_pending", S(await BodyOf(again), "code"));

        // The Director's report lands on the record on its own credential, and closes it.
        var report = await _owner.PostAsJsonAsync($"machines/{Machine}/director/restart-requests/{id}/report",
            new { state = "Abandoned", progress = "this Director build carries no drain", workspaceId = (string?)null });
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        var closed = await BodyOf(await _owner.GetAsync($"machines/{Machine}/director/restart-requests/{id}"));
        Assert.Equal("Abandoned", S(closed, "state"));
        Assert.Contains("carries no drain", S(closed, "stateReason"));
    }

    [Fact]
    public async Task The_owners_decline_closes_the_request_and_the_Director_is_never_told()
    {
        await LauncherThatDeclaresTheGuardAsync();
        await DirectorThatAnswersAsync();
        var id = S(await BodyOf(await Ask(_session)), "id");

        var decline = await _owner.PostAsJsonAsync($"machines/{Machine}/director/restart-requests/{id}/decline", new { reason = "not during the demo" });
        var body = await BodyOf(decline);

        Assert.Equal(HttpStatusCode.OK, decline.StatusCode);
        Assert.Equal("Declined", S(body, "state"));
        Assert.Contains("not during the demo", S(body, "stateReason"));
        Assert.NotEqual(DirectorRestartVerbs.Cycle, _director!.LastCommand?.Verb);
    }
}
