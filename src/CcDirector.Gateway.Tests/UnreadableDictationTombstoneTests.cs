using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Voice;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #2745, the front-door half: a dictation upload whose terminal tombstone is on disk but CANNOT BE
/// READ is not re-opened, not injected, and not written over.
///
/// The de-dupe guard in front of register and complete tests positively for a DELIVERED or ABANDONED record,
/// which is the right direction - but the reader beneath it used to answer null for "no record" and for "a
/// record I could not read" alike, and null fails that positive test. So a corrupt, half-written or locked
/// tombstone fell through to Register plus MarkPending, the upload was re-opened as a fresh PENDING, and the
/// operator's speech was injected a second time. The comment above that guard promised it could not happen.
///
/// These tests drive a real GatewayHost over HTTP with no Director present, exactly as
/// <see cref="DurableDictationDedupeTests"/> does: a prior delivery is a tombstone written to the same
/// staging root the Gateway reads, and the corruption is done to that file. Each refusal is asserted as a
/// PRESENCE - the specific status, the named kind in the body, the tombstone's bytes unchanged, no PENDING
/// marker - next to a control showing a genuinely absent id still opens normally, so the fix cannot have
/// worked by refusing everything.
///
/// REVERT-PROVABLE: make <c>VoiceUploadStore.ReadRecordFile</c> answer Absent for a file it cannot parse
/// (the old fold) and the register test goes red on "the tombstone was overwritten with a PENDING marker" -
/// the reported symptom - while the control stays green.
/// </summary>
public sealed class UnreadableDictationTombstoneTests : IAsyncLifetime
{
    private const string GatewayToken = "test-token";
    private const string NotARecord = "{ this is not a delivery record";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string? _originalRoot;

    private readonly string _storageRoot =
        Path.Combine(Path.GetTempPath(), "cc-unreadable-storage-" + Guid.NewGuid().ToString("N"));
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-unreadable-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _originalRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _storageRoot);

        _gateway = NewGateway();
        await _gateway.StartAsync();
        _http = NewClient(_gateway.Port);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _originalRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* cleanup */ }
        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, true); } catch { /* cleanup */ }
    }

    // ===== register: the leg from the issue ========================================================

    [Fact]
    public async Task ReRegister_OfACorruptTombstone_IsRefused_AndTheUploadIsNotReopened()
    {
        // A delivered upload whose tombstone can no longer be parsed.
        var uploadId = Guid.NewGuid().ToString();
        var path = CorruptDeliveredTombstone(uploadId);
        var before = File.ReadAllBytes(path);

        var resp = await RegisterAsync(uploadId);

        // Refused, by name, with the file named for whoever has to fix it.
        Assert.Equal(HttpStatusCode.Conflict, resp.status);
        Assert.Equal("Malformed", resp.body.GetProperty("record").GetString());
        Assert.Equal(path, resp.body.GetProperty("file").GetString());
        Assert.Contains(uploadId, resp.body.GetProperty("error").GetString());
        // Not a terminal outcome either: the client must not drop its copy on the strength of this.
        Assert.False(resp.body.TryGetProperty("terminal", out var terminal) && terminal.GetBoolean());

        // The reported symptom, asserted as its absence-of-harm PRESENCE: the tombstone's bytes are exactly
        // what they were, no PENDING marker exists, and the store still reads the marker as Malformed.
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(Store().IsPending(uploadId), "the upload was re-opened as PENDING - the #2745 re-injection door");
        Assert.Equal(DictationRecordReadKind.Malformed, Store().Read(uploadId).Kind);

        // CONTROL: a genuinely absent id on the same Gateway still opens as a fresh PENDING upload, so the
        // refusal above is a decision about THIS marker and not a Gateway that refuses everything.
        var fresh = Guid.NewGuid().ToString();
        var control = await RegisterAsync(fresh);
        Assert.Equal(HttpStatusCode.OK, control.status);
        Assert.Equal(fresh, control.body.GetProperty("upload_id").GetString());
        Assert.True(Store().IsPending(fresh));
    }

    [Fact]
    public async Task ReRegister_WhileTheTombstoneIsLocked_IsRefusedAsUnreadable_AndReadsNormallyOnceReleased()
    {
        // The locked-file case from the issue. Only Windows enforces a share lock against a reader.
        if (!OperatingSystem.IsWindows()) return;

        var uploadId = Guid.NewGuid().ToString();
        var store = Store();
        store.MarkDelivered(uploadId, submitted: true, movedOn: false, transcript: "said once");
        var path = RecordPath(uploadId);
        var before = File.ReadAllBytes(path);

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var locked = await RegisterAsync(uploadId);

            // Could not look is a 503 - "try again" - and never "open it".
            Assert.Equal(HttpStatusCode.ServiceUnavailable, locked.status);
            Assert.Equal("Unreadable", locked.body.GetProperty("record").GetString());
        }

        // Released: the same register now gets the cached terminal outcome the tombstone always held. The
        // bytes never moved, which is why it can.
        Assert.Equal(before, File.ReadAllBytes(path));
        var released = await RegisterAsync(uploadId);
        Assert.Equal(HttpStatusCode.OK, released.status);
        Assert.True(released.body.GetProperty("terminal").GetBoolean());
        Assert.Equal("said once", released.body.GetProperty("transcript").GetString());
    }

    // ===== complete: the injection point =============================================================

    [Fact]
    public async Task ReComplete_OfACorruptTombstone_IsRefused_WithNothingInjectedAndNothingWritten()
    {
        var uploadId = Guid.NewGuid().ToString();
        var path = CorruptDeliveredTombstone(uploadId);
        var before = File.ReadAllBytes(path);

        var resp = await _http.PostAsJsonAsync($"/dictation/{uploadId}/complete",
            new { sessionId = Guid.NewGuid().ToString(), totalChunks = 1 });
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("Malformed", body.GetProperty("record").GetString());
        // Neither a cached submitted turn nor a dropped one: no outcome at all was fabricated.
        Assert.False(body.TryGetProperty("submitted", out var s) && s.GetBoolean());
        Assert.False(body.TryGetProperty("dropped", out var d) && d.GetBoolean());

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(Store().IsPending(uploadId));
    }

    // ===== abandon: the write from the phone ===========================================================

    [Fact]
    public async Task Abandon_OfACorruptTombstone_IsRefused_AndTheMarkerIsLeftAsItIs()
    {
        var uploadId = Guid.NewGuid().ToString();
        var path = CorruptDeliveredTombstone(uploadId);
        var before = File.ReadAllBytes(path);

        var resp = await _http.PostAsync($"/dictation/{uploadId}/abandon", content: null);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("Malformed", body.GetProperty("record").GetString());
        Assert.False(body.TryGetProperty("ok", out var ok) && ok.GetBoolean());

        // Not turned into an ABANDONED tombstone: had it been DELIVERED, a later re-complete would have told
        // the user "dropped" about speech that was acted on.
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(DictationRecordReadKind.Malformed, Store().Read(uploadId).Kind);

        // CONTROL: an abandon of a readable PENDING upload still lands as ABANDONED.
        var pending = Guid.NewGuid().ToString();
        Store().Register(pending);
        Store().MarkPending(pending, Guid.NewGuid().ToString());
        var control = await _http.PostAsync($"/dictation/{pending}/abandon", content: null);
        Assert.Equal(HttpStatusCode.OK, control.StatusCode);
        Assert.Equal(DictationDeliveryState.Abandoned, Store().ReadRecord(pending)!.State);
    }

    // ===== helpers =================================================================================

    private static VoiceUploadStore Store() => new(CcStorage.DictationUploads(), TenantId.Local);

    private static string RecordPath(string uploadId)
        => Path.Combine(CcStorage.DictationUploads(), Guid.Parse(uploadId).ToString("N"), "record.json");

    // A real DELIVERED tombstone written by the store the Gateway reads, then its bytes replaced with
    // something that is not a delivery record. The id genuinely WAS delivered; only the marker is unreadable.
    private static string CorruptDeliveredTombstone(string uploadId)
    {
        Store().MarkDelivered(uploadId, submitted: true, movedOn: false, transcript: "said once");
        var path = RecordPath(uploadId);
        Assert.True(File.Exists(path));
        File.WriteAllText(path, NotARecord, Encoding.UTF8);
        return path;
    }

    private async Task<(HttpStatusCode status, JsonElement body)> RegisterAsync(string uploadId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/dictation/upload")
        {
            Content = JsonContent.Create(new { sessionId = Guid.NewGuid().ToString(), baselineBufferBytes = 0 }),
        };
        req.Headers.Add("Idempotency-Key", uploadId);
        var resp = await _http.SendAsync(req);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return (resp.StatusCode, body);
    }

    private GatewayHost NewGateway() => new(
        port: GatewayHost.OperatingSystemAssignedPort, token: GatewayToken, authEnabled: false,
        instancesDirectory: _instancesDir,
        workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));

    private static HttpClient NewClient(int port)
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayToken);
        return http;
    }
}
