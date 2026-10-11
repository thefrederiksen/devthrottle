using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Hosted Multi-Tenancy (hosted voice-serving): the wingman VOICE read surface is tenant-aware end-to-end over
/// real HTTP. Two Directors connect on two DIFFERENT tenants, each authenticated with its OWN per-device key;
/// the Gateway resolves the request's tenant from that same authenticated key and serves ONLY that tenant's
/// voice state. Voice audio is the sensitive payload here - a wrong-tenant read would play one account's
/// narration to another - so every route is proven isolated with a same-tenant POSITIVE CONTROL next to each
/// cross-tenant negative:
///   - GET /wingman/voice/ready lists ONLY the requesting tenant's ready session ids (key B sees sess-b, key A
///     does not);
///   - GET /sessions/{sid}/wingman/voice for B's session is ready:false under key A, ready:true under key B;
///   - GET /sessions/{sid}/wingman/voice/audio for B's session is 404 under key A, 200 bytes under key B;
///   - a request whose authenticated device key has NO bound tenant is DENIED 403 (deny-by-default), never
///     served the Local partition.
///
/// Revert-prove: change any of these routes back to passing TenantId.Local into WingmanVoiceService (instead
/// of the request tenant) and key B reads the empty Local partition, so every same-tenant positive control
/// (key B sees / plays sess-b's voice) goes RED.
/// </summary>
public sealed class VoiceServingReadIsolationTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private const string SessB = "voice-read-sess-b";
    // Account tenants are minted GUIDs in production (WingmanVoiceService refuses a non-GUID, non-Local tenant
    // as a voice-state partition), so the device bindings here use real GUID tenant ids, not friendly labels.
    private TenantId TenantA { get; set; }
    private TenantId TenantB { get; set; }
    private static readonly byte[] AudioBytes = { 1, 2, 3, 4, 5 };

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private FakeTunnelDirector _dirA = null!;
    private FakeTunnelDirector _dirB = null!;

    private string _keyA = "";
    private string _keyB = "";
    private string _keyUnbound = "";

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-voice-read-" + Guid.NewGuid().ToString("N"));

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

        // Two accounts (each a device key bound to its OWN tenant) plus one registered-but-unbound key.
        var deviceA = HostedTestEnrollment.Enroll(
            _gateway, "sub-alice", "alice@example.com", "dev-a", "MA");
        var deviceB = HostedTestEnrollment.Enroll(
            _gateway, "sub-bob", "bob@example.com", "dev-b", "MB");
        TenantA = deviceA.Tenant;
        TenantB = deviceB.Tenant;
        _keyA = deviceA.DeviceKey;
        _keyB = deviceB.DeviceKey;
        _keyUnbound = _gateway.Devices.Register("dev-x", "MX").DeviceKey;

        _dirA = await FakeTunnelDirector.StartAsync(_gateway, _keyA, "dir-a", "MA");
        _dirB = await FakeTunnelDirector.StartAsync(_gateway, _keyB, "dir-b", "MB");

        // Seed a ready, playable voice clip for B's session UNDER TENANT B only. This is what a completed
        // narration leaves behind; seeding it directly avoids a live hosted brain / speech call the test
        // harness cannot make, while still proving the read path partitions by tenant.
        _gateway.VoiceService!.StoreReadyAudioForTest(
            TenantB, SessB, spoken: "The build finished cleanly.", reply: "Build succeeded.", audio: AudioBytes,
            contentType: "audio/mpeg");
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

    [Fact]
    public async Task Voice_ready_list_serves_only_the_requesting_tenants_sessions()
    {
        // POSITIVE CONTROL: B's own key lists B's ready voice session. Without this the negative below would
        // pass vacuously for a route that simply lists nothing for anyone.
        Assert.Contains(SessB, await ReadySids(_keyB));

        // ABSENCE: A's key never sees B's ready voice session (A resolves tenant-alice, an empty partition).
        Assert.DoesNotContain(SessB, await ReadySids(_keyA));
    }

    [Fact]
    public async Task Voice_by_id_ready_is_isolated_across_tenants()
    {
        // POSITIVE CONTROL: B's key reads its own session's voice as ready.
        Assert.True(await VoiceReady($"sessions/{SessB}/wingman/voice", _keyB));

        // ABSENCE: A's key sees it as not-ready (never the cross-tenant clip).
        Assert.False(await VoiceReady($"sessions/{SessB}/wingman/voice", _keyA));
    }

    [Fact]
    public async Task Voice_audio_is_isolated_across_tenants()
    {
        // POSITIVE CONTROL: B's key can fetch B's session audio - the exact bytes that were seeded.
        var ownResp = await Get($"sessions/{SessB}/wingman/voice/audio", _keyB);
        Assert.Equal(HttpStatusCode.OK, ownResp.StatusCode);
        Assert.Equal(AudioBytes, await ownResp.Content.ReadAsByteArrayAsync());

        // ABSENCE: A's key cannot fetch B's session audio - 404, never another tenant's narration.
        var crossResp = await Get($"sessions/{SessB}/wingman/voice/audio", _keyA);
        Assert.Equal(HttpStatusCode.NotFound, crossResp.StatusCode);
    }

    [Fact]
    public async Task A_device_key_with_no_bound_tenant_is_denied()
    {
        // Deny-by-default across the voice read surface: a tenant-unbound hosted credential is rejected at
        // authentication and never falls back to the Local partition.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("wingman/voice/ready", _keyUnbound)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get($"sessions/{SessB}/wingman/voice", _keyUnbound)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get($"sessions/{SessB}/wingman/voice/audio", _keyUnbound)).StatusCode);
    }

    private Task<HttpResponseMessage> Get(string path, string deviceKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        return _http.SendAsync(req);
    }

    private async Task<string[]> ReadySids(string deviceKey)
    {
        var resp = await Get("wingman/voice/ready", deviceKey);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("sids").EnumerateArray().Select(e => e.GetString()!).ToArray();
    }

    private async Task<bool> VoiceReady(string path, string deviceKey)
    {
        var resp = await Get(path, deviceKey);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("ready").GetBoolean();
    }

}
