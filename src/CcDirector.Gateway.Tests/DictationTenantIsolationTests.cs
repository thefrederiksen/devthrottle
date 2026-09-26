using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Linq;
using System.Threading.Tasks;
using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Voice;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1884: the dictation front door is TENANT-KEYED on the hosted Gateway, proven over real HTTP with
/// two accounts posting THE SAME UPLOAD ID.
///
/// This is an AUDIO and TRANSCRIPT disclosure surface. Before this, <c>VoiceUploadStore</c> had one global
/// root and keyed every directory, chunk and record solely by the caller-supplied upload id; the delivery
/// record carried no tenant; the upload / chunk / ack / abandon legs authenticated a DEVICE and never asked
/// whose upload it was; and the static single-flight completion cache was keyed by upload id alone. So after
/// account A completed upload id X, account B posting <c>Idempotency-Key: X</c> read A's terminal record and
/// was handed A's TRANSCRIPT - and before A reached a terminal state, B could overwrite A's chunks, ack or
/// abandon A's record, or race its completion. A GUID is not a tenant boundary: secrecy of an identifier is
/// not authorization, and upload ids travel in client logs, retries and store-and-forward queues.
///
/// Every canary here is BIDIRECTIONAL and every absence claim has a POSITIVE CONTROL in front of it - the
/// same account's own operation on the same id succeeding - so no assertion can pass merely because nothing
/// happened at all.
///
/// Dictation now WORKS on hosted (issue #1884, un-deny): the family is no longer refused, the five legs are
/// served through <c>DictationTenantGate</c>, and the completion's session locate is scoped to the request's
/// tenant so a hosted session is found in its own account's partition. What these tests prove is that the
/// STATE stays isolated across accounts while that happens - the same upload id in two tenants is two
/// separate uploads, and no leg of one account can read, overwrite, resolve, or retire another's.
///
/// THE PRODUCTION LINES THESE TESTS PROTECT (each revert-provable - revert it, watch these go red):
///   - The <c>gate.TryOpen</c> at the head of each of the five legs in <c>GatewayDictationEndpoint</c>, which
///     hands back a store bound to the caller's tenant. Put any leg back on a shared/unscoped store and that
///     leg's canary goes RED.
///   - <c>GatewayDictationEndpoint.CacheKey</c> at the <c>_completes.GetOrAdd</c> call site. Key that call on
///     the upload id alone and the single-flight canary goes RED.
///   - <c>VoiceUploadStore.PartitionRootFor</c>, which is what makes all of the above structural rather than
///     conventional.
///
/// The assembly runs sequentially (TestParallelization), so toggling CC_GATEWAY_HOSTED and the storage root
/// here is safe; both are restored in DisposeAsync.
/// </summary>
public sealed class DictationTenantIsolationTests : IAsyncLifetime
{
    private const string GatewayToken = "test-token";

    // The things one account must never be able to read, overwrite, or resolve out of the other.
    private const string SecretTranscriptA = "alpha-account-secret-transcript";
    private const string SecretTranscriptB = "bravo-account-secret-transcript";
    private const string SecretAudioA = "alpha-account-secret-audio-bytes";
    private const string SecretAudioB = "bravo-account-secret-audio-bytes";

    private GatewayHost _gateway = null!;
    private HttpClient _httpA = null!;
    private HttpClient _httpB = null!;
    private HttpClient _httpUnbound = null!;

    private TenantId _tenantA;
    private TenantId _tenantB;

    private string? _priorHosted;
    private string? _priorRoot;

    private readonly string _storageRoot =
        Path.Combine(Path.GetTempPath(), "cc-dictation-iso-storage-" + Guid.NewGuid().ToString("N"));
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-dictation-iso-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        // Isolate the storage root BEFORE the Gateway starts so its dictation upload store binds the temp
        // root, never the developer's real one.
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _storageRoot);

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: GatewayToken, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            promptLogPath: Path.Combine(_instancesDir, "prompt-log"));
        await _gateway.StartAsync();

        // Two accounts: two device keys, each bound to its OWN tenant, plus one registered-but-unbound key.
        // The tenants are MINTED BY THE REAL REGISTRY, exactly as the hosted enrollment route does, because
        // the upload store turns a tenant id into a DIRECTORY NAME and enforces the real minted shape - a
        // test binding a made-up id would be testing a partition production can never create.
        var keyA = _gateway.Devices.Register("dev-a", "MA").DeviceKey;
        var keyB = _gateway.Devices.Register("dev-b", "MB").DeviceKey;
        var keyUnbound = _gateway.Devices.Register("dev-x", "MX").DeviceKey;
        _tenantA = _gateway.TenantRegistry.MintOrLookupBySubject("sub-alice", "alice@example.com");
        _tenantB = _gateway.TenantRegistry.MintOrLookupBySubject("sub-bob", "bob@example.com");
        _gateway.Devices.SetAccountBinding("dev-a", "sub-alice", _tenantA.Value);
        _gateway.Devices.SetAccountBinding("dev-b", "sub-bob", _tenantB.Value);

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
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* cleanup */ }
        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, true); } catch { /* cleanup */ }
    }

    // ===== leg 1: chunks ===========================================================================

    [Fact]
    public async Task Chunks_cannot_cross_between_accounts_on_the_same_upload_id()
    {
        var id = Guid.NewGuid().ToString();

        // Positive control: A registers the id and stages a chunk under it, successfully.
        Assert.Equal(HttpStatusCode.OK, (await RegisterAsync(_httpA, id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutChunkAsync(_httpA, id, 0, SecretAudioA)).StatusCode);
        Assert.Equal(SecretAudioA, await StagedTextAsync(_tenantA, id));

        // B cannot write into it. The upload id does not exist in B's partition, so B cannot overwrite A's
        // audio, extend it, or even confirm that it exists.
        Assert.Equal(HttpStatusCode.NotFound, (await PutChunkAsync(_httpB, id, 0, SecretAudioB)).StatusCode);
        Assert.Equal(SecretAudioA, await StagedTextAsync(_tenantA, id));

        // The other direction, on the same id - which is now legally B's id too, and that is the point: an
        // upload id is only meaningful inside its own tenant.
        Assert.Equal(HttpStatusCode.OK, (await RegisterAsync(_httpB, id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutChunkAsync(_httpB, id, 0, SecretAudioB)).StatusCode);
        Assert.Equal(SecretAudioB, await StagedTextAsync(_tenantB, id));
        Assert.Equal(SecretAudioA, await StagedTextAsync(_tenantA, id));
    }

    // ===== leg 2: the terminal transcript, at register =============================================

    [Fact]
    public async Task A_terminal_transcript_is_never_handed_to_another_account_at_register()
    {
        // THE CONCRETE DISCLOSURE from the issue, driven over HTTP: after A completes upload id X, B posts
        // /dictation/upload with Idempotency-Key: X and the terminal-register result hands B A's transcript.
        var id = Guid.NewGuid().ToString();
        Store(_tenantA).MarkDelivered(id, submitted: true, movedOn: false, transcript: SecretTranscriptA);

        // Positive control: A re-registering its OWN delivered id really does get the cached terminal outcome
        // back, so "B does not get it" cannot pass because the short-circuit never fires.
        var (statusA, bodyA) = await ReadAsync(await RegisterAsync(_httpA, id));
        Assert.Equal(HttpStatusCode.OK, statusA);
        Assert.Contains(SecretTranscriptA, bodyA);

        // The absence claim: B's register of the same id is an ordinary fresh register - no terminal flag, no
        // transcript, nothing of A's in any form.
        var (statusB, bodyB) = await ReadAsync(await RegisterAsync(_httpB, id));
        Assert.Equal(HttpStatusCode.OK, statusB);
        Assert.DoesNotContain(SecretTranscriptA, bodyB);
        Assert.False(JsonDocument.Parse(bodyB).RootElement.TryGetProperty("terminal", out var terminal)
                     && terminal.GetBoolean());
        // A's record is untouched by B's register - still delivered, still A's words.
        var afterB = MustStillExist(_tenantA, id, "A's delivered record must survive B registering the same upload id");
        Assert.Equal(DictationDeliveryState.Delivered, afterB.State);
        Assert.Equal(SecretTranscriptA, afterB.Transcript);

        // The mirror direction on a second id owned by B.
        var idB = Guid.NewGuid().ToString();
        Store(_tenantB).MarkDelivered(idB, submitted: true, movedOn: false, transcript: SecretTranscriptB);
        Assert.Contains(SecretTranscriptB, (await ReadAsync(await RegisterAsync(_httpB, idB))).body);
        Assert.DoesNotContain(SecretTranscriptB, (await ReadAsync(await RegisterAsync(_httpA, idB))).body);
    }

    [Fact]
    public async Task A_terminal_transcript_is_never_handed_to_another_account_at_complete()
    {
        // The same disclosure through the OTHER door: the cached-terminal-outcome short-circuit in complete.
        var id = Guid.NewGuid().ToString();
        Store(_tenantA).MarkDelivered(id, submitted: true, movedOn: false, transcript: SecretTranscriptA);

        // Positive control: A's own re-complete returns its cached submitted outcome.
        var (statusA, bodyA) = await ReadAsync(await CompleteAsync(_httpA, id));
        Assert.Equal(HttpStatusCode.OK, statusA);
        Assert.Contains(SecretTranscriptA, bodyA);

        // B completing the same id gets nothing of A's: no 200, and no transcript in any form.
        var (statusB, bodyB) = await ReadAsync(await CompleteAsync(_httpB, id));
        Assert.NotEqual(HttpStatusCode.OK, statusB);
        Assert.DoesNotContain(SecretTranscriptA, bodyB);

        // Mirror.
        var idB = Guid.NewGuid().ToString();
        Store(_tenantB).MarkDelivered(idB, submitted: true, movedOn: false, transcript: SecretTranscriptB);
        Assert.Contains(SecretTranscriptB, (await ReadAsync(await CompleteAsync(_httpB, idB))).body);
        var crossing = await ReadAsync(await CompleteAsync(_httpA, idB));
        Assert.NotEqual(HttpStatusCode.OK, crossing.status);
        Assert.DoesNotContain(SecretTranscriptB, crossing.body);
    }

    // ===== leg 3: the in-flight completion cache ===================================================

    [Fact]
    public async Task The_static_single_flight_completion_cache_is_keyed_by_tenant()
    {
        // The cache is STATIC and used to be keyed by upload id alone, so whichever account's complete
        // arrived first supplied the cached run to every other account posting that id - a purely in-memory
        // cross-serve that no on-disk partition can guard.
        //
        // Bound to the REAL GetOrAdd call site through its seam, not to the key helper: a unit test of the
        // helper alone would stay green if that call went back to keying on the upload id.
        var id = Guid.NewGuid().ToString();
        var observed = new List<string>();
        GatewayDictationEndpoint.OnCompleteEntryCreatedForTests = key => { lock (observed) observed.Add(key); };
        try
        {
            await RegisterAsync(_httpA, id);
            await PutChunkAsync(_httpA, id, 0, SecretAudioA);
            await CompleteAsync(_httpA, id);

            await RegisterAsync(_httpB, id);
            await PutChunkAsync(_httpB, id, 0, SecretAudioB);
            await CompleteAsync(_httpB, id);
        }
        finally
        {
            GatewayDictationEndpoint.OnCompleteEntryCreatedForTests = null;
        }

        // Positive control: both completes really did create an entry, so "the two entries are different"
        // cannot pass because the cache was never reached.
        Assert.Contains(observed, k => k.Contains(_tenantA.Value, StringComparison.Ordinal));
        Assert.Contains(observed, k => k.Contains(_tenantB.Value, StringComparison.Ordinal));

        // Both directions: neither account's key can be read as the other's, so neither can join the other's
        // in-flight run for the same upload id.
        var keyA = GatewayDictationEndpoint.CacheKey(_tenantA, id);
        var keyB = GatewayDictationEndpoint.CacheKey(_tenantB, id);
        Assert.NotEqual(keyA, keyB);
        Assert.DoesNotContain(_tenantB.Value, keyA, StringComparison.Ordinal);
        Assert.DoesNotContain(_tenantA.Value, keyB, StringComparison.Ordinal);
        Assert.Contains(keyA, observed);
        Assert.Contains(keyB, observed);
    }

    // ===== leg 4: ack ==============================================================================

    [Fact]
    public async Task An_ack_never_retires_another_accounts_record()
    {
        // Positive control on a separate id first: an ack really does retire this account's own tombstone in
        // this setup, so the "retired=false" claim below is a refusal and not a broken route.
        var control = Guid.NewGuid().ToString();
        Store(_tenantA).MarkDelivered(control, submitted: true, movedOn: false, transcript: SecretTranscriptA);
        Assert.True(await AckRetiredAsync(_httpA, control));

        var id = Guid.NewGuid().ToString();
        Store(_tenantA).MarkDelivered(id, submitted: true, movedOn: false, transcript: SecretTranscriptA);

        // B acking A's id retires nothing, and A's tombstone - the durable de-dupe that stops A's turn being
        // injected a second time - is still standing afterwards.
        Assert.False(await AckRetiredAsync(_httpB, id));
        Assert.NotNull(Store(_tenantA).ReadRecord(id));
        Assert.Equal(SecretTranscriptA,
            MustStillExist(_tenantA, id, "B's ack must not have retired A's tombstone").Transcript);

        // And A can still retire its own, on the very id B just tried to.
        Assert.True(await AckRetiredAsync(_httpA, id));
        Assert.Null(Store(_tenantA).ReadRecord(id));

        // Mirror.
        var idB = Guid.NewGuid().ToString();
        Store(_tenantB).MarkDelivered(idB, submitted: true, movedOn: false, transcript: SecretTranscriptB);
        Assert.False(await AckRetiredAsync(_httpA, idB));
        Assert.NotNull(Store(_tenantB).ReadRecord(idB));
        Assert.True(await AckRetiredAsync(_httpB, idB));
    }

    // ===== leg 5: abandon ==========================================================================

    [Fact]
    public async Task An_abandon_never_resolves_or_discards_another_accounts_dictation()
    {
        var sessionA = Guid.NewGuid().ToString();

        // Positive control on a separate id first: abandon really does work for this account.
        var control = Guid.NewGuid().ToString();
        await RegisterAsync(_httpA, control, sessionA);
        Assert.True(await AbandonedAsync(_httpA, control));
        Assert.Equal(DictationDeliveryState.Abandoned,
            MustStillExist(_tenantA, control, "A's own abandon must have written A's tombstone").State);

        // A has a live pending dictation, with staged audio, holding its session lock.
        var id = Guid.NewGuid().ToString();
        await RegisterAsync(_httpA, id, sessionA);
        await PutChunkAsync(_httpA, id, 0, SecretAudioA);
        Assert.True(Store(_tenantA).IsSessionLocked(sessionA));

        // B abandoning that id resolves nothing of A's: A's record is still PENDING, its audio is still
        // staged, and its session is still locked. (B's own partition gets B's own tombstone, which is B's
        // business and nobody else's.)
        await AbandonedAsync(_httpB, id);
        Assert.Equal(DictationDeliveryState.Pending,
            MustStillExist(_tenantA, id, "B's abandon must not have resolved A's pending record").State);
        Assert.Equal(SecretAudioA, await StagedTextAsync(_tenantA, id));
        Assert.True(Store(_tenantA).IsSessionLocked(sessionA));

        // Mirror: A cannot abandon B's live dictation either.
        var sessionB = Guid.NewGuid().ToString();
        var idB = Guid.NewGuid().ToString();
        await RegisterAsync(_httpB, idB, sessionB);
        await PutChunkAsync(_httpB, idB, 0, SecretAudioB);
        await AbandonedAsync(_httpA, idB);
        Assert.Equal(DictationDeliveryState.Pending,
            MustStillExist(_tenantB, idB, "A's abandon must not have resolved B's pending record").State);
        Assert.Equal(SecretAudioB, await StagedTextAsync(_tenantB, idB));
        Assert.True(Store(_tenantB).IsSessionLocked(sessionB));
    }

    // ===== deny by default =========================================================================

    [Fact]
    public async Task Every_leg_denies_an_authenticated_key_with_no_bound_tenant()
    {
        // Deny-by-default: on hosted, a device row with no canonical tenant binding is rejected by
        // authentication and never quietly served the LOCAL partition where self-host uploads live.
        var id = Guid.NewGuid().ToString();
        Store(_tenantA).MarkDelivered(id, submitted: true, movedOn: false, transcript: SecretTranscriptA);

        Assert.Equal(HttpStatusCode.Unauthorized, (await RegisterAsync(_httpUnbound, id)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PutChunkAsync(_httpUnbound, id, 0, SecretAudioB)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CompleteAsync(_httpUnbound, id)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _httpUnbound.PostAsync($"/dictation/{id}/ack", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _httpUnbound.PostAsync($"/dictation/{id}/abandon", content: null)).StatusCode);

        // Positive control: the same five calls from a bound key pass authentication.
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await RegisterAsync(_httpA, id)).StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await PutChunkAsync(_httpA, id, 0, SecretAudioA)).StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await CompleteAsync(_httpA, id)).StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized,
            (await _httpA.PostAsync($"/dictation/{id}/abandon", content: null)).StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized,
            (await _httpA.PostAsync($"/dictation/{id}/ack", content: null)).StatusCode);
    }

    // ===== leg 6: the decision log read (Voice Delivery mission) ===================================

    [Fact]
    public async Task The_decision_log_reads_for_its_own_account_and_is_not_found_for_another()
    {
        // Proves GET /dictation/{id}/decisions returns the owner's own decisions and answers another account
        // "not found" for the same upload id - it is dictation data, scoped exactly like the other five legs.
        var id = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.OK, (await RegisterAsync(_httpA, id)).StatusCode);
        Store(_tenantA).MarkDelivered(id, submitted: true, movedOn: false, transcript: SecretTranscriptA);

        // Positive control: A reads its own lines, in order, with no words in them.
        var own = await _httpA.GetAsync($"/dictation/{id}/decisions");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        var ownBody = await own.Content.ReadAsStringAsync();
        var decisions = JsonDocument.Parse(ownBody).RootElement.GetProperty("decisions");
        Assert.Equal(new[] { DeliveryDecisions.Received, DeliveryDecisions.Delivered },
            decisions.EnumerateArray().Select(d => d.GetProperty("decision").GetString()).ToArray());
        Assert.DoesNotContain(SecretTranscriptA, ownBody);

        // B asks for the same id: not found, and nothing of A's in the answer.
        var other = await _httpB.GetAsync($"/dictation/{id}/decisions");
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        var otherBody = await other.Content.ReadAsStringAsync();
        Assert.DoesNotContain(DeliveryDecisions.Received, otherBody);

        // A key with no account is refused before anything is read.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _httpUnbound.GetAsync($"/dictation/{id}/decisions")).StatusCode);
    }

    // ===== helpers =================================================================================

    // A store bound to ONE tenant's partition of the SAME on-disk dictation root the running Gateway reads
    // (both resolve CcStorage.DictationUploads() under the isolated CC_DIRECTOR_ROOT), so a record written
    // here is the one that tenant's handlers see - and one no other tenant's handlers can.

    /// <summary>
    /// Read a record the claim under test says MUST still be there, asserting its presence before touching
    /// it. Dereferencing with <c>!</c> turns an absent record into a NullReferenceException, and a crash is
    /// not evidence about the line the test was aimed at - it only says something upstream broke before the
    /// test could ask its question. Every red must be able to name what it caught.
    /// </summary>
    private static DictationDeliveryRecord MustStillExist(TenantId tenant, string uploadId, string claim)
    {
        var record = Store(tenant).ReadRecord(uploadId);
        Assert.True(record is not null, claim + " (the record was absent)");
        return record!;
    }

    private static VoiceUploadStore Store(TenantId tenant)
        => new VoiceUploadStore(CcStorage.DictationUploads(), TenantId.Local).ForTenant(tenant);

    private static async Task<string> StagedTextAsync(TenantId tenant, string uploadId)
    {
        var assembled = await Store(tenant).AssembleAsync(uploadId, 1);
        return assembled.Audio is null ? "" : Encoding.UTF8.GetString(assembled.Audio);
    }

    private static async Task<HttpResponseMessage> RegisterAsync(HttpClient http, string uploadId, string? sessionId = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/dictation/upload")
        {
            Content = JsonContent.Create(new { sessionId = sessionId ?? Guid.NewGuid().ToString() }),
        };
        req.Headers.Add("Idempotency-Key", uploadId);
        return await http.SendAsync(req);
    }

    private static async Task<HttpResponseMessage> PutChunkAsync(HttpClient http, string uploadId, int index, string text)
    {
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return await http.PutAsync($"/dictation/{uploadId}/chunk/{index}", content);
    }

    private static Task<HttpResponseMessage> CompleteAsync(HttpClient http, string uploadId)
        => http.PostAsJsonAsync($"/dictation/{uploadId}/complete",
            new { sessionId = Guid.NewGuid().ToString(), totalChunks = 1, sentAtUtc = DateTime.UtcNow });

    private static async Task<bool> AckRetiredAsync(HttpClient http, string uploadId)
    {
        var resp = await http.PostAsync($"/dictation/{uploadId}/ack", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("retired").GetBoolean();
    }

    private static async Task<bool> AbandonedAsync(HttpClient http, string uploadId)
    {
        var resp = await http.PostAsync($"/dictation/{uploadId}/abandon", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("abandoned").GetBoolean();
    }

    private static async Task<(HttpStatusCode status, string body)> ReadAsync(HttpResponseMessage resp)
        => (resp.StatusCode, await resp.Content.ReadAsStringAsync());

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
