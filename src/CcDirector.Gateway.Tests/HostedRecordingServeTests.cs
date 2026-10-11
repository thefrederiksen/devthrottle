using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issues #2058 (Voice Recorder) and #2060 (Dictionary): the whole <c>/ingest</c> group used to be DENIED on
/// the hosted Gateway because its store carried no tenant - the recording directory was keyed on a
/// caller-supplied id alone and the glossary was one global file. Both are now PARTITIONED BY TENANT, so the
/// routes SERVE per-tenant on hosted: each resolves the caller's tenant and dispatches to that tenant's own
/// recording store / glossary.
///
/// This is the hostile A/B proof, on a real HOSTED GatewayHost with TWO fully enrolled tenants and one unbound
/// device:
///   1. SERVE - the routes answer 200 for an enrolled tenant (not the old 404 refusal).
///   2. FAIL CLOSED - they answer 403 for a device whose key resolves to NO tenant, never the Local partition.
///   3. ISOLATED - a recording registered by tenant A is invisible to tenant B's list, and a glossary term
///      added by tenant A never appears in tenant B's glossary.
///
/// Self-host is unchanged (the single Local tenant maps to the existing flat store) and is exercised by
/// RecordingEndpointsE2ETests / RecordingCompletenessGateHttpTests with hosted mode off.
/// </summary>
[Collection("DirectorRoot")]
public sealed class HostedRecordingServeTests : IAsyncLifetime
{
    private const string Token = "test-token-rec-serve";

    private readonly string _root;
    private readonly string? _priorRoot;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-rec-serve-" + Guid.NewGuid().ToString("N"));
    private readonly string _vaultPath =
        Path.Combine(Path.GetTempPath(), "cc-rec-serve-" + Guid.NewGuid().ToString("N") + ".json");

    private GatewayHost _gateway = null!;
    private HttpClient _httpA = null!;
    private HttpClient _httpB = null!;
    private HttpClient _httpUnbound = null!;

    public HostedRecordingServeTests()
    {
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-rec-serve-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);

    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            keyVaultPath: _vaultPath,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = true });
        await _gateway.StartAsync();

        _httpA = Enrolled("dev-a", "sub-alice", "alice@example.com");
        _httpB = Enrolled("dev-b", "sub-bob", "bob@example.com");

        var unboundKey = _gateway.Devices.Register("dev-unbound", "MA").DeviceKey;
        _httpUnbound = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _httpUnbound.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", unboundKey);
    }

    private HttpClient Enrolled(string deviceId, string subject, string email)
    {
        var key = _gateway.Devices.Register(deviceId, "MA").DeviceKey;
        var tenant = _gateway.TenantRegistry.MintOrLookupBySubject(subject, email);
        _gateway.Devices.SetAccountBinding(deviceId, subject, tenant.Value);
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
        try { if (File.Exists(_vaultPath)) File.Delete(_vaultPath); } catch { /* best effort */ }
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private static string RegisterBody(string id) =>
        "{\"recordingId\":\"" + id + "\",\"title\":\"t\",\"deviceId\":\"d\"," +
        "\"startedAt\":\"2026-01-01T00:00:00Z\",\"codec\":\"mp3\",\"sampleRateHz\":16000,\"channels\":1}";

    /// <summary>SERVE: the recordings list answers 200 for an enrolled tenant on hosted, not the old 404.</summary>
    [Fact]
    public async Task Recordings_list_serves_an_enrolled_tenant_on_hosted()
    {
        var resp = await _httpA.GetAsync("ingest/recordings");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>SERVE: the glossary answers 200 for an enrolled tenant on hosted.</summary>
    [Fact]
    public async Task Glossary_serves_an_enrolled_tenant_on_hosted()
    {
        var resp = await _httpA.GetAsync("ingest/dictionary");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>FAIL CLOSED: a device with no bound tenant is refused, never served the Local partition.
    /// MTR-14B (#2020): an unbound device on hosted is an invalid credential, so it is now denied at the auth
    /// gate with 401 before reaching the route's tenant-boundary 403 - the isolation is unchanged, only the
    /// denial layer moved earlier.</summary>
    [Theory]
    [InlineData("ingest/recordings")]
    [InlineData("ingest/dictionary")]
    public async Task Ingest_reads_refuse_an_unresolved_tenant_with_401(string path)
    {
        var resp = await _httpUnbound.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    /// <summary>Control: an unauthenticated caller is still rejected by the host-wide auth gate.</summary>
    [Fact]
    public async Task An_unauthenticated_caller_is_still_rejected()
    {
        using var noAuth = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await noAuth.GetAsync("ingest/recordings")).StatusCode);
    }

    /// <summary>ISOLATED: a recording registered by tenant A is listed for A and INVISIBLE to tenant B.</summary>
    [Fact]
    public async Task A_recording_registered_by_one_tenant_is_invisible_to_another_on_hosted()
    {
        const string idA = "alpha-only-recording";
        var reg = await _httpA.PostAsync("ingest/recording",
            new StringContent(RegisterBody(idA), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, reg.StatusCode);

        Assert.Contains(idA, await ListIds(_httpA));
        Assert.DoesNotContain(idA, await ListIds(_httpB));

        // And B cannot read A's recording status by guessing its id (different partition).
        var bStatus = await _httpB.GetAsync($"ingest/recording/{idA}/status");
        Assert.Equal(HttpStatusCode.NotFound, bStatus.StatusCode);
    }

    /// <summary>ISOLATED: a glossary term added by tenant A never appears in tenant B's glossary.</summary>
    [Fact]
    public async Task A_glossary_term_added_by_one_tenant_is_invisible_to_another_on_hosted()
    {
        const string termA = "alphaonlyterm";
        var add = await _httpA.PostAsync("ingest/dictionary/terms",
            new StringContent("{\"terms\":[\"" + termA + "\"]}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);

        Assert.Contains(termA, await GlossaryVocab(_httpA));
        Assert.DoesNotContain(termA, await GlossaryVocab(_httpB));
    }

    private static async Task<string[]> ListIds(HttpClient http)
    {
        var resp = await http.GetAsync("ingest/recordings");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.EnumerateArray()
            .Select(e => e.TryGetProperty("recordingId", out var v) ? v.GetString() ?? "" : "")
            .ToArray();
    }

    private static async Task<string[]> GlossaryVocab(HttpClient http)
    {
        var resp = await http.GetAsync("ingest/dictionary");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("vocabulary", out var vocab) && vocab.ValueKind == JsonValueKind.Array
            ? vocab.EnumerateArray().Select(e => e.GetString() ?? "").ToArray()
            : Array.Empty<string>();
    }

}

/// <summary>
/// The future route's body record, bound by the FRAMEWORK ([FromBody] on a minimal-API handler). Kept here as
/// a shared probe type that other deny families' future-route proofs reference (for example the exes group's).
/// A custom <c>IBindableFromHttpContext</c>/<c>BindAsync</c> parameter would ignore the request body entirely
/// and bind unconditionally, so a malformed body could never reach the framework 400 - which is why a
/// future-route canary that wants to prove framework binding uses a plain record like this one.
/// </summary>
internal sealed record RecordingProbeBody(string Text);

/// <summary>
/// An OBSERVABLE-BINDING SEAM: a custom-bound parameter whose execution is counted, so a proof can assert that
/// NO handler-bound code ran behind a refusal. Kept as a shared probe instrument.
/// </summary>
internal sealed class RecordingProbeBinding
{
    public const string Sentinel = "probe-payload-that-must-never-be-served-on-hosted";

    private static int _count;

    public string Value { get; init; } = "";

    public static int Count => Volatile.Read(ref _count);

    public static void Reset() => Interlocked.Exchange(ref _count, 0);

    public static ValueTask<RecordingProbeBinding?> BindAsync(HttpContext context)
    {
        Interlocked.Increment(ref _count);
        return ValueTask.FromResult<RecordingProbeBinding?>(new RecordingProbeBinding { Value = Sentinel });
    }
}
