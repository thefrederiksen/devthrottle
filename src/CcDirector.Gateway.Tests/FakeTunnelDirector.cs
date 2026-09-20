using System.Net.Sockets;
using System.Net;
using System.Text.Json;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection; // AddMessagePackProtocol (client)

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gateway Cleanup mission (the cut): a reusable stand-in for a tunnel-connected Director in Gateway tests.
///
/// After the cut the Gateway NEVER dials a Director over HTTP - it reads the roster from the PUSH store and
/// dispatches every session/director verb over THE TUNNEL (the two-way SignalR stream). So a test that used to
/// register a fake Kestrel Director and expect the Gateway to HTTP-dial it must now: (1) register the Director
/// UNREACHABLE (nothing listens on its advertised endpoint), (2) open a real hub connection to
/// <c>/director-stream</c> and say <c>Hello</c>, (3) push its sessions with <c>PushSnapshot</c>, and (4) answer
/// the Gateway's <c>Command</c> invocations with a per-verb dispatcher. Because the advertised endpoint is dead,
/// any working result proves the Gateway rode the tunnel, never an HTTP dial (tunnel-by-construction).
///
/// This wraps that setup so each test only supplies its verb dispatcher and the sessions to push. It mirrors
/// the seam proven in <c>TunnelDirectorReadProofTests</c> / <c>TunnelRosterPushReadProofTests</c>.
/// </summary>
public sealed class FakeTunnelDirector : IAsyncDisposable
{
    /// <summary>Web-shaped JSON for verb result bodies, matching what the real Director cores emit.</summary>
    public static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly HubConnection _conn;
    // Held for this fake's whole life: the registered tailnet endpoint must STAY unreachable, so the port
    // stays reserved-and-refusing until DisposeAsync. Releasing it early would let another process bind the
    // address this fake advertises as dead (issue #1156).
    private readonly DeadPortReservation _deadEndpoint;
    private Func<DirectorCommand, DirectorCommandResult> _dispatch;
    private long _sequence;

    /// <summary>The id this Director registered + connected under.</summary>
    public string DirectorId { get; }

    /// <summary>The last command the Gateway sent over the tunnel, so a test can assert the verb + payload.</summary>
    public DirectorCommand? LastCommand { get; private set; }

    private FakeTunnelDirector(string directorId, HubConnection conn, Func<DirectorCommand, DirectorCommandResult> dispatch, DeadPortReservation deadEndpoint)
    {
        DirectorId = directorId;
        _conn = conn;
        _dispatch = dispatch;
        _deadEndpoint = deadEndpoint;
    }

    /// <summary>
    /// Register the Director UNREACHABLE in the Gateway and open its tunnel connection. The default verb
    /// dispatcher fails every verb with BadRequest; call <see cref="OnCommand"/> (or pass one) to answer verbs.
    /// </summary>
    /// <param name="gateway">A started, streamMode Gateway.</param>
    /// <param name="token">The Gateway auth token.</param>
    /// <param name="directorId">The Director id to register + connect under.</param>
    /// <param name="machineName">The advertised machine name (defaults to this machine so local-only reads surface it).</param>
    /// <param name="dispatch">Optional per-verb dispatcher; may be set later with <see cref="OnCommand"/>.</param>
    /// <param name="changesOwner">Say on Hello that this Director carries out <c>set-controller</c> (hand over).</param>
    /// <param name="changesOwnerIfExpected">Say on Hello that it makes that change only while the owner is the expected one.</param>
    public static async Task<FakeTunnelDirector> StartAsync(
        GatewayHost gateway,
        string token,
        string directorId,
        string? machineName = null,
        Func<DirectorCommand, DirectorCommandResult>? dispatch = null,
        bool changesOwner = false,
        bool changesOwnerIfExpected = false)
    {
        // Registered UNREACHABLE: nothing listens on this advertised endpoint, so any working result
        // could only have come over the tunnel. The port is RESERVED for the fake's lifetime rather than
        // probed-and-released, so no other process can start answering on it mid-test (issue #1156).
        var deadEndpoint = DeadPortReservation.Reserve();
        gateway.Registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = directorId,
            TailnetEndpoint = deadEndpoint.LoopbackUrl,
            MachineName = machineName ?? Environment.MachineName,
            Pid = 1,
            Version = "test",
            StartedAt = DateTime.UtcNow,
        });

        var conn = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{gateway.Port}/director-stream",
                o => o.AccessTokenProvider = () => Task.FromResult<string?>(token))
            .AddMessagePackProtocol()
            .Build();

        var fake = new FakeTunnelDirector(directorId, conn,
            dispatch ?? (cmd => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}")),
            deadEndpoint);

        conn.On<DirectorCommand, DirectorCommandResult>("Command", cmd =>
        {
            fake.LastCommand = cmd;
            return fake._dispatch(cmd);
        });

        await conn.StartAsync();
        // Gateway Cleanup mission (tunnel-only): the Hello now carries the Director identity and IS the
        // registration (DirectorHub.Hello -> DirectorRegistry.RegisterFromStream). Send the full identity so
        // the registered entry has the same machine name the Upsert above set - the stream registration
        // overwrites the (unreachable) HTTP entry, so a working result still proves the tunnel carried it.
        await conn.InvokeAsync("Hello", new DirectorStreamHello
        {
            DirectorId = directorId,
            Version = "test",
            MachineName = machineName ?? Environment.MachineName,
            User = "test",
            Pid = 1,
            StartedAt = DateTime.UtcNow,
            ChangesOwner = changesOwner,
            ChangesOwnerIfExpected = changesOwnerIfExpected,
        });
        return fake;
    }

    /// <summary>Tell the Gateway a session is over, through the hub method a Director really calls when it reaps one.</summary>
    public Task RevokeSessionKeyAsync(string sessionId) => _conn.InvokeAsync("RevokeSessionKey", sessionId);

    /// <summary>Replace the per-verb dispatcher (e.g. after arranging test state).</summary>
    public void OnCommand(Func<DirectorCommand, DirectorCommandResult> dispatch) => _dispatch = dispatch;

    /// <summary>Push a session snapshot into the Gateway push store. Each call bumps the sequence.</summary>
    public Task PushSnapshotAsync(params SessionDto[] sessions) =>
        _conn.InvokeAsync("PushSnapshot", ++_sequence, sessions);

    /// <summary>
    /// Push the Director's FULL repository snapshot - what its root-folder scan currently finds - the way
    /// <c>ControlApiHost.WireRepositoryPush</c> does on every upsert, removal and completed scan, plus the
    /// ten-second reseed. Each call bumps the sequence.
    /// </summary>
    public Task PushRepoSnapshotAsync(params RepoStatusDto[] repositories) =>
        _conn.InvokeAsync("PushRepoSnapshot", ++_sequence, repositories);

    /// <summary>
    /// Push ONE session delta, the way a Director reports a single session changing. Each call bumps the
    /// sequence.
    ///
    /// IT IS NOT A ONE-SESSION SNAPSHOT, and the difference is the point of having it: the turn-end watcher is
    /// fed from the ACCEPTED DELTA and from nowhere else on this path, so a Working transition - the edge that
    /// ends a turn and supersedes a stored verdict - only happens when a delta carries it. A test that pushed a
    /// snapshot instead would change the roster and raise no edge at all.
    /// </summary>
    public Task PushDeltaAsync(SessionDto session) =>
        _conn.InvokeAsync("PushDelta", ++_sequence, session);

    /// <summary>Serialize a verb result body the way the real Director cores do.</summary>
    public static DirectorCommandResult Ok(object body) =>
        DirectorCommandResult.Success(JsonSerializer.Serialize(body, WebJson));

    public async ValueTask DisposeAsync()
    {
        try { await _conn.DisposeAsync(); } catch { /* best effort */ }
        _deadEndpoint.Dispose();
    }
}
