using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The doorbell's WIRING in a real Gateway host (the Message Load mission, slice 2): a queued message plus a
/// session's settled edge sends <c>ring</c> down the tunnel to the owning Director with the unread count, and
/// the Director's answer is what decides whether the ring is counted. The schedule itself is proven on a fake
/// clock in FleetDoorbellTests; this proves the host joins the turn-end watcher, the store and the tunnel.
/// </summary>
public sealed class FleetDoorbellRouteTests : IAsyncLifetime
{
    private const string Token = "fleet-doorbell-route-token";
    private const string DirectorId = "director-fleet-doorbell-route";
    private const string Machine = "DOORBELL-MACHINE";

    private readonly string _manager = Guid.NewGuid().ToString();
    private readonly string _worker = Guid.NewGuid().ToString();
    private readonly ConcurrentQueue<DirectorCommand> _rings = new();
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-fleet-doorbell-route-" + Guid.NewGuid().ToString("N"));
    private string _answer = FleetRingOutcomes.Rung;

    private GatewayHost _gateway = null!;
    private FakeTunnelDirector _director = null!;
    private HttpClient _asManager = null!;

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();

        var key = GatewaySessionKey.Mint();
        Assert.True(_gateway.SessionKeys.Register(TenantId.Local, DirectorId, _manager, GatewaySessionKey.Hash(key), DateTime.UtcNow.AddHours(1)));
        _asManager = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _asManager.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

        _director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId, Machine, cmd =>
        {
            if (cmd.Verb == FleetDoorbellVerbs.Ring)
            {
                _rings.Enqueue(cmd);
                return FakeTunnelDirector.Ok(new FleetRingResponse
                {
                    Outcome = _answer,
                    Reason = _answer == FleetRingOutcomes.Deferred ? FleetRingDeferReasons.ComposerHoldsText : "",
                });
            }
            return FakeTunnelDirector.Ok(new { accepted = true });
        });
        await _director.PushSnapshotAsync(Row(_manager, null, "WaitingForInput"), Row(_worker, _manager, "Working"));
    }

    public async Task DisposeAsync()
    {
        await _director.DisposeAsync();
        _asManager.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
    }

    private static SessionDto Row(string sid, string? controller, string activity) => new()
    {
        SessionId = sid,
        DirectorId = DirectorId,
        Name = controller is null ? "Proof - Manager" : "Proof - Worker",
        MachineName = Machine,
        ActivityState = activity,
        ControllerSessionId = controller,
        LastActivityAt = DateTime.UtcNow,
    };

    private async Task<string> SendToWorkerAsync(string text)
    {
        var r = await _asManager.PostAsJsonAsync($"sessions/{_worker}/message", new { text });
        var body = await r.Content.ReadFromJsonAsync<FleetMessageSendResponse>();
        Assert.Equal("queued", body?.Status);
        return body!.MessageId!;
    }

    private int RingCountOf(string id)
    {
        using var ctx = _gateway.GatewayDatabaseForTests.CreateContext(TenantId.Local);
        return ctx.FleetMessages.Single(m => m.MessageId == id).RingCount;
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("Timed out waiting for " + what);
    }

    [Fact]
    public async Task A_working_session_is_not_rung_and_its_settled_edge_rings_it_with_the_unread_count()
    {
        await _director.PushDeltaAsync(Row(_worker, _manager, "Working"));
        var id = await SendToWorkerAsync("line one\nline two");

        // The message is queued while the worker works: nothing goes down the tunnel for it.
        await Task.Delay(500);
        Assert.Empty(_rings);

        await _director.PushDeltaAsync(Row(_worker, _manager, "WaitingForInput"));

        await WaitUntil(() => !_rings.IsEmpty, "a ring after the settled edge");
        var ring = Assert.Single(_rings);
        Assert.Equal(_worker, ring.SessionId);
        var payload = System.Text.Json.JsonSerializer.Deserialize<FleetRingRequest>(ring.PayloadJson, FakeTunnelDirector.WebJson)!;
        Assert.Equal(1, payload.UnreadCount);
        await WaitUntil(() => RingCountOf(id) == 1, "the ring to be recorded");
    }

    [Fact]
    public async Task A_deferred_answer_is_not_recorded_as_a_ring()
    {
        _answer = FleetRingOutcomes.Deferred;
        await _director.PushDeltaAsync(Row(_worker, _manager, "Working"));
        var id = await SendToWorkerAsync("please look");

        await _director.PushDeltaAsync(Row(_worker, _manager, "WaitingForInput"));
        await WaitUntil(() => !_rings.IsEmpty, "a ring after the settled edge");
        await Task.Delay(300);

        Assert.Equal(0, RingCountOf(id));
    }

    [Fact]
    public async Task The_heartbeat_rings_a_waiting_session_without_any_edge()
    {
        await _director.PushDeltaAsync(Row(_worker, _manager, "WaitingForInput"));
        await Task.Delay(300);
        _rings.Clear();
        var id = await SendToWorkerAsync("waiting already");

        await _gateway.FleetDoorbell.SweepAsync();

        Assert.Single(_rings);
        Assert.Equal(1, RingCountOf(id));
    }
}
