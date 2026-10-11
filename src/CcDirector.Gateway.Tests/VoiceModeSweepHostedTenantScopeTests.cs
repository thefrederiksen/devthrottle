using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Threading.Tasks;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The voice-mode sweep must actually SEND on the hosted Gateway (owner report, 2026-07-25).
///
/// The sweep is what makes voice mode a standing switch: the fan-out at POST /sessions/voice-mode/all reaches
/// the sessions alive at that instant, and the sweep carries the same intent to every session that appears
/// afterwards. On hosted it never switched a single session on. It decided correctly - it named the right
/// sessions every 15 seconds - and then threw every command away, because the DECIDING half ran inside the
/// per-tenant scope and the SENDING half ran after that scope had been left. <c>SendCommandAsync</c> resolves
/// a Director's stream within the tenant of the current unit of work and treats no-scope as a DENY, so each
/// command was dropped before it reached the tunnel and logged as an unreachable Director - for the whole
/// lifetime of the process, on a Director that was answering other verbs in the same millisecond.
///
/// The existing sweep proofs in <see cref="VoiceModeAllEndpointProofTests"/> could not catch this, and adding
/// more of them never would: they run SELF-HOST, where the ambient tenant is <c>TenantId.Local</c> whether a
/// scope was entered or not, so the missing scope has no effect there. Only a HOSTED Gateway can tell the two
/// apart. Hence this file boots one, with two tenants, and calls the sweep exactly as the timer does - from a
/// caller holding NO tenant scope of its own.
///
/// Revert-prove: move the send loop back outside <c>ITenantPass.ForEachTenantAsync</c> (plan inside a
/// synchronous <c>ForEachTenant</c>, act after it returns) and both tests below go RED - no voice-mode command
/// reaches either Director and neither session becomes a voice session.
/// </summary>
public sealed class VoiceModeSweepHostedTenantScopeTests : IAsyncLifetime
{
    private const string Token = "test-token-voice-sweep-hosted";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private FakeTunnelDirector _dirA = null!;
    private FakeTunnelDirector _dirB = null!;

    private HostedTestDevice _deviceA;
    private HostedTestDevice _deviceB;

    // Every voice-mode command each tenant's Director received. The sweep sends per tenant, so keeping them
    // apart is what proves each command was sent under the RIGHT tenant's scope rather than one shared one.
    private readonly ConcurrentBag<string> _voiceModeToA = new();
    private readonly ConcurrentBag<string> _voiceModeToB = new();

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-vmsweep-" + Guid.NewGuid().ToString("N"));

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

        _deviceA = HostedTestEnrollment.Enroll(_gateway, "sub-alice", "alice@example.com", "dev-a", "MACHINE-A");
        _deviceB = HostedTestEnrollment.Enroll(_gateway, "sub-bob", "bob@example.com", "dev-b", "MACHINE-B");

        // Each Director is registered UNREACHABLE and answers ONLY over the tunnel, so a command that lands
        // here can only have been routed - which is precisely what the no-scope DENY prevented.
        _dirA = await FakeTunnelDirector.StartAsync(_gateway, _deviceA.DeviceKey, "dir-a", "MACHINE-A",
            dispatch: cmd => Record(cmd, _voiceModeToA));
        _dirB = await FakeTunnelDirector.StartAsync(_gateway, _deviceB.DeviceKey, "dir-b", "MACHINE-B",
            dispatch: cmd => Record(cmd, _voiceModeToB));
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _dirA.DisposeAsync();
        await _dirB.DisposeAsync();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    private static DirectorCommandResult Record(DirectorCommand cmd, ConcurrentBag<string> sink)
    {
        if (cmd.Verb != "voice-mode")
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}");
        sink.Add(cmd.SessionId ?? "");
        return FakeTunnelDirector.Ok(new { ok = true });
    }

    [Fact]
    public async Task Sweep_switchesOnASessionThatAppearedLater_onTheHOSTEDGateway()
    {
        // Tenant A throws the switch while it owns one session. That fan-out is on a request, so it has a
        // tenant scope and has always worked - it is the arrangement here, not the thing under test.
        await _dirA.PushSnapshotAsync(Session("a-first"));
        var toggle = await Post("sessions/voice-mode/all", _deviceA.DeviceKey, new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, toggle.StatusCode);
        Assert.Contains("a-first", _voiceModeToA);

        // A new session joins A's fleet a moment later. Nobody has told it anything - this is the session the
        // owner watched sit outside voice mode while the banner said every session was in it.
        await _dirA.PushSnapshotAsync(Session("a-first"), Session("a-born-later"));

        // Exactly how the timer calls it: from a caller with NO ambient tenant scope. That is the whole point -
        // a sweep is not on a request and not on a tunnel connection, so it has no scope of its own.
        await _gateway.SweepVoiceModeAllAsync();

        // The command actually reached the Director...
        Assert.Contains("a-born-later", _voiceModeToA);
        // ...and the Gateway marked it, in TENANT A's voice state, so narration will really be spent on it.
        Assert.True(_gateway.VoiceService?.IsVoiceSession(_deviceA.Tenant, "a-born-later"));

        // A steady fleet produces no repeat traffic: the session that was already on is not re-sent.
        Assert.Single(_voiceModeToA, sid => sid == "a-first");
    }

    [Fact]
    public async Task Sweep_sendsEachTenantsCommandsUnderItsOwnScope_andLeavesATenantThatNeverAskedAlone()
    {
        // Both tenants own a session; only A asks for voice mode.
        await _dirA.PushSnapshotAsync(Session("a-one"));
        await _dirB.PushSnapshotAsync(Session("b-one"));
        (await Post("sessions/voice-mode/all", _deviceA.DeviceKey, new { enabled = true })).EnsureSuccessStatusCode();

        // Each tenant gains a session afterwards.
        await _dirA.PushSnapshotAsync(Session("a-one"), Session("a-two"));
        await _dirB.PushSnapshotAsync(Session("b-one"), Session("b-two"));

        await _gateway.SweepVoiceModeAllAsync();

        // A's later session is switched on, under A's own scope - a fix that entered ONE scope for the whole
        // loop would route A's command into whichever tenant happened to be first and this would go red.
        Assert.Contains("a-two", _voiceModeToA);
        Assert.True(_gateway.VoiceService?.IsVoiceSession(_deviceA.Tenant, "a-two"));

        // B never asked for voice mode, so B is untouched. The sweep must never put a fleet on voice that did
        // not ask for it - merely running the Gateway would otherwise start spending narration on everyone.
        Assert.Empty(_voiceModeToB);
        Assert.False(_gateway.VoiceService?.IsVoiceSession(_deviceB.Tenant, "b-two"));

        // And nothing crossed: A's marker lives in A's partition only.
        Assert.False(_gateway.VoiceService?.IsVoiceSession(_deviceB.Tenant, "a-two"));
    }

    private Task<HttpResponseMessage> Post(string path, string deviceKey, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        return _http.SendAsync(req);
    }

    private static SessionDto Session(string sid) => new()
    {
        SessionId = sid,
        Agent = "claude",
        RepoPath = "/repo",
        Status = "WaitingForInput",
        ActivityState = "WaitingForInput",
        StatusColor = "red",
        CreatedAt = DateTime.UtcNow,
        LastActivityAt = DateTime.UtcNow,
    };

}
