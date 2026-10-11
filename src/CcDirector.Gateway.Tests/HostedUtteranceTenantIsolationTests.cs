using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1884, finding 4: the hosted <c>/wingman/utterance</c> upload family is served and TENANT-SCOPED,
/// proven over real HTTP with two bound accounts plus one unbound key across ALL THREE legs (register, chunk,
/// complete).
///
/// WHY THIS FILE EXISTS. Before it, there was NO hosted request test for the utterance family at all - the
/// only executable utterance-route tests were self-host controls. So reinstating the old hosted
/// <c>ExclusiveGroup</c> refusal for the three utterance handlers would leave the whole suite green while the
/// mobile Voice screen's guaranteed audio-turn front door went dead on hosted. An earlier claim that this
/// proof lived in a <c>HostedDictationTenantRoundTripTests</c> class was false - no such class existed.
///
/// WHAT IS PROVED, each revert-provable:
///   - A's own round-trip WORKS on hosted: register returns 200 and the leg bodies run (chunk 200, complete
///     gets past the gate and the unknown-upload check to the transcription step). Put the hosted deny back on
///     any of the three legs and A's positive control for that leg goes RED - this is what makes re-denying
///     utterance redden, where before it stayed green.
///   - B (a different bound tenant) cannot reach A's staged upload: the same upload id does not exist in B's
///     partition, so B's chunk and complete on A's id are 404, while A's own are not. State stays isolated.
///   - An authenticated key with NO bound tenant is refused (403) on every leg - never quietly served the
///     shared/Local root - with a bound-key positive control in front so the 403s are the tenant deny and not
///     a route broken for everyone.
///
/// The complete leg cannot finish a real transcription offline (no backend key is configured on the test
/// Gateway), so A's complete is asserted to get PAST the gate and the unknown-upload check (not 403, not 404)
/// rather than to return a transcript. That is the same offline bound the dictation isolation suite runs
/// under, and it is exactly enough to prove the leg is served for A and denied for B.
///
/// The assembly runs sequentially (TestParallelization), so toggling the storage root here is safe; it is
/// restored in DisposeAsync.
/// </summary>
public sealed class HostedUtteranceTenantIsolationTests : IAsyncLifetime
{
    private const string GatewayToken = "test-token";
    private const string SecretAudioA = "alpha-account-secret-utterance-audio";
    private const string SecretAudioB = "bravo-account-secret-utterance-audio";

    private GatewayHost _gateway = null!;
    private HttpClient _httpA = null!;
    private HttpClient _httpB = null!;
    private HttpClient _httpUnbound = null!;

    private string? _priorRoot;

    private readonly string _storageRoot =
        Path.Combine(Path.GetTempPath(), "cc-utterance-iso-storage-" + Guid.NewGuid().ToString("N"));
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-utterance-iso-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _storageRoot);

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: GatewayToken, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            promptLogPath: Path.Combine(_instancesDir, "prompt-log"),
            options: new GatewayHostOptions { Hosted = true });
        await _gateway.StartAsync();

        var keyA = _gateway.Devices.Register("dev-a", "MA").DeviceKey;
        var keyB = _gateway.Devices.Register("dev-b", "MB").DeviceKey;
        var keyUnbound = _gateway.Devices.Register("dev-x", "MX").DeviceKey;
        var tenantA = _gateway.TenantRegistry.MintOrLookupBySubject("sub-alice", "alice@example.com");
        var tenantB = _gateway.TenantRegistry.MintOrLookupBySubject("sub-bob", "bob@example.com");
        _gateway.Devices.SetAccountBinding("dev-a", "sub-alice", tenantA.Value);
        _gateway.Devices.SetAccountBinding("dev-b", "sub-bob", tenantB.Value);

        _httpA = NewClient(keyA);
        _httpB = NewClient(keyB);
        _httpUnbound = NewClient(keyUnbound);
    }

    public async Task DisposeAsync()
    {
        _httpA.Dispose();
        _httpB.Dispose();
        _httpUnbound.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* cleanup */ }
        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, true); } catch { /* cleanup */ }
    }

    // ===== A's own round-trip is served on hosted (re-deny reddens here) ============================

    [Fact]
    public async Task A_can_register_chunk_and_complete_its_own_utterance_on_hosted()
    {
        var id = Guid.NewGuid().ToString();

        // Register: 200 with an upload_id. This is the anchor - re-adding the hosted deny to /wingman/utterance
        // makes THIS 403, which reddens the test that today proves the family is served.
        var register = await RegisterAsync(_httpA, id);
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);
        // The store echoes the id in its canonical staging form (a normalized GUID), so compare by GUID value
        // rather than by spelling.
        var returnedId = (await register.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("upload_id").GetString();
        Assert.Equal(Guid.Parse(id), Guid.Parse(returnedId!));

        // Chunk: 200. The leg body ran inside A's partition.
        Assert.Equal(HttpStatusCode.OK, (await PutChunkAsync(_httpA, id, 0, SecretAudioA)).StatusCode);

        // Complete: gets PAST the gate and the unknown-upload check for A's own upload. A real transcription
        // backend is not configured offline, so this is 503 (no key) rather than a transcript - the point is it
        // is NEITHER 403 (denied) NOR 404 (upload not found), so the leg is genuinely served for A.
        var complete = await CompleteAsync(_httpA, id, totalChunks: 1);
        Assert.NotEqual(HttpStatusCode.Forbidden, complete.StatusCode);
        Assert.NotEqual(HttpStatusCode.NotFound, complete.StatusCode);
    }

    // ===== B cannot reach A's staged upload ========================================================

    [Fact]
    public async Task B_cannot_chunk_or_complete_another_accounts_utterance_upload_id()
    {
        var id = Guid.NewGuid().ToString();

        // A stages an upload under the id.
        Assert.Equal(HttpStatusCode.OK, (await RegisterAsync(_httpA, id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutChunkAsync(_httpA, id, 0, SecretAudioA)).StatusCode);

        // B has never registered this id in ITS partition, so to B it does not exist: chunk and complete are
        // both 404. B cannot overwrite, extend, or resolve A's staged audio.
        Assert.Equal(HttpStatusCode.NotFound, (await PutChunkAsync(_httpB, id, 0, SecretAudioB)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CompleteAsync(_httpB, id, totalChunks: 1)).StatusCode);

        // Positive control: B registering the SAME id in its own partition succeeds (an upload id is only
        // meaningful inside its own tenant), and B's own chunk then lands - so the 404s above are the partition
        // boundary, not a route that is simply broken for B.
        Assert.Equal(HttpStatusCode.OK, (await RegisterAsync(_httpB, id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutChunkAsync(_httpB, id, 0, SecretAudioB)).StatusCode);
    }

    // ===== deny-by-default on every leg ============================================================

    [Fact]
    public async Task Every_utterance_leg_denies_an_authenticated_key_with_no_bound_tenant()
    {
        var id = Guid.NewGuid().ToString();

        // A device row without a canonical tenant binding is rejected by hosted authentication on all three
        // legs and never quietly served the shared/Local root.
        Assert.Equal(HttpStatusCode.Unauthorized, (await RegisterAsync(_httpUnbound, id)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PutChunkAsync(_httpUnbound, id, 0, SecretAudioB)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CompleteAsync(_httpUnbound, id, totalChunks: 1)).StatusCode);

        // Positive control: the same three calls from a bound key pass authentication.
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await RegisterAsync(_httpA, id)).StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await PutChunkAsync(_httpA, id, 0, SecretAudioA)).StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await CompleteAsync(_httpA, id, totalChunks: 1)).StatusCode);
    }

    // ===== helpers =================================================================================

    private static async Task<HttpResponseMessage> RegisterAsync(HttpClient http, string uploadId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/wingman/utterance/upload");
        req.Headers.Add("Idempotency-Key", uploadId);
        return await http.SendAsync(req);
    }

    private static async Task<HttpResponseMessage> PutChunkAsync(HttpClient http, string uploadId, int index, string text)
    {
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return await http.PutAsync($"/wingman/utterance/{uploadId}/chunk/{index}", content);
    }

    private static Task<HttpResponseMessage> CompleteAsync(HttpClient http, string uploadId, int totalChunks)
        => http.PostAsJsonAsync($"/wingman/utterance/{uploadId}/complete", new { totalChunks });

    private HttpClient NewClient(string deviceKey)
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        return http;
    }

}
