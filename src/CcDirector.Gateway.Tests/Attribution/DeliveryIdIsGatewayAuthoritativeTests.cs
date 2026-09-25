using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Voice;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Attribution;

/// <summary>
/// THE DELIVERY ID IS THE GATEWAY'S, PROVEN ACROSS THE REAL ROUTE (Voice Delivery mission, phase 1). The Director
/// refuses a second copy of a delivery id it has typed, so a client that could set the id freely could block words it
/// does not own, or pass them off as delivered. "Send anyway" may only CLAIM its recording; the prompt route turns the
/// claim into the delivery id only for an upload of the caller's own tenant for that session.
///
/// A hostile body is posted at the MAPPED ROUTE of a real <see cref="GatewayHost"/>, rides the REAL tunnel (a real
/// SignalR Director connection), and what arrives is read and run through the REAL prompt core - with a delivery
/// record of this test's own, so nothing touches the machine's record.
/// </summary>
[Collection("DirectorRoot")]
public sealed class DeliveryIdIsGatewayAuthoritativeTests : IAsyncLifetime
{
    private const string Token = "test-token-delivery-id";
    private const string DirectorId = "dir-delivery-id";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-delid-" + Guid.NewGuid().ToString("N"));
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private SessionManager _sm = null!;
    private HubConnection _conn = null!;
    private Session _session = null!;
    private string _sid = "";
    private DeliveryRecord _directorRecord = null!;
    private VoiceUploadStore _uploads = null!;
    private readonly List<PromptRequest> _arrived = new();

    public DeliveryIdIsGatewayAuthoritativeTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-delid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            keyVaultPath: Path.Combine(_root, "keyvault.json"),
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        // The same staging root the Gateway's own dictation store uses (it resolves under CC_DIRECTOR_ROOT).
        _uploads = new VoiceUploadStore(CcStorage.DictationUploads(), TenantId.Local);
        _directorRecord = new DeliveryRecord(Path.Combine(_root, "director-delivery-records"));

        _sm = new SessionManager(new AgentOptions());
        _session = _sm.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        _sid = _session.Id.ToString();

        _gateway.Registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = DirectorId,
            TailnetEndpoint = "http://127.0.0.1:59918/",
            MachineName = "delid-machine",
            Pid = 1,
            Version = "test",
            StartedAt = DateTime.UtcNow,
        });
        _conn = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{_gateway.Port}/director-stream", o => o.AccessTokenProvider = () => Task.FromResult<string?>(Token))
            .AddMessagePackProtocol()
            .Build();
        // THE DIRECTOR SIDE IS THE REAL PROMPT CORE, over this test's own delivery record.
        _conn.On<DirectorCommand, DirectorCommandResult>("Command", async cmd =>
        {
            if (cmd.Verb != "prompt") return await SessionCommandExecutor.DispatchAsync(_sm, DirectorId, cmd);
            var body = JsonSerializer.Deserialize<PromptRequest>(cmd.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            lock (_arrived) _arrived.Add(body);
            var result = await SessionCommandExecutor.SendPromptAsync(_session, body, SendSource.UserInput, _directorRecord);
            result.CommandId = cmd.CommandId;
            return result;
        });
        await _conn.StartAsync();
        await _conn.InvokeAsync("Hello", new DirectorStreamHello { DirectorId = DirectorId, Version = "test" });
        await _conn.InvokeAsync("PushSnapshot", 1L, new[] { new SessionDto { SessionId = _sid, ActivityState = "WaitingForInput" } });
    }

    public async Task DisposeAsync()
    {
        try { await _conn.DisposeAsync(); } catch { /* best effort */ }
        _sm.Dispose();
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        foreach (var dir in new[] { _instancesDir, _root })
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    private async Task<JsonElement> PostPrompt(object body)
    {
        var resp = await _http.PostAsJsonAsync($"sessions/{_sid}/prompt", body);
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"expected 200, got {(int)resp.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private PromptRequest LastArrived()
    {
        lock (_arrived) return _arrived[^1];
    }

    /// <summary>An upload record for <paramref name="sessionId"/> in <paramref name="store"/>'s tenant, as the
    /// dictation upload leg leaves it.</summary>
    private static string Upload(VoiceUploadStore store, string sessionId)
    {
        var id = Guid.NewGuid().ToString();
        store.Register(id);
        store.MarkPending(id, sessionId);
        return id;
    }

    [Fact]
    public async Task A_claim_for_this_accounts_recording_of_this_session_becomes_the_delivery_id()
    {
        // Proves "Send anyway" naming its own recording reaches the Director with that recording as the delivery id.
        var uploadId = Upload(_uploads, _sid);

        await PostPrompt(new { text = "typed before the spoken words", appendEnter = true, deliveryIdClaim = uploadId });

        Assert.Equal(VoiceUploadStore.NormalizeUploadId(uploadId), LastArrived().DeliveryId);
        Assert.Null(LastArrived().DeliveryIdClaim);
    }

    [Fact]
    public async Task A_claim_for_another_sessions_recording_is_dropped_and_the_text_still_goes()
    {
        // Proves a recording of a DIFFERENT session cannot be named: the claim is dropped, the words are still sent.
        var uploadId = Upload(_uploads, Guid.NewGuid().ToString());

        var answer = await PostPrompt(new { text = "hello", appendEnter = true, deliveryIdClaim = uploadId });

        Assert.True(answer.GetProperty("accepted").GetBoolean());
        Assert.Null(LastArrived().DeliveryId);
    }

    [Fact]
    public async Task A_claim_for_another_tenants_recording_is_dropped()
    {
        // Proves a recording in ANOTHER account's partition - even for this very session id - cannot be named.
        var otherTenant = _uploads.ForTenant(new TenantId("22222222-2222-2222-2222-222222222222"));
        var uploadId = Upload(otherTenant, _sid);

        await PostPrompt(new { text = "hello", appendEnter = true, deliveryIdClaim = uploadId });

        Assert.Null(LastArrived().DeliveryId);
    }

    [Fact]
    public async Task A_delivery_id_set_directly_in_the_body_is_overwritten()
    {
        // Proves the body cannot set the delivery id itself: without a claim it arrives empty, and with a verified
        // claim it arrives as the Gateway's own verified id, never the body's.
        var uploadId = Upload(_uploads, _sid);

        await PostPrompt(new { text = "first", appendEnter = true, deliveryId = "made-up-id" });
        Assert.Null(LastArrived().DeliveryId);

        await PostPrompt(new { text = "second", appendEnter = true, deliveryId = "made-up-id", deliveryIdClaim = uploadId });
        Assert.Equal(VoiceUploadStore.NormalizeUploadId(uploadId), LastArrived().DeliveryId);
    }

    [Fact]
    public async Task A_second_send_anyway_of_a_delivered_recording_is_refused_and_the_caller_is_told()
    {
        // Proves the Director's delivery state reaches the caller unchanged: the second copy is refused as delivered.
        var uploadId = Upload(_uploads, _sid);

        var first = await PostPrompt(new { text = "send me once", appendEnter = true, deliveryIdClaim = uploadId });
        var second = await PostPrompt(new { text = "send me once", appendEnter = true, deliveryIdClaim = uploadId });

        Assert.True(first.GetProperty("accepted").GetBoolean());
        Assert.Equal("delivered", first.GetProperty("deliveryState").GetString());
        Assert.False(second.GetProperty("accepted").GetBoolean());
        Assert.Equal("delivered", second.GetProperty("deliveryState").GetString());
    }
}
