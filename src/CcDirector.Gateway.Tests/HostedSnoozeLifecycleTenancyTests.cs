using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The FULL two-tenant snooze lifecycle over the REAL hosted wire, with the SAME session id on both tenants.
///
/// The owner reported that the Snooze button on the hosted mobile voice screen "does nothing". The voice
/// screen's Snooze is the same verb the roster uses: useSessionManage.toggleHold -> holdSession(sid) ->
/// POST /sessions/{sid}/hold, and it renders its held state from the SAME /sessions roster the Home page
/// polls. So a snooze that does not work in voice mode is a snooze whose WRITE (the hold endpoint records
/// it in the Gateway snooze registry) and READ (the roster fold reads that registry back) do not agree.
///
/// #1909 (f575e7ee) gave the snoozes table a COMPOSITE (tenant_id, SessionId) primary key. That table was
/// ALREADY tenant-scoped before #1909 (a tenant_id column and a global query filter), so the READ isolation
/// this asserts held before #1909 too; what the composite key adds is that a SECOND tenant can WRITE a snooze
/// for a session id the first tenant already holds without hitting a PRIMARY KEY violation. On a SessionId-only
/// key the store's upsert (read through the tenant filter, INSERT when it finds nothing) would insert a second
/// row with the same SessionId and throw, so the second tenant's Snooze click 500s. That is the exact edge a
/// bare status-code or single-tenant test cannot see, and it is why these tenants deliberately COLLIDE on one
/// session id.
///
/// WHAT IS ASSERTED, in both directions:
///  - the owner's own snooze is visible to the owner (roster HoldState=Held, a running SnoozeUntil clock) and
///    unsnoozes cleanly;
///  - the colliding tenant does NOT see the owner's snooze on the same id, CAN set its own snooze on that same
///    id without collision (the #1909 composite-key proof - a SessionId-only key 500s here), and clearing the
///    colliding tenant's snooze does NOT clear the owner's.
///
/// This drives a REAL GatewayHost over REAL HTTP through the REAL auth middleware, with two REAL tunnel
/// Directors on two different tenants each pushing a session under the SAME id - the same wire path a
/// production Director uses.
/// </summary>
public sealed class HostedSnoozeLifecycleTenancyTests : IAsyncLifetime
{
    private const string Token = "test-token";
    // The COLLIDING id: both tenants own a session under this exact id. Isolation must hold per id, per tenant.
    private const string Shared = "shared-sid";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private FakeTunnelDirector _dirA = null!;
    private FakeTunnelDirector _dirB = null!;
    private string _keyA = "";
    private string _keyB = "";

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-snooze-" + Guid.NewGuid().ToString("N"));

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

        // Both fake Directors answer every verb (including the set-display-state push the hold endpoint fires),
        // so a hold that reaches its Director gets a real answer and any failure can only be the registry step.
        _dirA = await FakeTunnelDirector.StartAsync(_gateway, _keyA, "dir-a", "MA", dispatch: AnswerAnything);
        _dirB = await FakeTunnelDirector.StartAsync(_gateway, _keyB, "dir-b", "MB", dispatch: AnswerAnything);
        // The COLLISION: the very same session id lives under both tenants. Idle, so a hold ARMS immediately
        // (a running clock) rather than deferring - the ordinary "park this quiet session" case.
        await _dirA.PushSnapshotAsync(Sample(Shared));
        await _dirB.PushSnapshotAsync(Sample(Shared));
    }

    private static DirectorCommandResult AnswerAnything(DirectorCommand cmd) =>
        FakeTunnelDirector.Ok(new { ok = true, lines = Array.Empty<string>(), items = Array.Empty<object>() });

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _dirA.DisposeAsync();
        await _dirB.DisposeAsync();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task Owner_snooze_is_visible_to_the_owner_and_invisible_to_a_colliding_tenant()
    {
        // Alice snoozes the shared id. Idle -> armed, so the response is OnHold=true (a landed hold) and the
        // roster reads back Held with a running clock.
        var held = await Hold(Shared, _keyA, onHold: true, minutes: 60);
        Assert.Equal(HttpStatusCode.OK, held.StatusCode);
        Assert.True((await Body(held)).GetProperty("onHold").GetBoolean(),
            "Alice's armed snooze on an Idle session should report OnHold=true");

        var mineAfter = await RosterSession(Shared, _keyA);
        Assert.True(mineAfter.GetProperty("onHold").GetBoolean(), "the owner's own roster must show the snooze held");
        Assert.Equal(HoldStates.Held, mineAfter.GetProperty("holdState").GetString());
        Assert.False(IsNull(mineAfter, "snoozeUntil"), "an armed snooze must carry a running SnoozeUntil clock");

        // Bob owns a session under the SAME id, and he never snoozed it. The owner's snooze must NOT leak onto
        // his roster - the read is scoped to his tenant's partition.
        var his = await RosterSession(Shared, _keyB);
        Assert.False(his.GetProperty("onHold").GetBoolean(),
            "the colliding tenant must NOT see the owner's snooze on the same session id");
        Assert.Equal(HoldStates.None, his.GetProperty("holdState").GetString());
    }

    [Fact]
    public async Task A_colliding_tenant_snoozes_the_same_id_without_collision_and_clears_do_not_cross()
    {
        // Alice arms the shared id.
        Assert.Equal(HttpStatusCode.OK, (await Hold(Shared, _keyA, onHold: true, minutes: 60)).StatusCode);

        // Bob snoozes the SAME id. This is the #1909 composite-key proof: on a SessionId-only primary key his
        // upsert inserts a second row with an id Alice already holds and the write 500s. It must be a clean 200.
        var bobHeld = await Hold(Shared, _keyB, onHold: true, minutes: 60);
        Assert.Equal(HttpStatusCode.OK, bobHeld.StatusCode);
        Assert.True((await Body(bobHeld)).GetProperty("onHold").GetBoolean());

        // Both tenants now hold the same id in their own partitions.
        Assert.True((await RosterSession(Shared, _keyA)).GetProperty("onHold").GetBoolean());
        Assert.True((await RosterSession(Shared, _keyB)).GetProperty("onHold").GetBoolean());

        // Bob unsnoozes. His clear must touch only HIS row - Alice's snooze on the same id must survive.
        Assert.Equal(HttpStatusCode.OK, (await Hold(Shared, _keyB, onHold: false, minutes: null)).StatusCode);
        Assert.False((await RosterSession(Shared, _keyB)).GetProperty("onHold").GetBoolean(),
            "Bob's own unsnooze must clear Bob's snooze");
        Assert.True((await RosterSession(Shared, _keyA)).GetProperty("onHold").GetBoolean(),
            "the colliding tenant's unsnooze must NOT clear the owner's snooze on the same id");

        // And Alice can unsnooze her own cleanly.
        Assert.Equal(HttpStatusCode.OK, (await Hold(Shared, _keyA, onHold: false, minutes: null)).StatusCode);
        Assert.False((await RosterSession(Shared, _keyA)).GetProperty("onHold").GetBoolean());
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private Task<HttpResponseMessage> Hold(string sid, string deviceKey, bool onHold, int? minutes)
    {
        var body = minutes is int m
            ? $"{{\"onHold\":{(onHold ? "true" : "false")},\"snoozeMinutes\":{m}}}"
            : $"{{\"onHold\":{(onHold ? "true" : "false")}}}";
        return Send("POST", $"sessions/{sid}/hold", deviceKey, body);
    }

    /// <summary>Fetch the roster for a tenant and return the JSON element for one session id (fails loud if absent).</summary>
    private async Task<JsonElement> RosterSession(string sid, string deviceKey)
    {
        var resp = await Send("GET", "sessions", deviceKey, null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        foreach (var s in doc.RootElement.EnumerateArray())
            if (s.TryGetProperty("sessionId", out var id) && id.GetString() == sid)
                return s.Clone();
        throw new Xunit.Sdk.XunitException($"session {sid} was not in the roster served to this tenant");
    }

    private static async Task<JsonElement> Body(HttpResponseMessage resp)
    {
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static bool IsNull(JsonElement obj, string prop) =>
        !obj.TryGetProperty(prop, out var v) || v.ValueKind == JsonValueKind.Null;

    private Task<HttpResponseMessage> Send(string method, string path, string deviceKey, string? body)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        if (body is not null)
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return _http.SendAsync(req);
    }

    private static SessionDto Sample(string sid) => new()
    {
        SessionId = sid,
        Agent = "claude",
        RepoPath = "/repo",
        ActivityState = "Idle",
        Status = "Running",
        StatusColor = "blue",
        CreatedAt = DateTime.UtcNow,
        LastActivityAt = DateTime.UtcNow,
    };

}
