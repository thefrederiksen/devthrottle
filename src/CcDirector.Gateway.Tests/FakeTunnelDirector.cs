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

    /// <summary>What the Gateway answered this Director's Hello with: its version and the hub methods it offers.</summary>
    public GatewayCapabilities? Capabilities { get; private set; }

    // Every command the Gateway sent over the tunnel, in the order it arrived.
    private readonly System.Collections.Concurrent.ConcurrentQueue<DirectorCommand> _received = new();

    /// <summary>
    /// The most recent command of ONE verb the Gateway sent over the tunnel, or null when it never sent that verb.
    ///
    /// A TEST ASKS FOR ITS OWN VERB, NEVER FOR "THE LAST COMMAND". The Gateway sends a Director commands of its own
    /// accord - pushing a waiting session makes it read that session's screen (the "screen-grid" verb) on a
    /// background task - so which command arrived last depends on how busy the machine is. This fake used to expose
    /// a single LastCommand, and on 12 October 2026 a release gate went red because a "screen-grid" landed after the
    /// "handover" a test had just been answered. There is no such property any more, on purpose.
    /// </summary>
    public DirectorCommand? LastOf(string verb) =>
        _received.LastOrDefault(c => string.Equals(c.Verb, verb, StringComparison.Ordinal));

    /// <summary>Every command of one verb the Gateway sent over the tunnel, in the order it arrived.</summary>
    public IReadOnlyList<DirectorCommand> AllOf(string verb) =>
        _received.Where(c => string.Equals(c.Verb, verb, StringComparison.Ordinal)).ToList();

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
    /// <param name="secretTransferPublicKey">The public half this Director says on Hello for receiving a sealed value;
    /// none by default, as a Director that cannot receive one.</param>
    /// <param name="osUser">The operating system user this Director says on Hello.</param>
    public static async Task<FakeTunnelDirector> StartAsync(
        GatewayHost gateway,
        string token,
        string directorId,
        string? machineName = null,
        Func<DirectorCommand, DirectorCommandResult>? dispatch = null,
        bool changesOwner = false,
        bool changesOwnerIfExpected = false,
        string secretTransferPublicKey = "",
        string osUser = "test")
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
            fake._received.Enqueue(cmd);
            return fake._dispatch(cmd);
        });

        await conn.StartAsync();
        // Gateway Cleanup mission (tunnel-only): the Hello now carries the Director identity and IS the
        // registration (DirectorHub.Hello -> DirectorRegistry.RegisterFromStream). Send the full identity so
        // the registered entry has the same machine name the Upsert above set - the stream registration
        // overwrites the (unreachable) HTTP entry, so a working result still proves the tunnel carried it.
        fake.Capabilities = await conn.InvokeAsync<GatewayCapabilities?>("Hello", new DirectorStreamHello
        {
            DirectorId = directorId,
            Version = "test",
            MachineName = machineName ?? Environment.MachineName,
            User = osUser,
            SecretTransferPublicKey = secretTransferPublicKey,
            Pid = 1,
            StartedAt = DateTime.UtcNow,
            ChangesOwner = changesOwner,
            ChangesOwnerIfExpected = changesOwnerIfExpected,
        });
        return fake;
    }

    /// <summary>Tell the Gateway a session is over, through the hub method a Director really calls when it reaps one.</summary>
    public Task RevokeSessionKeyAsync(string sessionId) => _conn.InvokeAsync("RevokeSessionKey", sessionId);

    /// <summary>Register a session's key, through the hub method a Director calls when it mints one (the hash only).
    /// The hub refuses it by throwing, which surfaces here as an exception.</summary>
    public Task RegisterSessionKeyAsync(string sessionId, string rawKey, DateTime expiresAtUtc) =>
        _conn.InvokeAsync("RegisterSessionKey", new SessionKeyRegistration
        {
            SessionId = sessionId,
            KeyHash = CcDirector.Core.Security.GatewaySessionKey.Hash(rawKey),
            ExpiresAtUtc = expiresAtUtc,
        });

    /// <summary>Tell the Gateway one session is gone, through the hub method a Director calls for a single removal.</summary>
    public Task RemoveSessionAsync(string sessionId) => _conn.InvokeAsync("RemoveSession", ++_sequence, sessionId);

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

    /// <summary>Send the machine's license usage report (License Switching, contract section 2.1), the way the key
    /// holder Director does. Each call bumps the sequence.</summary>
    public Task PushLicenseUsageAsync(LicenseUsageReport report) =>
        _conn.InvokeAsync("PushLicenseUsage", ++_sequence, report);

    /// <summary>Ask the Gateway to have a sign-in approved on another machine (License Switching, contract section
    /// 8.3), through the hub method a Director with no browser profile of its own for the account calls. The hub
    /// answers at once; the outcome arrives later as the <c>license-approval-result</c> command.</summary>
    public Task RequestLicenseApprovalAsync(LicenseApprovalRequest request) =>
        _conn.InvokeAsync(LicenseContract.RequestApprovalHubMethod, request);

    /// <summary>Ask which approver records name this Director's machine (License Switching phase 6, Track C).</summary>
    public Task<LicenseApproverAssignments> GetLicenseApproverAssignmentsAsync() =>
        _conn.InvokeAsync<LicenseApproverAssignments>(LicenseApproverContract.GetAssignmentsHubMethod);

    /// <summary>Report what this Director's approver checks found (License Switching phase 6, Track C).</summary>
    public Task<LicenseApproverCheckAnswer> ReportLicenseApproverChecksAsync(LicenseApproverCheckReport report) =>
        _conn.InvokeAsync<LicenseApproverCheckAnswer>(LicenseApproverContract.ReportChecksHubMethod, report);

    /// <summary>Close this Director's tunnel, as a machine that lost its connection does. A command it has not
    /// answered yet ends on the Gateway as a dropped tunnel.</summary>
    public Task StopTunnelAsync() => _conn.StopAsync();

    /// <summary>Serialize a verb result body the way the real Director cores do.</summary>
    public static DirectorCommandResult Ok(object body) =>
        DirectorCommandResult.Success(JsonSerializer.Serialize(body, WebJson));

    public async ValueTask DisposeAsync()
    {
        try { await _conn.DisposeAsync(); } catch { /* best effort */ }
        _deadEndpoint.Dispose();
    }
}
