using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #2059: the Transcription Health surface (/transcription/stats, /turns, /terms, DELETE /history) used
/// to be denied on the hosted Gateway because the history store carried no tenant. The store is now
/// partitioned by tenant (<see cref="TranscriptionHistoryLog.DirectoryFor"/>), so the routes SERVE per-tenant.
///
/// This is the hostile A/B proof on a real HOSTED GatewayHost with TWO fully enrolled tenants and one unbound
/// device:
///   1. SERVE - the reads answer 200 for an enrolled tenant (not the old 404 refusal).
///   2. FAIL CLOSED - they answer 403 for a device whose key resolves to NO tenant, never the Local partition.
///   3. ISOLATED - a turn recorded in tenant A's history is counted in A's stats and INVISIBLE to B's.
///
/// Self-host is unchanged (the single Local tenant reads the existing flat store) and is exercised by
/// HostedContentReadSelfHostControlTests with hosted mode off.
/// </summary>
[Collection("DirectorRoot")]
public sealed class HostedTranscriptionServeTests : IAsyncLifetime
{
    private const string Token = "test-token-txn-serve";

    private readonly string _root;
    private readonly string? _priorRoot;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-txn-serve-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _httpA = null!;
    private HttpClient _httpB = null!;
    private HttpClient _httpUnbound = null!;
    private TenantId _tenantA;

    public HostedTranscriptionServeTests()
    {
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-txn-serve-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);

    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = true });
        await _gateway.StartAsync();

        _httpA = Enrolled("dev-a", "sub-alice", "alice@example.com", out _tenantA);
        _httpB = Enrolled("dev-b", "sub-bob", "bob@example.com", out _);

        var unboundKey = _gateway.Devices.Register("dev-unbound", "MA").DeviceKey;
        _httpUnbound = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _httpUnbound.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", unboundKey);

        // Record ONE turn into tenant A's history partition, through the same per-tenant log the write path
        // uses. Nothing is recorded for tenant B.
        TranscriptionHistoryLog.ForTenant(_tenantA).Record(new TranscriptionHistoryRecord
        {
            TimestampUtc = DateTime.UtcNow,
            TurnId = "alpha-turn",
            Outcome = "ok",
            TranscriptionMs = 120,
            CleanupMs = 10,
            Corrected = false,
        });
    }

    private HttpClient Enrolled(string deviceId, string subject, string email, out TenantId tenant)
    {
        var key = _gateway.Devices.Register(deviceId, "MA").DeviceKey;
        var minted = _gateway.TenantRegistry.MintOrLookupBySubject(subject, email);
        _gateway.Devices.SetAccountBinding(deviceId, subject, minted.Value);
        tenant = minted;
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return http;
    }

    public async Task DisposeAsync()
    {
        _httpA.Dispose();
        _httpB.Dispose();
        _httpUnbound.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    /// <summary>SERVE: the reads answer 200 for an enrolled tenant on hosted, not the old 404 refusal.</summary>
    [Theory]
    [InlineData("transcription/stats")]
    [InlineData("transcription/turns")]
    [InlineData("transcription/terms")]
    public async Task Transcription_reads_serve_an_enrolled_tenant_on_hosted(string path)
    {
        var resp = await _httpA.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>FAIL CLOSED: a device with no bound tenant is refused, never served the Local partition.
    /// MTR-14B (#2020): an unbound device on hosted is an invalid credential, so it is now denied at the auth
    /// gate with 401 before reaching the route's tenant-boundary 403 - the isolation is unchanged, only the
    /// denial layer moved earlier.</summary>
    [Theory]
    [InlineData("transcription/stats")]
    [InlineData("transcription/turns")]
    [InlineData("transcription/terms")]
    public async Task Transcription_reads_refuse_an_unresolved_tenant_with_401(string path)
    {
        var resp = await _httpUnbound.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    /// <summary>Control: an unauthenticated caller is still rejected by the host-wide auth gate.</summary>
    [Fact]
    public async Task An_unauthenticated_caller_is_still_rejected()
    {
        using var noAuth = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await noAuth.GetAsync("transcription/turns")).StatusCode);
    }

    /// <summary>ISOLATED: the turn recorded in tenant A's history is counted for A and INVISIBLE to B.</summary>
    [Fact]
    public async Task One_tenants_transcription_history_is_invisible_to_another_on_hosted()
    {
        // Tenant A sees its own turn.
        using (var aStats = await Json(_httpA, "transcription/stats"))
            Assert.Equal(1, aStats.RootElement.GetProperty("totalTurns").GetInt32());
        using (var aTurns = await Json(_httpA, "transcription/turns"))
        {
            var turns = aTurns.RootElement.GetProperty("turns").EnumerateArray().ToArray();
            Assert.Contains(turns, t => t.GetProperty("turnId").GetString() == "alpha-turn");
        }

        // Tenant B sees NONE of it.
        using (var bStats = await Json(_httpB, "transcription/stats"))
            Assert.Equal(0, bStats.RootElement.GetProperty("totalTurns").GetInt32());
        using (var bTurns = await Json(_httpB, "transcription/turns"))
            Assert.Empty(bTurns.RootElement.GetProperty("turns").EnumerateArray());
    }

    private static async Task<JsonDocument> Json(HttpClient http, string path)
    {
        var resp = await http.GetAsync(path);
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    }

}
